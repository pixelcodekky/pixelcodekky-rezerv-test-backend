using Microsoft.Extensions.Logging;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Application.Contracts;
using Rezerv.Domain.Entities;

namespace Rezerv.Application.Services;

/// <param name="Amount">null = everything not refunded yet.</param>
public sealed record RefundCommand(string? IdempotencyKey, string InvoiceNo, decimal? Amount, string? Reason);

/// <summary>
/// Refunds a succeeded payment through the 2C2P Payment Action API: a void (full amount) while it is still unsettled,
/// otherwise a refund (full or partial). Uses the same idempotency scheme as checkout (Redis lock per key, unique index
/// as the final guard), and reserves the amount on the transaction atomically so refunds never exceed the payment.
/// </summary>
public sealed class RefundService(IPaymentTransactionRepository transactions,IRefundRepository refunds,IUnitOfWork unitOfWork,
    IPaymentActionGateway gateway,ICacheService cache,CheckoutOptions options,ILogger<RefundService> logger)
{
    internal const string AwaitingResultCode = "PENDING";
    private const string InProgressMessage = "A refund with this Idempotency-Key is still in progress. Retry shortly.";

    public async Task<RefundDto> RefundAsync(RefundCommand command, CancellationToken ct = default)
    {
        var key = CheckoutService.NormalizeKey(command.IdempotencyKey);
        if (command.Amount is decimal requested && (requested <= 0 || decimal.Round(requested, 2) != requested))
            throw new ValidationException("amount must be positive with at most 2 decimal places.");

        var existing = await FindExistingAsync(key, command, ct);
        if (existing.Refund is not null)
            return existing.Refund;

        var deadline = DateTime.UtcNow + options.WaitTimeout;
        while (true)
        {
            await using (var lease = await cache.AcquireLockAsync(CacheKeys.RefundLock(key), options.LockTtl, ct))
            {
                if (lease is not null)
                {
                    // Re-check under the lock: the previous owner may have finished in the meantime.
                    existing = await FindExistingAsync(key, command, ct);
                    if (existing.Refund is not null)
                        return existing.Refund;
                    if (existing.InProgress)
                        throw new ConflictException(InProgressMessage);

                    try
                    {
                        return await CreateAndSubmitAsync(key, command, ct);
                    }
                    catch (ValidationException)
                    {
                        // Without a real lock (Redis down) a request with the same key may have just used up the amount:
                        // answer with its result rather than "already refunded".
                        var winner = await FindExistingAsync(key, command, CancellationToken.None);
                        if (winner.Refund is not null)
                            return winner.Refund;
                        if (winner.InProgress)
                            throw new ConflictException(InProgressMessage);
                        throw;
                    }
                }
            }

            if (DateTime.UtcNow >= deadline)
                throw new ConflictException(InProgressMessage);

            await Task.Delay(options.WaitPollInterval, ct);
            existing = await FindExistingAsync(key, command, ct);
            if (existing.Refund is not null)
                return existing.Refund;
        }
    }

    /// <summary>Refunds of a transaction, newest last. Pending ones are checked with 2C2P (throttled per invoice).</summary>
    public async Task<IReadOnlyList<RefundDto>> ListAsync(string invoiceNo, CancellationToken ct = default)
    {
        var txn = await transactions.GetByInvoiceNoAsync(invoiceNo, ct)
            ?? throw new NotFoundException($"Transaction {invoiceNo} not found.");

        var list = await refunds.GetByTransactionAsync(txn.Id, ct);
        if (gateway.IsConfigured && list.Any(IsReconcilable) && await TakeStatusSlotAsync(invoiceNo, ct))
        {
            await ReconcileAsync(txn, list, ct);
            list = await refunds.GetByTransactionAsync(txn.Id, ct);
        }

        return list.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Select(r => r.ToDto(invoiceNo)).ToList();
    }

    private async Task<RefundDto> CreateAndSubmitAsync(string key, RefundCommand command, CancellationToken ct)
    {
        if (!gateway.IsConfigured)
            throw new NotConfiguredException("Refunds need the 2C2P Payment Action keys (_2C2P:PaymentAction settings).");

        var txn = await transactions.GetByInvoiceNoAsync(command.InvoiceNo, ct)
            ?? throw new NotFoundException($"Transaction {command.InvoiceNo} not found.");
        if (txn.Status != TransactionStatus.Succeeded)
            throw new ValidationException($"Only succeeded payments can be refunded; {txn.InvoiceNo} is {txn.Status}.");

        var remaining = txn.Amount - txn.RefundedAmount;
        if (remaining <= 0)
            throw new ValidationException($"{txn.InvoiceNo} is already fully refunded (or a refund is pending).");
        var amount = command.Amount ?? remaining;
        if (amount > remaining)
            throw new ValidationException($"amount {amount:0.00} exceeds the refundable amount {remaining:0.00} {txn.Currency}.");

        // Nothing is written before this call: if 2C2P can't be reached the request fails with 502 and can be retried.
        var payment = await gateway.GetPaymentStatusAsync(txn.InvoiceNo, txn.Amount, ct);
        var type = ChooseType(payment, txn, amount);

        var now = DateTime.UtcNow;
        var refund = new Refund
        {
            TransactionId = txn.Id,
            IdempotencyKey = key,
            Type = type,
            Amount = amount,
            Currency = txn.Currency,
            Reason = Truncate(command.Reason, 255),
            Status = RefundStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async c =>
            {
                // Insert first: a duplicate key hits the unique index (replay) rather than looking like an over-refund.
                refunds.Add(refund);
                await unitOfWork.SaveChangesAsync(c);

                // Concurrent refunds with different keys race here; the conditional UPDATE lets only those that fit through.
                // Failing rolls back the insert too.
                if (!await transactions.TryReserveRefundAsync(txn.Id, amount, c))
                    throw new ValidationException(
                        $"amount {amount:0.00} exceeds what is left to refund on {txn.InvoiceNo}; another refund may have just been made.");
            }, ct);
        }
        catch (ConflictException ex)
        {
            // Final guard: the Redis lock was unavailable or expired and another request inserted this key first.
            logger.LogWarning("Refund Idempotency-Key {Key} was claimed concurrently; unique index prevented a second refund", key);
            var winner = await FindExistingAsync(key, command, CancellationToken.None);
            if (winner.Refund is not null)
                return winner.Refund;
            throw new ConflictException(InProgressMessage, ex);
        }

        logger.LogInformation("Created {Type} {RefundId} of {Amount} for {InvoiceNo} (key {Key})", type, refund.Id, amount, txn.InvoiceNo, key);

        // The reservation is committed: finish even if the client disconnects, so the key always ends up with a result.
        var request = new RefundActionRequest(txn.InvoiceNo, amount, key);
        try
        {
            var result = type == RefundType.Void
                ? await gateway.VoidAsync(request, CancellationToken.None)
                : await gateway.RefundAsync(request, CancellationToken.None);
            await ApplyAsync(refund, result.Outcome, new RefundProviderResult(result.RespCode, result.RespDesc, result.Status, result.ReferenceNo));
        }
        catch (PaymentGatewayException ex)
        {
            // The request may have reached 2C2P, so never fail it here (that would release the amount for a second refund).
            // It stays Pending; GET .../refunds checks it with 2C2P.
            logger.LogWarning(ex, "{Type} {RefundId} for {InvoiceNo} ended without a response; awaiting the result", type, refund.Id, txn.InvoiceNo);
            await refunds.SetProviderResultAsync(refund.Id, new RefundProviderResult(AwaitingResultCode, "Awaiting result from 2C2P", null, null));
        }

        var saved = await refunds.GetByIdempotencyKeyAsync(key, CancellationToken.None) ?? refund;
        return saved.ToDto(txn.InvoiceNo);
    }

    private static RefundType ChooseType(PaymentActionStatus payment, PaymentTransaction txn, decimal amount) => payment.State switch
    {
        CaptureState.Authorized when amount == txn.Amount && txn.RefundedAmount == 0 => RefundType.Void,
        CaptureState.Authorized => throw new ValidationException(
            "This payment is not settled yet, so only the full amount can be cancelled (void). Partial refunds are possible after settlement."),
        CaptureState.Settled => RefundType.Refund,
        CaptureState.Voided => throw new ValidationException($"{txn.InvoiceNo} is already voided at 2C2P."),
        _ => throw new ValidationException(
            $"{txn.InvoiceNo} can't be refunded now (2C2P status {payment.Status ?? payment.RespCode}: {payment.RespDesc}).")
    };

    /// <summary>Applies a 2C2P answer. Only a Pending refund changes; a failed one gives its amount back to the transaction.</summary>
    private async Task ApplyAsync(Refund refund, RefundOutcome outcome, RefundProviderResult result)
    {
        result = result with
        {
            RespCode = Truncate(result.RespCode, 10),
            RespDesc = Truncate(result.RespDesc, 100),
            ProviderStatus = Truncate(result.ProviderStatus, 20),
            ProviderRef = Truncate(result.ProviderRef, 50)
        };
        var now = DateTime.UtcNow;

        switch (outcome)
        {
            case RefundOutcome.Succeeded:
                if (await refunds.TryMarkSucceededAsync(refund.Id, result, now))
                    logger.LogInformation("{Type} {RefundId} succeeded ({Status})", refund.Type, refund.Id, result.ProviderStatus);
                break;

            case RefundOutcome.Failed:
                await unitOfWork.ExecuteInTransactionAsync(async c =>
                {
                    // Only the call that moves it out of Pending releases the amount, so it is released exactly once.
                    if (await refunds.TryMarkFailedAsync(refund.Id, result, now, c))
                        await transactions.ReleaseRefundAsync(refund.TransactionId, refund.Amount, c);
                });
                logger.LogInformation("{Type} {RefundId} failed: {RespCode} {RespDesc}", refund.Type, refund.Id, result.RespCode, result.RespDesc);
                break;

            default:
                await refunds.SetProviderResultAsync(refund.Id, result);
                break;
        }
    }

    /// <summary>Checks pending refunds with 2C2P: Payment Inquiry for voids, Refund Status Inquiry for refunds.</summary>
    private async Task ReconcileAsync(PaymentTransaction txn, IReadOnlyList<Refund> list, CancellationToken ct)
    {
        var pending = list.Where(IsReconcilable).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList();
        try
        {
            if (pending.Any(r => r.Type == RefundType.Void))
            {
                var payment = await gateway.GetPaymentStatusAsync(txn.InvoiceNo, txn.Amount, ct);
                foreach (var refund in pending.Where(r => r.Type == RefundType.Void))
                {
                    var result = new RefundProviderResult(payment.RespCode, payment.RespDesc, payment.Status, refund.ProviderRef);
                    switch (payment.State)
                    {
                        case CaptureState.Voided:
                            await ApplyAsync(refund, RefundOutcome.Succeeded, result);
                            break;
                        case CaptureState.Authorized or CaptureState.Settled:
                            // 2C2P still has the payment as charged, so the void did not happen.
                            await ApplyAsync(refund, RefundOutcome.Failed, result with { RespDesc = "Void was not applied by 2C2P" });
                            break;
                    }
                }
            }

            if (pending.Any(r => r.Type == RefundType.Refund))
            {
                var status = await gateway.GetRefundStatusAsync(txn.InvoiceNo, txn.Amount, ct);
                // Refunds are matched by 2C2P reference; one we never got a reference for is matched by amount to an unclaimed entry.
                var claimed = list.Where(r => r.ProviderRef is not null).Select(r => r.ProviderRef!).ToHashSet();
                foreach (var refund in pending.Where(r => r.Type == RefundType.Refund))
                {
                    var entry = refund.ProviderRef is not null
                        ? status.Refunds.FirstOrDefault(e => e.ReferenceNo == refund.ProviderRef)
                        : status.Refunds.FirstOrDefault(e => e.Amount == refund.Amount && (e.ReferenceNo is null || !claimed.Contains(e.ReferenceNo)));

                    if (entry is not null)
                    {
                        if (entry.ReferenceNo is not null)
                            claimed.Add(entry.ReferenceNo);
                        await ApplyAsync(refund, entry.Outcome,
                            new RefundProviderResult(status.RespCode, status.RespDesc, entry.Status, entry.ReferenceNo ?? refund.ProviderRef));
                    }
                    else if (refund.ProviderRef is null && DateTime.UtcNow - refund.CreatedAt >= options.LockTtl)
                    {
                        // The request never got an answer and 2C2P has no record of it: it did not happen.
                        await ApplyAsync(refund, RefundOutcome.Failed,
                            new RefundProviderResult(status.RespCode, "Refund not found at 2C2P", null, null));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is PaymentGatewayException or NotConfiguredException)
        {
            logger.LogWarning(ex, "Refund status check for {InvoiceNo} failed; will retry on the next request", txn.InvoiceNo);
        }
    }

    /// <summary>A pending refund whose 2C2P call has finished (or whose owner is long gone) can be checked with 2C2P.</summary>
    private bool IsReconcilable(Refund refund) =>
        refund.Status == RefundStatus.Pending && (refund.RespCode is not null || DateTime.UtcNow - refund.CreatedAt >= options.LockTtl);

    private async Task<ExistingRefund> FindExistingAsync(string key, RefundCommand command, CancellationToken ct)
    {
        var refund = await refunds.GetByIdempotencyKeyAsync(key, ct);
        if (refund is null)
            return default;

        var txn = await transactions.GetByInvoiceNoAsync(command.InvoiceNo, ct);
        if (txn is null || txn.Id != refund.TransactionId || (command.Amount is decimal amount && amount != refund.Amount))
            throw new IdempotencyKeyMismatchException("This Idempotency-Key was already used with a different refund request.");

        // Created but 2C2P not answered yet, by a request that is still running.
        if (refund.Status == RefundStatus.Pending && refund.RespCode is null && DateTime.UtcNow - refund.CreatedAt < options.LockTtl)
            return new ExistingRefund(null, InProgress: true);

        return new ExistingRefund(refund.ToDto(txn.InvoiceNo), InProgress: false);
    }

    private async Task<bool> TakeStatusSlotAsync(string invoiceNo, CancellationToken ct)
    {
        var key = CacheKeys.RefundStatusThrottle(invoiceNo);
        if (await cache.GetAsync<string>(key, ct) is not null)
            return false;

        await cache.SetAsync(key, "1", options.InquiryThrottle, ct);
        return true;
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private readonly record struct ExistingRefund(RefundDto? Refund, bool InProgress);
}

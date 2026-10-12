using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Application.Contracts;
using Rezerv.Domain.Entities;

namespace Rezerv.Application.Services;

/// <param name="RetryOfTransactionId">Set when this checkout retries a failed transaction (POST /api/transactions/{invoiceNo}/retry).</param>
public sealed record CheckoutCommand(string? IdempotencyKey, int CustomerId, int ProductId, int? CustomerCardId, string ClientIp,
    long? RetryOfTransactionId = null);

/// <param name="CustomerCardId">Saved card to pay with, or null for the 2C2P hosted page.</param>
public sealed record RetryCommand(string? IdempotencyKey, string InvoiceNo, int? CustomerCardId, string ClientIp);

public sealed class CheckoutService(ICustomerRepository customers,IProductRepository products,ICustomerCardRepository cards,IPaymentTransactionRepository transactions,
    IUnitOfWork unitOfWork,IPaymentGateway gateway,PaymentResultProcessor results,ICacheService cache,CheckoutOptions options,ILogger<CheckoutService> logger)
{
    internal const string GatewayErrorCode = "GW_ERROR";
    internal const string AwaitingResultCode = "PENDING";
    private const string InProgressMessage = "A checkout with this Idempotency-Key is still in progress. Retry shortly.";

    public async Task<CheckoutResponse> CheckoutAsync(CheckoutCommand command, CancellationToken ct = default)
    {
        var key = NormalizeKey(command.IdempotencyKey);

        var existing = await FindExistingAsync(key, command, ct);
        if (existing.Response is not null)
            return existing.Response;

        var deadline = DateTime.UtcNow + options.WaitTimeout;
        while (true)
        {
            await using (var lease = await cache.AcquireLockAsync(CacheKeys.CheckoutLock(key), options.LockTtl, ct))
            {  
                if (lease is not null)
                {
                    // Re-check under the lock: the previous owner may have finished in the meantime.
                    existing = await FindExistingAsync(key, command, ct);
                    if (existing.Response is not null)
                        return existing.Response;
                    if (existing.InProgress)
                        throw new ConflictException(InProgressMessage);

                    return await CreateAndPayAsync(key, command, ct);
                }
            }

            // Another request owns this key. Wait for its result; if it released the lock without creating
            // a transaction (e.g. validation error) the next loop takes the lock and runs the checkout itself.
            if (DateTime.UtcNow >= deadline)
                throw new ConflictException(InProgressMessage);

            await Task.Delay(options.WaitPollInterval, ct);
            existing = await FindExistingAsync(key, command, ct);
            if (existing.Response is not null)
                return existing.Response;
        }
    }

    /// <summary>
    /// Retries a failed transaction as a new checkout (2C2P never accepts an invoiceNo twice): same customer and product,
    /// a new invoiceNo, linked to the failed one. Idempotent like checkout. A failed transaction can be retried only once,
    /// so two retries (e.g. two tabs with different keys) can never both charge the customer.
    /// </summary>
    public async Task<CheckoutResponse> RetryAsync(RetryCommand command, CancellationToken ct = default)
    {
        var key = NormalizeKey(command.IdempotencyKey);

        var original = await transactions.GetByInvoiceNoAsync(command.InvoiceNo, ct)
            ?? throw new NotFoundException($"Transaction {command.InvoiceNo} not found.");

        var checkout = new CheckoutCommand(key, original.CustomerId, original.ProductId, command.CustomerCardId, command.ClientIp,
            RetryOfTransactionId: original.Id);

        // Same key again (double click, refresh): replay the retry that was already created.
        var existing = await FindExistingAsync(key, checkout, ct);
        if (existing.Response is not null)
            return existing.Response;

        if (original.Status != TransactionStatus.Failed)
            throw new ValidationException($"Only failed transactions can be retried; {original.InvoiceNo} is {original.Status}.");
        if (original.Retry is not null && original.Retry.IdempotencyKey != key)
            throw new ConflictException($"Transaction {original.InvoiceNo} has already been retried as {original.Retry.InvoiceNo}. " +
                                        "Check that transaction, and retry it instead if it failed too.");

        return await CheckoutAsync(checkout, ct);
    }

    private async Task<CheckoutResponse> CreateAndPayAsync(string key, CheckoutCommand command, CancellationToken ct)
    {
        var customer = await customers.GetByIdAsync(command.CustomerId, ct)
            ?? throw new NotFoundException($"Customer {command.CustomerId} not found.");

        var product = await products.GetByIdAsync(command.ProductId, ct);
        if (product is null || !product.IsActive)
            throw new NotFoundException($"Product {command.ProductId} not found.");
        if (!string.Equals(product.Currency, PaymentRules.Currency, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException($"Product {product.Id} is priced in {product.Currency}; only {PaymentRules.Currency} is supported.");

        CustomerCard? card = null;
        if (command.CustomerCardId is int cardId)
        {
            card = await cards.GetByIdAsync(cardId, ct);
            if (card is null || card.CustomerId != customer.Id)
                throw new NotFoundException($"Saved card {cardId} not found for customer {customer.Id}.");
        }

        var now = DateTime.UtcNow;
        var txn = new PaymentTransaction
        {
            CustomerId = customer.Id,
            ProductId = product.Id,
            CustomerCardId = card?.Id,
            InvoiceNo = NewInvoiceNo(now),
            IdempotencyKey = key,
            RetryOfTransactionId = command.RetryOfTransactionId,
            Amount = product.Price,
            Currency = PaymentRules.Currency,
            Status = TransactionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        transactions.Add(txn);

        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (ConflictException ex)
        {
            // Final guard: the Redis lock was unavailable or expired and another request inserted this key first.
            logger.LogWarning("Idempotency-Key {Key} was claimed concurrently; unique index prevented a second transaction", key);
            var winner = await FindExistingAsync(key, command, CancellationToken.None);
            if (winner.Response is not null)
                return winner.Response;
            // Not our key: another retry of the same failed transaction won (uq_txn_retry_of).
            if (command.RetryOfTransactionId is not null && !winner.InProgress)
                throw new ConflictException("This transaction has already been retried by another request.", ex);
            throw new ConflictException(InProgressMessage, ex);
        }

        logger.LogInformation("Created transaction {InvoiceNo} for key {Key} ({Flow})",
            txn.InvoiceNo, key, card is null ? "hosted page" : "saved card");

        // The row is committed: from here on, finish even if the client disconnects, so the key always ends up with a result.
        var name = $"{customer.FirstName} {customer.LastName}";
        var response = card is null
            ? await StartHostedPaymentAsync(txn, product, name, customer.Email)
            : await PayWithSavedCardAsync(txn, product, card, name, customer.Email, command.ClientIp);

        await RememberAsync(key, command, response);
        return response;
    }

    /// <summary>
    /// New payment method: the customer pays on the 2C2P hosted page. OneTime shows every method enabled for the merchant;
    /// Recurring shows card only with the save-card option (tokenize), so the card can be charged again later.
    /// </summary>
    private async Task<CheckoutResponse> StartHostedPaymentAsync(PaymentTransaction txn, Product product, string name, string email)
    {
        var token = await CreatePaymentTokenAsync(txn, product, name, email, PaymentRules.ForNewPaymentMethod(product.PaymentType));
        if (token is null || !token.IsSuccess || string.IsNullOrEmpty(token.WebPaymentUrl))
            return await FailAsync(txn, token?.RespCode ?? GatewayErrorCode, token?.RespDesc ?? "Payment token request failed");

        await transactions.SavePaymentTokenAsync(txn.Id, token.PaymentToken ?? string.Empty);
        
        await transactions.SetCheckoutResultAsync(txn.Id, token.WebPaymentUrl, null, null);
        
        return new CheckoutResponse(txn.InvoiceNo, TransactionStatus.Pending, token.WebPaymentUrl);
    }

    /// <summary>
    /// Saved card (any product): Payment Token (card only, customerToken), then Do Payment. respCode 1001 sends the browser
    /// to the 3DS URL; anything else goes straight to the result, confirmed by Payment Inquiry.
    /// </summary>
    private async Task<CheckoutResponse> PayWithSavedCardAsync(PaymentTransaction txn, Product product, CustomerCard card, string name, string email, string clientIp)
    {
        var token = await CreatePaymentTokenAsync(txn, product, name, email, PaymentRules.ForSavedCard(card));
        
        if (token is null || !token.IsSuccess || string.IsNullOrEmpty(token.PaymentToken))
            return await FailAsync(txn, token?.RespCode ?? GatewayErrorCode, token?.RespDesc ?? "Payment token request failed");
        
        await transactions.SavePaymentTokenAsync(txn.Id, token.PaymentToken);

        DoPaymentResult payment;
        
        try
        {
            payment = await gateway.DoPaymentAsync(new DoPaymentRequest(txn.InvoiceNo, token.PaymentToken,card.CustomerToken, clientIp, name, email));
        }
        catch (PaymentGatewayException ex)
        {
            logger.LogWarning(ex, "Do Payment for {InvoiceNo} ended without a response; awaiting the result", txn.InvoiceNo);
            await transactions.SetCheckoutResultAsync(txn.Id, null, AwaitingResultCode, "Awaiting result from 2C2P");
            return new CheckoutResponse(txn.InvoiceNo, TransactionStatus.Pending, null);
        }

        switch (payment.Outcome)
        {
            case DoPaymentOutcome.Redirect:
                await transactions.SetCheckoutResultAsync(txn.Id, payment.RedirectUrl, null, null);
                return new CheckoutResponse(txn.InvoiceNo, TransactionStatus.Pending, payment.RedirectUrl);

            default:
                // 2000 "transaction completed" does not mean it succeeded: Payment Inquiry decides (0000 = Succeeded, else Failed).
                await transactions.SetCheckoutResultAsync(txn.Id, null, payment.RespCode, payment.RespDesc);
                
                var status = await results.ReconcileAsync(txn.InvoiceNo, ReconcileMode.Final);
                
                return new CheckoutResponse(txn.InvoiceNo, status, null);
        }
    }

    
    private async Task<PaymentTokenResult?> CreatePaymentTokenAsync(PaymentTransaction txn, Product product, string name, string email, PaymentTokenPlan plan)
    {
        try
        {
            return await gateway.CreatePaymentTokenAsync(new PaymentTokenRequest(
                txn.InvoiceNo, product.Name, txn.Amount, txn.Currency, plan.Tokenize, plan.CustomerToken, name, email, plan.PaymentChannels,
                plan.CustomerTokenOnly, plan.Request3DS));
        }
        catch (PaymentGatewayException ex)
        {
            logger.LogError(ex, "Payment Token request for {InvoiceNo} failed", txn.InvoiceNo);
            return null;
        }
    }

    private async Task<CheckoutResponse> FailAsync(PaymentTransaction txn, string respCode, string? respDesc)
    {
        logger.LogInformation("Checkout {InvoiceNo} failed: {RespCode} {RespDesc}", txn.InvoiceNo, respCode, respDesc);
        await transactions.TryMarkFailedAsync(txn.Id, Truncate(respCode, 10)!, Truncate(respDesc, 100));
        return new CheckoutResponse(txn.InvoiceNo, TransactionStatus.Failed, null);
    }

    private async Task<ExistingCheckout> FindExistingAsync(string key, CheckoutCommand command, CancellationToken ct)
    {
        var stored = await cache.GetAsync<StoredCheckout>(CacheKeys.CheckoutResponse(key), ct);
        if (stored is not null)
        {
            EnsureSameRequest(stored.CustomerId, stored.ProductId, stored.CustomerCardId, stored.RetryOfTransactionId, command);
            return new ExistingCheckout(stored.Response, InProgress: false);
        }

        var txn = await transactions.GetByIdempotencyKeyAsync(key, ct);
        if (txn is null)
            return default;

        EnsureSameRequest(txn.CustomerId, txn.ProductId, txn.CustomerCardId, txn.RetryOfTransactionId, command);

        // Created but 2C2P not answered yet. Past the lock TTL it is an orphan of a crashed instance: replay it as-is
        // (Pending, no redirect) and let GET /api/transactions reconcile it with Payment Inquiry.
        if (!IsSettled(txn) && DateTime.UtcNow - txn.CreatedAt < options.LockTtl)
            return new ExistingCheckout(null, InProgress: true);

        var response = txn.ToCheckoutResponse();
        await RememberAsync(key, command, response);
        return new ExistingCheckout(response, InProgress: false);
    }

    /// <summary>A checkout is settled once 2C2P's answer is stored: a redirect URL, a response code, or a final status.</summary>
    private static bool IsSettled(PaymentTransaction txn) =>
        txn.Status != TransactionStatus.Pending || txn.RedirectUrl is not null || txn.RespCode is not null;

    private Task RememberAsync(string key, CheckoutCommand command, CheckoutResponse response) =>
        cache.SetAsync(CacheKeys.CheckoutResponse(key),
            new StoredCheckout(command.CustomerId, command.ProductId, command.CustomerCardId, response, command.RetryOfTransactionId),
            options.ResponseTtl);

    private static void EnsureSameRequest(int customerId, int productId, int? customerCardId, long? retryOfTransactionId, CheckoutCommand command)
    {
        if (customerId != command.CustomerId || productId != command.ProductId || customerCardId != command.CustomerCardId
            || retryOfTransactionId != command.RetryOfTransactionId)
            throw new IdempotencyKeyMismatchException("This Idempotency-Key was already used with a different request body.");
    }

    internal static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !Guid.TryParse(key, out var guid))
            throw new ValidationException("Idempotency-Key header is required and must be a UUID.");
        return guid.ToString("D");
    }

    /// <summary>2C2P invoiceNo: alphanumeric, max 50. INV + UTC timestamp + random suffix, e.g. INV20261009153012345A1B2C3D4E5.</summary>
    internal static string NewInvoiceNo(DateTime utcNow) =>
        $"INV{utcNow:yyyyMMddHHmmssfff}{Convert.ToHexString(RandomNumberGenerator.GetBytes(5))}";

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private readonly record struct ExistingCheckout(CheckoutResponse? Response, bool InProgress);

    internal sealed record StoredCheckout(int CustomerId, int ProductId, int? CustomerCardId, CheckoutResponse Response,
        long? RetryOfTransactionId = null);
}

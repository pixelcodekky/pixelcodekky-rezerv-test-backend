using Microsoft.Extensions.Logging;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Domain.Entities;

namespace Rezerv.Application.Services;

public enum ReconcileMode
{
    /// <summary>The customer can no longer act on the payment (saved card after Do Payment / 3DS return): apply Succeeded or Failed.</summary>
    Final,
    /// <summary>The customer may still be paying (hosted page, 3DS outstanding): apply only Succeeded; a failure waits for the notification.</summary>
    SuccessOnly
}

public enum ApplyOutcome
{
    /// <summary>This call moved the transaction from Pending to its final status.</summary>
    Updated,
    /// <summary>Already final (duplicate notification, or another request got there first); nothing changed.</summary>
    AlreadyFinal,
    /// <summary>The gateway has no final result yet.</summary>
    StillPending,
    NotFound,
    /// <summary>Amount or currency differ from what we stored; the result was rejected.</summary>
    Mismatch
}

/// <summary>
/// Applies a 2C2P result (backend notification or Payment Inquiry) to a transaction. Safe to call repeatedly and
/// concurrently: the status change is a conditional update on Pending, and card saving is guarded by the
/// (customer_id, customer_token) unique index.
/// </summary>
public sealed class PaymentResultProcessor(IPaymentTransactionRepository transactions,ICustomerCardRepository cards,IUnitOfWork unitOfWork,IPaymentGateway gateway,ICacheService cache,ILogger<PaymentResultProcessor> logger)
{
    public async Task<ApplyOutcome> ApplyAsync(PaymentResult result, CancellationToken ct = default)
    {
        var txn = await transactions.GetByInvoiceNoAsync(result.InvoiceNo, ct);
        if (txn is null)
        {
            logger.LogWarning("2C2P result for unknown invoice {InvoiceNo}", result.InvoiceNo);
            return ApplyOutcome.NotFound;
        }

        if (result.Status == TransactionStatus.Pending)
            return ApplyOutcome.StillPending;

        if (result.Amount != txn.Amount || !string.Equals(result.CurrencyCode, txn.Currency, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "2C2P result for {InvoiceNo} rejected: amount/currency {Amount} {Currency} do not match stored {StoredAmount} {StoredCurrency}",
                txn.InvoiceNo, result.Amount, result.CurrencyCode, txn.Amount, txn.Currency);
            return ApplyOutcome.Mismatch;
        }

        var updated = result.Status == TransactionStatus.Succeeded
            ? await transactions.TryMarkSucceededAsync(txn.Id,
                new PaymentCompletion(result.RespCode, result.RespDesc, result.ChannelCode, result.AccountNo, result.TranRef, DateTime.UtcNow), ct)
            : await transactions.TryMarkFailedAsync(txn.Id, result.RespCode, result.RespDesc, ct);

        if (!updated && txn.Status != TransactionStatus.Pending && txn.Status != result.Status)
        {
            // e.g. an inquiry said Failed, then a Succeeded notification arrived. Never flip a final status automatically.
            logger.LogCritical("2C2P reports {Reported} for {InvoiceNo} but it is already {Stored}; needs manual review",
                result.Status, txn.InvoiceNo, txn.Status);
        }

        // Saving the card is the customer's choice: 2C2P only sends a customerToken when they ticked save-card on the
        // hosted page. Without one the payment is recorded as normal and no card is created.
        // Runs for duplicates too: if an earlier attempt updated the status but died before saving the card, this completes it.
        if (result.Status == TransactionStatus.Succeeded)
        {
            if (!string.IsNullOrWhiteSpace(result.CustomerToken))
                await SaveCardIfNewAsync(txn.CustomerId, result, ct);
            else if (updated && txn.CustomerCardId is null)
                logger.LogInformation("No customerToken for {InvoiceNo} (save-card not ticked); payment recorded, no card saved", txn.InvoiceNo);
        }

        logger.LogInformation("2C2P result {Status} ({RespCode}) for {InvoiceNo}: {Outcome}",
            result.Status, result.RespCode, txn.InvoiceNo, updated ? "updated" : "already final");
        return updated ? ApplyOutcome.Updated : ApplyOutcome.AlreadyFinal;
    }

    /// <summary>
    /// Asks 2C2P for the result (Payment Inquiry), applies it like a notification (amount/currency check, update only while Pending),
    /// and returns the transaction's status.
    /// </summary>
    public async Task<TransactionStatus> ReconcileAsync(string invoiceNo, ReconcileMode mode, CancellationToken ct = default)
    {
        try
        {
            var result = await gateway.InquireAsync(invoiceNo, ct);
            if (mode == ReconcileMode.SuccessOnly && result.Status != TransactionStatus.Succeeded)
            {
                logger.LogDebug("Inquiry for {InvoiceNo} returned {RespCode}; customer may still be paying, waiting for the notification",
                    invoiceNo, result.RespCode);
            }
            else if (await ApplyAsync(result, ct) == ApplyOutcome.Updated)
            {
                return result.Status;
            }
        }
        catch (PaymentGatewayException ex)
        {
            logger.LogWarning(ex, "Payment Inquiry for {InvoiceNo} failed; keeping the stored status", invoiceNo);
        }

        var txn = await transactions.GetByInvoiceNoAsync(invoiceNo, ct)
            ?? throw new NotFoundException($"Transaction {invoiceNo} not found.");
        return txn.Status;
    }

    private async Task SaveCardIfNewAsync(int customerId, PaymentResult result, CancellationToken ct)
    {
        var token = result.CustomerToken!;
        if (await cards.GetByTokenAsync(customerId, token, ct) is not null)
            return;

        cards.Add(new CustomerCard
        {
            CustomerId = customerId,
            CustomerToken = token,
            MaskedCardNo = string.IsNullOrWhiteSpace(result.AccountNo) ? "XXXXXXXXXXXXXXXX" : result.AccountNo,
            PaymentChannel = result.ChannelCode,
            CreatedAt = DateTime.UtcNow
        });

        try
        {
            await unitOfWork.SaveChangesAsync(ct);
            logger.LogInformation("Saved card token for customer {CustomerId}", customerId);
        }
        catch (ConflictException)
        {
            // A concurrent duplicate notification inserted the same token first.
            logger.LogInformation("Card token for customer {CustomerId} was already saved concurrently", customerId);
        }

        await cache.RemoveAsync(CacheKeys.CustomerCards(customerId), ct);
    }
}

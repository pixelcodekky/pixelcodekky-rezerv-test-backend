using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Application.Contracts;
using Rezerv.Domain.Entities;

namespace Rezerv.Application.Services;

public sealed class TransactionQueryService(IPaymentTransactionRepository transactions,ICustomerRepository customers,PaymentResultProcessor processor,ICacheService cache,CheckoutOptions options)
{
    /// <summary>A customer's transactions, newest first, for the refund page. Read from MySQL only (no 2C2P calls).</summary>
    public async Task<IReadOnlyList<TransactionDto>> GetByCustomerAsync(int customerId, CancellationToken ct = default)
    {
        if (!await customers.ExistsAsync(customerId, ct))
            throw new NotFoundException($"Customer {customerId} not found.");

        return (await transactions.GetByCustomerAsync(customerId, ct)).Select(t => t.ToDto()).ToList();
    }

    public async Task<TransactionDto> GetAsync(string invoiceNo, CancellationToken ct = default)
    {
        var txn = await transactions.GetByInvoiceNoAsync(invoiceNo, ct)
            ?? throw new NotFoundException($"Transaction {invoiceNo} not found.");

        if (txn.Status == TransactionStatus.Pending && await TakeInquirySlotAsync(invoiceNo, ct))
        {
            var mode = IsSavedCard(txn) && txn.RedirectUrl is null ? ReconcileMode.Final : ReconcileMode.SuccessOnly;
            if (await processor.ReconcileAsync(invoiceNo, mode, ct) != TransactionStatus.Pending)
                txn = await transactions.GetByInvoiceNoAsync(invoiceNo, ct) ?? txn;
        }

        return txn.ToDto();
    }

    private static bool IsSavedCard(PaymentTransaction txn) => txn.CustomerCardId is not null;

    private async Task<bool> TakeInquirySlotAsync(string invoiceNo, CancellationToken ct)
    {
        var key = CacheKeys.InquiryThrottle(invoiceNo);
        if (await cache.GetAsync<string>(key, ct) is not null)
            return false;

        await cache.SetAsync(key, "1", options.InquiryThrottle, ct);
        return true;
    }
}

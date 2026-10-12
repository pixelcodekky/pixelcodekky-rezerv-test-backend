using Rezerv.Domain.Entities;

namespace Rezerv.Application.Abstractions;

public interface ICustomerRepository
{
    Task<IReadOnlyList<Customer>> GetAllAsync(CancellationToken ct = default);
    Task<Customer?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<bool> ExistsAsync(int id, CancellationToken ct = default);
}

public interface IProductRepository
{
    Task<IReadOnlyList<Product>> GetAllActiveAsync(CancellationToken ct = default);
    Task<Product?> GetByIdAsync(int id, CancellationToken ct = default);
}

public interface ICustomerCardRepository
{
    Task<IReadOnlyList<CustomerCard>> GetByCustomerAsync(int customerId, CancellationToken ct = default);
    Task<CustomerCard?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<CustomerCard?> GetByTokenAsync(int customerId, string customerToken, CancellationToken ct = default);
    void Add(CustomerCard card);

    /// <summary>
    /// DELETE ... WHERE id = @id AND customer_id = @customerId. Transactions paid with the card keep their own
    /// masked card number; their customer_card_id becomes NULL (fk_txn_customer_card ON DELETE SET NULL).
    /// Returns false when the card was already gone.
    /// </summary>
    Task<bool> DeleteAsync(int id, int customerId, CancellationToken ct = default);
}

/// <summary>
/// State changes are single conditional UPDATEs (WHERE status = 'Pending') rather than tracked entity saves,
/// so concurrent notifications / inquiries can never overwrite a final result. Each returns true only when
/// this call changed the row.
/// </summary>
public interface IPaymentTransactionRepository
{
    /// <summary>Read-only, with Product, RetryOf and Retry loaded.</summary>
    Task<PaymentTransaction?> GetByInvoiceNoAsync(string invoiceNo, CancellationToken ct = default);
    Task<PaymentTransaction?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>Read-only, with Product, RetryOf and Retry loaded, newest first.</summary>
    Task<IReadOnlyList<PaymentTransaction>> GetByCustomerAsync(int customerId, CancellationToken ct = default);

    /// <summary>True while a payment with this saved card is still Pending (e.g. waiting for 3DS or the inquiry).</summary>
    Task<bool> HasPendingForCardAsync(int customerCardId, CancellationToken ct = default);
    void Add(PaymentTransaction transaction);

    /// <summary>Stores the paymentToken returned by the Payment Token API on a Pending row.</summary>
    Task<bool> SavePaymentTokenAsync(long id, string paymentToken, CancellationToken ct = default);

    /// <summary>Records what 2C2P answered at checkout (redirect URL and/or interim response code) on a Pending row.</summary>
    Task<bool> SetCheckoutResultAsync(long id, string? redirectUrl, string? respCode, string? respDesc, CancellationToken ct = default);

    Task<bool> TryMarkSucceededAsync(long id, PaymentCompletion completion, CancellationToken ct = default);

    Task<bool> TryMarkFailedAsync(long id, string respCode, string? respDesc, CancellationToken ct = default);

    /// <summary>
    /// UPDATE ... SET refunded_amount = refunded_amount + @amount WHERE id = @id AND status = 'Succeeded'
    /// AND refunded_amount + @amount &lt;= amount. Atomic, so concurrent refunds can never exceed the payment.
    /// </summary>
    Task<bool> TryReserveRefundAsync(long id, decimal amount, CancellationToken ct = default);

    /// <summary>Gives back a reservation whose refund failed.</summary>
    Task ReleaseRefundAsync(long id, decimal amount, CancellationToken ct = default);
}

/// <summary>Refund state changes are conditional on status = 'Pending', like transactions.</summary>
public interface IRefundRepository
{
    Task<Refund?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
    Task<IReadOnlyList<Refund>> GetByTransactionAsync(long transactionId, CancellationToken ct = default);
    void Add(Refund refund);

    /// <summary>Records an interim answer (e.g. RP refund pending) on a Pending refund.</summary>
    Task<bool> SetProviderResultAsync(long id, RefundProviderResult result, CancellationToken ct = default);

    Task<bool> TryMarkSucceededAsync(long id, RefundProviderResult result, DateTime completedAt, CancellationToken ct = default);

    Task<bool> TryMarkFailedAsync(long id, RefundProviderResult result, DateTime completedAt, CancellationToken ct = default);
}

public sealed record RefundProviderResult(string? RespCode, string? RespDesc, string? ProviderStatus, string? ProviderRef);

public sealed record PaymentCompletion(
    string RespCode,
    string? RespDesc,
    string? ChannelCode,
    string? AccountNo,
    string? TranRef,
    DateTime PaidAt);

public interface IUnitOfWork
{
    /// <summary>Persists tracked changes. Throws <see cref="Common.ConflictException"/> on unique-key violations.</summary>
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Runs <paramref name="work"/> in one database transaction; everything is rolled back if it throws.</summary>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default);
}

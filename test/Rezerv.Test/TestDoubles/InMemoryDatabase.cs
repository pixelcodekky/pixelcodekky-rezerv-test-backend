using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Domain.Entities;

namespace Rezerv.Tests.TestDoubles;

/// <summary>
/// In-memory stand-in for MySQL with the behaviour the services rely on: unique indexes (idempotency_key,
/// customer_id + customer_token, retry_of_transaction_id), conditional UPDATEs (WHERE status = 'Pending') and no-tracking reads (copies).
/// Thread-safe, so concurrency tests can share one instance between several service instances ("API instances").
/// </summary>
public sealed class InMemoryDatabase
{
    private readonly object _gate = new();
    private long _nextId = 1;

    public List<Customer> Customers { get; } = [];
    public List<Product> Products { get; } = [];
    public List<CustomerCard> Cards { get; } = [];
    public List<PaymentTransaction> Transactions { get; } = [];
    public List<Refund> Refunds { get; } = [];

    /// <summary>One request scope (DbContext): repositories and unit of work sharing the tracked inserts.</summary>
    public Session NewSession() => new(this);

    public sealed class Session
    {
        internal Session(InMemoryDatabase db)
        {
            CustomerRepository = new Customers_(db);
            ProductRepository = new Products_(db);
            CardRepository = new Cards_(db, this);
            TransactionRepository = new Transactions_(db, this);
            RefundRepository = new Refunds_(db, this);
            UnitOfWork = new UnitOfWork_(db, this);
        }

        // Tracked inserts waiting for SaveChanges.
        internal readonly List<PaymentTransaction> PendingTransactions = [];
        internal readonly List<CustomerCard> PendingCards = [];
        internal readonly List<Refund> PendingRefunds = [];

        public ICustomerRepository CustomerRepository { get; }
        public IProductRepository ProductRepository { get; }
        public ICustomerCardRepository CardRepository { get; }
        public IPaymentTransactionRepository TransactionRepository { get; }
        public IRefundRepository RefundRepository { get; }
        public IUnitOfWork UnitOfWork { get; }
    }

    public T Read<T>(Func<InMemoryDatabase, T> read)
    {
        lock (_gate) return read(this);
    }

    public PaymentTransaction SingleTransaction() => Read(db => Copy(db.Transactions.Single()));

    public PaymentTransaction Transaction(string invoiceNo) => Read(db => Copy(db.Transactions.Single(t => t.InvoiceNo == invoiceNo)));

    public void Seed(PaymentTransaction txn)
    {
        lock (_gate)
        {
            if (txn.Id == 0) txn.Id = _nextId++;
            Transactions.Add(txn);
        }
    }

    public void Seed(Refund refund)
    {
        lock (_gate)
        {
            if (refund.Id == 0) refund.Id = _nextId++;
            Refunds.Add(refund);
        }
    }

    private static T Copy<T>(T source) where T : class, new()
    {
        var copy = new T();
        foreach (var p in typeof(T).GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (type.IsValueType || type == typeof(string))
                p.SetValue(copy, p.GetValue(source));
        }
        return copy;
    }

    private sealed class Customers_(InMemoryDatabase db) : ICustomerRepository
    {
        public Task<IReadOnlyList<Customer>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Customer>>(db.Read(d => d.Customers.OrderBy(c => c.Id).Select(Copy).ToList()));

        public Task<Customer?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult(db.Read(d => d.Customers.Where(c => c.Id == id).Select(Copy).FirstOrDefault()));

        public Task<bool> ExistsAsync(int id, CancellationToken ct = default) =>
            Task.FromResult(db.Read(d => d.Customers.Any(c => c.Id == id)));
    }

    private sealed class Products_(InMemoryDatabase db) : IProductRepository
    {
        public Task<IReadOnlyList<Product>> GetAllActiveAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Product>>(db.Read(d => d.Products.Where(p => p.IsActive).OrderBy(p => p.Id).Select(Copy).ToList()));

        public Task<Product?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult(db.Read(d => d.Products.Where(p => p.Id == id).Select(Copy).FirstOrDefault()));
    }

    private sealed class Cards_(InMemoryDatabase db, Session session) : ICustomerCardRepository
    {
        public Task<IReadOnlyList<CustomerCard>> GetByCustomerAsync(int customerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CustomerCard>>(db.Read(d => d.Cards.Where(c => c.CustomerId == customerId).Select(Copy).ToList()));

        public Task<CustomerCard?> GetByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult(db.Read(d => d.Cards.Where(c => c.Id == id).Select(Copy).FirstOrDefault()));

        public Task<CustomerCard?> GetByTokenAsync(int customerId, string customerToken, CancellationToken ct = default) =>
            Task.FromResult(db.Read(d => d.Cards.Where(c => c.CustomerId == customerId && c.CustomerToken == customerToken).Select(Copy).FirstOrDefault()));

        public void Add(CustomerCard card)
        {
            lock (db._gate) session.PendingCards.Add(card);
        }

        public Task<bool> DeleteAsync(int id, int customerId, CancellationToken ct = default)
        {
            lock (db._gate)
            {
                var removed = db.Cards.RemoveAll(c => c.Id == id && c.CustomerId == customerId) == 1;
                if (removed)
                {
                    // fk_txn_customer_card ON DELETE SET NULL.
                    foreach (var t in db.Transactions.Where(t => t.CustomerCardId == id))
                        t.CustomerCardId = null;
                }
                return Task.FromResult(removed);
            }
        }
    }

    private sealed class Transactions_(InMemoryDatabase db, Session session) : IPaymentTransactionRepository
    {
        public Task<PaymentTransaction?> GetByInvoiceNoAsync(string invoiceNo, CancellationToken ct = default) =>
            Task.FromResult(db.Read(d =>
            {
                var t = d.Transactions.FirstOrDefault(x => x.InvoiceNo == invoiceNo);
                if (t is null) return null;
                return WithIncludes(d, t);
            }));

        public Task<PaymentTransaction?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default) =>
            Task.FromResult(db.Read(d => d.Transactions.Where(x => x.IdempotencyKey == idempotencyKey).Select(Copy).FirstOrDefault()));

        public Task<IReadOnlyList<PaymentTransaction>> GetByCustomerAsync(int customerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PaymentTransaction>>(db.Read(d => d.Transactions
                .Where(x => x.CustomerId == customerId)
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .Select(t => WithIncludes(d, t))
                .ToList()));

        // Include(Product), Include(RetryOf), Include(Retry).
        private static PaymentTransaction WithIncludes(InMemoryDatabase d, PaymentTransaction t)
        {
            var copy = Copy(t);
            copy.Product = d.Products.Where(p => p.Id == t.ProductId).Select(Copy).FirstOrDefault()!;
            copy.RetryOf = d.Transactions.Where(x => x.Id == t.RetryOfTransactionId).Select(Copy).FirstOrDefault();
            copy.Retry = d.Transactions.Where(x => x.RetryOfTransactionId == t.Id).Select(Copy).FirstOrDefault();
            return copy;
        }

        public Task<bool> HasPendingForCardAsync(int customerCardId, CancellationToken ct = default) =>
            Task.FromResult(db.Read(d => d.Transactions.Any(x => x.CustomerCardId == customerCardId && x.Status == TransactionStatus.Pending)));

        public void Add(PaymentTransaction transaction)
        {
            lock (db._gate) session.PendingTransactions.Add(transaction);
        }

        public Task<bool> SavePaymentTokenAsync(long id, string paymentToken, CancellationToken ct = default) =>
            UpdatePending(id, t => t.PaymentToken = paymentToken);

        public Task<bool> SetCheckoutResultAsync(long id, string? redirectUrl, string? respCode, string? respDesc, CancellationToken ct = default) =>
            UpdatePending(id, t =>
            {
                t.RedirectUrl = redirectUrl;
                t.RespCode = respCode;
                t.RespDesc = respDesc;
            });

        public Task<bool> TryMarkSucceededAsync(long id, PaymentCompletion completion, CancellationToken ct = default) =>
            UpdatePending(id, t =>
            {
                t.Status = TransactionStatus.Succeeded;
                t.RespCode = completion.RespCode;
                t.RespDesc = completion.RespDesc;
                t.PaymentChannel = completion.ChannelCode;
                t.MaskedCardNo = completion.AccountNo;
                t.ProviderRef = completion.TranRef;
                t.PaidAt = completion.PaidAt;
            });

        public Task<bool> TryMarkFailedAsync(long id, string respCode, string? respDesc, CancellationToken ct = default) =>
            UpdatePending(id, t =>
            {
                t.Status = TransactionStatus.Failed;
                t.RespCode = respCode;
                t.RespDesc = respDesc;
            });

        public Task<bool> TryReserveRefundAsync(long id, decimal amount, CancellationToken ct = default)
        {
            lock (db._gate)
            {
                var t = db.Transactions.Single(x => x.Id == id);
                if (t.Status != TransactionStatus.Succeeded || t.RefundedAmount + amount > t.Amount)
                    return Task.FromResult(false);
                t.RefundedAmount += amount;
                return Task.FromResult(true);
            }
        }

        public Task ReleaseRefundAsync(long id, decimal amount, CancellationToken ct = default)
        {
            lock (db._gate) db.Transactions.Single(x => x.Id == id).RefundedAmount -= amount;
            return Task.CompletedTask;
        }

        private Task<bool> UpdatePending(long id, Action<PaymentTransaction> update)
        {
            lock (db._gate)
            {
                var t = db.Transactions.Single(x => x.Id == id);
                if (t.Status != TransactionStatus.Pending)
                    return Task.FromResult(false);
                update(t);
                t.UpdatedAt = DateTime.UtcNow;
                return Task.FromResult(true);
            }
        }
    }

    private sealed class Refunds_(InMemoryDatabase db, Session session) : IRefundRepository
    {
        public Task<Refund?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default) =>
            Task.FromResult(db.Read(d => d.Refunds.Where(r => r.IdempotencyKey == idempotencyKey).Select(Copy).FirstOrDefault()));

        public Task<IReadOnlyList<Refund>> GetByTransactionAsync(long transactionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Refund>>(db.Read(d => d.Refunds.Where(r => r.TransactionId == transactionId).Select(Copy).ToList()));

        public void Add(Refund refund)
        {
            lock (db._gate) session.PendingRefunds.Add(refund);
        }

        public Task<bool> SetProviderResultAsync(long id, RefundProviderResult result, CancellationToken ct = default) =>
            UpdatePending(id, r => Apply(r, result));

        public Task<bool> TryMarkSucceededAsync(long id, RefundProviderResult result, DateTime completedAt, CancellationToken ct = default) =>
            UpdatePending(id, r =>
            {
                Apply(r, result);
                r.Status = RefundStatus.Succeeded;
                r.CompletedAt = completedAt;
            });

        public Task<bool> TryMarkFailedAsync(long id, RefundProviderResult result, DateTime completedAt, CancellationToken ct = default) =>
            UpdatePending(id, r =>
            {
                Apply(r, result);
                r.Status = RefundStatus.Failed;
                r.CompletedAt = completedAt;
            });

        private static void Apply(Refund r, RefundProviderResult result)
        {
            r.RespCode = result.RespCode;
            r.RespDesc = result.RespDesc;
            r.ProviderStatus = result.ProviderStatus;
            r.ProviderRef = result.ProviderRef ?? r.ProviderRef;
        }

        private Task<bool> UpdatePending(long id, Action<Refund> update)
        {
            lock (db._gate)
            {
                var r = db.Refunds.Single(x => x.Id == id);
                if (r.Status != RefundStatus.Pending)
                    return Task.FromResult(false);
                update(r);
                return Task.FromResult(true);
            }
        }
    }

    private sealed class UnitOfWork_(InMemoryDatabase db, Session session) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            lock (db._gate)
            {
                try
                {
                    var count = 0;
                    foreach (var t in session.PendingTransactions)
                    {
                        if (db.Transactions.Any(x => x.IdempotencyKey == t.IdempotencyKey || x.InvoiceNo == t.InvoiceNo))
                            throw new ConflictException("Duplicate entry for key 'uq_txn_idempotency_key'.");
                        if (t.RetryOfTransactionId is not null && db.Transactions.Any(x => x.RetryOfTransactionId == t.RetryOfTransactionId))
                            throw new ConflictException("Duplicate entry for key 'uq_txn_retry_of'.");
                        t.Id = db._nextId++;
                        db.Transactions.Add(t);
                        count++;
                    }
                    foreach (var c in session.PendingCards)
                    {
                        if (db.Cards.Any(x => x.CustomerId == c.CustomerId && x.CustomerToken == c.CustomerToken))
                            throw new ConflictException("Duplicate entry for key 'uq_card_customer_token'.");
                        c.Id = (int)db._nextId++;
                        db.Cards.Add(c);
                        count++;
                    }
                    foreach (var r in session.PendingRefunds)
                    {
                        if (db.Refunds.Any(x => x.IdempotencyKey == r.IdempotencyKey))
                            throw new ConflictException("Duplicate entry for key 'uq_refund_idempotency_key'.");
                        r.Id = db._nextId++;
                        db.Refunds.Add(r);
                        _scopeInserts?.Add(r);
                        count++;
                    }
                    return Task.FromResult(count);
                }
                finally
                {
                    session.PendingTransactions.Clear();
                    session.PendingCards.Clear();
                    session.PendingRefunds.Clear();
                }
            }
        }

        private List<Refund>? _scopeInserts;

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
        {
            // Rolls back the rows this scope inserted. (The services only reserve refund amounts as the last step,
            // so nothing else needs undoing.)
            _scopeInserts = [];
            try
            {
                await work(ct);
            }
            catch
            {
                lock (db._gate) db.Refunds.RemoveAll(r => _scopeInserts.Contains(r));
                throw;
            }
            finally
            {
                _scopeInserts = null;
            }
        }
    }
}

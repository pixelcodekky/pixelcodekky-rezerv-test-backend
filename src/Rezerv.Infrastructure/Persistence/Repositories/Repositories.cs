using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Rezerv.Infrastructure.Persistence.Repositories
{
    internal class Repositories { }
    
    public class CustomerRepository(AppDbContext db) : ICustomerRepository
    {
        public async Task<bool> ExistsAsync(int id, CancellationToken ct = default)
        {
            return await db.customers.AnyAsync(x => x.Id == id, ct);
        }

        public async Task<IReadOnlyList<Customer>> GetAllAsync(CancellationToken ct = default)
        {
            return await db.customers.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        }

        public async Task<Customer?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            return await db.customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        }
    }

    public class ProductRepository(AppDbContext db) : IProductRepository
    {
        public async Task<IReadOnlyList<Product>> GetAllActiveAsync(CancellationToken ct = default)
        {
            return await db.products.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Id).ToListAsync(ct);
        }
        public async Task<Product?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            return await db.products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        }
    }

    public class CustomerCardRepository(AppDbContext db) : ICustomerCardRepository
    {
        public void Add(CustomerCard card)
        {
            db.customerCards.Add(card);
        }
        public async Task<IReadOnlyList<CustomerCard>> GetByCustomerAsync(int customerId, CancellationToken ct = default)
        {
            return await db.customerCards.AsNoTracking().Where(x => x.CustomerId == customerId).ToListAsync(ct);
        }
        public async Task<CustomerCard?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            return await db.customerCards.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        }
        public async Task<CustomerCard?> GetByTokenAsync(int customerId, string customerToken, CancellationToken ct = default)
        {
            return await db.customerCards.AsNoTracking().FirstOrDefaultAsync(x => x.CustomerId == customerId && x.CustomerToken == customerToken, ct);
        }
        public async Task<bool> DeleteAsync(int id, int customerId, CancellationToken ct = default)
        {
            // MySQL nulls transaction.customer_card_id itself (fk_txn_customer_card ON DELETE SET NULL).
            return await db.customerCards.Where(x => x.Id == id && x.CustomerId == customerId).ExecuteDeleteAsync(ct) == 1;
        }
    }

    public class PaymentTransactionRepository(AppDbContext db) : IPaymentTransactionRepository
    {
        public void Add(PaymentTransaction transaction)
        {
            db.transactions.Add(transaction);
        }
        public async Task<PaymentTransaction?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
        {
            return await db.transactions.AsNoTracking().FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
        }
        public async Task<PaymentTransaction?> GetByInvoiceNoAsync(string invoiceNo, CancellationToken ct = default)
        {
            return await db.transactions.AsNoTracking().Include(x => x.Product).Include(x => x.RetryOf).Include(x => x.Retry)
                .FirstOrDefaultAsync(x => x.InvoiceNo == invoiceNo, ct);
        }
        public async Task<IReadOnlyList<PaymentTransaction>> GetByCustomerAsync(int customerId, CancellationToken ct = default)
        {
            // Served by ix_txn_customer_created.
            return await db.transactions.AsNoTracking().Include(x => x.Product).Include(x => x.RetryOf).Include(x => x.Retry)
                .Where(x => x.CustomerId == customerId)
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .ToListAsync(ct);
        }

        public Task<bool> HasPendingForCardAsync(int customerCardId, CancellationToken ct = default)
        {
            return db.transactions.AnyAsync(x => x.CustomerCardId == customerCardId && x.Status == TransactionStatus.Pending, ct);
        }

        public async Task<bool> SavePaymentTokenAsync(long id, string paymentToken, CancellationToken ct = default)
        {
            return await Pending(id).ExecuteUpdateAsync(s => s.SetProperty(x => x.PaymentToken, paymentToken), ct) == 1;
        }

        public async Task<bool> SetCheckoutResultAsync(long id, string? redirectUrl, string? respCode, string? respDesc, CancellationToken ct = default)
        {
            return await Pending(id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RedirectUrl, redirectUrl)
                .SetProperty(x => x.RespCode, respCode)
                .SetProperty(x => x.RespDesc, respDesc), ct) == 1;
        }

        public async Task<bool> TryMarkSucceededAsync(long id, PaymentCompletion completion, CancellationToken ct = default)
        {
            return await Pending(id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, TransactionStatus.Succeeded)
                .SetProperty(x => x.RespCode, completion.RespCode)
                .SetProperty(x => x.RespDesc, completion.RespDesc)
                .SetProperty(x => x.PaymentChannel, completion.ChannelCode)
                .SetProperty(x => x.MaskedCardNo, completion.AccountNo)
                .SetProperty(x => x.ProviderRef, completion.TranRef)
                .SetProperty(x => x.PaidAt, completion.PaidAt), ct) == 1;
        }

        public async Task<bool> TryMarkFailedAsync(long id, string respCode, string? respDesc, CancellationToken ct = default)
        {
            return await Pending(id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, TransactionStatus.Failed)
                .SetProperty(x => x.RespCode, respCode)
                .SetProperty(x => x.RespDesc, respDesc), ct) == 1;
        }

        public async Task<bool> TryReserveRefundAsync(long id, decimal amount, CancellationToken ct = default)
        {
            return await db.transactions
                .Where(x => x.Id == id && x.Status == TransactionStatus.Succeeded && x.RefundedAmount + amount <= x.Amount)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RefundedAmount, x => x.RefundedAmount + amount), ct) == 1;
        }

        public async Task ReleaseRefundAsync(long id, decimal amount, CancellationToken ct = default)
        {
            await db.transactions
                .Where(x => x.Id == id && x.RefundedAmount >= amount)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RefundedAmount, x => x.RefundedAmount - amount), ct);
        }

        // UPDATE ... WHERE id = @id AND status = 'Pending': a final status is never overwritten, even by concurrent callers.
        private IQueryable<PaymentTransaction> Pending(long id) =>
            db.transactions.Where(x => x.Id == id && x.Status == TransactionStatus.Pending);
    }

    public class RefundRepository(AppDbContext db) : IRefundRepository
    {
        public void Add(Refund refund)
        {
            db.refunds.Add(refund);
        }

        public async Task<Refund?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
        {
            return await db.refunds.AsNoTracking().FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
        }

        public async Task<IReadOnlyList<Refund>> GetByTransactionAsync(long transactionId, CancellationToken ct = default)
        {
            return await db.refunds.AsNoTracking().Where(x => x.TransactionId == transactionId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        }

        public async Task<bool> SetProviderResultAsync(long id, RefundProviderResult result, CancellationToken ct = default)
        {
            return await Pending(id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RespCode, result.RespCode)
                .SetProperty(x => x.RespDesc, result.RespDesc)
                .SetProperty(x => x.ProviderStatus, result.ProviderStatus)
                .SetProperty(x => x.ProviderRef, result.ProviderRef), ct) == 1;
        }

        public Task<bool> TryMarkSucceededAsync(long id, RefundProviderResult result, DateTime completedAt, CancellationToken ct = default)
        {
            return CompleteAsync(id, RefundStatus.Succeeded, result, completedAt, ct);
        }

        public Task<bool> TryMarkFailedAsync(long id, RefundProviderResult result, DateTime completedAt, CancellationToken ct = default)
        {
            return CompleteAsync(id, RefundStatus.Failed, result, completedAt, ct);
        }
        private async Task<bool> CompleteAsync(long id, RefundStatus status, RefundProviderResult result, DateTime completedAt, CancellationToken ct)
        {
            return await Pending(id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.RespCode, result.RespCode)
                .SetProperty(x => x.RespDesc, result.RespDesc)
                .SetProperty(x => x.ProviderStatus, result.ProviderStatus)
                .SetProperty(x => x.ProviderRef, result.ProviderRef)
                .SetProperty(x => x.CompletedAt, completedAt), ct) == 1;
        }

        private IQueryable<Refund> Pending(long id) =>
            db.refunds.Where(x => x.Id == id && x.Status == RefundStatus.Pending);
    }

    public class UnitOfWork(AppDbContext db) : IUnitOfWork
    {
        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
        {
            if (db.Database.CurrentTransaction is not null)
            {
                await work(ct);
                return;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await work(ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                throw;
            }
        }

        public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            try
            {
                return await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is MySqlException { Number: 1062 }) 
            {
                db.ChangeTracker.Clear();
                throw new ConflictException("Unique-key violation", ex);
            }
        }
    }
}

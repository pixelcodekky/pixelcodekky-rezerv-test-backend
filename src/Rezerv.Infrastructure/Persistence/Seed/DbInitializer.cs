using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Domain.Entities;

namespace Rezerv.Infrastructure.Persistence.Seed
{
    public static class DbInitializer
    {
        public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cache = scope.ServiceProvider.GetRequiredService<ICacheService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DbInitializer));

            var created = await EnsureCreatedWithRetryAsync(db, logger, ct);
            if (created)
            {
                // EF can't express ON UPDATE; updated_at is ValueGeneratedOnAddOrUpdate so the DB must maintain it.
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE `transaction` MODIFY `updated_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3)", ct);
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE `refund` MODIFY `updated_at` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3)", ct);
            }
            else
            {
                await AddRetryLinkIfMissingAsync(db, logger, ct);
            }

            if (await db.customers.AnyAsync(ct))
                return;

            logger.LogInformation("Seeding customers, products and saved cards");
            var now = DateTime.UtcNow;

            var alice = new Customer { FirstName = "Alice", LastName = "Tan", Email = "alice.tan@example.com", CreatedAt = now };
            var bob = new Customer { FirstName = "Bob", LastName = "Lim", Email = "bob.lim@example.com", CreatedAt = now };
            var chloe = new Customer { FirstName = "Chloe", LastName = "Ng", Email = "chloe.ng@example.com", CreatedAt = now };
            db.customers.AddRange(alice, bob, chloe);

            db.products.AddRange(
                new Product { Name = "Single Session Pass", Description = "One-off access pass", Price = 25.00m, Currency = "SGD", PaymentType = PaymentType.OneTime, CreatedAt = now },
                new Product { Name = "Event Ticket", Description = "Admission to a single event", Price = 120.00m, Currency = "SGD", PaymentType = PaymentType.OneTime, CreatedAt = now },
                new Product { Name = "Monthly Membership", Description = "Recurring monthly plan", Price = 49.90m, Currency = "SGD", PaymentType = PaymentType.Recurring, CreatedAt = now },
                new Product { Name = "Annual Membership", Description = "Recurring yearly plan", Price = 499.00m, Currency = "SGD", PaymentType = PaymentType.Recurring, CreatedAt = now });

            // Placeholder tokens for listing/UI tests. A real 2C2P customerToken is created by paying once with a new card
            // (tokenize=true); it is then saved automatically from the notification and can be used for Do Payment.
            //db.customerCards.AddRange(
            //    new CustomerCard { Customer = alice, CustomerToken = "SEED-TOKEN-ALICE-VISA", MaskedCardNo = "411111XXXXXX1111", PaymentChannel = "CC", CreatedAt = now },
            //    new CustomerCard { Customer = alice, CustomerToken = "SEED-TOKEN-ALICE-MC", MaskedCardNo = "555555XXXXXX4444", PaymentChannel = "CC", CreatedAt = now },
            //    new CustomerCard { Customer = bob, CustomerToken = "SEED-TOKEN-BOB-VISA", MaskedCardNo = "401200XXXXXX0026", PaymentChannel = "CC", CreatedAt = now });

            await db.SaveChangesAsync(ct);

            // A fresh database means anything cached from a previous one is stale.
            await cache.RemoveAsync(CacheKeys.Customers, ct);
            await cache.RemoveAsync(CacheKeys.Products, ct);
            foreach (var id in new[] { alice.Id, bob.Id, chloe.Id })
                await cache.RemoveAsync(CacheKeys.CustomerCards(id), ct);
        }

        /// <summary>
        /// EnsureCreated does not alter an existing schema. Databases created before payment retries were added get
        /// transaction.retry_of_transaction_id (unique, FK to transaction) here, so they keep their data.
        /// </summary>
        private static async Task AddRetryLinkIfMissingAsync(AppDbContext db, ILogger logger, CancellationToken ct)
        {
            var exists = await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS `Value` FROM information_schema.columns " +
                "WHERE table_schema = DATABASE() AND table_name = 'transaction' AND column_name = 'retry_of_transaction_id'")
                .SingleAsync(ct);
            if (exists > 0)
                return;

            logger.LogInformation("Adding transaction.retry_of_transaction_id to the existing database");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE `transaction` ADD COLUMN `retry_of_transaction_id` bigint NULL, " +
                "ADD UNIQUE INDEX `uq_txn_retry_of` (`retry_of_transaction_id`), " +
                "ADD CONSTRAINT `fk_txn_retry_of` FOREIGN KEY (`retry_of_transaction_id`) REFERENCES `transaction` (`id`) ON DELETE RESTRICT", ct);
        }

        private static async Task<bool> EnsureCreatedWithRetryAsync(AppDbContext db, ILogger logger, CancellationToken ct)
        {
            const int maxAttempts = 10;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await db.Database.EnsureCreatedAsync(ct);
                }
                catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
                {
                    logger.LogWarning("Database not ready (attempt {Attempt}/{Max}): {Message}", attempt, maxAttempts, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
            }
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Rezerv.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Rezerv.Infrastructure.Persistence
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Customer> customers { get; set; }

        public DbSet<CustomerCard> customerCards { get; set; }

        public DbSet<Product> products { get; set; }

        public DbSet<PaymentTransaction> transactions { get; set; }

        public DbSet<Refund> refunds { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>(e =>
            {
                e.Property(x => x.CreatedAt)
                 .HasColumnType("datetime(3)")
                 .HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
            });

            modelBuilder.Entity<Product>(e =>
            {
                e.ToTable(t =>
                {
                    t.HasCheckConstraint("chk_product_price", "price > 0");
                    t.HasCheckConstraint("chk_product_currency", "currency = 'SGD'");
                });

                e.Property(x => x.Currency).HasDefaultValue("SGD");
                e.Property(x => x.PaymentType)
                 .HasConversion<string>()
                 .HasColumnType("enum('OneTime','Recurring')");
                e.Property(x => x.IsActive)
                 .HasColumnType("tinyint(1)")
                 .HasDefaultValue(true);
                e.Property(x => x.CreatedAt)
                 .HasColumnType("datetime(3)")
                 .HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
            });

            modelBuilder.Entity<PaymentTransaction>(e =>
            {
                e.ToTable(t =>
                {
                    t.HasCheckConstraint("chk_txn_amount", "amount > 0");
                    t.HasCheckConstraint("chk_txn_paid_at", "status <> 'Succeeded' OR paid_at IS NOT NULL");
                    t.HasCheckConstraint("chk_txn_refunded_amount", "refunded_amount >= 0 AND refunded_amount <= amount");
                });

                e.Property(x => x.RefundedAmount).HasDefaultValue(0m);

                e.Property(x => x.Currency).HasDefaultValue("SGD");
                e.Property(x => x.Status)
                 .HasConversion<string>()
                 .HasColumnType("enum('Pending','Succeeded','Failed')")
                 .HasDefaultValue(TransactionStatus.Pending)
                 .HasSentinel((TransactionStatus)(-1)); 

                e.Property(x => x.CreatedAt)
                 .HasColumnType("datetime(3)")
                 .HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
                e.Property(x => x.UpdatedAt)
                 .HasColumnType("datetime(3)")
                 .HasDefaultValueSql("CURRENT_TIMESTAMP(3)")
                 .ValueGeneratedOnAddOrUpdate(); 
                e.Property(x => x.PaidAt).HasColumnType("datetime(3)");

                e.HasOne(x => x.Customer)
                 .WithMany(c => c.Transactions)
                 .HasForeignKey(x => x.CustomerId)
                 .HasConstraintName("fk_txn_customer")
                 .OnDelete(DeleteBehavior.Restrict);

                e.HasOne(x => x.Product)
                 .WithMany(p => p.Transactions)
                 .HasForeignKey(x => x.ProductId)
                 .HasConstraintName("fk_txn_product")
                 .OnDelete(DeleteBehavior.Restrict);

                e.HasOne(x => x.CustomerCard)
                 .WithMany(c => c.Transactions)
                 .HasForeignKey(x => x.CustomerCardId)
                 .HasConstraintName("fk_txn_customer_card")
                 .OnDelete(DeleteBehavior.SetNull);

                e.HasOne(x => x.RetryOf)
                 .WithOne(x => x.Retry)
                 .HasForeignKey<PaymentTransaction>(x => x.RetryOfTransactionId)
                 .HasConstraintName("fk_txn_retry_of")
                 .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<Refund>(e =>
            {
                e.ToTable(t => t.HasCheckConstraint("chk_refund_amount", "amount > 0"));

                e.Property(x => x.Currency).HasDefaultValue("SGD");
                e.Property(x => x.Type)
                 .HasConversion<string>()
                 .HasColumnType("enum('Void','Refund')");
                e.Property(x => x.Status)
                 .HasConversion<string>()
                 .HasColumnType("enum('Pending','Succeeded','Failed')")
                 .HasDefaultValue(RefundStatus.Pending)
                 .HasSentinel((RefundStatus)(-1));

                e.Property(x => x.CreatedAt)
                 .HasColumnType("datetime(3)")
                 .HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
                e.Property(x => x.UpdatedAt)
                 .HasColumnType("datetime(3)")
                 .HasDefaultValueSql("CURRENT_TIMESTAMP(3)")
                 .ValueGeneratedOnAddOrUpdate();
                e.Property(x => x.CompletedAt).HasColumnType("datetime(3)");

                e.HasOne(x => x.Transaction)
                 .WithMany(t => t.Refunds)
                 .HasForeignKey(x => x.TransactionId)
                 .HasConstraintName("fk_refund_txn")
                 .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<CustomerCard>(e =>
            {
                e.Property(x => x.CreatedAt)
                 .HasColumnType("datetime(3)")
                 .HasDefaultValueSql("CURRENT_TIMESTAMP(3)");
                e.Property(x => x.LastUsedAt).HasColumnType("datetime(3)");

                e.HasOne(x => x.Customer)
                 .WithMany(c => c.Cards)
                 .HasForeignKey(x => x.CustomerId)
                 .HasConstraintName("fk_card_customer")
                 .OnDelete(DeleteBehavior.Cascade);
            });

            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        }
    }
}

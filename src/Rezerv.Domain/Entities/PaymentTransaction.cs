using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Rezerv.Domain.Entities
{
    public enum PaymentType
    {
        OneTime,
        Recurring
    }

    public enum TransactionStatus
    {
        Pending,
        Succeeded,
        Failed
    }

    [Table("transaction")]
    [Index(nameof(InvoiceNo), IsUnique = true, Name = "uq_txn_invoice_no")]
    [Index(nameof(IdempotencyKey), IsUnique = true, Name = "uq_txn_idempotency_key")]
    [Index(nameof(CustomerId), nameof(CreatedAt), Name = "ix_txn_customer_created")]
    [Index(nameof(ProductId), Name = "ix_txn_product")]
    [Index(nameof(Status), nameof(CreatedAt), Name = "ix_txn_status_created")]
    [Index(nameof(ProviderRef), Name = "ix_txn_provider_ref")]
    [Index(nameof(RetryOfTransactionId), IsUnique = true, Name = "uq_txn_retry_of")]
    public class PaymentTransaction
    {
        [Key]
        [Column("id")]
        public long Id { get; set; }

        [Column("customer_id")]
        public int CustomerId { get; set; }

        [Column("product_id")]
        public int ProductId { get; set; }

        /// <summary>Sent to 2C2P as invoiceNo.</summary>
        [Required, MaxLength(50)]
        [Column("invoice_no")]
        public string InvoiceNo { get; set; } = null!;

        [Required, MaxLength(100)]
        [Column("idempotency_key")]
        public string IdempotencyKey { get; set; } = null!;

        /// <summary>Snapshot of product.price at purchase time.</summary>
        [Column("amount", TypeName = "decimal(12,2)")]
        public decimal Amount { get; set; }

        [Required, StringLength(3, MinimumLength = 3)]
        [Column("currency", TypeName = "char(3)")]
        public string Currency { get; set; } = "SGD";

        [Column("status")]
        public TransactionStatus Status { get; set; } = TransactionStatus.Pending;

        /// <summary>Notification channelCode.</summary>
        [MaxLength(20)]
        [Column("payment_channel")]
        public string? PaymentChannel { get; set; }

        /// <summary>Notification accountNo, e.g. 411111XXXXXX1111.</summary>
        [MaxLength(30)]
        [Column("masked_card_no")]
        public string? MaskedCardNo { get; set; }

        /// <summary>Notification tranRef.</summary>
        [MaxLength(50)]
        [Column("provider_ref")]
        public string? ProviderRef { get; set; }

        [MaxLength(10)]
        [Column("resp_code")]
        public string? RespCode { get; set; }

        [MaxLength(100)]
        [Column("resp_desc")]
        public string? RespDesc { get; set; }

        /// <summary>paymentToken from the 2C2P Payment Token API.</summary>
        [MaxLength(512)]
        [Column("payment_token")]
        public string? PaymentToken { get; set; }

        /// <summary>Hosted payment page or 3DS URL returned to the client; replayed for a repeated Idempotency-Key.</summary>
        [MaxLength(1024)]
        [Column("redirect_url")]
        public string? RedirectUrl { get; set; }

        /// <summary>Saved card used, if paid with a stored token.</summary>
        [Column("customer_card_id")]
        public int? CustomerCardId { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [Column("paid_at")]
        public DateTime? PaidAt { get; set; }

        /// <summary>Sum of refunds that succeeded or are still pending. Reserved atomically so refunds never exceed Amount.</summary>
        [Column("refunded_amount", TypeName = "decimal(12,2)")]
        public decimal RefundedAmount { get; set; }

        /// <summary>
        /// The failed transaction this one retries. Unique, so a failed transaction can be retried only once:
        /// the final guard against two retries charging the customer twice.
        /// </summary>
        [Column("retry_of_transaction_id")]
        public long? RetryOfTransactionId { get; set; }

        public Customer Customer { get; set; } = null!;
        public Product Product { get; set; } = null!;
        public CustomerCard? CustomerCard { get; set; }
        public PaymentTransaction? RetryOf { get; set; }
        public PaymentTransaction? Retry { get; set; }
        public ICollection<Refund> Refunds { get; set; } = new List<Refund>();
    }
}

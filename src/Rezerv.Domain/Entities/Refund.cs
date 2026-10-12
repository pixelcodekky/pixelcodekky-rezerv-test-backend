using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Rezerv.Domain.Entities
{
    public enum RefundType
    {
        /// <summary>Cancels an authorised payment before settlement; the customer is never charged. Full amount only.</summary>
        Void,
        /// <summary>Returns money for a settled payment; full or partial.</summary>
        Refund
    }

    public enum RefundStatus
    {
        Pending,
        Succeeded,
        Failed
    }

    [Table("refund")]
    [Index(nameof(IdempotencyKey), IsUnique = true, Name = "uq_refund_idempotency_key")]
    [Index(nameof(TransactionId), nameof(CreatedAt), Name = "ix_refund_txn_created")]
    public class Refund
    {
        [Key]
        [Column("id")]
        public long Id { get; set; }

        [Column("transaction_id")]
        public long TransactionId { get; set; }

        /// <summary>Idempotency-Key of the refund request; also sent to 2C2P as idempotencyID.</summary>
        [Required, MaxLength(100)]
        [Column("idempotency_key")]
        public string IdempotencyKey { get; set; } = null!;

        [Column("type")]
        public RefundType Type { get; set; }

        [Column("amount", TypeName = "decimal(12,2)")]
        public decimal Amount { get; set; }

        [Required, StringLength(3, MinimumLength = 3)]
        [Column("currency", TypeName = "char(3)")]
        public string Currency { get; set; } = "SGD";

        [Column("status")]
        public RefundStatus Status { get; set; } = RefundStatus.Pending;

        [MaxLength(255)]
        [Column("reason")]
        public string? Reason { get; set; }

        /// <summary>2C2P Payment Action status, e.g. V, VP, RF, RP, RFF.</summary>
        [MaxLength(20)]
        [Column("provider_status")]
        public string? ProviderStatus { get; set; }

        /// <summary>2C2P refundReferenceNo (refund) or referenceNo (void).</summary>
        [MaxLength(50)]
        [Column("provider_ref")]
        public string? ProviderRef { get; set; }

        [MaxLength(10)]
        [Column("resp_code")]
        public string? RespCode { get; set; }

        [MaxLength(100)]
        [Column("resp_desc")]
        public string? RespDesc { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [Column("completed_at")]
        public DateTime? CompletedAt { get; set; }

        public PaymentTransaction Transaction { get; set; } = null!;
    }
}

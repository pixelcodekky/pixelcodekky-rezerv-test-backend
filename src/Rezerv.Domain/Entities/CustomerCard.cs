using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using System.Xml.Linq;

namespace Rezerv.Domain.Entities
{
    [Table("customer_card")]
    [Index(nameof(CustomerId), nameof(CustomerToken), IsUnique = true, Name = "uq_card_customer_token")]
    public class CustomerCard
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("customer_id")]
        public int CustomerId { get; set; }

        /// <summary>customerToken from the 2C2P notification.</summary>
        [Required, MaxLength(100)]
        [Column("customer_token")]
        public string CustomerToken { get; set; } = null!;

        /// <summary>For the saved-card dropdown.</summary>
        [Required, MaxLength(30)]
        [Column("masked_card_no")]
        public string MaskedCardNo { get; set; } = null!;

        [MaxLength(20)]
        [Column("payment_channel")]
        public string? PaymentChannel { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("last_used_at")]
        public DateTime? LastUsedAt { get; set; }

        public Customer Customer { get; set; } = null!;
        public ICollection<PaymentTransaction> Transactions { get; set; } = new List<PaymentTransaction>();
    }
}

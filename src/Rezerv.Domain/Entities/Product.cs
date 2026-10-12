using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

namespace Rezerv.Domain.Entities
{
    [Table("product")]
    public class Product
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Required, MaxLength(150)]
        [Column("name")]
        public string Name { get; set; } = null!;

        [MaxLength(500)]
        [Column("description")]
        public string? Description { get; set; }

        [Column("price", TypeName = "decimal(12,2)")]
        public decimal Price { get; set; }

        [Required, StringLength(3, MinimumLength = 3)]
        [Column("currency", TypeName = "char(3)")]
        public string Currency { get; set; } = "SGD";

        [Column("payment_type")]
        public PaymentType PaymentType { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; } = true;

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        public ICollection<PaymentTransaction> Transactions { get; set; } = new List<PaymentTransaction>();
    }
}

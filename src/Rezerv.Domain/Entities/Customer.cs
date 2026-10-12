using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using System.Xml.Linq;

namespace Rezerv.Domain.Entities
{
    [Table("customer")]
    [Index(nameof(Email), IsUnique = true, Name = "uq_customer_email")]
    public class Customer
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Required, MaxLength(100)]
        [Column("first_name")]
        public string FirstName { get; set; } = null!;

        [Required, MaxLength(100)]
        [Column("last_name")]
        public string LastName { get; set; } = null!;

        [Required, MaxLength(255)]
        [Column("email")]
        public string Email { get; set; } = null!;

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        public ICollection<PaymentTransaction> Transactions { get; set; } = new List<PaymentTransaction>();
        public ICollection<CustomerCard> Cards { get; set; } = new List<CustomerCard>();
    }
}

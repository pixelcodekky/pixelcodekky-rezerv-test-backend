using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Rezerv.Infrastructure.Payment._2C2P
{
    public class _2C2POptions
    {
        public const string SectionName = "_2C2P";
        [Required, Url]
        public string BaseUrl { get; set; } = "https://sandbox-pgw.2c2p.com";
        [Required]
        public string MerchantId { get; set; } = string.Empty;
        [Required]
        public string SecretKey { get; set; } = string.Empty;
        /// <summary>
        /// Where 2C2P sends the browser after the hosted page or 3DS. May contain <see cref="InvoiceNoPlaceholder"/>,
        /// replaced per transaction, e.g. http://localhost:3000/api/result/{invoiceNo}.
        /// </summary>
        [Required, Url]
        public string FrontendReturnUrl { get; set; } = string.Empty;

        public const string InvoiceNoPlaceholder = "{invoiceNo}";

        public string FrontendReturnUrlFor(string invoiceNo) =>
            FrontendReturnUrl.Replace(InvoiceNoPlaceholder, Uri.EscapeDataString(invoiceNo), StringComparison.OrdinalIgnoreCase);
        [Required, Url]
        public string BackendReturnUrl { get; set; } = string.Empty;
        public string Locale { get; set; } = "en";


        [Range(1, 120)]
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>Payment Maintenance (Payment Action API) used for void and refund. Optional: refunds are disabled without keys.</summary>
        public PaymentActionOptions PaymentAction { get; set; } = new();
    }

    public class PaymentActionOptions
    {
        public string Url { get; set; } = "https://demo2.2c2p.com/PaymentAction/2.0/action";

        public string Version { get; set; } = "4.3";

        /// <summary>Version of the Store Card Maintenance request (MaintenanceRequest) used to remove saved cards.</summary>
        public string MaintenanceVersion { get; set; } = "2.3";

        /// <summary>Merchant private key: PEM (PKCS#1/PKCS#8, optionally encrypted) or a .pfx/.p12 file.</summary>
        public string? MerchantPrivateKeyPath { get; set; }

        public string? MerchantPrivateKeyPassword { get; set; }

        /// <summary>2C2P public key from the merchant portal (Account > Options > 2C2P Public Keys): certificate (PEM or DER) or PEM public key.</summary>
        public string? TwoC2PPublicKeyPath { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(MerchantPrivateKeyPath) && !string.IsNullOrWhiteSpace(TwoC2PPublicKeyPath);

        /// <summary>
        /// Development only: answer refunds with <see cref="SimulatedPaymentActionGateway"/> instead of calling 2C2P
        /// (for the shared demo merchant, which has no keys). The API refuses to start with this outside Development.
        /// </summary>
        public bool Simulate { get; set; }
    }
}

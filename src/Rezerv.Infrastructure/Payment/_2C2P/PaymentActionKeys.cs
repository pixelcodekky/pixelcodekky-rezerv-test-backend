using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Rezerv.Application.Common;

namespace Rezerv.Infrastructure.Payment._2C2P
{
    /// <summary>
    /// Keys for the Payment Action API, loaded once on first use (singleton), so the API starts without them
    /// and only refunds are unavailable until they are configured.
    /// </summary>
    public sealed class PaymentActionKeys
    {
        private readonly PaymentActionOptions _options;
        private readonly Lazy<(RSA MerchantPrivate, RSA TwoC2PPublic)> _keys;

        public PaymentActionKeys(IOptions<_2C2POptions> options)
            : this(options.Value.PaymentAction, null, null) { }

        /// <summary>For tests: keys supplied directly.</summary>
        internal PaymentActionKeys(PaymentActionOptions options, RSA? merchantPrivate, RSA? twoC2PPublic)
        {
            _options = options;
            _keys = merchantPrivate is not null && twoC2PPublic is not null
                ? new Lazy<(RSA, RSA)>((merchantPrivate, twoC2PPublic))
                : new Lazy<(RSA, RSA)>(Load);
        }

        public bool IsConfigured => _keys.IsValueCreated || _options.IsConfigured;

        public RSA MerchantPrivate => _keys.Value.MerchantPrivate;

        public RSA TwoC2PPublic => _keys.Value.TwoC2PPublic;

        private (RSA, RSA) Load()
        {
            if (!_options.IsConfigured)
                throw new NotConfiguredException("Refunds need _2C2P:PaymentAction:MerchantPrivateKeyPath and TwoC2PPublicKeyPath.");
            try
            {
                return (LoadPrivateKey(_options.MerchantPrivateKeyPath!, _options.MerchantPrivateKeyPassword),
                    LoadPublicKey(_options.TwoC2PPublicKeyPath!));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
            {
                throw new NotConfiguredException($"2C2P Payment Action keys could not be loaded: {ex.Message}");
            }
        }

        private static RSA LoadPrivateKey(string path, string? password)
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".p12", StringComparison.OrdinalIgnoreCase))
            {
                // Not disposed: on some platforms the private key handle stays tied to the certificate.
                var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
                return certificate.GetRSAPrivateKey() ?? throw new CryptographicException($"{path} has no RSA private key.");
            }

            var rsa = RSA.Create();
            if (string.IsNullOrEmpty(password))
                rsa.ImportFromPem(File.ReadAllText(path));
            else
                rsa.ImportFromEncryptedPem(File.ReadAllText(path), password);
            return rsa;
        }

        private static RSA LoadPublicKey(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {
                using var pem = X509Certificate2.CreateFromPem(text);
                return pem.GetRSAPublicKey() ?? throw new CryptographicException($"{path} has no RSA public key.");
            }
            if (text.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                var rsa = RSA.Create();
                rsa.ImportFromPem(text);
                return rsa;
            }

            using var der = X509CertificateLoader.LoadCertificate(bytes);
            return der.GetRSAPublicKey() ?? throw new CryptographicException($"{path} has no RSA public key.");
        }
    }
}

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rezerv.Application.Common;

namespace Rezerv.Infrastructure.Payment._2C2P
{
    /// <summary>
    /// "JWE + JWS with keys" as used by the 2C2P Payment Action API: the payload is encrypted as a compact JWE
    /// (RSA-OAEP key wrap, A256GCM content) for the recipient, and that JWE is signed as a compact JWS (PS256) by the sender.
    /// Requests: encrypt with the 2C2P public key, sign with the merchant private key.
    /// Responses: verify with the 2C2P public key, decrypt with the merchant private key.
    /// </summary>
    internal static class RsaJose
    {
        private const string KeyAlgorithm = "RSA-OAEP";
        private const string ContentAlgorithm = "A256GCM";
        private const string SignatureAlgorithm = "PS256";
        private const int TagSize = 16;

        private static readonly string JweHeader = Base64Url.EncodeToString(
            JsonSerializer.SerializeToUtf8Bytes(new { alg = KeyAlgorithm, enc = ContentAlgorithm }));

        private static readonly string JwsHeader = Base64Url.EncodeToString(
            JsonSerializer.SerializeToUtf8Bytes(new { alg = SignatureAlgorithm }));

        public static string EncryptAndSign(string plaintext, RSA recipientPublicKey, RSA signerPrivateKey)
        {
            var jwe = Encrypt(Encoding.UTF8.GetBytes(plaintext), recipientPublicKey);
            var signingInput = $"{JwsHeader}.{Base64Url.EncodeToString(Encoding.ASCII.GetBytes(jwe))}";
            var signature = signerPrivateKey.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
        }

        /// <exception cref="InvalidSignatureException">Malformed token, unexpected algorithm, bad signature, or undecryptable content.</exception>
        public static string VerifyAndDecrypt(string? token, RSA signerPublicKey, RSA recipientPrivateKey)
        {
            var parts = token?.Trim().Split('.');
            if (parts is not { Length: 3 })
                throw new InvalidSignatureException("Malformed JWS.");

            try
            {
                RequireAlgorithm(parts[0], "alg", SignatureAlgorithm);
                var valid = signerPublicKey.VerifyData(
                    Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.DecodeFromChars(parts[2]),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
                if (!valid)
                    throw new InvalidSignatureException("JWS signature verification failed.");

                return Encoding.UTF8.GetString(Decrypt(Encoding.ASCII.GetString(Base64Url.DecodeFromChars(parts[1])), recipientPrivateKey));
            }
            catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
            {
                throw new InvalidSignatureException($"Malformed or undecryptable JWS/JWE: {ex.Message}");
            }
        }

        private static string Encrypt(byte[] plaintext, RSA recipientPublicKey)
        {
            var cek = RandomNumberGenerator.GetBytes(32);
            var iv = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(cek, TagSize))
                aes.Encrypt(iv, plaintext, ciphertext, tag, Encoding.ASCII.GetBytes(JweHeader));

            var encryptedKey = recipientPublicKey.Encrypt(cek, RSAEncryptionPadding.OaepSHA1);
            return string.Join('.', JweHeader, Base64Url.EncodeToString(encryptedKey), Base64Url.EncodeToString(iv),
                Base64Url.EncodeToString(ciphertext), Base64Url.EncodeToString(tag));
        }

        private static byte[] Decrypt(string jwe, RSA recipientPrivateKey)
        {
            var parts = jwe.Split('.');
            if (parts.Length != 5)
                throw new InvalidSignatureException("Malformed JWE.");
            RequireAlgorithm(parts[0], "alg", KeyAlgorithm);
            RequireAlgorithm(parts[0], "enc", ContentAlgorithm);

            var cek = recipientPrivateKey.Decrypt(Base64Url.DecodeFromChars(parts[1]), RSAEncryptionPadding.OaepSHA1);
            var ciphertext = Base64Url.DecodeFromChars(parts[3]);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(cek, TagSize);
            aes.Decrypt(Base64Url.DecodeFromChars(parts[2]), ciphertext, Base64Url.DecodeFromChars(parts[4]), plaintext,
                Encoding.ASCII.GetBytes(parts[0]));
            return plaintext;
        }

        private static void RequireAlgorithm(string encodedHeader, string name, string expected)
        {
            using var header = JsonDocument.Parse(Base64Url.DecodeFromChars(encodedHeader));
            if (!header.RootElement.TryGetProperty(name, out var value) || value.GetString() != expected)
                throw new InvalidSignatureException($"Unexpected {name} (expected {expected}).");
        }
    }
}

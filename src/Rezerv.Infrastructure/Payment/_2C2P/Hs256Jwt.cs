using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rezerv.Application.Common;

namespace Rezerv.Infrastructure.Payment._2C2P
{
    /// <summary>JWT (HS256) as used by 2C2P PGW v4.3 for request/response payloads and backend notifications.</summary>
    internal static class Hs256Jwt
    {
        private static readonly byte[] Header = JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" });

        private static readonly JsonSerializerOptions Json = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public static string Encode(object payload, string secretKey)
        {
            var signingInput = $"{Base64Url.EncodeToString(Header)}.{Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload, Json))}";
            return $"{signingInput}.{Base64Url.EncodeToString(Sign(signingInput, secretKey))}";
        }

        /// <summary>Verifies the signature with the merchant secret key and returns the claims.</summary>
        /// <exception cref="InvalidSignatureException">Malformed token, unexpected algorithm, or bad signature.</exception>
        public static JsonElement Decode(string? token, string secretKey)
        {
            var parts = token?.Split('.');
            if (parts is not { Length: 3 })
                throw new InvalidSignatureException("Malformed JWT.");

            try
            {
                using (var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])))
                {
                    if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "HS256")
                        throw new InvalidSignatureException("Unexpected JWT algorithm.");
                }

                var expected = Sign($"{parts[0]}.{parts[1]}", secretKey);
                if (!CryptographicOperations.FixedTimeEquals(expected, Base64Url.DecodeFromChars(parts[2])))
                    throw new InvalidSignatureException("JWT signature verification failed.");

                using var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
                return claims.RootElement.Clone();
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                throw new InvalidSignatureException("Malformed JWT.");
            }
        }

        private static byte[] Sign(string signingInput, string secretKey) =>
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.ASCII.GetBytes(signingInput));
    }
}

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Application.Services;

namespace Rezerv.Infrastructure.Payment._2C2P
{
    /// <summary>
    /// 2C2P PGW v4.3. Payment Token and Payment Inquiry exchange {"payload": "&lt;JWT&gt;"} signed with the merchant secret key;
    /// Do Payment is plain JSON. The HttpClient deliberately has no retry policy: retrying Do Payment could charge twice.
    /// </summary>
    public sealed class _2C2PPaymentGateway(HttpClient http, IOptions<_2C2POptions> options, ILogger<_2C2PPaymentGateway> logger) : IPaymentGateway
    {
        private readonly _2C2POptions _options = options.Value;

        public async Task<PaymentTokenResult> CreatePaymentTokenAsync(PaymentTokenRequest request, CancellationToken ct = default)
        {
            var claims = new Dictionary<string, object?>
            {
                ["merchantID"] = _options.MerchantId,
                ["invoiceNo"] = request.InvoiceNo,
                ["description"] = request.Description,
                ["amount"] = request.Amount,
                ["currencyCode"] = request.CurrencyCode,
                ["locale"] = _options.Locale,
                ["frontendReturnUrl"] = _options.FrontendReturnUrlFor(request.InvoiceNo),
                ["backendReturnUrl"] = _options.BackendReturnUrl,
                ["tokenize"] = request.Tokenize,
                ["uiParams"] = new { userInfo = new { name = request.CustomerName, email = request.CustomerEmail } }
            };
            // No paymentChannel = no restriction: 2C2P shows every method enabled for the merchant.
            if (request.PaymentChannels is { Count: > 0 })
                claims["paymentChannel"] = request.PaymentChannels;
            if (request.CustomerToken is not null)
                claims["customerToken"] = new[] { request.CustomerToken };
            if (request.CustomerTokenOnly)
                claims["customerTokenOnly"] = true;
            if (request.Request3DS is not null)
                claims["request3DS"] = request.Request3DS;

            var response = await PostJwtAsync("payment/4.3/paymentToken", claims, ct);
            var respCode = response.Str("respCode") ?? string.Empty;
            logger.LogInformation("2C2P Payment Token for {InvoiceNo}: {RespCode} {RespDesc}", request.InvoiceNo, respCode, response.Str("respDesc"));

            return new PaymentTokenResult(
                respCode == _2C2PResponseCodes.Success,
                response.Str("paymentToken"),
                response.Str("webPaymentUrl"),
                respCode,
                response.Str("respDesc"));
        }

        public async Task<DoPaymentResult> DoPaymentAsync(DoPaymentRequest request, CancellationToken ct = default)
        {
            var body = new
            {
                paymentToken = request.PaymentToken,
                locale = _options.Locale,
                responseReturnUrl = _options.FrontendReturnUrlFor(request.InvoiceNo),
                clientIP = request.ClientIp,
                payment = new
                {
                    code = new { channelCode = PaymentRules.CardChannel },
                    data = new { name = request.CustomerName, email = request.CustomerEmail, customerToken = request.CustomerToken }
                }
            };

            var response = await SendAsync("payment/4.3/payment", JsonContent.Create(body), ct);
            var respCode = response.Str("respCode") ?? string.Empty;
            var redirectUrl = response.Str("data");
            var outcome = _2C2PResponseCodes.ToDoPaymentOutcome(respCode, redirectUrl);
            logger.LogInformation("2C2P Do Payment: {RespCode} {RespDesc} -> {Outcome}", respCode, response.Str("respDesc"), outcome);

            return new DoPaymentResult(outcome, respCode, response.Str("respDesc"),
                outcome == DoPaymentOutcome.Redirect ? redirectUrl : null);
        }

        public async Task<PaymentResult> InquireAsync(string invoiceNo, CancellationToken ct = default)
        {
            var claims = new { merchantID = _options.MerchantId, invoiceNo, locale = _options.Locale };
            var response = await PostJwtAsync("payment/4.3/paymentInquiry", claims, ct, requireSignedResponse: true);
            var result = ToPaymentResult(response, invoiceNo, _2C2PResponseCodes.InquiryStatus);
            logger.LogInformation("2C2P Payment Inquiry for {InvoiceNo}: {RespCode} {RespDesc} -> {Status}",
                invoiceNo, result.RespCode, result.RespDesc, result.Status);
            return result;
        }

        public PaymentResult ParseNotification(string payload)
        {
            var claims = Hs256Jwt.Decode(payload, _options.SecretKey);
            if (!string.Equals(claims.Str("merchantID"), _options.MerchantId, StringComparison.Ordinal))
                throw new InvalidSignatureException("Notification is for a different merchant.");

            var invoiceNo = claims.Str("invoiceNo");
            if (string.IsNullOrWhiteSpace(invoiceNo))
                throw new InvalidSignatureException("Notification has no invoiceNo.");

            return ToPaymentResult(claims, invoiceNo, _2C2PResponseCodes.NotificationStatus);
        }

        private static PaymentResult ToPaymentResult(JsonElement claims, string invoiceNo, Func<string, Domain.Entities.TransactionStatus> status)
        {
            var respCode = claims.Str("respCode") ?? string.Empty;

            // When the merchant settles in another currency (e.g. JT01 in THB), 2C2P converts the payment:
            // amount/currencyCode are what it processed (605.88 THB), fxAmount/fxCurrencyCode what we requested (25.00 SGD).
            // Compare against what we requested.
            var fxAmount = claims.Decimal("fxAmount");
            var fxCurrency = claims.Str("fxCurrencyCode");
            var converted = fxAmount is not null && !string.IsNullOrWhiteSpace(fxCurrency);

            return new PaymentResult(
                claims.Str("invoiceNo") ?? invoiceNo,
                status(respCode),
                respCode,
                claims.Str("respDesc"),
                converted ? fxAmount : claims.Decimal("amount"),
                converted ? fxCurrency : claims.Str("currencyCode"),
                claims.Str("tranRef"),
                claims.Str("accountNo") ?? claims.Str("cardNo"),
                claims.Str("channelCode"),
                claims.Str("customerToken"));
        }

        /// <summary>Sends {"payload": JWT(claims)}; verifies and decodes the JWT in the answer. Error answers may come back as plain JSON.</summary>
        private async Task<JsonElement> PostJwtAsync(string path, object claims, CancellationToken ct, bool requireSignedResponse = false)
        {
            var response = await SendAsync(path, JsonContent.Create(new { payload = Hs256Jwt.Encode(claims, _options.SecretKey) }), ct);
            var payload = response.Str("payload");
            if (payload is null)
            {
                // A result we act on must be signed; an unsigned error body is not trusted as a payment result.
                if (requireSignedResponse)
                    throw new PaymentGatewayException(
                        $"2C2P {path} returned no signed payload: {response.Str("respCode")} {response.Str("respDesc")}");
                return response;
            }

            try
            {
                return Hs256Jwt.Decode(payload, _options.SecretKey);
            }
            catch (InvalidSignatureException ex)
            {
                throw new PaymentGatewayException($"2C2P {path} response failed signature verification.", ex);
            }
        }

        private async Task<JsonElement> SendAsync(string path, HttpContent content, CancellationToken ct)
        {
            try
            {
                using var response = await http.PostAsync(path, content, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new PaymentGatewayException($"2C2P {path} returned HTTP {(int)response.StatusCode}.");

                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new PaymentGatewayException($"2C2P {path} timed out.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new PaymentGatewayException($"2C2P {path} request failed: {ex.Message}", ex);
            }
            catch (JsonException ex)
            {
                throw new PaymentGatewayException($"2C2P {path} returned invalid JSON.", ex);
            }
        }
    }

    internal static class JsonElementExtensions
    {
        public static string? Str(this JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
                ? v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null
                }
                : null;

        public static decimal? Decimal(this JsonElement e, string name) =>
            decimal.TryParse(e.Str(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}

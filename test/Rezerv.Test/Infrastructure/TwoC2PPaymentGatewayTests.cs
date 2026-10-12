using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Domain.Entities;
using Rezerv.Infrastructure.Payment._2C2P;

namespace Rezerv.Tests.Infrastructure;

public class TwoC2PPaymentGatewayTests
{
    private const string Secret = "TEST-SECRET-KEY";
    private const string MerchantId = "JT01";

    private readonly StubHandler _handler = new();
    private readonly _2C2PPaymentGateway _gateway;

    public TwoC2PPaymentGatewayTests()
    {
        var options = new _2C2POptions
        {
            MerchantId = MerchantId,
            SecretKey = Secret,
            FrontendReturnUrl = "http://localhost:3000/result/{invoiceNo}",
            BackendReturnUrl = "https://api.example.com/api/payments/2c2p/notification"
        };
        var http = new HttpClient(_handler) { BaseAddress = new Uri("https://sandbox-pgw.2c2p.com/") };
        _gateway = new _2C2PPaymentGateway(http, Options.Create(options), NullLogger<_2C2PPaymentGateway>.Instance);
    }

    private static string Signed(object claims) => JsonSerializer.Serialize(new { payload = Hs256Jwt.Encode(claims, Secret) });

    private static PaymentTokenRequest TokenRequest(IReadOnlyList<string>? channels = null, bool tokenize = false,
        string? customerToken = null, bool tokenOnly = false, string? request3Ds = null) =>
        new("INV1", "Concert ticket", 25.00m, "SGD", tokenize, customerToken, "Alice Tan", "alice@example.com", channels, tokenOnly, request3Ds);

    private JsonElement SentClaims() =>
        Hs256Jwt.Decode(JsonDocument.Parse(_handler.LastBody!).RootElement.GetProperty("payload").GetString(), Secret);

    // ---------- Payment Token ----------

    [Fact]
    public async Task Payment_token_sends_a_signed_jwt_with_the_required_fields()
    {
        _handler.Respond(Signed(new { respCode = "0000", respDesc = "Success", paymentToken = "PT", webPaymentUrl = "https://pay" }));

        var result = await _gateway.CreatePaymentTokenAsync(TokenRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal("PT", result.PaymentToken);
        Assert.Equal("https://pay", result.WebPaymentUrl);
        Assert.EndsWith("payment/4.3/paymentToken", _handler.LastUri!.AbsolutePath);

        var claims = SentClaims();
        Assert.Equal(MerchantId, claims.GetProperty("merchantID").GetString());
        Assert.Equal("INV1", claims.GetProperty("invoiceNo").GetString());
        Assert.Equal("Concert ticket", claims.GetProperty("description").GetString());
        Assert.Equal(25.00m, claims.GetProperty("amount").GetDecimal());
        Assert.Equal("SGD", claims.GetProperty("currencyCode").GetString());
        Assert.Equal("http://localhost:3000/result/INV1", claims.GetProperty("frontendReturnUrl").GetString());
        Assert.Equal("https://api.example.com/api/payments/2c2p/notification", claims.GetProperty("backendReturnUrl").GetString());
    }

    [Fact]
    public async Task OneTime_payment_token_has_no_payment_channel()
    {
        _handler.Respond(Signed(new { respCode = "0000" }));

        await _gateway.CreatePaymentTokenAsync(TokenRequest());

        var claims = SentClaims();
        Assert.False(claims.TryGetProperty("paymentChannel", out _));
        Assert.False(claims.TryGetProperty("customerToken", out _));
        Assert.False(claims.TryGetProperty("customerTokenOnly", out _));
    }

    [Fact]
    public async Task Recurring_payment_token_is_card_only_with_tokenize()
    {
        _handler.Respond(Signed(new { respCode = "0000" }));

        await _gateway.CreatePaymentTokenAsync(TokenRequest(["CC"], tokenize: true));

        var claims = SentClaims();
        Assert.Equal("CC", Assert.Single(claims.GetProperty("paymentChannel").EnumerateArray()).GetString());
        Assert.True(claims.GetProperty("tokenize").GetBoolean());
    }

    [Fact]
    public async Task Saved_card_payment_token_sends_customer_token_array_token_only_and_no_3ds()
    {
        _handler.Respond(Signed(new { respCode = "0000" }));

        await _gateway.CreatePaymentTokenAsync(TokenRequest(["CC"], customerToken: "TKN", tokenOnly: true, request3Ds: "N"));

        var claims = SentClaims();
        Assert.Equal("TKN", Assert.Single(claims.GetProperty("customerToken").EnumerateArray()).GetString());
        Assert.True(claims.GetProperty("customerTokenOnly").GetBoolean());
        Assert.Equal("N", claims.GetProperty("request3DS").GetString());
    }

    [Fact]
    public async Task Payment_token_error_answer_is_returned_unsuccessful()
    {
        // 2C2P may answer errors as plain JSON.
        _handler.Respond("""{"respCode":"9015","respDesc":"Invalid merchant"}""");

        var result = await _gateway.CreatePaymentTokenAsync(TokenRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal("9015", result.RespCode);
        Assert.Equal("Invalid merchant", result.RespDesc);
    }

    [Fact]
    public async Task Payment_token_response_with_a_bad_signature_is_a_gateway_error()
    {
        _handler.Respond(JsonSerializer.Serialize(new { payload = Hs256Jwt.Encode(new { respCode = "0000" }, "wrong-key") }));

        await Assert.ThrowsAsync<PaymentGatewayException>(() => _gateway.CreatePaymentTokenAsync(TokenRequest()));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task Http_or_parse_failures_are_gateway_errors(HttpStatusCode status, string body)
    {
        _handler.Respond(body, status);

        await Assert.ThrowsAsync<PaymentGatewayException>(() => _gateway.CreatePaymentTokenAsync(TokenRequest()));
    }

    // ---------- Do Payment ----------

    [Fact]
    public async Task Do_payment_sends_plain_json_with_cc_channel_and_customer_token()
    {
        _handler.Respond("""{"respCode":"2000","respDesc":"Transaction is completed"}""");

        var result = await _gateway.DoPaymentAsync(new DoPaymentRequest("INV1", "PT", "TKN", "1.2.3.4", "Alice", "alice@example.com"));

        Assert.Equal(DoPaymentOutcome.Completed, result.Outcome);
        Assert.Equal("2000", result.RespCode);
        Assert.Null(result.RedirectUrl);

        var body = JsonDocument.Parse(_handler.LastBody!).RootElement;
        Assert.False(body.TryGetProperty("payload", out _));
        Assert.Equal("PT", body.GetProperty("paymentToken").GetString());
        Assert.Equal("1.2.3.4", body.GetProperty("clientIP").GetString());
        Assert.Equal("CC", body.GetProperty("payment").GetProperty("code").GetProperty("channelCode").GetString());
        Assert.Equal("TKN", body.GetProperty("payment").GetProperty("data").GetProperty("customerToken").GetString());
    }

    [Fact]
    public async Task Do_payment_1001_redirects_to_3ds()
    {
        _handler.Respond("""{"respCode":"1001","respDesc":"Redirect","data":"https://3ds/acs"}""");

        var result = await _gateway.DoPaymentAsync(new DoPaymentRequest("INV1", "PT", "TKN", "1.2.3.4", "Alice", "a@x"));

        Assert.Equal(DoPaymentOutcome.Redirect, result.Outcome);
        Assert.Equal("https://3ds/acs", result.RedirectUrl);
    }

    // ---------- Payment Inquiry ----------

    [Theory]
    [InlineData("0000", TransactionStatus.Succeeded)]
    [InlineData("4005", TransactionStatus.Failed)]
    public async Task Inquiry_maps_0000_to_succeeded_and_anything_else_to_failed(string respCode, TransactionStatus expected)
    {
        _handler.Respond(Signed(new { invoiceNo = "INV1", respCode, amount = 25.00m, currencyCode = "SGD", tranRef = "T1" }));

        var result = await _gateway.InquireAsync("INV1");

        Assert.Equal(expected, result.Status);
        Assert.Equal(25.00m, result.Amount);
        Assert.Equal("SGD", result.CurrencyCode);
        Assert.Equal("INV1", SentClaims().GetProperty("invoiceNo").GetString());
    }

    [Fact]
    public async Task Inquiry_requires_a_signed_response()
    {
        _handler.Respond("""{"respCode":"0000","invoiceNo":"INV1","amount":"25.00","currencyCode":"SGD"}""");

        await Assert.ThrowsAsync<PaymentGatewayException>(() => _gateway.InquireAsync("INV1"));
    }

    // ---------- Backend notification ----------

    [Fact]
    public void Notification_is_verified_and_decoded()
    {
        var payload = Hs256Jwt.Encode(new
        {
            merchantID = MerchantId, invoiceNo = "INV1", respCode = "0000", respDesc = "Success", amount = 25.00m, currencyCode = "SGD",
            tranRef = "T1", accountNo = "411111XXXXXX1111", channelCode = "VI", customerToken = "TKN"
        }, Secret);

        var result = _gateway.ParseNotification(payload);

        Assert.Equal("INV1", result.InvoiceNo);
        Assert.Equal(TransactionStatus.Succeeded, result.Status);
        Assert.Equal(25.00m, result.Amount);
        Assert.Equal("SGD", result.CurrencyCode);
        Assert.Equal("T1", result.TranRef);
        Assert.Equal("411111XXXXXX1111", result.AccountNo);
        Assert.Equal("VI", result.ChannelCode);
        Assert.Equal("TKN", result.CustomerToken);
    }

    [Fact]
    public void Notification_with_a_non_success_code_is_failed()
    {
        var payload = Hs256Jwt.Encode(new { merchantID = MerchantId, invoiceNo = "INV1", respCode = "4005", respDesc = "Declined" }, Secret);

        var result = _gateway.ParseNotification(payload);

        Assert.Equal(TransactionStatus.Failed, result.Status);
        Assert.Equal("Declined", result.RespDesc);
    }

    [Fact]
    public void Notification_signed_with_another_key_is_rejected()
    {
        var payload = Hs256Jwt.Encode(new { merchantID = MerchantId, invoiceNo = "INV1", respCode = "0000" }, "attacker-key");

        Assert.Throws<InvalidSignatureException>(() => _gateway.ParseNotification(payload));
    }

    [Fact]
    public void Notification_for_another_merchant_is_rejected()
    {
        var payload = Hs256Jwt.Encode(new { merchantID = "OTHER", invoiceNo = "INV1", respCode = "0000" }, Secret);

        Assert.Throws<InvalidSignatureException>(() => _gateway.ParseNotification(payload));
    }

    [Fact]
    public void Notification_without_invoice_is_rejected()
    {
        var payload = Hs256Jwt.Encode(new { merchantID = MerchantId, respCode = "0000" }, Secret);

        Assert.Throws<InvalidSignatureException>(() => _gateway.ParseNotification(payload));
    }

    [Fact]
    public void Converted_notification_reports_the_requested_fx_amount_and_currency()
    {
        var payload = Hs256Jwt.Encode(new
        {
            merchantID = MerchantId, invoiceNo = "INV1", respCode = "0000",
            amount = 605.88m, currencyCode = "THB", fxAmount = 25.00m, fxCurrencyCode = "SGD"
        }, Secret);

        var result = _gateway.ParseNotification(payload);

        Assert.Equal(25.00m, result.Amount);
        Assert.Equal("SGD", result.CurrencyCode);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private string _body = "{}";
        private HttpStatusCode _status = HttpStatusCode.OK;

        public Uri? LastUri { get; private set; }
        public string? LastBody { get; private set; }

        public void Respond(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }
}

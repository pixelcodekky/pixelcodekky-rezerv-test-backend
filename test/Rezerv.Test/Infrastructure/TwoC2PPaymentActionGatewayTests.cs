using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Infrastructure.Payment._2C2P;

namespace Rezerv.Tests.Infrastructure;

/// <summary>Store Card Maintenance (remove card token) over the signed and encrypted Payment Action channel.</summary>
public class TwoC2PPaymentActionGatewayTests
{
    private const string Token = "CUSTOMER-TOKEN-1";
    private readonly RSA _merchant = RSA.Create(2048);
    private readonly RSA _twoC2P = RSA.Create(2048);
    private readonly _2C2POptions _options = new() { MerchantId = "JT01" };

    private (_2C2PPaymentActionGateway Gateway, StubHandler Handler) Create(Func<XElement, string> answer)
    {
        var handler = new StubHandler(body =>
        {
            // What 2C2P does: verify our signature, decrypt with its key, answer signed with its key for us.
            var request = XElement.Parse(RsaJose.VerifyAndDecrypt(body, _merchant, _twoC2P));
            return RsaJose.EncryptAndSign(answer(request), _merchant, _twoC2P);
        });
        var keys = new PaymentActionKeys(_options.PaymentAction, _merchant, _twoC2P);
        var gateway = new _2C2PPaymentActionGateway(new HttpClient(handler), keys, Options.Create(_options),
            NullLogger<_2C2PPaymentActionGateway>.Instance);
        return (gateway, handler);
    }

    private static string Response(string respCode, string respReason, string token = Token) =>
        new XElement("MaintenanceResponse",
            new XElement("version", "2.3"),
            new XElement("merchantID", "JT01"),
            new XElement("respCode", respCode),
            new XElement("respReason", respReason),
            new XElement("storeCardUniqueID", token),
            new XElement("panMasked", "411111XXXXXX1111")).ToString();

    [Fact]
    public async Task Remove_card_sends_a_signed_maintenance_request_with_action_D()
    {
        XElement? sent = null;
        var (gateway, handler) = Create(req => { sent = req; return Response("00", "Success"); });

        var result = await gateway.RemoveCardTokenAsync(new CardTokenRemovalRequest(Token, "Alice Tan", "alice@example.com"));

        Assert.True(result.Removed);
        Assert.Equal("00", result.RespCode);
        Assert.Equal(_options.PaymentAction.Url, handler.RequestUri?.ToString());
        Assert.NotNull(sent);
        Assert.Equal("MaintenanceRequest", sent!.Name.LocalName);
        Assert.Equal("2.3", sent.Element("version")?.Value);
        Assert.Equal("JT01", sent.Element("merchantID")?.Value);
        Assert.Equal(Token, sent.Element("storeCardUniqueID")?.Value);
        Assert.Equal("Alice Tan", sent.Element("cardholderName")?.Value);
        Assert.Equal("alice@example.com", sent.Element("cardholderEmail")?.Value);
        Assert.Equal("D", sent.Element("action")?.Value);
        Assert.Null(sent.Element("pan"));
    }

    [Fact]
    public async Task Any_respCode_other_than_00_is_not_removed()
    {
        var (gateway, _) = Create(_ => Response("99", "Card not found"));

        var result = await gateway.RemoveCardTokenAsync(new CardTokenRemovalRequest(Token, "Alice Tan", null));

        Assert.False(result.Removed);
        Assert.Equal("99", result.RespCode);
        Assert.Equal("Card not found", result.RespReason);
    }

    [Fact]
    public async Task Answer_for_another_card_token_is_rejected()
    {
        var (gateway, _) = Create(_ => Response("00", "Success", token: "SOMEONE-ELSES-TOKEN"));

        await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            gateway.RemoveCardTokenAsync(new CardTokenRemovalRequest(Token, "Alice Tan", null)));
    }

    [Fact]
    public async Task Unsigned_answer_is_a_gateway_error()
    {
        var handler = new StubHandler(_ => "<MaintenanceResponse><respCode>00</respCode></MaintenanceResponse>");
        var gateway = new _2C2PPaymentActionGateway(new HttpClient(handler), new PaymentActionKeys(_options.PaymentAction, _merchant, _twoC2P),
            Options.Create(_options), NullLogger<_2C2PPaymentActionGateway>.Instance);

        await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            gateway.RemoveCardTokenAsync(new CardTokenRemovalRequest(Token, "Alice Tan", null)));
    }

    [Fact]
    public void Long_cardholder_name_is_truncated_to_the_2c2p_limit()
    {
        var (gateway, _) = Create(_ => "");

        var xml = XElement.Parse(gateway.BuildMaintenanceRequest(new CardTokenRemovalRequest(Token, new string('A', 80), null)));

        Assert.Equal(50, xml.Element("cardholderName")!.Value.Length);
        Assert.Null(xml.Element("cardholderEmail"));
    }

    private sealed class StubHandler(Func<string, string> respond) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestUri = request.RequestUri;
            var body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(body), Encoding.UTF8, "text/plain") };
        }
    }
}

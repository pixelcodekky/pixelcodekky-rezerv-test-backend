using System.Buffers.Text;
using System.Text;
using Rezerv.Application.Common;
using Rezerv.Infrastructure.Payment._2C2P;

namespace Rezerv.Tests.Infrastructure;

public class Hs256JwtTests
{
    private const string Secret = "CD229682D3297390B9F66FF4020B758F4A5E625AF4992E5D75D311D6458B38E2";

    [Fact]
    public void Encode_then_decode_round_trips_the_claims()
    {
        var token = Hs256Jwt.Encode(new { invoiceNo = "INV1", amount = 25.00m }, Secret);

        var claims = Hs256Jwt.Decode(token, Secret);

        Assert.Equal("INV1", claims.GetProperty("invoiceNo").GetString());
        Assert.Equal(25.00m, claims.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public void Null_values_are_omitted()
    {
        var token = Hs256Jwt.Encode(new { invoiceNo = "INV1", customerToken = (string?)null }, Secret);

        Assert.False(Hs256Jwt.Decode(token, Secret).TryGetProperty("customerToken", out _));
    }

    [Fact]
    public void Wrong_secret_is_rejected()
    {
        var token = Hs256Jwt.Encode(new { invoiceNo = "INV1" }, Secret);

        Assert.Throws<InvalidSignatureException>(() => Hs256Jwt.Decode(token, "another-secret"));
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var parts = Hs256Jwt.Encode(new { invoiceNo = "INV1", amount = 25.00m }, Secret).Split('.');
        var forged = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"invoiceNo":"INV1","amount":0.01}"""));

        Assert.Throws<InvalidSignatureException>(() => Hs256Jwt.Decode($"{parts[0]}.{forged}.{parts[2]}", Secret));
    }

    [Fact]
    public void Alg_none_is_rejected()
    {
        var parts = Hs256Jwt.Encode(new { invoiceNo = "INV1" }, Secret).Split('.');
        var none = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));

        Assert.Throws<InvalidSignatureException>(() => Hs256Jwt.Decode($"{none}.{parts[1]}.", Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.b")]
    [InlineData("!!.@@.##")]
    public void Malformed_tokens_are_rejected(string? token)
    {
        Assert.Throws<InvalidSignatureException>(() => Hs256Jwt.Decode(token, Secret));
    }
}

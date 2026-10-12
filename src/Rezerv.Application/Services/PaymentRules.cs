using Rezerv.Domain.Entities;

namespace Rezerv.Application.Services;

/// <summary>What the Payment Token request asks 2C2P for, per checkout scenario.</summary>
/// <param name="PaymentChannels">null = no restriction: every method enabled for the merchant is shown.</param>
/// <param name="CustomerToken">Saved card to charge via Do Payment; null for the hosted page.</param>
/// <param name="CustomerTokenOnly">Restrict the payment to <paramref name="CustomerToken"/>.</param>
/// <param name="Request3DS">"N" for saved cards; null keeps the merchant default (hosted page handles 3DS itself).</param>
public sealed record PaymentTokenPlan(
    IReadOnlyList<string>? PaymentChannels,
    bool Tokenize,
    string? CustomerToken,
    bool CustomerTokenOnly = false,
    string? Request3DS = null);

/// <summary>
/// Payment rules (CLAUDE.md):
/// <list type="table">
/// <item><term>OneTime, new payment method</term><description>no paymentChannel restriction → hosted page with every available method.</description></item>
/// <item><term>Recurring, new payment method</term><description>paymentChannel ["CC"], tokenize true → hosted page with card only and the save-card option.</description></item>
/// <item><term>Saved card (any product)</term><description>paymentChannel ["CC"], customerToken [token], customerTokenOnly true,
/// request3DS "N" → Do Payment, then Payment Inquiry; no hosted page.</description></item>
/// </list>
/// Every request carries invoiceNo, amount, currencyCode "SGD", frontendReturnUrl and backendReturnUrl.
/// </summary>
public static class PaymentRules
{
    public const string Currency = "SGD";
    public const string CardChannel = "CC";

    private static readonly IReadOnlyList<string> CardOnly = [CardChannel];

    public static PaymentTokenPlan ForNewPaymentMethod(PaymentType paymentType)
    {
        return paymentType switch
        {
            PaymentType.Recurring => new PaymentTokenPlan(CardOnly, Tokenize: true, CustomerToken: null),
            PaymentType.OneTime => new PaymentTokenPlan(PaymentChannels: null, Tokenize: true, CustomerToken: null),
            _ => new PaymentTokenPlan(PaymentChannels: null, Tokenize: false, CustomerToken: null)
        };
    }

    public static PaymentTokenPlan ForSavedCard(CustomerCard card)
    { 
        return new(CardOnly, Tokenize: false, CustomerToken: card.CustomerToken, CustomerTokenOnly: true, Request3DS: "N");
    }
        
}

using Rezerv.Application.Services;
using Rezerv.Domain.Entities;

namespace Rezerv.Tests.Application;

public class PaymentRulesTests
{
    [Fact]
    public void OneTime_new_payment_method_has_no_channel_restriction()
    {
        var plan = PaymentRules.ForNewPaymentMethod(PaymentType.OneTime);

        Assert.Null(plan.PaymentChannels);
        Assert.Null(plan.CustomerToken);
        Assert.False(plan.CustomerTokenOnly);
        Assert.Null(plan.Request3DS);
    }

    [Fact]
    public void Recurring_new_payment_method_is_card_only_with_tokenize()
    {
        var plan = PaymentRules.ForNewPaymentMethod(PaymentType.Recurring);

        Assert.Equal(new[] { "CC" }, plan.PaymentChannels);
        Assert.True(plan.Tokenize);
        Assert.Null(plan.CustomerToken);
    }

    [Fact]
    public void Saved_card_is_card_only_with_customer_token_only_and_no_3ds()
    {
        var plan = PaymentRules.ForSavedCard(new CustomerCard { CustomerToken = "TKN" });

        Assert.Equal(new[] { "CC" }, plan.PaymentChannels);
        Assert.Equal("TKN", plan.CustomerToken);
        Assert.True(plan.CustomerTokenOnly);
        Assert.Equal("N", plan.Request3DS);
        Assert.False(plan.Tokenize);
    }

    [Fact]
    public void Currency_is_SGD()
    {
        Assert.Equal("SGD", PaymentRules.Currency);
    }
}

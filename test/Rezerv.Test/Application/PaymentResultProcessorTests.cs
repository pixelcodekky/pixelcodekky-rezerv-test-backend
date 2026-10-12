using Moq;
using Rezerv.Application.Common;
using Rezerv.Application.Services;
using Rezerv.Domain.Entities;
using Rezerv.Tests.TestDoubles;
using static Rezerv.Tests.TestDoubles.TestHarness;

namespace Rezerv.Tests.Application;

/// <summary>Backend notification handling (and Payment Inquiry results, which are applied the same way).</summary>
public class PaymentResultProcessorTests
{
    private readonly TestHarness _h = new();

    [Fact]
    public async Task Success_sets_status_and_saves_channel_account_tranRef_and_paidAt()
    {
        _h.SeedTransaction("INV1");

        var outcome = await _h.ResultProcessor().ApplyAsync(Result("INV1"));

        Assert.Equal(ApplyOutcome.Updated, outcome);
        var txn = _h.Db.Transaction("INV1");
        Assert.Equal(TransactionStatus.Succeeded, txn.Status);
        Assert.Equal("CC", txn.PaymentChannel);
        Assert.Equal("411111XXXXXX1111", txn.MaskedCardNo);
        Assert.Equal("TRAN-REF-1", txn.ProviderRef);
        Assert.Equal("0000", txn.RespCode);
        Assert.NotNull(txn.PaidAt);
    }

    [Fact]
    public async Task Other_respCode_sets_failed_with_respCode_and_respDesc()
    {
        _h.SeedTransaction("INV1");

        var outcome = await _h.ResultProcessor().ApplyAsync(Result("INV1", "4005", respDesc: "Do not honor"));

        Assert.Equal(ApplyOutcome.Updated, outcome);
        var txn = _h.Db.Transaction("INV1");
        Assert.Equal(TransactionStatus.Failed, txn.Status);
        Assert.Equal("4005", txn.RespCode);
        Assert.Equal("Do not honor", txn.RespDesc);
        Assert.Null(txn.PaidAt);
    }

    [Fact]
    public async Task Unknown_invoice_is_reported_not_found()
    {
        Assert.Equal(ApplyOutcome.NotFound, await _h.ResultProcessor().ApplyAsync(Result("NOPE")));
    }

    [Theory]
    [InlineData("24.99", "SGD")]
    [InlineData("25.00", "THB")]
    [InlineData(null, "SGD")]
    [InlineData("25.00", null)]
    public async Task Amount_or_currency_mismatch_is_rejected_and_nothing_changes(string? amount, string? currency)
    {
        _h.SeedTransaction("INV1", amount: 25.00m);

        var outcome = await _h.ResultProcessor().ApplyAsync(
            Result("INV1", amount: amount is null ? null : decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency: currency, customerToken: "NEW"));

        Assert.Equal(ApplyOutcome.Mismatch, outcome);
        Assert.Equal(TransactionStatus.Pending, _h.Db.Transaction("INV1").Status);
        Assert.Equal(2, _h.Db.Cards.Count);
    }

    [Fact]
    public async Task Currency_match_is_case_insensitive()
    {
        _h.SeedTransaction("INV1");

        Assert.Equal(ApplyOutcome.Updated, await _h.ResultProcessor().ApplyAsync(Result("INV1", currency: "sgd")));
    }

    [Fact]
    public async Task Pending_result_does_not_change_the_transaction()
    {
        _h.SeedTransaction("INV1");
        var pending = Result("INV1") with { Status = TransactionStatus.Pending, RespCode = "0001" };

        Assert.Equal(ApplyOutcome.StillPending, await _h.ResultProcessor().ApplyAsync(pending));
        Assert.Equal(TransactionStatus.Pending, _h.Db.Transaction("INV1").Status);
    }

    [Fact]
    public async Task CustomerToken_is_saved_as_a_new_card_for_the_customer()
    {
        _h.SeedTransaction("INV1", productId: RecurringProductId, amount: 9.90m);

        await _h.ResultProcessor().ApplyAsync(Result("INV1", amount: 9.90m, customerToken: "NEW-TOKEN"));

        var card = _h.Db.Cards.Single(c => c.CustomerToken == "NEW-TOKEN");
        Assert.Equal(CustomerId, card.CustomerId);
        Assert.Equal("411111XXXXXX1111", card.MaskedCardNo);
        Assert.Equal("CC", card.PaymentChannel);
    }

    [Fact]
    public async Task Saving_a_card_invalidates_the_cached_card_list()
    {
        _h.SeedTransaction("INV1");
        await _h.Cache.SetAsync(CacheKeys.CustomerCards(CustomerId), new List<string> { "stale" }, TimeSpan.FromMinutes(1));

        await _h.ResultProcessor().ApplyAsync(Result("INV1", customerToken: "NEW-TOKEN"));

        Assert.False(_h.Cache.Contains(CacheKeys.CustomerCards(CustomerId)));
    }

    [Fact]
    public async Task Existing_customerToken_is_not_saved_twice()
    {
        _h.SeedTransaction("INV1");

        await _h.ResultProcessor().ApplyAsync(Result("INV1", customerToken: CardToken));

        Assert.Single(_h.Db.Cards, c => c.CustomerToken == CardToken);
    }

    [Fact]
    public async Task No_card_is_saved_without_a_customerToken_or_on_failure()
    {
        _h.SeedTransaction("INV1");
        _h.SeedTransaction("INV2");

        await _h.ResultProcessor().ApplyAsync(Result("INV1"));
        await _h.ResultProcessor().ApplyAsync(Result("INV2", "4005", customerToken: "SHOULD-NOT-SAVE"));

        Assert.Equal(2, _h.Db.Cards.Count);
    }

    [Fact]
    public async Task Duplicate_notification_is_idempotent()
    {
        _h.SeedTransaction("INV1");
        var result = Result("INV1", customerToken: "NEW-TOKEN");

        var first = await _h.ResultProcessor().ApplyAsync(result);
        var paidAt = _h.Db.Transaction("INV1").PaidAt;
        var second = await _h.ResultProcessor().ApplyAsync(result);

        Assert.Equal(ApplyOutcome.Updated, first);
        Assert.Equal(ApplyOutcome.AlreadyFinal, second);
        Assert.Single(_h.Db.Cards, c => c.CustomerToken == "NEW-TOKEN");
        Assert.Equal(paidAt, _h.Db.Transaction("INV1").PaidAt);
    }

    [Theory]
    [InlineData(TransactionStatus.Succeeded, "4005")]
    [InlineData(TransactionStatus.Failed, "0000")]
    public async Task Final_status_is_never_overwritten(TransactionStatus stored, string incoming)
    {
        _h.SeedTransaction("INV1", status: stored);

        var outcome = await _h.ResultProcessor().ApplyAsync(Result("INV1", incoming));

        Assert.Equal(ApplyOutcome.AlreadyFinal, outcome);
        Assert.Equal(stored, _h.Db.Transaction("INV1").Status);
    }

    [Fact]
    public async Task Concurrent_duplicate_notifications_update_once_and_save_one_card()
    {
        _h.SeedTransaction("INV1");
        var result = Result("INV1", customerToken: "NEW-TOKEN");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => _h.ResultProcessor().ApplyAsync(result))));

        Assert.Single(outcomes, o => o == ApplyOutcome.Updated);
        Assert.Equal(9, outcomes.Count(o => o == ApplyOutcome.AlreadyFinal));
        Assert.Single(_h.Db.Cards, c => c.CustomerToken == "NEW-TOKEN");
    }

    // ---------- Payment Inquiry (ReconcileAsync) ----------

    [Fact]
    public async Task Reconcile_final_applies_a_failed_inquiry()
    {
        _h.SeedTransaction("INV1", cardId: CardId);
        _h.Gateway.Setup(g => g.InquireAsync("INV1", It.IsAny<CancellationToken>())).ReturnsAsync(Result("INV1", "4005"));

        var status = await _h.ResultProcessor().ReconcileAsync("INV1", ReconcileMode.Final);

        Assert.Equal(TransactionStatus.Failed, status);
        Assert.Equal(TransactionStatus.Failed, _h.Db.Transaction("INV1").Status);
    }

    [Fact]
    public async Task Reconcile_success_only_ignores_a_failed_inquiry_while_the_customer_may_still_be_paying()
    {
        _h.SeedTransaction("INV1");
        _h.Gateway.Setup(g => g.InquireAsync("INV1", It.IsAny<CancellationToken>())).ReturnsAsync(Result("INV1", "4005"));

        var status = await _h.ResultProcessor().ReconcileAsync("INV1", ReconcileMode.SuccessOnly);

        Assert.Equal(TransactionStatus.Pending, status);
        Assert.Equal(TransactionStatus.Pending, _h.Db.Transaction("INV1").Status);
    }

    [Fact]
    public async Task Reconcile_success_only_applies_a_succeeded_inquiry()
    {
        _h.SeedTransaction("INV1");
        _h.Gateway.Setup(g => g.InquireAsync("INV1", It.IsAny<CancellationToken>())).ReturnsAsync(Result("INV1"));

        Assert.Equal(TransactionStatus.Succeeded, await _h.ResultProcessor().ReconcileAsync("INV1", ReconcileMode.SuccessOnly));
    }

    [Fact]
    public async Task Reconcile_keeps_the_stored_status_when_the_inquiry_fails()
    {
        _h.SeedTransaction("INV1");
        _h.Gateway.Setup(g => g.InquireAsync("INV1", It.IsAny<CancellationToken>())).ThrowsAsync(new PaymentGatewayException("down"));

        Assert.Equal(TransactionStatus.Pending, await _h.ResultProcessor().ReconcileAsync("INV1", ReconcileMode.Final));
    }

    [Fact]
    public async Task Reconcile_does_not_trust_an_inquiry_with_a_different_amount()
    {
        _h.SeedTransaction("INV1", cardId: CardId);
        _h.Gateway.Setup(g => g.InquireAsync("INV1", It.IsAny<CancellationToken>())).ReturnsAsync(Result("INV1", amount: 1m));

        Assert.Equal(TransactionStatus.Pending, await _h.ResultProcessor().ReconcileAsync("INV1", ReconcileMode.Final));
    }

    // ---------- PaymentNotificationService ----------

    [Fact]
    public async Task Notification_service_parses_the_payload_with_the_gateway_and_applies_it()
    {
        _h.SeedTransaction("INV1");
        _h.Gateway.Setup(g => g.ParseNotification("jwt")).Returns(Result("INV1"));

        var outcome = await _h.NotificationService().HandleAsync("jwt");

        Assert.Equal(ApplyOutcome.Updated, outcome);
        Assert.Equal(TransactionStatus.Succeeded, _h.Db.Transaction("INV1").Status);
    }

    [Fact]
    public async Task Notification_with_a_bad_signature_is_rejected_and_nothing_changes()
    {
        _h.SeedTransaction("INV1");
        _h.Gateway.Setup(g => g.ParseNotification("forged")).Throws(new InvalidSignatureException("bad"));

        await Assert.ThrowsAsync<InvalidSignatureException>(() => _h.NotificationService().HandleAsync("forged"));
        Assert.Equal(TransactionStatus.Pending, _h.Db.Transaction("INV1").Status);
    }
}

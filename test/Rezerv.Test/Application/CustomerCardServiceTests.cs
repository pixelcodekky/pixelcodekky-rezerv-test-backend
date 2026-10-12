using Moq;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Application.Services;
using Rezerv.Domain.Entities;
using Rezerv.Tests.TestDoubles;
using static Rezerv.Tests.TestDoubles.TestHarness;

namespace Rezerv.Tests.Application;

public class CustomerCardServiceTests
{
    private readonly TestHarness _h = new();

    private void RemovalAnswers(bool removed, string respCode = "00", string respReason = "Success") =>
        _h.ActionGateway.Setup(g => g.RemoveCardTokenAsync(It.IsAny<CardTokenRemovalRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CardTokenRemovalResult(removed, respCode, respReason));

    private bool CardExists(int id) => _h.Db.Read(d => d.Cards.Any(c => c.Id == id));

    private void VerifyNoRemovalAtTwoC2P() =>
        _h.ActionGateway.Verify(g => g.RemoveCardTokenAsync(It.IsAny<CardTokenRemovalRequest>(), It.IsAny<CancellationToken>()), Times.Never);

    // ---------- Happy path ----------

    [Fact]
    public async Task Card_is_removed_at_2c2p_with_its_token_then_from_the_database()
    {
        RemovalAnswers(removed: true);

        await _h.CustomerCardService().RemoveAsync(CustomerId, CardId);

        _h.ActionGateway.Verify(g => g.RemoveCardTokenAsync(
            It.Is<CardTokenRemovalRequest>(r => r.CustomerToken == CardToken && r.CardholderName == "Alice Tan" && r.CardholderEmail == "alice@example.com"),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(CardExists(CardId));
        Assert.True(CardExists(OtherCustomersCardId));
    }

    [Fact]
    public async Task Removing_a_card_invalidates_the_cached_card_list()
    {
        RemovalAnswers(removed: true);
        Assert.Single(await _h.CatalogService().GetCustomerCardsAsync(CustomerId));
        Assert.True(_h.Cache.Contains(CacheKeys.CustomerCards(CustomerId)));

        await _h.CustomerCardService().RemoveAsync(CustomerId, CardId);

        Assert.False(_h.Cache.Contains(CacheKeys.CustomerCards(CustomerId)));
        Assert.Empty(await _h.CatalogService().GetCustomerCardsAsync(CustomerId));
    }

    [Fact]
    public async Task Past_payments_keep_their_masked_card_after_the_card_is_removed()
    {
        RemovalAnswers(removed: true);
        var paid = _h.SeedTransaction("INV1", TransactionStatus.Succeeded, cardId: CardId);
        _h.Db.Read(d => d.Transactions.Single(t => t.Id == paid.Id).MaskedCardNo = "411111XXXXXX1111");

        await _h.CustomerCardService().RemoveAsync(CustomerId, CardId);

        var txn = _h.Db.Transaction("INV1");
        Assert.Null(txn.CustomerCardId);
        Assert.Equal("411111XXXXXX1111", txn.MaskedCardNo);
        Assert.Equal(TransactionStatus.Succeeded, txn.Status);
    }

    [Fact]
    public async Task Removed_card_can_no_longer_be_used_for_checkout()
    {
        RemovalAnswers(removed: true);
        await _h.CustomerCardService().RemoveAsync(CustomerId, CardId);

        await Assert.ThrowsAsync<NotFoundException>(() => _h.CheckoutService().CheckoutAsync(
            new CheckoutCommand(Guid.NewGuid().ToString(), CustomerId, OneTimeProductId, CardId, "127.0.0.1")));
    }

    // ---------- Ownership ----------

    [Fact]
    public async Task Unknown_customer_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.CustomerCardService().RemoveAsync(999, CardId));
        VerifyNoRemovalAtTwoC2P();
    }

    [Fact]
    public async Task Card_of_another_customer_is_not_found_and_is_kept()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.CustomerCardService().RemoveAsync(CustomerId, OtherCustomersCardId));

        VerifyNoRemovalAtTwoC2P();
        Assert.True(CardExists(OtherCustomersCardId));
    }

    [Fact]
    public async Task Already_removed_card_is_not_found_and_2c2p_is_not_called_again()
    {
        RemovalAnswers(removed: true);
        await _h.CustomerCardService().RemoveAsync(CustomerId, CardId);

        await Assert.ThrowsAsync<NotFoundException>(() => _h.CustomerCardService().RemoveAsync(CustomerId, CardId));
        _h.ActionGateway.Verify(g => g.RemoveCardTokenAsync(It.IsAny<CardTokenRemovalRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------- Safety ----------

    [Fact]
    public async Task Card_with_a_pending_payment_is_not_removed()
    {
        _h.SeedTransaction("INV1", TransactionStatus.Pending, cardId: CardId);

        await Assert.ThrowsAsync<ConflictException>(() => _h.CustomerCardService().RemoveAsync(CustomerId, CardId));

        VerifyNoRemovalAtTwoC2P();
        Assert.True(CardExists(CardId));
    }

    [Fact]
    public async Task Card_being_removed_by_another_request_is_a_conflict()
    {
        await using var held = await _h.Cache.AcquireLockAsync(CacheKeys.CardRemovalLock(CardId), TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<ConflictException>(() => _h.CustomerCardService().RemoveAsync(CustomerId, CardId));

        VerifyNoRemovalAtTwoC2P();
        Assert.True(CardExists(CardId));
    }

    [Fact]
    public async Task Concurrent_removals_call_2c2p_once()
    {
        var release = new TaskCompletionSource<CardTokenRemovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _h.ActionGateway.Setup(g => g.RemoveCardTokenAsync(It.IsAny<CardTokenRemovalRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => { Interlocked.Increment(ref calls); return release.Task; });

        var first = _h.CustomerCardService().RemoveAsync(CustomerId, CardId);
        var second = await Record.ExceptionAsync(() => _h.CustomerCardService().RemoveAsync(CustomerId, CardId));
        release.SetResult(new CardTokenRemovalResult(true, "00", "Success"));
        await first;

        Assert.IsType<ConflictException>(second);
        Assert.Equal(1, calls);
        Assert.False(CardExists(CardId));
    }

    [Fact]
    public async Task Card_is_kept_when_2c2p_refuses_the_removal()
    {
        RemovalAnswers(removed: false, respCode: "99", respReason: "Card not found");

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => _h.CustomerCardService().RemoveAsync(CustomerId, CardId));

        Assert.Contains("Card not found", ex.Message);
        Assert.True(CardExists(CardId));
    }

    [Fact]
    public async Task Card_is_kept_when_2c2p_cannot_be_reached()
    {
        _h.ActionGateway.Setup(g => g.RemoveCardTokenAsync(It.IsAny<CardTokenRemovalRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PaymentGatewayException("timed out"));

        await Assert.ThrowsAsync<PaymentGatewayException>(() => _h.CustomerCardService().RemoveAsync(CustomerId, CardId));

        Assert.True(CardExists(CardId));
    }

    [Fact]
    public async Task Removal_requires_payment_action_configuration()
    {
        _h.ActionGateway.SetupGet(g => g.IsConfigured).Returns(false);

        await Assert.ThrowsAsync<NotConfiguredException>(() => _h.CustomerCardService().RemoveAsync(CustomerId, CardId));

        VerifyNoRemovalAtTwoC2P();
        Assert.True(CardExists(CardId));
    }

    [Fact]
    public async Task Lock_is_released_after_a_failed_removal_so_it_can_be_retried()
    {
        RemovalAnswers(removed: false, respCode: "99");
        await Assert.ThrowsAsync<PaymentGatewayException>(() => _h.CustomerCardService().RemoveAsync(CustomerId, CardId));
        Assert.False(_h.Cache.IsLocked(CacheKeys.CardRemovalLock(CardId)));

        RemovalAnswers(removed: true);
        await _h.CustomerCardService().RemoveAsync(CustomerId, CardId);

        Assert.False(CardExists(CardId));
    }
}

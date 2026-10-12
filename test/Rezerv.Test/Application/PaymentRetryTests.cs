using Moq;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Application.Contracts;
using Rezerv.Application.Services;
using Rezerv.Domain.Entities;
using Rezerv.Tests.TestDoubles;
using static Rezerv.Tests.TestDoubles.TestHarness;

namespace Rezerv.Tests.Application;

/// <summary>POST /api/transactions/{invoiceNo}/retry: a failed payment is retried once, as a new linked checkout.</summary>
public class PaymentRetryTests
{
    private readonly TestHarness _h = new();

    private static RetryCommand Retry(string invoiceNo = "INV1", string? key = null, int? cardId = null) =>
        new(key ?? Guid.NewGuid().ToString(), invoiceNo, cardId, "203.0.113.7");

    private void SetupHostedPage(string url = "https://pay/retry") =>
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenOk(url));

    private void VerifyPaymentTokenCalls(Func<Times> times) =>
        _h.Gateway.Verify(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()), times);

    private List<PaymentTransaction> Retries() => _h.Db.Read(db => db.Transactions.Where(t => t.InvoiceNo != "INV1").ToList());

    // ---------- Happy path ----------

    [Fact]
    public async Task Failed_payment_is_retried_as_a_new_transaction_linked_to_the_failed_one()
    {
        var original = _h.SeedTransaction("INV1", status: TransactionStatus.Failed, respDesc: "Declined");
        PaymentTokenRequest? sent = null;
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentTokenRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(TokenOk("https://pay/retry"));

        var response = await _h.CheckoutService().RetryAsync(Retry());

        Assert.NotEqual("INV1", response.InvoiceNo);
        Assert.Equal((TransactionStatus.Pending, "https://pay/retry"), (response.Status, response.RedirectUrl));

        var retry = _h.Db.Transaction(response.InvoiceNo);
        Assert.Equal(original.Id, retry.RetryOfTransactionId);
        Assert.Equal((CustomerId, OneTimeProductId, 25.00m, "SGD"), (retry.CustomerId, retry.ProductId, retry.Amount, retry.Currency));
        Assert.Equal(response.InvoiceNo, sent!.InvoiceNo);

        // The failed transaction itself is never changed.
        var failed = _h.Db.Transaction("INV1");
        Assert.Equal((TransactionStatus.Failed, "Declined"), (failed.Status, failed.RespDesc));
    }

    [Fact]
    public async Task Retry_without_a_card_uses_the_hosted_page_even_if_the_failed_payment_used_a_saved_card()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed, cardId: CardId);
        SetupHostedPage();

        var response = await _h.CheckoutService().RetryAsync(Retry());

        Assert.Equal("https://pay/retry", response.RedirectUrl);
        Assert.Null(_h.Db.Transaction(response.InvoiceNo).CustomerCardId);
        _h.Gateway.Verify(g => g.DoPaymentAsync(It.IsAny<DoPaymentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Retry_with_a_saved_card_pays_with_do_payment()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed);
        PaymentTokenRequest? token = null;
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentTokenRequest, CancellationToken>((r, _) => token = r)
            .ReturnsAsync(new PaymentTokenResult(true, "PAYMENT-TOKEN-1", null, "0000", "Success"));
        _h.Gateway.Setup(g => g.DoPaymentAsync(It.IsAny<DoPaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DoPaymentResult(DoPaymentOutcome.Redirect, "1001", "Redirect to 3DS", "https://3ds/acs"));

        var response = await _h.CheckoutService().RetryAsync(Retry(cardId: CardId));

        Assert.Equal("https://3ds/acs", response.RedirectUrl);
        Assert.Equal(CardToken, token!.CustomerToken);
        Assert.Equal(CardId, _h.Db.Transaction(response.InvoiceNo).CustomerCardId);
    }

    [Fact]
    public async Task Transaction_details_link_the_failed_payment_and_its_retry()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed);
        SetupHostedPage();
        var before = await _h.TransactionQueryService().GetAsync("INV1");

        var response = await _h.CheckoutService().RetryAsync(Retry());

        Assert.True(before.CanRetry);
        Assert.Null(before.RetryInvoiceNo);
        var failed = await _h.TransactionQueryService().GetAsync("INV1");
        Assert.Equal(response.InvoiceNo, failed.RetryInvoiceNo);
        Assert.False(failed.CanRetry);
        var retry = Assert.Single(await _h.TransactionQueryService().GetByCustomerAsync(CustomerId), t => t.InvoiceNo == response.InvoiceNo);
        Assert.Equal("INV1", retry.RetryOfInvoiceNo);
        Assert.False(retry.CanRetry); // still Pending
    }

    [Fact]
    public async Task A_retry_that_failed_too_can_itself_be_retried()
    {
        var first = _h.SeedTransaction("INV1", status: TransactionStatus.Failed);
        _h.SeedTransaction("INV2", status: TransactionStatus.Failed, retryOf: first.Id);
        SetupHostedPage();

        var response = await _h.CheckoutService().RetryAsync(Retry("INV2"));

        Assert.Equal(_h.Db.Transaction("INV2").Id, _h.Db.Transaction(response.InvoiceNo).RetryOfTransactionId);
        Assert.True((await _h.TransactionQueryService().GetAsync("INV2")).RetryOfInvoiceNo == "INV1");
    }

    // ---------- Idempotency and double charges ----------

    [Fact]
    public async Task Same_idempotency_key_returns_the_same_retry_and_calls_2c2p_once()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed);
        SetupHostedPage();
        var command = Retry();

        var first = await _h.CheckoutService().RetryAsync(command);
        var second = await _h.CheckoutService().RetryAsync(command);

        Assert.Equal(first, second);
        Assert.Single(Retries());
        VerifyPaymentTokenCalls(Times.Once);
    }

    [Fact]
    public async Task Same_key_still_replays_after_the_cached_response_expired()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed);
        SetupHostedPage();
        var command = Retry();
        var first = await _h.CheckoutService().RetryAsync(command);
        await _h.Cache.RemoveAsync(CacheKeys.CheckoutResponse(command.IdempotencyKey!));

        var second = await _h.CheckoutService().RetryAsync(command);

        Assert.Equal(first.InvoiceNo, second.InvoiceNo);
        VerifyPaymentTokenCalls(Times.Once);
    }

    [Fact]
    public async Task A_second_retry_with_another_key_is_a_conflict_that_names_the_first_retry()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed);
        SetupHostedPage();
        var first = await _h.CheckoutService().RetryAsync(Retry());

        var ex = await Assert.ThrowsAsync<ConflictException>(() => _h.CheckoutService().RetryAsync(Retry()));

        Assert.Contains(first.InvoiceNo, ex.Message);
        Assert.Single(Retries());
        VerifyPaymentTokenCalls(Times.Once);
    }

    [Fact]
    public async Task Concurrent_retries_with_different_keys_create_one_transaction_and_call_2c2p_once()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed);
        var gate = new TaskCompletionSource();
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await gate.Task; // hold 2C2P until every request has had its chance to insert
                return TokenOk();
            });

        const int count = 8;
        var requests = Enumerable.Range(0, count)
            .Select(_ => Task.Run(async () =>
            {
                try { return (object)await _h.CheckoutService().RetryAsync(Retry()); }
                catch (ConflictException ex) { return ex; }
            }))
            .ToList();
        // Every request but the one holding 2C2P has been turned away.
        await WaitUntil(() => requests.Count(t => t.IsCompleted) == count - 1);
        gate.SetResult();
        var outcomes = await Task.WhenAll(requests);

        Assert.Single(Retries());
        Assert.Single(outcomes, o => o is CheckoutResponse);
        Assert.Equal(count - 1, outcomes.Count(o => o is ConflictException));
        VerifyPaymentTokenCalls(Times.Once);
    }

    [Fact]
    public async Task Key_used_for_a_checkout_cannot_be_reused_for_a_retry()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed);
        SetupHostedPage();
        var key = Guid.NewGuid().ToString();
        await _h.CheckoutService().CheckoutAsync(new CheckoutCommand(key, CustomerId, OneTimeProductId, null, "1.1.1.1"));

        await Assert.ThrowsAsync<IdempotencyKeyMismatchException>(() => _h.CheckoutService().RetryAsync(Retry(key: key)));
    }

    // ---------- Rejected retries ----------

    [Theory]
    [InlineData(TransactionStatus.Pending)]
    [InlineData(TransactionStatus.Succeeded)]
    public async Task Only_failed_transactions_can_be_retried(TransactionStatus status)
    {
        _h.SeedTransaction("INV1", status: status);

        await Assert.ThrowsAsync<ValidationException>(() => _h.CheckoutService().RetryAsync(Retry()));

        Assert.Empty(Retries());
        _h.Gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Unknown_transaction_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.CheckoutService().RetryAsync(Retry("NOPE")));
        Assert.Empty(_h.Db.Transactions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    public async Task Retry_needs_a_uuid_idempotency_key(string? key)
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _h.CheckoutService().RetryAsync(new RetryCommand(key, "INV1", null, "1.1.1.1")));
        Assert.Empty(Retries());
    }

    [Theory]
    [InlineData(OtherCustomersCardId)]
    [InlineData(9999)]
    public async Task Saved_card_must_belong_to_the_customer_of_the_failed_payment(int cardId)
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed);

        await Assert.ThrowsAsync<NotFoundException>(() => _h.CheckoutService().RetryAsync(Retry(cardId: cardId)));

        Assert.Empty(Retries());
        _h.Gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Product_that_is_no_longer_sold_cannot_be_retried()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed, productId: InactiveProductId);

        await Assert.ThrowsAsync<NotFoundException>(() => _h.CheckoutService().RetryAsync(Retry()));
        Assert.Empty(Retries());
    }

    [Fact]
    public async Task A_rejected_retry_does_not_use_up_the_single_retry()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed);
        await Assert.ThrowsAsync<NotFoundException>(() => _h.CheckoutService().RetryAsync(Retry(cardId: 9999)));
        SetupHostedPage();

        var response = await _h.CheckoutService().RetryAsync(Retry());

        Assert.Equal("https://pay/retry", response.RedirectUrl);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not reached.");
            await Task.Delay(10);
        }
    }
}

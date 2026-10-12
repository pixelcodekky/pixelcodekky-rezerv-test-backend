using Moq;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Application.Services;
using Rezerv.Domain.Entities;
using Rezerv.Tests.TestDoubles;
using static Rezerv.Tests.TestDoubles.TestHarness;

namespace Rezerv.Tests.Application;

public class CheckoutServiceTests
{
    private readonly TestHarness _h = new();

    private static CheckoutCommand Command(string? key = null, int customerId = CustomerId, int productId = OneTimeProductId, int? cardId = null) =>
        new(key ?? Guid.NewGuid().ToString(), customerId, productId, cardId, "203.0.113.7");

    // ---------- Idempotency-Key validation ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uuid")]
    public async Task Rejects_missing_or_non_uuid_idempotency_key(string? key)
    {
        var command = new CheckoutCommand(key, CustomerId, OneTimeProductId, null, "1.1.1.1");

        await Assert.ThrowsAsync<ValidationException>(() => _h.CheckoutService().CheckoutAsync(command));
        Assert.Empty(_h.Db.Transactions);
    }

    [Fact]
    public void NormalizeKey_accepts_any_uuid_format_and_returns_canonical_form()
    {
        var guid = Guid.NewGuid();

        Assert.Equal(guid.ToString("D"), CheckoutService.NormalizeKey(guid.ToString("N")));
        Assert.Equal(guid.ToString("D"), CheckoutService.NormalizeKey(guid.ToString("B").ToUpperInvariant()));
    }

    [Fact]
    public void NewInvoiceNo_is_alphanumeric_unique_and_at_most_50_chars()
    {
        var now = new DateTime(2026, 10, 9, 15, 30, 12, 345, DateTimeKind.Utc);

        var a = CheckoutService.NewInvoiceNo(now);
        var b = CheckoutService.NewInvoiceNo(now);

        Assert.StartsWith("INV20261009153012345", a);
        Assert.True(a.Length <= 50);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        Assert.NotEqual(a, b);
    }

    // ---------- Validation of the request ----------

    [Fact]
    public async Task Unknown_customer_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.CheckoutService().CheckoutAsync(Command(customerId: 999)));
        Assert.Empty(_h.Db.Transactions);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(InactiveProductId)]
    public async Task Unknown_or_inactive_product_is_not_found(int productId)
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.CheckoutService().CheckoutAsync(Command(productId: productId)));
        Assert.Empty(_h.Db.Transactions);
    }

    [Fact]
    public async Task Product_not_priced_in_SGD_is_rejected()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _h.CheckoutService().CheckoutAsync(Command(productId: UsdProductId)));
    }

    [Theory]
    [InlineData(OtherCustomersCardId)]
    [InlineData(9999)]
    public async Task Saved_card_must_belong_to_the_customer(int cardId)
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.CheckoutService().CheckoutAsync(Command(cardId: cardId)));

        Assert.Empty(_h.Db.Transactions);
        _h.Gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Validation_failure_releases_the_lock_so_the_key_can_be_retried()
    {
        var key = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<NotFoundException>(() => _h.CheckoutService().CheckoutAsync(Command(key, customerId: 999)));

        Assert.False(_h.Cache.IsLocked(CacheKeys.CheckoutLock(key)));
    }

    // ---------- Hosted page flow ----------

    [Fact]
    public async Task OneTime_new_payment_method_creates_pending_transaction_and_returns_hosted_page_without_channel_restriction()
    {
        PaymentTokenRequest? sent = null;
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentTokenRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(TokenOk("https://pay/onetime"));

        var response = await _h.CheckoutService().CheckoutAsync(Command());

        Assert.Equal(TransactionStatus.Pending, response.Status);
        Assert.Equal("https://pay/onetime", response.RedirectUrl);

        var txn = _h.Db.SingleTransaction();
        Assert.Equal(response.InvoiceNo, txn.InvoiceNo);
        Assert.Equal(TransactionStatus.Pending, txn.Status);
        Assert.Equal(25.00m, txn.Amount);
        Assert.Equal("SGD", txn.Currency);
        Assert.Equal("PAYMENT-TOKEN-1", txn.PaymentToken);
        Assert.Equal("https://pay/onetime", txn.RedirectUrl);
        Assert.Null(txn.CustomerCardId);

        Assert.NotNull(sent);
        Assert.Equal(txn.InvoiceNo, sent!.InvoiceNo);
        Assert.Equal(25.00m, sent.Amount);
        Assert.Equal("SGD", sent.CurrencyCode);
        Assert.Equal("Concert ticket", sent.Description);
        Assert.Null(sent.PaymentChannels);
        Assert.Null(sent.CustomerToken);
        Assert.False(sent.CustomerTokenOnly);
        Assert.Equal("Alice Tan", sent.CustomerName);
        Assert.Equal("alice@example.com", sent.CustomerEmail);

        _h.Gateway.Verify(g => g.DoPaymentAsync(It.IsAny<DoPaymentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Recurring_new_payment_method_requests_card_only_with_tokenize()
    {
        PaymentTokenRequest? sent = null;
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentTokenRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(TokenOk());

        await _h.CheckoutService().CheckoutAsync(Command(productId: RecurringProductId));

        Assert.Equal(["CC"], sent!.PaymentChannels);
        Assert.True(sent.Tokenize);
        Assert.Null(sent.CustomerToken);
        Assert.Equal(9.90m, sent.Amount);
    }

    [Fact]
    public async Task Payment_token_error_marks_transaction_failed_with_respCode_and_respDesc()
    {
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentTokenResult(false, null, null, "9015", "Merchant not found"));

        var response = await _h.CheckoutService().CheckoutAsync(Command());

        Assert.Equal(TransactionStatus.Failed, response.Status);
        Assert.Null(response.RedirectUrl);
        var txn = _h.Db.SingleTransaction();
        Assert.Equal(TransactionStatus.Failed, txn.Status);
        Assert.Equal("9015", txn.RespCode);
        Assert.Equal("Merchant not found", txn.RespDesc);
    }

    [Fact]
    public async Task Payment_token_transport_error_marks_transaction_failed_with_gateway_error()
    {
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PaymentGatewayException("timeout"));

        var response = await _h.CheckoutService().CheckoutAsync(Command());

        Assert.Equal(TransactionStatus.Failed, response.Status);
        Assert.Equal(CheckoutService.GatewayErrorCode, _h.Db.SingleTransaction().RespCode);
    }

    // ---------- Saved-card flow ----------

    private void SetupSavedCardToken(Action<PaymentTokenRequest>? capture = null) =>
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentTokenRequest, CancellationToken>((r, _) => capture?.Invoke(r))
            .ReturnsAsync(new PaymentTokenResult(true, "PAYMENT-TOKEN-1", null, "0000", "Success"));

    [Fact]
    public async Task Saved_card_requests_token_with_customerToken_only_and_no_3ds_then_calls_do_payment()
    {
        PaymentTokenRequest? tokenRequest = null;
        DoPaymentRequest? doPayment = null;
        SetupSavedCardToken(r => tokenRequest = r);
        _h.Gateway.Setup(g => g.DoPaymentAsync(It.IsAny<DoPaymentRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DoPaymentRequest, CancellationToken>((r, _) => doPayment = r)
            .ReturnsAsync(new DoPaymentResult(DoPaymentOutcome.Redirect, "1001", "Redirect to 3DS", "https://3ds/acs"));

        var response = await _h.CheckoutService().CheckoutAsync(Command(cardId: CardId));

        Assert.Equal(["CC"], tokenRequest!.PaymentChannels);
        Assert.Equal(CardToken, tokenRequest.CustomerToken);
        Assert.True(tokenRequest.CustomerTokenOnly);
        Assert.Equal("N", tokenRequest.Request3DS);
        Assert.False(tokenRequest.Tokenize);

        Assert.Equal("PAYMENT-TOKEN-1", doPayment!.PaymentToken);
        Assert.Equal(CardToken, doPayment.CustomerToken);
        Assert.Equal("203.0.113.7", doPayment.ClientIp);
        Assert.Equal(response.InvoiceNo, doPayment.InvoiceNo);

        // 1001: send the browser to the 3DS URL; no inquiry yet.
        Assert.Equal(TransactionStatus.Pending, response.Status);
        Assert.Equal("https://3ds/acs", response.RedirectUrl);
        Assert.Equal(CardId, _h.Db.SingleTransaction().CustomerCardId);
        _h.Gateway.Verify(g => g.InquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Saved_card_2000_is_confirmed_by_payment_inquiry_0000_as_succeeded()
    {
        SetupSavedCardToken();
        _h.Gateway.Setup(g => g.DoPaymentAsync(It.IsAny<DoPaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DoPaymentResult(DoPaymentOutcome.Completed, "2000", "Transaction is completed", null));
        _h.Gateway.Setup(g => g.InquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string invoiceNo, CancellationToken _) => Result(invoiceNo));

        var response = await _h.CheckoutService().CheckoutAsync(Command(cardId: CardId));

        Assert.Equal(TransactionStatus.Succeeded, response.Status);
        Assert.Null(response.RedirectUrl);
        var txn = _h.Db.SingleTransaction();
        Assert.Equal(TransactionStatus.Succeeded, txn.Status);
        Assert.Equal("TRAN-REF-1", txn.ProviderRef);
        Assert.Equal("411111XXXXXX1111", txn.MaskedCardNo);
        Assert.NotNull(txn.PaidAt);
    }

    [Fact]
    public async Task Saved_card_2000_alone_is_not_success_when_inquiry_says_otherwise()
    {
        SetupSavedCardToken();
        _h.Gateway.Setup(g => g.DoPaymentAsync(It.IsAny<DoPaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DoPaymentResult(DoPaymentOutcome.Completed, "2000", "Transaction is completed", null));
        _h.Gateway.Setup(g => g.InquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string invoiceNo, CancellationToken _) => Result(invoiceNo, "4005", respDesc: "Do not honor"));

        var response = await _h.CheckoutService().CheckoutAsync(Command(cardId: CardId));

        Assert.Equal(TransactionStatus.Failed, response.Status);
        var txn = _h.Db.SingleTransaction();
        Assert.Equal("4005", txn.RespCode);
        Assert.Equal("Do not honor", txn.RespDesc);
    }

    [Fact]
    public async Task Saved_card_do_payment_without_response_stays_pending_and_is_not_failed()
    {
        SetupSavedCardToken();
        _h.Gateway.Setup(g => g.DoPaymentAsync(It.IsAny<DoPaymentRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PaymentGatewayException("timeout"));

        var response = await _h.CheckoutService().CheckoutAsync(Command(cardId: CardId));

        Assert.Equal(TransactionStatus.Pending, response.Status);
        Assert.Null(response.RedirectUrl);
        Assert.Equal(CheckoutService.AwaitingResultCode, _h.Db.SingleTransaction().RespCode);
    }

    [Fact]
    public async Task Saved_card_payment_token_failure_never_calls_do_payment()
    {
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentTokenResult(false, null, null, "4200", "Tokenization failed"));

        var response = await _h.CheckoutService().CheckoutAsync(Command(cardId: CardId));

        Assert.Equal(TransactionStatus.Failed, response.Status);
        _h.Gateway.Verify(g => g.DoPaymentAsync(It.IsAny<DoPaymentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- Idempotency and locking ----------

    [Fact]
    public async Task Same_key_returns_the_original_response_without_calling_2c2p_again()
    {
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenOk());
        var command = Command();

        var first = await _h.CheckoutService().CheckoutAsync(command);
        var second = await _h.CheckoutService().CheckoutAsync(command);

        Assert.Equal(first, second);
        Assert.Single(_h.Db.Transactions);
        _h.Gateway.Verify(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Same_key_is_replayed_from_mysql_when_the_redis_response_is_gone()
    {
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenOk("https://pay/1"));
        var key = Guid.NewGuid().ToString();

        var first = await _h.CheckoutService().CheckoutAsync(Command(key));
        await _h.Cache.RemoveAsync(CacheKeys.CheckoutResponse(key));
        var second = await _h.CheckoutService().CheckoutAsync(Command(key));

        Assert.Equal(first.InvoiceNo, second.InvoiceNo);
        Assert.Equal("https://pay/1", second.RedirectUrl);
        _h.Gateway.Verify(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Same_key_in_a_different_uuid_format_is_the_same_key()
    {
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenOk());
        var guid = Guid.NewGuid();

        var first = await _h.CheckoutService().CheckoutAsync(Command(guid.ToString("D")));
        var second = await _h.CheckoutService().CheckoutAsync(Command(guid.ToString("N").ToUpperInvariant()));

        Assert.Equal(first.InvoiceNo, second.InvoiceNo);
        Assert.Single(_h.Db.Transactions);
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_rejected()
    {
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenOk());
        var key = Guid.NewGuid().ToString();
        await _h.CheckoutService().CheckoutAsync(Command(key));

        await Assert.ThrowsAsync<IdempotencyKeyMismatchException>(() =>
            _h.CheckoutService().CheckoutAsync(Command(key, productId: RecurringProductId)));
        Assert.Single(_h.Db.Transactions);
    }

    [Fact]
    public async Task Failed_checkout_is_replayed_too_and_not_retried_with_the_same_key()
    {
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentTokenResult(false, null, null, "9015", "Error"));
        var command = Command();

        var first = await _h.CheckoutService().CheckoutAsync(command);
        var second = await _h.CheckoutService().CheckoutAsync(command);

        Assert.Equal(TransactionStatus.Failed, second.Status);
        Assert.Equal(first.InvoiceNo, second.InvoiceNo);
        _h.Gateway.Verify(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Different_keys_create_separate_transactions()
    {
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenOk());

        var a = await _h.CheckoutService().CheckoutAsync(Command());
        var b = await _h.CheckoutService().CheckoutAsync(Command());

        Assert.NotEqual(a.InvoiceNo, b.InvoiceNo);
        Assert.Equal(2, _h.Db.Transactions.Count);
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_create_one_transaction_and_call_2c2p_once()
    {
        var gate = new TaskCompletionSource();
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await gate.Task; // hold 2C2P until every request has started
                return TokenOk();
            });
        var command = Command();
        int maxcount = 10;
        var requests = Enumerable.Range(0, maxcount)
            .Select(_ => Task.Run(() => _h.CheckoutService().CheckoutAsync(command)))
            .ToList();
        await WaitUntil(() => _h.Cache.LockAttempts >= maxcount);
        gate.SetResult();

        var responses = await Task.WhenAll(requests);

        Assert.Single(_h.Db.Transactions);
        Assert.Single(responses.Select(r => r.InvoiceNo).Distinct());
        Assert.All(responses, r => Assert.Equal(TokenOk().WebPaymentUrl, r.RedirectUrl));
        _h.Gateway.Verify(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Waiting_request_gets_409_when_the_first_is_still_in_progress_after_the_wait_timeout()
    {
        _h.Options.WaitTimeout = TimeSpan.FromMilliseconds(100);
        var gate = new TaskCompletionSource();
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await gate.Task;
                return TokenOk();
            });
        var command = Command();

        var first = Task.Run(() => _h.CheckoutService().CheckoutAsync(command));
        await WaitUntil(() => _h.Db.Read(db => db.Transactions.Count) == 1);

        await Assert.ThrowsAsync<ConflictException>(() => _h.CheckoutService().CheckoutAsync(command));

        gate.SetResult();
        var original = await first;
        var replay = await _h.CheckoutService().CheckoutAsync(command);
        Assert.Equal(original.InvoiceNo, replay.InvoiceNo);
        _h.Gateway.Verify(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unique_index_is_the_final_guard_when_redis_is_down()
    {
        _h.Cache.Unavailable = true;
        var gate = new TaskCompletionSource();
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await gate.Task;
                return TokenOk();
            });
        var command = Command();

        var requests = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                try { return (object)await _h.CheckoutService().CheckoutAsync(command); }
                catch (ConflictException ex) { return ex; }
            }))
            .ToList();
        await WaitUntil(() => _h.Db.Read(db => db.Transactions.Count) == 1 && _h.Cache.LockAttempts >= 1);
        await Task.Delay(100);
        gate.SetResult();
        var outcomes = await Task.WhenAll(requests);

        Assert.Single(_h.Db.Transactions);
        _h.Gateway.Verify(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        // The others either answered 409 (still in progress) or the same invoice.
        var invoice = _h.Db.SingleTransaction().InvoiceNo;
        Assert.All(outcomes, o => Assert.True(o is ConflictException || (o is Rezerv.Application.Contracts.CheckoutResponse r && r.InvoiceNo == invoice)));
    }

    [Fact]
    public async Task Orphaned_pending_transaction_past_the_lock_ttl_is_replayed_instead_of_charging_again()
    {
        var key = Guid.NewGuid().ToString();
        _h.Db.Seed(new PaymentTransaction
        {
            CustomerId = CustomerId, ProductId = OneTimeProductId, InvoiceNo = "INVORPHAN", IdempotencyKey = key,
            Amount = 25m, Currency = "SGD", Status = TransactionStatus.Pending,
            CreatedAt = DateTime.UtcNow - _h.Options.LockTtl - TimeSpan.FromMinutes(1)
        });

        var response = await _h.CheckoutService().CheckoutAsync(Command(key));

        Assert.Equal("INVORPHAN", response.InvoiceNo);
        Assert.Equal(TransactionStatus.Pending, response.Status);
        _h.Gateway.VerifyNoOtherCalls();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(5);
        }
    }
}

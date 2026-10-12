using Moq;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Application.Services;
using Rezerv.Domain.Entities;
using Rezerv.Tests.TestDoubles;

namespace Rezerv.Tests.Application;

public class RefundServiceTests
{
    private readonly TestHarness _h = new();

    private static RefundCommand Command(decimal? amount = null, string? key = null, string invoiceNo = "INV1") =>
        new(key ?? Guid.NewGuid().ToString(), invoiceNo, amount, "Customer request");

    private void PaymentState(CaptureState state) =>
        _h.ActionGateway.Setup(g => g.GetPaymentStatusAsync("INV1", It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentActionStatus(state, "0000", "Success", state.ToString()));

    private void RefundAnswers(RefundOutcome outcome, string respCode = "0000") =>
        _h.ActionGateway.Setup(g => g.RefundAsync(It.IsAny<RefundActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RefundActionResult(outcome, respCode, outcome.ToString(), outcome == RefundOutcome.Pending ? "RP" : "RF", "REF-1"));

    // ---------- Preconditions ----------

    [Theory]
    [InlineData(TransactionStatus.Pending)]
    [InlineData(TransactionStatus.Failed)]
    public async Task Only_succeeded_payments_can_be_refunded(TransactionStatus status)
    {
        _h.SeedTransaction("INV1", status: status);

        await Assert.ThrowsAsync<ValidationException>(() => _h.RefundService().RefundAsync(Command()));
        Assert.Empty(_h.Db.Refunds);
    }

    [Fact]
    public async Task Unknown_invoice_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.RefundService().RefundAsync(Command(invoiceNo: "NOPE")));
    }

    [Fact]
    public async Task Refund_requires_payment_action_configuration()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);
        _h.ActionGateway.SetupGet(g => g.IsConfigured).Returns(false);

        await Assert.ThrowsAsync<NotConfiguredException>(() => _h.RefundService().RefundAsync(Command()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    public async Task Refund_requires_a_uuid_idempotency_key(string? key)
    {
        await Assert.ThrowsAsync<ValidationException>(() => _h.RefundService().RefundAsync(new RefundCommand(key, "INV1", null, null)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.005")]
    public async Task Amount_must_be_positive_with_two_decimals(string amount)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _h.RefundService().RefundAsync(Command(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task Amount_above_what_is_left_is_rejected()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded, refunded: 20m);

        await Assert.ThrowsAsync<ValidationException>(() => _h.RefundService().RefundAsync(Command(10m)));
    }

    [Fact]
    public async Task Fully_refunded_payment_is_rejected()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded, refunded: 25m);

        await Assert.ThrowsAsync<ValidationException>(() => _h.RefundService().RefundAsync(Command()));
    }

    // ---------- Void vs refund ----------

    [Fact]
    public async Task Unsettled_payment_is_voided_for_the_full_amount()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);
        PaymentState(CaptureState.Authorized);
        RefundActionRequest? sent = null;
        _h.ActionGateway.Setup(g => g.VoidAsync(It.IsAny<RefundActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<RefundActionRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(new RefundActionResult(RefundOutcome.Succeeded, "0000", "Success", "V", "VOID-1"));
        var command = Command();

        var dto = await _h.RefundService().RefundAsync(command);

        Assert.Equal(RefundType.Void, dto.Type);
        Assert.Equal(RefundStatus.Succeeded, dto.Status);
        Assert.Equal(25m, dto.Amount);
        Assert.Equal("INV1", sent!.InvoiceNo);
        Assert.Equal(command.IdempotencyKey, sent.IdempotencyId);
        Assert.Equal(25m, _h.Db.Transaction("INV1").RefundedAmount);
    }

    [Fact]
    public async Task Partial_refund_of_an_unsettled_payment_is_rejected()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);
        PaymentState(CaptureState.Authorized);

        await Assert.ThrowsAsync<ValidationException>(() => _h.RefundService().RefundAsync(Command(10m)));
        Assert.Empty(_h.Db.Refunds);
    }

    [Fact]
    public async Task Settled_payment_is_refunded_partially()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);
        PaymentState(CaptureState.Settled);
        RefundAnswers(RefundOutcome.Succeeded);

        var dto = await _h.RefundService().RefundAsync(Command(10m));

        Assert.Equal(RefundType.Refund, dto.Type);
        Assert.Equal(RefundStatus.Succeeded, dto.Status);
        Assert.Equal(10m, dto.Amount);
        Assert.Equal("REF-1", dto.ProviderRef);
        Assert.Equal(10m, _h.Db.Transaction("INV1").RefundedAmount);
    }

    [Fact]
    public async Task Refund_without_amount_refunds_what_is_left()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded, refunded: 5m);
        PaymentState(CaptureState.Settled);
        RefundAnswers(RefundOutcome.Succeeded);

        var dto = await _h.RefundService().RefundAsync(Command());

        Assert.Equal(20m, dto.Amount);
        Assert.Equal(25m, _h.Db.Transaction("INV1").RefundedAmount);
    }

    [Theory]
    [InlineData(CaptureState.Voided)]
    [InlineData(CaptureState.Other)]
    public async Task Payment_that_cannot_be_refunded_at_2c2p_is_rejected(CaptureState state)
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);
        PaymentState(state);

        await Assert.ThrowsAsync<ValidationException>(() => _h.RefundService().RefundAsync(Command()));
    }

    [Fact]
    public async Task Rejected_refund_is_failed_and_releases_the_reserved_amount()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);
        PaymentState(CaptureState.Settled);
        RefundAnswers(RefundOutcome.Failed, "4001");

        var dto = await _h.RefundService().RefundAsync(Command(10m));

        Assert.Equal(RefundStatus.Failed, dto.Status);
        Assert.Equal(0m, _h.Db.Transaction("INV1").RefundedAmount);
    }

    [Fact]
    public async Task Refund_without_an_answer_stays_pending_and_keeps_the_reservation()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);
        PaymentState(CaptureState.Settled);
        _h.ActionGateway.Setup(g => g.RefundAsync(It.IsAny<RefundActionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PaymentGatewayException("timeout"));

        var dto = await _h.RefundService().RefundAsync(Command(10m));

        Assert.Equal(RefundStatus.Pending, dto.Status);
        Assert.Equal(RefundService.AwaitingResultCode, dto.RespCode);
        Assert.Equal(10m, _h.Db.Transaction("INV1").RefundedAmount);
    }

    // ---------- Idempotency ----------

    [Fact]
    public async Task Same_key_returns_the_original_refund_without_calling_2c2p_again()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);
        PaymentState(CaptureState.Settled);
        RefundAnswers(RefundOutcome.Succeeded);
        var command = Command(10m);

        var first = await _h.RefundService().RefundAsync(command);
        var second = await _h.RefundService().RefundAsync(command);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_h.Db.Refunds);
        Assert.Equal(10m, _h.Db.Transaction("INV1").RefundedAmount);
        _h.ActionGateway.Verify(g => g.RefundAsync(It.IsAny<RefundActionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Same_key_with_a_different_amount_is_rejected()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);
        PaymentState(CaptureState.Settled);
        RefundAnswers(RefundOutcome.Succeeded);
        var key = Guid.NewGuid().ToString();
        await _h.RefundService().RefundAsync(Command(10m, key));

        await Assert.ThrowsAsync<IdempotencyKeyMismatchException>(() => _h.RefundService().RefundAsync(Command(5m, key)));
    }

    [Fact]
    public async Task Concurrent_refunds_with_different_keys_never_exceed_the_payment()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);
        PaymentState(CaptureState.Settled);
        RefundAnswers(RefundOutcome.Succeeded);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            try { await _h.RefundService().RefundAsync(Command(10m)); return true; }
            catch (ValidationException) { return false; }
        })));

        Assert.Equal(2, attempts.Count(ok => ok));
        Assert.Equal(20m, _h.Db.Transaction("INV1").RefundedAmount);
        Assert.Equal(2, _h.Db.Refunds.Count);
    }

    // ---------- Listing ----------

    [Fact]
    public async Task List_reconciles_a_pending_refund_with_2c2p()
    {
        var txn = _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded, refunded: 10m);
        _h.Db.Seed(new Refund
        {
            TransactionId = txn.Id, IdempotencyKey = Guid.NewGuid().ToString(), Type = RefundType.Refund, Amount = 10m,
            Currency = "SGD", Status = RefundStatus.Pending, RespCode = "0000", ProviderRef = "REF-1",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        _h.ActionGateway.Setup(g => g.GetRefundStatusAsync("INV1", 25m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RefundStatusResult("0000", "Success", "RF", [new RefundStatusEntry(10m, RefundOutcome.Succeeded, "RF", "REF-1")]));

        var list = await _h.RefundService().ListAsync("INV1");

        Assert.Equal(RefundStatus.Succeeded, Assert.Single(list).Status);
    }

    [Fact]
    public async Task List_of_an_unknown_invoice_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.RefundService().ListAsync("NOPE"));
    }
}

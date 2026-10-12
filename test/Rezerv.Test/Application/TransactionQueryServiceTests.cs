using Moq;
using Rezerv.Application.Common;
using Rezerv.Domain.Entities;
using Rezerv.Tests.TestDoubles;
using static Rezerv.Tests.TestDoubles.TestHarness;

namespace Rezerv.Tests.Application;

/// <summary>GET /api/transactions/{invoiceNo}: the result page reads our database and nudges Payment Inquiry while Pending.</summary>
public class TransactionQueryServiceTests
{
    private readonly TestHarness _h = new();

    [Fact]
    public async Task Unknown_invoice_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.TransactionQueryService().GetAsync("NOPE"));
    }

    [Fact]
    public async Task Final_transaction_is_returned_from_the_database_without_calling_2c2p()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);

        var dto = await _h.TransactionQueryService().GetAsync("INV1");

        Assert.Equal(TransactionStatus.Succeeded, dto.Status);
        Assert.Equal("Concert ticket", dto.ProductName);
        Assert.Equal(25.00m, dto.Amount);
        Assert.Equal("SGD", dto.Currency);
        _h.Gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Failed_transaction_exposes_respDesc_and_product_for_the_go_back_button()
    {
        var txn = _h.SeedTransaction("INV1", status: TransactionStatus.Failed);
        _h.Db.Read(db => db.Transactions.Single(t => t.Id == txn.Id).RespDesc = "Insufficient funds");

        var dto = await _h.TransactionQueryService().GetAsync("INV1");

        Assert.Equal(TransactionStatus.Failed, dto.Status);
        Assert.Equal("Insufficient funds", dto.RespDesc);
        Assert.Equal(CustomerId, dto.CustomerId);
        Assert.Equal(OneTimeProductId, dto.ProductId);
    }

    [Fact]
    public async Task Pending_saved_card_without_redirect_is_reconciled_with_a_final_inquiry()
    {
        _h.SeedTransaction("INV1", cardId: CardId);
        _h.Gateway.Setup(g => g.InquireAsync("INV1", It.IsAny<CancellationToken>())).ReturnsAsync(Result("INV1", "4005", respDesc: "Declined"));

        var dto = await _h.TransactionQueryService().GetAsync("INV1");

        Assert.Equal(TransactionStatus.Failed, dto.Status);
        Assert.Equal("Declined", dto.RespDesc);
    }

    [Fact]
    public async Task Pending_hosted_page_payment_is_not_failed_by_an_inquiry()
    {
        _h.SeedTransaction("INV1", redirectUrl: "https://pay");
        _h.Gateway.Setup(g => g.InquireAsync("INV1", It.IsAny<CancellationToken>())).ReturnsAsync(Result("INV1", "4005"));

        var dto = await _h.TransactionQueryService().GetAsync("INV1");

        Assert.Equal(TransactionStatus.Pending, dto.Status);
    }

    [Fact]
    public async Task Pending_hosted_page_payment_picks_up_a_succeeded_inquiry()
    {
        _h.SeedTransaction("INV1", redirectUrl: "https://pay");
        _h.Gateway.Setup(g => g.InquireAsync("INV1", It.IsAny<CancellationToken>())).ReturnsAsync(Result("INV1"));

        var dto = await _h.TransactionQueryService().GetAsync("INV1");

        Assert.Equal(TransactionStatus.Succeeded, dto.Status);
        Assert.Equal("411111XXXXXX1111", dto.MaskedCardNo);
        Assert.Equal("CC", dto.PaymentChannel);
    }

    // ---------- GET /api/transactions/{customerId} (refund page) ----------

    [Fact]
    public async Task Customer_transactions_are_listed_newest_first_and_only_for_that_customer()
    {
        var now = DateTime.UtcNow;
        _h.SeedTransaction("OLD", status: TransactionStatus.Succeeded, createdAt: now.AddDays(-2));
        _h.SeedTransaction("NEW", status: TransactionStatus.Failed, createdAt: now);
        _h.SeedTransaction("MID", createdAt: now.AddDays(-1));
        _h.SeedTransaction("BOBS", status: TransactionStatus.Succeeded, customerId: OtherCustomerId);

        var list = await _h.TransactionQueryService().GetByCustomerAsync(CustomerId);

        Assert.Equal(["NEW", "MID", "OLD"], list.Select(t => t.InvoiceNo));
        Assert.All(list, t => Assert.Equal("Concert ticket", t.ProductName));
    }

    [Fact]
    public async Task Customer_transactions_show_what_is_left_to_refund()
    {
        _h.SeedTransaction("PAID", status: TransactionStatus.Succeeded, amount: 25m);
        _h.SeedTransaction("PARTLY", status: TransactionStatus.Succeeded, amount: 25m, refunded: 10m);
        _h.SeedTransaction("FULLY", status: TransactionStatus.Succeeded, amount: 25m, refunded: 25m);
        _h.SeedTransaction("FAILED", status: TransactionStatus.Failed);
        _h.SeedTransaction("PENDING");

        var refundable = (await _h.TransactionQueryService().GetByCustomerAsync(CustomerId))
            .ToDictionary(t => t.InvoiceNo, t => t.RefundableAmount);

        Assert.Equal(25m, refundable["PAID"]);
        Assert.Equal(15m, refundable["PARTLY"]);
        Assert.Equal(0m, refundable["FULLY"]);
        Assert.Equal(0m, refundable["FAILED"]);
        Assert.Equal(0m, refundable["PENDING"]);
    }

    [Fact]
    public async Task Customer_without_transactions_gets_an_empty_list()
    {
        Assert.Empty(await _h.TransactionQueryService().GetByCustomerAsync(OtherCustomerId));
    }

    [Fact]
    public async Task Customer_transactions_of_an_unknown_customer_are_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.TransactionQueryService().GetByCustomerAsync(999));
    }

    [Fact]
    public async Task Listing_customer_transactions_never_calls_2c2p()
    {
        _h.SeedTransaction("INV1", redirectUrl: "https://pay");

        await _h.TransactionQueryService().GetByCustomerAsync(CustomerId);

        _h.Gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Polling_is_throttled_to_one_inquiry_per_interval()
    {
        _h.SeedTransaction("INV1", redirectUrl: "https://pay");
        _h.Gateway.Setup(g => g.InquireAsync("INV1", It.IsAny<CancellationToken>())).ReturnsAsync(Result("INV1", "4005"));

        for (var i = 0; i < 5; i++)
            await _h.TransactionQueryService().GetAsync("INV1");

        _h.Gateway.Verify(g => g.InquireAsync("INV1", It.IsAny<CancellationToken>()), Times.Once);
    }
}

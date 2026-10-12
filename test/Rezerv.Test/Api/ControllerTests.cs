using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Rezerv.API.Controllers;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Application.Contracts;
using Rezerv.Domain.Entities;
using Rezerv.Tests.TestDoubles;
using static Rezerv.Tests.TestDoubles.TestHarness;

namespace Rezerv.Tests.Api;

public class ControllerTests
{
    private readonly TestHarness _h = new();

    private static T WithHttpContext<T>(T controller, IPAddress? remoteIp = null) where T : ControllerBase
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = remoteIp;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.ProblemDetailsFactory = new SimpleProblemDetailsFactory();
        return controller;
    }

    // ---------- GET /api/customers, /api/customers/{id}/cards, /api/products ----------

    [Fact]
    public async Task Customers_lists_seeded_customers()
    {
        var customers = await new CustomersController(_h.CatalogService(), _h.CustomerCardService()).GetAll(default);

        Assert.Equal(2, customers.Count);
        Assert.Contains(customers, c => c is { Id: CustomerId, FirstName: "Alice", Email: "alice@example.com" });
    }

    [Fact]
    public async Task Customer_cards_are_masked()
    {
        var cards = await new CustomersController(_h.CatalogService(), _h.CustomerCardService()).GetCards(CustomerId, default);

        Assert.Equal("411111XXXXXX1111", Assert.Single(cards).MaskedCardNo);
    }

    [Fact]
    public async Task Customer_cards_of_an_unknown_customer_throw_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => new CustomersController(_h.CatalogService(), _h.CustomerCardService()).GetCards(999, default));
    }

    [Fact]
    public async Task Remove_card_returns_204_and_the_card_is_gone()
    {
        _h.ActionGateway.Setup(g => g.RemoveCardTokenAsync(It.IsAny<CardTokenRemovalRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CardTokenRemovalResult(true, "00", "Success"));
        var controller = new CustomersController(_h.CatalogService(), _h.CustomerCardService());

        var result = await controller.RemoveCard(CustomerId, CardId, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await controller.GetCards(CustomerId, default));
    }

    [Fact]
    public async Task Remove_card_of_another_customer_throws_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            new CustomersController(_h.CatalogService(), _h.CustomerCardService()).RemoveCard(CustomerId, OtherCustomersCardId, default));
    }

    [Fact]
    public async Task Products_lists_active_products()
    {
        var products = await new ProductsController(_h.CatalogService()).GetAll(default);

        Assert.Equal([OneTimeProductId, RecurringProductId, UsdProductId], products.Select(p => p.Id));
    }

    // ---------- POST /api/checkout ----------

    [Fact]
    public async Task Checkout_passes_body_and_idempotency_key_and_returns_redirect()
    {
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenOk("https://pay/x"));
        var controller = WithHttpContext(new CheckoutController(_h.CheckoutService()));
        var key = Guid.NewGuid().ToString();

        var response = await controller.Checkout(new CheckoutRequest(CustomerId, OneTimeProductId, null), key, default);

        Assert.Equal(TransactionStatus.Pending, response.Status);
        Assert.Equal("https://pay/x", response.RedirectUrl);
        Assert.Equal(key, _h.Db.SingleTransaction().IdempotencyKey);
    }

    [Fact]
    public async Task Checkout_twice_with_the_same_key_returns_the_same_invoice()
    {
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenOk());
        var key = Guid.NewGuid().ToString();
        var request = new CheckoutRequest(CustomerId, OneTimeProductId, null);

        var first = await WithHttpContext(new CheckoutController(_h.CheckoutService())).Checkout(request, key, default);
        var second = await WithHttpContext(new CheckoutController(_h.CheckoutService())).Checkout(request, key, default);

        Assert.Equal(first.InvoiceNo, second.InvoiceNo);
        _h.Gateway.Verify(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Checkout_without_idempotency_key_is_a_validation_error()
    {
        var controller = WithHttpContext(new CheckoutController(_h.CheckoutService()));

        await Assert.ThrowsAsync<ValidationException>(() =>
            controller.Checkout(new CheckoutRequest(CustomerId, OneTimeProductId, null), null, default));
    }

    [Theory]
    [InlineData("::ffff:10.0.0.5", "10.0.0.5")]
    [InlineData("198.51.100.20", "198.51.100.20")]
    [InlineData(null, "127.0.0.1")]
    public async Task Checkout_sends_the_client_ipv4_to_do_payment(string? remote, string expected)
    {
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentTokenResult(true, "PT", null, "0000", "Success"));
        DoPaymentRequest? sent = null;
        _h.Gateway.Setup(g => g.DoPaymentAsync(It.IsAny<DoPaymentRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DoPaymentRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(new DoPaymentResult(DoPaymentOutcome.Redirect, "1001", null, "https://3ds"));
        var controller = WithHttpContext(new CheckoutController(_h.CheckoutService()), remote is null ? null : IPAddress.Parse(remote));

        await controller.Checkout(new CheckoutRequest(CustomerId, OneTimeProductId, CardId), Guid.NewGuid().ToString(), default);

        Assert.Equal(expected, sent!.ClientIp);
    }

    // ---------- POST /api/payments/2c2p/notification ----------

    private PaymentsController PaymentsController() =>
        WithHttpContext(new PaymentsController(_h.NotificationService(), _h.TransactionQueryService(), NullLogger<PaymentsController>.Instance));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Notification_without_payload_is_400(string? payload)
    {
        var result = await PaymentsController().Notification(new PaymentsController.NotificationRequest(payload), default);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        _h.Gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Verified_notification_updates_the_transaction_and_returns_200()
    {
        _h.SeedTransaction("INV1");
        _h.Gateway.Setup(g => g.ParseNotification("jwt")).Returns(Result("INV1"));

        var result = await PaymentsController().Notification(new PaymentsController.NotificationRequest("jwt"), default);

        Assert.IsType<OkResult>(result);
        Assert.Equal(TransactionStatus.Succeeded, _h.Db.Transaction("INV1").Status);
    }

    [Fact]
    public async Task Notification_for_an_unknown_invoice_or_with_a_mismatch_still_returns_200()
    {
        _h.SeedTransaction("INV1");
        _h.Gateway.Setup(g => g.ParseNotification("unknown")).Returns(Result("NOPE"));
        _h.Gateway.Setup(g => g.ParseNotification("mismatch")).Returns(Result("INV1", amount: 1m));

        Assert.IsType<OkResult>(await PaymentsController().Notification(new("unknown"), default));
        Assert.IsType<OkResult>(await PaymentsController().Notification(new("mismatch"), default));
        Assert.Equal(TransactionStatus.Pending, _h.Db.Transaction("INV1").Status);
    }

    [Fact]
    public async Task Duplicate_notification_returns_200_and_changes_nothing()
    {
        _h.SeedTransaction("INV1");
        _h.Gateway.Setup(g => g.ParseNotification("jwt")).Returns(Result("INV1", customerToken: "NEW"));

        Assert.IsType<OkResult>(await PaymentsController().Notification(new("jwt"), default));
        Assert.IsType<OkResult>(await PaymentsController().Notification(new("jwt"), default));

        Assert.Single(_h.Db.Cards, c => c.CustomerToken == "NEW");
    }

    [Fact]
    public async Task Notification_with_a_bad_signature_throws_invalid_signature()
    {
        _h.Gateway.Setup(g => g.ParseNotification("forged")).Throws(new InvalidSignatureException("bad"));

        await Assert.ThrowsAsync<InvalidSignatureException>(() => PaymentsController().Notification(new("forged"), default));
    }

    // ---------- GET /api/transactions/{invoiceNo} ----------

    private TransactionsController TransactionsApi() =>
        WithHttpContext(new TransactionsController(_h.TransactionQueryService(), _h.CheckoutService()));

    [Fact]
    public async Task Transaction_details_come_from_the_database()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);

        var dto = await TransactionsApi().Get("INV1", default);

        Assert.Equal("INV1", dto.InvoiceNo);
        Assert.Equal(TransactionStatus.Succeeded, dto.Status);
        Assert.Equal("Concert ticket", dto.ProductName);
    }

    [Fact]
    public async Task Unknown_transaction_throws_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => TransactionsApi().Get("NOPE", default));
    }

    [Fact]
    public async Task Customer_transactions_endpoint_lists_refundable_transactions()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);

        var list = await TransactionsApi().GetByCustomer(CustomerId, default);

        var txn = Assert.Single(list);
        Assert.Equal("INV1", txn.InvoiceNo);
        Assert.Equal(25m, txn.RefundableAmount);
    }

    [Fact]
    public void Customer_route_only_matches_numeric_ids_so_invoice_lookups_still_work()
    {
        var byCustomer = typeof(TransactionsController).GetMethod(nameof(TransactionsController.GetByCustomer))!
            .GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>().Single();
        var byInvoice = typeof(TransactionsController).GetMethod(nameof(TransactionsController.Get))!
            .GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>().Single();

        Assert.Equal("{customerId:int}", byCustomer.Template);
        Assert.Equal("{invoiceNo}", byInvoice.Template);
        Assert.False(int.TryParse(Rezerv.Application.Services.CheckoutService.NewInvoiceNo(DateTime.UtcNow), out _));
    }

    // ---------- POST /api/transactions/{invoiceNo}/retry ----------

    [Fact]
    public async Task Retry_of_a_failed_payment_returns_a_new_invoice_with_the_hosted_page()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed);
        _h.Gateway.Setup(g => g.CreatePaymentTokenAsync(It.IsAny<PaymentTokenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TokenOk("https://pay/retry"));

        var response = await TransactionsApi().Retry("INV1", null, Guid.NewGuid().ToString(), default);

        Assert.NotEqual("INV1", response.InvoiceNo);
        Assert.Equal((TransactionStatus.Pending, "https://pay/retry"), (response.Status, response.RedirectUrl));
        var original = await TransactionsApi().Get("INV1", default);
        Assert.Equal(response.InvoiceNo, original.RetryInvoiceNo);
        Assert.False(original.CanRetry);
    }

    [Fact]
    public async Task Retry_of_a_succeeded_payment_is_a_validation_error()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);

        await Assert.ThrowsAsync<ValidationException>(() =>
            TransactionsApi().Retry("INV1", new RetryRequest(null), Guid.NewGuid().ToString(), default));
    }

    [Fact]
    public void Retry_route_is_a_post_on_the_invoice()
    {
        var retry = typeof(TransactionsController).GetMethod(nameof(TransactionsController.Retry))!
            .GetCustomAttributes(typeof(HttpPostAttribute), false).Cast<HttpPostAttribute>().Single();

        Assert.Equal("{invoiceNo}/retry", retry.Template);
    }

    // ---------- /api/transactions/{invoiceNo}/refunds ----------

    [Fact]
    public async Task Refund_creates_a_refund_for_a_succeeded_payment()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);
        _h.ActionGateway.Setup(g => g.GetPaymentStatusAsync("INV1", 25m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentActionStatus(CaptureState.Settled, "0000", null, "S"));
        _h.ActionGateway.Setup(g => g.RefundAsync(It.IsAny<RefundActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RefundActionResult(RefundOutcome.Succeeded, "0000", "Success", "RF", "REF-1"));
        var controller = new RefundsController(_h.RefundService());

        var dto = await controller.Create("INV1", new RefundRequest(5m, "Damaged"), Guid.NewGuid().ToString(), default);

        Assert.Equal(RefundStatus.Succeeded, dto.Status);
        Assert.Equal(5m, dto.Amount);
        Assert.Equal("Damaged", dto.Reason);
        Assert.Single(await controller.List("INV1", default));
    }

    [Fact]
    public async Task Refund_of_a_failed_payment_is_a_validation_error()
    {
        _h.SeedTransaction("INV1", status: TransactionStatus.Failed);

        await Assert.ThrowsAsync<ValidationException>(() =>
            new RefundsController(_h.RefundService()).Create("INV1", new RefundRequest(null, null), Guid.NewGuid().ToString(), default));
    }

    private sealed class SimpleProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(HttpContext httpContext, int? statusCode = null, string? title = null,
            string? type = null, string? detail = null, string? instance = null) =>
            new() { Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance };

        public override ValidationProblemDetails CreateValidationProblemDetails(HttpContext httpContext,
            Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelStateDictionary, int? statusCode = null, string? title = null,
            string? type = null, string? detail = null, string? instance = null) =>
            new(modelStateDictionary) { Status = statusCode, Title = title, Detail = detail };
    }
}

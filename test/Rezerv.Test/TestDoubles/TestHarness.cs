using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Services;
using Rezerv.Domain.Entities;

namespace Rezerv.Tests.TestDoubles;

/// <summary>Seeded in-memory database, fake Redis and a mocked 2C2P, wired into the real application services.</summary>
public sealed class TestHarness
{
    public const int CustomerId = 1;
    public const int OtherCustomerId = 2;
    public const int OneTimeProductId = 10;
    public const int RecurringProductId = 11;
    public const int InactiveProductId = 12;
    public const int UsdProductId = 13;
    public const int CardId = 100;
    public const int OtherCustomersCardId = 101;
    public const string CardToken = "TOKEN-ALICE-1";

    public InMemoryDatabase Db { get; } = new();
    public FakeCacheService Cache { get; } = new();
    public Mock<IPaymentGateway> Gateway { get; } = new(MockBehavior.Strict);
    public Mock<IPaymentActionGateway> ActionGateway { get; } = new(MockBehavior.Strict);

    public CheckoutOptions Options { get; } = new()
    {
        WaitTimeout = TimeSpan.FromSeconds(5),
        WaitPollInterval = TimeSpan.FromMilliseconds(10)
    };

    public TestHarness()
    {
        Db.Customers.Add(new Customer { Id = CustomerId, FirstName = "Alice", LastName = "Tan", Email = "alice@example.com" });
        Db.Customers.Add(new Customer { Id = OtherCustomerId, FirstName = "Bob", LastName = "Lim", Email = "bob@example.com" });

        Db.Products.Add(new Product { Id = OneTimeProductId, Name = "Concert ticket", Price = 25.00m, Currency = "SGD", PaymentType = PaymentType.OneTime });
        Db.Products.Add(new Product { Id = RecurringProductId, Name = "Monthly plan", Price = 9.90m, Currency = "SGD", PaymentType = PaymentType.Recurring });
        Db.Products.Add(new Product { Id = InactiveProductId, Name = "Retired", Price = 1m, Currency = "SGD", IsActive = false });
        Db.Products.Add(new Product { Id = UsdProductId, Name = "US only", Price = 5m, Currency = "USD" });

        Db.Cards.Add(new CustomerCard { Id = CardId, CustomerId = CustomerId, CustomerToken = CardToken, MaskedCardNo = "411111XXXXXX1111", PaymentChannel = "CC" });
        Db.Cards.Add(new CustomerCard { Id = OtherCustomersCardId, CustomerId = OtherCustomerId, CustomerToken = "TOKEN-BOB-1", MaskedCardNo = "520000XXXXXX0007", PaymentChannel = "CC" });

        ActionGateway.SetupGet(g => g.IsConfigured).Returns(true);
    }

    // Each factory call is a new request scope (or another API instance sharing MySQL and Redis).

    public PaymentResultProcessor ResultProcessor() => ResultProcessor(Db.NewSession());

    private PaymentResultProcessor ResultProcessor(InMemoryDatabase.Session s) =>
        new(s.TransactionRepository, s.CardRepository, s.UnitOfWork, Gateway.Object, Cache, NullLogger<PaymentResultProcessor>.Instance);

    public CheckoutService CheckoutService()
    {
        var s = Db.NewSession();
        return new(s.CustomerRepository, s.ProductRepository, s.CardRepository, s.TransactionRepository, s.UnitOfWork,
            Gateway.Object, ResultProcessor(s), Cache, Options, NullLogger<CheckoutService>.Instance);
    }

    public CatalogService CatalogService()
    {
        var s = Db.NewSession();
        return new(s.CustomerRepository, s.ProductRepository, s.CardRepository, Cache, Options);
    }

    public CustomerCardService CustomerCardService()
    {
        var s = Db.NewSession();
        return new(s.CustomerRepository, s.CardRepository, s.TransactionRepository, ActionGateway.Object, Cache, Options,
            NullLogger<CustomerCardService>.Instance);
    }

    public PaymentNotificationService NotificationService() => new(Gateway.Object, ResultProcessor());

    public TransactionQueryService TransactionQueryService()
    {
        var s = Db.NewSession();
        return new(s.TransactionRepository, s.CustomerRepository, ResultProcessor(s), Cache, Options);
    }

    public RefundService RefundService()
    {
        var s = Db.NewSession();
        return new(s.TransactionRepository, s.RefundRepository, s.UnitOfWork, ActionGateway.Object, Cache, Options, NullLogger<RefundService>.Instance);
    }

    public PaymentTransaction SeedTransaction(string invoiceNo = "INV1", TransactionStatus status = TransactionStatus.Pending,
        decimal amount = 25.00m, int productId = OneTimeProductId, int? cardId = null, string? redirectUrl = null, decimal refunded = 0m,
        int customerId = CustomerId, DateTime? createdAt = null, long? retryOf = null, string? respDesc = null)
    {
        var txn = new PaymentTransaction
        {
            CustomerId = customerId,
            ProductId = productId,
            CustomerCardId = cardId,
            InvoiceNo = invoiceNo,
            IdempotencyKey = Guid.NewGuid().ToString(),
            Amount = amount,
            Currency = "SGD",
            Status = status,
            RedirectUrl = redirectUrl,
            RefundedAmount = refunded,
            RetryOfTransactionId = retryOf,
            RespDesc = respDesc,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        Db.Seed(txn);
        return txn;
    }

    public static PaymentResult Result(string invoiceNo, string respCode = "0000", decimal? amount = 25.00m, string? currency = "SGD",
        string? customerToken = null, string? respDesc = null) =>
        new(invoiceNo,
            respCode == "0000" ? TransactionStatus.Succeeded : TransactionStatus.Failed,
            respCode,
            respDesc ?? (respCode == "0000" ? "Success" : "Declined"),
            amount,
            currency,
            "TRAN-REF-1",
            "411111XXXXXX1111",
            "CC",
            customerToken);

    public static PaymentTokenResult TokenOk(string url = "https://sandbox-pgw.2c2p.com/payment/4.3/#/token/abc") =>
        new(true, "PAYMENT-TOKEN-1", url, "0000", "Success");
}

using Rezerv.Application.Common;
using Rezerv.Application.Contracts;
using Rezerv.Domain.Entities;
using Rezerv.Tests.TestDoubles;
using static Rezerv.Tests.TestDoubles.TestHarness;

namespace Rezerv.Tests.Application;

public class CatalogServiceTests
{
    private readonly TestHarness _h = new();

    [Fact]
    public async Task Customers_are_loaded_from_the_database_and_cached()
    {
        var customers = await _h.CatalogService().GetCustomersAsync();

        Assert.Equal([CustomerId, OtherCustomerId], customers.Select(c => c.Id));
        Assert.True(_h.Cache.Contains(CacheKeys.Customers));
    }

    [Fact]
    public async Task Cached_customers_are_served_from_redis()
    {
        await _h.Cache.SetAsync(CacheKeys.Customers, new List<CustomerDto> { new(42, "From", "Cache", "c@x") }, TimeSpan.FromMinutes(1));

        var customers = await _h.CatalogService().GetCustomersAsync();

        Assert.Equal(42, Assert.Single(customers).Id);
    }

    [Fact]
    public async Task Database_is_used_when_redis_is_down()
    {
        _h.Cache.Unavailable = true;

        Assert.Equal(2, (await _h.CatalogService().GetCustomersAsync()).Count);
    }

    [Fact]
    public async Task Only_active_products_are_listed()
    {
        var products = await _h.CatalogService().GetProductsAsync();

        Assert.DoesNotContain(products, p => p.Id == InactiveProductId);
        Assert.Contains(products, p => p.Id == RecurringProductId && p.PaymentType == PaymentType.Recurring);
    }

    [Fact]
    public async Task Cards_are_masked_and_scoped_to_the_customer()
    {
        var cards = await _h.CatalogService().GetCustomerCardsAsync(CustomerId);

        var card = Assert.Single(cards);
        Assert.Equal(CardId, card.Id);
        Assert.Equal("411111XXXXXX1111", card.MaskedCardNo);
        // The DTO has no token field at all.
        Assert.DoesNotContain(typeof(CustomerCardDto).GetProperties(), p => p.Name.Contains("Token"));
    }

    [Fact]
    public async Task Cards_of_an_unknown_customer_are_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.CatalogService().GetCustomerCardsAsync(999));
    }
}

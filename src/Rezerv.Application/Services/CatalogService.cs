using Rezerv.Application.Abstractions;
using Rezerv.Application.Common;
using Rezerv.Application.Contracts;

namespace Rezerv.Application.Services;

/// <summary>Read side for customers, products and saved cards: Redis first, MySQL on a miss.</summary>
public sealed class CatalogService(ICustomerRepository customers,IProductRepository products,ICustomerCardRepository cards,ICacheService cache,CheckoutOptions options)
{
    public Task<IReadOnlyList<CustomerDto>> GetCustomersAsync(CancellationToken ct = default) =>
        GetOrLoadAsync(CacheKeys.Customers, async () => (await customers.GetAllAsync(ct)).Select(c => c.ToDto()).ToList(), ct);

    public Task<IReadOnlyList<ProductDto>> GetProductsAsync(CancellationToken ct = default) =>
        GetOrLoadAsync(CacheKeys.Products, async () => (await products.GetAllActiveAsync(ct)).Select(p => p.ToDto()).ToList(), ct);

    public async Task<IReadOnlyList<CustomerCardDto>> GetCustomerCardsAsync(int customerId, CancellationToken ct = default)
    {
        if (!await customers.ExistsAsync(customerId, ct))
            throw new NotFoundException($"Customer {customerId} not found.");

        return await GetOrLoadAsync(CacheKeys.CustomerCards(customerId),
            async () => (await cards.GetByCustomerAsync(customerId, ct)).Select(c => c.ToDto()).ToList(), ct);
    }

    private async Task<IReadOnlyList<T>> GetOrLoadAsync<T>(string key, Func<Task<List<T>>> load, CancellationToken ct)
    {
        var cached = await cache.GetAsync<List<T>>(key, ct);
        if (cached is not null)
            return cached;

        var loaded = await load();
        await cache.SetAsync(key, loaded, options.CatalogCacheTtl, ct);
        return loaded;
    }
}

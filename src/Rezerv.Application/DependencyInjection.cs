using Microsoft.Extensions.DependencyInjection;
using Rezerv.Application.Services;

namespace Rezerv.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, CheckoutOptions? checkoutOptions = null)
    {
        services.AddSingleton(checkoutOptions ?? new CheckoutOptions());
        services.AddScoped<CatalogService>();
        services.AddScoped<CustomerCardService>();
        services.AddScoped<CheckoutService>();
        services.AddScoped<PaymentResultProcessor>();
        services.AddScoped<PaymentNotificationService>();
        services.AddScoped<TransactionQueryService>();
        services.AddScoped<RefundService>();
        return services;
    }
}

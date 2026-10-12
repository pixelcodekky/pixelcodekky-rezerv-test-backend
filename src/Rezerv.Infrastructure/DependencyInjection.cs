using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rezerv.Application.Abstractions;
using Rezerv.Infrastructure.Caching;
using Rezerv.Infrastructure.Payment._2C2P;
using Rezerv.Infrastructure.Persistence;
using Rezerv.Infrastructure.Persistence.Repositories;
using StackExchange.Redis;

namespace Rezerv.Infrastructure
{
    public static class DependencyInjection
    {
        /// <param name="isDevelopment">Allows development-only fakes such as the simulated refund gateway.</param>
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, bool isDevelopment = false)
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
            var redisConnection = configuration.GetConnectionString("RedisConnection")
                ?? throw new InvalidOperationException("Connection string 'RedisConnection' not found.");

            //register MYSQL
            services.AddDbContext<AppDbContext>(opt => opt.UseMySQL(connectionString));
            //register redis
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var redisOptions = ConfigurationOptions.Parse(redisConnection);
                redisOptions.AbortOnConnectFail = true;
                return ConnectionMultiplexer.Connect(redisOptions);
            });

            //register services
            services.AddScoped<ICustomerRepository, CustomerRepository>();
            services.AddScoped<IProductRepository, ProductRepository>();
            services.AddScoped<ICustomerCardRepository, CustomerCardRepository>();
            services.AddScoped<IPaymentTransactionRepository, PaymentTransactionRepository>();
            services.AddScoped<IRefundRepository, RefundRepository>();
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<ICacheService, RedisCacheService>();

            //register 2C2P
            services.AddOptions<_2C2POptions>()
                .Bind(configuration.GetSection(_2C2POptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            

            // No retry/resilience handler on purpose: Do Payment is not idempotent at 2C2P.
            services.AddHttpClient<IPaymentGateway, _2C2PPaymentGateway>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptions<_2C2POptions>>().Value;
                http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            });

            // Payment Action API (void / refund). Keys load on first use, so the API starts without them.
            var paymentAction = configuration.GetSection(_2C2POptions.SectionName).Get<_2C2POptions>()?.PaymentAction ?? new();
            if (paymentAction.Simulate)
            {
                // Never let a simulated refund (no money returned) run against a real deployment.
                if (!isDevelopment)
                    throw new InvalidOperationException("_2C2P:PaymentAction:Simulate is only allowed in the Development environment.");
                services.AddSingleton<IPaymentActionGateway, SimulatedPaymentActionGateway>();
            }
            else
            {
                services.AddSingleton<PaymentActionKeys>();
                services.AddHttpClient<IPaymentActionGateway, _2C2PPaymentActionGateway>((sp, http) =>
                    http.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<_2C2POptions>>().Value.TimeoutSeconds));
            }

            return services;
        }
    }
}

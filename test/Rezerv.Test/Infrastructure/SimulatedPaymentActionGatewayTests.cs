using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Rezerv.Application.Abstractions;
using Rezerv.Application.Services;
using Rezerv.Domain.Entities;
using Rezerv.Infrastructure;
using Rezerv.Infrastructure.Payment._2C2P;
using Rezerv.Tests.TestDoubles;

namespace Rezerv.Tests.Infrastructure;

public class SimulatedPaymentActionGatewayTests
{
    private readonly SimulatedPaymentActionGateway _gateway = new(NullLogger<SimulatedPaymentActionGateway>.Instance);

    [Fact]
    public async Task Payments_are_reported_settled_so_partial_refunds_are_allowed()
    {
        Assert.True(_gateway.IsConfigured);
        Assert.Equal(CaptureState.Settled, (await _gateway.GetPaymentStatusAsync("INV1", 25m)).State);
    }

    [Fact]
    public async Task Refund_succeeds_and_shows_up_in_the_refund_status()
    {
        var result = await _gateway.RefundAsync(new RefundActionRequest("INV1", 10m, "key-1"));

        Assert.Equal(RefundOutcome.Succeeded, result.Outcome);
        Assert.StartsWith("SIM", result.ReferenceNo);
        var entry = Assert.Single((await _gateway.GetRefundStatusAsync("INV1", 25m)).Refunds);
        Assert.Equal(10m, entry.Amount);
        Assert.Equal(result.ReferenceNo, entry.ReferenceNo);
    }

    [Fact]
    public async Task Same_idempotency_id_returns_the_same_refund()
    {
        var first = await _gateway.RefundAsync(new RefundActionRequest("INV1", 10m, "key-1"));
        var second = await _gateway.RefundAsync(new RefundActionRequest("INV1", 10m, "key-1"));

        Assert.Equal(first, second);
        Assert.Single((await _gateway.GetRefundStatusAsync("INV1", 25m)).Refunds);
    }

    [Fact]
    public async Task Card_token_removal_is_confirmed()
    {
        var result = await _gateway.RemoveCardTokenAsync(new CardTokenRemovalRequest("TOKEN-1", "Alice Tan", null));

        Assert.True(result.Removed);
        Assert.Equal("00", result.RespCode);
    }

    [Fact]
    public async Task Refund_service_works_end_to_end_with_the_simulator()
    {
        var h = new TestHarness();
        h.SeedTransaction("INV1", status: TransactionStatus.Succeeded);
        var s = h.Db.NewSession();
        var service = new RefundService(s.TransactionRepository, s.RefundRepository, s.UnitOfWork, _gateway, h.Cache, h.Options,
            NullLogger<RefundService>.Instance);

        var partial = await service.RefundAsync(new RefundCommand(Guid.NewGuid().ToString(), "INV1", 10m, null));
        var rest = await service.RefundAsync(new RefundCommand(Guid.NewGuid().ToString(), "INV1", null, null));

        Assert.Equal((RefundType.Refund, RefundStatus.Succeeded, 10m), (partial.Type, partial.Status, partial.Amount));
        Assert.Equal((RefundStatus.Succeeded, 15m), (rest.Status, rest.Amount));
        Assert.Equal(25m, h.Db.Transaction("INV1").RefundedAmount);
    }

    // ---------- Registration ----------

    private static IServiceCollection Register(bool simulate, bool isDevelopment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=localhost;Database=x;User=x;Password=x;",
            ["ConnectionStrings:RedisConnection"] = "localhost:6379",
            ["_2C2P:PaymentAction:Simulate"] = simulate.ToString()
        }).Build();
        return new ServiceCollection().AddLogging().AddInfrastructure(configuration, isDevelopment);
    }

    [Fact]
    public void Simulate_in_development_registers_the_simulator()
    {
        using var provider = Register(simulate: true, isDevelopment: true).BuildServiceProvider();

        Assert.IsType<SimulatedPaymentActionGateway>(provider.GetRequiredService<IPaymentActionGateway>());
    }

    [Fact]
    public void Simulate_outside_development_refuses_to_start()
    {
        Assert.Throws<InvalidOperationException>(() => Register(simulate: true, isDevelopment: false));
    }

    [Fact]
    public void Without_simulate_the_real_2c2p_gateway_is_used()
    {
        var services = Register(simulate: false, isDevelopment: true);

        Assert.Contains(services, d => d.ServiceType == typeof(PaymentActionKeys));
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(SimulatedPaymentActionGateway));
    }
}

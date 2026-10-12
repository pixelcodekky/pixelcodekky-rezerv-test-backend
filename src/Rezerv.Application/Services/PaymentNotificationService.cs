using Rezerv.Application.Abstractions;

namespace Rezerv.Application.Services;

/// <summary>Handles the 2C2P backend notification ({"payload": "&lt;JWT&gt;"} posted to backendReturnUrl).</summary>
public sealed class PaymentNotificationService(IPaymentGateway gateway, PaymentResultProcessor processor)
{
    /// <exception cref="Common.InvalidSignatureException">The JWT signature does not verify with the merchant secret key.</exception>
    public Task<ApplyOutcome> HandleAsync(string payload, CancellationToken ct = default)
    {
        var result = gateway.ParseNotification(payload);
        return processor.ApplyAsync(result, ct);
    }
}

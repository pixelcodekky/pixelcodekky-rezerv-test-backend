using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rezerv.Application.Services;

namespace Rezerv.API.Controllers;

[ApiController]
[Route("api/payments/2c2p")]
public sealed class PaymentsController(PaymentNotificationService notifications,TransactionQueryService transactions,ILogger<PaymentsController> logger) : ControllerBase
{
    public sealed record NotificationRequest(string? Payload);

    /// backendReturnUrl is configured in the 2C2P merchant portal
    [HttpPost("notification")]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Notification([FromBody] NotificationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Payload))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: "payload is required.");

        var outcome = await notifications.HandleAsync(request.Payload, ct);
        logger.LogInformation("2C2P notification processed: {Outcome}", outcome);

        // 200 for every verified notification: an unknown invoice or a mismatch will not improve by 2C2P retrying.
        return Ok();
    }

    private static string? TryReadInvoiceNo(string? paymentResponse)
    {
        if (string.IsNullOrWhiteSpace(paymentResponse))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(paymentResponse)));
            return doc.RootElement.TryGetProperty("invoiceNo", out var v) ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

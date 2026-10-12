using Microsoft.AspNetCore.Mvc;
using Rezerv.Application.Contracts;
using Rezerv.Application.Services;

namespace Rezerv.API.Controllers;

[ApiController]
[Route("api/checkout")]
[Produces("application/json")]
public sealed class CheckoutController(CheckoutService checkout) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<CheckoutResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<CheckoutResponse> Checkout([FromBody] CheckoutRequest request,[FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,CancellationToken ct)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "127.0.0.1";
        return checkout.CheckoutAsync(new CheckoutCommand(idempotencyKey, request.CustomerId, request.ProductId, request.CustomerCardId, clientIp), ct);
    }
}

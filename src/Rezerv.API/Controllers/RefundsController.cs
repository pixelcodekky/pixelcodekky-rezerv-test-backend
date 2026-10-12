using Microsoft.AspNetCore.Mvc;
using Rezerv.Application.Contracts;
using Rezerv.Application.Services;

namespace Rezerv.API.Controllers;

/// <summary>
/// Refunds of a succeeded payment. This is a back-office operation: protect it (authentication / admin role)
/// before exposing it publicly.
/// </summary>
[ApiController]
[Route("api/transactions/{invoiceNo}/refunds")]
[Produces("application/json")]
public sealed class RefundsController(RefundService refunds) : ControllerBase
{
    /// <summary>
    /// Voids (unsettled, full amount) or refunds (settled, full or partial) a succeeded payment.
    /// Omit amount to refund everything not refunded yet. The same Idempotency-Key returns the original refund.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<RefundDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public Task<RefundDto> Create(string invoiceNo, [FromBody] RefundRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        return refunds.RefundAsync(new RefundCommand(idempotencyKey, invoiceNo, request.Amount, request.Reason), ct);
    }

    /// <summary>Refunds of the transaction. Pending ones are checked with 2C2P (at most every few seconds); poll until final.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<RefundDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<IReadOnlyList<RefundDto>> List(string invoiceNo, CancellationToken ct)
    {
        return refunds.ListAsync(invoiceNo, ct);
    }
}

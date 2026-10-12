using Microsoft.AspNetCore.Mvc;
using Rezerv.Application.Contracts;
using Rezerv.Application.Services;

namespace Rezerv.API.Controllers;

[ApiController]
[Route("api/transactions")]
[Produces("application/json")]
public sealed class TransactionsController(TransactionQueryService transactions, CheckoutService checkout) : ControllerBase
{
    /// <summary>
    /// Transactions of a customer, newest first, for the refund page. refundableAmount &gt; 0 marks the ones that can be
    /// refunded via POST /api/transactions/{invoiceNo}/refunds.
    /// </summary>
    /// <remarks>Numeric ids only: invoice numbers (INV...) are never numeric, so this does not clash with GET {invoiceNo}.</remarks>
    [HttpGet("{customerId:int}")]
    [ProducesResponseType<IReadOnlyList<TransactionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<IReadOnlyList<TransactionDto>> GetByCustomer(int customerId, CancellationToken ct)
    {
        return transactions.GetByCustomerAsync(customerId, ct);
    }

    [HttpGet("{invoiceNo}")]
    [ProducesResponseType<TransactionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<TransactionDto> Get(string invoiceNo, CancellationToken ct)
    {
        return transactions.GetAsync(invoiceNo, ct);
    }

    /// <summary>
    /// Retries a failed payment as a new checkout for the same customer and product, with a new invoiceNo linked to the
    /// failed one. Body is optional: customerCardId pays with a saved card, otherwise the 2C2P hosted page is used.
    /// Needs an Idempotency-Key like checkout; the same key returns the same retry.
    /// </summary>
    /// <response code="200">The new payment: invoiceNo, status and redirectUrl, exactly like POST /api/checkout.</response>
    /// <response code="400">Missing or invalid Idempotency-Key, or the transaction is not Failed.</response>
    /// <response code="404">Unknown transaction, product no longer available, or the card does not belong to the customer.</response>
    /// <response code="409">The transaction was already retried (retry the newest attempt instead), or this retry is still in progress.</response>
    /// <response code="422">The Idempotency-Key was already used for a different request.</response>
    [HttpPost("{invoiceNo}/retry")]
    [ProducesResponseType<CheckoutResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<CheckoutResponse> Retry(string invoiceNo, [FromBody] RetryRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "127.0.0.1";
        return checkout.RetryAsync(new RetryCommand(idempotencyKey, invoiceNo, request?.CustomerCardId, clientIp), ct);
    }
}

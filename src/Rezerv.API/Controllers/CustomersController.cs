using Microsoft.AspNetCore.Mvc;
using Rezerv.Application.Contracts;
using Rezerv.Application.Services;

namespace Rezerv.API.Controllers;

[ApiController]
[Route("api/customers")]
[Produces("application/json")]
public sealed class CustomersController(CatalogService catalog, CustomerCardService cards) : ControllerBase
{
    /// <summary>List seeded customers.</summary>
    [HttpGet]
    public Task<IReadOnlyList<CustomerDto>> GetAll(CancellationToken ct) => catalog.GetCustomersAsync(ct);

    /// <summary>Saved cards for a customer (masked; tokens are never returned).</summary>
    [HttpGet("{id:int}/cards")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IReadOnlyList<CustomerCardDto>> GetCards(int id, CancellationToken ct) => catalog.GetCustomerCardsAsync(id, ct);

    /// <summary>
    /// Removes a saved card: deletes its token at 2C2P first, then the card in MySQL and the cached card list.
    /// Past payments keep their masked card number.
    /// </summary>
    /// <response code="204">Card removed at 2C2P and locally.</response>
    /// <response code="404">Unknown customer, or the card does not belong to the customer (or was already removed).</response>
    /// <response code="409">The card is already being removed, or a payment with it is still Pending.</response>
    /// <response code="502">2C2P could not be reached or refused the removal; the card was kept.</response>
    /// <response code="503">The 2C2P Payment Action keys are not configured.</response>
    [HttpDelete("{id:int}/cards/{cardId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RemoveCard(int id, int cardId, CancellationToken ct)
    {
        await cards.RemoveAsync(id, cardId, ct);
        return NoContent();
    }
}

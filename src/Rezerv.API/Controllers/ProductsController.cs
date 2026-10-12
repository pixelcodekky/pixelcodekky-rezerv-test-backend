using Microsoft.AspNetCore.Mvc;
using Rezerv.Application.Contracts;
using Rezerv.Application.Services;

namespace Rezerv.API.Controllers;

[ApiController]
[Route("api/products")]
[Produces("application/json")]
public sealed class ProductsController(CatalogService catalog) : ControllerBase
{
    /// <summary>List seeded (active) products.</summary>
    [HttpGet]
    public Task<IReadOnlyList<ProductDto>> GetAll(CancellationToken ct) => catalog.GetProductsAsync(ct);
}

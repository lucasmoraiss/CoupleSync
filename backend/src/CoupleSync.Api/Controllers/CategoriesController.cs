using CoupleSync.Api.Contracts.Categories;
using CoupleSync.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/categories")]
public sealed class CategoriesController : ControllerBase
{
    /// <summary>The canonical list of categories accepted by every route that receives a category.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(CategoriesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<CategoriesResponse> GetCategories()
        => Ok(new CategoriesResponse(
            TransactionCategories.All.Select(c => new CategoryResponse(c.Key, c.Label)).ToList()));
}

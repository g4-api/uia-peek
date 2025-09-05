using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using Swashbuckle.AspNetCore.Annotations;

using System.ComponentModel.DataAnnotations;
using System.Net.Mime;

using UiaPeek.Domain;

namespace UiaPeek.Controllers
{
    [ApiController]
    [Route("/api/v4/g4/[controller]")]
    [SwaggerTag(description: "Utilities for peeking UI Automation elements and returning ancestor chains for debugging and inspection.")]
    public class PeekController : ControllerBase
    {
        [HttpGet]
        #region *** OpenApi Documentation ***
        [SwaggerOperation(
            Summary = "Peek UIA tree at screen coordinates",
            Description = "Resolves the UI Automation element at (x, y) screen coordinates and returns its ancestor chain for inspection."
        )]
        [SwaggerResponse(StatusCodes.Status200OK,
            description: "Ancestor chain successfully resolved.",
            type: typeof(object),
            contentTypes: MediaTypeNames.Application.Json)]
        [SwaggerResponse(StatusCodes.Status400BadRequest,
            description: "Invalid or missing coordinates.",
            type: typeof(object),
            contentTypes: MediaTypeNames.Application.Json)]
        [SwaggerResponse(StatusCodes.Status500InternalServerError,
            description: "Unexpected error while resolving the UIA tree.",
            type: typeof(object),
            contentTypes: MediaTypeNames.Application.Json)]
        #endregion
        public IActionResult Peek(
            [FromQuery(Name = "x")]
            [SwaggerParameter(description: "The X screen coordinate in pixels (device-independent if applicable).")]
            int? x,

            [FromQuery(Name = "y")]
            [SwaggerParameter(description: "The Y screen coordinate in pixels (device-independent if applicable).")]
            int? y,

            [FromQuery(Name = "focused")]
            [SwaggerParameter(description: "If true and if x, y or both are missing, peek the currently focused " +
                "UI element instead of using coordinates.")]
            bool focused)
        {
            // Get ancestor chain at the specified coordinates.
            var chain = (x == null || y == null) && focused
                ? new UiaPeekRepository().Peek()
                : new UiaPeekRepository().Peek((int)x, (int)y);

            // Return the JSON result.
            return Ok(chain);
        }
    }
}

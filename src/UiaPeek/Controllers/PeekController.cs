using Microsoft.AspNetCore.Mvc;

namespace UiaPeek.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PeekController : ControllerBase
    {
        [HttpGet]
        public IActionResult Peek([FromQuery]int x, [FromQuery]int y)
        {
            return Ok();
        }
    }
}

using ChromiumPeek.Domain;
using ChromiumPeek.Domain.Models;

using Microsoft.AspNetCore.Mvc;

using System;
using System.IO;

namespace ChromiumPeek.Controllers
{
    /// <summary>
    /// Exposes client-driven start/stop of a peek browser (Chromium launched with the
    /// recorder extension loaded). Start returns the launched process id; Stop kills a
    /// browser previously started by this service.
    /// </summary>
    [ApiController]
    [Route("api/v4/g4/peek")]
    public class PeekController(IChromiumPeekDomain domain) : ControllerBase
    {
        // Domain aggregate exposing the launcher (process creation + tracked-instance registry).
        private readonly IChromiumPeekDomain _domain = domain;

        /// <summary>
        /// Starts a peek browser from the supplied driver parameters and returns its process id.
        /// </summary>
        /// <param name="request">The request carrying the client driver parameters.</param>
        /// <returns>200 with the launched process id, or 400 when the request is invalid.</returns>
        [HttpPost]
        public IActionResult Start([FromBody] StartPeekRequestModel request)
        {
            // A missing body or capabilities means there is nothing to launch from.
            if (request?.DriverParameters is null)
            {
                return BadRequest(new { error = "driverParameters is required." });
            }

            // Launch the browser; an invalid binary or a missing extension surfaces as a 400.
            try
            {
                var processId = _domain.Launcher.Start(request.DriverParameters);

                return Ok(new PeekLaunchResponseModel { ProcessId = processId });
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
            {
                return BadRequest(new { error = e.Message });
            }
        }

        /// <summary>
        /// Stops a peek browser previously started by this service.
        /// </summary>
        /// <param name="processId">The process id returned by <see cref="Start"/>.</param>
        /// <returns>204 when stopped; 404 when the id is not a tracked instance.</returns>
        [HttpDelete("{processId:int}")]
        public IActionResult Stop(int processId)
        {
            // Only tracked processes can be stopped; unknown ids report not found.
            var isStopped = _domain.Launcher.Stop(processId);

            if (!isStopped)
            {
                return NotFound(new { error = $"No tracked peek process with id {processId}." });
            }

            return NoContent();
        }
    }
}

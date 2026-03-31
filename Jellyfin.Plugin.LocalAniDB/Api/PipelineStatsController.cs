using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LocalAniDB.Api
{
    [ApiController]
    [Authorize]
    [Route("LocalAniDB/Pipeline")]
    [Produces(MediaTypeNames.Application.Json)]
    public class PipelineStatsController : ControllerBase
    {
        /// <summary>
        /// Gets the current pipeline statistics across all stages.
        /// Includes queue depths, items processed, throughput (items/min), and estimated time remaining.
        /// </summary>
        [HttpGet("Stats")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public IActionResult GetStats()
        {
            var pipeline = Plugin.PipelineState;
            if (pipeline == null)
            {
                return StatusCode(503, new { error = "Pipeline not initialized" });
            }

            return Ok(pipeline.GetStats());
        }
    }
}

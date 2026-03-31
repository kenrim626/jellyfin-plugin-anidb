using System;
using System.Net.Http;
using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LocalAniDB.Api
{
    [ApiController]
    [Authorize]
    [Route("[controller]")]
    [Produces(MediaTypeNames.Application.Json)]
    public class LocalAniDBController : ControllerBase
    {
        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetStatus()
        {
            var config = Plugin.Instance.Configuration;
            var localApiUrl = config.LocalApiUrl.TrimEnd('/');
            var url = $"{localApiUrl}/api/status?maxDaily={config.MaxDailyRequests}&banCooldownHours={config.BanCooldownHours}";

            var httpClient = Plugin.Instance.GetHttpClient();
            var response = await httpClient.GetAsync(url);

            var content = await response.Content.ReadAsStringAsync();
            return Content(content, "application/json");
        }

        [HttpPost("ClearBan")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> ClearBan()
        {
            var config = Plugin.Instance.Configuration;
            var localApiUrl = config.LocalApiUrl.TrimEnd('/');
            var url = $"{localApiUrl}/api/clearban";

            var httpClient = Plugin.Instance.GetHttpClient();
            await httpClient.PostAsync(url, null);

            return NoContent();
        }
    }
}

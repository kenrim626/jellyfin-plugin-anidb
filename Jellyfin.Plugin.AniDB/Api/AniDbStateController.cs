using System;
using System.Net.Mime;
using Jellyfin.Plugin.AniDB.Providers.AniDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AniDB.Api
{
    [ApiController]
    [Authorize]
    [Route("[controller]")]
    [Produces(MediaTypeNames.Application.Json)]
    public class AniDBController : ControllerBase
    {
        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<AniDbStatusResponse> GetStatus()
        {
            var tracker = Plugin.Instance.RequestTracker;
            var config = Plugin.Instance.Configuration;

            return new AniDbStatusResponse
            {
                RequestsLast24h = tracker.RequestsInLast24Hours,
                MaxDailyRequests = config.MaxDailyRequests,
                IsBanned = tracker.IsBanned,
                BannedSince = tracker.BannedSince,
                EstimatedUnban = tracker.EstimatedUnban,
                BanCooldownHours = config.BanCooldownHours
            };
        }

        [HttpPost("ClearBan")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult ClearBan()
        {
            Plugin.Instance.RequestTracker.ClearBan();
            return NoContent();
        }
    }

    public class AniDbStatusResponse
    {
        public int RequestsLast24h { get; set; }
        public int MaxDailyRequests { get; set; }
        public bool IsBanned { get; set; }
        public DateTime? BannedSince { get; set; }
        public DateTime? EstimatedUnban { get; set; }
        public int BanCooldownHours { get; set; }
    }
}

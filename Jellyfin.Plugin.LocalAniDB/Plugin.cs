using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using Jellyfin.Plugin.LocalAniDB.Configuration;
using Jellyfin.Plugin.LocalAniDB.Providers.AniDB.Identity;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalAniDB
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Limits local API requests to 1 at a time to prevent flooding and file collisions.
        /// </summary>
        public static readonly SemaphoreSlim LocalApiSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Per-series locks to prevent concurrent writes to the same series.xml file.
        /// </summary>
        public static readonly ConcurrentDictionary<string, SemaphoreSlim> SeriesLocks = new();

        /// <summary>
        /// Tracks which series IDs have already been downloaded in the current scan cycle.
        /// Prevents redundant API calls when multiple providers (series, season, episode, image)
        /// all request the same series data during one refresh pass.
        /// Value is the UTC timestamp when the download completed.
        /// </summary>
        public static readonly ConcurrentDictionary<string, DateTime> SeriesDataFreshness = new();

        IHttpClientFactory _httpClientFactory;
        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger<AniDbTitleMatcher> matcherLogger,
            ILogger<AniDbTitleDownloader> downloaderLogger,
            IHttpClientFactory httpClientFactory)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _httpClientFactory = httpClientFactory;

            AniDbTitleMatcher.DefaultInstance = new AniDbTitleMatcher(
                matcherLogger,
                new AniDbTitleDownloader(downloaderLogger, applicationPaths));
        }

        public HttpClient GetHttpClient() {
            var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
            httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(Name, Version.ToString()));
            httpClient.Timeout = TimeSpan.FromSeconds(300);

            return httpClient;
        }

        /// <inheritdoc />
        public override string Name => Constants.PluginName;

        /// <inheritdoc />
        public override Guid Id => Guid.Parse(Constants.PluginGuid);

        public static Plugin Instance { get; private set; }

        /// <summary>
        /// Builds the query string with rate limit parameters for the local API.
        /// </summary>
        public string BuildRateLimitQueryString()
        {
            var config = Configuration;
            return $"autoRefresh={config.AutoRefresh.ToString().ToLower()}" +
                   $"&rateLimitMs={config.AniDbRateLimit}" +
                   $"&maxDaily={config.MaxDailyRequests}" +
                   $"&banCooldownHours={config.BanCooldownHours}";
        }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace)
                }
            };
        }
    }
}

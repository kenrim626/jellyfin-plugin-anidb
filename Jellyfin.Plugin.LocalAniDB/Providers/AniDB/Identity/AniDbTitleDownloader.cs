using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalAniDB.Providers.AniDB.Metadata;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalAniDB.Providers.AniDB.Identity
{
    /// <summary>
    /// The AniDbTitleDownloader class downloads the anime titles file from AniDB and stores it.
    /// </summary>
    public class AniDbTitleDownloader : IAniDbTitleDownloader
    {
        private readonly ILogger<AniDbTitleDownloader> _logger;

        public AniDbTitleDownloader(ILogger<AniDbTitleDownloader> logger, IApplicationPaths applicationPaths)
        {
            _logger = logger;
            Paths = GetDataPath(applicationPaths);
        }

        static AniDbTitleDownloader()
        {
        }

        public static string Paths { get; private set; }

        /// <summary>
        /// Gets the path to the anidb data folder.
        /// </summary>
        /// <param name="applicationPaths">The application paths.</param>
        /// <returns>The path to the anidb data folder.</returns>
        public static string GetDataPath(IApplicationPaths applicationPaths)
        {
            return Path.Combine(applicationPaths.CachePath, "anidb");
        }

        /// <summary>
        /// Load XML static| Too prevent EXCEPTIONS
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static async Task Load_static(CancellationToken cancellationToken)
        {
            var titlesFile = TitlesFilePath_;
            var titlesFileInfo = new FileInfo(titlesFile);

            // download titles if we do not already have them, or have not updated for a week
            if (!titlesFileInfo.Exists || (DateTime.UtcNow - titlesFileInfo.LastWriteTimeUtc).TotalDays > 7)
            {
                await DownloadTitles_static(titlesFile).ConfigureAwait(false);
            }
        }

        public async Task Load(CancellationToken cancellationToken)
        {
            var titlesFile = TitlesFilePath;
            var titlesFileInfo = new FileInfo(titlesFile);

            // download titles if we do not already have them, or have not updated for a week
            if (!titlesFileInfo.Exists || (DateTime.UtcNow - titlesFileInfo.LastWriteTimeUtc).TotalDays > 7)
            {
                await DownloadTitles(titlesFile).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Downloads an xml file from AniDB which contains all of the titles for every anime, and their IDs,
        /// and saves it to disk.
        /// </summary>
        /// <param name="titlesFile">The destination file name.</param>
        private Task DownloadTitles(string titlesFile)
        {
            _logger.LogDebug("Downloading new AniDB titles file.");
            return DownloadTitles_static(titlesFile);
        }

        /// <summary>
        /// static|Downloads an xml file from AniDB which contains all of the titles for every anime, and their IDs,
        /// and saves it to disk.
        /// </summary>
        /// <param name="titlesFile"></param>
        /// <returns></returns>
        private static async Task DownloadTitles_static(string titlesFile)
        {
            var config = Plugin.Instance.Configuration;
            var localApiUrl = config.LocalApiUrl.TrimEnd('/');
            var autoRefresh = config.AutoRefresh.ToString().ToLower();
            var url = $"{localApiUrl}/api/anime/titles?autoRefresh={autoRefresh}";

            await Plugin.LocalApiSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var httpClient = Plugin.Instance.GetHttpClient();
                using (var stream = await httpClient.GetStreamAsync(url).ConfigureAwait(false))
                using (var writer = File.Open(titlesFile, FileMode.Create, FileAccess.Write))
                {
                    await stream.CopyToAsync(writer).ConfigureAwait(false);
                }
            }
            finally
            {
                Plugin.LocalApiSemaphore.Release();
            }
        }

        public string TitlesFilePath
        {
            get
            {
                Directory.CreateDirectory(Paths);

                return Path.Combine(Paths, "titles.xml");
            }
        }

        /// <summary>
        /// Get the FilePath
        /// </summary>
        public static string TitlesFilePath_
        {
            get
            {
                Directory.CreateDirectory(Paths);

                return Path.Combine(Paths, "titles.xml");
            }
        }
    }
}

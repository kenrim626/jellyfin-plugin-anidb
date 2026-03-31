using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB
{
    public class AniDbApiResponseCache
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
        private readonly string _cacheDirectory;
        private readonly ILogger<AniDbApiResponseCache> _logger;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        public AniDbApiResponseCache(string cachePath, ILogger<AniDbApiResponseCache> logger)
        {
            _cacheDirectory = Path.Combine(cachePath, "anidb", "api-response-cache");
            _logger = logger;
        }

        public async Task<string> GetOrFetchAnimeDataAsync(string aid, Func<Task<string>> fetchFactory, CancellationToken cancellationToken)
        {
            if (!Plugin.Instance.Configuration.EnableApiResponseCache)
            {
                return await fetchFactory().ConfigureAwait(false);
            }

            var cacheFilePath = GetCacheFilePath(aid);

            // Fast path: check cache without locking
            var cached = TryReadCache(cacheFilePath);
            if (cached != null)
            {
                _logger.LogWarning("AniDB API response cache HIT for anime {Aid}", aid);
                return cached;
            }

            // Acquire per-ID lock to prevent parallel requests for the same anime
            var semaphore = _locks.GetOrAdd(aid, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Double-check after acquiring lock — another thread may have populated the cache
                cached = TryReadCache(cacheFilePath);
                if (cached != null)
                {
                    _logger.LogWarning("AniDB API response cache HIT for anime {Aid} (after lock)", aid);
                    return cached;
                }

                _logger.LogWarning("AniDB API response cache MISS for anime {Aid}, fetching from API", aid);
                var data = await fetchFactory().ConfigureAwait(false);

                // Write to cache (fetchFactory throws on API errors, so we only cache successful responses)
                Directory.CreateDirectory(_cacheDirectory);
                await File.WriteAllTextAsync(cacheFilePath, data, cancellationToken).ConfigureAwait(false);

                return data;
            }
            finally
            {
                semaphore.Release();
            }
        }

        private string GetCacheFilePath(string aid)
        {
            return Path.Combine(_cacheDirectory, aid + ".xml");
        }

        private static string TryReadCache(string cacheFilePath)
        {
            var fileInfo = new FileInfo(cacheFilePath);
            if (fileInfo.Exists && DateTime.UtcNow - fileInfo.LastWriteTimeUtc < CacheTtl)
            {
                return File.ReadAllText(cacheFilePath);
            }

            return null;
        }
    }
}

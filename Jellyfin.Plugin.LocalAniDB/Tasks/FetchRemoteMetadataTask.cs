using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalAniDB.Pipeline;
using Jellyfin.Plugin.LocalAniDB.Providers.AniDB;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalAniDB.Tasks
{
    /// <summary>
    /// Task 3: Drains the fetch queue and downloads series data from the local AniDB API.
    /// This is THE BOTTLENECK (~3-5 sec per item due to rate limiting).
    /// Runs frequently to keep the API saturated and process newly queued IDs quickly.
    /// </summary>
    public class FetchRemoteMetadataTask : IScheduledTask
    {
        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<FetchRemoteMetadataTask> _logger;

        public FetchRemoteMetadataTask(IApplicationPaths appPaths, ILogger<FetchRemoteMetadataTask> logger)
        {
            _appPaths = appPaths;
            _logger = logger;
        }

        public string Name => "3. AniDB: Fetch Remote Metadata";
        public string Key => "AniDb3FetchRemoteMetadata";
        public string Description => "Downloads metadata from AniDB (via local API) for all queued anime. This is rate-limited and may take several minutes for large batches.";
        public string Category => "AniDB Pipeline";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromMinutes(15).Ticks
                }
            };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var pipeline = Plugin.PipelineState;
            if (pipeline == null)
            {
                _logger.LogWarning("Pipeline state not initialized, skipping");
                return;
            }

            pipeline.SetRemoteFetchProcessing(true);
            pipeline.ResetApplyDedup();

            try
            {
                var items = pipeline.DrainFetchQueue();

                if (items.Count == 0)
                {
                    _logger.LogDebug("Fetch queue empty, nothing to do");
                    progress.Report(100);
                    return;
                }

                _logger.LogInformation("Fetching remote metadata for {Count} anime from local API", items.Count);

                int processed = 0;
                int fetched = 0;
                int failed = 0;
                int skipped = 0;

                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        // Double-check freshness — another task or provider may have fetched this already
                        if (AniDbDataService.IsCacheFresh(_appPaths, item.AniDbId))
                        {
                            _logger.LogDebug("Cache already fresh for {Name} (AniDB {Id}), promoting to apply queue", item.Name, item.AniDbId);
                            skipped++;
                        }
                        else
                        {
                            _logger.LogDebug("Fetching AniDB data for {Name} (AniDB {Id})", item.Name, item.AniDbId);
                            await AniDbDataService.GetSeriesData(_appPaths, item.AniDbId, _logger, cancellationToken).ConfigureAwait(false);
                            fetched++;
                        }

                        // Enqueue for metadata application
                        var cachePath = Path.Combine(
                            AniDbDataService.GetSeriesDataPath(_appPaths, item.AniDbId),
                            "series.xml");

                        pipeline.EnqueueApply(new MetadataReadyItem
                        {
                            ItemId = item.ItemId,
                            AniDbId = item.AniDbId,
                            Name = item.Name,
                            CachePath = cachePath
                        });

                        pipeline.RecordRemoteFetchItem();
                    }
                    catch (OperationCanceledException)
                    {
                        // Re-enqueue this item and all remaining items
                        pipeline.RequeueFetch(item);
                        for (int i = processed + 1; i < items.Count; i++)
                            pipeline.RequeueFetch(items[i]);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch metadata for {Name} (AniDB {Id}), re-enqueuing", item.Name, item.AniDbId);
                        pipeline.RecordRemoteFetchFailed();
                        pipeline.RequeueFetch(item);
                        failed++;
                    }

                    processed++;
                    progress.Report(100.0 * processed / items.Count);
                }

                _logger.LogInformation(
                    "Remote fetch complete: {Fetched} fetched, {Skipped} skipped (fresh), {Failed} failed out of {Total}",
                    fetched, skipped, failed, items.Count);

                progress.Report(100);
            }
            finally
            {
                pipeline.SetRemoteFetchProcessing(false);
            }
        }
    }
}

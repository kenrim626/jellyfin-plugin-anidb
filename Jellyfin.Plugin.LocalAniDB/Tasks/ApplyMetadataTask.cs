using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalAniDB.Pipeline;
using Jellyfin.Plugin.LocalAniDB.Providers;
using Jellyfin.Plugin.LocalAniDB.Providers.AniDB;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalAniDB.Tasks
{
    /// <summary>
    /// Task 4: Drains the apply queue and triggers a metadata refresh on each Jellyfin item
    /// so the warm cache is applied. All operations are local — no API calls.
    /// </summary>
    public class ApplyMetadataTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<ApplyMetadataTask> _logger;

        public ApplyMetadataTask(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            IApplicationPaths appPaths,
            ILogger<ApplyMetadataTask> logger)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _appPaths = appPaths;
            _logger = logger;
        }

        public string Name => "4. AniDB: Apply Metadata";
        public string Key => "AniDb4ApplyMetadata";
        public string Description => "Applies cached AniDB metadata to Jellyfin library items. Fast — reads from local cache only.";
        public string Category => "AniDB Pipeline";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromMinutes(10).Ticks
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

            pipeline.SetApplyProcessing(true);

            try
            {
                var items = pipeline.DrainApplyQueue();

                if (items.Count == 0)
                {
                    _logger.LogDebug("Apply queue empty, nothing to do");
                    progress.Report(100);
                    return;
                }

                _logger.LogInformation("Applying metadata for {Count} items from warm cache", items.Count);

                int processed = 0;
                int applied = 0;
                int failed = 0;

                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var libraryItem = _libraryManager.GetItemById(item.ItemId);
                        if (libraryItem == null)
                        {
                            _logger.LogDebug("Item {ItemId} no longer exists in library, skipping", item.ItemId);
                            processed++;
                            progress.Report(100.0 * processed / items.Count);
                            continue;
                        }

                        // Ensure the AniDB provider ID is set on the item
                        if (!libraryItem.ProviderIds.ContainsKey(ProviderNames.AniDb))
                        {
                            libraryItem.ProviderIds[ProviderNames.AniDb] = item.AniDbId;
                        }

                        // Verify cache file exists and is non-empty before triggering refresh
                        if (!File.Exists(item.CachePath) || new FileInfo(item.CachePath).Length == 0)
                        {
                            _logger.LogDebug("Cache file missing or empty for {Name} (AniDB {Id}), skipping", item.Name, item.AniDbId);
                            failed++;
                            pipeline.RecordApplyFailed();
                            processed++;
                            progress.Report(100.0 * processed / items.Count);
                            continue;
                        }

                        // Trigger a metadata refresh — providers will find warm cache and apply instantly
                        var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                        {
                            MetadataRefreshMode = MetadataRefreshMode.Default,
                            ImageRefreshMode = MetadataRefreshMode.Default,
                            ReplaceAllMetadata = false,
                            IsAutomated = true
                        };

                        await _providerManager.RefreshFullItem(libraryItem, refreshOptions, cancellationToken).ConfigureAwait(false);
                        applied++;
                        pipeline.RecordApplyItem();

                        _logger.LogDebug("Applied metadata for {Name} (AniDB {Id})", item.Name, item.AniDbId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to apply metadata for {Name} (AniDB {Id})", item.Name, item.AniDbId);
                        pipeline.RecordApplyFailed();
                        failed++;
                    }

                    processed++;
                    progress.Report(100.0 * processed / items.Count);
                }

                _logger.LogInformation(
                    "Metadata application complete: {Applied} applied, {Failed} failed out of {Total}",
                    applied, failed, items.Count);

                progress.Report(100);
            }
            finally
            {
                pipeline.SetApplyProcessing(false);
            }
        }
    }
}

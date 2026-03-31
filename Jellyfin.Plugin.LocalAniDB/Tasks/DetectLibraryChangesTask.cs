using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LocalAniDB.Pipeline;
using Jellyfin.Plugin.LocalAniDB.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalAniDB.Tasks
{
    /// <summary>
    /// Task 1: Walks configured anime library folders, detects new/modified items,
    /// and enqueues them for classification. Integrates with the Scheduled Tasks dashboard.
    /// </summary>
    public class DetectLibraryChangesTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<DetectLibraryChangesTask> _logger;

        public DetectLibraryChangesTask(ILibraryManager libraryManager, ILogger<DetectLibraryChangesTask> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public string Name => "1. AniDB: Detect Library Changes";
        public string Key => "AniDb1DetectLibraryChanges";
        public string Description => "Scans anime library folders for new or modified items and queues them for AniDB metadata processing.";
        public string Category => "AniDB Pipeline";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.StartupTrigger
                },
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(6).Ticks
                }
            };
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var pipeline = Plugin.PipelineState;
            if (pipeline == null)
            {
                _logger.LogWarning("Pipeline state not initialized, skipping");
                return Task.CompletedTask;
            }

            pipeline.SetChangeDetectionProcessing(true);
            pipeline.ResetClassificationDedup();

            try
            {
                // Gather all series and movies from the library
                var items = GetAnimeLibraryItems();
                var totalItems = items.Count;

                if (totalItems == 0)
                {
                    _logger.LogInformation("No anime library items found");
                    progress.Report(100);
                    return Task.CompletedTask;
                }

                _logger.LogInformation("Scanning {Count} library items for changes", totalItems);

                int processed = 0;
                int enqueued = 0;

                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var itemPath = item.Path;
                    if (string.IsNullOrEmpty(itemPath))
                    {
                        processed++;
                        continue;
                    }

                    var changeType = DetectChange(item, itemPath);
                    if (changeType != null)
                    {
                        var changeItem = new LibraryChangeItem
                        {
                            ItemId = item.Id,
                            Path = itemPath,
                            Name = item.Name,
                            ChangeType = changeType.Value
                        };

                        if (pipeline.EnqueueClassification(changeItem))
                        {
                            enqueued++;
                        }

                        pipeline.RecordChangeDetectionItem();
                    }

                    // Update known state
                    pipeline.KnownFolderDates[itemPath] = item.DateModified;

                    processed++;
                    progress.Report(100.0 * processed / totalItems);
                }

                _logger.LogInformation(
                    "Library change detection complete: {Processed} items scanned, {Enqueued} changes queued for classification",
                    processed, enqueued);

                pipeline.ForcePersist();

                progress.Report(100);
            }
            finally
            {
                pipeline.SetChangeDetectionProcessing(false);
            }

            return Task.CompletedTask;
        }

        private List<BaseItem> GetAnimeLibraryItems()
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[]
                {
                    BaseItemKind.Series,
                    BaseItemKind.Movie
                },
                IsVirtualItem = false,
                Recursive = true
            };

            return _libraryManager.GetItemList(query).ToList();
        }

        private ChangeType? DetectChange(BaseItem item, string itemPath)
        {
            // Check if we've seen this path before
            if (!Plugin.PipelineState.KnownFolderDates.TryGetValue(itemPath, out var lastKnownDate))
            {
                return ChangeType.New;
            }

            // Check if modification date changed
            if (item.DateModified > lastKnownDate)
            {
                return ChangeType.Modified;
            }

            // Check if it has no AniDB provider ID yet (might have been missed)
            if (!item.ProviderIds.ContainsKey(ProviderNames.AniDb))
            {
                return ChangeType.New;
            }

            return null;
        }
    }
}

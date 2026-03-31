using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.LocalAniDB.Pipeline;
using Jellyfin.Plugin.LocalAniDB.Providers;
using Jellyfin.Plugin.LocalAniDB.Providers.AniDB;
using Jellyfin.Plugin.LocalAniDB.Providers.AniDB.Identity;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalAniDB.Tasks
{
    /// <summary>
    /// Task 2: Drains the classification queue. For each item, checks existing metadata /
    /// .nfo files for an AniDB ID, falls back to fuzzy title matching, and enqueues items
    /// that need a remote fetch.
    /// </summary>
    public class ClassifyAndResolveIdsTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<ClassifyAndResolveIdsTask> _logger;

        public ClassifyAndResolveIdsTask(
            ILibraryManager libraryManager,
            IApplicationPaths appPaths,
            ILogger<ClassifyAndResolveIdsTask> logger)
        {
            _libraryManager = libraryManager;
            _appPaths = appPaths;
            _logger = logger;
        }

        public string Name => "2. AniDB: Classify & Resolve IDs";
        public string Key => "AniDb2ClassifyResolveIds";
        public string Description => "Resolves AniDB IDs for queued library items by checking existing metadata, .nfo files, and fuzzy title matching. Queues items needing remote data fetch.";
        public string Category => "AniDB Pipeline";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromMinutes(30).Ticks
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

            pipeline.SetClassificationProcessing(true);
            pipeline.ResetFetchDedup();

            try
            {
                var items = pipeline.DrainClassificationQueue();

                if (items.Count == 0)
                {
                    _logger.LogDebug("Classification queue empty, nothing to do");
                    progress.Report(100);
                    return;
                }

                _logger.LogInformation("Classifying {Count} items for AniDB ID resolution", items.Count);

                int processed = 0;
                int resolved = 0;
                int skipped = 0;

                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var aniDbId = await ResolveAniDbId(item, cancellationToken).ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(aniDbId))
                        {
                            // Check if cache is fresh — if so, skip the fetch stage
                            if (AniDbDataService.IsCacheFresh(_appPaths, aniDbId))
                            {
                                _logger.LogDebug("Cache is fresh for {Name} (AniDB {Id}), skipping fetch", item.Name, aniDbId);
                                skipped++;
                            }
                            else
                            {
                                pipeline.EnqueueFetch(new AniDbFetchItem
                                {
                                    ItemId = item.ItemId,
                                    AniDbId = aniDbId,
                                    Name = item.Name
                                });
                                resolved++;
                            }
                        }
                        else
                        {
                            _logger.LogDebug("Could not resolve AniDB ID for {Name} at {Path}", item.Name, item.Path);
                        }

                        pipeline.RecordClassificationItem();
                    }
                    catch (OperationCanceledException)
                    {
                        // Re-enqueue this item and all remaining items
                        pipeline.RequeueClassification(item);
                        for (int i = processed + 1; i < items.Count; i++)
                            pipeline.RequeueClassification(items[i]);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to classify {Name}, re-enqueuing", item.Name);
                        pipeline.RecordClassificationFailed();
                        pipeline.RequeueClassification(item);
                    }

                    processed++;
                    progress.Report(100.0 * processed / items.Count);
                }

                _logger.LogInformation(
                    "Classification complete: {Processed} processed, {Resolved} queued for fetch, {Skipped} skipped (fresh cache)",
                    processed, resolved, skipped);

                progress.Report(100);
            }
            finally
            {
                pipeline.SetClassificationProcessing(false);
            }
        }

        private async Task<string> ResolveAniDbId(LibraryChangeItem item, CancellationToken cancellationToken)
        {
            // 1. Check if the Jellyfin item already has an AniDB provider ID
            var libraryItem = _libraryManager.GetItemById(item.ItemId);
            if (libraryItem != null)
            {
                var existingId = libraryItem.ProviderIds.GetOrDefault(ProviderNames.AniDb);
                if (!string.IsNullOrEmpty(existingId))
                {
                    return existingId;
                }
            }

            // 2. Check for .nfo file with AniDB ID
            var nfoId = TryReadAniDbIdFromNfo(item.Path);
            if (!string.IsNullOrEmpty(nfoId))
            {
                _logger.LogDebug("Resolved AniDB ID {Id} from .nfo for {Name}", nfoId, item.Name);
                return nfoId;
            }

            // 3. Fall back to fuzzy title matching against titles.xml
            var name = item.Name;
            if (!string.IsNullOrEmpty(name))
            {
                var matchedId = await Equals_check.XmlFindId(name, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(matchedId))
                {
                    _logger.LogDebug("Fuzzy matched AniDB ID {Id} for {Name}", matchedId, name);
                    return matchedId;
                }
            }

            return null;
        }

        /// <summary>
        /// Attempts to read an AniDB ID from .nfo files adjacent to the item path.
        /// Looks for tvshow.nfo (series) or {filename}.nfo (movies) and parses for anidbid.
        /// </summary>
        private string TryReadAniDbIdFromNfo(string itemPath)
        {
            if (string.IsNullOrEmpty(itemPath))
                return null;

            try
            {
                // For series: look for tvshow.nfo in the series folder
                var nfoPath = Path.Combine(itemPath, "tvshow.nfo");
                if (!File.Exists(nfoPath))
                {
                    // For movies: look for {filename}.nfo next to the file
                    if (File.Exists(itemPath))
                    {
                        nfoPath = Path.ChangeExtension(itemPath, ".nfo");
                    }
                }

                if (!File.Exists(nfoPath))
                    return null;

                return ParseNfoForAniDbId(nfoPath);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private string ParseNfoForAniDbId(string nfoPath)
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    CheckCharacters = false,
                    IgnoreProcessingInstructions = true,
                    IgnoreComments = true,
                    ValidationType = ValidationType.None
                };

                using var stream = File.OpenRead(nfoPath);
                using var reader = XmlReader.Create(stream, settings);

                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        // Look for <anidbid>12345</anidbid> or providerids with AniDB
                        if (reader.Name.Equals("anidbid", StringComparison.OrdinalIgnoreCase))
                        {
                            return reader.ReadElementContentAsString();
                        }

                        // Also check uniqueid elements: <uniqueid type="anidb">12345</uniqueid>
                        if (reader.Name.Equals("uniqueid", StringComparison.OrdinalIgnoreCase))
                        {
                            var type = reader.GetAttribute("type");
                            if (type != null && type.Equals("anidb", StringComparison.OrdinalIgnoreCase))
                            {
                                return reader.ReadElementContentAsString();
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Malformed NFO — ignore
            }

            return null;
        }
    }
}

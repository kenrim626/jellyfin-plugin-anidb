using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalAniDB.Pipeline
{
    /// <summary>
    /// Manages the pipeline queues, deduplication, statistics tracking, and persistence.
    /// Singleton — lives on Plugin.PipelineState.
    /// </summary>
    public class PipelineStateService
    {
        private readonly ILogger _logger;
        private readonly string _persistPath;
        private readonly object _persistLock = new();
        private Timer _persistTimer;

        // ── Queues ────────────────────────────────────────────────────────────────
        public Channel<LibraryChangeItem> ClassificationQueue { get; } = Channel.CreateUnbounded<LibraryChangeItem>();
        public Channel<AniDbFetchItem> FetchQueue { get; } = Channel.CreateUnbounded<AniDbFetchItem>();
        public Channel<MetadataReadyItem> ApplyQueue { get; } = Channel.CreateUnbounded<MetadataReadyItem>();

        // ── Deduplication ─────────────────────────────────────────────────────────
        private readonly ConcurrentDictionary<string, byte> _classificationSeen = new();
        private readonly ConcurrentDictionary<string, byte> _fetchSeen = new();
        private readonly ConcurrentDictionary<string, byte> _applySeen = new();

        // ── Stats per stage ───────────────────────────────────────────────────────
        private readonly StageTracker _changeDetection = new("Change Detection");
        private readonly StageTracker _classification = new("Classification");
        private readonly StageTracker _remoteFetch = new("Remote Fetch");
        private readonly StageTracker _applyMetadata = new("Apply Metadata");

        // ── Persisted known-state (last scan dates per folder) ────────────────────
        public ConcurrentDictionary<string, DateTime> KnownFolderDates { get; private set; } = new();

        public PipelineStateService(string dataPath, ILogger logger)
        {
            _logger = logger;
            _persistPath = Path.Combine(dataPath, "anidb-pipeline-state.json");

            LoadPersistedState();

            // Debounced persistence — flush at most every 10 seconds
            _persistTimer = new Timer(_ => PersistState(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        }

        // ── Enqueue helpers with deduplication ────────────────────────────────────

        public bool EnqueueClassification(LibraryChangeItem item)
        {
            var key = item.Path ?? item.ItemId.ToString();
            if (!_classificationSeen.TryAdd(key, 0))
                return false;

            _changeDetection.RecordEnqueued();
            ClassificationQueue.Writer.TryWrite(item);
            RequestPersist();
            return true;
        }

        public bool EnqueueFetch(AniDbFetchItem item)
        {
            if (!_fetchSeen.TryAdd(item.AniDbId, 0))
                return false;

            _classification.RecordEnqueued();
            FetchQueue.Writer.TryWrite(item);
            RequestPersist();
            return true;
        }

        public bool EnqueueApply(MetadataReadyItem item)
        {
            var key = item.AniDbId + "|" + item.ItemId;
            if (!_applySeen.TryAdd(key, 0))
                return false;

            _remoteFetch.RecordEnqueued();
            ApplyQueue.Writer.TryWrite(item);
            RequestPersist();
            return true;
        }

        // ── Drain helpers (used by tasks) ─────────────────────────────────────────

        public List<LibraryChangeItem> DrainClassificationQueue(int maxItems = int.MaxValue)
        {
            var items = new List<LibraryChangeItem>();
            while (items.Count < maxItems && ClassificationQueue.Reader.TryRead(out var item))
            {
                items.Add(item);
            }
            return items;
        }

        public List<AniDbFetchItem> DrainFetchQueue(int maxItems = int.MaxValue)
        {
            var items = new List<AniDbFetchItem>();
            while (items.Count < maxItems && FetchQueue.Reader.TryRead(out var item))
            {
                items.Add(item);
            }
            return items;
        }

        public List<MetadataReadyItem> DrainApplyQueue(int maxItems = int.MaxValue)
        {
            var items = new List<MetadataReadyItem>();
            while (items.Count < maxItems && ApplyQueue.Reader.TryRead(out var item))
            {
                items.Add(item);
            }
            return items;
        }

        // ── Stats recording ───────────────────────────────────────────────────────

        public void RecordChangeDetectionItem() => _changeDetection.RecordProcessed();
        public void RecordChangeDetectionFailed() => _changeDetection.RecordFailed();
        public void SetChangeDetectionProcessing(bool v) => _changeDetection.IsProcessing = v;

        public void RecordClassificationItem() => _classification.RecordProcessed();
        public void RecordClassificationFailed() => _classification.RecordFailed();
        public void SetClassificationProcessing(bool v) => _classification.IsProcessing = v;

        public void RecordRemoteFetchItem() => _remoteFetch.RecordProcessed();
        public void RecordRemoteFetchFailed() => _remoteFetch.RecordFailed();
        public void SetRemoteFetchProcessing(bool v) => _remoteFetch.IsProcessing = v;

        public void RecordApplyItem() => _applyMetadata.RecordProcessed();
        public void RecordApplyFailed() => _applyMetadata.RecordFailed();
        public void SetApplyProcessing(bool v) => _applyMetadata.IsProcessing = v;

        // ── Reset dedup sets (allows re-queueing in next cycle) ───────────────────
        public void ResetClassificationDedup() => _classificationSeen.Clear();
        public void ResetFetchDedup() => _fetchSeen.Clear();
        public void ResetApplyDedup() => _applySeen.Clear();

        // ── Stats snapshot ────────────────────────────────────────────────────────

        public PipelineStats GetStats()
        {
            return new PipelineStats
            {
                ChangeDetection = _changeDetection.ToQueueStats(0), // Task 1 doesn't use a channel queue
                Classification = _classification.ToQueueStats(ClassificationQueue.Reader.Count),
                RemoteFetch = _remoteFetch.ToQueueStats(FetchQueue.Reader.Count),
                ApplyMetadata = _applyMetadata.ToQueueStats(ApplyQueue.Reader.Count)
            };
        }

        // ── Persistence ───────────────────────────────────────────────────────────

        private volatile bool _persistRequested;

        private void RequestPersist()
        {
            _persistRequested = true;
        }

        private void PersistState()
        {
            if (!_persistRequested)
                return;
            _persistRequested = false;

            try
            {
                var state = new PersistedPipelineState
                {
                    KnownFolderDates = new Dictionary<string, DateTime>(KnownFolderDates),
                    Stats = new PersistedStats
                    {
                        ChangeDetectionProcessed = _changeDetection.TotalProcessed,
                        ClassificationProcessed = _classification.TotalProcessed,
                        RemoteFetchProcessed = _remoteFetch.TotalProcessed,
                        ApplyProcessed = _applyMetadata.TotalProcessed,
                    }
                };

                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });

                lock (_persistLock)
                {
                    var dir = Path.GetDirectoryName(_persistPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    File.WriteAllText(_persistPath, json);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist pipeline state");
            }
        }

        private void LoadPersistedState()
        {
            try
            {
                if (!File.Exists(_persistPath))
                    return;

                var json = File.ReadAllText(_persistPath);
                var state = JsonSerializer.Deserialize<PersistedPipelineState>(json);
                if (state == null)
                    return;

                if (state.KnownFolderDates != null)
                {
                    KnownFolderDates = new ConcurrentDictionary<string, DateTime>(state.KnownFolderDates);
                }

                _logger.LogInformation(
                    "Loaded pipeline state: {FolderCount} known folders, previous run processed " +
                    "{ChangeDetection}/{Classification}/{Fetch}/{Apply} items across stages",
                    KnownFolderDates.Count,
                    state.Stats?.ChangeDetectionProcessed ?? 0,
                    state.Stats?.ClassificationProcessed ?? 0,
                    state.Stats?.RemoteFetchProcessed ?? 0,
                    state.Stats?.ApplyProcessed ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load persisted pipeline state, starting fresh");
            }
        }

        public void ForcePersist()
        {
            _persistRequested = true;
            PersistState();
        }

        // ── Inner types ───────────────────────────────────────────────────────────

        private class StageTracker
        {
            private readonly string _name;
            private readonly Stopwatch _window = Stopwatch.StartNew();
            private int _windowProcessed;

            public StageTracker(string name) => _name = name;

            public int TotalEnqueued;
            public int TotalProcessed;
            public int TotalFailed;
            public volatile bool IsProcessing;
            public DateTime? LastProcessedAtUtc;

            public void RecordEnqueued() => Interlocked.Increment(ref TotalEnqueued);

            public void RecordProcessed()
            {
                Interlocked.Increment(ref TotalProcessed);
                Interlocked.Increment(ref _windowProcessed);
                LastProcessedAtUtc = DateTime.UtcNow;
            }

            public void RecordFailed()
            {
                Interlocked.Increment(ref TotalFailed);
                Interlocked.Increment(ref _windowProcessed);
                LastProcessedAtUtc = DateTime.UtcNow;
            }

            public QueueStats ToQueueStats(int currentQueueDepth)
            {
                var elapsed = _window.Elapsed;
                double itemsPerMinute = 0;
                if (elapsed.TotalMinutes > 0 && _windowProcessed > 0)
                {
                    itemsPerMinute = Math.Round(_windowProcessed / elapsed.TotalMinutes, 1);
                }

                string eta = "N/A";
                if (itemsPerMinute > 0 && currentQueueDepth > 0)
                {
                    var minutes = currentQueueDepth / itemsPerMinute;
                    eta = TimeSpan.FromMinutes(minutes).ToString(@"hh\:mm\:ss");
                }

                return new QueueStats
                {
                    StageName = _name,
                    Queued = currentQueueDepth,
                    Processed = TotalProcessed,
                    Failed = TotalFailed,
                    IsProcessing = IsProcessing,
                    ItemsPerMinute = itemsPerMinute,
                    EstimatedTimeRemaining = eta,
                    LastProcessedAtUtc = LastProcessedAtUtc
                };
            }
        }

        private class PersistedPipelineState
        {
            public Dictionary<string, DateTime> KnownFolderDates { get; set; }
            public PersistedStats Stats { get; set; }
        }

        private class PersistedStats
        {
            public int ChangeDetectionProcessed { get; set; }
            public int ClassificationProcessed { get; set; }
            public int RemoteFetchProcessed { get; set; }
            public int ApplyProcessed { get; set; }
        }
    }
}

using System;

namespace Jellyfin.Plugin.LocalAniDB.Pipeline
{
    public enum ChangeType
    {
        New,
        Modified,
        Deleted
    }

    /// <summary>
    /// Queued by Task 1 (DetectLibraryChanges) when a library folder has changed.
    /// </summary>
    public class LibraryChangeItem
    {
        public Guid ItemId { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public ChangeType ChangeType { get; set; }
        public DateTime DetectedAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Queued by Task 2 (ClassifyAndResolveIds) when an AniDB ID needs remote data fetched.
    /// </summary>
    public class AniDbFetchItem
    {
        public Guid ItemId { get; set; }
        public string AniDbId { get; set; }
        public string Name { get; set; }
        public DateTime QueuedAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Queued by Task 3 (FetchRemoteMetadata) when cached data is ready to be applied to a Jellyfin item.
    /// </summary>
    public class MetadataReadyItem
    {
        public Guid ItemId { get; set; }
        public string AniDbId { get; set; }
        public string Name { get; set; }
        public string CachePath { get; set; }
        public DateTime ReadyAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Statistics for a single pipeline queue stage.
    /// </summary>
    public class QueueStats
    {
        public string StageName { get; set; }
        public int Queued { get; set; }
        public int Processed { get; set; }
        public int Failed { get; set; }
        public bool IsProcessing { get; set; }
        public double ItemsPerMinute { get; set; }
        public string EstimatedTimeRemaining { get; set; }
        public DateTime? LastProcessedAtUtc { get; set; }
    }

    /// <summary>
    /// Full pipeline statistics across all stages.
    /// </summary>
    public class PipelineStats
    {
        public QueueStats ChangeDetection { get; set; } = new() { StageName = "Change Detection" };
        public QueueStats Classification { get; set; } = new() { StageName = "Classification" };
        public QueueStats RemoteFetch { get; set; } = new() { StageName = "Remote Fetch" };
        public QueueStats ApplyMetadata { get; set; } = new() { StageName = "Apply Metadata" };
    }
}

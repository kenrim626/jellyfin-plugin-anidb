using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB
{
    public class AniDbRequestTracker
    {
        private static readonly TimeSpan RetryIdCooldown = TimeSpan.FromMinutes(15);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly string _stateFilePath;
        private readonly ILogger<AniDbRequestTracker> _logger;
        private readonly object _lock = new();

        private AniDbTrackerState _state;

        public AniDbRequestTracker(string dataFolderPath, ILogger<AniDbRequestTracker> logger)
        {
            _stateFilePath = Path.Combine(dataFolderPath, "anidb-state.json");
            _logger = logger;
            _state = LoadState();
        }

        public bool IsBanned
        {
            get
            {
                lock (_lock)
                {
                    if (_state.BannedSince == null)
                    {
                        return false;
                    }

                    var cooldownHours = Plugin.Instance.Configuration.BanCooldownHours;
                    if (DateTime.UtcNow - _state.BannedSince.Value > TimeSpan.FromHours(cooldownHours))
                    {
                        _logger.LogInformation("AniDB ban cooldown of {Hours}h has expired, resuming requests", cooldownHours);
                        _state.BannedSince = null;
                        SaveState();
                        return false;
                    }

                    return true;
                }
            }
        }

        public DateTime? BannedSince
        {
            get
            {
                lock (_lock)
                {
                    return _state.BannedSince;
                }
            }
        }

        public DateTime? EstimatedUnban
        {
            get
            {
                lock (_lock)
                {
                    if (_state.BannedSince == null)
                    {
                        return null;
                    }

                    return _state.BannedSince.Value.AddHours(Plugin.Instance.Configuration.BanCooldownHours);
                }
            }
        }

        public int RequestsInLast24Hours
        {
            get
            {
                lock (_lock)
                {
                    PruneOldTimestamps();
                    return _state.RequestTimestamps.Count;
                }
            }
        }

        public void RecordRequest()
        {
            lock (_lock)
            {
                _state.RequestTimestamps.Add(DateTime.UtcNow);
                PruneOldTimestamps();
                SaveState();
            }
        }

        public void SetBanned()
        {
            lock (_lock)
            {
                if (_state.BannedSince != null)
                {
                    return;
                }

                _state.BannedSince = DateTime.UtcNow;
                _logger.LogError(
                    "AniDB IP ban detected. All requests will be blocked for {Hours} hours (until {Until:u})",
                    Plugin.Instance.Configuration.BanCooldownHours,
                    _state.BannedSince.Value.AddHours(Plugin.Instance.Configuration.BanCooldownHours));
                SaveState();
            }
        }

        public void ClearBan()
        {
            lock (_lock)
            {
                _state.BannedSince = null;
                SaveState();
                _logger.LogInformation("AniDB ban state cleared manually");
            }
        }

        public bool CanRetryForId(string id)
        {
            lock (_lock)
            {
                if (_state.LastAttemptedById.TryGetValue(id, out var lastAttempt))
                {
                    return DateTime.UtcNow - lastAttempt > RetryIdCooldown;
                }

                return true;
            }
        }

        public void RecordAttemptForId(string id)
        {
            lock (_lock)
            {
                _state.LastAttemptedById[id] = DateTime.UtcNow;
                PruneOldAttempts();
                SaveState();
            }
        }

        public void ThrowIfBanned()
        {
            if (IsBanned)
            {
                throw new AniDbBannedException(
                    $"AniDB IP ban active since {_state.BannedSince:u}. " +
                    $"Requests blocked until estimated unban at {EstimatedUnban:u}. " +
                    $"Configure 'Ban Cooldown Hours' in AniDB plugin settings to adjust.");
            }
        }

        private void PruneOldTimestamps()
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            _state.RequestTimestamps.RemoveAll(t => t < cutoff);
        }

        private void PruneOldAttempts()
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            var expired = _state.LastAttemptedById
                .Where(kvp => kvp.Value < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired)
            {
                _state.LastAttemptedById.Remove(key);
            }
        }

        private AniDbTrackerState LoadState()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    var json = File.ReadAllText(_stateFilePath);
                    var state = JsonSerializer.Deserialize<AniDbTrackerState>(json, JsonOptions);
                    if (state != null)
                    {
                        return state;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load AniDB state file, starting fresh");
            }

            return new AniDbTrackerState();
        }

        private void SaveState()
        {
            try
            {
                var directory = Path.GetDirectoryName(_stateFilePath);
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(_state, JsonOptions);
                File.WriteAllText(_stateFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save AniDB state file");
            }
        }
    }

    public class AniDbTrackerState
    {
        public DateTime? BannedSince { get; set; }
        public List<DateTime> RequestTimestamps { get; set; } = new();
        public Dictionary<string, DateTime> LastAttemptedById { get; set; } = new();
    }

    public class AniDbBannedException : Exception
    {
        public AniDbBannedException(string message) : base(message) { }
    }
}

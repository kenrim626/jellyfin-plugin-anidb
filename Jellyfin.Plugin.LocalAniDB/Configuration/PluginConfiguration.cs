using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LocalAniDB.Configuration
{
    public enum TitlePreferenceType
    {
        /// <summary>
        /// Use titles in the local metadata language.
        /// </summary>
        Localized,

        /// <summary>
        /// Use titles in Japanese.
        /// </summary>
        Japanese,

        /// <summary>
        /// Use titles in Japanese romaji.
        /// </summary>
        JapaneseRomaji
    }

    public enum AnimeDefaultGenreType
    {
        None, Anime, Animation
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public PluginConfiguration()
        {
            TitlePreference = TitlePreferenceType.Localized;
            OriginalTitlePreference = TitlePreferenceType.JapaneseRomaji;
            IgnoreSeason = false;
            TitleSimilarityThreshold = 50;
            MaxGenres = 5;
            TidyGenreList = true;
            TitleCaseGenres = false;
            AnimeDefaultGenre = AnimeDefaultGenreType.Anime;
            AniDbRateLimit = 2000;
            AniDbReplaceGraves = true;
            BanCooldownHours = 24;
            MaxDailyRequests = 500;
            StubCooldownMinutes = 1;
            LocalApiUrl = "http://localhost:5170";
            AutoRefresh = false;
        }

        public TitlePreferenceType TitlePreference { get; set; }

        public TitlePreferenceType OriginalTitlePreference { get; set; }

        public bool IgnoreSeason { get; set; }

        public int TitleSimilarityThreshold { get; set; }

        public int MaxGenres { get; set; }

        public bool TidyGenreList { get; set; }

        public bool TitleCaseGenres { get; set; }

        public AnimeDefaultGenreType AnimeDefaultGenre { get; set; }

        public int AniDbRateLimit { get; set; }

        public bool AniDbReplaceGraves { get; set; }

        public int BanCooldownHours { get; set; }

        public int MaxDailyRequests { get; set; }

        /// <summary>
        /// How many minutes to wait before retrying a stub (failed fetch with no data). 0 = disabled (retry immediately).
        /// </summary>
        public int StubCooldownMinutes { get; set; }

        public string LocalApiUrl { get; set; }

        public bool AutoRefresh { get; set; }

        public bool EnablePipelineTasks { get; set; } = true;
        public int PipelineDetectIntervalHours { get; set; } = 6;
        public int PipelineClassifyIntervalMinutes { get; set; } = 30;
        public int PipelineFetchIntervalMinutes { get; set; } = 15;
        public int PipelineApplyIntervalMinutes { get; set; } = 10;
    }
}

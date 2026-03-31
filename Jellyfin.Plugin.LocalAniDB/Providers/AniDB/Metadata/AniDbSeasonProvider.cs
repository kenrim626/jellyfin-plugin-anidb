using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Xml;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalAniDB.Providers.AniDB.Metadata
{
    public class AniDbSeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>
    {
        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<AniDbSeasonProvider> _logger;

        public AniDbSeasonProvider(IApplicationPaths appPaths, ILogger<AniDbSeasonProvider> logger)
        {
            _appPaths = appPaths;
            _logger = logger;
        }

        public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Season>
            {
                HasMetadata = true,
                Item = new Season
                {
                    Name = info.Name,
                    IndexNumber = info.IndexNumber
                }
            };

            var seriesId = info.ProviderIds.GetOrDefault(ProviderNames.AniDb)
                ?? info.SeriesProviderIds.GetOrDefault(ProviderNames.AniDb);
            if (seriesId == null)
            {
                return result;
            }

            result.Item.ProviderIds.Add(ProviderNames.AniDb, seriesId);

            // Read only the fields we need directly from the cached series.xml
            // instead of running the full series provider (which downloads, extracts episodes, extracts cast, etc.)
            var seriesFolder = AniDbSeriesProvider.GetSeriesDataPath(_appPaths, seriesId);
            var seriesXmlPath = Path.Combine(seriesFolder, "series.xml");

            if (!File.Exists(seriesXmlPath) || new FileInfo(seriesXmlPath).Length == 0)
            {
                // Data not cached yet — trigger one download
                var downloaded = await AniDbSeriesProvider.GetSeriesData(_appPaths, seriesId, cancellationToken).ConfigureAwait(false);
                seriesXmlPath = downloaded;
            }

            if (!File.Exists(seriesXmlPath) || new FileInfo(seriesXmlPath).Length == 0)
            {
                return result;
            }

            await ParseSeasonFromSeriesXml(seriesXmlPath, result.Item, info.MetadataLanguage).ConfigureAwait(false);

            return result;
        }

        /// <summary>
        /// Lightweight parse of series.xml — extracts only the fields needed for a Season.
        /// Skips episodes, characters, creators (except Animation Work for studios), resources, etc.
        /// </summary>
        private async Task ParseSeasonFromSeriesXml(string seriesXmlPath, Season season, string preferredMetadataLanguage)
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            var titles = new List<Title>();
            var genres = new List<(string Name, int Weight)>();
            var studios = new List<string>();

            using (var streamReader = File.Open(seriesXmlPath, FileMode.Open, FileAccess.Read))
            using (var reader = XmlReader.Create(streamReader, settings))
            {
                await reader.MoveToContentAsync().ConfigureAwait(false);

                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.Name)
                        {
                            case "startdate":
                                var val = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(val) &&
                                    DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime startDate))
                                {
                                    season.PremiereDate = startDate.ToUniversalTime();
                                }
                                break;

                            case "enddate":
                                var endVal = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(endVal) &&
                                    DateTime.TryParse(endVal, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime endDate))
                                {
                                    season.EndDate = endDate.ToUniversalTime();
                                }
                                break;

                            case "titles":
                                using (var subtree = reader.ReadSubtree())
                                {
                                    while (await subtree.ReadAsync().ConfigureAwait(false))
                                    {
                                        if (subtree.NodeType == XmlNodeType.Element && subtree.Name == "title")
                                        {
                                            var language = subtree.GetAttribute("xml:lang");
                                            var type = subtree.GetAttribute("type");
                                            var name = await subtree.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                            titles.Add(new Title { Language = language, Type = type, Name = name });
                                        }
                                    }
                                }
                                break;

                            case "description":
                                var description = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                description = description.TrimStart('*').Trim();
                                season.Overview = AniDbSeriesProvider.ReplaceNewLine(AniDbSeriesProvider.StripAniDbLinks(
                                    Plugin.Instance.Configuration.AniDbReplaceGraves ? description.Replace('`', '\'') : description));
                                break;

                            case "ratings":
                                using (var subtree = reader.ReadSubtree())
                                {
                                    while (await subtree.ReadAsync().ConfigureAwait(false))
                                    {
                                        if (subtree.NodeType == XmlNodeType.Element && subtree.Name == "permanent")
                                        {
                                            if (float.TryParse(subtree.ReadElementContentAsString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out float rating))
                                            {
                                                season.CommunityRating = rating;
                                            }
                                        }
                                    }
                                }
                                break;

                            case "creators":
                                using (var subtree = reader.ReadSubtree())
                                {
                                    while (await subtree.ReadAsync().ConfigureAwait(false))
                                    {
                                        if (subtree.NodeType == XmlNodeType.Element && subtree.Name == "name")
                                        {
                                            var type = subtree.GetAttribute("type");
                                            var name = await subtree.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                            if (type == "Animation Work")
                                            {
                                                studios.Add(name);
                                            }
                                        }
                                    }
                                }
                                break;

                            case "tags":
                                using (var subtree = reader.ReadSubtree())
                                {
                                    while (await subtree.ReadAsync().ConfigureAwait(false))
                                    {
                                        if (subtree.NodeType == XmlNodeType.Element && subtree.Name == "tag")
                                        {
                                            if (!int.TryParse(subtree.GetAttribute("weight"), out int weight))
                                                weight = 0;

                                            if (weight >= 400)
                                            {
                                                using (var tagSubtree = subtree.ReadSubtree())
                                                {
                                                    while (await tagSubtree.ReadAsync().ConfigureAwait(false))
                                                    {
                                                        if (tagSubtree.NodeType == XmlNodeType.Element && tagSubtree.Name == "name")
                                                        {
                                                            genres.Add((await tagSubtree.ReadElementContentAsStringAsync().ConfigureAwait(false), weight));
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                break;

                            // Skip expensive sections entirely
                            case "episodes":
                            case "characters":
                            case "resources":
                                reader.Skip();
                                break;
                        }
                    }
                }
            }

            // Apply title
            var title = titles.Localize(Plugin.Instance.Configuration.TitlePreference, preferredMetadataLanguage).Name;
            if (!string.IsNullOrEmpty(title))
            {
                season.Name = Plugin.Instance.Configuration.AniDbReplaceGraves
                    ? title.Replace('`', '\'')
                    : title;
            }

            // Apply genres and studios
            season.Genres = genres.OrderBy(g => g.Weight).Select(g => g.Name).ToArray();
            foreach (var studio in studios)
            {
                season.AddStudio(studio);
            }
        }

        public string Name => "LocalAniDB";

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
        {
            var metadata = await GetMetadata(searchInfo, cancellationToken).ConfigureAwait(false);

            var list = new List<RemoteSearchResult>();

            if (metadata.HasMetadata)
            {
                var res = new RemoteSearchResult
                {
                    Name = metadata.Item.Name,
                    PremiereDate = metadata.Item.PremiereDate,
                    ProductionYear = metadata.Item.ProductionYear,
                    ProviderIds = metadata.Item.ProviderIds,
                    SearchProviderName = Name
                };

                list.Add(res);
            }

            return list;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}

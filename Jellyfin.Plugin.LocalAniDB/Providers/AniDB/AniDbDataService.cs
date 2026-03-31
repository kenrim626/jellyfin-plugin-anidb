using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalAniDB.Providers.AniDB
{
    /// <summary>
    /// Shared service for downloading and caching AniDB series data.
    /// Used by both <see cref="Metadata.AniDbSeriesProvider"/> and the pipeline fetch task.
    /// </summary>
    public static class AniDbDataService
    {
        private const string SeriesDataFile = "series.xml";
        private static readonly Regex ErrorRegex = new(@"<error code=""[0-9]+"">[a-zA-Z]+</error>", RegexOptions.Compiled);

        /// <summary>
        /// Gets the series data path for the given series ID.
        /// </summary>
        public static string GetSeriesDataPath(IApplicationPaths appPaths, string seriesId)
        {
            return Path.Combine(appPaths.CachePath, "anidb", "series", seriesId);
        }

        /// <summary>
        /// Ensures series data is downloaded and cached. Returns the path to series.xml.
        /// Respects 30-minute freshness window per series and per-series locks.
        /// </summary>
        public static async Task<string> GetSeriesData(IApplicationPaths appPaths, string seriesId, ILogger logger, CancellationToken cancellationToken)
        {
            var dataPath = GetSeriesDataPath(appPaths, seriesId);
            var seriesDataPath = Path.Combine(dataPath, SeriesDataFile);

            if (Plugin.SeriesDataFreshness.TryGetValue(seriesId, out var lastFetched)
                && (DateTime.UtcNow - lastFetched).TotalMinutes < 30
                && File.Exists(seriesDataPath)
                && new FileInfo(seriesDataPath).Length > 0)
            {
                return seriesDataPath;
            }

            var seriesLock = Plugin.SeriesLocks.GetOrAdd(seriesId, _ => new SemaphoreSlim(1, 1));
            await seriesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Plugin.SeriesDataFreshness.TryGetValue(seriesId, out lastFetched)
                    && (DateTime.UtcNow - lastFetched).TotalMinutes < 30
                    && File.Exists(seriesDataPath)
                    && new FileInfo(seriesDataPath).Length > 0)
                {
                    return seriesDataPath;
                }

                await DownloadSeriesData(seriesId, seriesDataPath, appPaths.CachePath, logger, cancellationToken).ConfigureAwait(false);
                Plugin.SeriesDataFreshness[seriesId] = DateTime.UtcNow;
            }
            finally
            {
                seriesLock.Release();
            }

            return seriesDataPath;
        }

        /// <summary>
        /// Downloads series data from the local API, writes series.xml, extracts episodes and cast.
        /// </summary>
        public static async Task DownloadSeriesData(string aid, string seriesDataPath, string cachePath, ILogger logger, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(seriesDataPath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            DeleteXmlFiles(directory);

            var config = Plugin.Instance.Configuration;
            var localApiUrl = config.LocalApiUrl.TrimEnd('/');
            var url = $"{localApiUrl}/api/anime/{aid}?{Plugin.Instance.BuildRateLimitQueryString()}";

            await Plugin.LocalApiSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                var httpClient = Plugin.Instance.GetHttpClient();
                response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Plugin.LocalApiSemaphore.Release();
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning("Local API returned 404 for anime {Aid} — no data available yet", aid);
                await File.WriteAllTextAsync(seriesDataPath, string.Empty, cancellationToken).ConfigureAwait(false);
                response.Dispose();
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Local API request for anime {Aid} failed with HTTP {StatusCode}", aid, response.StatusCode);
                response.Dispose();
                throw new Exception($"Local API request failed with HTTP {(int)response.StatusCode} {response.StatusCode}");
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            var errorRegexMatch = ErrorRegex.Match(text);
            if (errorRegexMatch.Success)
            {
                if (errorRegexMatch.Value.Contains("banned", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("AniDB banned response detected for anime {Aid}. The local API handles ban cooldown automatically.", aid);
                }

                logger.LogError("AniDB API returned an error for anime {Aid}: {Error}", aid, errorRegexMatch.Value);
                throw new Exception("AniDB API error " + errorRegexMatch.Value);
            }

            using (var file = File.Open(seriesDataPath, FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(file))
            {
                await writer.WriteAsync(text).ConfigureAwait(false);
            }

            await ExtractEpisodes(directory, seriesDataPath).ConfigureAwait(false);
            await ExtractCast(cachePath, seriesDataPath).ConfigureAwait(false);
        }

        /// <summary>
        /// Checks if cached series data exists and is fresh (within 30 minutes).
        /// </summary>
        public static bool IsCacheFresh(IApplicationPaths appPaths, string seriesId)
        {
            var dataPath = GetSeriesDataPath(appPaths, seriesId);
            var seriesDataPath = Path.Combine(dataPath, SeriesDataFile);

            if (Plugin.SeriesDataFreshness.TryGetValue(seriesId, out var lastFetched)
                && (DateTime.UtcNow - lastFetched).TotalMinutes < 30
                && File.Exists(seriesDataPath)
                && new FileInfo(seriesDataPath).Length > 0)
            {
                return true;
            }

            // Also check file modification time for data that was cached before this process started
            if (File.Exists(seriesDataPath) && new FileInfo(seriesDataPath).Length > 0)
            {
                var fileAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(seriesDataPath);
                if (fileAge.TotalMinutes < 30)
                {
                    return true;
                }
            }

            return false;
        }

        private static void DeleteXmlFiles(string path)
        {
            try
            {
                foreach (var file in new DirectoryInfo(path)
                    .EnumerateFiles("*.xml", SearchOption.AllDirectories))
                {
                    file.Delete();
                }
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        public static async Task ExtractEpisodes(string seriesDataDirectory, string seriesDataPath)
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            using (var streamReader = new StreamReader(seriesDataPath, Encoding.UTF8))
            using (var reader = XmlReader.Create(streamReader, settings))
            {
                await reader.MoveToContentAsync().ConfigureAwait(false);

                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "episode")
                    {
                        var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                        await SaveEpisodeXml(seriesDataDirectory, outerXml).ConfigureAwait(false);
                    }
                }
            }
        }

        public static async Task ExtractCast(string cachePath, string seriesDataPath)
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            var cast = new List<Metadata.AniDbPersonInfo>();

            using (var streamReader = new StreamReader(seriesDataPath, Encoding.UTF8))
            using (var reader = XmlReader.Create(streamReader, settings))
            {
                await reader.MoveToContentAsync().ConfigureAwait(false);

                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "characters")
                    {
                        var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                        cast.AddRange(ParseCharacterList(outerXml));
                    }

                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "creators")
                    {
                        var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                        cast.AddRange(ParseCreatorsList(outerXml));
                    }
                }
            }

            var serializer = new XmlSerializer(typeof(Metadata.AniDbPersonInfo));
            foreach (var person in cast)
            {
                var path = GetCastPath(person.Name, cachePath);
                var directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);

                if (!File.Exists(path) || person.Image != null)
                {
                    try
                    {
                        using (var stream = File.Open(path, FileMode.Create))
                        {
                            serializer.Serialize(stream, person);
                        }
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }

        public static string ReverseNameOrder(string name)
        {
            return name.Split(' ').Reverse().Aggregate(string.Empty, (n, part) => n + " " + part).Trim();
        }

        private static string GetCastPath(string name, string cachePath)
        {
            name = name.ToLowerInvariant();
            return Path.Combine(cachePath, "anidb-people", name[0].ToString(), name + ".xml");
        }

        private static IEnumerable<Metadata.AniDbPersonInfo> ParseCharacterList(string xml)
        {
            var doc = XDocument.Parse(xml);
            var people = new List<Metadata.AniDbPersonInfo>();

            var characters = doc.Element("characters");
            if (characters != null)
            {
                foreach (var character in characters.Descendants("character"))
                {
                    var seiyuu = character.Element("seiyuu");
                    if (seiyuu != null)
                    {
                        var person = new Metadata.AniDbPersonInfo
                        {
                            Name = ReverseNameOrder(seiyuu.Value)
                        };

                        var picture = seiyuu.Attribute("picture");
                        if (picture != null && !string.IsNullOrEmpty(picture.Value))
                        {
                            var localApiUrl = Plugin.Instance.Configuration.LocalApiUrl.TrimEnd('/');
                            person.Image = $"{localApiUrl}/api/anime/person/image/{picture.Value}?{Plugin.Instance.BuildRateLimitQueryString()}";
                        }

                        var id = seiyuu.Attribute("id");
                        if (id != null && !string.IsNullOrEmpty(id.Value))
                        {
                            person.Id = id.Value;
                        }

                        people.Add(person);
                    }
                }
            }

            return people;
        }

        private static IEnumerable<Metadata.AniDbPersonInfo> ParseCreatorsList(string xml)
        {
            var doc = XDocument.Parse(xml);
            var people = new List<Metadata.AniDbPersonInfo>();

            var creators = doc.Element("creators");
            if (creators != null)
            {
                foreach (var creator in creators.Descendants("name"))
                {
                    var type = creator.Attribute("type");
                    if (type != null && type.Value == "Animation Work")
                    {
                        continue;
                    }

                    var person = new Metadata.AniDbPersonInfo
                    {
                        Name = ReverseNameOrder(creator.Value)
                    };

                    var id = creator.Attribute("id");
                    if (id != null && !string.IsNullOrEmpty(id.Value))
                    {
                        person.Id = id.Value;
                    }

                    people.Add(person);
                }
            }

            return people;
        }

        private static async Task SaveEpisodeXml(string seriesDataDirectory, string xml)
        {
            var episodeNumber = await ParseEpisodeNumber(xml).ConfigureAwait(false);

            if (episodeNumber != null)
            {
                var file = Path.Combine(seriesDataDirectory, string.Format("episode-{0}.xml", episodeNumber));
                await SaveXml(xml, file);
            }
        }

        private static async Task<string> ParseEpisodeNumber(string xml)
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            using (var streamReader = new StringReader(xml))
            using (var reader = XmlReader.Create(streamReader, settings))
            {
                reader.MoveToContent();

                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        if (reader.Name == "epno")
                        {
                            var val = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(val))
                            {
                                return val;
                            }
                        }
                        else
                        {
                            await reader.SkipAsync().ConfigureAwait(false);
                        }
                    }
                }
            }

            return null;
        }

        private static async Task SaveXml(string xml, string filename)
        {
            var writerSettings = new XmlWriterSettings
            {
                Encoding = Encoding.UTF8,
                Async = true
            };

            using (var writer = XmlWriter.Create(filename, writerSettings))
            {
                await writer.WriteRawAsync(xml).ConfigureAwait(false);
            }
        }
    }
}

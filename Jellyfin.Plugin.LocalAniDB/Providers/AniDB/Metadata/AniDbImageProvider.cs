using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.LocalAniDB.Providers.AniDB.Metadata
{
    public class AniDbImageProvider : IRemoteImageProvider
    {
        public string Name => "LocalAniDB";
        private readonly IApplicationPaths _appPaths;

        public AniDbImageProvider(IApplicationPaths appPaths)
        {
            _appPaths = appPaths;
        }

        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            // No rate limiting needed — local API handles it
            await Plugin.LocalApiSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var httpClient = Plugin.Instance.GetHttpClient();
                return await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Plugin.LocalApiSemaphore.Release();
            }
        }

        public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var seriesId = item.GetProviderId(ProviderNames.AniDb);
            return GetImages(seriesId, cancellationToken);
        }

        public Task<IEnumerable<RemoteImageInfo>> GetImages(string aniDbId, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();

            if (!string.IsNullOrEmpty(aniDbId))
            {
                // Point image URL to local API instead of CDN
                var config = Plugin.Instance.Configuration;
                var localApiUrl = config.LocalApiUrl.TrimEnd('/');
                var imageUrl = $"{localApiUrl}/api/anime/{aniDbId}/image?{Plugin.Instance.BuildRateLimitQueryString()}";

                list.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = imageUrl
                });
            }

            return Task.FromResult<IEnumerable<RemoteImageInfo>>(list);
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new[] { ImageType.Primary };
        }

        public bool Supports(BaseItem item)
        {
            return item is Series || item is Season || item is Movie;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Danmu.Core;
using Jellyfin.Plugin.Danmu.Core.Extensions;
using Jellyfin.Plugin.Danmu.Model;
using Jellyfin.Plugin.Danmu.Scrapers;
using Jellyfin.Plugin.Danmu.Scrapers.Entity;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Danmu;

public class DanmuSubtitleProvider : ISubtitleProvider
{
    public string Name => "Danmu";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryManagerEventsHelper> _logger;
    private readonly LibraryManagerEventsHelper _libraryManagerEventsHelper;

    private readonly ScraperManager _scraperManager;

    public IEnumerable<VideoContentType> SupportedMediaTypes => new List<VideoContentType>() { VideoContentType.Movie, VideoContentType.Episode };

    public DanmuSubtitleProvider(ILibraryManager libraryManager, ILoggerFactory loggerFactory, ScraperManager scraperManager, LibraryManagerEventsHelper libraryManagerEventsHelper)
    {
        _libraryManager = libraryManager;
        _logger = loggerFactory.CreateLogger<LibraryManagerEventsHelper>();
        _scraperManager = scraperManager;
        _libraryManagerEventsHelper = libraryManagerEventsHelper;
    }

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var base64EncodedBytes = System.Convert.FromBase64String(id);
        id = System.Text.Encoding.UTF8.GetString(base64EncodedBytes);
        _logger.LogInformation("手动查询弹幕信息 info={id}", id);
        var info = id.FromJson<SubtitleId>();
        if (info == null)
        {
            throw new ArgumentException();
        }

        var item = _libraryManager.GetItemById(info.ItemId);
        if (item == null)
        {
            throw new ArgumentException();
        }

        var scraper = _scraperManager.All().FirstOrDefault(x => x.ProviderId == info.ProviderId);
        if (scraper != null)
        {
            string thirdScraperId = info.Id;
            if (!info.All && item is Episode)
            {
                var scraperMedia = await scraper.GetMedia(item, info.Id).ConfigureAwait(false);
                if (scraperMedia == null || scraperMedia.Episodes == null || scraperMedia.Episodes.Count <= item.IndexNumber)
                {
                    throw new Exception($"查询信息失败");
                }

                int itemIndexNumber = item.IndexNumber ?? 0;
                ScraperEpisode scraperMediaEpisode = scraperMedia.Episodes[itemIndexNumber];
                thirdScraperId = scraperMediaEpisode.Id;
            }

            // 注意！！：item这里要使用临时对象，假如直接修改原始item的ProviderIds，会导致直接修改原始item数据
            // if (item is Movie)
            // {
            //     item = new Movie() { Id = item.Id, Name = item.Name, ProviderIds = new Dictionary<string, string>() { { scraper.ProviderId, thirdScraperId } } };
            // }
            //
            // if (item is Episode)
            // {
            //     item = new Episode() { Id = item.Id, Name = item.Name, ProviderIds = new Dictionary<string, string>() { { scraper.ProviderId, thirdScraperId } } };
            // }

            var libraryEvent = new LibraryEvent()
            {
                Item = item,
                Id = thirdScraperId,
                EventType = EventType.Force,
                ProviderId = info.ProviderId,
                All = info.All,
                Force = info.Force,
            };

            this._libraryManagerEventsHelper.QueueItem(libraryEvent);
        }

        throw new CanIgnoreException($"弹幕下载已由{Plugin.Instance?.Name}插件接管.");
    }

    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var list = new List<RemoteSubtitleInfo>();
        if (request.IsAutomated || string.IsNullOrEmpty(request.MediaPath))
        {
            return list;
        }

        var item = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Path = request.MediaPath,
        }).FirstOrDefault();

        if (item == null)
        {
            return list;
        }

        // 媒体库未启用就不处理
        if (_libraryManagerEventsHelper.IsIgnoreItem(item))
        {
            return list;
        }

        // 剧集使用series名称进行搜索
        if (item is Episode)
        {
            item.Name = request.SeriesName;
        }

        foreach (var scraper in _scraperManager.All())
        {
            try
            {

                var result = await scraper.Search(item);
                foreach (var searchInfo in result)
                {
                    var title = searchInfo.Name;
                    if (!string.IsNullOrEmpty(searchInfo.Category))
                    {
                        title = $"[{searchInfo.Category}] {searchInfo.Name}";
                    }
                    if (searchInfo.Year != null && searchInfo.Year > 0)
                    {
                        title += $" ({searchInfo.Year})";
                    }

                    // 剧集支持更多规则
                    if (item is Episode)
                    {
                        EpisodeAddMultiple(title, item, searchInfo, scraper, list);
                    }
                    else
                    {
                        AddRemoteSubtitleInfo(title, item, searchInfo, scraper, list, true, false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{0}]Exception handled processing queued movie events", scraper.Name);
            }
        }


        return list;
    }

    private void EpisodeAddMultiple(string title, BaseItem item, ScraperSearchInfo searchInfo, AbstractScraper scraper, List<RemoteSubtitleInfo> list)
    {
        if (item.IndexNumber > searchInfo.EpisodeSize)
        {
            return;
        }

        var allName = title;
        var allForceName = title;
        var oneName = title;
        if (searchInfo.EpisodeSize > 0)
        {
            oneName += $"【共{searchInfo.EpisodeSize}集】【第{((Episode) item).IndexNumber}集】";
            allName += $"【共{searchInfo.EpisodeSize}集】【只下载未下载的集数】";
            allForceName += $"【共{searchInfo.EpisodeSize}集】【强制更新全部集数】";
        }

        this.AddRemoteSubtitleInfo(oneName, item, searchInfo, scraper, list, true);
        this.AddRemoteSubtitleInfo(allName, item, searchInfo, scraper, list, false, true);
        this.AddRemoteSubtitleInfo(allForceName, item, searchInfo, scraper, list, true, true);
    }

    private void AddRemoteSubtitleInfo(string title, BaseItem item, ScraperSearchInfo searchInfo,
        AbstractScraper scraper, List<RemoteSubtitleInfo> list, bool force = false, bool all = false)
    {
        var idInfo = new SubtitleId()
        {
            ItemId = item.Id.ToString(),
            Id = searchInfo.Id.ToString(),
            ProviderId = scraper.ProviderId,
            Force = force,
            All = all,
        };
        list.Add(new RemoteSubtitleInfo()
        {
            Id = idInfo.ToJson().ToBase64(),  // Id不允许特殊字幕，做base64编码处理
            Name = title,
            ProviderName = $"{Name}",
            Format = "xml",
            Comment = $"来源：{scraper.Name}",
        });
    }

    private void UpdateDanmuMetadata(BaseItem item, string providerId, string providerVal)
    {
        // 先清空旧弹幕的所有元数据
        foreach (var s in _scraperManager.All())
        {
            item.ProviderIds.Remove(s.ProviderId);
        }
        // 保存指定弹幕元数据
        item.ProviderIds[providerId] = providerVal;
    }
}
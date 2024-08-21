using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Danmu.Configuration;
using Jellyfin.Plugin.Danmu.Core.Extensions;
using Jellyfin.Plugin.Danmu.Model;
using Jellyfin.Plugin.Danmu.Scrapers;
using Jellyfin.Plugin.Danmu.Scrapers.Entity;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Danmu;

public class LibraryManagerEventsHelper : IDisposable
{
    private readonly List<LibraryEvent> _queuedEvents;
    private readonly IMemoryCache _memoryCache;
    private readonly MemoryCacheEntryOptions _pendingAddExpiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
    private readonly MemoryCacheEntryOptions _danmuUpdatedExpiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryManagerEventsHelper> _logger;
    private readonly Jellyfin.Plugin.Danmu.Core.IFileSystem _fileSystem;
    private Timer _queueTimer;
    private readonly ScraperManager _scraperManager;

    public PluginConfiguration Config
    {
        get
        {
            return Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        }
    }


    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryManagerEventsHelper"/> class.
    /// </summary>
    /// <param name="libraryManager">The <see cref="ILibraryManager"/>.</param>
    /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
    /// <param name="api">The <see cref="BilibiliApi"/>.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    public LibraryManagerEventsHelper(ILibraryManager libraryManager, ILoggerFactory loggerFactory, Jellyfin.Plugin.Danmu.Core.IFileSystem fileSystem, ScraperManager scraperManager)
    {
        _queuedEvents = new List<LibraryEvent>();
        _memoryCache = new MemoryCache(new MemoryCacheOptions());

        _libraryManager = libraryManager;
        _logger = loggerFactory.CreateLogger<LibraryManagerEventsHelper>();
        _fileSystem = fileSystem;
        _scraperManager = scraperManager;
    }

    /// <summary>
    /// Queues an item to be added to trakt.
    /// </summary>
    /// <param name="item"> The <see cref="BaseItem"/>.</param>
    /// <param name="eventType">The <see cref="EventType"/>.</param>
    public void QueueItem(LibraryEvent libraryEvent)
    {
        lock (_queuedEvents)
        {
            if (libraryEvent.Item == null)
            {
                throw new ArgumentNullException(nameof(libraryEvent.Item));
            }

            if (_queueTimer == null)
            {
                _queueTimer = new Timer(
                    OnQueueTimerCallback,
                    null,
                    TimeSpan.FromMilliseconds(10000),
                    Timeout.InfiniteTimeSpan);
            }
            else
            {
                _queueTimer.Change(TimeSpan.FromMilliseconds(10000), Timeout.InfiniteTimeSpan);
            }

            _queuedEvents.Add(libraryEvent);
        }
    }

    /// <summary>
    /// Wait for timer callback to be completed.
    /// </summary>
    private async void OnQueueTimerCallback(object state)
    {
        try
        {
            await OnQueueTimerCallbackInternal().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnQueueTimerCallbackInternal");
        }
    }

    /// <summary>
    /// Wait for timer to be completed.
    /// </summary>
    private async Task OnQueueTimerCallbackInternal()
    {
        // _logger.LogInformation("Timer elapsed - processing queued items");
        List<LibraryEvent> queue;

        lock (_queuedEvents)
        {
            if (!_queuedEvents.Any())
            {
                _logger.LogInformation("No events... stopping queue timer");
                return;
            }

            queue = _queuedEvents.ToList();
            _queuedEvents.Clear();
        }

        var queuedMovieAdds = new List<LibraryEvent>();
        var queuedMovieUpdates = new List<LibraryEvent>();
        var queuedMovieForces = new List<LibraryEvent>();
        var queuedEpisodeAdds = new List<LibraryEvent>();
        var queuedEpisodeUpdates = new List<LibraryEvent>();
        var queuedEpisodeForces = new List<LibraryEvent>();
        var queuedShowAdds = new List<LibraryEvent>();
        var queuedShowUpdates = new List<LibraryEvent>();
        var queuedSeasonAdds = new List<LibraryEvent>();
        var queuedSeasonUpdates = new List<LibraryEvent>();

        // add事件可能会在获取元数据完之前执行，导致可能会中断元数据获取，通过pending集合把add事件延缓到获取元数据后再执行（获取完元数据后，一般会多推送一个update事件）
        foreach (var ev in queue)
        {

            // item所在的媒体库不启用弹幕插件，忽略处理
            if (IsIgnoreItem(ev.Item))
            {
                continue;
            }

            switch (ev.Item)
            {
                case Movie when ev.EventType is EventType.Add:
                    _memoryCache.Set<LibraryEvent>(ev.Item.Id, ev, _pendingAddExpiredOption);
                    break;
                case Movie when ev.EventType is EventType.Update:
                    if (_memoryCache.TryGetValue<LibraryEvent>(ev.Item.Id, out LibraryEvent addMovieEv))
                    {
                        _logger.LogInformation("新增电影 item={Name}, type={eventType}, providerId={providerId}, id={id}, force={force}, all={all}", ev.Item.Name, ev.EventType.ToString(), ev.ProviderId, ev.Id, ev.Refresh, ev.All);
                        queuedMovieAdds.Add(addMovieEv);
                        _memoryCache.Remove(ev.Item.Id);
                    }
                    else
                    {
                        // 不允许直接更新 -- 刷新数据忽略
                        // queuedMovieUpdates.Add(ev);
                    }
                    break;
                case Movie when ev.EventType is EventType.Force:
                    _logger.LogInformation("刷新电影 item={Name}, type={eventType}, providerId={providerId}, id={id}, force={force}, all={all}", ev.Item.Name, ev.EventType.ToString(), ev.ProviderId, ev.Id, ev.Refresh, ev.All);
                    queuedMovieForces.Add(ev);
                    break;
                case Series when ev.EventType is EventType.Add:
                    // _logger.LogInformation("Series add: {0}", ev.Item.Name);
                    // _pendingAddEventCache.Set<LibraryEvent>(ev.Item.Id, ev, _expiredOption);
                    break;
                case Series when ev.EventType is EventType.Update:
                    // _logger.LogInformation("Series update: {0}", ev.Item.Name);
                    // if (_pendingAddEventCache.TryGetValue<LibraryEvent>(ev.Item.Id, out LibraryEvent addSerieEv))
                    // {
                    //     // 紧跟add事件的update事件不需要处理
                    //     _pendingAddEventCache.Remove(ev.Item.Id);
                    // }
                    // else
                    // {
                    //     queuedShowUpdates.Add(ev);
                    // }
                    break;
                case Season when ev.EventType is EventType.Add:
                    _memoryCache.Set<LibraryEvent>(ev.Item.Id, ev, _pendingAddExpiredOption);
                    break;
                case Season when ev.EventType is EventType.Update:
                    if (_memoryCache.TryGetValue<LibraryEvent>(ev.Item.Id, out LibraryEvent addSeasonEv))
                    {
                        _logger.LogInformation("新增Season item={Name}, type={eventType}, providerId={providerId}, id={id}, force={force}, all={all}", ev.Item.Name, ev.EventType.ToString(), ev.ProviderId, ev.Id, ev.Refresh, ev.All);
                        queuedSeasonAdds.Add(addSeasonEv);
                        _memoryCache.Remove(ev.Item.Id);
                    }
                    else
                    {
                        // 不允许直接更新 -- 刷新数据忽略
                        // queuedSeasonUpdates.Add(ev);
                    }
                    break;
                case Episode when ev.EventType is EventType.Add:
                    _memoryCache.Set<LibraryEvent>(ev.Item.Id, ev, _pendingAddExpiredOption);
                    break;
                case Episode when ev.EventType is EventType.Update:
                    if (_memoryCache.TryGetValue<LibraryEvent>(ev.Item.Id, out LibraryEvent addEpisodeEv))
                    {
                        _logger.LogInformation("新增Episode item={Name}, type={eventType}, providerId={providerId}, id={id}, force={force}, all={all}", ev.Item.Name, ev.EventType.ToString(), ev.ProviderId, ev.Id, ev.Refresh, ev.All);
                        queuedEpisodeAdds.Add(addEpisodeEv);
                        _memoryCache.Remove(ev.Item.Id);
                    }
                    else
                    {
                        // 不允许直接更新 -- 刷新数据忽略
                        // queuedSeasonUpdates.Add(ev);
                    }

                    break;
                case Episode when ev.EventType is EventType.Force:
                    _logger.LogInformation("刷新Episode index={IndexNumber}, item={Name}, type={eventType}, providerId={providerId}, id={id}, force={force}, all={all}", ev.Item.IndexNumber, ev.Item.Name, ev.EventType.ToString(), ev.ProviderId, ev.Id, ev.Refresh, ev.All);
                    queuedEpisodeForces.Add(ev);
                    break;
            }

        }

        // 对于剧集，处理顺序也很重要（Add事件后，会刷新元数据，导致会同时推送Update事件）
        await ProcessQueuedMovieEvents(queuedMovieAdds, EventType.Add).ConfigureAwait(false);
        await ProcessQueuedMovieEvents(queuedMovieUpdates, EventType.Update).ConfigureAwait(false);

        await ProcessQueuedShowEvents(queuedShowAdds, EventType.Add).ConfigureAwait(false);
        await ProcessQueuedSeasonEvents(queuedSeasonAdds).ConfigureAwait(false);
        await ProcessQueuedEpisodeEvents(queuedEpisodeAdds, EventType.Add).ConfigureAwait(false);

        await ProcessQueuedShowEvents(queuedShowUpdates, EventType.Update).ConfigureAwait(false);
        await ProcessQueuedSeasonEvents(queuedSeasonUpdates).ConfigureAwait(false);
        await ProcessQueuedEpisodeEvents(queuedEpisodeUpdates, EventType.Update).ConfigureAwait(false);

        await ProcessQueuedMovieEvents(queuedMovieForces, EventType.Force).ConfigureAwait(false);
        await ProcessQueuedEpisodeEvents(queuedEpisodeForces, EventType.Force).ConfigureAwait(false);
    }

    public bool IsIgnoreItem(BaseItem item)
    {
        // item所在的媒体库不启用弹幕插件，忽略处理
        var libraryOptions = _libraryManager.GetLibraryOptions(item);
        if (libraryOptions != null && libraryOptions.DisabledSubtitleFetchers.Contains(Plugin.Instance?.Name))
        {
            this._logger.LogInformation($"媒体库已关闭danmu插件, 忽略处理[{item.Name}].");
            return true;
        }

        return false;
    }


    /// <summary>
    /// Processes queued movie events.
    /// </summary>
    /// <param name="events">The <see cref="LibraryEvent"/> enumerable.</param>
    /// <param name="eventType">The <see cref="EventType"/>.</param>
    /// <returns>Task.</returns>
    public async Task ProcessQueuedMovieEvents(IReadOnlyCollection<LibraryEvent> events, EventType eventType)
    {
        if (events.Count == 0)
        {
            return;
        }

        var movieLibs = events.Select(lev => lev)
            .Where(lev => !string.IsNullOrEmpty(lev.Item.Name))
            .ToHashSet();

        if (movieLibs.Count == 0)
        {
            _logger.LogInformation("没有有效任务需要执行 events={events}, movies={episodeLibs}", events.Count, movieLibs.Count);
            return;
        }

        var scrapers = this._scraperManager.All();
        var queueUpdateMeta = new List<BaseItem>();
        foreach (LibraryEvent movieEvent in movieLibs)
        {
            try
            {
                Movie? item = (Movie)movieEvent.Item;
                if (movieEvent.EventType == EventType.Add)
                {

                    item = _libraryManager.GetItemById<Movie>(item.Id);
                }

                if (item == null)
                {
                    _logger.LogInformation("查询最新数据失败 originId={id}", movieEvent.Item.Id);
                    continue;
                }

                // 指定具体三方id，手动刷新弹幕场景
                string movieThirdId = movieEvent.Id;
                string movieProviderId = movieEvent.ProviderId;
                bool downloadSuccess = false;
                if (!string.IsNullOrEmpty(movieThirdId) && !string.IsNullOrEmpty(movieProviderId))
                {
                    AbstractScraper? matchScraper = scrapers.FirstOrDefault(x => x.ProviderId.Equals(movieProviderId));
                    if (matchScraper != null)
                    {
                        downloadSuccess = await this
                            .DownloadMovie(movieEvent, queueUpdateMeta, item, null, matchScraper, movieThirdId)
                            .ConfigureAwait(false);
                        continue;
                    }

                    throw new Exception($"当前三方id=${movieThirdId}, 三方下载器=${movieProviderId} 下载器不存在请重试");
                }

                // 重新根据原始数据获取最新弹幕
                if (!movieEvent.Force)
                {
                    downloadSuccess = false;
                    foreach (AbstractScraper scraper in scrapers)
                    {
                        string? providerId = item.GetProviderId(scraper.ProviderId);
                        if (!string.IsNullOrEmpty(providerId))
                        {
                            downloadSuccess = await this
                                .DownloadMovie(movieEvent, queueUpdateMeta, item, null, scraper, movieThirdId)
                                .ConfigureAwait(false);
                            if (downloadSuccess)
                            {
                                break;
                            }
                        }
                    }

                    if (downloadSuccess)
                    {
                        continue;
                    }
                }

                downloadSuccess = await this.DownloadMovie(movieEvent, queueUpdateMeta, item, scrapers, null, null)
                    .ConfigureAwait(false);
            }
            catch (FrequentlyRequestException ex)
            {
                _logger.LogError(ex, "[{0}]api接口触发风控，中止执行，请稍候再试.", movieEvent.Item.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{0}]Exception handled processing movie events", movieEvent.Item.Name);
            }
        }

        await ProcessQueuedUpdateMeta(queueUpdateMeta).ConfigureAwait(false);
    }

    private async Task<bool> DownloadMovie(LibraryEvent movieEvent, List<BaseItem> queueUpdateMeta, Movie currentItem, ICollection<AbstractScraper>? scrapers, AbstractScraper? forceScraper, string? thirdProviderId)
    {
        // 指定下载相应的弹幕
        if (forceScraper != null && !string.IsNullOrEmpty(thirdProviderId))
        {
            if (!movieEvent.Refresh)
            {
                string danmuXmlPath = currentItem.GetDanmuXmlPath(forceScraper.ProviderId);
                if (File.Exists(danmuXmlPath))
                {
                    _logger.LogInformation("当前弹幕信息已存在，无需下载 danmuXmlPath={danmuXmlPath}", danmuXmlPath);
                    return true;
                }
            }

            var episode = await forceScraper.GetMediaEpisode(currentItem, thirdProviderId).ConfigureAwait(false);
            if (episode != null)
            {
                // 下载弹幕xml文件
                await this.DownloadDanmu(forceScraper, currentItem, episode.CommentId).ConfigureAwait(false);
            }

            return true;
        }

        // 不指定下载第一个能匹配的数据
        foreach (var scraper in scrapers)
        {
            try
            {
                var mediaId = await scraper.SearchMediaId(currentItem);
                if (string.IsNullOrEmpty(mediaId))
                {
                    this._logger.LogInformation("[{0}]匹配失败：{1} ({2})", scraper.Name, currentItem.Name, currentItem.ProductionYear);
                    continue;
                }

                var media = await scraper.GetMedia(currentItem, mediaId);
                if (media != null)
                {
                    var providerVal = media.Id;
                    var commentId = media.CommentId;
                    _logger.LogInformation("[{0}]匹配成功：name={1} ProviderId: {2}", scraper.Name, currentItem.Name, providerVal);

                    // 更新epid元数据
                    currentItem.SetProviderId(scraper.ProviderId, providerVal);
                    queueUpdateMeta.Add(currentItem);

                    // 下载弹幕
                    await this.DownloadDanmu(scraper, currentItem, commentId).ConfigureAwait(false);
                    return true;
                }
            }
            catch (FrequentlyRequestException ex)
            {
                _logger.LogError(ex, "[{0}]api接口触发风控，中止执行，请稍候再试.", scraper.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{0}]Exception handled processing movie events", scraper.Name);
            }
        }

        return false;
    }


    /// <summary>
    /// Processes queued show events.
    /// </summary>
    /// <param name="events">The <see cref="LibraryEvent"/> enumerable.</param>
    /// <param name="eventType">The <see cref="EventType"/>.</param>
    /// <returns>Task.</returns>
    public async Task ProcessQueuedShowEvents(IReadOnlyCollection<LibraryEvent> events, EventType eventType)
    {
        if (events.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Processing {Count} shows with event type {EventType}", events.Count, eventType);

        var series = events.Select(lev => (Series)lev.Item)
            .Where(lev => !string.IsNullOrEmpty(lev.Name))
            .ToHashSet();

        try
        {
            if (eventType == EventType.Update)
            {
                foreach (var item in series)
                {
                    var seasons = item.GetSeasons(null, new DtoOptions(false));
                    foreach (var season in seasons)
                    {
                        // 发现season保存元数据，不会推送update事件，这里通过series的update事件推送刷新
                        QueueItem(new LibraryEvent()
                        {
                            Item = season,
                            EventType = eventType,
                            Refresh = false,
                            All = false,
                        });
                    }
                }
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception handled processing queued show events");
        }
    }

    /// <summary>
    /// Processes queued season events.
    /// </summary>
    /// <param name="events">The <see cref="LibraryEvent"/> enumerable.</param>
    /// <param name="eventType">The <see cref="EventType"/>.</param>
    /// <returns>Task.</returns>
    public async Task ProcessQueuedSeasonEvents(IReadOnlyCollection<LibraryEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Processing {Count} seasons with event type", events.Count);
        var seasonLibs = events.Select(lev => lev)
            .Where(lev => !string.IsNullOrEmpty(lev.Item.Name))
            .ToHashSet();

        foreach (var seasonLib in seasonLibs)
        {
            // // 虚拟季第一次请求忽略
            // if (season.LocationType == LocationType.Virtual && season.IndexNumber is null)
            // {
            //     continue;
            // }
            _logger.LogInformation("season 任务触发 item={Name}, type={eventType}, providerId={providerId}, id={id}, force={force}, all={all}", seasonLib.Item.Name, seasonLib.EventType.ToString(), seasonLib.ProviderId, seasonLib.Id, seasonLib.Refresh, seasonLib.All);
            
            Season season = (Season)seasonLib.Item;
            var queueUpdateMeta = new List<BaseItem>();
            // GetEpisodes一定要取所有fields，要不然更新会导致重建虚拟season季信息
            // TODO：可能出现未刮削完，就触发获取弹幕，导致GetEpisodes只能获取到部分剧集的情况
            var episodes = season.GetEpisodes();
            _logger.LogInformation("ProcessQueuedSeasonEvents episodes={count}", episodes.Count);
            if (episodes == null)
            {
                continue;
            }

            // 不处理季文件夹下的特典和extras影片（动画经常会混在一起）
            var episodesWithoutSP = episodes.Where(x => x.ParentIndexNumber != null && x.ParentIndexNumber > 0).ToList();
            if (episodes.Count != episodesWithoutSP.Count)
            {
                _logger.LogInformation("{0}季存在{1}个特典或extra片段，忽略处理.", season.Name, (episodes.Count - episodesWithoutSP.Count));
                episodes = episodesWithoutSP;
            }

            foreach (var scraper in _scraperManager.All())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(seasonLib.ProviderId) && !scraper.ProviderId.Equals(seasonLib.ProviderId))
                    {
                        continue;
                    }

                    var providerVal = seasonLib.Id ?? season.GetProviderId(scraper.ProviderId);
                    _logger.LogInformation(
                        "ProcessQueuedSeasonEvents 查询providerVal={providerVal}, providerId={ProviderId}, id={Id}",
                        providerVal, seasonLib.ProviderId, seasonLib.Id);
                    ScraperMedia? media = null;
                    if (string.IsNullOrEmpty(providerVal))
                    {
                        // 新增或者强制更新，需要更新season的id
                        if (seasonLib.Force || seasonLib.EventType == EventType.Add)
                        {
                            media = await this.GetSeason(queueUpdateMeta, season, scraper)
                                .ConfigureAwait(false);
                            providerVal = season.GetProviderId(scraper.ProviderId);
                        }
                        else
                        {
                            continue;
                        }
                    }

                    if (string.IsNullOrEmpty(providerVal))
                    {
                        continue;
                    }

                    // 如果存在新的id，将id更新到数据库上
                    string? originProviderId = season.GetProviderId(scraper.ProviderId);
                    if (string.IsNullOrEmpty(originProviderId) && !String.Equals(providerVal, originProviderId))
                    {
                        season.SetProviderId(scraper.ProviderId, providerVal);
                        queueUpdateMeta.Add(season);
                    }

                    if (media == null)
                    {
                        media = await scraper.GetMedia(season, providerVal).ConfigureAwait(false);
                    }

                    if (media == null)
                    {
                        _logger.LogInformation("[{0}]获取不到视频信息. ProviderId: {1}", scraper.Name, providerVal);
                        continue;
                    }

                    foreach (var (episode, idx) in episodes.WithIndex())
                    {
                        var fileName = Path.GetFileName(episode.Path);
                        var indexNumber = episode.IndexNumber ?? 0;
                        if (indexNumber <= 0)
                        {
                            _logger.LogInformation("[{0}]匹配失败，缺少集号. [{1}]{2}", scraper.Name, season.Name, fileName);
                            continue;
                        }

                        if (indexNumber > media.Episodes.Count)
                        {
                            _logger.LogInformation("[{0}]匹配失败，集号超过总集数，可能识别集号错误. [{1}]{2} indexNumber: {3}, 集数：{4}", scraper.Name, season.Name, fileName, indexNumber, media.Episodes.Count);
                            continue;
                        }

                        if (this.Config.DownloadOption.EnableEpisodeCountSame && media.Episodes.Count != episodes.Count)
                        {
                             _logger.LogInformation("[{0}]刷新弹幕失败, 集数不一致。video: {1}.{2} 弹幕数：{3} 集数：{4}", scraper.Name, indexNumber, episode.Name, media.Episodes.Count, media.Episodes.Count);
                             continue;
                        }

                        // 剧集允许只下载没有的数据
                        if (!seasonLib.Refresh)
                        {
                            string danmuXmlPath = episode.GetDanmuXmlPath(scraper.ProviderId);
                            if (File.Exists(danmuXmlPath))
                            {
                                _logger.LogInformation("当前弹幕信息已存在，无需下载 danmuXmlPath={danmuXmlPath}", danmuXmlPath);
                                continue;
                            }
                        }

                        var epId = media.Episodes[idx].Id;
                        var commentId = media.Episodes[idx].CommentId;
                        _logger.LogInformation("[{0}]成功匹配. {1}.{2} -> epId: {3} cid: {4}", scraper.Name, indexNumber, episode.Name, epId, commentId);

                        // 更新eposide元数据
                        var episodeProviderVal = episode.GetProviderId(scraper.ProviderId);
                        if (!string.IsNullOrEmpty(epId) && episodeProviderVal != epId)
                        {
                            episode.SetProviderId(scraper.ProviderId, epId);
                            queueUpdateMeta.Add(episode);
                        }
                        
                        // 新增只更新providerId信息
                        if (seasonLib.EventType == EventType.Add)
                        {
                            continue;
                        }

                        // 下载弹幕
                        await this.DownloadDanmu(scraper, episode, commentId).ConfigureAwait(false);
                    }

                    break;
                }
                catch (FrequentlyRequestException ex)
                {
                    _logger.LogError(ex, "api接口触发风控，中止执行，请稍候再试.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception handled processing queued movie events");
                }
            }

            // 保存元数据
            await ProcessQueuedUpdateMeta(queueUpdateMeta).ConfigureAwait(false);
        }
    }

    private async Task<ScraperMedia?> GetSeason(List<BaseItem> queueUpdateMeta, Season season, AbstractScraper scraper)
    {
        try
        {
            // 读取最新数据，要不然取不到年份信息（不能对GetItemById的对象直接修改属性，要不然会直接改到数据！！！！）
            var currentItem = _libraryManager.GetItemById(season.Id);
            if (currentItem != null)
            {
                season.ProductionYear = currentItem.ProductionYear;
            }

            // 季的名称不准确，改使用series的名称
            Series series = season.Series;
            if (series != null)
            {
                season.Name = series.Name;
            }
            var mediaId = await scraper.SearchMediaId(season);
            if (string.IsNullOrEmpty(mediaId))
            {
                _logger.LogInformation("[{0}]匹配失败：{1} ({2})", scraper.Name, season.Name, season.ProductionYear);
                return null;
            }
            var media = await scraper.GetMedia(season, mediaId);
            if (media == null)
            {
                _logger.LogInformation("[{0}]匹配成功，但获取不到视频信息. id: {1}", scraper.Name, mediaId);
                return null;
            }


            // 更新seasonId元数据
            season.SetProviderId(scraper.ProviderId, mediaId);
            queueUpdateMeta.Add(season);

            _logger.LogInformation("[{0}]匹配成功：name={1} season_number={2} ProviderId: {3}", scraper.Name, season.Name, season.IndexNumber, mediaId);
            return media;
        }
        catch (FrequentlyRequestException ex)
        {
            _logger.LogError(ex, "api接口触发风控，中止执行，请稍候再试.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception handled processing season events");
        }

        return null;
    }


    /// <summary>
    /// Processes queued episode events.
    /// </summary>
    /// <param name="events">The <see cref="LibraryEvent"/> enumerable.</param>
    /// <param name="eventType">The <see cref="EventType"/>.</param>
    /// <returns>Task.</returns>
    public async Task ProcessQueuedEpisodeEvents(IReadOnlyCollection<LibraryEvent> events, EventType eventType)
    {
        if (events.Count == 0)
        {
            return;
        }

        var episodeLibs = events.Select(lev => lev)
            .Where(lev => !string.IsNullOrEmpty(lev.Item.Name))
            .ToHashSet();

        if (episodeLibs.Count == 0)
        {
            _logger.LogInformation("没有有效任务需要执行 events={events}, episodeLibs={episodeLibs}", events.Count, episodeLibs.Count);
            return;
        }

        var scrapers = this._scraperManager.All();
        var queueUpdateMeta = new List<BaseItem>();
        foreach (var itemLib in episodeLibs)
        {
            var item = (Episode)itemLib.Item;
            var season = item.Season;
            if (season == null)
            {
                Episode? baseItem = (Episode)_libraryManager.GetItemById(item.Id);
                if (baseItem != null)
                {
                    item = baseItem;
                    itemLib.Item = item;
                    season = baseItem.Season;
                }
            }

            if (season == null)
            {
                _logger.LogInformation("season信息不能为空 item={Id}, name={Name}", item.Id, item.Name);
                continue;
            }

            // 更新全部剧集交给剧集更新逻辑
            if (itemLib.All)
            {
                await this.ProcessQueuedSeasonEvents(new[]
                {
                    new LibraryEvent()
                    {
                        Item = season,
                        EventType = itemLib.EventType,
                        ProviderId = itemLib.ProviderId,
                        Refresh = itemLib.Refresh,
                        Force = itemLib.Force,
                        All = itemLib.All,
                        Id = itemLib.Id,
                    },
                }).ConfigureAwait(false);
                continue;
            }

            // 不要求强制下载，优先使用原始id下载
            if (!itemLib.Force)
            {
                // 获取匹配的查询器和id
                this.GetMatchScraperAndThirdId(scrapers, item, itemLib.ProviderId, out var matchScraper, out var thirdProviderId);
                bool downloadEpisodeSuccess = await this.DownloadEpisode(queueUpdateMeta, matchScraper, thirdProviderId, item).ConfigureAwait(false);
                if (downloadEpisodeSuccess)
                {
                    continue;
                }

                // 使用季信息进行查询弹幕
                this.GetMatchScraperAndThirdId(scrapers, season, itemLib.ProviderId, out matchScraper, out thirdProviderId);
                downloadEpisodeSuccess = await this.DownloadEpisode(queueUpdateMeta, matchScraper, null, item, true).ConfigureAwait(false);
                if (downloadEpisodeSuccess)
                {
                    continue;
                }
            }

            // 刷新弹幕
            foreach (var scraper in _scraperManager.All())
            {
                try
                {
                    // 如果指定相应的数据id，使用特定的id
                    if (!string.IsNullOrWhiteSpace(itemLib.ProviderId))
                    {
                        if (!scraper.ProviderId.Equals(itemLib.ProviderId))
                        {
                            continue;
                        }
                    }

                    var providerVal = itemLib.Id ?? item.GetProviderId(scraper.ProviderId);
                    bool downloadEpisodeSuccess = await this.DownloadEpisode(queueUpdateMeta, scraper, providerVal, item, true).ConfigureAwait(false);
                    if (downloadEpisodeSuccess)
                    {
                        break;
                    }
                }
                catch (FrequentlyRequestException ex)
                {
                    _logger.LogError(ex, "api接口触发风控，中止执行，请稍候再试.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception handled processing queued movie events");
                }
            }
        }

        // 保存元数据
        await ProcessQueuedUpdateMeta(queueUpdateMeta).ConfigureAwait(false);
    }

    private async Task<bool> DownloadEpisode(List<BaseItem> queueUpdateMeta, AbstractScraper? scraper, string? espisodeThirdProviderId, Episode? item, bool needQuerySeason = false)
    {
        if (scraper == null || item == null)
        {
            return false;
        }

        try
        {
            // 获取匹配的查询器和id
            if (!string.IsNullOrEmpty(espisodeThirdProviderId))
            {
                // 剧集已经存在匹配的弹幕元信息
                var episode = await scraper.GetMediaEpisode(item, espisodeThirdProviderId).ConfigureAwait(false);
                if (episode != null)
                {
                    // 下载弹幕xml文件
                    await this.DownloadDanmu(scraper, item, episode.CommentId).ConfigureAwait(false);
                    return true;
                }
            }

            if (!needQuerySeason)
            {
                return false;
            }

            // 使用季信息进行查询弹幕
            Season season = item.Season;
            string? seasonProviderId = season.GetProviderId(scraper.ProviderId);
            // 使用serise信息
            ScraperMedia? media = null;
            if (string.IsNullOrEmpty(seasonProviderId))
            {
                await this.GetSeason(queueUpdateMeta, season, scraper).ConfigureAwait(false);
            }
            else
            {
                media = await scraper.GetMedia(season, seasonProviderId).ConfigureAwait(false);
            }

            if (media != null)
            {
                var fileName = Path.GetFileName(item.Path);
                var indexNumber = item.IndexNumber ?? 0;
                if (indexNumber <= 0)
                {
                    this._logger.LogInformation("[{0}]匹配失败，缺少集号. [{1}]{2}", scraper.Name, season.Name, fileName);
                    return false;
                }

                if (indexNumber > media.Episodes.Count)
                {
                    this._logger.LogInformation("[{0}]匹配失败，集号超过总集数，可能识别集号错误. [{1}]{2} indexNumber: {3}",scraper.Name, season.Name, fileName, indexNumber);
                    return false;
                }

                if (this.Config.DownloadOption.EnableEpisodeCountSame && media.Episodes.Count != season.GetEpisodes().Count)
                {
                    this._logger.LogInformation("[{0}]刷新弹幕失败, 集数不一致。video: {1}.{2} 弹幕数：{3} 集数：{4}",scraper.Name, indexNumber, item.Name, media.Episodes.Count, season.GetEpisodes().Count);
                    return false;
                }

                var idx = indexNumber - 1;
                var epId = media.Episodes[idx].Id;
                var commentId = media.Episodes[idx].CommentId;
                this._logger.LogInformation("[{0}]成功匹配. {1}.{2} -> epId: {3} cid: {4}", scraper.Name, item.IndexNumber, item.Name, epId, commentId);

                // 更新 eposide 元数据
                var episodeProviderVal = item.GetProviderId(scraper.ProviderId);
                if (!string.IsNullOrEmpty(epId) && episodeProviderVal != epId)
                {
                    item.SetProviderId(scraper.ProviderId, epId);
                    queueUpdateMeta.Add(item);
                }

                // 如果存在新的id，将id更新到数据库上
                string? originProviderId = season.GetProviderId(scraper.ProviderId);
                if (string.IsNullOrEmpty(originProviderId) && !string.Equals(media.Id, originProviderId, StringComparison.Ordinal))
                {
                    season.SetProviderId(scraper.ProviderId, media.Id);
                    queueUpdateMeta.Add(season);
                }

                // 下载弹幕
                await this.DownloadDanmu(scraper, item, commentId).ConfigureAwait(false);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception handled DownloadEpisode fail. name={0}", item.Name);
        }

        return false;
    }

    private void GetMatchScraperAndThirdId(ICollection<AbstractScraper> scrapers, BaseItem baseItem, string? matchProviderId, out AbstractScraper? scraper, out string? thirdProviderId)
    {
        // 如果指定providerId使用providerId
        if (!string.IsNullOrEmpty(matchProviderId))
        {
            thirdProviderId = baseItem.GetProviderId(matchProviderId);
            scraper = scrapers.FirstOrDefault(s => s.ProviderId.Equals(matchProviderId));
            return;
        }

        string? matchThirdProviderId = null;
        AbstractScraper? matchScraper = scrapers.FirstOrDefault(s =>
        {
            matchThirdProviderId = baseItem.GetProviderId(s.ProviderId);
            if (string.IsNullOrEmpty(matchThirdProviderId))
            {
                return false;
            }

            return true;
        });

        thirdProviderId = matchThirdProviderId;
        scraper = matchScraper;
    }


    // 调用UpdateToRepositoryAsync后，但未完成时，会导致GetEpisodes返回缺少正在处理的集数，所以采用统一最后处理
    private async Task ProcessQueuedUpdateMeta(List<BaseItem> queue)
    {
        if (queue == null || queue.Count <= 0)
        {
            return;
        }

        foreach (var queueItem in queue)
        {
            // 获取最新的item数据
            var item = _libraryManager.GetItemById(queueItem.Id);
            if (item != null)
            {
                // 合并新添加的provider id
                foreach (var pair in queueItem.ProviderIds)
                {
                    if (string.IsNullOrEmpty(pair.Value))
                    {
                        continue;
                    }

                    item.ProviderIds[pair.Key] = pair.Value;
                }

                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                // _logger.LogInformation("更新epid到元数据, type={type} item={name}, id={id}, ProviderIds={ProviderIds}", item.GetType(), item.Name, item.Id, item.ProviderIds);
            }
        }
        _logger.LogInformation("更新epid到元数据完成。item数：{0}", queue.Count);
    }

    public async Task DownloadDanmu(AbstractScraper scraper, BaseItem item, string commentId, bool ignoreCheck = false)
    {
        // 下载弹幕xml文件
        var checkDownloadedKey = $"{item.Id}_{commentId}";
        try
        {
            // 弹幕5分钟内更新过，忽略处理（有时Update事件会重复执行）
            if (!ignoreCheck && _memoryCache.TryGetValue(checkDownloadedKey, out var latestDownloaded))
            {
                _logger.LogInformation("[{0}]最近5分钟已更新过弹幕xml，忽略处理：{1}.{2}", scraper.Name, item.IndexNumber, item.Name);
                return;
            }

            _memoryCache.Set(checkDownloadedKey, true, _danmuUpdatedExpiredOption);
            var danmaku = await scraper.GetDanmuContent(item, commentId);
            if (danmaku != null)
            {
                var bytes = danmaku.ToXml();
                if (bytes.Length < 1024)
                {
                    _logger.LogInformation("[{0}]弹幕内容少于1KB，忽略处理：{1}.{2}", scraper.Name, item.IndexNumber, item.Name);
                    return;
                }
                await this.SaveDanmu(scraper, item, bytes);
                this._logger.LogInformation("[{0}]弹幕下载成功：name={1}.{2} commentId={3}", scraper.Name, item.IndexNumber ?? 1, item.Name, commentId);
            }
            else
            {
                _memoryCache.Remove(checkDownloadedKey);
            }
        }
        catch (Exception ex)
        {
            _memoryCache.Remove(checkDownloadedKey);
            _logger.LogError(ex, "[{0}]Exception handled download danmu file. name={1}", scraper.Name, item.Name);
        }
    }

    private bool IsRepeatAction(BaseItem item, string checkDownloadedKey)
    {
        // 单元测试时为null
        if (item.FileNameWithoutExtension == null) return false;

        // 通过xml文件属性判断（多线程时判断有误）
        var danmuPath = Path.Combine(item.ContainingFolderPath, item.FileNameWithoutExtension + ".xml");
        if (!this._fileSystem.Exists(danmuPath))
        {
            return false;
        }

        var lastWriteTime = this._fileSystem.GetLastWriteTime(danmuPath);
        var diff = DateTime.Now - lastWriteTime;
        return diff.TotalSeconds < 300;
    }

    private async Task SaveDanmu(AbstractScraper scraper, BaseItem item, byte[] bytes)
    {
        // 单元测试时为null
        if (item.FileNameWithoutExtension == null) return;

        // 下载弹幕xml文件
        var danmuPath = item.GetDanmuXmlPath(scraper.ProviderId);
        _logger.LogInformation("弹幕存储目录 danmuPath={danmuPath}", danmuPath);
        await this._fileSystem.WriteAllBytesAsync(danmuPath, bytes, CancellationToken.None).ConfigureAwait(false);

        if (this.Config.ToAss && bytes.Length > 0)
        {
            var assConfig = new Danmaku2Ass.Config();
            assConfig.Title = item.Name;
            if (!string.IsNullOrEmpty(this.Config.AssFont.Trim()))
            {
                assConfig.FontName = this.Config.AssFont;
            }
            if (!string.IsNullOrEmpty(this.Config.AssFontSize.Trim()))
            {
                assConfig.BaseFontSize = this.Config.AssFontSize.Trim().ToInt();
            }
            if (!string.IsNullOrEmpty(this.Config.AssTextOpacity.Trim()))
            {
                assConfig.TextOpacity = this.Config.AssTextOpacity.Trim().ToFloat();
            }
            if (!string.IsNullOrEmpty(this.Config.AssLineCount.Trim()))
            {
                assConfig.LineCount = this.Config.AssLineCount.Trim().ToInt();
            }
            if (!string.IsNullOrEmpty(this.Config.AssSpeed.Trim()))
            {
                assConfig.TuneDuration = this.Config.AssSpeed.Trim().ToInt() - 8;
            }

            var assPath = item.GetDanmuAssPath(scraper.ProviderId);
            Danmaku2Ass.Bilibili.GetInstance().Create(bytes, assConfig, assPath);
        }
    }

    private async Task ForceSaveProviderId(BaseItem item, string providerId, string providerVal)
    {
        // 先清空旧弹幕的所有元数据
        foreach (var s in _scraperManager.All())
        {
            item.ProviderIds.Remove(s.ProviderId);
        }
        // 保存指定弹幕元数据
        item.ProviderIds[providerId] = providerVal;

        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
    }


    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _queueTimer?.Dispose();
        }
    }
}

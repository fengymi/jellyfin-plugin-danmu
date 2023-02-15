using System.Linq;
using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Danmu.Core;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Danmu.Scrapers.Entity;
using System.Collections.Generic;
using System.Xml;
using Jellyfin.Plugin.Danmu.Core.Extensions;
using System.Text.Json;
using Jellyfin.Plugin.Danmu.Scrapers.Iqiyi.Entity;

namespace Jellyfin.Plugin.Danmu.Scrapers.Iqiyi;

public class Iqiyi : AbstractScraper
{
    public const string ScraperProviderName = "爱奇艺";
    public const string ScraperProviderId = "IqiyiID";

    private readonly IqiyiApi _api;

    public Iqiyi(ILoggerFactory logManager)
        : base(logManager.CreateLogger<Iqiyi>())
    {
        _api = new IqiyiApi(logManager);
    }

    public override int DefaultOrder => 4;

    public override bool DefaultEnable => false;

    public override string Name => "爱奇艺";

    public override string ProviderName => ScraperProviderName;

    public override string ProviderId => ScraperProviderId;

    public override async Task<List<ScraperSearchInfo>> Search(BaseItem item)
    {
        var list = new List<ScraperSearchInfo>();
        var isMovieItemType = item is MediaBrowser.Controller.Entities.Movies.Movie;
        var searchName = this.NormalizeSearchName(item.Name);
        var videos = await this._api.GetSuggestAsync(searchName, CancellationToken.None).ConfigureAwait(false);
        foreach (var video in videos)
        {
            var videoId = video.VideoId;
            var title = video.Name;
            var pubYear = video.Year;

            if (isMovieItemType && video.ChannelName != "电影")
            {
                continue;
            }

            if (!isMovieItemType && video.ChannelName == "电影")
            {
                continue;
            }

            list.Add(new ScraperSearchInfo()
            {
                Id = $"{video.LinkId}",
                Name = title,
                Category = video.ChannelName,
                Year = pubYear,
            });
        }


        return list;
    }

    public override async Task<string?> SearchMediaId(BaseItem item)
    {
        var isMovieItemType = item is MediaBrowser.Controller.Entities.Movies.Movie;
        var searchName = this.NormalizeSearchName(item.Name);
        var videos = await this._api.GetSuggestAsync(searchName, CancellationToken.None).ConfigureAwait(false);
        foreach (var video in videos)
        {
            var videoId = video.VideoId;
            var title = video.Name;
            var pubYear = video.Year;

            if (isMovieItemType && video.ChannelName != "电影")
            {
                continue;
            }

            if (!isMovieItemType && video.ChannelName == "电影")
            {
                continue;
            }

            // 检测标题是否相似（越大越相似）
            var score = searchName.Distance(title);
            if (score < 0.7)
            {
                log.LogInformation("[{0}] 标题差异太大，忽略处理. 搜索词：{1}, score:　{2}", title, searchName, score);
                continue;
            }

            // 检测年份是否一致
            var itemPubYear = item.ProductionYear ?? 0;
            if (itemPubYear > 0 && pubYear > 0 && itemPubYear != pubYear)
            {
                log.LogInformation("[{0}] 发行年份不一致，忽略处理. Iqiyi：{1} jellyfin: {2}", title, pubYear, itemPubYear);
                continue;
            }

            return video.LinkId;
        }

        return null;
    }


    public override async Task<ScraperMedia?> GetMedia(BaseItem item, string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        // id是编码后的，需要还原为真实id
        var isMovieItemType = item is MediaBrowser.Controller.Entities.Movies.Movie;
        var tvId = await _api.GetTvId(id, !isMovieItemType, CancellationToken.None);
        if (string.IsNullOrEmpty(tvId))
        {
            return null;
        }

        var video = await _api.GetVideoAsync(tvId, CancellationToken.None).ConfigureAwait(false);
        if (video == null)
        {
            log.LogInformation("[{0}]获取不到视频信息：id={1}", this.Name, id);
            return null;
        }


        var media = new ScraperMedia();
        media.Id = video.LinkId;  // 使用url编码后的id，movie使用vid，电视剧使用aid
        if (isMovieItemType && video.Epsodelist != null && video.Epsodelist.Count > 0)
        {
            media.CommentId = $"{video.Epsodelist[0].TvId}";
        }
        if (video.Epsodelist != null && video.Epsodelist.Count > 0)
        {
            foreach (var ep in video.Epsodelist)
            {
                media.Episodes.Add(new ScraperEpisode() { Id = $"{ep.LinkId}", CommentId = $"{ep.TvId}" });
            }
        }

        return media;
    }

    /// <inheritdoc />
    public override async Task<ScraperEpisode?> GetMediaEpisode(BaseItem item, string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        // id是编码后的，需要还原为真实id
        var tvId = await _api.GetTvId(id, false, CancellationToken.None);
        if (string.IsNullOrEmpty(tvId))
        {
            return null;
        }

        return new ScraperEpisode() { Id = id, CommentId = tvId };
    }

    public override async Task<ScraperDanmaku?> GetDanmuContent(BaseItem item, string commentId)
    {
        if (string.IsNullOrEmpty(commentId))
        {
            return null;
        }

        var comments = await _api.GetDanmuContentAsync(commentId, CancellationToken.None).ConfigureAwait(false);
        var danmaku = new ScraperDanmaku();
        danmaku.ChatId = commentId.ToLong();
        danmaku.ChatServer = "cmts.iqiyi.com";
        foreach (var comment in comments)
        {
            try
            {
                var danmakuText = new ScraperDanmakuText();
                danmakuText.Progress = (int)comment.ShowTime * 1000;
                danmakuText.Mode = 1;
                danmakuText.MidHash = $"[iqiyi]{comment.UserInfo.Uid}";
                danmakuText.Id = comment.ContentId.ToLong();
                danmakuText.Content = comment.Content;
                if (uint.TryParse(comment.Color, System.Globalization.NumberStyles.HexNumber, null, out var color))
                {
                    danmakuText.Color = color;
                }

                danmaku.Items.Add(danmakuText);
            }
            catch (Exception ex)
            {

            }

        }

        return danmaku;
    }


    private string NormalizeSearchName(string name)
    {
        // 去掉可能存在的季名称
        return Regex.Replace(name, @"\s*第.季", "");
    }
}

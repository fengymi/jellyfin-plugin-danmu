using System;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Dto;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Danmu.Scrapers;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Danmu.Core.Extensions;
using System.Text.RegularExpressions;
using System.Xml;
using Jellyfin.Plugin.Danmu.Model.Self;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Danmu.Controllers
{
    [ApiController]
    [AllowAnonymous]
    public class DanmuController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly LibraryManagerEventsHelper _libraryManagerEventsHelper;
        private readonly IFileSystem _fileSystem;
        private readonly ScraperManager _scraperManager;

        private readonly ILogger<DanmuController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DanmuController"/> class.
        /// </summary>
        /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        public DanmuController(
            IFileSystem fileSystem,
            ILoggerFactory loggerFactory,
            LibraryManagerEventsHelper libraryManagerEventsHelper,
            ILibraryManager libraryManager,
            ScraperManager scraperManager)
        {
            _fileSystem = fileSystem;
            _logger = loggerFactory.CreateLogger<DanmuController>();
            _libraryManager = libraryManager;
            _libraryManagerEventsHelper = libraryManagerEventsHelper;
            _scraperManager = scraperManager;
        }

        /// <summary>
        /// 获取弹幕文件内容.
        /// </summary>
        /// <returns>xml弹幕文件内容</returns>
        [Route("/plugin/danmu/{id}")]
        [Route("/api/danmu/{id}")]
        [HttpGet]
        public async Task<DanmuFileInfo> Get(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return new DanmuFileInfo();
            }

            var currentItem = _libraryManager.GetItemById(id);
            if (currentItem == null)
            {
                return new DanmuFileInfo();
            }

            var danmuPath = Path.Combine(currentItem.ContainingFolderPath, currentItem.FileNameWithoutExtension + ".xml");
            var fileMeta = _fileSystem.GetFileInfo(danmuPath);
            if (!fileMeta.Exists)
            {
                return new DanmuFileInfo();
            }

            var domain = Request.Scheme + System.Uri.SchemeDelimiter + Request.Host;
            return new DanmuFileInfo() { Url = string.Format("{0}/api/danmu/{1}/raw", domain, id) };
        }

        /// <summary>
        /// 获取弹幕文件内容.
        /// </summary>
        /// <returns>xml弹幕文件内容</returns>
        [Route("/plugin/danmu/raw/{id}")]
        [Route("/api/danmu/{id}/raw")]
        [HttpGet]
        public async Task<ActionResult> Download(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ResourceNotFoundException();
            }

            var currentItem = _libraryManager.GetItemById(id);
            if (currentItem == null)
            {
                throw new ResourceNotFoundException();
            }

            var danmuPath = Path.Combine(currentItem.ContainingFolderPath, currentItem.FileNameWithoutExtension + ".xml");
            var fileMeta = _fileSystem.GetFileInfo(danmuPath);
            if (!fileMeta.Exists)
            {
                throw new ResourceNotFoundException();
            }

            return File(System.IO.File.ReadAllBytes(danmuPath), "text/xml");
        }

        /// <summary>
        /// 获取弹幕文件内容.
        /// </summary>
        /// <returns>xml弹幕文件内容</returns>
        [Route("/api/danmu/supportsites")]
        [HttpGet]
        public Task<DanmuResultDto> GetAllSupportSite()
        {
            DanmuResultDto result = new DanmuResultDto();
            var allWithNoEnabled = this._scraperManager.AllWithNoEnabled();
            List<DanmuSourceDto> sources = new List<DanmuSourceDto>(allWithNoEnabled.Count);
            foreach (AbstractScraper scraper in allWithNoEnabled)
            {
                DanmuSourceDto source = new DanmuSourceDto
                {
                    Source = scraper.ProviderId,
                    SourceName = scraper.ProviderName,
                    Opened = scraper.DefaultEnable,
                };
                sources.Add(source);
            }

            result.Data = sources;
            return Task.FromResult(result);
        }

        /// <summary>
        /// 获取弹幕文件内容.
        /// </summary>
        /// <returns>xml弹幕文件内容</returns>
        [Route("/plugin/danmu/json/{id}")]
        [Route("/api/danmu/{id}/json")]
        [HttpGet]
        [HttpPost]
        public async Task<DanmuResultDto> GetByJson(string id, DanmuParams danmuParams)
        {
            _logger.LogInformation("请求参数 id={0}, site={1}", id, danmuParams.NeedSites);
            ArgumentNullException.ThrowIfNull(id);
            if (string.IsNullOrEmpty(id))
            {
                throw new ResourceNotFoundException();
            }

            var currentItem = this._libraryManager.GetItemById(id);
            if (currentItem == null)
            {
                throw new ResourceNotFoundException();
            }

            DanmuResultDto danmuResultDto = new DanmuResultDto();
            List<string> sites;
            if (danmuParams == null || danmuParams.NeedSites == null || danmuParams.NeedSites.Count == 0)
            {
                var count = this._scraperManager.All().Count;
                if (count == 0)
                {
                    return danmuResultDto;
                }

                sites = this._scraperManager
                    .All()
                    .Select(s => s.ProviderId)
                    .ToList();
            }
            else
            {
                sites = danmuParams.NeedSites;
            }

            List<DanmuSourceDto> danmuSources = new List<DanmuSourceDto>(sites.Count);
            List<Task<DanmuSourceDto?>> danmuSourceTasks = new List<Task<DanmuSourceDto?>>(sites.Count);
            danmuResultDto.Data = danmuSources;

            foreach (string? site in sites)
            {
                Task<DanmuSourceDto?> danmuSourceTask = this.GetDanmuSourceDto(currentItem, site);
                danmuSourceTasks.Add(danmuSourceTask);
            }

            danmuSourceTasks.Add(this.GetDanmuSourceDto(currentItem, null));

            await Task.WhenAll(danmuSourceTasks).ConfigureAwait(false);
            foreach (Task<DanmuSourceDto?> danmuSourceTask in danmuSourceTasks)
            {
                var danmuSourceDto = danmuSourceTask.GetAwaiter().GetResult();
                if (danmuSourceDto != null && sites.Contains(danmuSourceDto.Source))
                {
                    danmuSources.Add(danmuSourceDto);
                }
            }

            return danmuResultDto;
        }

        /// <summary>
        /// 查找弹幕
        /// </summary>
        [Route("/api/danmu/search")]
        [HttpGet]
        public async Task<IEnumerable<MediaInfo>> SearchDanmu(string keyword)
        {
            var list = new List<MediaInfo>();

            if (string.IsNullOrEmpty(keyword))
            {
                return list;
            }

            
            foreach (var scraper in _scraperManager.All())
            {
                try
                {
                    var scraperId = Regex.Replace(scraper.ProviderId, "ID$", string.Empty).ToLower();
                    var result = await scraper.SearchForApi(keyword).ConfigureAwait(false);
                    foreach (var searchInfo in result)
                    {
                        list.Add(new MediaInfo()
                        {
                            Id = searchInfo.Id,
                            Name = searchInfo.Name,
                            Category = searchInfo.Category,
                            Year = searchInfo.Year == null ? string.Empty : searchInfo.Year.ToString(),
                            EpisodeSize = searchInfo.EpisodeSize,
                            Site = scraper.Name,
                            SiteId = scraperId,
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{0}]Exception handled processing search movie [{1}]", scraper.Name, keyword);
                }
            }

            return list;
        }

        /// <summary>
        /// 查找弹幕
        /// </summary>
        [Route("/api/{site}/danmu/search")]
        [HttpGet]
        public async Task<IEnumerable<MediaInfo>> SearchDanmuBySite(string site, string keyword)
        {
            var list = new List<MediaInfo>();

            if (string.IsNullOrEmpty(keyword))
            {
                return list;
            }

            
            foreach (var scraper in _scraperManager.All())
            {
                try
                {
                    var scraperId = Regex.Replace(scraper.ProviderId, "ID$", string.Empty).ToLower();
                    if (scraperId != site)
                    {
                        continue;
                    }

                    var result = await scraper.SearchForApi(keyword).ConfigureAwait(false);
                    foreach (var searchInfo in result)
                    {
                        list.Add(new MediaInfo()
                        {
                            Id = searchInfo.Id,
                            Name = searchInfo.Name,
                            Category = searchInfo.Category,
                            Year = searchInfo.Year == null ? string.Empty : searchInfo.Year.ToString(),
                            EpisodeSize = searchInfo.EpisodeSize,
                            Site = scraper.Name,
                            SiteId = scraperId,
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{0}]Exception handled processing search movie [{1}]", scraper.Name, keyword);
                }
            }

            return list;
        }

        /// <summary>
        /// 查找弹幕
        /// </summary>
        [Route("/api/{site}/danmu/{id}/episodes")]
        [HttpGet]
        public async Task<IEnumerable<EpisodeInfo>> GetDanmuEpisodesBySite(string site, string id)
        {
            var list = new List<EpisodeInfo>();

            if (string.IsNullOrEmpty(id))
            {
                return list;
            }

            
            foreach (var scraper in _scraperManager.All())
            {
                try
                {
                    var scraperId = Regex.Replace(scraper.ProviderId, "ID$", string.Empty).ToLower();
                    if (scraperId != site)
                    {
                        continue;
                    }

                    var result = await scraper.GetEpisodesForApi(id).ConfigureAwait(false);
                    foreach (var (ep, idx) in result.WithIndex())
                    {
                        list.Add(new EpisodeInfo()
                        {
                            Id = ep.Id,
                            CommentId = ep.CommentId,
                            Number = idx + 1,
                            Title = ep.Title,
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{0}]Exception handled processing get episodes [{1}]", scraper.Name, id);
                }
            }

            return list;
        }



        /// <summary>
        /// 下载弹幕.
        /// </summary>
        [Route("/api/{site}/danmu/{cid}/download")]
        [HttpGet]
        public async Task<ActionResult> DownloadByCommentID(string site, string cid)
        {
            if (string.IsNullOrEmpty(cid))
            {
                throw new ResourceNotFoundException();
            }

            foreach (var scraper in this._scraperManager.All())
            {
                var scraperId = Regex.Replace(scraper.ProviderId, "ID$", string.Empty).ToLower();
                if (scraperId == site)
                {
                    var danmaku = await scraper.DownloadDanmuForApi(cid).ConfigureAwait(false);
                    if (danmaku != null)
                    {
                        var bytes = danmaku.ToXml();
                        return File(bytes, "text/xml");
                    }
                }
            }

            throw new ResourceNotFoundException();
        }

        /// <summary>
        /// 跳转链接.
        /// </summary>
        [Route("goto")]
        [HttpGet]
        public RedirectResult GoTo(string provider, string id, string type)
        {
            var url = $"/";
            switch (provider)
            {
                case "bilibili":
                    if (id.StartsWith("BV"))
                    {
                        url = $"https://www.bilibili.com/video/{id}/";
                    }
                    else
                    {
                        if (type == "movie")
                        {
                            url = $"https://www.bilibili.com/bangumi/play/ep{id}";
                        }
                        else
                        {
                            url = $"https://www.bilibili.com/bangumi/play/ss{id}";
                        }
                    }
                    break;
                default:
                    break;
            }
            return Redirect(url);
        }



        /// <summary>
        /// 重新获取对应的弹幕id.
        /// </summary>
        /// <returns>请求结果</returns>
        [Route("")]
        [HttpGet]
        public async Task<String> Refresh(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ResourceNotFoundException();
            }

            var item = _libraryManager.GetItemById(id);
            if (item == null)
            {
                throw new ResourceNotFoundException();
            }

            if (item is Movie || item is Season)
            {
                _libraryManagerEventsHelper.QueueItem(item, Model.EventType.Add);
                _libraryManagerEventsHelper.QueueItem(item, Model.EventType.Update);
            }

            if (item is Series)
            {
                var seasons = ((Series)item).GetSeasons(null, new DtoOptions(false));
                foreach (var season in seasons)
                {
                    _libraryManagerEventsHelper.QueueItem(season, Model.EventType.Add);
                    _libraryManagerEventsHelper.QueueItem(season, Model.EventType.Update);
                }
            }

            return "ok";
        }
        
        private Task<DanmuSourceDto?> GetDanmuSourceDto(BaseItem currentItem, string? site)
        {
            // return Task.FromResult<DanmuSourceDto>(null);
            var danmuPath = Path.Combine(
                currentItem.ContainingFolderPath,
                currentItem.FileNameWithoutExtension + (site != null ? "_" + site : string.Empty) + ".xml");
            var fileMeta = this._fileSystem.GetFileInfo(danmuPath);
            if (!fileMeta.Exists)
            {
                return Task.FromResult<DanmuSourceDto>(null);
            }
            
            var xmlDocument = new XmlDocument();
            xmlDocument.Load(danmuPath);
            XmlElement? xmlNode = xmlDocument.DocumentElement;
            if (xmlNode == null)
            {
                return Task.FromResult<DanmuSourceDto>(null);
            }
            
            DanmuSourceDto? danmuSourceDto = new DanmuSourceDto();
            List<DanmuEventDTO> danmuEventDtos = new List<DanmuEventDTO>();
            foreach (XmlNode node in xmlNode.ChildNodes) //4.遍历根节点（根节点包含所有节点）
            {
                // _logger.Info("XmlNode.InnerText={0}", node.InnerText);
                if ("sourceprovider".Equals(node.Name))
                {
                    danmuSourceDto.Source = node.InnerText;
                }
                else if ("datasize".Equals(node.Name) && danmuEventDtos.Count == 0)
                {
                    danmuEventDtos = new List<DanmuEventDTO>(int.Parse(node.InnerText));
                }
                else if ("d".Equals(node.Name) && node is XmlElement)
                {
                    DanmuEventDTO danmuEvent = new DanmuEventDTO();
                    danmuEvent.M = node.InnerText;
                    danmuEvent.P = ((XmlElement)node).GetAttribute("p");
                    danmuEventDtos.Add(danmuEvent);
                }
            }
            
            if (danmuSourceDto.Source == null)
            {
                return Task.FromResult<DanmuSourceDto>(null);
            }
            
            danmuSourceDto.DanmuEvents = danmuEventDtos;
            return Task.FromResult(danmuSourceDto);
        }
    }
}

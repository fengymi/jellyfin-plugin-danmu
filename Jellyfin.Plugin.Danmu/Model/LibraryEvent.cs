using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Danmu.Model;

public class LibraryEvent
{
    public BaseItem Item { get; set; }

    public EventType EventType { get; set; }

    public string ProviderId { get; set; }

    public string Id { get; set; }

    /**
     * 刷新 重新下载 (如果有id，使用原有id)
     */
    public bool Refresh { get; set; } = true;

    /**
     * 强制重新下载 (强制重新匹配id下载)
     */
    public bool Force { get; set; }

    /**
     * 全量下载
     */
    public bool All { get; set; }
}
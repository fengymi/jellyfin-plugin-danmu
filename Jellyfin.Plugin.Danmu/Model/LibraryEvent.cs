using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Danmu.Model;

public class LibraryEvent
{
    public BaseItem Item { get; set; }

    public EventType EventType { get; set; }

    public string ProviderId { get; set; }

    public string Id { get; set; }

    /**
     * 强制下载
     */
    public bool Force { get; set; }

    /**
     * 全量下载
     */
    public bool All { get; set; }
}
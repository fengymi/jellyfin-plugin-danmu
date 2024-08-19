namespace Jellyfin.Plugin.Danmu.Model;

public class SubtitleId
{
    public string ItemId { get; set; }

    public string Id { get; set; }

    public string ProviderId { get; set; }

    /**
     * 强制下载
     */
    public bool Force { get; set; }
    
    /**
     * 全量下载
     */
    public bool All { get; set; }
}

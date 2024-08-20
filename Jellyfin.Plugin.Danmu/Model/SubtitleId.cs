namespace Jellyfin.Plugin.Danmu.Model;

public class SubtitleId
{
    public string ItemId { get; set; }

    public string Id { get; set; }

    public string ProviderId { get; set; }

    /**
     * 刷新 重新下载 (如果有id，使用原有id)
     */
    public bool Refresh { get; set; }

    /**
     * 强制重新下载 (强制重新匹配id下载)
     */
    public bool Force { get; set; }

    /**
     * 全量下载
     */
    public bool All { get; set; }
}

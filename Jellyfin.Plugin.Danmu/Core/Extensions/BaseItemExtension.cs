using System.IO;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Danmu.Core.Extensions;

public static class BaseItemExtension
{
    
    public static string GetDanmuXmlPath(this BaseItem item, string providerId = "")
    {
        if (string.IsNullOrEmpty(providerId))
        {
            return Path.Combine(item.ContainingFolderPath, item.FileNameWithoutExtension + ".xml");
        }
        return Path.Combine(item.ContainingFolderPath, item.FileNameWithoutExtension + "_" + providerId + ".xml");
    }
    
    public static string GetDanmuAssPath(this BaseItem item, string providerId = "")
    {
        if (string.IsNullOrEmpty(providerId))
        {
            return Path.Combine(item.ContainingFolderPath, item.FileNameWithoutExtension + ".danmu.ass");
        }
        return Path.Combine(item.ContainingFolderPath, item.FileNameWithoutExtension + "_" + providerId + ".danmu.ass");
    }
}
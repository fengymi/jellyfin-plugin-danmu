using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Danmu.Model.Self;

public class DanmuParams
{
    
    [JsonPropertyName("needSites")]
    public List<string> NeedSites { get; set; } = new();
}
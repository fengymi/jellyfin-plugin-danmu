using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Danmu.Model.Self;

public class DanmuSourceDto
{
    [JsonPropertyName("source")] public string Source { get; set; }
        
    [JsonPropertyName("sourceName")] public string SourceName { get; set; }
        
    [JsonPropertyName("opened")] public bool Opened { get; set; }

    /**
     *
     */
    [JsonPropertyName("danmuEvents")]
    public List<DanmuEventDTO> DanmuEvents { get; set; }
}
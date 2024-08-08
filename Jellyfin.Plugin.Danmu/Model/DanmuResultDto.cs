using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Danmu.Model;

public class DanmuResultDto
{
    [JsonPropertyName("hasNext")]
    public bool HasNext
    {
        get => false;
        set { }
    }

    [JsonPropertyName("data")]
    public List<DanmuSourceDto> Data { get; set; }

    [JsonPropertyName("extra")]
    public string Extra { get; set; }
}
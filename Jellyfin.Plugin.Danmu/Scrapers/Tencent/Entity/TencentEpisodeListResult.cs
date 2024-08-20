using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Danmu.Scrapers.Tencent.Entity;

public class TencentEpisodeListResult
{
    [JsonPropertyName("data")]
    public TencentModuleDataList Data { get; set; }
}

public class TencentModuleDataList
{
    [JsonPropertyName("module_list_datas")]
    public List<TencentModuleList> ModuleListDatas { get; set; }
}

public class TencentModuleList
{
    [JsonPropertyName("module_datas")]
    public List<TencentModule> ModuleDatas { get; set; }
}

public class TencentModule
{
    [JsonPropertyName("item_data_lists")]
    public TencentModuleItemList ItemDataLists { get; set; }

    [JsonPropertyName("module_params")]
    public TencentModuleParams? ModuleParams { get; set; }
}

public class TencentModuleItemList
{
    [JsonPropertyName("item_datas")]
    public List<TencentModuleItem> ItemDatas { get; set; }
}

public class TencentModuleParams
{
    [JsonPropertyName("tabs")]
    [JsonConverter(typeof(TabDataConverter))]
    public List<TencentModuleParamsTab>? ParamsTabs { get; set; }
}

public class TencentModuleParamsTab
{
    
    /// <summary>
    /// Gets or sets
    /// chapter_name=&cid=m441e3rjq9kwpsc&detail_page_type=0&episode_begin=1&episode_end=100&episode_step=100&filter_rule_id=&id_type=1&is_nocopyright=false&is_skp_style=false&lid=1&list_page_context=&mvl_strategy_id=&need_tab=1&order=&page_num=0&page_size=100&req_from=web_mobile&req_from_second_type=&req_type=0&siteName=&tab_type=1&title_style=&ui_type=null&un_strategy_id=13dc6f30819942eb805250fb671fb082&watch_together_pay_status=0&year=
    /// </summary>
    [JsonPropertyName("page_context")]
    public string? PageContext { get; set; }
}

public class TencentModuleItem
{
    [JsonPropertyName("item_id")]
    public string ItemId { get; set; }
    [JsonPropertyName("item_type")]
    public string ItemType { get; set; }
    [JsonPropertyName("item_params")]
    public TencentEpisode ItemParams { get; set; }
}

class TabDataConverter : JsonConverter<List<TencentModuleParamsTab>>
{
    /// <inheritdoc/>
    public override List<TencentModuleParamsTab>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? originStr = reader.GetString();
        if (string.IsNullOrEmpty(originStr))
        {
            return null;
        }
        
        TencentApi._logger_2.LogInformation("获取json数据 originStr={originStr}", originStr);
        try
        {

            List<TencentModuleParamsTab>? array = JsonSerializer.Deserialize<List<TencentModuleParamsTab>>(originStr);
            return array;
        }
        catch (Exception e)
        {
            TencentApi._logger_2.LogError(e, "解析json失败 originStr={originStr}", originStr);
        }

        return null;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, List<TencentModuleParamsTab> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}

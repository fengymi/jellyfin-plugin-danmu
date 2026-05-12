using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Danmu.Model;

public class LibraryEvent : IEquatable<LibraryEvent>
{
    public BaseItem Item { get; set; }

    public EventType EventType { get; set; }

    public bool Equals(LibraryEvent? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Item?.Id == other.Item?.Id && EventType == other.EventType;
    }

    public override bool Equals(object? obj)
    {
        return obj is LibraryEvent other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Item?.Id, EventType);
    }

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
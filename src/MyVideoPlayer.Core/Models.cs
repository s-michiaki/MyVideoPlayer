namespace MyVideoPlayer.Core;

/// <summary>タイムスタンプの出どころ</summary>
public enum MarkerSource
{
    /// <summary>MP4/MKV などに埋め込まれたチャプター</summary>
    Chapter,
    /// <summary>説明文・コメント内の「12:34 タイトル」形式の記述</summary>
    Description,
}

/// <summary>再生位置のマーカー（チャプターまたは説明文中のタイムスタンプ）</summary>
public sealed record TimeMarker(TimeSpan Start, TimeSpan? End, string Title, MarkerSource Source)
{
    public string StartText => TimeFormat.Format(Start);
}

/// <summary>ffprobe で取得した動画のメタデータ</summary>
public sealed class MediaInfo
{
    public TimeSpan? Duration { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset? CreationTime { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? VideoCodec { get; init; }

    /// <summary>説明文（description / synopsis / comment タグのうち最も情報量の多いもの）</summary>
    public string? Description { get; init; }

    public IReadOnlyList<TimeMarker> Chapters { get; init; } = Array.Empty<TimeMarker>();
    public IReadOnlyList<TimeMarker> DescriptionTimestamps { get; init; } = Array.Empty<TimeMarker>();

    public bool HasMarkers => Chapters.Count > 0 || DescriptionTimestamps.Count > 0;

    public static MediaInfo Empty { get; } = new();
}

public static class TimeFormat
{
    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }
}

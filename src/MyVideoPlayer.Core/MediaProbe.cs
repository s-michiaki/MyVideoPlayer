using System.Globalization;
using System.Text.Json;

namespace MyVideoPlayer.Core;

/// <summary>ffprobe の JSON 出力から長さ・チャプター・説明文を取り出す</summary>
public static class MediaProbe
{
    /// <summary>説明文として扱うタグ名（大文字小文字は区別しない）。先にあるほど優先。</summary>
    private static readonly string[] DescriptionTagNames =
        { "description", "synopsis", "comment", "ldes", "desc", "summary" };

    public static IReadOnlyList<string> FfprobeArguments(string file) => new[]
    {
        "-v", "error",
        "-print_format", "json",
        "-show_format", "-show_chapters", "-show_streams",
        file,
    };

    public static async Task<string?> RunFfprobeAsync(FfmpegTools tools, string file, CancellationToken ct)
    {
        if (tools.FfprobePath is null) return null;
        var (code, stdout, _) = await FfmpegTools.RunAsync(tools.FfprobePath, FfprobeArguments(file), ct,
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return code == 0 && stdout.Length > 0 ? stdout : null;
    }

    public static MediaInfo Parse(string ffprobeJson)
    {
        using var doc = JsonDocument.Parse(ffprobeJson);
        var root = doc.RootElement;

        TimeSpan? duration = null;
        string? title = null;
        DateTimeOffset? created = null;
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("format", out var format))
        {
            duration = ReadSeconds(format, "duration");
            if (format.TryGetProperty("tags", out var t)) CollectTags(t, tags);
        }

        int? width = null, height = null;
        string? codec = null, audioCodec = null;
        bool hasVideo = false, hasCover = false;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                if (type == "audio")
                {
                    if (audioCodec is null && s.TryGetProperty("codec_name", out var acn)) audioCodec = acn.GetString();
                    duration ??= ReadSeconds(s, "duration");
                    continue;
                }
                if (type != "video") continue;

                // m4a などのカバー画像は「映像ストリーム」として見えるので区別する
                if (IsAttachedPicture(s)) { hasCover = true; continue; }
                if (hasVideo) continue;
                hasVideo = true;

                if (s.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi)) width = wi;
                if (s.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi)) height = hi;
                if (s.TryGetProperty("codec_name", out var cn)) codec = cn.GetString();
                duration ??= ReadSeconds(s, "duration");
                // MKV ではストリーム側にタグが入ることがある（フォーマット側を優先）
                if (s.TryGetProperty("tags", out var st))
                {
                    var streamTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    CollectTags(st, streamTags);
                    foreach (var kv in streamTags) tags.TryAdd(kv.Key, kv.Value);
                }
            }
        }

        title = tags.GetValueOrDefault("title");
        if (tags.TryGetValue("creation_time", out var ctText) &&
            DateTimeOffset.TryParse(ctText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            created = dto;

        var chapters = ParseChapters(root, duration);
        var description = PickDescription(tags);
        var descTimestamps = TimestampParser.Parse(description, duration);

        return new MediaInfo
        {
            Duration = duration,
            Title = title,
            CreationTime = created,
            Width = width,
            Height = height,
            VideoCodec = codec,
            AudioCodec = audioCodec,
            IsAudioOnly = !hasVideo && audioCodec is not null,
            HasCoverArt = hasCover,
            Description = description,
            Chapters = chapters,
            DescriptionTimestamps = descTimestamps,
        };
    }

    private static IReadOnlyList<TimeMarker> ParseChapters(JsonElement root, TimeSpan? duration)
    {
        if (!root.TryGetProperty("chapters", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<TimeMarker>();

        var list = new List<TimeMarker>();
        int index = 0;
        foreach (var ch in arr.EnumerateArray())
        {
            index++;
            var start = ReadSeconds(ch, "start_time") ?? TimeSpan.Zero;
            var end = ReadSeconds(ch, "end_time");
            string? name = null;
            if (ch.TryGetProperty("tags", out var t))
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                CollectTags(t, d);
                name = d.GetValueOrDefault("title");
            }
            if (duration is { } dur && end is { } e && e > dur) end = dur;
            list.Add(new TimeMarker(start, end, string.IsNullOrWhiteSpace(name) ? $"チャプター {index}" : name.Trim(),
                MarkerSource.Chapter));
        }
        return list.OrderBy(c => c.Start).ToList();
    }

    private static string? PickDescription(Dictionary<string, string> tags)
    {
        // 複数のタグに同じ説明が入っていることが多い（yt-dlp など）。
        // タイムスタンプを最も多く含むもの → 最も長いもの、の順で選ぶ。
        var candidates = DescriptionTagNames
            .Select(n => tags.GetValueOrDefault(n))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct()
            .ToList();
        if (candidates.Count == 0) return null;

        return candidates
            .OrderByDescending(c => TimestampParser.Parse(c).Count)
            .ThenByDescending(c => c.Length)
            .First();
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var d)
        && d.TryGetProperty("attached_pic", out var ap)
        && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1;

    private static void CollectTags(JsonElement tagsElement, Dictionary<string, string> into)
    {
        if (tagsElement.ValueKind != JsonValueKind.Object) return;
        foreach (var p in tagsElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            // "description-eng" のような言語付きキーも拾う
            var key = p.Name;
            var dash = key.IndexOf('-');
            if (dash > 0) into.TryAdd(key[..dash], p.Value.GetString()!);
            into.TryAdd(key, p.Value.GetString()!);
        }
    }

    private static TimeSpan? ReadSeconds(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) && sec >= 0 && !double.IsInfinity(sec)
            ? TimeSpan.FromSeconds(sec)
            : null;
    }
}

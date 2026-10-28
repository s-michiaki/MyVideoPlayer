using System.Text.RegularExpressions;

namespace MyVideoPlayer.Core;

/// <summary>
/// 説明文から「00:00 イントロ」「1:02:03 - まとめ」「[12:34] 本編」のような
/// タイムスタンプ付きの行を抜き出す。
/// </summary>
public static partial class TimestampParser
{
    // h:mm:ss / m:ss / mm:ss。前後が数字やコロンでないこと（日付・時刻の一部を誤検出しないため）
    [GeneratedRegex(@"(?<![\d:：])(?:(?<h>\d{1,2})[:：])?(?<m>\d{1,2})[:：](?<s>[0-5]\d)(?![\d:：])")]
    private static partial Regex TimeRegex();

    private static readonly char[] TrimChars =
        { ' ', '\t', '　', '-', '–', '—', '―', '－', ':', '：', '|', '｜', '・', '>', '▶', '►', '→', '~', '〜', '=', '.', ',' };

    private static readonly (char Open, char Close)[] Brackets =
        { ('(', ')'), ('[', ']'), ('（', '）'), ('【', '】'), ('「', '」'), ('<', '>') };

    /// <param name="text">説明文</param>
    /// <param name="duration">動画の長さ。分かれば、それを超える時刻は除外する</param>
    public static IReadOnlyList<TimeMarker> Parse(string? text, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<TimeMarker>();

        var found = new List<(TimeSpan Time, string Title)>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.Length > 300) continue;

            var match = TimeRegex().Match(line);
            if (!match.Success) continue;

            int h = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value) : 0;
            int m = int.Parse(match.Groups["m"].Value);
            int s = int.Parse(match.Groups["s"].Value);
            if (match.Groups["h"].Success && m > 59) continue;

            var time = new TimeSpan(h, m, s);
            if (duration is { } d && d > TimeSpan.Zero && time > d) continue;

            found.Add((time, ExtractTitle(line, match)));
        }

        // 同じ時刻は最初の1件だけ採用し、時刻順に並べる
        var ordered = found
            .GroupBy(f => f.Time)
            .Select(g => g.First())
            .OrderBy(f => f.Time)
            .ToList();

        var result = new List<TimeMarker>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            TimeSpan? end = i + 1 < ordered.Count ? ordered[i + 1].Time : duration;
            var title = ordered[i].Title.Length > 0 ? ordered[i].Title : $"#{i + 1}";
            result.Add(new TimeMarker(ordered[i].Time, end, title, MarkerSource.Description));
        }
        return result;
    }

    private static string ExtractTitle(string line, Match match)
    {
        var before = line[..match.Index];
        var after = line[(match.Index + match.Length)..];

        // 「00:00 - 01:30 イントロ」のような範囲表記は、後ろ側の時刻も取り除く
        var second = TimeRegex().Match(after);
        if (second.Success && after[..second.Index].Trim(TrimChars).Length == 0)
            after = after[(second.Index + second.Length)..];

        before = StripBrackets(before.Trim(TrimChars), atEnd: true);
        after = StripBrackets(after.Trim(TrimChars), atEnd: false);

        // タイトルは時刻の後ろに書かれることが多いが、前にしか無ければ前を使う
        var title = after.Trim(TrimChars).Length > 0 ? after : before;
        return title.Trim(TrimChars);
    }

    /// <summary>時刻を囲んでいた括弧の片割れ（"[" や "】"）を取り除く</summary>
    private static string StripBrackets(string s, bool atEnd)
    {
        foreach (var (open, close) in Brackets)
        {
            if (atEnd && s.EndsWith(open)) s = s[..^1];
            if (!atEnd && s.StartsWith(close)) s = s[1..];
        }
        return s.Trim(TrimChars);
    }
}

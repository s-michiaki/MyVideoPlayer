using System.Text.Json;

namespace MyVideoPlayer.Core;

/// <summary>%LOCALAPPDATA%\MyVideoPlayer\settings.json に保存する設定</summary>
public sealed class AppSettings
{
    public string? LastFolder { get; set; }
    public List<string> RecentFolders { get; set; } = new();
    public bool IncludeSubfolders { get; set; } = true;
    public string SortMode { get; set; } = "Name";
    public int Volume { get; set; } = 80;
    public double ThumbnailWidth { get; set; } = 240;
    public bool ResumePlayback { get; set; } = true;
    /// <summary>ffmpeg / ffprobe を置いたフォルダ（空なら自動検出）</summary>
    public string? FfmpegDirectory { get; set; }
    /// <summary>動画ごとの再生位置（秒）。キーはフルパス。</summary>
    public Dictionary<string, double> ResumePositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxRecent = 10;
    private const int MaxResume = 500;

    public void AddRecentFolder(string folder)
    {
        RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > MaxRecent) RecentFolders.RemoveRange(MaxRecent, RecentFolders.Count - MaxRecent);
    }

    public void SetResume(string path, TimeSpan position, TimeSpan? duration)
    {
        // 冒頭 10 秒未満、または終わりの 15 秒以内なら「最初から」扱いにして記録を消す
        if (position.TotalSeconds < 10 || (duration is { } d && d - position < TimeSpan.FromSeconds(15)))
        {
            ResumePositions.Remove(path);
            return;
        }
        ResumePositions[path] = position.TotalSeconds;
        if (ResumePositions.Count > MaxResume)
        {
            foreach (var k in ResumePositions.Keys.Take(ResumePositions.Count - MaxResume).ToList())
                ResumePositions.Remove(k);
        }
    }

    public TimeSpan? GetResume(string path) =>
        ResumePositions.TryGetValue(path, out var s) ? TimeSpan.FromSeconds(s) : null;

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyVideoPlayer");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load(string? path = null)
    {
        path ??= Path.Combine(DefaultDataDirectory, "settings.json");
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
                s.ResumePositions = new Dictionary<string, double>(s.ResumePositions, StringComparer.OrdinalIgnoreCase);
                return s;
            }
        }
        catch (Exception) { /* 壊れていたら初期値で起動 */ }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= Path.Combine(DefaultDataDirectory, "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception) { }
    }
}

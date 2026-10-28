using CommunityToolkit.Mvvm.ComponentModel;
using MyVideoPlayer.Core;

namespace MyVideoPlayer.App.ViewModels;

/// <summary>一覧に並ぶ動画1本分</summary>
public sealed partial class VideoItemViewModel : ObservableObject
{
    public VideoFile File { get; }

    public VideoItemViewModel(VideoFile file)
    {
        File = file;
    }

    public string Name => System.IO.Path.GetFileNameWithoutExtension(File.Name);
    public string Extension => System.IO.Path.GetExtension(File.Name).TrimStart('.').ToUpperInvariant();
    public string FullPath => File.FullPath;
    public string Folder => File.RelativeDirectory;
    public bool HasFolder => File.RelativeDirectory.Length > 0;
    public string SizeText => FormatSize(File.SizeBytes);
    public string DateText => File.LastWriteTime.ToString("yyyy/MM/dd HH:mm");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(ChapterCount), nameof(TimestampCount),
        nameof(ResolutionText), nameof(ToolTipText), nameof(IsAudio))]
    private MediaInfo? _info;

    [ObservableProperty]
    private string? _thumbnailPath;

    [ObservableProperty]
    private bool _isLoading = true;

    /// <summary>途中まで見た割合（0〜1）。0 なら非表示。</summary>
    [ObservableProperty]
    private double _resumeRatio;

    /// <summary>m4a などの音声ファイル（一覧では音符アイコン／カバー画像で表示）</summary>
    public bool IsAudio => Info?.IsAudioOnly ?? VideoScanner.IsAudioExtension(File.FullPath);

    public string? DurationText => Info?.Duration is { } d ? TimeFormat.Format(d) : null;
    public int ChapterCount => Info?.Chapters.Count ?? 0;
    public int TimestampCount => Info?.DescriptionTimestamps.Count ?? 0;

    public string? ResolutionText => IsAudio ? "音声" : Info is { Height: { } h }
        ? h switch { >= 2100 => "4K", >= 1400 => "1440p", >= 1000 => "1080p", >= 700 => "720p", _ => $"{h}p" }
        : null;

    public string ToolTipText
    {
        get
        {
            var lines = new List<string> { File.Name };
            if (HasFolder) lines.Add($"フォルダ: {Folder}");
            lines.Add($"{SizeText}  ·  {DateText}");
            if (Info is not null)
            {
                if (Info.IsAudioOnly) lines.Add($"音声  {Info.AudioCodec?.ToUpperInvariant()}");
                else if (Info.Width is { } w && Info.Height is { } h) lines.Add($"{w}×{h}  {Info.VideoCodec?.ToUpperInvariant()}");
                if (ChapterCount > 0) lines.Add($"チャプター {ChapterCount} 件");
                if (TimestampCount > 0) lines.Add($"説明文のタイムスタンプ {TimestampCount} 件");
            }
            return string.Join("\n", lines);
        }
    }

    public void UpdateResume(AppSettings settings)
    {
        var pos = settings.GetResume(FullPath);
        ResumeRatio = pos is { } p && Info?.Duration is { } d && d > TimeSpan.Zero
            ? Math.Clamp(p / d, 0, 1)
            : 0;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };
}

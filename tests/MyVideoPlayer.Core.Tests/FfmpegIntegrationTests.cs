using System.Diagnostics;
using MyVideoPlayer.Core;
using Xunit;

namespace MyVideoPlayer.Core.Tests;

/// <summary>
/// 実際に ffmpeg でチャプター・説明文入りの動画を作り、取得できるか確認する。
/// ffmpeg が PATH に無い環境ではスキップ扱い（何もせず成功）。
/// </summary>
public sealed class FfmpegIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FfmpegTools _tools = FfmpegTools.Locate();

    public FfmpegIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string MakeVideo(string name, string? metadataFile, params string[] extra)
    {
        var output = Path.Combine(_dir, name);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=duration=30:size=320x240:rate=10" };
        if (metadataFile is not null) { args.AddRange(new[] { "-i", metadataFile, "-map_metadata", "1", "-map_chapters", "1", "-map", "0" }); }
        args.AddRange(extra);
        args.AddRange(new[] { "-c:v", "libx264", "-pix_fmt", "yuv420p", output });
        var psi = new ProcessStartInfo(_tools.FfmpegPath!) { RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
        return output;
    }

    private string WriteChapterMetadata()
    {
        var path = Path.Combine(_dir, "meta.txt");
        File.WriteAllText(path, """
            ;FFMETADATA1
            title=テスト動画

            [CHAPTER]
            TIMEBASE=1/1000
            START=0
            END=10000
            title=オープニング

            [CHAPTER]
            TIMEBASE=1/1000
            START=10000
            END=20000
            title=本編

            [CHAPTER]
            TIMEBASE=1/1000
            START=20000
            END=30000
            title=エンディング
            """);
        return path;
    }

    [Fact]
    public async Task ReadsEmbeddedChaptersFromMp4()
    {
        if (!_tools.IsAvailable) return;
        var file = MakeVideo("チャプター付き.mp4", WriteChapterMetadata());

        var cache = new MediaCache(_tools, Path.Combine(_dir, "cache"));
        var info = await cache.GetInfoAsync(file);

        Assert.NotNull(info);
        Assert.InRange(info!.Duration!.Value.TotalSeconds, 29, 31);
        Assert.Equal("テスト動画", info.Title);
        Assert.Equal(new[] { "オープニング", "本編", "エンディング" }, info.Chapters.Select(c => c.Title));
        Assert.Equal(TimeSpan.FromSeconds(10), info.Chapters[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(20), info.Chapters[1].End);
        Assert.Equal(320, info.Width);
    }

    [Fact]
    public async Task ReadsDescriptionTimestampsFromMkvAndMp4()
    {
        if (!_tools.IsAvailable) return;
        const string desc = "説明文です\n0:00 はじまり\n0:12 中盤\n0:25 おわり\n9:99 これは無視";
        foreach (var name in new[] { "desc.mkv", "desc.mp4" })
        {
            var file = MakeVideo(name, null, "-metadata", "description=" + desc, "-metadata", "comment=短いコメント");
            var info = await new MediaCache(_tools, Path.Combine(_dir, "cache-" + name)).GetInfoAsync(file);

            Assert.NotNull(info);
            Assert.Contains("説明文です", info!.Description);
            Assert.Equal(new[] { "はじまり", "中盤", "おわり" }, info.DescriptionTimestamps.Select(t => t.Title));
            Assert.Equal(TimeSpan.FromSeconds(12), info.DescriptionTimestamps[1].Start);
            Assert.Empty(info.Chapters);
        }
    }

    [Fact]
    public async Task CreatesThumbnailAndReusesCache()
    {
        if (!_tools.IsAvailable) return;
        var file = MakeVideo("thumb test.mp4", null);
        var cache = new MediaCache(_tools, Path.Combine(_dir, "cache"));
        var info = await cache.GetInfoAsync(file);

        var thumb = await cache.GetThumbnailAsync(file, info!.Duration);
        Assert.NotNull(thumb);
        var bytes = await File.ReadAllBytesAsync(thumb!);
        Assert.True(bytes.Length > 500);
        Assert.Equal(0xFF, bytes[0]); // JPEG
        Assert.Equal(0xD8, bytes[1]);

        var stamp = File.GetLastWriteTimeUtc(thumb!);
        var again = await cache.GetThumbnailAsync(file, info.Duration);
        Assert.Equal(thumb, again);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(again!));
    }

    [Fact]
    public void ScannerFindsVideosRecursively()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "a.MP4"), "x");
        File.WriteAllText(Path.Combine(_dir, "sub", "b.mkv"), "x");
        File.WriteAllText(Path.Combine(_dir, "note.txt"), "x");

        Assert.Single(VideoScanner.Scan(_dir, recursive: false));
        var all = VideoScanner.Scan(_dir, recursive: true);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, v => v.Name == "b.mkv" && v.RelativeDirectory == "sub");
    }

    [Fact]
    public async Task BrokenFileReturnsNullInsteadOfThrowing()
    {
        if (!_tools.IsAvailable) return;
        var bad = Path.Combine(_dir, "broken.mp4");
        File.WriteAllText(bad, "not a video");
        var cache = new MediaCache(_tools, Path.Combine(_dir, "cache"));
        Assert.Null(await cache.GetInfoAsync(bad));
        Assert.Null(await cache.GetThumbnailAsync(bad, null));
    }

    [Fact]
    public void SettingsRoundTripAndResumeRules()
    {
        var path = Path.Combine(_dir, "settings.json");
        var s = new AppSettings();
        s.AddRecentFolder(@"D:\Videos");
        s.SetResume(@"D:\Videos\a.mp4", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));
        s.SetResume(@"D:\Videos\b.mp4", TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(20));
        s.Save(path);

        var loaded = AppSettings.Load(path);
        Assert.Equal(@"D:\Videos", loaded.RecentFolders[0]);
        Assert.Equal(TimeSpan.FromMinutes(5), loaded.GetResume(@"d:\videos\A.mp4"));
        Assert.Null(loaded.GetResume(@"D:\Videos\b.mp4"));
    }
}

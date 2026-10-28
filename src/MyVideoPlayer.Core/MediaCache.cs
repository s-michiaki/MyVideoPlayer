using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MyVideoPlayer.Core;

/// <summary>
/// サムネイル画像と ffprobe 結果をディスクにキャッシュする。
/// キーは「フルパス + サイズ + 更新日時」なので、ファイルが変わると自動的に作り直される。
/// </summary>
public sealed class MediaCache
{
    private readonly FfmpegTools _tools;
    private readonly SemaphoreSlim _ffmpegSlots;

    public string CacheDirectory { get; }

    public MediaCache(FfmpegTools tools, string cacheDirectory, int maxParallel = 3)
    {
        _tools = tools;
        CacheDirectory = cacheDirectory;
        Directory.CreateDirectory(cacheDirectory);
        _ffmpegSlots = new SemaphoreSlim(Math.Max(1, maxParallel));
    }

    public static string KeyFor(FileInfo file)
    {
        var raw = string.Join('|',
            file.FullName.ToUpperInvariant(),
            file.Length.ToString(CultureInfo.InvariantCulture),
            file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    /// <summary>メタデータを取得（キャッシュがなければ ffprobe を実行）。ffprobe が無ければ null。</summary>
    public async Task<MediaInfo?> GetInfoAsync(string path, CancellationToken ct = default)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return null;
        var jsonPath = Path.Combine(CacheDirectory, KeyFor(fi) + ".json");

        if (File.Exists(jsonPath))
        {
            try { return MediaProbe.Parse(await File.ReadAllTextAsync(jsonPath, ct).ConfigureAwait(false)); }
            catch (Exception) { TryDelete(jsonPath); }
        }

        if (_tools.FfprobePath is null) return null;

        await _ffmpegSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = await MediaProbe.RunFfprobeAsync(_tools, path, ct).ConfigureAwait(false);
            if (json is null) return null;
            var info = MediaProbe.Parse(json);
            await File.WriteAllTextAsync(jsonPath, json, ct).ConfigureAwait(false);
            return info;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _ffmpegSlots.Release();
        }
    }

    /// <summary>サムネイル JPEG のパスを返す（なければ ffmpeg で作る）。作れなければ null。</summary>
    public async Task<string?> GetThumbnailAsync(string path, TimeSpan? duration, CancellationToken ct = default)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return null;
        var thumbPath = Path.Combine(CacheDirectory, KeyFor(fi) + ".jpg");
        if (File.Exists(thumbPath) && new FileInfo(thumbPath).Length > 0) return thumbPath;
        if (_tools.FfmpegPath is null) return null;

        await _ffmpegSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 冒頭は黒画面やロゴのことが多いので、長さの 10%（最大 60 秒）地点を使う
            var seek = duration is { } d && d > TimeSpan.Zero
                ? TimeSpan.FromSeconds(Math.Min(d.TotalSeconds * 0.1, 60))
                : TimeSpan.FromSeconds(3);

            foreach (var at in new[] { seek, TimeSpan.Zero })
            {
                var tmp = thumbPath + ".tmp.jpg";
                var (code, _, _) = await FfmpegTools.RunAsync(_tools.FfmpegPath, ThumbnailArguments(path, at, tmp),
                    ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (code == 0 && File.Exists(tmp) && new FileInfo(tmp).Length > 0)
                {
                    File.Move(tmp, thumbPath, overwrite: true);
                    return thumbPath;
                }
                TryDelete(tmp);
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _ffmpegSlots.Release();
        }
    }

    public static IReadOnlyList<string> ThumbnailArguments(string input, TimeSpan at, string output) => new[]
    {
        "-hide_banner", "-loglevel", "error", "-y",
        "-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        "-i", input,
        "-frames:v", "1",
        "-vf", "scale=480:-2",
        "-q:v", "4",
        "-f", "image2",
        output,
    };

    public long GetCacheSizeBytes() =>
        Directory.Exists(CacheDirectory)
            ? new DirectoryInfo(CacheDirectory).EnumerateFiles().Sum(f => f.Length)
            : 0;

    public void Clear()
    {
        if (!Directory.Exists(CacheDirectory)) return;
        foreach (var f in Directory.EnumerateFiles(CacheDirectory)) TryDelete(f);
    }

    private static void TryDelete(string p)
    {
        try { File.Delete(p); } catch { }
    }
}

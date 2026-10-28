using System.Diagnostics;
using System.Text;

namespace MyVideoPlayer.Core;

/// <summary>ffmpeg / ffprobe の場所を探し、実行する</summary>
public sealed class FfmpegTools
{
    public string? FfmpegPath { get; }
    public string? FfprobePath { get; }

    public bool IsAvailable => FfmpegPath is not null && FfprobePath is not null;

    public FfmpegTools(string? ffmpegPath, string? ffprobePath)
    {
        FfmpegPath = ffmpegPath;
        FfprobePath = ffprobePath;
    }

    /// <summary>
    /// 探す順番: 指定フォルダ → アプリと同じフォルダ → アプリ\ffmpeg\ → アプリ\ffmpeg\bin\ → PATH
    /// </summary>
    public static FfmpegTools Locate(string? preferredDirectory = null)
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredDirectory))
        {
            candidates.Add(preferredDirectory);
            candidates.Add(Path.Combine(preferredDirectory, "bin"));
        }
        candidates.Add(baseDir);
        candidates.Add(Path.Combine(baseDir, "ffmpeg"));
        candidates.Add(Path.Combine(baseDir, "ffmpeg", "bin"));

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        candidates.AddRange(pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        string? Find(string name) => candidates
            .Select(dir => SafeCombine(dir, name + exe))
            .FirstOrDefault(p => p is not null && File.Exists(p));

        return new FfmpegTools(Find("ffmpeg"), Find("ffprobe"));
    }

    private static string? SafeCombine(string dir, string file)
    {
        try { return Path.Combine(dir.Trim().Trim('"'), file); }
        catch (ArgumentException) { return null; }
    }

    internal static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string exePath, IEnumerable<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* 権限がなければ無視 */ }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return (proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }
}

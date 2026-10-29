using System.IO;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MyVideoPlayer.Core;

namespace MyVideoPlayer.App;

public partial class App : Application
{
    /// <summary>アプリ全体で共有するサービス</summary>
    public static AppSettings Settings { get; private set; } = new();
    public static FfmpegTools Ffmpeg { get; private set; } = new(null, null);
    public static MediaCache Cache { get; private set; } = null!;
    public static LibVLC LibVlc => _libVlc.Value;

    // LibVLC の初期化（プラグイン読み込み）は 0.5 秒ほどかかるので、最初に再生するときまで遅らせる
    private static readonly Lazy<LibVLC> _libVlc = new(() =>
    {
        // 単一ファイル発行では DLL の場所から libvlc を探せないので、exe の隣の libvlc\win-x64 を明示する
        var libvlcDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        if (Directory.Exists(libvlcDir)) LibVLCSharp.Shared.Core.Initialize(libvlcDir);
        else LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC(
            "--no-video-title-show",   // 再生開始時にファイル名を映像に重ねない
            "--no-snapshot-preview",
            "--avcodec-hw=any");       // 使えればハードウェアデコード
    });

    public static string DataDirectory => AppSettings.DefaultDataDirectory;

    protected override void OnStartup(StartupEventArgs e)
    {
        // CI 用: 発行した exe が単体で起動でき、libvlc を読み込んで再生準備できるかを確かめて終了する
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            base.OnStartup(e);
            _ = RunSelfTestAsync(e.Args[1]);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        Settings = AppSettings.Load();
        ReloadFfmpeg();
        base.OnStartup(e);

        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        // 起動時の引数にフォルダか動画ファイルが渡されたら開く（「送る」やショートカットから）
        if (e.Args.Length > 0) main.OpenFromArgument(e.Args[0]);
    }

    /// <summary>
    /// 画面（XAML）の読み込み、libvlc の初期化、1 秒の無音 WAV の解析と再生開始までを行い、
    /// 結果を resultPath に書いて終了コード 0（成功）/ 1（失敗）で終わる。画面は表示しない。
    /// </summary>
    private async Task RunSelfTestAsync(string resultPath)
    {
        var log = new List<string>();
        int code = 1;
        string? wav = null;
        try
        {
            log.Add($"base={AppContext.BaseDirectory}");
            ReloadFfmpeg();

            // XAML とリソースが読み込めるか（表示はしない）
            _ = new MainWindow();
            log.Add("MainWindow ok");

            var vlc = LibVlc;
            log.Add($"libvlc ok version={vlc.Version}");

            wav = Path.Combine(Path.GetTempPath(), $"mvp-selftest-{Guid.NewGuid():N}.wav");
            WriteSilentWav(wav, seconds: 1);
            using var media = new Media(vlc, wav, FromType.FromPath);
            var status = await media.Parse(MediaParseOptions.ParseLocal, timeout: 10000);
            log.Add($"parse={status} duration={media.Duration}ms");
            if (status != MediaParsedStatus.Done || media.Duration <= 0) throw new Exception("WAV を解析できませんでした");

            using var player = new MediaPlayer(vlc) { Volume = 0 };
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Playing += (_, _) => started.TrySetResult(true);
            player.EncounteredError += (_, _) => started.TrySetResult(false);
            await Task.Run(() => player.Play(media));
            var ok = await Task.WhenAny(started.Task, Task.Delay(10000)) == started.Task && started.Task.Result;
            await Task.Run(() => player.Stop());
            log.Add($"play={(ok ? "ok" : "failed")}");
            if (!ok) throw new Exception("再生を開始できませんでした");

            code = 0;
        }
        catch (Exception ex)
        {
            log.Add($"FAIL: {ex}");
        }
        finally
        {
            try { if (wav is not null) File.Delete(wav); } catch { }
            log.Add(code == 0 ? "SELFTEST PASSED" : "SELFTEST FAILED");
            try { File.WriteAllLines(resultPath, log); } catch { }
            Shutdown(code);
        }
    }

    private static void WriteSilentWav(string path, int seconds)
    {
        const int rate = 8000;
        int dataLen = rate * seconds * 2; // 16bit モノラル
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen); w.Write(new byte[dataLen]);
    }

    public static void ReloadFfmpeg()
    {
        Ffmpeg = FfmpegTools.Locate(Settings.FfmpegDirectory);
        Cache = new MediaCache(Ffmpeg, Path.Combine(DataDirectory, "cache"),
            maxParallel: Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Settings.Save();
        if (_libVlc.IsValueCreated)
        {
            try { _libVlc.Value.Dispose(); } catch { }
        }
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(Path.Combine(DataDirectory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show($"予期しないエラーが発生しました。\n\n{e.Exception.Message}\n\n詳細は {Path.Combine(DataDirectory, "error.log")} に記録しました。",
            "MyVideoPlayer", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}

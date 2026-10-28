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
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC(
            "--no-video-title-show",   // 再生開始時にファイル名を映像に重ねない
            "--no-snapshot-preview",
            "--avcodec-hw=any");       // 使えればハードウェアデコード
    });

    public static string DataDirectory => AppSettings.DefaultDataDirectory;

    protected override void OnStartup(StartupEventArgs e)
    {
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

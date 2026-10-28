using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using MyVideoPlayer.Core;

namespace MyVideoPlayer.App.ViewModels;

/// <summary>
/// 再生画面。LibVLC の MediaPlayer を包む。
/// 注意: LibVLC のイベントは LibVLC 側のスレッドで届くので、UI スレッドに戻してから処理する。
/// また Stop() や Media の切り替えは UI スレッドで呼ぶと固まることがあるため、必ずスレッドプールで行う。
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    public const int ChapterTab = 0, TimestampTab = 1, DescriptionTab = 2, InfoTab = 3;

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private Media? _media;
    private List<VideoItemViewModel> _playlist = new();
    private int _index = -1;
    private bool _fallbackTried;
    private bool _disposed;
    private DateTime _lastResumeSave = DateTime.MinValue;

    public MediaPlayer Player { get; }

    public ObservableCollection<MarkerItem> Chapters { get; } = new();
    public ObservableCollection<MarkerItem> Timestamps { get; } = new();

    public IReadOnlyList<double> RateOptions { get; } = new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 3.0 };

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _subTitle;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isEnded;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private int _volume;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private double _rate = 1.0;
    [ObservableProperty] private string? _currentMarkerTitle;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _infoText = "";
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _hasPrevious;
    [ObservableProperty] private bool _hasNext;
    [ObservableProperty] private string? _errorText;

    /// <summary>シークバーをドラッグ中は、再生位置で上書きしない</summary>
    public bool IsSeeking { get; set; }

    public VideoItemViewModel? Current => _index >= 0 && _index < _playlist.Count ? _playlist[_index] : null;

    /// <summary>チャプターがあればチャプター、なければ説明文のタイムスタンプ</summary>
    public IReadOnlyList<MarkerItem> ActiveMarkers => Chapters.Count > 0 ? Chapters : Timestamps;

    /// <summary>画面に一瞬だけ出すメッセージ（音量 70% など）</summary>
    public event Action<string>? OsdRequested;
    /// <summary>チャプター一覧が変わった（シークバーの目盛りを描き直す）</summary>
    public event Action? MarkersChanged;
    /// <summary>動画が切り替わった（ウィンドウタイトル更新など）</summary>
    public event Action? MediaChanged;

    public PlayerViewModel()
    {
        _ui = Application.Current.Dispatcher;
        Player = new MediaPlayer(App.LibVlc)
        {
            EnableHardwareDecoding = true,
            // マウス・キー操作は WPF 側で処理する
            EnableMouseInput = false,
            EnableKeyInput = false,
        };
        _volume = Math.Clamp(App.Settings.Volume, 0, 100);

        Player.Playing += (_, _) => _ui.BeginInvoke(OnPlaying);
        Player.Paused += (_, _) => _ui.BeginInvoke(() => IsPlaying = false);
        Player.Stopped += (_, _) => _ui.BeginInvoke(() => IsPlaying = false);
        Player.EndReached += (_, _) => _ui.BeginInvoke(OnEnded);
        Player.Buffering += (_, e) => _ui.BeginInvoke(() => IsBuffering = e.Cache < 100);
        Player.LengthChanged += (_, e) => _ui.BeginInvoke(() => SetDuration(TimeSpan.FromMilliseconds(e.Length)));
        Player.EncounteredError += (_, _) => _ui.BeginInvoke(() =>
        {
            IsPlaying = false;
            ErrorText = "この動画を再生できませんでした。";
        });

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // ───────── 開く・切り替え ─────────

    public Task OpenAsync(IReadOnlyList<VideoItemViewModel> playlist, int index)
    {
        _playlist = playlist.ToList();
        return OpenIndexAsync(index, startAt: null);
    }

    private async Task OpenIndexAsync(int index, TimeSpan? startAt)
    {
        if (_disposed || index < 0 || index >= _playlist.Count) return;
        await _switchLock.WaitAsync();
        try
        {
            SaveResume();
            _index = index;
            var item = _playlist[index];
            HasPrevious = index > 0;
            HasNext = index < _playlist.Count - 1;

            Title = item.Name;
            SubTitle = item.HasFolder ? item.Folder : null;
            ErrorText = null;
            IsEnded = false;
            _fallbackTried = false;
            PositionSeconds = 0;
            PositionText = "0:00";

            // メタデータ（一覧で読み込み済みならキャッシュから即座に返る）
            var info = item.Info ?? await App.Cache.GetInfoAsync(item.FullPath);
            item.Info ??= info;
            ApplyInfo(item, info);

            // 続きから再生
            if (startAt is null && App.Settings.ResumePlayback && App.Settings.GetResume(item.FullPath) is { } resume)
            {
                startAt = resume;
                OsdRequested?.Invoke($"続きから再生  {TimeFormat.Format(resume)}   （Home で最初から）");
            }

            var media = new Media(App.LibVlc, item.FullPath, FromType.FromPath);
            if (startAt is { } st && st > TimeSpan.Zero)
                media.AddOption($":start-time={st.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            var old = _media;
            _media = media;
            await Task.Run(() =>
            {
                Player.Play(media);
                old?.Dispose();
            });
            MediaChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorText = $"開けませんでした: {ex.Message}";
        }
        finally
        {
            _switchLock.Release();
        }
    }

    private void ApplyInfo(VideoItemViewModel item, MediaInfo? info)
    {
        Chapters.Clear();
        Timestamps.Clear();
        if (info is not null)
        {
            int n = 0;
            foreach (var c in info.Chapters) Chapters.Add(new MarkerItem(c, ++n));
            n = 0;
            foreach (var t in info.DescriptionTimestamps) Timestamps.Add(new MarkerItem(t, ++n));
        }
        Description = string.IsNullOrWhiteSpace(info?.Description) ? null : info!.Description;
        SetDuration(info?.Duration ?? TimeSpan.Zero);
        CurrentMarkerTitle = null;

        var lines = new List<string>
        {
            $"ファイル: {item.File.Name}",
            $"場所: {Path.GetDirectoryName(item.FullPath)}",
            $"サイズ: {item.SizeText}",
            $"更新日時: {item.DateText}",
        };
        if (info is not null)
        {
            if (info.Duration is { } d) lines.Add($"長さ: {TimeFormat.Format(d)}");
            if (info.Width is { } w && info.Height is { } h) lines.Add($"解像度: {w} × {h}");
            if (info.VideoCodec is { } codec) lines.Add($"映像コーデック: {codec.ToUpperInvariant()}");
            if (!string.IsNullOrWhiteSpace(info.Title)) lines.Add($"タイトル: {info.Title}");
            if (info.CreationTime is { } ct) lines.Add($"作成日時: {ct.ToLocalTime():yyyy/MM/dd HH:mm}");
            lines.Add($"チャプター: {info.Chapters.Count} 件");
            lines.Add($"説明文のタイムスタンプ: {info.DescriptionTimestamps.Count} 件");
        }
        else if (!App.Ffmpeg.IsAvailable)
        {
            lines.Add("");
            lines.Add("※ ffprobe が見つからないため、詳しい情報は再生開始後に VLC から取得します。");
        }
        InfoText = string.Join("\n", lines);

        SelectedTab = Chapters.Count > 0 ? ChapterTab
            : Timestamps.Count > 0 ? TimestampTab
            : Description is not null ? DescriptionTab
            : InfoTab;
        OnPropertyChanged(nameof(ActiveMarkers));
        MarkersChanged?.Invoke();
    }

    private void OnPlaying()
    {
        IsPlaying = true;
        IsEnded = false;
        IsBuffering = false;
        Player.Volume = IsMuted ? 0 : Volume;
        if (Math.Abs(Rate - 1.0) > 0.001) Player.SetRate((float)Rate);
        if (Player.Length > 0) SetDuration(TimeSpan.FromMilliseconds(Player.Length));
        if (!_fallbackTried && Chapters.Count == 0) _ = TryLibVlcChaptersAsync();
    }

    /// <summary>ffprobe でチャプターが取れなかったときは、VLC が認識したチャプターを使う</summary>
    private async Task TryLibVlcChaptersAsync()
    {
        _fallbackTried = true;
        var media = _media;
        var (chapters, description) = await Task.Run(() =>
        {
            try
            {
                var list = Player.FullChapterDescriptions(-1)
                    .Select((c, i) => new TimeMarker(
                        TimeSpan.FromMilliseconds(c.TimeOffset),
                        TimeSpan.FromMilliseconds(c.TimeOffset + c.Duration),
                        string.IsNullOrWhiteSpace(c.Name) ? $"チャプター {i + 1}" : c.Name!,
                        MarkerSource.Chapter))
                    .ToList();
                return (list, media?.Meta(MetadataType.Description));
            }
            catch (Exception)
            {
                return (new List<TimeMarker>(), (string?)null);
            }
        });
        if (media != _media) return; // その間に別の動画に切り替わった

        bool changed = false;
        if (chapters.Count > 1 && Chapters.Count == 0)
        {
            int n = 0;
            foreach (var c in chapters) Chapters.Add(new MarkerItem(c, ++n));
            changed = true;
        }
        if (Description is null && !string.IsNullOrWhiteSpace(description))
        {
            Description = description;
            if (Timestamps.Count == 0)
            {
                int n = 0;
                foreach (var t in TimestampParser.Parse(description, TimeSpan.FromSeconds(DurationSeconds)))
                    Timestamps.Add(new MarkerItem(t, ++n));
            }
            changed = true;
        }
        if (changed)
        {
            if (SelectedTab is InfoTab or DescriptionTab)
                SelectedTab = Chapters.Count > 0 ? ChapterTab : Timestamps.Count > 0 ? TimestampTab : DescriptionTab;
            OnPropertyChanged(nameof(ActiveMarkers));
            MarkersChanged?.Invoke();
        }
    }

    private void OnEnded()
    {
        IsPlaying = false;
        IsEnded = true;
        PositionSeconds = DurationSeconds;
        PositionText = DurationText;
        if (Current is { } item) App.Settings.ResumePositions.Remove(item.FullPath);
    }

    private void SetDuration(TimeSpan d)
    {
        if (d <= TimeSpan.Zero) return;
        var changed = Math.Abs(DurationSeconds - d.TotalSeconds) > 0.5;
        DurationSeconds = d.TotalSeconds;
        DurationText = TimeFormat.Format(d);
        if (changed) MarkersChanged?.Invoke();
    }

    // ───────── 定期更新 ─────────

    private void Tick()
    {
        if (_disposed || _media is null || IsEnded) return;
        var ms = Player.Time;
        if (ms < 0) return;
        var t = TimeSpan.FromMilliseconds(ms);
        if (!IsSeeking)
        {
            PositionSeconds = t.TotalSeconds;
            PositionText = TimeFormat.Format(t);
        }
        UpdateCurrentMarker(t);

        if (IsPlaying && DateTime.UtcNow - _lastResumeSave > TimeSpan.FromSeconds(5))
        {
            _lastResumeSave = DateTime.UtcNow;
            SaveResume();
        }
    }

    private void UpdateCurrentMarker(TimeSpan t)
    {
        string? title = null;
        foreach (var list in new[] { Chapters, Timestamps })
        {
            MarkerItem? current = null;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Marker.Start <= t) { current = list[i]; break; }
            }
            foreach (var m in list) m.IsCurrent = ReferenceEquals(m, current);
            if (title is null && current is not null) title = current.Title;
        }
        CurrentMarkerTitle = title;
    }

    private void SaveResume()
    {
        if (Current is not { } item || _media is null || IsEnded) return;
        var ms = Player.Time;
        if (ms <= 0) return;
        App.Settings.SetResume(item.FullPath, TimeSpan.FromMilliseconds(ms), TimeSpan.FromSeconds(DurationSeconds));
    }

    // ───────── 操作 ─────────

    [RelayCommand]
    public void TogglePlay()
    {
        if (_media is null) return;
        if (IsEnded) { _ = OpenIndexAsync(_index, TimeSpan.Zero); return; }
        if (Player.State is VLCState.Stopped or VLCState.Error) { _ = OpenIndexAsync(_index, null); return; }
        Player.SetPause(IsPlaying);
        OsdRequested?.Invoke(IsPlaying ? "一時停止" : "再生");
    }

    public void SeekTo(TimeSpan t)
    {
        if (_media is null) return;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var max = TimeSpan.FromSeconds(DurationSeconds);
        if (DurationSeconds > 1 && t > max) t = max - TimeSpan.FromMilliseconds(500);

        if (IsEnded || Player.State is VLCState.Stopped or VLCState.Error)
        {
            _ = OpenIndexAsync(_index, t);
            return;
        }
        Player.Time = (long)t.TotalMilliseconds;
        PositionSeconds = t.TotalSeconds;
        PositionText = TimeFormat.Format(t);
        UpdateCurrentMarker(t);
    }

    public void SeekBy(double seconds)
    {
        var target = TimeSpan.FromSeconds(Math.Max(0, PositionSeconds + seconds));
        SeekTo(target);
        OsdRequested?.Invoke($"{(seconds > 0 ? "+" : "−")}{Math.Abs(seconds):0} 秒   {TimeFormat.Format(target)}");
    }

    [RelayCommand] private void SkipForward() => SeekBy(10);
    [RelayCommand] private void SkipBack() => SeekBy(-10);

    [RelayCommand]
    public void JumpToMarker(MarkerItem? marker)
    {
        if (marker is null) return;
        SeekTo(marker.Marker.Start);
        if (!IsPlaying && !IsEnded) Player.SetPause(false);
        OsdRequested?.Invoke($"{marker.StartText}  {marker.Title}");
    }

    [RelayCommand]
    public void NextMarker()
    {
        var t = TimeSpan.FromSeconds(PositionSeconds + 0.5);
        var next = ActiveMarkers.FirstOrDefault(m => m.Marker.Start > t);
        if (next is not null) JumpToMarker(next);
        else if (HasNext) _ = NextVideoCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    public void PreviousMarker()
    {
        var t = TimeSpan.FromSeconds(PositionSeconds);
        var markers = ActiveMarkers;
        if (markers.Count == 0) { SeekTo(TimeSpan.Zero); return; }
        int cur = -1;
        for (int i = markers.Count - 1; i >= 0; i--)
            if (markers[i].Marker.Start <= t) { cur = i; break; }
        // 区間の頭から3秒以上経っていれば区間の頭へ、そうでなければ1つ前へ
        if (cur >= 0 && t - markers[cur].Marker.Start > TimeSpan.FromSeconds(3)) JumpToMarker(markers[cur]);
        else if (cur > 0) JumpToMarker(markers[cur - 1]);
        else SeekTo(TimeSpan.Zero);
    }

    [RelayCommand]
    private Task NextVideo() => HasNext ? OpenIndexAsync(_index + 1, null) : Task.CompletedTask;

    [RelayCommand]
    private Task PreviousVideo() => HasPrevious ? OpenIndexAsync(_index - 1, null) : Task.CompletedTask;

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        OsdRequested?.Invoke(IsMuted ? "ミュート" : $"音量 {Volume}%");
    }

    public void ChangeVolume(int delta)
    {
        Volume = Math.Clamp(Volume + delta, 0, 100);
        if (IsMuted) IsMuted = false;
        OsdRequested?.Invoke($"音量 {Volume}%");
    }

    public void ChangeRate(int step)
    {
        var idx = RateOptions.Select((r, i) => (r, i)).MinBy(x => Math.Abs(x.r - Rate)).i;
        Rate = RateOptions[Math.Clamp(idx + step, 0, RateOptions.Count - 1)];
    }

    partial void OnVolumeChanged(int value)
    {
        App.Settings.Volume = value;
        if (!IsMuted) Player.Volume = value;
    }

    partial void OnIsMutedChanged(bool value) => Player.Volume = value ? 0 : Volume;

    partial void OnRateChanged(double value)
    {
        Player.SetRate((float)value);
        OsdRequested?.Invoke($"再生速度 {value:0.##}x");
    }

    /// <summary>再生を止める（ウィンドウを閉じる前に呼ぶ）。Stop は UI スレッドで呼ぶと固まることがあるのでバックグラウンドで。</summary>
    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        SaveResume();
        _disposed = true;
        _timer.Stop();
        App.Settings.Save();
        await _switchLock.WaitAsync(); // 動画の切り替え中なら終わるのを待つ
        try
        {
            await Task.Run(() => { try { Player.Stop(); } catch { } });
        }
        finally
        {
            _switchLock.Release();
        }
    }

    /// <summary>VideoView から MediaPlayer を外したあとに呼ぶ</summary>
    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        var player = Player;
        var media = _media;
        _media = null;
        Task.Run(() =>
        {
            try { player.Stop(); } catch { }
            try { player.Dispose(); } catch { }
            try { media?.Dispose(); } catch { }
        });
    }
}

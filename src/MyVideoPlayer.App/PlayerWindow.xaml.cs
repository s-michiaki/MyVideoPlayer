using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyVideoPlayer.App.ViewModels;
using MyVideoPlayer.Core;

namespace MyVideoPlayer.App;

public partial class PlayerWindow : Window
{
    private readonly PlayerViewModel _vm = new();
    private readonly TaskCompletionSource _videoViewReady = new();
    private readonly DispatcherTimer _clickTimer;
    private readonly DispatcherTimer _hideControlsTimer;
    private bool _updatingSlider;
    private bool _isFullscreen;
    private WindowState _stateBeforeFullscreen;
    private GridLength _sidePanelWidth = new(340);
    private Point _lastMouse;
    private Window? _overlayWindow;

    public PlayerWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Overlay.DataContext = _vm; // 重ね合わせ用ウィンドウにも ViewModel を渡す

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.OsdRequested += ShowOsd;
        _vm.MarkersChanged += DrawMarkers;
        _vm.MediaChanged += () => Title = $"{_vm.Title} - MyVideoPlayer";

        // シングルクリック（一時停止）とダブルクリック（全画面）を区別するための待ち時間
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); _vm.TogglePlay(); };

        _hideControlsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _hideControlsTimer.Tick += (_, _) => HideControlsIfIdle();

        VideoView.Loaded += (_, _) =>
        {
            // MediaPlayer は VideoView が表示されてから割り当てないと、VLC が別ウィンドウを開いてしまう
            VideoView.MediaPlayer = _vm.Player;
            _videoViewReady.TrySetResult();
        };
        Overlay.Loaded += (_, _) =>
        {
            // 重ね合わせ用ウィンドウにフォーカスがあるときもキー操作を受け付ける
            _overlayWindow = GetWindow(Overlay);
            if (_overlayWindow is not null && _overlayWindow != this)
                _overlayWindow.PreviewKeyDown += OnPreviewKeyDown;
        };

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
    }

    public async Task OpenAsync(IReadOnlyList<VideoItemViewModel> playlist, int index)
    {
        await _videoViewReady.Task;
        await _vm.OpenAsync(playlist, index);
    }

    private bool _closeStarted;
    private bool _canClose;
    private readonly TaskCompletionSource _closed = new();

    /// <summary>閉じて、再生の後片付けが終わるまで待つ</summary>
    public Task CloseAndWaitAsync()
    {
        Close();
        return _closed.Task;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed.TrySetResult();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_canClose) return;
        // 再生を止めてから閉じないと、VLC の映像出力が残ったり終了時に落ちたりする
        e.Cancel = true;
        if (_closeStarted) return;
        _closeStarted = true;
        _ = ShutdownThenCloseAsync();
    }

    private async Task ShutdownThenCloseAsync()
    {
        if (_overlayWindow is not null && _overlayWindow != this)
            _overlayWindow.PreviewKeyDown -= OnPreviewKeyDown;
        _clickTimer.Stop();
        _hideControlsTimer.Stop();
        SeekPreview.IsOpen = false;
        if (_isFullscreen) SetFullscreen(false);
        Hide();
        try
        {
            await _vm.ShutdownAsync();
            VideoView.MediaPlayer = null;
            _vm.Dispose();
        }
        finally
        {
            _canClose = true;
            Close();
        }
    }

    // ───────── ViewModel → 画面 ─────────

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.DurationSeconds):
                _updatingSlider = true;
                SeekSlider.Maximum = Math.Max(1, _vm.DurationSeconds);
                SeekSlider.SmallChange = 5;
                SeekSlider.LargeChange = Math.Max(10, _vm.DurationSeconds / 20);
                _updatingSlider = false;
                break;

            case nameof(PlayerViewModel.PositionSeconds) when !_vm.IsSeeking:
                _updatingSlider = true;
                SeekSlider.Value = Math.Min(_vm.PositionSeconds, SeekSlider.Maximum);
                _updatingSlider = false;
                break;

            case nameof(PlayerViewModel.CurrentMarkerTitle):
                ScrollCurrentMarkerIntoView();
                break;

            case nameof(PlayerViewModel.IsPlaying):
                if (!_vm.IsPlaying) ShowControls();
                else if (_isFullscreen) _hideControlsTimer.Start();
                break;
        }
    }

    private void ScrollCurrentMarkerIntoView()
    {
        foreach (var (list, items) in new[] { (ChapterList, _vm.Chapters), (TimestampList, _vm.Timestamps) })
        {
            if (list.IsMouseOver) continue; // 一覧を操作中ならスクロールさせない
            var current = items.FirstOrDefault(m => m.IsCurrent);
            if (current is not null) list.ScrollIntoView(current);
        }
    }

    /// <summary>シークバーの上にチャプター（なければタイムスタンプ）の目盛りを描く</summary>
    private void DrawMarkers()
    {
        MarkerCanvas.Children.Clear();
        var markers = _vm.ActiveMarkers;
        var width = MarkerCanvas.ActualWidth;
        var duration = _vm.DurationSeconds;
        if (markers.Count == 0 || width <= 0 || duration <= 1) return;

        var accent = (Brush)FindResource("AccentBrush");
        foreach (var m in markers)
        {
            var x = m.Marker.Start.TotalSeconds / duration * width;
            if (x < 1) continue; // 0:00 の目盛りは描かない
            var tick = new Rectangle
            {
                Width = 3,
                Height = 8,
                RadiusX = 1,
                RadiusY = 1,
                Fill = accent,
                Cursor = Cursors.Hand,
                ToolTip = $"{m.StartText}  {m.Title}",
                Tag = m,
            };
            tick.MouseLeftButtonUp += (s, _) => _vm.JumpToMarker((MarkerItem)((FrameworkElement)s).Tag);
            Canvas.SetLeft(tick, x - 1.5);
            MarkerCanvas.Children.Add(tick);
        }
    }

    private void OnMarkerCanvasSizeChanged(object sender, SizeChangedEventArgs e) => DrawMarkers();

    // ───────── シークバー ─────────

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e) => _vm.IsSeeking = true;

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _vm.IsSeeking = false;
        _vm.SeekTo(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void OnSeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider) return;
        if (_vm.IsSeeking)
        {
            // ドラッグ中は時刻表示だけ更新し、離したときにシークする
            _vm.PositionText = TimeFormat.Format(TimeSpan.FromSeconds(e.NewValue));
            return;
        }
        // バーをクリックした
        _vm.SeekTo(TimeSpan.FromSeconds(e.NewValue));
    }

    /// <summary>シークバーにマウスを乗せたら、その位置の時刻とチャプター名を出す</summary>
    private void OnSeekMouseMove(object sender, MouseEventArgs e)
    {
        const double inset = 10; // Slider のつまみの半分（トラックの左右の余白）
        var p = e.GetPosition(SeekSlider);
        var trackWidth = SeekSlider.ActualWidth - inset * 2;
        if (trackWidth <= 0 || _vm.DurationSeconds <= 1) return;

        var ratio = Math.Clamp((p.X - inset) / trackWidth, 0, 1);
        var t = TimeSpan.FromSeconds(ratio * _vm.DurationSeconds);
        SeekPreviewTime.Text = TimeFormat.Format(t);
        var marker = _vm.ActiveMarkers.LastOrDefault(m => m.Marker.Start <= t);
        SeekPreviewTitle.Text = marker?.Title ?? "";
        SeekPreviewTitle.Visibility = marker is null ? Visibility.Collapsed : Visibility.Visible;

        SeekPreview.IsOpen = true;
        if (SeekPreview.Child is FrameworkElement child)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SeekPreview.HorizontalOffset = Math.Clamp(p.X - child.DesiredSize.Width / 2, 0,
                Math.Max(0, SeekSlider.ActualWidth - child.DesiredSize.Width));
            SeekPreview.VerticalOffset = -child.DesiredSize.Height - 12;
        }
    }

    private void OnSeekMouseLeave(object sender, MouseEventArgs e) => SeekPreview.IsOpen = false;

    // ───────── サイドパネル ─────────

    private void OnMarkerClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: MarkerItem m }) _vm.JumpToMarker(m);
    }

    private void OnSidePanelToggled(object sender, RoutedEventArgs e)
    {
        if (SidePanelColumn is null) return;
        SetSidePanelVisible(SidePanelToggle.IsChecked == true);
    }

    private void SetSidePanelVisible(bool visible)
    {
        if (visible)
        {
            SidePanelColumn.Width = _sidePanelWidth;
            SidePanel.Visibility = Visibility.Visible;
            Splitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (SidePanelColumn.Width.Value > 0) _sidePanelWidth = SidePanelColumn.Width;
            SidePanelColumn.Width = new GridLength(0);
            SidePanel.Visibility = Visibility.Collapsed;
            Splitter.Visibility = Visibility.Collapsed;
        }
    }

    // ───────── 映像上のマウス操作 ─────────

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            _clickTimer.Stop();
            ToggleFullscreen();
        }
        else
        {
            _clickTimer.Stop();
            _clickTimer.Start();
        }
        e.Handled = true;
    }

    private void OnOverlayWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.ChangeVolume(e.Delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        if (Math.Abs(p.X - _lastMouse.X) < 3 && Math.Abs(p.Y - _lastMouse.Y) < 3) return;
        _lastMouse = p;
        ShowControls();
    }

    private void OnControlsMouseMove(object sender, MouseEventArgs e) => ShowControls();

    private void ShowControls()
    {
        ControlsPanel.Visibility = Visibility.Visible;
        Overlay.Cursor = null;
        _hideControlsTimer.Stop();
        if (_isFullscreen && _vm.IsPlaying) _hideControlsTimer.Start();
    }

    private void HideControlsIfIdle()
    {
        _hideControlsTimer.Stop();
        if (!_isFullscreen || !_vm.IsPlaying || ControlsPanel.IsMouseOver || RateCombo.IsDropDownOpen) return;
        ControlsPanel.Visibility = Visibility.Collapsed;
        Overlay.Cursor = Cursors.None;
        SeekPreview.IsOpen = false;
    }

    // ───────── 全画面 ─────────

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen() => SetFullscreen(!_isFullscreen);

    private void SetFullscreen(bool on)
    {
        if (on == _isFullscreen) return;
        _isFullscreen = on;
        if (on)
        {
            _stateBeforeFullscreen = WindowState;
            // 最大化中に枠を消すとタスクバーが残るので、一度通常に戻してから
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            TopBar.Visibility = Visibility.Collapsed;
            SetSidePanelVisible(false);
            FullscreenButton.Content = "";
            if (_vm.IsPlaying) _hideControlsTimer.Start();
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFullscreen;
            TopBar.Visibility = Visibility.Visible;
            SetSidePanelVisible(SidePanelToggle.IsChecked == true);
            FullscreenButton.Content = "";
            ShowControls();
        }
    }

    // ───────── OSD ─────────

    private void ShowOsd(string text)
    {
        OsdText.Text = text;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.8))));
        OsdBorder.BeginAnimation(OpacityProperty, anim);
    }

    // ───────── キーボード ─────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // 説明文のテキストを選択・コピーしているときは邪魔しない
        if (e.OriginalSource is TextBox && key is not (Key.Escape or Key.F11) && !(key == Key.Space && !ctrl))
            return;
        if (ctrl && key != Key.W) return;

        switch (key)
        {
            case Key.Space:
            case Key.K:
                _vm.TogglePlay(); break;
            case Key.Left:
                _vm.SeekBy(shift ? -30 : -5); break;
            case Key.Right:
                _vm.SeekBy(shift ? 30 : 5); break;
            case Key.J:
                _vm.SeekBy(-10); break;
            case Key.L:
                _vm.SeekBy(10); break;
            case Key.Up:
                _vm.ChangeVolume(5); break;
            case Key.Down:
                _vm.ChangeVolume(-5); break;
            case Key.M:
                _vm.ToggleMuteCommand.Execute(null); break;
            case Key.F:
            case Key.F11:
                ToggleFullscreen(); break;
            case Key.Escape:
                if (_isFullscreen) SetFullscreen(false); else Close();
                break;
            case Key.W when ctrl:
                Close(); break;
            case Key.PageDown:
                _vm.NextMarker(); break;
            case Key.PageUp:
                _vm.PreviousMarker(); break;
            case Key.N:
                _vm.NextVideoCommand.Execute(null); break;
            case Key.P:
                _vm.PreviousVideoCommand.Execute(null); break;
            case Key.OemCloseBrackets:
                _vm.ChangeRate(+1); break;
            case Key.OemOpenBrackets:
                _vm.ChangeRate(-1); break;
            case Key.Back:
                _vm.Rate = 1.0; break;
            case Key.Home:
                _vm.SeekTo(TimeSpan.Zero); break;
            case Key.End:
                _vm.SeekTo(TimeSpan.FromSeconds(Math.Max(0, _vm.DurationSeconds - 5))); break;
            case Key.C:
            case Key.I:
                if (!_isFullscreen) SidePanelToggle.IsChecked = SidePanelToggle.IsChecked != true;
                break;
            case >= Key.D0 and <= Key.D9:
                // 数字キーで 0%〜90% の位置へ（YouTube と同じ）
                _vm.SeekTo(TimeSpan.FromSeconds(_vm.DurationSeconds * (key - Key.D0) / 10.0)); break;
            default:
                return;
        }
        e.Handled = true;
    }
}

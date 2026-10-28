using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyVideoPlayer.App.ViewModels;
using MyVideoPlayer.Core;

namespace MyVideoPlayer.App;

public partial class MainWindow : Window
{
    private readonly LibraryViewModel _vm = new();
    private PlayerWindow? _player;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PlayRequested += OpenPlayer;

        InputBindings.Add(new KeyBinding(new RelayKey(() => { SearchBox.Focus(); SearchBox.SelectAll(); }),
            Key.F, ModifierKeys.Control));

        Loaded += (_, _) =>
        {
            if (_vm.FolderPath is null && App.Settings.LastFolder is { } last && Directory.Exists(last))
                _ = _vm.LoadFolderAsync(last);
        };
        Closing += async (_, e) =>
        {
            App.Settings.Save();
            if (_player is { } player)
            {
                // プレイヤーの後片付けが終わってからアプリを終了する
                e.Cancel = true;
                await player.CloseAndWaitAsync();
                Close();
            }
        };
    }

    /// <summary>コマンドライン引数（フォルダまたは動画ファイル）を開く</summary>
    public async void OpenFromArgument(string arg)
    {
        if (Directory.Exists(arg))
        {
            await _vm.LoadFolderAsync(arg);
        }
        else if (File.Exists(arg))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(arg))!;
            await _vm.LoadFolderAsync(dir);
            var item = _vm.Items.FirstOrDefault(i => string.Equals(i.FullPath, Path.GetFullPath(arg), StringComparison.OrdinalIgnoreCase));
            if (item is not null) _vm.PlayCommand.Execute(item);
        }
    }

    private void OpenPlayer(IReadOnlyList<VideoItemViewModel> playlist, int index)
    {
        if (_player is null)
        {
            _player = new PlayerWindow { Owner = null };
            _player.Closed += (_, _) =>
            {
                _player = null;
                _vm.RefreshResumeMarks();
                Activate();
            };
            _player.Show();
        }
        else
        {
            if (_player.WindowState == WindowState.Minimized) _player.WindowState = WindowState.Normal;
            _player.Activate();
        }
        _ = _player.OpenAsync(playlist, index);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // スクロールバーなど項目以外のダブルクリックは無視
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(VideoList, d) is ListBoxItem { DataContext: VideoItemViewModel item })
            _vm.PlayCommand.Execute(item);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SelectedItem is { } item)
        {
            _vm.PlayCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        RecentMenu.Items.Clear();
        if (_vm.RecentFolders.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "（まだありません）", IsEnabled = false });
        }
        foreach (var folder in _vm.RecentFolders)
        {
            var path = folder;
            var mi = new MenuItem { Header = new TextBlock { Text = path } }; // TextBlock にしないと "_" がアクセスキー扱いされる
            mi.Click += (_, _) => _vm.OpenRecentCommand.Execute(path);
            RecentMenu.Items.Add(mi);
        }
        RecentMenu.PlacementTarget = RecentButton;
        RecentMenu.IsOpen = true;
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.DataContext = _vm;
            menu.IsOpen = true;
        }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.DataDirectory);
        System.Diagnostics.Process.Start("explorer.exe", $"\"{App.DataDirectory}\"");
    }

    private DispatcherTimer? _resizeTimer;

    /// <summary>サムネイルの大きさを変えたら、並びを計算し直す</summary>
    private void OnThumbSizeChanged(object sender, MouseButtonEventArgs e)
    {
        _resizeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => { _resizeTimer!.Stop(); VideoList.Items.Refresh(); }, Dispatcher);
        _resizeTimer.Stop();
        _resizeTimer.Start();
        App.Settings.Save();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            OpenFromArgument(paths[0]);
    }

    /// <summary>コード上で使う簡単な ICommand</summary>
    private sealed class RelayKey(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}

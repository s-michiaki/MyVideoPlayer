using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyVideoPlayer.Core;

namespace MyVideoPlayer.App.ViewModels;

public sealed record SortOption(string Key, string Label);

/// <summary>一覧画面</summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<VideoItemViewModel> Items { get; } = new();
    public ListCollectionView View { get; }
    public ObservableCollection<string> RecentFolders { get; } = new();

    public IReadOnlyList<SortOption> SortOptions { get; } = new[]
    {
        new SortOption("Name", "名前順"),
        new SortOption("DateDesc", "新しい順"),
        new SortOption("DateAsc", "古い順"),
        new SortOption("SizeDesc", "サイズが大きい順"),
        new SortOption("DurationDesc", "長い順"),
    };

    [ObservableProperty] private string? _folderPath;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _includeSubfolders;
    [ObservableProperty] private SortOption _selectedSort;
    [ObservableProperty] private double _thumbnailWidth;
    [ObservableProperty] private string _statusText = "フォルダを選んでください";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _ffmpegMissing;
    [ObservableProperty] private VideoItemViewModel? _selectedItem;

    public bool HasFolder => FolderPath is not null;

    /// <summary>動画を開くよう依頼された（MainWindow がプレイヤーを開く）</summary>
    public event Action<IReadOnlyList<VideoItemViewModel>, int>? PlayRequested;

    public LibraryViewModel()
    {
        var s = App.Settings;
        _includeSubfolders = s.IncludeSubfolders;
        _thumbnailWidth = Math.Clamp(s.ThumbnailWidth, 160, 420);
        _selectedSort = SortOptions.FirstOrDefault(o => o.Key == s.SortMode) ?? SortOptions[0];
        foreach (var f in s.RecentFolders) RecentFolders.Add(f);
        _ffmpegMissing = !App.Ffmpeg.IsAvailable;

        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        View.Filter = Filter;
        View.CustomSort = new ItemComparer(_selectedSort.Key);
    }

    partial void OnSearchTextChanged(string value)
    {
        View.Refresh();
        UpdateStatus();
    }

    partial void OnSelectedSortChanged(SortOption value)
    {
        App.Settings.SortMode = value.Key;
        View.CustomSort = new ItemComparer(value.Key);
    }

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        App.Settings.IncludeSubfolders = value;
        if (FolderPath is not null) _ = LoadFolderAsync(FolderPath);
    }

    partial void OnThumbnailWidthChanged(double value) => App.Settings.ThumbnailWidth = value;

    partial void OnFolderPathChanged(string? value) => OnPropertyChanged(nameof(HasFolder));

    private bool Filter(object o)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var item = (VideoItemViewModel)o;
        // スペース区切りの全キーワードを含むものだけ（ファイル名とフォルダ名が対象）
        return SearchText.Split(' ', '　', StringSplitOptions.RemoveEmptyEntries)
            .All(k => item.File.Name.Contains(k, StringComparison.CurrentCultureIgnoreCase)
                   || item.Folder.Contains(k, StringComparison.CurrentCultureIgnoreCase));
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "動画を保存しているフォルダを選択",
            InitialDirectory = FolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        if (dlg.ShowDialog() == true) _ = LoadFolderAsync(dlg.FolderName);
    }

    [RelayCommand]
    private void OpenRecent(string? folder)
    {
        if (folder is not null) _ = LoadFolderAsync(folder);
    }

    [RelayCommand]
    private void Refresh()
    {
        if (FolderPath is not null) _ = LoadFolderAsync(FolderPath);
    }

    [RelayCommand]
    private void Play(VideoItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        var list = View.Cast<VideoItemViewModel>().ToList();
        var index = list.IndexOf(item);
        if (index < 0) { list = new() { item }; index = 0; }
        PlayRequested?.Invoke(list, index);
    }

    [RelayCommand]
    private void ShowInExplorer(VideoItemViewModel? item)
    {
        if (item is null) return;
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
    }

    [RelayCommand]
    private void CopyPath(VideoItemViewModel? item)
    {
        if (item is not null) Clipboard.SetText(item.FullPath);
    }

    [RelayCommand]
    private void ChooseFfmpegFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "ffmpeg.exe と ffprobe.exe があるフォルダを選択" };
        if (dlg.ShowDialog() != true) return;
        App.Settings.FfmpegDirectory = dlg.FolderName;
        App.ReloadFfmpeg();
        FfmpegMissing = !App.Ffmpeg.IsAvailable;
        if (FfmpegMissing)
        {
            MessageBox.Show("そのフォルダには ffmpeg.exe と ffprobe.exe の両方が見つかりませんでした。\n（bin フォルダの中も探しています）",
                "MyVideoPlayer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            App.Settings.Save();
            Refresh();
        }
    }

    [RelayCommand]
    private void OpenFfmpegHelp() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.gyan.dev/ffmpeg/builds/") { UseShellExecute = true });

    [RelayCommand]
    private void ClearCache()
    {
        var mb = App.Cache.GetCacheSizeBytes() / (1024.0 * 1024.0);
        if (MessageBox.Show($"サムネイルとメタデータのキャッシュ（{mb:0.0} MB）を削除して作り直しますか？",
                "MyVideoPlayer", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _loadCts?.Cancel();
        App.Cache.Clear();
        Refresh();
    }

    public async Task LoadFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
        {
            MessageBox.Show($"フォルダが見つかりません:\n{folder}", "MyVideoPlayer", MessageBoxButton.OK, MessageBoxImage.Warning);
            RecentFolders.Remove(folder);
            App.Settings.RecentFolders.Remove(folder);
            return;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;

        FolderPath = folder;
        App.Settings.LastFolder = folder;
        App.Settings.AddRecentFolder(folder);
        RecentFolders.Clear();
        foreach (var f in App.Settings.RecentFolders) RecentFolders.Add(f);
        App.Settings.Save();

        IsBusy = true;
        StatusText = "フォルダを読み込み中…";
        Items.Clear();

        IReadOnlyList<VideoFile> files;
        try
        {
            var recursive = IncludeSubfolders;
            files = await Task.Run(() => VideoScanner.Scan(folder, recursive, ct: ct), ct);
        }
        catch (OperationCanceledException) { return; }

        // まとめて追加すると一覧の再描画が1回で済む
        using (View.DeferRefresh())
        {
            foreach (var f in files) Items.Add(new VideoItemViewModel(f));
        }
        UpdateStatus();

        // 表示順にメタデータとサムネイルを読み込む
        var ordered = View.Cast<VideoItemViewModel>().ToList();
        int done = 0;
        try
        {
            await Parallel.ForEachAsync(ordered,
                new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
                async (item, token) =>
                {
                    var info = await App.Cache.GetInfoAsync(item.FullPath, token);
                    var thumb = await App.Cache.GetThumbnailAsync(item.FullPath, info?.Duration, token);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        item.Info = info;
                        item.ThumbnailPath = thumb;
                        item.IsLoading = false;
                        item.UpdateResume(App.Settings);
                        done++;
                        if (done % 10 == 0 || done == ordered.Count)
                            StatusText = done == ordered.Count ? CountText() : $"{CountText()}  ·  サムネイル作成中 {done}/{ordered.Count}";
                    });
                });
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (_loadCts == cts) IsBusy = false;
        }

        // 長さで並べ替え中なら、長さが分かったので並べ直す
        if (SelectedSort.Key == "DurationDesc") View.Refresh();
        UpdateStatus();
    }

    /// <summary>プレイヤーから戻ったとき、途中まで見たバーを更新する</summary>
    public void RefreshResumeMarks()
    {
        foreach (var i in Items) i.UpdateResume(App.Settings);
    }

    private string CountText()
    {
        var shown = View.Count;
        return shown == Items.Count ? $"{Items.Count} 本の動画" : $"{shown} / {Items.Count} 本を表示";
    }

    private void UpdateStatus()
    {
        if (FolderPath is null) return;
        StatusText = Items.Count == 0 ? "このフォルダには動画が見つかりませんでした" : CountText();
    }

    private sealed class ItemComparer(string key) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (VideoItemViewModel)x!;
            var b = (VideoItemViewModel)y!;
            int c = key switch
            {
                "DateDesc" => b.File.LastWriteTime.CompareTo(a.File.LastWriteTime),
                "DateAsc" => a.File.LastWriteTime.CompareTo(b.File.LastWriteTime),
                "SizeDesc" => b.File.SizeBytes.CompareTo(a.File.SizeBytes),
                "DurationDesc" => Nullable.Compare(b.Info?.Duration, a.Info?.Duration),
                _ => 0,
            };
            if (c != 0) return c;
            c = NaturalComparer.Instance.Compare(a.Folder, b.Folder);
            return c != 0 ? c : NaturalComparer.Instance.Compare(a.File.Name, b.File.Name);
        }
    }
}

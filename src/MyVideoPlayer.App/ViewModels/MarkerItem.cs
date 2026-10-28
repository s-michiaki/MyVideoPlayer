using CommunityToolkit.Mvvm.ComponentModel;
using MyVideoPlayer.Core;

namespace MyVideoPlayer.App.ViewModels;

/// <summary>サイドパネルに並ぶチャプター／タイムスタンプ1件</summary>
public sealed partial class MarkerItem : ObservableObject
{
    public TimeMarker Marker { get; }
    public int Number { get; }

    public MarkerItem(TimeMarker marker, int number)
    {
        Marker = marker;
        Number = number;
    }

    public string StartText => Marker.StartText;
    public string Title => Marker.Title;
    public string? LengthText => Marker.End is { } e && e > Marker.Start ? TimeFormat.Format(e - Marker.Start) : null;

    [ObservableProperty]
    private bool _isCurrent;

    public bool Contains(TimeSpan t) =>
        t >= Marker.Start && (Marker.End is not { } e || t < e);
}

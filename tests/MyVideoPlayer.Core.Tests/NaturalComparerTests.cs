using MyVideoPlayer.Core;
using Xunit;

namespace MyVideoPlayer.Core.Tests;

public class NaturalComparerTests
{
    [Fact]
    public void SortsNumbersByValue()
    {
        var names = new[] { "第10話.mp4", "第2話.mp4", "第1話.mp4", "ep02.mkv", "ep1.mkv", "Ep10.mkv", "a.mp4" };
        var sorted = names.OrderBy(n => n, NaturalComparer.Instance).ToArray();
        Assert.Equal(new[] { "a.mp4", "ep1.mkv", "ep02.mkv", "Ep10.mkv", "第1話.mp4", "第2話.mp4", "第10話.mp4" }, sorted);
    }

    [Fact]
    public void FormatsTimes()
    {
        Assert.Equal("0:05", TimeFormat.Format(TimeSpan.FromSeconds(5)));
        Assert.Equal("12:34", TimeFormat.Format(new TimeSpan(0, 12, 34)));
        Assert.Equal("1:02:03", TimeFormat.Format(new TimeSpan(1, 2, 3)));
    }
}

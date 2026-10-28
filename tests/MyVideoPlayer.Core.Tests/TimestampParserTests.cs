using MyVideoPlayer.Core;
using Xunit;

namespace MyVideoPlayer.Core.Tests;

public class TimestampParserTests
{
    [Fact]
    public void ParsesTypicalYoutubeStyleList()
    {
        var text = """
            今日の配信です！

            00:00 オープニング
            02:15 雑談
            1:05:30 - まとめ
            【1:10:00】 エンディング
            """;
        var r = TimestampParser.Parse(text);

        Assert.Equal(4, r.Count);
        Assert.Equal(("オープニング", TimeSpan.Zero), (r[0].Title, r[0].Start));
        Assert.Equal(("雑談", new TimeSpan(0, 2, 15)), (r[1].Title, r[1].Start));
        Assert.Equal(("まとめ", new TimeSpan(1, 5, 30)), (r[2].Title, r[2].Start));
        Assert.Equal(("エンディング", new TimeSpan(1, 10, 0)), (r[3].Title, r[3].Start));
        Assert.Equal(new TimeSpan(0, 2, 15), r[0].End);
        Assert.All(r, m => Assert.Equal(MarkerSource.Description, m.Source));
    }

    [Theory]
    [InlineData("[03:20] Intro", 200, "Intro")]
    [InlineData("(3:20) Intro", 200, "Intro")]
    [InlineData("Intro - 3:20", 200, "Intro")]
    [InlineData("3:20 | Intro", 200, "Intro")]
    [InlineData("0:00 - 3:20 Intro", 0, "Intro")]
    [InlineData("3：20　全角コロン", 200, "全角コロン")]
    public void ParsesVariousFormats(string line, int expectedSeconds, string expectedTitle)
    {
        var r = Assert.Single(TimestampParser.Parse(line));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), r.Start);
        Assert.Equal(expectedTitle, r.Title);
    }

    [Fact]
    public void IgnoresTimesBeyondDuration()
    {
        var r = TimestampParser.Parse("0:00 A\n5:00 B\n99:00 C", TimeSpan.FromMinutes(10));
        Assert.Equal(new[] { "A", "B" }, r.Select(x => x.Title));
        Assert.Equal(TimeSpan.FromMinutes(10), r[^1].End);
    }

    [Theory]
    [InlineData("2024/01/02 12:34:567")]
    [InlineData("バージョン 1.2.3")]
    [InlineData("比率 16:9 で撮影")]
    [InlineData("")]
    public void IgnoresNonTimestamps(string text)
    {
        Assert.Empty(TimestampParser.Parse(text));
    }

    [Fact]
    public void DeduplicatesAndSorts()
    {
        var r = TimestampParser.Parse("5:00 B\n0:00 A\n5:00 B again");
        Assert.Equal(new[] { "A", "B" }, r.Select(x => x.Title));
    }

    [Fact]
    public void UsesPlaceholderWhenNoTitle()
    {
        var r = Assert.Single(TimestampParser.Parse("12:00"));
        Assert.Equal("#1", r.Title);
    }
}

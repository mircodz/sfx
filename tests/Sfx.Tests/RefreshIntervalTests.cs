using Sfx.Tui;

namespace Sfx.Tests;

public class RefreshIntervalTests
{
    [Theory]
    [InlineData("30", 30)]
    [InlineData("2s", 2)]
    [InlineData("1m", 60)]
    [InlineData(" 1H ", 3600)]
    [InlineData("off", 0)]
    [InlineData("0", 0)]
    [InlineData("0s", 0)]
    public void Parses_seconds_minutes_hours_and_off(string text, int seconds)
    {
        Assert.True(RefreshInterval.TryParse(text, out var interval));
        Assert.Equal(TimeSpan.FromSeconds(seconds), interval);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("5d")]
    [InlineData("1.5s")]
    public void Refuses_anything_else(string text)
    {
        Assert.False(RefreshInterval.TryParse(text, out _));
    }

    [Theory]
    [InlineData(0, "off")]
    [InlineData(5, "5s")]
    [InlineData(90, "90s")]
    [InlineData(120, "2m")]
    [InlineData(7200, "2h")]
    public void Formats_the_way_it_parses(int seconds, string expected)
    {
        Assert.Equal(expected, RefreshInterval.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Command_line_takes_the_same_intervals()
    {
        var options = CommandLine.Parse(["--refresh", "2m"], new HashSet<string>());

        Assert.Equal(TimeSpan.FromMinutes(2), options.RefreshInterval);
        Assert.Throws<UsageException>(() => CommandLine.Parse(["--refresh", "soon"], new HashSet<string>()));
    }
}

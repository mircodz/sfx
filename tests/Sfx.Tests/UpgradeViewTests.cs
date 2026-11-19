using Microsoft.ServiceFabric.Common;
using Sfx.Core;
using Sfx.Tui;

namespace Sfx.Tests;

public class UpgradeViewTests
{
    private static UpgradeRow Row(string state, string started = "2026-11-19T10:00:00.000Z", params string[] domains) =>
        new("fabric:/orders", "application", "orders", "1.0.0", new UpgradeReport(
            state, "Monitored", "1.1.0", "", started, TimeSpan.FromMinutes(5), "", "", "",
            domains.Select((d, i) => new UpgradeDomainRow(i.ToString(), d)).ToList(), "", [], []));

    [Fact]
    public void Running_upgrades_sort_before_waiting_failed_and_done_ones()
    {
        string[] states =
        [
            nameof(UpgradeState.RollingForwardCompleted),
            nameof(UpgradeState.Failed),
            nameof(UpgradeState.RollingForwardPending),
            nameof(UpgradeState.RollingForwardInProgress),
        ];

        var order = states.Select(s => Row(s)).OrderBy(r => r.Urgency).Select(r => r.Report.State);

        Assert.Equal([states[3], states[2], states[1], states[0]], order);
    }

    [Fact]
    public void Counts_completed_domains_and_reads_the_start_time_as_utc()
    {
        var row = Row(nameof(UpgradeState.RollingForwardInProgress), "2026-11-19T10:00:00.000Z", "Completed", "Completed", "InProgress", "Pending");

        Assert.Equal(2, row.DomainsDone);
        Assert.Equal(new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc), row.StartedUtc);
        Assert.Null(Row("", started: "").StartedUtc);
    }

    [Theory]
    [InlineData("en-GB", "19/11/2026 15:22:44")]
    [InlineData("en-US", "11/19/2026 3:22:44 PM")]
    [InlineData("de-DE", "19.11.2026 15:22:44")]
    public void Reads_the_start_time_in_the_format_the_client_library_writes_it(string culture, string started)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
        try
        {
            Assert.Equal(new DateTime(2026, 11, 19, 15, 22, 44, DateTimeKind.Utc), Row("RollingForwardCompleted", started).StartedUtc);
            Assert.Null(Row("RollingForwardCompleted", "01/01/0001 00:00:00").StartedUtc);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("PT0H5M26.812S", 326812)]
    [InlineData("326812", 326812)]
    [InlineData("", -1)]
    [InlineData("soon", -1)]
    public void Reads_durations_as_milliseconds_or_iso_8601(string text, double milliseconds)
    {
        Assert.Equal(milliseconds < 0 ? null : TimeSpan.FromMilliseconds(milliseconds), UpgradeReport.Milliseconds(text));
    }

    [Fact]
    public void Progress_draws_one_box_per_domain()
    {
        Assert.Equal("■■□□□ 2/5", Styles.Progress(2, 5, default).PlainText);
        Assert.Equal("-", Styles.Progress(0, 0, default).PlainText);
    }

    [Theory]
    [InlineData(30, "30s")]
    [InlineData(4 * 60, "4m")]
    [InlineData(2 * 3600 + 5 * 60, "2h5m")]
    public void Age_reads_like_a_duration(int secondsAgo, string expected)
    {
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expected, Styles.Age(now.AddSeconds(-secondsAgo), now));
        Assert.Equal("-", Styles.Age(null, now));
    }
}

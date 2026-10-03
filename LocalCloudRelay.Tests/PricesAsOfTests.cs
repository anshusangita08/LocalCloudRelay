using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>A price is shown with its age, so a stale one is recognisable as stale.</summary>
public sealed class PricesAsOfTests
{
    [Fact]
    public void TodayShowsTheTimeAnEarlierDayShowsTheDateAndNeverShowsNothing()
    {
        var now = new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 3)));
        var today = MainForm.PricesAsOf(now.AddHours(-2), now);
        Assert.StartsWith("  ·  prices as of ", today);
        Assert.EndsWith(now.AddHours(-2).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture), today);

        var yesterday = now.AddDays(-1);
        Assert.EndsWith(yesterday.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture),
            MainForm.PricesAsOf(yesterday, now));
        Assert.Equal(string.Empty, MainForm.PricesAsOf(null, now));
    }

    [Fact]
    public void TheAutomaticRefreshRunsEverySixHours() =>
        Assert.Equal(TimeSpan.FromHours(6), MainForm.AutoRefreshInterval);
}

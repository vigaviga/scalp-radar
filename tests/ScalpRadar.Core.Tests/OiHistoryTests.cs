using ScalpRadar.Core.Indicators;
using ScalpRadar.Core.Market;
using ScalpRadar.Core.Models;

namespace ScalpRadar.Core.Tests;

public class OiHistoryTests
{
    private static OiHistory Build(params (int minute, double oi)[] points)
    {
        var h = new OiHistory(TimeSpan.FromHours(24));
        foreach (var (m, oi) in points) h.Add(new OiSample(Fx.Min(m), oi, oi * 10));
        return h;
    }

    [Fact]
    public void ChangePct_compares_latest_with_the_sample_at_window_start()
    {
        var h = Build((0, 1000), (5, 1005), (10, 1010), (15, 1020));
        Assert.Equal(2.0, h.ChangePct(TimeSpan.FromMinutes(15))!.Value, 6);
        Assert.Equal(1000.0 * 1020 / 1005 / 10 - 100, h.ChangePct(TimeSpan.FromMinutes(10))!.Value, 6);
    }

    [Fact]
    public void ChangePct_uses_the_latest_sample_at_or_before_the_window_start()
    {
        // Live polls every 30 s mixed with 5m history.
        var h = Build((0, 1000), (5, 1010));
        h.Add(new OiSample(Fx.Min(15, 30), 1030, 0));
        // window start = 15:30 - 15 min = 0:30 -> the sample at minute 0
        Assert.Equal(3.0, h.ChangePct(TimeSpan.FromMinutes(15))!.Value, 6);
    }

    [Fact]
    public void ChangePct_is_null_when_history_is_too_short()
    {
        var h = Build((10, 1000), (15, 1010));
        Assert.Null(h.ChangePct(TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void Out_of_order_samples_are_inserted_in_order_and_duplicates_replace()
    {
        var h = Build((10, 1010), (0, 1000), (5, 1005));
        h.Add(new OiSample(Fx.Min(5), 1006, 0));
        Assert.Equal([Fx.Min(0), Fx.Min(5), Fx.Min(10)], h.Samples.Select(s => s.Time));
        Assert.Equal(1006, h.At(Fx.Min(7))!.OpenInterest);
    }

    [Fact]
    public void Old_samples_are_trimmed_but_one_is_kept_before_the_cutoff()
    {
        var h = new OiHistory(TimeSpan.FromMinutes(30));
        for (var m = 0; m <= 60; m += 5) h.Add(new OiSample(Fx.Min(m), 1000 + m, 0));
        Assert.Equal(Fx.Min(25), h.Samples[0].Time); // cutoff is minute 30; the sample just before it stays
        Assert.NotNull(h.At(Fx.Min(30)));
    }
}

public class FundingTests
{
    [Theory]
    [InlineData(0.0001, 8, 0.01)]    // baseline +0.01% per 8h
    [InlineData(0.0001, 4, 0.02)]    // 4h interval pays twice as often
    [InlineData(0.0001, 1, 0.08)]    // 1h interval
    [InlineData(-0.00005, 4, -0.01)]
    public void Rate_is_normalised_to_8h_in_percent(double rate, int hours, double expectedPct) =>
        Assert.Equal(expectedPct, Funding.Rate8hPct(rate, hours), 10);
}

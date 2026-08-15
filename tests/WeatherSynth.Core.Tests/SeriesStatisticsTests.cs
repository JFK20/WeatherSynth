using FluentAssertions;
using WeatherSynth.Climate;
using Xunit;

namespace WeatherSynth.Core.Tests;

public class SeriesStatisticsTests
{
    [Fact]
    public void Independent_days_show_no_persistence()
    {
        // The baseline the model sat at before LatentAr1Chain, and the reason it was needed:
        // i.i.d. sampling cannot produce autocorrelation whatever its histogram looks like.
        var random = new Random(11);
        var start = new DateOnly(2020, 1, 1);

        var series = Enumerable
            .Range(0, 5000)
            .Select(i => (Date: start.AddDays(i), Index: random.NextDouble()))
            .ToList();

        SeriesStatistics.Lag1Autocorrelation(series).Should().BeApproximately(0.0, 0.05);
    }

    [Fact]
    public void A_persistent_series_reports_high_correlation()
    {
        var random = new Random(12);
        var start = new DateOnly(2020, 1, 1);
        var series = new List<(DateOnly, double)>();

        double value = 0.5;
        for (int i = 0; i < 5000; i++)
        {
            // Strongly autoregressive: today is mostly yesterday.
            value = 0.9 * value + 0.1 * random.NextDouble();
            series.Add((start.AddDays(i), value));
        }

        SeriesStatistics.Lag1Autocorrelation(series).Should().BeGreaterThan(0.8);
    }

    [Fact]
    public void Gaps_in_the_record_are_not_treated_as_consecutive_days()
    {
        // Two isolated blocks, each alternating dark-clear-dark. Pairing across the gap would
        // invent a transition that never happened, which is the error that skews persistence on
        // a record with missing days, and the Bochum record has 7.4% missing rows.
        var series = new List<(DateOnly, double)>
        {
            (new DateOnly(2020, 1, 1), 0.2),
            (new DateOnly(2020, 1, 2), 0.8),
            (new DateOnly(2020, 1, 3), 0.2),
            (new DateOnly(2020, 6, 1), 0.2),
            (new DateOnly(2020, 6, 2), 0.8),
            (new DateOnly(2020, 6, 3), 0.2),
        };

        // The four genuine pairs alternate perfectly, so the correlation is exactly -1.
        // Admitting the cross-gap pair (2020-01-03 to 2020-06-01, both 0.2) would drag it to
        // -0.667, so this pins the gap handling rather than merely sampling near it.
        SeriesStatistics.Lag1Autocorrelation(series).Should().BeApproximately(-1.0, 1e-9);
    }
}

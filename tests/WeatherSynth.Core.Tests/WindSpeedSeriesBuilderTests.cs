using FluentAssertions;
using WeatherSynth.Data;
using Xunit;

namespace WeatherSynth.Core.Tests;

public class WindSpeedSeriesBuilderTests
{
    private static DwdWindDay DayOf(DateOnly date, int hours, double speed)
    {
        var rows = Enumerable
            .Range(0, hours)
            .Select(hour =>
                DwdWindReader.ParseLine(
                    $"       1303;{date:yyyyMMdd}{hour:00};   10;"
                        + $"{speed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}; 250;eor"
                )
            )
            .ToList();

        return DwdWindDayAggregator.ToDays(rows).Single();
    }

    [Fact]
    public void Keeps_only_complete_days()
    {
        // A day averaged over some of its hours is not a daily mean: wind has a diurnal cycle, so
        // a day missing its afternoon is biased rather than merely noisy, and nothing downstream
        // can tell that apart from a genuinely calm day.
        var days = new[]
        {
            DayOf(new DateOnly(2015, 3, 14), hours: 24, speed: 4.0),
            DayOf(new DateOnly(2015, 3, 15), hours: 18, speed: 4.0),
            DayOf(new DateOnly(2015, 3, 16), hours: 24, speed: 5.0),
        };

        var series = WindSpeedSeriesBuilder.Build(days);

        series.Select(d => d.Date)
            .Should()
            .Equal(new DateOnly(2015, 3, 14), new DateOnly(2015, 3, 16));
    }

    [Fact]
    public void Carries_the_mean_and_the_cubed_mean_through()
    {
        var series = WindSpeedSeriesBuilder.Build(
            new[] { DayOf(new DateOnly(2015, 3, 14), hours: 24, speed: 4.0) }
        );

        series.Should().ContainSingle();
        series[0].MeanSpeed.Should().BeApproximately(4.0, 1e-12);
        series[0].MeanCubedSpeed.Should().BeApproximately(64.0, 1e-12);
        series[0].EnergyPatternFactor.Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void Drops_a_day_whose_mean_is_exactly_zero()
    {
        // Not a calm day - a stuck sensor. The station has never recorded one, and a Weibull's
        // support is open below in any case.
        var series = WindSpeedSeriesBuilder.Build(
            new[] { DayOf(new DateOnly(2015, 3, 14), hours: 24, speed: 0.0) }
        );

        series.Should().BeEmpty();
    }
}

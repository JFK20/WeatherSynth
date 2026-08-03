using FluentAssertions;
using WeatherSynth.Data;
using Xunit;

namespace WeatherSynth.Core.Tests;

public class DwdWindDayTests
{
    private static readonly DateOnly Date = new(2015, 3, 14);

    /// <summary>A day whose hours are the given speeds, null meaning a missing hour.</summary>
    private static DwdWindDay DayOf(params double?[] speeds)
    {
        var hours = speeds
            .Select(
                (speed, index) =>
                    DwdWindReader.ParseLine(
                        $"       1303;{Date:yyyyMMdd}{index:00};   10;"
                            + $"{(speed?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "-999")};"
                            + " 250;eor"
                    )
            )
            .ToList();

        return DwdWindDayAggregator.ToDays(hours).Single();
    }

    private static double?[] Constant(double speed, int hours) =>
        Enumerable.Repeat((double?)speed, hours).ToArray();

    [Fact]
    public void A_day_is_complete_only_with_all_twenty_four_hours()
    {
        DayOf(Constant(3.0, 24)).IsComplete.Should().BeTrue();
        DayOf(Constant(3.0, 23)).IsComplete.Should().BeFalse();
    }

    [Fact]
    public void A_missing_hour_makes_the_day_incomplete_without_polluting_the_mean()
    {
        var speeds = Constant(4.0, 24);
        speeds[7] = null;

        var day = DayOf(speeds);

        day.IsComplete.Should().BeFalse();
        day.ValidHourCount.Should().Be(23);
        day.MeanSpeed.Should().BeApproximately(4.0, 1e-12);
    }

    [Fact]
    public void Mean_cubed_speed_exceeds_the_cube_of_the_mean_unless_the_day_is_flat()
    {
        // Jensen, and the reason a synthetic daily mean alone understates energy: E[v³] > (E[v])³
        // for anything that varies at all, in one direction only.
        var varying = DayOf(
            Constant(1.0, 12).Concat(Constant(5.0, 12)).ToArray()
        );

        varying.MeanSpeed.Should().BeApproximately(3.0, 1e-12);
        varying.MeanCubedSpeed.Should().BeApproximately(63.0, 1e-12);
        varying.EnergyPatternFactor.Should().BeApproximately(63.0 / 27.0, 1e-12);
        varying.EnergyPatternFactor.Should().BeGreaterThan(1.0);

        var flat = DayOf(Constant(3.0, 24));
        flat.EnergyPatternFactor.Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void Reports_the_maximum_and_the_calm_hours()
    {
        var speeds = Constant(2.0, 24);
        speeds[3] = 0.0;
        speeds[4] = 0.0;
        speeds[5] = 11.5;

        var day = DayOf(speeds);

        day.MaxSpeed.Should().Be(11.5);
        day.ZeroHourCount.Should().Be(2);
    }

    [Fact]
    public void Aggregates_into_utc_calendar_days_in_order_leaving_absent_days_absent()
    {
        var hours = new[] { "2015031423", "2015031500", "2015031700" }
            .Select(stamp => DwdWindReader.ParseLine($"       1303;{stamp};   10;   3.0; 250;eor"))
            .Reverse() // Order of the input must not matter.
            .ToList();

        var days = DwdWindDayAggregator.ToDays(hours);

        days.Select(d => d.Date)
            .Should()
            .Equal(new DateOnly(2015, 3, 14), new DateOnly(2015, 3, 15), new DateOnly(2015, 3, 17));

        // 2015-03-16 is absent rather than present-and-empty, so anything measuring day-to-day
        // persistence downstream sees a break instead of bridging it.
        days.Should().NotContain(d => d.Date == new DateOnly(2015, 3, 16));
    }
}

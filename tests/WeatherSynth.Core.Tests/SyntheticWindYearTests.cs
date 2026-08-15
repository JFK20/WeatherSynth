using FluentAssertions;
using WeatherSynth.Climate;
using Xunit;

namespace WeatherSynth.Core.Tests;

public class SyntheticWindYearTests
{
    private static SyntheticWindDay Day(int month, int day, double speed, double factor = 1.3) =>
        new(new DateOnly(2026, month, day), speed, speed, factor * speed * speed * speed);

    [Fact]
    public void Aggregates_by_averaging_rather_than_summing()
    {
        // The one structural difference from SyntheticSolarYear: a year of irradiance has a total
        // and a year of wind speeds does not. Adding daily speeds together produces a number with
        // no physical meaning, so the annual figure is a mean.
        var year = new SyntheticWindYear(
            2026,
            seed: 1,
            new[] { Day(1, 1, 2.0), Day(1, 2, 4.0), Day(1, 3, 6.0) }
        );

        year.MeanSpeed.Should().BeApproximately(4.0, 1e-12);
        year.MaxSpeed.Should().Be(6.0);
    }

    [Fact]
    public void Groups_days_into_months_in_calendar_order()
    {
        var year = new SyntheticWindYear(
            2026,
            seed: 1,
            new[] { Day(1, 1, 3.0), Day(1, 2, 5.0), Day(3, 1, 2.0) }
        );

        year.Months.Select(m => m.Month).Should().Equal(1, 3);

        var january = year.Months[0];
        january.Days.Should().Be(2);
        january.MeanSpeed.Should().BeApproximately(4.0, 1e-12);
        january.MaxSpeed.Should().Be(5.0);

        // February is absent rather than present and empty - the year holds what was generated.
        year.Months.Should().NotContain(m => m.Month == 2);
    }

    [Fact]
    public void The_annual_energy_proxy_exceeds_the_cube_of_the_annual_mean()
    {
        // Jensen twice over: once inside each day, and again across days. A year whose speeds vary
        // carries more energy than its mean speed alone suggests, and this is the number that
        // says by how much.
        var year = new SyntheticWindYear(
            2026,
            seed: 1,
            new[] { Day(1, 1, 2.0), Day(1, 2, 6.0) }
        );

        year.MeanSpeed.Should().BeApproximately(4.0, 1e-12);

        // mean(v³) over the two days is 1.3 * (8 + 216) / 2 = 145.6, against 4³ = 64.
        year.MeanCubedSpeed.Should().BeApproximately(145.6, 1e-9);
        year.EnergyPatternFactor.Should().BeApproximately(145.6 / 64.0, 1e-9);
        year.EnergyPatternFactor.Should().BeGreaterThan(1.3);
    }

    [Fact]
    public void A_day_from_another_year_is_refused()
    {
        var strayDay = new SyntheticWindDay(new DateOnly(2025, 12, 31), 3.0, 3.0, 35.0);

        Action wrongYear = () =>
            new SyntheticWindYear(2026, seed: 1, new[] { Day(1, 1, 3.0), strayDay });

        wrongYear.Should().Throw<ArgumentException>().WithMessage("*does not belong to 2026*");
    }

    [Fact]
    public void An_empty_year_is_refused()
    {
        Action empty = () => new SyntheticWindYear(2026, seed: 1, Array.Empty<SyntheticWindDay>());

        empty.Should().Throw<ArgumentException>();
    }
}

using FluentAssertions;
using WeatherSynth.Climate;
using WeatherSynth.Solar;
using Xunit;

namespace WeatherSynth.Core.Tests;

/// <summary>
/// The year-at-a-time API: the shape a caller requesting synthetic data actually uses.
///
/// <para>What is tested here is not the physics or the distributions - those have their own
/// suites - but the two promises this layer adds on top of them: that a year covers the whole
/// calendar, and that a seed reproduces it.</para>
/// </summary>
public class SyntheticSolarYearTests
{
    /// <summary>Bochum, the station the model is fitted at, as a generation site.</summary>
    private static SolarSite Site() => new(51.4445, 7.3852, 150.0);

    private static SyntheticSolarGenerator Generator() =>
        new(ClimateFixtures.SeasonalModel, Site().CreateCeiling());

    [Fact]
    public void GenerateYear_CoversEveryDayIncludingTheLeapDay()
    {
        var ordinary = Generator().GenerateYear(2026, seed: 1);
        var leap = Generator().GenerateYear(2028, seed: 1);

        ordinary.Days.Should().HaveCount(365);
        leap.Days.Should().HaveCount(366);

        ordinary.Days[0].Date.Should().Be(new DateOnly(2026, 1, 1));
        ordinary.Days[^1].Date.Should().Be(new DateOnly(2026, 12, 31));
        leap.Days.Select(d => d.Date).Should().Contain(new DateOnly(2028, 2, 29));
    }

    /// <summary>
    /// The promise that makes a generated year quotable: ask for the same year and seed later
    /// and the same days come back, whoever asks and in whatever order.
    /// </summary>
    [Fact]
    public void GenerateYear_IsReproducibleFromItsSeed()
    {
        var first = Generator().GenerateYear(2026, seed: 4242);

        // A second generator, and a different year drawn in between, so the repeat cannot be
        // riding on leftover chain state.
        var other = Generator();
        other.GenerateYear(2019, seed: 99);
        var repeat = other.GenerateYear(2026, seed: 4242);

        repeat.Seed.Should().Be(4242);
        repeat.Days.Should().Equal(first.Days);
    }

    [Fact]
    public void GenerateYear_DifferentSeedsGiveDifferentWeatherOnTheSameCeiling()
    {
        var first = Generator().GenerateYear(2026, seed: 1);
        var second = Generator().GenerateYear(2026, seed: 2);

        second
            .Days.Select(d => d.ClearSkyIndex)
            .Should()
            .NotEqual(first.Days.Select(d => d.ClearSkyIndex));

        // The ceiling is deterministic, so the half of the product that is physics must match
        // exactly - anything else would mean the seed had reached into the geometry.
        second
            .Days.Select(d => d.ClearSkyWhPerM2)
            .Should()
            .Equal(first.Days.Select(d => d.ClearSkyWhPerM2));
    }

    [Fact]
    public void Months_PartitionTheYear()
    {
        var year = Generator().GenerateYear(2026, seed: 7);

        year.Months.Should().HaveCount(12);
        year.Months.Select(m => m.Month).Should().Equal(Enumerable.Range(1, 12));
        year.Months.Sum(m => m.Days).Should().Be(year.Days.Count);
        year.Months.Sum(m => m.GhiKWhPerM2).Should().BeApproximately(year.GhiKWhPerM2, 1e-6);
    }

    /// <summary>
    /// The two annual means are not interchangeable, and the difference is not rounding: the
    /// energy-weighted one counts summer days more heavily, and summer is clearer.
    /// </summary>
    [Fact]
    public void ClearSkyFraction_IsEnergyWeightedAndExceedsTheDailyMean()
    {
        var year = Generator().GenerateYear(2026, seed: 7);

        year.ClearSkyFraction.Should()
            .BeApproximately(year.GhiKWhPerM2 / year.ClearSkyKWhPerM2, 1e-12);
        year.ClearSkyFraction.Should().BeGreaterThan(year.MeanClearSkyIndex);
    }

    [Fact]
    public void Constructor_RejectsADayFromAnotherYear()
    {
        var days = Generator().GenerateYear(2026, seed: 3).Days.ToList();
        days.Add(days[0] with { Date = new DateOnly(2027, 1, 1) });

        var construct = () => new SyntheticSolarYear(2026, 3, days);

        construct.Should().Throw<ArgumentException>().WithMessage("*2027-01-01*");
    }
}

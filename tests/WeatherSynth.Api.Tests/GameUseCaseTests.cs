using FluentAssertions;
using WeatherSynth;
using Xunit;

namespace WeatherSynthApiTests;

/// <summary>
/// The use case the package exists for, written the way a game would write it: one
/// <c>using WeatherSynth;</c>, no internals, nothing reached for that a NuGet consumer could not
/// reach. If this file stops compiling, the public surface has a hole in it - which is exactly what
/// <c>WeatherSynth.Core.Tests</c> cannot tell you, because it has internals access.
///
/// <para><b>Data-aware</b>, following the precedent of the ZENIT validation and
/// <c>EssenWindRecordTests</c>: with the station files absent every test here passes silently, so a
/// machine without them still builds and tests. Note that the files are located by a probe of this
/// project's own, because <c>RepositoryData</c> is internal now - and that is the correct shape for
/// this project. A consumer has no repository to walk up; they have a path.</para>
/// </summary>
public class GameUseCaseTests
{
    /// <summary>
    /// The station records, or null when this machine has none. Shared with
    /// <see cref="DefaultModelTests"/> so there is one probe rather than two.
    /// </summary>
    internal static readonly string? SolarCsvPath = Locate("dwd_bochum_solar.csv");

    /// <inheritdoc cref="SolarCsvPath"/>
    internal static readonly string? WindCsvPath = Locate("dwd_essen_wind.csv");

    private static string? SolarCsv => SolarCsvPath;

    private static string? WindCsv => WindCsvPath;

    private static string? Locate(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "data", fileName);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return null;
    }

    [Fact]
    public void A_game_can_ask_for_a_year_of_coupled_weather()
    {
        if (SolarCsv is null || WindCsv is null)
            return;

        var weather = CoupledWeatherProvider.FromDwdRecords(
            SolarCsv,
            DwdSolarStations.Bochum,
            WindCsv,
            DwdWindStations.EssenBredeney
        );

        var year = weather.GenerateYear(2027, seed: 4242);

        year.Days.Should().HaveCount(365);
        year.Year.Should().Be(2027);
        year.Seed.Should().Be(4242);

        // Everything a game reads per day, through the public surface only.
        foreach (var day in year.Days)
        {
            day.Solar.GhiKWhPerM2.Should().BeGreaterThanOrEqualTo(0.0).And.BeLessThan(12.0);
            day.Solar.ClearSkyIndex.Should().BeInRange(0.0, 1.25);
            day.Wind.MeanSpeed.Should().BeGreaterThan(0.0).And.BeLessThan(40.0);
            day.Date.Year.Should().Be(2027);
        }

        // The two halves are reachable as whole years too, with their own totals.
        year.Solar.GhiKWhPerM2.Should().BeInRange(700.0, 1400.0);
        year.Wind.MeanSpeed.Should().BeInRange(2.0, 8.0);

        // Reproducible from the seed, which is what makes a game world stable across sessions.
        var again = weather.GenerateYear(2027, seed: 4242);
        again.Days.Should().Equal(year.Days);
    }

    /// <summary>
    /// Hours, the way a game reads them: a stretch of one day, by wall-clock time. Uses the
    /// bundled model, so it runs with no data files.
    /// </summary>
    [Fact]
    public void A_game_can_ask_for_the_hours_of_a_morning()
    {
        var year = SyntheticWeather.Default.GenerateYear(2025, seed: 4242);

        var morning = year.HoursBetween(new DateTime(2025, 1, 1, 6, 0, 0), new DateTime(2025, 1, 1, 12, 0, 0));

        morning.Should().HaveCount(6);
        morning[0].Start.Should().Be(new DateTimeOffset(2025, 1, 1, 6, 0, 0, TimeSpan.Zero));

        foreach (var hour in morning)
        {
            hour.Solar.GhiWhPerM2.Should().BeGreaterThanOrEqualTo(0.0).And.BeLessThan(1000.0);
            hour.Wind.Speed.Should().BeGreaterThan(0.0).And.BeLessThan(40.0);
        }

        // Hours and days are one generation, not two: a day's hours carry the day exactly.
        var day = year.Days[0];
        var dayHours = year.HoursBetween(new DateTime(2025, 1, 1), new DateTime(2025, 1, 2));

        dayHours.Sum(h => h.Solar.GhiWhPerM2).Should().BeApproximately(day.Solar.GhiWhPerM2, 1e-6);
        dayHours.Average(h => h.Wind.Speed).Should().BeApproximately(day.Wind.MeanSpeed, 1e-9);

        year.Hours.Should().HaveCount(365 * 24);
    }

    [Fact]
    public void A_game_can_ask_what_a_turbine_would_produce()
    {
        if (WindCsv is null)
            return;

        var wind = SyntheticWindProvider.FromDwdRecord(WindCsv, DwdWindStations.EssenBredeney);

        // At a height something could actually be built at - the fitting height's 2.27% capacity
        // factor is correct and meaningless, and the public docs say so.
        var yield = wind.EstimateYield(
            2027,
            seed: 4242,
            curve: ReferenceTurbines.Generic2Mw,
            site: ReferenceTurbines.HundredMetreHub
        );

        yield.Days.Should().HaveCount(365);
        yield.CapacityFactor.Should().BeInRange(0.05, 0.40);
        yield.EnergyMegawattHours.Should().BeGreaterThan(0.0);
        yield.Curve.RatedPowerKilowatts.Should().Be(2000.0);

        // The profile law is a caller's choice, and the docs insist it be quoted with the number.
        var underPowerLaw = wind.EstimateYield(
            2027,
            seed: 4242,
            curve: ReferenceTurbines.Generic2Mw,
            site: ReferenceTurbines.HundredMetreHub,
            profile: WindProfile.PowerLaw()
        );

        underPowerLaw.CapacityFactor.Should().NotBe(yield.CapacityFactor);
    }

    [Fact]
    public void A_game_can_generate_the_two_resources_independently_without_the_seed_trap()
    {
        if (SolarCsv is null || WindCsv is null)
            return;

        var solar = SyntheticSolarProvider.FromDwdRecord(SolarCsv, DwdSolarStations.Bochum);
        var wind = SyntheticWindProvider.FromDwdRecord(WindCsv, DwdWindStations.EssenBredeney);

        var (solarSeed, windSeed) = WeatherSeeds.Split(4242);

        solarSeed.Should().NotBe(windSeed);
        WeatherSeeds
            .Split(4242)
            .Should()
            .Be((solarSeed, windSeed), "one seed must reproduce a run");

        var solarYear = solar.GenerateYear(2027, solarSeed);
        var windYear = wind.GenerateYear(2027, windSeed);

        // The failure this guards against: one seed for both makes the two chains draw the same
        // normals in the same order, and the resources come out about +0.75 correlated. Split
        // seeds must land nowhere near that.
        double correlation = Correlation(
            solarYear.Days.Select(d => d.ClearSkyIndex).ToList(),
            windYear.Days.Select(d => d.MeanSpeed).ToList()
        );

        correlation.Should().BeInRange(-0.35, 0.35);

        double shared = Correlation(
            solar.GenerateYear(2027, 4242).Days.Select(d => d.ClearSkyIndex).ToList(),
            wind.GenerateYear(2027, 4242).Days.Select(d => d.MeanSpeed).ToList()
        );

        shared.Should().BeGreaterThan(0.5, "the trap is real, and this is what avoiding it buys");
    }

    [Fact]
    public void A_game_can_generate_for_its_own_location_rather_than_the_station()
    {
        if (SolarCsv is null)
            return;

        var solar = SyntheticSolarProvider.FromDwdRecord(SolarCsv, DwdSolarStations.Bochum);

        // Köln, ~50 km south: same cloud climate, different geometry.
        var koeln = new SolarSite(50.9375, 6.9603, 55.0);

        var here = solar.GenerateYear(2027, 4242);
        var there = solar.GenerateYear(2027, 4242, koeln);

        // Same weather, different ceiling - which is the whole point of modelling the index.
        there
            .Days.Select(d => d.ClearSkyIndex)
            .Should()
            .Equal(here.Days.Select(d => d.ClearSkyIndex));

        there.GhiKWhPerM2.Should().NotBe(here.GhiKWhPerM2);
    }

    [Fact]
    public void A_game_can_ask_for_an_arbitrary_span_rather_than_a_calendar_year()
    {
        if (WindCsv is null)
            return;

        var wind = SyntheticWindProvider.FromDwdRecord(WindCsv, DwdWindStations.EssenBredeney);

        var heatingSeason = wind.Generate(
                new DateOnly(2027, 10, 1),
                new DateOnly(2028, 3, 31),
                seed: 4242
            )
            .ToList();

        heatingSeason.Should().HaveCount(183);
        heatingSeason[0].Date.Should().Be(new DateOnly(2027, 10, 1));
        heatingSeason[^1].Date.Should().Be(new DateOnly(2028, 3, 31));
    }

    private static double Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        int n = Math.Min(a.Count, b.Count);
        double meanA = a.Take(n).Average();
        double meanB = b.Take(n).Average();

        double covariance = 0.0;
        double varianceA = 0.0;
        double varianceB = 0.0;

        for (int i = 0; i < n; i++)
        {
            double da = a[i] - meanA;
            double db = b[i] - meanB;

            covariance += da * db;
            varianceA += da * da;
            varianceB += db * db;
        }

        return covariance / Math.Sqrt(varianceA * varianceB);
    }
}

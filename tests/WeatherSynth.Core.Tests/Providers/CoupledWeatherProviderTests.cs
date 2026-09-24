using FluentAssertions;
using WeatherSynth.Climate;
using WeatherSynth.Data;
using WeatherSynth.Statistics;
using Xunit;

namespace WeatherSynth.Core.Tests;

/// <summary>
/// The coupled entry point against the real records.
///
/// <para><b>Data-aware</b>, following the precedent set by <see cref="EssenWindRecordTests"/>: with
/// either station file absent every test here passes silently, so a machine carrying neither can
/// still build and test the library. Coupling needs both records by its nature - there is no
/// solar-only fallback to degrade to, unlike the visualisation - so both are required here.</para>
/// </summary>
public class CoupledWeatherProviderTests
{
    private static readonly CoupledWeatherProvider? Provider = Load();

    private static CoupledWeatherProvider? Load()
    {
        string? solarPath = RepositoryData.TryLocateBochum();
        string? windPath = RepositoryData.TryLocateEssenWind();

        if (solarPath is null || windPath is null)
            return null;

        return CoupledWeatherProvider.FromDwdRecords(
            solarPath,
            DwdSolarStations.Bochum,
            windPath,
            DwdWindStations.EssenBredeney
        );
    }

    [Fact]
    public void Fits_a_negative_coupling_in_every_month()
    {
        if (Provider is null)
            return;

        // Windy days are cloudy days. A positive coefficient anywhere would mean the two series
        // were paired the wrong way round or a sign was lost, not that this site is unusual.
        for (int month = 1; month <= 12; month++)
        {
            Provider.Coupling.ForMonth(month).Should().BeNegative();
            Provider.Coupling.ForMonth(month).Should().BeGreaterThan(-1.0);
            Provider.Coupling.SampleCount(month).Should().BeGreaterThan(300);
            Provider.Coupling.IsPooled(month).Should().BeFalse();
        }

        Provider.Coupling.Pooled.Should().BeInRange(-0.20, -0.05);
    }

    /// <summary>
    /// The coupling is a within-month statistic, and the seasonal months differ from the
    /// shoulder ones by more than either differs from the pooled figure - which is the whole
    /// reason it is fitted twelve times rather than once.
    /// </summary>
    [Fact]
    public void Finds_more_coupling_in_summer_and_winter_than_in_the_shoulder_months()
    {
        if (Provider is null)
            return;

        double july = Math.Abs(Provider.Coupling.ForMonth(7));
        double january = Math.Abs(Provider.Coupling.ForMonth(1));
        double april = Math.Abs(Provider.Coupling.ForMonth(4));

        july.Should().BeGreaterThan(april);
        january.Should().BeGreaterThan(april);
    }

    [Fact]
    public void The_same_seed_reproduces_the_year_exactly()
    {
        if (Provider is null)
            return;

        var first = Provider.GenerateYear(2024, 4242);
        var second = Provider.GenerateYear(2024, 4242);

        first.Days.Should().Equal(second.Days);
        first.Solar.GhiKWhPerM2.Should().Be(second.Solar.GhiKWhPerM2);
        first.Wind.MeanSpeed.Should().Be(second.Wind.MeanSpeed);
    }

    [Fact]
    public void Pairs_the_two_resources_on_the_same_day()
    {
        if (Provider is null)
            return;

        var year = Provider.GenerateYear(2024, 7);

        year.Days.Should().HaveCount(366);
        year.Days.Should().OnlyContain(d => d.Solar.Date == d.Date && d.Wind.Date == d.Date);
        year.Solar.Days.Should().HaveCount(366);
        year.Wind.Days.Should().HaveCount(366);
    }

    /// <summary>
    /// The generated year must carry the coupling it was fitted with - the end-to-end version of
    /// the chain's own acceptance test, through both providers and both marginals.
    /// </summary>
    [Fact]
    public void A_generated_run_carries_the_fitted_coupling()
    {
        if (Provider is null)
            return;

        var run = Provider
            .Generate(new DateOnly(2000, 1, 1), new DateOnly(2059, 12, 31), 31337)
            .Select(d => (d.Date, d.Solar.ClearSkyIndex, d.Wind.MeanSpeedAtReference))
            .ToList();

        double realised = SeriesStatistics.LatentCrossCorrelation(
            run,
            Provider.Solar.Model.CumulativeProbability,
            Provider.Wind.Model.CumulativeProbability
        );

        double measured = SeriesStatistics.LatentCrossCorrelation(
            BuildPairedRecord(),
            Provider.Solar.Model.CumulativeProbability,
            Provider.Wind.Model.CumulativeProbability
        );

        realised.Should().BeApproximately(measured, 0.02);
    }

    /// <summary>
    /// The height transfer moves the wind half and leaves the solar half alone: it is a statement
    /// about an anemometer, and the sun does not know how high one is.
    /// </summary>
    [Fact]
    public void The_wind_site_moves_only_the_wind_half()
    {
        if (Provider is null)
            return;

        var atStation = Provider.GenerateYear(2024, 99);
        var atHub = Provider.GenerateYear(
            2024,
            99,
            solarSite: null,
            windSite: new WindSite(HeightMeters: 100.0, RoughnessLengthMeters: 0.1)
        );

        atHub.Solar.GhiKWhPerM2.Should().Be(atStation.Solar.GhiKWhPerM2);
        atHub.Wind.MeanSpeed.Should().BeGreaterThan(atStation.Wind.MeanSpeed);

        // The speed as fitted is untouched by the transfer - that is what it is carried for, and
        // it is the cheap way to catch a transfer applied twice.
        atHub
            .Days.Select(d => d.Wind.MeanSpeedAtReference)
            .Should()
            .Equal(atStation.Days.Select(d => d.Wind.MeanSpeedAtReference));
    }

    /// <summary>
    /// Neither half's own statistics move: the two providers this wraps produce the same
    /// distributions coupled as uncoupled, and each is still usable on its own.
    /// </summary>
    [Fact]
    public void Neither_half_is_disturbed_by_being_coupled()
    {
        if (Provider is null)
            return;

        Provider.Solar.Model.Persistence.Should().BeApproximately(0.353, 0.01);
        Provider.Wind.Model.Persistence.Should().BeApproximately(0.444, 0.01);

        // Averaged over enough years that a single year's sampling spread does not decide it -
        // knowledge.md §14 puts that at about +/-0.1 m/s for wind.
        var coupled = new List<double>();
        var alone = new List<double>();

        for (int seed = 0; seed < 40; seed++)
        {
            coupled.Add(Provider.GenerateYear(2024, seed).Wind.MeanSpeed);
            alone.Add(Provider.Wind.GenerateYear(2024, seed).MeanSpeed);
        }

        coupled.Average().Should().BeApproximately(alone.Average(), 0.05);
    }

    private static List<(DateOnly Date, double A, double B)> BuildPairedRecord()
    {
        string solarPath = RepositoryData.TryLocateBochum()!;
        string windPath = RepositoryData.TryLocateEssenWind()!;

        var solarDays = DwdSolarDayAggregator.ToDays(DwdSolarReader.Read(solarPath));
        var windDays = DwdWindDayAggregator.ToDays(DwdWindReader.Read(windPath));

        var clearness = ClearnessIndexBuilder.Build(
            solarDays.Where(d => d.IsUsable),
            DwdSolarStations.Bochum
        );
        var speeds = WindSpeedSeriesBuilder.Build(windDays);

        return CoupledSeriesBuilder
            .Build(clearness, speeds)
            .Select(p => (p.Date, p.ClearSkyIndex, p.MeanSpeed))
            .ToList();
    }
}

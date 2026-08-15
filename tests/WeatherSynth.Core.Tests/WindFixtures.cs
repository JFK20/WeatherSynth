using WeatherSynth.Climate;

namespace WeatherSynth.Core.Tests;

/// <summary>
/// The synthetic wind record the distribution and persistence suites are both built on - the wind
/// counterpart of <see cref="ClimateFixtures"/>.
///
/// <para>Shared rather than copied, for the same reason: the chain tests assert that persistence
/// leaves the marginals alone, which is only a statement about the marginals the model tests fit.
/// Two private copies of the same seeded generator would let them drift apart silently.</para>
/// </summary>
internal static class WindFixtures
{
    /// <summary>
    /// The Weibull the fixture record draws from on a given date: windy in winter, calm in late
    /// summer, like the station the model is built for.
    ///
    /// <para>Everything seasonal lives here, because in the wind model there is no ceiling for it
    /// to live in - which is the one structural difference from the solar fixture.</para>
    /// </summary>
    internal static Weibull SeasonalShape(DateOnly date)
    {
        double seasonal = 3.25 + 0.55 * Math.Cos((date.DayOfYear - 15) / 365.25 * 2.0 * Math.PI);
        return new Weibull(shape: 1.9, scale: seasonal - 1.0, location: 1.0);
    }

    /// <summary>A record with a deliberate seasonal swing and no day-to-day persistence.</summary>
    internal static List<DailyWindSpeed> SeasonalSeries(int years = 15, int seed = 4242)
    {
        var random = new Random(seed);
        var series = new List<DailyWindSpeed>();

        for (var date = new DateOnly(2010, 1, 1); date.Year < 2010 + years; date = date.AddDays(1))
        {
            double speed = SeasonalShape(date).Sample(random);

            // A plausible intra-day spread, so the energy pattern factor is a real number rather
            // than a constant 1.
            series.Add(new DailyWindSpeed(date, speed, speed * speed * speed * 1.25));
        }

        return series;
    }

    /// <summary>
    /// The model fitted from <see cref="SeasonalSeries"/>, built once.
    ///
    /// <para>Twelve maximum-likelihood fits, each running a KS-minimising scan over the location
    /// parameter, is the most expensive fixture in this suite. The model is immutable and the
    /// construction deterministic, so every test that needs one shares it.</para>
    /// </summary>
    internal static WindSpeedModel SeasonalModel => LazySeasonalModel.Value;

    private static readonly Lazy<WindSpeedModel> LazySeasonalModel = new(() =>
        WindSpeedModel.Fit(SeasonalSeries(), referenceHeightMeters: 15.0)
    );
}

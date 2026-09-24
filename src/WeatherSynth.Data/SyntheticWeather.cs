using WeatherSynth.Climate;
using WeatherSynth.Data;
using WeatherSynth.Statistics;
using WeatherSynth.Wind;

namespace WeatherSynth;

/// <summary>
/// The zero-setup entry point: a fitted model, bundled, with no file to find and no fit to wait
/// for.
///
/// <code>
/// foreach (var day in SyntheticWeather.Default.GenerateYear(2027, seed: 4242).Days)
///     Console.WriteLine($"{day.Date}  {day.Solar.GhiKWhPerM2:F2} kWh/m²  {day.Wind.MeanSpeed:F1} m/s");
/// </code>
///
/// <para><b>What is bundled.</b> Seventeen years of hourly measurements from two DWD stations -
/// Bochum 7365 for solar, Essen-Bredeney 1303 for wind, both in the German Ruhr at about 51.4°N -
/// reduced to the eighty-odd coefficients that are the whole of what was learned from them. The
/// records themselves are 30 MB and are not shipped; fitting is deterministic given frozen data, so
/// there is nothing to gain by redoing it on every launch. A caller with their own DWD record
/// should use <see cref="CoupledWeatherProvider.FromDwdRecords"/> instead.</para>
///
/// <para><b>This is a weather generator, not a forecast.</b> A year number selects a seasonal cycle
/// and a set of ceilings; it makes no claim whatever about that particular year.
/// <c>GenerateYear(2027, seed)</c> is one plausible 2027 out of a great many, and every other seed
/// is exactly as plausible. Nothing here predicts weather for a real future date.</para>
///
/// <para><b>The statistics travel; the geometry does not.</b> The clearness index divides out
/// latitude and season, so these distributions carry to anywhere sharing the Ruhr's cloud and wind
/// climate - a few hundred kilometres of northwest European weather, not another continent. Pass a
/// <see cref="SolarSite"/> and a <see cref="WindSite"/> to generate for somewhere else; that is what
/// puts the geometry back.</para>
///
/// <para><b>Thread-safe, and built once.</b> Each provider is created on first use and reused. The
/// providers themselves are immutable; the generators they hand out internally are not, and never
/// outlive a call.</para>
/// </summary>
public static class SyntheticWeather
{
    // Built once, on first touch. The coupled provider is composed from these two rather than
    // building its own, so a caller mixing the three entry points gets one model and not three.
    private static readonly Lazy<SyntheticSolarProvider> LazySolar = new(BuildSolar);

    private static readonly Lazy<SyntheticWindProvider> LazyWind = new(BuildWind);

    private static readonly Lazy<CoupledWeatherProvider> LazyDefault = new(BuildCoupled);

    /// <summary>
    /// Both resources, drawn together - <b>the one to use for a world that has both.</b>
    ///
    /// <para>The coupled provider is the only one that gets the dependence between sun and wind
    /// right: windy days come out cloudy, because that is what the record says. Generating the two
    /// independently gets each resource perfect on its own and the pairing wrong, which matters the
    /// moment anything depends on both at once - a hybrid PV and wind system, or a world where a
    /// storm should also be overcast.</para>
    /// </summary>
    public static CoupledWeatherProvider Default => LazyDefault.Value;

    /// <summary>
    /// Solar only, for a caller who needs no wind. Cheaper, and identical to
    /// <see cref="Default"/>'s solar half in distribution.
    /// </summary>
    public static SyntheticSolarProvider DefaultSolar => LazySolar.Value;

    /// <summary>
    /// Wind only, for a caller who needs no sun. Also the way to a turbine yield, through
    /// <see cref="SyntheticWindProvider.EstimateYield"/>.
    ///
    /// <para><b>Give that a hub height.</b> The bundled model is fitted at a 15 m anemometer, where
    /// a capacity factor comes out near 2% - arithmetically correct and practically meaningless,
    /// because no turbine stands at 15 m. See <see cref="WindProfile"/> for what the transfer to a
    /// real hub height costs, and never quote a yield without the profile it was computed
    /// under.</para>
    /// </summary>
    public static SyntheticWindProvider DefaultWind => LazyWind.Value;

    /// <summary>
    /// Whether both providers have been built yet, so a caller who cares can decide when to pay
    /// for it rather than discovering it mid-frame.
    ///
    /// <para>The cost is small - rebuilding from coefficients is a few dozen object allocations
    /// and no arithmetic worth the name, which is the entire point of shipping coefficients rather
    /// than a record. Touch <see cref="Default"/> during a loading screen if even that matters.</para>
    /// </summary>
    public static bool IsCreated => LazyDefault.IsValueCreated;

    private static SyntheticSolarProvider BuildSolar()
    {
        var monthly = Enumerable
            .Range(0, 12)
            .Select(i => new ScaledBeta(
                BochumEssenCoefficients.SolarAlpha[i],
                BochumEssenCoefficients.SolarBeta[i],
                BochumEssenCoefficients.SolarSupport
            ))
            .ToArray();

        var pooled = new ScaledBeta(
            BochumEssenCoefficients.SolarPooledAlpha,
            BochumEssenCoefficients.SolarPooledBeta,
            BochumEssenCoefficients.SolarSupport
        );

        var model = ClearSkyIndexModel.FromCoefficients(
            monthly,
            pooled,
            BochumEssenCoefficients.SolarSupport,
            BochumEssenCoefficients.SolarPersistence
        );

        // The fitting station's own geometry, which is what the index was divided by.
        return new SyntheticSolarProvider(model, DwdSolarStations.Bochum.ToSite());
    }

    private static SyntheticWindProvider BuildWind()
    {
        var monthly = Enumerable
            .Range(0, 12)
            .Select(i => new Weibull(
                BochumEssenCoefficients.WindShape[i],
                BochumEssenCoefficients.WindScale[i],
                BochumEssenCoefficients.WindLocation[i]
            ))
            .ToArray();

        var pooled = new Weibull(
            BochumEssenCoefficients.WindPooledShape,
            BochumEssenCoefficients.WindPooledScale,
            BochumEssenCoefficients.WindPooledLocation
        );

        var model = WindSpeedModel.FromCoefficients(
            monthly,
            pooled,
            BochumEssenCoefficients.WindPersistence,
            BochumEssenCoefficients.WindReferenceHeightMeters,
            BochumEssenCoefficients.WindMeanEnergyPatternFactor
        );

        var intradayShape = IntradayShapeModel.FromCoefficients(
            BochumEssenCoefficients.IntradayIntercept,
            BochumEssenCoefficients.IntradaySlope,
            BochumEssenCoefficients.IntradayPooledEnergyPatternFactor,
            BochumEssenCoefficients.IntradaySampleCount
        );

        var hourly = HourlyWindModel.FromCoefficients(
            BochumEssenCoefficients.HourlyPersistence,
            BochumEssenCoefficients.DiurnalWeight,
            BochumEssenCoefficients.DiurnalPattern,
            BochumEssenCoefficients.HourlySampleCount
        );

        return new SyntheticWindProvider(
            model,
            DwdWindStations.EssenBredeney.ToSite(),
            intradayShape,
            hourly
        );
    }

    private static CoupledWeatherProvider BuildCoupled()
    {
        var coupling = MonthlyCoupling.FromCoefficients(
            BochumEssenCoefficients.CouplingByMonth,
            BochumEssenCoefficients.CouplingCounts,
            BochumEssenCoefficients.CouplingPooled
        );

        return new CoupledWeatherProvider(LazySolar.Value, LazyWind.Value, coupling);
    }
}

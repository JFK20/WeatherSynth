using WeatherSynth.Climate;
using WeatherSynth.Data;
using WeatherSynth.Wind;

namespace WeatherSynth;

/// <summary>
/// The library's entry point for synthetic daily wind data: fit once from a station record, then
/// serve as many years as asked for.
///
/// <para>Getting a year out of the model by hand means reading the station file, aggregating hours
/// into days, filtering to complete ones, fitting twelve Weibulls and a persistence coefficient,
/// and only then building a generator against the <i>target</i> site's transfer factor. This class
/// is that sequence, done once, correctly - the counterpart of
/// <see cref="SyntheticSolarProvider"/>.</para>
///
/// <para><b>The cost split is the same but the balance is different.</b> Fitting is the expensive
/// half here too - 149k hourly rows, and twelve maximum-likelihood fits each running a
/// KS-minimising scan over the location parameter. What follows is far cheaper than solar's: a
/// generated day is one uniform, one logarithm and one power, with no clear-sky integration
/// anywhere. Construct one provider for the process lifetime and call
/// <see cref="GenerateYear(int, int)"/> per request.</para>
///
/// <para><b>Thread-safe.</b> The provider holds only the fitted model, which is immutable. Each
/// call builds its own generator, which is not thread-safe and never outlives the call.</para>
/// </summary>
public sealed class SyntheticWindProvider
{
    private readonly WindSite _fittedAt;

    /// <summary>
    /// Wraps an already-fitted model.
    /// </summary>
    /// <param name="model">The fitted distributions - twelve Weibulls plus the persistence.</param>
    /// <param name="fittedAt">
    /// Height and roughness the model was fitted at. Used as the default generation site, and
    /// load-bearing even when generating elsewhere: it is the reference every transfer starts from.
    /// </param>
    internal SyntheticWindProvider(WindSpeedModel model, WindSite fittedAt)
        : this(model, fittedAt, IntradayShapeModel.Constant(NonNull(model).MeanEnergyPatternFactor))
    { }

    /// <summary>
    /// Wraps an already-fitted model and an already-fitted intra-day shape.
    /// </summary>
    /// <param name="model">The fitted distributions - twelve Weibulls plus the persistence.</param>
    /// <param name="fittedAt">Height and roughness the model was fitted at.</param>
    /// <param name="intradayShape">
    /// How each day's wind is spread across its own hours, which is what a power curve has to be
    /// integrated over. Only <see cref="EstimateYield"/> consumes it; the speed generation above is
    /// untouched by it.
    /// </param>
    internal SyntheticWindProvider(
        WindSpeedModel model,
        WindSite fittedAt,
        IntradayShapeModel intradayShape
    )
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _fittedAt = fittedAt ?? throw new ArgumentNullException(nameof(fittedAt));
        IntradayShape = intradayShape ?? throw new ArgumentNullException(nameof(intradayShape));
    }

    private static WindSpeedModel NonNull(WindSpeedModel model) =>
        model ?? throw new ArgumentNullException(nameof(model));

    /// <summary>
    /// Fits from a DWD station file on disk.
    /// </summary>
    /// <param name="csvPath">Path to the station's hourly wind record.</param>
    /// <param name="station">Station metadata. Its anemometer height becomes the fitting height.</param>
    public static SyntheticWindProvider FromDwdRecord(string csvPath, DwdWindStation station)
    {
        ArgumentNullException.ThrowIfNull(csvPath);

        var days = DwdWindDayAggregator.ToDays(DwdWindReader.Read(csvPath));
        return FromStationDays(days, station);
    }

    /// <summary>
    /// Fits from station days already read, for callers that have the record in hand.
    ///
    /// <para>The quality filter the fit depends on lives in
    /// <see cref="WindSpeedSeriesBuilder.Build"/>: complete days only. A day averaged over some of
    /// its hours is not a daily mean, and because wind has a diurnal cycle it is biased rather than
    /// merely noisy.</para>
    /// </summary>
    /// <param name="days">Aggregated station days, unfiltered.</param>
    /// <param name="station">Station metadata. Its anemometer height becomes the fitting height.</param>
    internal static SyntheticWindProvider FromStationDays(
        IEnumerable<DwdWindDay> days,
        DwdWindStation station
    )
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(station);

        var series = WindSpeedSeriesBuilder.Build(days);
        var model = WindSpeedModel.Fit(series, station.AnemometerHeightMeters);

        // Fitted from the series already in hand, so this costs one more pass over ~6,000 days
        // rather than a second read of the record.
        var intradayShape = IntradayShapeModel.Fit(series);

        return new SyntheticWindProvider(model, station.ToSite(), intradayShape);
    }

    /// <summary>
    /// The fitted model behind this provider: the twelve monthly shapes, the persistence, and the
    /// height the whole thing belongs to.
    /// </summary>
    internal WindSpeedModel Model { get; }

    /// <summary>
    /// How each day's wind is spread within the day - the piece a power curve needs and a daily
    /// mean speed cannot supply. See <see cref="IntradayShapeModel"/>.
    /// </summary>
    internal IntradayShapeModel IntradayShape { get; }

    /// <summary>
    /// A synthetic year at the height the model was fitted at.
    ///
    /// <para>The transfer factor is exactly 1.0 here, so this year carries none of the profile's
    /// uncertainty - it is the model's own output, and the one to check against the record.</para>
    /// </summary>
    /// <param name="year">Calendar year. A label on the seasonal cycle, not a claim about that year.</param>
    /// <param name="seed">Seed. The same year, seed and site reproduce the run exactly.</param>
    public SyntheticWindYear GenerateYear(int year, int seed) =>
        GenerateYear(year, seed, _fittedAt);

    /// <summary>
    /// A synthetic year at any height.
    ///
    /// <para><b>This is the transfer step, and it is where the error budget lives.</b> The fitted
    /// distributions carry to any site sharing the station's wind climate, but the multiplication
    /// that moves them to another height rests on a single roughness length and a single profile
    /// law - see <see cref="WindProfile"/>. Extrapolating from a 15 m anemometer to a 100 m hub is
    /// where essentially all the uncertainty in a wind resource estimate sits, and it dwarfs
    /// everything in the fitted shapes.</para>
    /// </summary>
    /// <param name="year">Calendar year to generate.</param>
    /// <param name="seed">Seed. The same year, seed and site reproduce the run exactly.</param>
    /// <param name="site">Height and roughness to generate for.</param>
    /// <param name="profile">Profile law; defaults to <see cref="WindProfile.LogLaw"/>.</param>
    public SyntheticWindYear GenerateYear(
        int year,
        int seed,
        WindSite site,
        WindProfile? profile = null
    )
    {
        ArgumentNullException.ThrowIfNull(site);

        return CreateGenerator(site, profile).GenerateYear(year, seed);
    }

    /// <summary>
    /// An arbitrary run of days, for callers wanting something other than a calendar year - a
    /// heating season, a month, twenty years for a yield distribution.
    /// </summary>
    /// <param name="start">First day, inclusive.</param>
    /// <param name="endInclusive">Last day, inclusive.</param>
    /// <param name="seed">Seed. The same span, seed and site reproduce the run exactly.</param>
    /// <param name="site">Site to generate for; defaults to the fitting height.</param>
    /// <param name="profile">Profile law; defaults to <see cref="WindProfile.LogLaw"/>.</param>
    /// <returns>
    /// A lazy sequence - a long span is not materialised. The underlying generator carries
    /// day-to-day state, so enumerate it once, in order, on one thread.
    /// </returns>
    public IEnumerable<SyntheticWindDay> Generate(
        DateOnly start,
        DateOnly endInclusive,
        int seed,
        WindSite? site = null,
        WindProfile? profile = null
    ) => CreateGenerator(site, profile).Generate(start, endInclusive, new Random(seed));

    /// <summary>
    /// What a turbine would produce over a synthetic year - the shape a caller asking "is this site
    /// worth building on" actually wants.
    ///
    /// <para><b>Give it a hub height.</b> The default site is the 15 m anemometer, where the answer
    /// is arithmetically correct and practically meaningless: no turbine stands at 15 m, and the
    /// capacity factor comes out near 2%. The figure only means something at a height something
    /// could be built at - and the moment it is transferred there, it carries the profile
    /// uncertainty <see cref="WindProfile"/> warns about, <b>amplified</b>. The log law and the
    /// power law differ by a factor 1.35 in speed over a 15 m to 100 m step and by <b>2.5x in
    /// capacity factor</b>, because the working part of a power curve is cubic. Quote the profile
    /// alongside the number, always.</para>
    /// </summary>
    /// <param name="year">Calendar year to generate.</param>
    /// <param name="seed">Seed. The same year, seed and site reproduce the run exactly.</param>
    /// <param name="curve">The turbine.</param>
    /// <param name="site">Hub height and roughness; defaults to the fitting height. See above.</param>
    /// <param name="profile">Profile law; defaults to <see cref="WindProfile.LogLaw"/>.</param>
    public TurbineYield EstimateYield(
        int year,
        int seed,
        TurbinePowerCurve curve,
        WindSite? site = null,
        WindProfile? profile = null
    )
    {
        ArgumentNullException.ThrowIfNull(curve);

        var days = CreateGenerator(site, profile).GenerateYear(year, new Random(seed));

        return TurbineYield.Estimate(days, curve, IntradayShape);
    }

    /// <summary>
    /// A generator of this provider's model, bound to a site.
    ///
    /// <para>For callers that want to drive the generation themselves - day by day, or with a
    /// <see cref="Random"/> of their own. Not thread-safe, and it carries the previous day's
    /// weather; one per thread, and <see cref="SyntheticWindGenerator.Reset"/> between runs.</para>
    /// </summary>
    /// <param name="site">Site to generate for; defaults to the fitting height.</param>
    /// <param name="profile">Profile law; defaults to <see cref="WindProfile.LogLaw"/>.</param>
    internal SyntheticWindGenerator CreateGenerator(
        WindSite? site = null,
        WindProfile? profile = null
    )
    {
        var target = site ?? _fittedAt;
        return new SyntheticWindGenerator(Model, target.TransferFactorFrom(_fittedAt, profile));
    }
}

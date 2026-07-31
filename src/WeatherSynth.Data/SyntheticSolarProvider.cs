using WeatherSynth.Climate;
using WeatherSynth.Solar;

namespace WeatherSynth.Data;

/// <summary>
/// The library's entry point for synthetic daily solar data: fit once from a station record,
/// then serve as many years as asked for.
///
/// <para>Getting a year out of the model by hand means reading the station file, aggregating
/// intervals into days, applying two quality filters, building the clearness series against the
/// station's own coordinates, fitting the monthly distributions, and only then constructing a
/// generator against the <i>target</i> site's ceiling. Every one of those steps has a way to be
/// silently wrong. This class is that sequence, done once, correctly.</para>
///
/// <para><b>The split matters for cost.</b> Fitting reads ~152k rows and integrates a clear-sky
/// day for every one of ~5,800 usable days - seconds of work, and it is what this object holds.
/// A generated year afterwards costs 365 ceiling integrations and 365 draws. Construct one
/// provider for the process lifetime and call <see cref="GenerateYear(int, int)"/> per request;
/// constructing one per request re-does the fit every time for an identical result.</para>
///
/// <para><b>Thread-safe.</b> The provider holds only the fitted model, which is immutable. Each
/// call builds its own generator and its own ceiling, both of which are not thread-safe and
/// neither of which outlives the call.</para>
/// </summary>
public sealed class SyntheticSolarProvider
{
    private readonly SolarSite _fittedAt;

    /// <summary>
    /// Wraps an already-fitted model.
    /// </summary>
    /// <param name="model">The fitted distributions - twelve Beta pairs plus the persistence.</param>
    /// <param name="fittedAt">
    /// Site the model was fitted at. Used as the default generation site, and worth carrying
    /// even when generating elsewhere: it is what the transfer assumption is about.
    /// </param>
    public SyntheticSolarProvider(ClearSkyIndexModel model, SolarSite fittedAt)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _fittedAt = fittedAt ?? throw new ArgumentNullException(nameof(fittedAt));
    }

    /// <summary>
    /// Fits from a DWD station file on disk.
    /// </summary>
    /// <param name="csvPath">Path to the station's hourly solar record.</param>
    /// <param name="station">Station metadata. Its coordinates become the fitting geometry.</param>
    public static SyntheticSolarProvider FromDwdRecord(string csvPath, DwdStation station)
    {
        if (csvPath is null)
            throw new ArgumentNullException(nameof(csvPath));

        var days = DwdSolarDayAggregator.ToDays(DwdSolarReader.Read(csvPath));
        return FromStationDays(days, station);
    }

    /// <summary>
    /// Fits from station days already read, for callers that have the record in hand.
    ///
    /// <para>Applies both quality filters the fit depends on: incomplete days, and days whose
    /// zeros are a sensor outage rather than darkness. A day of false zeros drags the overcast
    /// tail down and nothing downstream can tell it apart from a genuinely dark one.</para>
    /// </summary>
    /// <param name="days">Aggregated station days, unfiltered.</param>
    /// <param name="station">Station metadata. Its coordinates become the fitting geometry.</param>
    public static SyntheticSolarProvider FromStationDays(
        IEnumerable<DwdSolarDay> days,
        DwdStation station
    )
    {
        if (days is null)
            throw new ArgumentNullException(nameof(days));
        if (station is null)
            throw new ArgumentNullException(nameof(station));

        var usable = days.Where(d => d.IsComplete && !d.HasImplausibleZeros);
        var series = ClearnessIndexBuilder.Build(usable, station);

        return new SyntheticSolarProvider(ClearSkyIndexModel.Fit(series), station.ToSite());
    }

    /// <summary>
    /// The fitted model behind this provider: the twelve monthly shapes and the persistence.
    ///
    /// <para>Exposed because it is the whole of what was learned, and callers reporting on the
    /// model - or checking a fit before trusting a year - need it.</para>
    /// </summary>
    public ClearSkyIndexModel Model { get; }

    /// <summary>
    /// A synthetic year at the site the model was fitted at.
    /// </summary>
    /// <param name="year">Calendar year. A label on the seasonal cycle, not a claim about that year.</param>
    /// <param name="seed">Seed. The same year, seed and site reproduce the run exactly.</param>
    public SyntheticSolarYear GenerateYear(int year, int seed) =>
        GenerateYear(year, seed, _fittedAt);

    /// <summary>
    /// A synthetic year at any site.
    ///
    /// <para><b>This is the transfer step.</b> The index divides geometry out, so the fitted
    /// distributions carry to any site sharing the fitting station's cloud climate - a few
    /// hundred kilometres of the same regional weather, not another continent. The ceiling is
    /// built here from <paramref name="site"/>, and that is what puts the geometry back.</para>
    /// </summary>
    /// <param name="year">Calendar year to generate.</param>
    /// <param name="seed">Seed. The same year, seed and site reproduce the run exactly.</param>
    /// <param name="site">Site to generate for.</param>
    public SyntheticSolarYear GenerateYear(int year, int seed, SolarSite site)
    {
        if (site is null)
            throw new ArgumentNullException(nameof(site));

        return CreateGenerator(site).GenerateYear(year, seed);
    }

    /// <summary>
    /// An arbitrary run of days, for callers wanting something other than a calendar year -
    /// a heating season, a month, twenty years for a yield distribution.
    /// </summary>
    /// <param name="start">First day, inclusive.</param>
    /// <param name="endInclusive">Last day, inclusive.</param>
    /// <param name="seed">Seed. The same span, seed and site reproduce the run exactly.</param>
    /// <param name="site">Site to generate for; defaults to the fitting station.</param>
    /// <returns>
    /// A lazy sequence - a long span is not materialised. The underlying generator carries
    /// day-to-day state, so enumerate it once, in order, on one thread.
    /// </returns>
    public IEnumerable<SyntheticSolarDay> Generate(
        DateOnly start,
        DateOnly endInclusive,
        int seed,
        SolarSite? site = null
    ) => CreateGenerator(site ?? _fittedAt).Generate(start, endInclusive, new Random(seed));

    /// <summary>
    /// A generator of this provider's model, bound to a site.
    ///
    /// <para>For callers that want to drive the generation themselves - day by day, or with a
    /// <see cref="Random"/> of their own. Not thread-safe, and it carries the previous day's
    /// weather; one per thread, and <see cref="SyntheticSolarGenerator.Reset"/> between runs.</para>
    /// </summary>
    /// <param name="site">Site to generate for; defaults to the fitting station.</param>
    public SyntheticSolarGenerator CreateGenerator(SolarSite? site = null)
    {
        var target = site ?? _fittedAt;
        return new SyntheticSolarGenerator(Model, target.CreateCeiling());
    }
}

using WeatherSynth.Climate;
using WeatherSynth.Data;
using WeatherSynth.Wind;

namespace WeatherSynth;

/// <summary>
/// The library's entry point for synthetic daily solar <i>and</i> wind data drawn together: fit
/// once from both station records, then serve as many jointly plausible years as asked for.
///
/// <para><b>This is opt-in, and that is the design.</b>
/// <see cref="SyntheticSolarProvider"/> and <see cref="SyntheticWindProvider"/> are untouched by
/// its existence and keep producing byte-for-byte what they always did. A caller who only wants
/// one resource, or who wants the two independent, should keep using them - they are cheaper, they
/// need only one record, and nothing here is a fix for them. What this class adds is the one thing
/// neither can supply: the dependence <i>between</i> the two.</para>
///
/// <para><b>What it costs and what it does not.</b> Coupling does not move either resource's
/// statistics. Each half comes back with the same twelve marginals, the same persistence and the
/// same annual figures in distribution as the independent providers give, because the dependence
/// is imposed by a Gaussian copula: it reorders which days coincide and touches neither resource's
/// monthly shapes nor its persistence. So the question this class answers is never "how much sun
/// will there be" or "how much wind", both of which the single-resource providers already answer
/// correctly. It is "how often do they fail together" - which is the whole question when sizing a
/// hybrid PV and wind system, and one the independent providers answer wrongly and confidently.</para>
///
/// <para><b>Both records are required.</b> There is no solar-only fallback: a coupling fitted from
/// one record does not exist. A repository without the wind file simply cannot construct this, and
/// should use <see cref="SyntheticSolarProvider"/>.</para>
///
/// <para><b>Thread-safe.</b> The provider holds three immutable fitted objects. Each call builds
/// its own generators and its own ceiling, none of which are thread-safe and none of which outlive
/// the call.</para>
/// </summary>
public sealed class CoupledWeatherProvider
{
    private readonly SyntheticSolarProvider _solar;
    private readonly SyntheticWindProvider _wind;

    /// <summary>
    /// Wraps two already-fitted providers and an already-fitted coupling.
    /// </summary>
    /// <param name="solar">The fitted solar half.</param>
    /// <param name="wind">The fitted wind half.</param>
    /// <param name="coupling">
    /// The fitted monthly cross-correlation, <b>solar first</b>. It must have been fitted through
    /// these two models' own CDFs: a coupling measured against different marginals describes a
    /// dependence between quantities these models do not produce.
    /// </param>
    internal CoupledWeatherProvider(
        SyntheticSolarProvider solar,
        SyntheticWindProvider wind,
        MonthlyCoupling coupling
    )
    {
        _solar = solar ?? throw new ArgumentNullException(nameof(solar));
        _wind = wind ?? throw new ArgumentNullException(nameof(wind));
        Coupling = coupling ?? throw new ArgumentNullException(nameof(coupling));
    }

    /// <summary>
    /// Fits everything from the two DWD station files on disk: both marginal models, both
    /// persistences, and the twelve coupling coefficients.
    ///
    /// <para>The one expensive call - it reads ~150k rows from each record and integrates a
    /// clear-sky day for every usable solar day. Construct one for the process lifetime.</para>
    /// </summary>
    /// <param name="solarCsvPath">Path to the solar station's hourly record.</param>
    /// <param name="solarStation">Solar station metadata; its coordinates become the fitting geometry.</param>
    /// <param name="windCsvPath">Path to the wind station's hourly record.</param>
    /// <param name="windStation">Wind station metadata; its anemometer height becomes the fitting height.</param>
    public static CoupledWeatherProvider FromDwdRecords(
        string solarCsvPath,
        DwdSolarStation solarStation,
        string windCsvPath,
        DwdWindStation windStation
    )
    {
        ArgumentNullException.ThrowIfNull(solarCsvPath);
        ArgumentNullException.ThrowIfNull(solarStation);
        ArgumentNullException.ThrowIfNull(windCsvPath);
        ArgumentNullException.ThrowIfNull(windStation);

        var solarDays = DwdSolarDayAggregator.ToDays(DwdSolarReader.Read(solarCsvPath));
        var windDays = DwdWindDayAggregator.ToDays(DwdWindReader.Read(windCsvPath));

        return FromStationDays(solarDays, solarStation, windDays, windStation);
    }

    /// <summary>
    /// Fits from station days already read, for callers that have both records in hand.
    ///
    /// <para>Each half is filtered exactly as its own provider filters it, and then the coupling is
    /// fitted on the days that survive <i>both</i> filters. Ordering is load-bearing and is the
    /// same constraint the persistence fit works under: the coefficient is measured through the two
    /// marginal models' CDFs, so those models must be fitted first.</para>
    /// </summary>
    /// <param name="solarDays">Aggregated solar station days, unfiltered.</param>
    /// <param name="solarStation">Solar station metadata.</param>
    /// <param name="windDays">Aggregated wind station days, unfiltered.</param>
    /// <param name="windStation">Wind station metadata.</param>
    internal static CoupledWeatherProvider FromStationDays(
        IEnumerable<DwdSolarDay> solarDays,
        DwdSolarStation solarStation,
        IEnumerable<DwdWindDay> windDays,
        DwdWindStation windStation
    )
    {
        ArgumentNullException.ThrowIfNull(solarDays);
        ArgumentNullException.ThrowIfNull(solarStation);
        ArgumentNullException.ThrowIfNull(windDays);
        ArgumentNullException.ThrowIfNull(windStation);

        // Each series is built once and shared, so the coupling sees exactly the days the marginals
        // were fitted on and no others.
        var windRecord = windDays as IReadOnlyList<DwdWindDay> ?? windDays.ToList();

        var clearness = SyntheticSolarProvider.BuildSeries(solarDays, solarStation);
        var speeds = WindSpeedSeriesBuilder.Build(windRecord);
        var hourly = HourlyWindModel.Fit(WindSpeedSeriesBuilder.BuildHourly(windRecord));

        var solar = SyntheticSolarProvider.FromSeries(clearness, solarStation);
        var wind = SyntheticWindProvider.FromSeries(speeds, hourly, windStation);

        var paired = CoupledSeriesBuilder.Build(clearness, speeds);

        var coupling = MonthlyCoupling.Fit(paired, solar.Model, wind.Model);

        return new CoupledWeatherProvider(solar, wind, coupling);
    }

    /// <summary>The fitted solar half. Usable on its own, and identical to a standalone fit.</summary>
    public SyntheticSolarProvider Solar => _solar;

    /// <summary>The fitted wind half. Usable on its own, and identical to a standalone fit.</summary>
    public SyntheticWindProvider Wind => _wind;

    /// <summary>
    /// The twelve fitted coupling coefficients - the whole of what this class adds over the two
    /// providers it holds.
    /// </summary>
    internal MonthlyCoupling Coupling { get; }

    /// <summary>
    /// A jointly generated year at both fitting sites.
    ///
    /// <para>The wind half carries a transfer factor of exactly 1.0 here, so it carries none of the
    /// profile uncertainty - see the overload below.</para>
    /// </summary>
    /// <param name="year">Calendar year. A label on the seasonal cycle, not a claim about that year.</param>
    /// <param name="seed">Seed. One seed drives both halves; the same seed reproduces the run exactly.</param>
    public CoupledWeatherYear GenerateYear(int year, int seed) =>
        GenerateYear(year, seed, solarSite: null, windSite: null);

    /// <summary>
    /// A jointly generated year at chosen sites.
    ///
    /// <para><b>The wind site is where the error budget lives.</b> The fitted distributions carry
    /// to any site sharing the station's wind climate, but the multiplication that moves them to
    /// another height rests on a single roughness length and a single profile law: the log law and
    /// the power law disagree by 26% over a 15 m to 100 m extrapolation, and that gap dwarfs
    /// everything in the fitted shapes - the coupling included. See <see cref="WindProfile"/>.</para>
    ///
    /// <para>The coupling itself is unaffected by either site. It is a statement about weather, and
    /// weather does not know how high the anemometer is.</para>
    /// </summary>
    /// <param name="year">Calendar year to generate.</param>
    /// <param name="seed">Seed. The same year, seed and sites reproduce the run exactly.</param>
    /// <param name="solarSite">Site for the solar half; defaults to the solar fitting station.</param>
    /// <param name="windSite">Site for the wind half; defaults to the wind fitting height.</param>
    /// <param name="profile">Wind profile law; defaults to <see cref="WindProfile.LogLaw"/>.</param>
    public CoupledWeatherYear GenerateYear(
        int year,
        int seed,
        SolarSite? solarSite,
        WindSite? windSite,
        WindProfile? profile = null
    )
    {
        var (start, endInclusive) = DailyRun.Year(year);
        int dayCount = endInclusive.DayNumber - start.DayNumber + 1;

        var solarGenerator = _solar.CreateGenerator(solarSite);
        var windGenerator = _wind.CreateGenerator(windSite, profile);
        var hourlyWind = _wind.CreateHourlyGenerator();
        var chain = CreateChain();

        // The daily stream exactly as Generate draws it; the hours have a stream of their own,
        // so asking for them moves no day.
        var random = new Random(seed);
        var hourlyRandom = new Random(HourlyWindGenerator.StreamSeed(seed));

        var days = new List<CoupledWeatherDay>(dayCount);
        var dayStarts = new List<DateTimeOffset>(dayCount);
        var clearSkyByHour = new double[dayCount * HourGrid.HoursPerDay];
        var speedByHour = new double[dayCount * HourGrid.HoursPerDay];

        foreach (var date in DailyRun.Days(start, endInclusive))
        {
            int offset = days.Count * HourGrid.HoursPerDay;

            // The solar site bounds the day for both halves, so a coupled hour is one instant.
            var dayStart = solarGenerator.DayStart(date);

            var day = NextDay(
                date,
                random,
                chain,
                solarGenerator,
                windGenerator,
                clearSkyByHour.AsSpan(offset, HourGrid.HoursPerDay)
            );

            hourlyWind.FillDay(
                day.Wind,
                dayStart,
                hourlyRandom,
                speedByHour.AsSpan(offset, HourGrid.HoursPerDay)
            );

            days.Add(day);
            dayStarts.Add(dayStart);
        }

        return new CoupledWeatherYear(
            year,
            seed,
            days,
            new HourGrid(dayStarts, solarGenerator.TimeZone),
            clearSkyByHour,
            speedByHour
        );
    }

    /// <summary>
    /// An arbitrary run of jointly generated days, for callers wanting something other than a
    /// calendar year - a heating season, twenty years for a joint yield distribution.
    /// </summary>
    /// <param name="start">First day, inclusive.</param>
    /// <param name="endInclusive">Last day, inclusive.</param>
    /// <param name="seed">Seed. The same span, seed and sites reproduce the run exactly.</param>
    /// <param name="solarSite">Site for the solar half; defaults to the solar fitting station.</param>
    /// <param name="windSite">Site for the wind half; defaults to the wind fitting height.</param>
    /// <param name="profile">Wind profile law; defaults to <see cref="WindProfile.LogLaw"/>.</param>
    /// <returns>
    /// A lazy sequence - a long span is not materialised. The underlying chain carries the previous
    /// day's weather for both resources, so enumerate it once, in order, on one thread.
    /// </returns>
    public IEnumerable<CoupledWeatherDay> Generate(
        DateOnly start,
        DateOnly endInclusive,
        int seed,
        SolarSite? solarSite = null,
        WindSite? windSite = null,
        WindProfile? profile = null
    )
    {
        DailyRun.Validate(start, endInclusive);

        var solarGenerator = _solar.CreateGenerator(solarSite);
        var windGenerator = _wind.CreateGenerator(windSite, profile);
        var chain = CreateChain();
        var random = new Random(seed);

        // Not an iterator, so the argument check happens on the call rather than on the first
        // MoveNext, matching what the two single-resource generators do.
        return DailyRun
            .Days(start, endInclusive)
            .Select(date =>
                NextDay(date, random, chain, solarGenerator, windGenerator, Span<double>.Empty)
            );
    }

    /// <summary>
    /// One joint draw turned into a day, optionally with its hourly clear-sky ceilings. The one
    /// place the pair becomes a day, so <see cref="Generate"/> and <see cref="GenerateYear(int, int)"/>
    /// cannot drift apart.
    /// </summary>
    private static CoupledWeatherDay NextDay(
        DateOnly date,
        Random random,
        CoupledLatentAr1Chain chain,
        SyntheticSolarGenerator solarGenerator,
        SyntheticWindGenerator windGenerator,
        Span<double> hourlyClearSkyWhPerM2
    )
    {
        var (index, speed) = chain.Next(date, random);

        return new CoupledWeatherDay(
            date,
            solarGenerator.DayFromIndex(date, index, hourlyClearSkyWhPerM2),
            windGenerator.DayFromReferenceSpeed(date, speed)
        );
    }

    /// <summary>
    /// A coupled chain over this provider's two models, for callers driving the generation
    /// themselves.
    ///
    /// <para>It yields latent-transformed pairs - a clear-sky index and a daily mean speed at the
    /// fitting height - and applies neither the ceiling nor the height transfer. Feed them to
    /// <see cref="SyntheticSolarGenerator.DayFromIndex(DateOnly, double)"/> and
    /// <see cref="SyntheticWindGenerator.DayFromReferenceSpeed"/> rather than re-deriving either;
    /// both exist for exactly this.</para>
    ///
    /// <para>Not thread-safe, and it carries both of the previous day's values; one per thread, and
    /// <see cref="CoupledLatentAr1Chain.Reset"/> between runs.</para>
    /// </summary>
    internal CoupledLatentAr1Chain CreateChain() =>
        new CoupledLatentAr1Chain(_solar.Model, _wind.Model, Coupling);
}

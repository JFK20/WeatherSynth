namespace WeatherSynth.Climate;

/// <summary>
/// Produces synthetic daily wind speeds: draws from the fitted monthly distributions with
/// day-to-day persistence, and applies the height transfer.
///
/// <para>The wind counterpart of <see cref="SyntheticSolarGenerator"/>, and the join between
/// the same two halves - a stochastic model that knows nothing about where it is, and a
/// deterministic factor that knows nothing about weather. The asymmetry worth noticing is how
/// much smaller wind's deterministic half is: solar's ceiling is an integration over the day's
/// sun positions, wind's is a single multiplication.</para>
///
/// <para><b>The transfer is applied per draw, and that is exactly equivalent to transferring
/// the fitted parameters.</b> Scaling every speed by c maps Weibull(k, A, γ) to
/// Weibull(k, cA, cγ) with k invariant, and sampling is a monotone transform of one uniform -
/// so scale-then-sample and sample-then-scale agree to the last bit, not approximately. Doing
/// it here keeps <see cref="WindSpeedModel"/> meaning "what was learned, at the height it was
/// learned at".</para>
///
/// <para><b>Order-dependent.</b> The speed carries day-to-day persistence
/// (<see cref="LatentAr1Chain"/>), so <see cref="GenerateDay"/> depends on the call before it.
/// Walk dates forwards, and call <see cref="Reset"/> between independent runs.
/// <see cref="Generate"/> resets for you.</para>
///
/// <para><b>Not thread-safe</b>, because the chain carries state. One generator per thread.</para>
/// </summary>
internal sealed class SyntheticWindGenerator
{
    private readonly LatentAr1Chain _chain;
    private readonly double _transferFactor;
    private readonly double _energyPatternFactor;

    /// <param name="model">Fitted speed distributions, from a measured record.</param>
    /// <param name="transferFactor">
    /// Height-and-roughness factor from the fitting station to the target, normally from
    /// <c>WindSite.TransferFactorFrom</c>. One means "generate at the fitting station", which
    /// is the only value that carries no profile error.
    /// </param>
    public SyntheticWindGenerator(WindSpeedModel model, double transferFactor = 1.0)
        : this(
            new LatentAr1Chain(model ?? throw new ArgumentNullException(nameof(model))),
            transferFactor,
            model.MeanEnergyPatternFactor
        ) { }

    /// <summary>
    /// Takes the speed source directly, for callers that want something other than the fitted
    /// persistence - a chain at phi 0 is the independent-sampling baseline the reports compare
    /// against.
    /// </summary>
    /// <param name="chain">Source of daily speeds. This generator owns its state.</param>
    /// <param name="transferFactor">Height-and-roughness factor; see the other constructor.</param>
    /// <param name="energyPatternFactor">
    /// Mean of the record's daily <c>mean(v³)/mean(v)³</c>, used to fill
    /// <see cref="SyntheticWindDay.MeanCubedSpeed"/>. Must be at least 1: below it the implied
    /// energy would be less than a perfectly steady day's, which no real day manages.
    /// </param>
    public SyntheticWindGenerator(
        LatentAr1Chain chain,
        double transferFactor,
        double energyPatternFactor
    )
    {
        _chain = chain ?? throw new ArgumentNullException(nameof(chain));

        if (!(transferFactor > 0.0))
            throw new ArgumentOutOfRangeException(
                nameof(transferFactor),
                transferFactor,
                "Transfer factor must be positive."
            );

        // NaN arrives here when a model was fitted from a series carrying no cubed speeds,
        // which is legitimate - it just means no energy proxy is available.
        if (!double.IsNaN(energyPatternFactor) && energyPatternFactor < 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(energyPatternFactor),
                energyPatternFactor,
                "Energy pattern factor cannot be below 1: mean(v³) >= mean(v)³ always, with "
                    + "equality only for a perfectly steady day."
            );

        _transferFactor = transferFactor;
        _energyPatternFactor = energyPatternFactor;
    }

    /// <summary>The height-and-roughness factor this generator applies to every draw.</summary>
    public double TransferFactor => _transferFactor;

    /// <summary>Starts a fresh run, forgetting the previous day's weather.</summary>
    public void Reset() => _chain.Reset();

    /// <summary>
    /// Generates one synthetic day, continuing on from the day generated before it.
    ///
    /// <para>No clamping: the Weibull's support already bounds the draw below by its location
    /// parameter, and there is no upper bound to impose - a synthetic gale is a real
    /// possibility rather than an artefact, and the fitted tail is what decides how often.</para>
    /// </summary>
    public SyntheticWindDay GenerateDay(DateOnly date, Random random)
    {
        if (random is null)
            throw new ArgumentNullException(nameof(random));

        return DayFromReferenceSpeed(date, _chain.Next(date, random));
    }

    /// <summary>
    /// Builds the day for a speed that has already been drawn, rather than drawing one.
    ///
    /// <para>The seam for <see cref="CoupledLatentAr1Chain"/>, which draws the speed jointly
    /// with a clear-sky index and so cannot go through <see cref="GenerateDay"/>. It exists so
    /// that the height transfer and the energy-pattern correction keep exactly one definition -
    /// a coupled generator applying its own transfer would be a second place for it to be
    /// applied twice, which is the first thing to suspect when a synthetic annual mean comes
    /// out far from the record's.</para>
    ///
    /// <para>Stateless, and it does not advance the persistence chain - the caller owns the
    /// ordering.</para>
    /// </summary>
    /// <param name="date">The day being built.</param>
    /// <param name="atReference">A daily mean speed drawn elsewhere, at the fitting height.</param>
    public SyntheticWindDay DayFromReferenceSpeed(DateOnly date, double atReference)
    {
        double speed = atReference * _transferFactor;

        return new SyntheticWindDay(
            date,
            atReference,
            speed,
            _energyPatternFactor * speed * speed * speed
        );
    }

    /// <summary>
    /// Generates a continuous run of days, inclusive of both ends.
    ///
    /// <para>Streams, so a long run does not materialise at once - and unlike the solar
    /// generator this is genuinely cheap per day: one uniform, one logarithm and one power,
    /// with no ceiling to integrate.</para>
    ///
    /// <para>The persistence chain is reset when enumeration begins, so a run depends only on
    /// the seed it was given and not on whatever this generator produced before.</para>
    /// </summary>
    public IEnumerable<SyntheticWindDay> Generate(
        DateOnly start,
        DateOnly endInclusive,
        Random random
    )
    {
        if (random is null)
            throw new ArgumentNullException(nameof(random));
        if (endInclusive < start)
            throw new ArgumentException("End must not precede start.", nameof(endInclusive));

        // Outside the iterator, so both the argument checks and the reset happen when Generate
        // is called rather than on the first MoveNext. Otherwise two enumerables taken from one
        // generator would silently share a chain until whichever was enumerated first.
        Reset();

        return Iterate(start, endInclusive, random);
    }

    /// <summary>
    /// Generates one calendar year at daily resolution, 1 January to 31 December inclusive.
    ///
    /// <para>The year is a label on the seasonal cycle, not a claim about that particular year.
    /// Unlike the solar generator, two runs over different years with the same seed are
    /// identical apart from the calendar: there is no ceiling for the year to change.</para>
    /// </summary>
    public IEnumerable<SyntheticWindDay> GenerateYear(int year, Random random) =>
        Generate(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), random);

    /// <summary>
    /// Generates a whole year from a seed, with its monthly and annual aggregates - the shape a
    /// caller asking "give me a plausible year for this site" actually wants.
    /// </summary>
    /// <param name="year">Calendar year to generate.</param>
    /// <param name="seed">Seed for the run. The same seed and site reproduce it exactly.</param>
    public SyntheticWindYear GenerateYear(int year, int seed)
    {
        var days = new List<SyntheticWindDay>(366);
        days.AddRange(GenerateYear(year, new Random(seed)));

        return new SyntheticWindYear(year, seed, days);
    }

    private IEnumerable<SyntheticWindDay> Iterate(
        DateOnly start,
        DateOnly endInclusive,
        Random random
    )
    {
        for (var date = start; date <= endInclusive; date = date.AddDays(1))
            yield return GenerateDay(date, random);
    }
}

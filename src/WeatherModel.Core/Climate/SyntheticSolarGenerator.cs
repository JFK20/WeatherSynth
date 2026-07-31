using System;
using System.Collections.Generic;
using WeatherModel.Solar;

namespace WeatherModel.Climate
{
    /// <summary>One generated day: the sampled index, the ceiling it was applied to, and the result.</summary>
    /// <param name="Date">The day generated.</param>
    /// <param name="ClearSkyIndex">The value drawn from the fitted distribution for that month.</param>
    /// <param name="ClearSkyWhPerM2">The deterministic ceiling for that date and location.</param>
    /// <param name="GhiWhPerM2">Synthetic global horizontal irradiation, the product of the two.</param>
    public readonly record struct SyntheticSolarDay(
        DateOnly Date,
        double ClearSkyIndex,
        double ClearSkyWhPerM2,
        double GhiWhPerM2)
    {
        /// <summary>Synthetic daily irradiation in kWh/m², the unit the totals are usually quoted in.</summary>
        public double GhiKWhPerM2 => GhiWhPerM2 / 1000.0;
    }

    /// <summary>
    /// Produces synthetic daily irradiation: draws a clear-sky index from the fitted
    /// distribution and multiplies it by the deterministic ceiling for that date and place.
    ///
    /// <para>This is the join between the two halves of the model, and the reason the whole
    /// thing is built on an index rather than on irradiance. The distribution knows nothing
    /// about latitude or season; the ceiling knows nothing about weather. Neither could produce
    /// a plausible day alone.</para>
    ///
    /// <para><b>Fit at one site, apply at another.</b> The index divides out geometry, so a
    /// distribution fitted at Bochum transfers to any site sharing its cloud climate. The
    /// ceiling passed here must be built with the <i>target</i> site's coordinates - that is
    /// what puts the geometry back.</para>
    ///
    /// <para><b>Order-dependent.</b> The index carries day-to-day persistence
    /// (<see cref="ClearSkyIndexChain"/>), so <see cref="GenerateDay"/> depends on the call
    /// before it. Walk dates forwards, and call <see cref="Reset"/> between independent runs.
    /// <see cref="Generate"/> resets for you.</para>
    ///
    /// <para><b>Not thread-safe.</b> The underlying solar position calculator memoises per-date
    /// terms, and the persistence chain carries state. One generator per thread.</para>
    /// </summary>
    public sealed class SyntheticSolarGenerator
    {
        private readonly ClearSkyIndexChain _chain;
        private readonly DailyClearSkyCalculator _ceiling;

        /// <param name="model">Fitted index distribution, from a measured record.</param>
        /// <param name="ceiling">Clear-sky calculator built for the site being generated for.</param>
        public SyntheticSolarGenerator(ClearSkyIndexModel model, DailyClearSkyCalculator ceiling)
            : this(new ClearSkyIndexChain(model), ceiling)
        {
        }

        /// <summary>
        /// Takes the index source directly, for callers that want something other than the fitted
        /// persistence - a chain at phi 0 is the independent-sampling baseline the reports
        /// compare against.
        /// </summary>
        /// <param name="chain">Source of clear-sky indices. This generator owns its state.</param>
        /// <param name="ceiling">Clear-sky calculator built for the site being generated for.</param>
        public SyntheticSolarGenerator(ClearSkyIndexChain chain, DailyClearSkyCalculator ceiling)
        {
            _chain = chain ?? throw new ArgumentNullException(nameof(chain));
            _ceiling = ceiling ?? throw new ArgumentNullException(nameof(ceiling));
        }

        /// <summary>Starts a fresh run, forgetting the previous day's weather.</summary>
        public void Reset() => _chain.Reset();

        /// <summary>
        /// Generates one synthetic day, continuing on from the day generated before it.
        ///
        /// <para>No clamping is applied. The pipeline in knowledge.md §2 calls for a final clamp
        /// to [0, clear-sky] as a safety net, but the Beta's support already bounds the draw to
        /// [0, 1.25] structurally, and clamping at 1.0 would delete the days that genuinely beat
        /// a monthly-mean ceiling - which are real, and are the tail worth reproducing.</para>
        /// </summary>
        public SyntheticSolarDay GenerateDay(DateOnly date, Random random)
        {
            if (random is null) throw new ArgumentNullException(nameof(random));

            double index = _chain.Next(date, random);
            double clearSky = _ceiling.ForDate(date.ToDateTime(TimeOnly.MinValue)).GhiWhPerM2;

            return new SyntheticSolarDay(date, index, clearSky, index * clearSky);
        }

        /// <summary>
        /// Generates a continuous run of days, inclusive of both ends.
        ///
        /// <para>Streams, so a long run does not materialise in memory at once. Each day costs
        /// one clear-sky integration, which dominates the sampling by a wide margin.</para>
        ///
        /// <para>The persistence chain is reset when enumeration begins, so a run depends only on
        /// the seed it was given and not on whatever this generator produced before.</para>
        /// </summary>
        public IEnumerable<SyntheticSolarDay> Generate(DateOnly start, DateOnly endInclusive, Random random)
        {
            if (random is null) throw new ArgumentNullException(nameof(random));
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
        /// <para>366 days in a leap year, and that is deliberate rather than an accident of
        /// <see cref="DateOnly"/> arithmetic: 29 February is a real day with a real ceiling, and
        /// dropping it would put a one-day hole in the persistence chain for no gain.</para>
        ///
        /// <para>The year is a label on the seasonal cycle, not a claim about that particular
        /// year. Two runs over different years with the same seed differ only in their ceilings -
        /// the weather is drawn fresh either way. Same contract as
        /// <see cref="Generate"/>: streaming, and reset before the first day.</para>
        /// </summary>
        /// <param name="year">Calendar year to generate.</param>
        /// <param name="random">Source of randomness; seed it to make a run reproducible.</param>
        public IEnumerable<SyntheticSolarDay> GenerateYear(int year, Random random) =>
            Generate(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), random);

        /// <summary>
        /// Generates a whole year from a seed, with its monthly and annual totals - the shape a
        /// caller asking "give me a plausible year for this site" actually wants.
        ///
        /// <para>The seed is the argument rather than a <see cref="Random"/> because it travels:
        /// it is carried on the result, so the same year can be re-requested later and come back
        /// identical. Passing a shared <see cref="Random"/> could not promise that.</para>
        /// </summary>
        /// <param name="year">Calendar year to generate.</param>
        /// <param name="seed">Seed for the run. The same seed and site reproduce it exactly.</param>
        public SyntheticSolarYear GenerateYear(int year, int seed)
        {
            var days = new List<SyntheticSolarDay>(366);
            days.AddRange(GenerateYear(year, new Random(seed)));

            return new SyntheticSolarYear(year, seed, days);
        }

        private IEnumerable<SyntheticSolarDay> Iterate(
            DateOnly start, DateOnly endInclusive, Random random)
        {
            for (var date = start; date <= endInclusive; date = date.AddDays(1))
                yield return GenerateDay(date, random);
        }
    }
}

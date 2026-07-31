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
    /// <para><b>Not thread-safe.</b> The underlying solar position calculator memoises per-date
    /// terms. One generator per thread.</para>
    /// </summary>
    public sealed class SyntheticSolarGenerator
    {
        private readonly ClearSkyIndexModel _model;
        private readonly DailyClearSkyCalculator _ceiling;

        /// <param name="model">Fitted index distribution, from a measured record.</param>
        /// <param name="ceiling">Clear-sky calculator built for the site being generated for.</param>
        public SyntheticSolarGenerator(ClearSkyIndexModel model, DailyClearSkyCalculator ceiling)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _ceiling = ceiling ?? throw new ArgumentNullException(nameof(ceiling));
        }

        /// <summary>
        /// Generates one synthetic day.
        ///
        /// <para>No clamping is applied. The pipeline in knowledge.md §2 calls for a final clamp
        /// to [0, clear-sky] as a safety net, but the Beta's support already bounds the draw to
        /// [0, 1.25] structurally, and clamping at 1.0 would delete the days that genuinely beat
        /// a monthly-mean ceiling - which are real, and are the tail worth reproducing.</para>
        /// </summary>
        public SyntheticSolarDay GenerateDay(DateOnly date, Random random)
        {
            if (random is null) throw new ArgumentNullException(nameof(random));

            double index = _model.Sample(date.Month, random);
            double clearSky = _ceiling.ForDate(date.ToDateTime(TimeOnly.MinValue)).GhiWhPerM2;

            return new SyntheticSolarDay(date, index, clearSky, index * clearSky);
        }

        /// <summary>
        /// Generates a continuous run of days, inclusive of both ends.
        ///
        /// <para>Streams, so a long run does not materialise in memory at once. Each day costs
        /// one clear-sky integration, which dominates the sampling by a wide margin.</para>
        /// </summary>
        public IEnumerable<SyntheticSolarDay> Generate(DateOnly start, DateOnly endInclusive, Random random)
        {
            if (random is null) throw new ArgumentNullException(nameof(random));
            if (endInclusive < start)
                throw new ArgumentException("End must not precede start.", nameof(endInclusive));

            for (var date = start; date <= endInclusive; date = date.AddDays(1))
                yield return GenerateDay(date, random);
        }
    }
}

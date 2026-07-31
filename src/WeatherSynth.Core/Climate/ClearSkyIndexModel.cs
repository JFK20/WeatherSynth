using System;
using System.Collections.Generic;
using System.Linq;

namespace WeatherSynth.Climate
{
    /// <summary>
    /// The fitted distribution of the daily clear-sky index: one <see cref="ScaledBeta"/> per
    /// calendar month, plus a pooled fit over the whole year.
    ///
    /// <para>This is the stochastic half of the generator. Everything upstream of it is
    /// deterministic physics; everything downstream is a draw from these twelve curves. Twelve
    /// (alpha, beta) pairs plus the single <see cref="Persistence"/> coefficient is the entire
    /// learned model.</para>
    ///
    /// <para><b>Monthly rather than per-day-of-year.</b> A 5-day window around each day of year
    /// leaves roughly 85 samples per window against a month's ~480, and the seasonal signal in
    /// the index is smooth and weak enough that the extra resolution buys noise rather than
    /// detail. Months are also what the turbidity climatology underneath already uses, so the
    /// two resolutions agree.</para>
    ///
    /// <para><b>The marginals alone are not enough.</b> Sampling from these twelve curves
    /// independently per day gives the right histogram and the wrong sequences - real cloudy
    /// spells cluster, independent draws scatter. <see cref="ClearSkyIndexChain"/> supplies the
    /// missing ordering using <see cref="Persistence"/>, without touching the shapes;
    /// <see cref="IndexSeriesStatistics.Lag1Autocorrelation"/> on observed against synthetic
    /// output is what says whether it worked.</para>
    /// </summary>
    public sealed class ClearSkyIndexModel
    {
        /// <summary>
        /// Default upper end of the support.
        ///
        /// <para>Not 1.0. The clear-sky ceiling carries a monthly-mean turbidity, so a day
        /// cleaner than its month's average legitimately beats it; the Bochum record reaches
        /// 1.185. 1.25 clears that with room for a site whose clear-day scatter is wider.</para>
        /// </summary>
        public const double DefaultSupport = 1.25;

        /// <summary>Fewer observations than this in a month and the pooled fit is used instead.</summary>
        public const int MinimumSamplesPerMonth = 30;

        /// <summary>
        /// Ceiling on the fitted persistence. A latent AR(1) needs |phi| &lt; 1 to be stationary,
        /// and anything this close to 1 is a sign the estimate has gone wrong rather than a site
        /// with extraordinary weather.
        /// </summary>
        private const double MaximumPersistence = 0.99;

        private readonly ScaledBeta[] _monthly;

        private ClearSkyIndexModel(
            ScaledBeta[] monthly,
            ScaledBeta pooled,
            double support,
            double persistence
        )
        {
            _monthly = monthly;
            Pooled = pooled;
            Support = support;
            Persistence = persistence;
        }

        /// <summary>Upper end of the support every fit in this model was scaled to.</summary>
        public double Support { get; }

        /// <summary>The fit over every day in the record, ignoring season.</summary>
        public ScaledBeta Pooled { get; }

        /// <summary>
        /// Lag-1 coefficient of the latent AR(1) process behind the index, in [0, 1).
        ///
        /// <para>The thirteenth parameter of the model, and the only one that is not a shape. It
        /// is measured on the <i>normal scores</i> of the record - each day mapped through its own
        /// month's fitted CDF and then through the inverse normal - not on the raw index. That
        /// transform divides out the seasonal cycle by construction, so what is left is weather
        /// persistence alone.</para>
        ///
        /// <para>At Bochum this comes out at 0.353, <i>smaller</i> than the 0.437 lag-1 of the raw
        /// index, because 0.137 of that 0.437 is the seasonal cycle on its own - and the season is
        /// re-supplied downstream by the twelve monthly marginals rather than by this number.
        /// Fitting phi directly against 0.437 would count it twice. The direction is
        /// site-dependent: a site with a weak seasonal swing and strongly shaped Betas goes the
        /// other way, so read the two numbers as living in different spaces rather than as a
        /// correction to each other (knowledge.md §13).</para>
        /// </summary>
        public double Persistence { get; }

        /// <summary>The fit for one calendar month, 1-12.</summary>
        public ScaledBeta ForMonth(int month)
        {
            if (month < 1 || month > 12)
                throw new ArgumentOutOfRangeException(nameof(month), month, "Month must be 1-12.");

            return _monthly[month - 1];
        }

        /// <summary>
        /// Fits the model to a measured series.
        /// </summary>
        /// <param name="series">
        /// Daily clearness records. Filter out incomplete days and sensor outages first: a day
        /// recorded as valid zeros drags the overcast tail down and there is nothing downstream
        /// that can tell it apart from genuine darkness.
        /// </param>
        /// <param name="support">
        /// Upper end of the Beta support. Must exceed every observed index, or the fit throws
        /// rather than silently clipping the clear end.
        /// </param>
        public static ClearSkyIndexModel Fit(
            IEnumerable<DailyClearness> series,
            double support = DefaultSupport
        )
        {
            if (series is null)
                throw new ArgumentNullException(nameof(series));

            var byMonth = new List<double>[12];
            for (int i = 0; i < 12; i++)
                byMonth[i] = new List<double>();

            // Dates are kept alongside the values because the persistence fit below needs them,
            // and the caller's sequence may be lazy - enumerating it a second time is not safe.
            var dated = new List<(DateOnly Date, double Index)>();

            foreach (var day in series)
            {
                double index = day.ClearSkyIndex;
                if (double.IsNaN(index))
                    continue;

                byMonth[day.Date.Month - 1].Add(index);
                dated.Add((day.Date, index));
            }

            if (dated.Count == 0)
                throw new ArgumentException("No usable days in the series.", nameof(series));

            var pooled = ScaledBeta.FitByMoments(dated.Select(d => d.Index), support);

            var monthly = new ScaledBeta[12];
            for (int i = 0; i < 12; i++)
            {
                monthly[i] =
                    byMonth[i].Count >= MinimumSamplesPerMonth
                        ? ScaledBeta.FitByMoments(byMonth[i], support)
                        : pooled;
            }

            // Order matters: phi is measured through the monthly CDFs, so they have to exist
            // first. The same ordering constraint the clear-sky ceiling imposes on the Betas,
            // one level further up.
            double persistence = FitPersistence(dated, monthly);

            return new ClearSkyIndexModel(monthly, pooled, support, persistence);
        }

        /// <summary>
        /// Lag-1 correlation of the record's normal scores, which is the AR(1) coefficient the
        /// generator needs.
        ///
        /// <para>Each day is mapped through its own month's fitted CDF, giving a value that is
        /// uniform if the fit is good, and then through the inverse normal. Because the transform
        /// is per month, the seasonal cycle comes out with it and the resulting series carries
        /// weather persistence and nothing else - so its lag-1 correlation <i>is</i> phi, with no
        /// simulation loop or search required.</para>
        /// </summary>
        private static double FitPersistence(
            IReadOnlyList<(DateOnly Date, double Index)> dated,
            ScaledBeta[] monthly
        )
        {
            // The Beta CDF reaches 0 and 1 exactly at the ends of the support, and the inverse
            // normal sends those to infinity. Nudging inside costs nothing at this magnitude.
            const double edge = 1e-12;

            var latent = new List<(DateOnly Date, double Index)>(dated.Count);

            foreach (var (date, index) in dated)
            {
                double u = monthly[date.Month - 1].CumulativeProbability(index);
                u = Math.Clamp(u, edge, 1.0 - edge);
                latent.Add((date, Gaussian.Quantile(u)));
            }

            // Reuses the gap-aware estimator, which is exactly right here: only genuinely
            // consecutive days are informative about a lag-1 coefficient.
            double phi = IndexSeriesStatistics.Lag1Autocorrelation(latent);

            // A record too short or too broken to estimate from means no persistence, not NaN
            // irradiance downstream. Negative persistence is not a thing daily cloud does.
            if (double.IsNaN(phi))
                return 0.0;

            return Math.Clamp(phi, 0.0, MaximumPersistence);
        }

        /// <summary>
        /// Draws one clear-sky index for the given calendar month, independently of every other
        /// draw.
        ///
        /// <para>Right for a single day, wrong for a series: this is the marginal on its own,
        /// which is the histogram-right, sequence-wrong behaviour described above. Use
        /// <see cref="ClearSkyIndexChain"/> for anything longer than one day.</para>
        /// </summary>
        public double Sample(int month, Random random) => ForMonth(month).Sample(random);
    }

    /// <summary>
    /// Diagnostics that describe a series of daily index values rather than fit anything to it.
    /// </summary>
    public static class IndexSeriesStatistics
    {
        /// <summary>
        /// Correlation between each day's index and the previous day's.
        ///
        /// <para>The single number that says whether a generator reproduces cloud persistence.
        /// Independent sampling gives ~0 by construction whatever its histogram looks like;
        /// measured daily solar records typically land around 0.3-0.5. This is the acceptance
        /// check on <see cref="ClearSkyIndexChain"/>, and it is also what fits the chain's
        /// coefficient - run over the record's normal scores it yields
        /// <see cref="ClearSkyIndexModel.Persistence"/> directly.</para>
        ///
        /// <para>Only genuinely consecutive calendar days count as pairs, so gaps in the record
        /// are skipped rather than being treated as adjacent.</para>
        /// </summary>
        /// <returns>The lag-1 correlation, or NaN if there are fewer than two consecutive pairs.</returns>
        public static double Lag1Autocorrelation(IEnumerable<(DateOnly Date, double Index)> series)
        {
            if (series is null)
                throw new ArgumentNullException(nameof(series));

            var ordered = series.OrderBy(d => d.Date).ToList();

            var today = new List<double>();
            var yesterday = new List<double>();

            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Date.DayNumber - ordered[i - 1].Date.DayNumber != 1)
                    continue;
                if (double.IsNaN(ordered[i].Index) || double.IsNaN(ordered[i - 1].Index))
                    continue;

                today.Add(ordered[i].Index);
                yesterday.Add(ordered[i - 1].Index);
            }

            if (today.Count < 2)
                return double.NaN;

            return Correlation(yesterday, today);
        }

        private static double Correlation(IReadOnlyList<double> x, IReadOnlyList<double> y)
        {
            double meanX = x.Average();
            double meanY = y.Average();

            double covariance = 0.0,
                varianceX = 0.0,
                varianceY = 0.0;
            for (int i = 0; i < x.Count; i++)
            {
                double dx = x[i] - meanX;
                double dy = y[i] - meanY;
                covariance += dx * dy;
                varianceX += dx * dx;
                varianceY += dy * dy;
            }

            double denominator = Math.Sqrt(varianceX * varianceY);
            return denominator > 0.0 ? covariance / denominator : double.NaN;
        }
    }
}

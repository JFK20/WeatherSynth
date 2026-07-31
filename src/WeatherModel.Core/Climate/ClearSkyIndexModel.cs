using System;
using System.Collections.Generic;
using System.Linq;

namespace WeatherModel.Climate
{
    /// <summary>
    /// The fitted distribution of the daily clear-sky index: one <see cref="ScaledBeta"/> per
    /// calendar month, plus a pooled fit over the whole year.
    ///
    /// <para>This is the stochastic half of the generator. Everything upstream of it is
    /// deterministic physics; everything downstream is a draw from these twelve curves. Twelve
    /// (alpha, beta) pairs is the entire learned model.</para>
    ///
    /// <para><b>Monthly rather than per-day-of-year.</b> A 5-day window around each day of year
    /// leaves roughly 85 samples per window against a month's ~480, and the seasonal signal in
    /// the index is smooth and weak enough that the extra resolution buys noise rather than
    /// detail. Months are also what the turbidity climatology underneath already uses, so the
    /// two resolutions agree.</para>
    ///
    /// <para><b>What this model does not capture:</b> day-to-day persistence. Sampling from it
    /// independently per day gives the right histogram and the wrong sequences - real cloudy
    /// spells cluster. Compare <see cref="IndexSeriesStatistics.Lag1Autocorrelation"/> on
    /// observed against synthetic output to see the size of the gap.</para>
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

        private readonly ScaledBeta[] _monthly;

        private ClearSkyIndexModel(ScaledBeta[] monthly, ScaledBeta pooled, double support)
        {
            _monthly = monthly;
            Pooled = pooled;
            Support = support;
        }

        /// <summary>Upper end of the support every fit in this model was scaled to.</summary>
        public double Support { get; }

        /// <summary>The fit over every day in the record, ignoring season.</summary>
        public ScaledBeta Pooled { get; }

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
            IEnumerable<DailyClearness> series, double support = DefaultSupport)
        {
            if (series is null) throw new ArgumentNullException(nameof(series));

            var byMonth = new List<double>[12];
            for (int i = 0; i < 12; i++)
                byMonth[i] = new List<double>();

            var all = new List<double>();

            foreach (var day in series)
            {
                double index = day.ClearSkyIndex;
                if (double.IsNaN(index))
                    continue;

                byMonth[day.Date.Month - 1].Add(index);
                all.Add(index);
            }

            if (all.Count == 0)
                throw new ArgumentException("No usable days in the series.", nameof(series));

            var pooled = ScaledBeta.FitByMoments(all, support);

            var monthly = new ScaledBeta[12];
            for (int i = 0; i < 12; i++)
            {
                monthly[i] = byMonth[i].Count >= MinimumSamplesPerMonth
                    ? ScaledBeta.FitByMoments(byMonth[i], support)
                    : pooled;
            }

            return new ClearSkyIndexModel(monthly, pooled, support);
        }

        /// <summary>Draws one clear-sky index for the given calendar month.</summary>
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
        /// measured daily solar records typically land around 0.3-0.5. This is the gap a Markov
        /// chain or an autoregressive term is there to close.</para>
        ///
        /// <para>Only genuinely consecutive calendar days count as pairs, so gaps in the record
        /// are skipped rather than being treated as adjacent.</para>
        /// </summary>
        /// <returns>The lag-1 correlation, or NaN if there are fewer than two consecutive pairs.</returns>
        public static double Lag1Autocorrelation(IEnumerable<(DateOnly Date, double Index)> series)
        {
            if (series is null) throw new ArgumentNullException(nameof(series));

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

            double covariance = 0.0, varianceX = 0.0, varianceY = 0.0;
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

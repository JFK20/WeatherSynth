using System;
using System.Collections.Generic;
using System.Linq;

namespace WeatherSynth.Climate
{
    /// <summary>
    /// Diagnostics that describe a series of daily values rather than fit anything to it.
    ///
    /// <para>Quantity-agnostic, and kept that way deliberately: a clear-sky index and a wind speed
    /// are the same shape of thing here - one number a day, with gaps - and the persistence
    /// question asked of both is identical.</para>
    /// </summary>
    public static class SeriesStatistics
    {
        /// <summary>
        /// Correlation between each day's value and the previous day's.
        ///
        /// <para>The single number that says whether a generator reproduces weather persistence.
        /// Independent sampling gives close to zero by construction whatever its histogram looks
        /// like; measured daily solar records land around 0.3-0.5 and daily wind speeds higher
        /// still. This is the acceptance check on <see cref="LatentAr1Chain"/>, and it is also
        /// what fits the chain's coefficient - run over a record's normal scores it yields
        /// <see cref="IMonthlyMarginals.Persistence"/> directly.</para>
        ///
        /// <para>Only genuinely consecutive calendar days count as pairs, so gaps in the record
        /// are skipped rather than being treated as adjacent.</para>
        /// </summary>
        /// <returns>The lag-1 correlation, or NaN if there are fewer than two consecutive pairs.</returns>
        public static double Lag1Autocorrelation(IEnumerable<(DateOnly Date, double Value)> series)
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
                if (double.IsNaN(ordered[i].Value) || double.IsNaN(ordered[i - 1].Value))
                    continue;

                today.Add(ordered[i].Value);
                yesterday.Add(ordered[i - 1].Value);
            }

            if (today.Count < 2)
                return double.NaN;

            return Correlation(yesterday, today);
        }

        /// <summary>
        /// Ceiling on a fitted persistence coefficient. A latent AR(1) needs |phi| &lt; 1 to be
        /// stationary, and anything this close to 1 is a sign the estimate has gone wrong rather
        /// than a site with extraordinary weather.
        /// </summary>
        public const double MaximumPersistence = 0.99;

        /// <summary>
        /// The lag-1 coefficient of the latent AR(1) process behind a dated series, fitted through
        /// that series' own monthly marginals.
        ///
        /// <para>Each day is mapped through its own month's fitted CDF, giving a value that is
        /// uniform if the fit is good, and then through the inverse normal. Because the transform
        /// is per month, the seasonal cycle comes out with it and what is left carries weather
        /// persistence alone - so the lag-1 correlation of the scores <i>is</i> phi, with no
        /// simulation loop or search required. Fitting against the raw series instead would count
        /// the season twice, since the twelve marginals re-supply it downstream.</para>
        ///
        /// <para>Quantity-agnostic, and shared by both halves of the library: a clear-sky index
        /// and a wind speed differ only in the CDF passed in. The caller supplies a delegate
        /// rather than an <see cref="IMonthlyMarginals"/> because this runs <i>during</i> a fit,
        /// before the model it belongs to exists.</para>
        /// </summary>
        /// <param name="series">Dated observations. NaN values must already be filtered out.</param>
        /// <param name="cumulativeProbability">
        /// The month's fitted CDF, called as <c>(value, month)</c> with a 1-12 month.
        /// </param>
        /// <returns>
        /// Phi in [0, <see cref="MaximumPersistence"/>]. A record too short or too broken to
        /// estimate from yields 0 - no persistence - rather than a NaN that would propagate into
        /// every generated day downstream. Negative persistence is not a thing daily weather does,
        /// so it clamps away too.
        /// </returns>
        public static double LatentPersistence(
            IEnumerable<(DateOnly Date, double Value)> series,
            Func<double, int, double> cumulativeProbability
        )
        {
            if (series is null)
                throw new ArgumentNullException(nameof(series));
            if (cumulativeProbability is null)
                throw new ArgumentNullException(nameof(cumulativeProbability));

            // A fitted CDF can reach 0 and 1 - exactly, at the ends of a Beta's support, or by
            // rounding for a day far out in a Weibull's tail - and the inverse normal sends those
            // to infinity. Nudging inside costs nothing at this magnitude.
            const double edge = 1e-12;

            var latent = new List<(DateOnly Date, double Score)>();

            foreach (var (date, value) in series)
            {
                double u = cumulativeProbability(value, date.Month);
                u = Math.Clamp(u, edge, 1.0 - edge);
                latent.Add((date, Gaussian.Quantile(u)));
            }

            // The gap-aware estimator above, which is exactly right here: only genuinely
            // consecutive days are informative about a lag-1 coefficient.
            double phi = Lag1Autocorrelation(latent);

            if (double.IsNaN(phi))
                return 0.0;

            return Math.Clamp(phi, 0.0, MaximumPersistence);
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

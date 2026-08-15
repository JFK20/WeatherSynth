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

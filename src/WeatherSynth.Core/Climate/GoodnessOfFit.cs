using System;
using System.Collections.Generic;
using System.Linq;

namespace WeatherSynth.Climate
{
    /// <summary>
    /// How well a fitted distribution matches the sample it was fitted to.
    ///
    /// <para>Written against an arbitrary CDF rather than against a particular distribution,
    /// because it is needed in three different shapes: scoring the monthly Beta fits, scoring the
    /// monthly Weibull fits, and - inside
    /// <see cref="Weibull.FitByMaximumLikelihood(IEnumerable{double})"/> - as the objective the
    /// location parameter is searched over.</para>
    /// </summary>
    public static class GoodnessOfFit
    {
        /// <summary>
        /// Kolmogorov-Smirnov distance: the largest absolute gap between the empirical CDF of
        /// <paramref name="values"/> and <paramref name="cdf"/>.
        ///
        /// <para>Checked on both sides of each step, since the empirical CDF jumps at every
        /// observation and the larger gap can be on either side of the jump.</para>
        ///
        /// <para>Compare against <see cref="CriticalValueFivePercent"/>. Two cautions about
        /// reading the result: the critical value falls as sqrt(n), so a large enough sample of
        /// <i>quantised</i> data fails against any continuous model whatever - the hourly wind
        /// speeds are quantised to 0.1 m/s and fail 12 of 12 months at n ≈ 12,600, while the same
        /// data as daily means passes 12 of 12 at n ≈ 500. And a KS distance is a diagnostic, not
        /// a fitted quantity: nothing downstream should ever depend on its value.</para>
        /// </summary>
        /// <param name="values">The sample. Need not be sorted.</param>
        /// <param name="cdf">Cumulative distribution function of the fitted model.</param>
        public static double KolmogorovSmirnovDistance(
            IEnumerable<double> values,
            Func<double, double> cdf
        )
        {
            if (values is null)
                throw new ArgumentNullException(nameof(values));
            if (cdf is null)
                throw new ArgumentNullException(nameof(cdf));

            var sorted = values.OrderBy(v => v).ToList();
            double worst = 0.0;

            for (int i = 0; i < sorted.Count; i++)
            {
                double fitted = cdf(sorted[i]);
                worst = Math.Max(worst, Math.Abs((i + 1.0) / sorted.Count - fitted));
                worst = Math.Max(worst, Math.Abs(fitted - (double)i / sorted.Count));
            }

            return worst;
        }

        /// <summary>
        /// Kolmogorov's 5% critical value for a sample of <paramref name="sampleCount"/>.
        ///
        /// <para>It scales with n, so the comparison means the same thing at any sample size -
        /// which is what makes a per-month KS table readable when the months hold different
        /// numbers of usable days.</para>
        /// </summary>
        public static double CriticalValueFivePercent(int sampleCount) =>
            1.36 / Math.Sqrt(sampleCount);
    }
}

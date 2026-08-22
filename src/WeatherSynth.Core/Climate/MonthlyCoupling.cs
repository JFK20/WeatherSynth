using System;
using System.Collections.Generic;

namespace WeatherSynth.Climate
{
    /// <summary>
    /// How strongly two daily quantities move together, one coefficient per calendar month.
    ///
    /// <para>The third learned object in this library, alongside <see cref="ClearSkyIndexModel"/>
    /// and <see cref="WindSpeedModel"/> - and much the smallest: twelve numbers. Where those two
    /// each describe one quantity on its own, this describes nothing about either and only the
    /// relationship between them, which is exactly why it is a separate type. Neither model has to
    /// learn that the other exists.</para>
    ///
    /// <para><b>Measured on normal scores, not raw values.</b> Each day goes through its own
    /// month's fitted CDF and the inverse normal before the correlation is taken - see
    /// <see cref="SeriesStatistics.LatentCrossCorrelation"/>. That is the space
    /// <see cref="CoupledLatentAr1Chain"/> drives in, so what is fitted here is directly what is
    /// consumed, with no simulation loop between them.</para>
    ///
    /// <para><b>Monthly rather than pooled, and that is not a refinement.</b> At the stations this
    /// was built on the seasonal spread in rho is larger than its mean: roughly -0.12 in January
    /// and -0.20 in July against a pooled -0.22 on the model-free proxy. A single pooled
    /// coefficient would be wrong in most months in both directions.</para>
    ///
    /// <para><b>Sign convention.</b> Positive means the two quantities rise together. For solar
    /// against wind it is negative - windy days are cloudy days - and a positive fitted value is
    /// the signature of a sign error somewhere upstream, not of an unusual site.</para>
    /// </summary>
    public sealed class MonthlyCoupling
    {
        /// <summary>Fewer pairs than this in a month and the pooled coefficient is used instead.</summary>
        /// <remarks>
        /// Higher than the 30 the two marginal models use, and deliberately. A correlation is a
        /// noisier statistic than a fitted shape: at n = 30 the standard error is about 0.18, which
        /// is comparable to the whole signal being estimated here. A month that thin contributes
        /// noise dressed as seasonality, and the pooled value is the better answer.
        /// </remarks>
        public const int MinimumSamplesPerMonth = 60;

        private readonly double[] _monthly;
        private readonly int[] _counts;

        private MonthlyCoupling(double[] monthly, int[] counts, double pooled)
        {
            _monthly = monthly;
            _counts = counts;
            Pooled = pooled;
        }

        /// <summary>
        /// No coupling at all: twelve zeros.
        ///
        /// <para>Present so that a coupled chain can be constructed in the uncoupled state, which
        /// is what the tests compare against. It is <b>not</b> the way to ask for independent
        /// output in production - the two independent providers already are that, and they are
        /// cheaper and byte-for-byte unchanged.</para>
        /// </summary>
        public static MonthlyCoupling None { get; } =
            new MonthlyCoupling(new double[12], new int[12], 0.0);

        /// <summary>The coefficient fitted over every pair in the record, ignoring season.</summary>
        /// <remarks>
        /// The fallback for months too thin to fit on their own, and a headline figure worth
        /// printing - but not a model of the site. See the class remarks on seasonal spread.
        /// </remarks>
        public double Pooled { get; }

        /// <summary>The coefficient for one calendar month, 1-12, in [-1, 1].</summary>
        public double ForMonth(int month) => _monthly[Checked(month) - 1];

        /// <summary>
        /// How many paired days went into <see cref="ForMonth"/> for this month.
        ///
        /// <para>Exposed because a correlation without its sample size cannot be judged: the rough
        /// significance threshold is 2/sqrt(n), and at these magnitudes that is the difference
        /// between a measurement and a coincidence.</para>
        /// </summary>
        public int SampleCount(int month) => _counts[Checked(month) - 1];

        /// <summary>
        /// Whether this month was fitted on its own or fell back to <see cref="Pooled"/>.
        /// </summary>
        public bool IsPooled(int month) => SampleCount(month) < MinimumSamplesPerMonth;

        /// <summary>
        /// Fits the coupling from days on which both quantities were observed.
        /// </summary>
        /// <param name="paired">
        /// Paired daily observations. Both marginal models must already be fitted, because the
        /// scores this runs on are taken through their CDFs - the same ordering constraint the
        /// persistence fit works under.
        /// </param>
        /// <param name="marginalsA">Fitted monthly distributions for the first quantity.</param>
        /// <param name="marginalsB">Fitted monthly distributions for the second quantity.</param>
        public static MonthlyCoupling Fit(
            IEnumerable<(DateOnly Date, double A, double B)> paired,
            IMonthlyMarginals marginalsA,
            IMonthlyMarginals marginalsB
        )
        {
            if (paired is null)
                throw new ArgumentNullException(nameof(paired));
            if (marginalsA is null)
                throw new ArgumentNullException(nameof(marginalsA));
            if (marginalsB is null)
                throw new ArgumentNullException(nameof(marginalsB));

            Func<double, int, double> cdfA = marginalsA.CumulativeProbability;
            Func<double, int, double> cdfB = marginalsB.CumulativeProbability;

            // Materialised because the caller's sequence may be lazy and it is walked thirteen
            // times below - once pooled, once per month.
            var all = new List<(DateOnly Date, double A, double B)>();
            var byMonth = new List<(DateOnly Date, double A, double B)>[12];
            for (int i = 0; i < 12; i++)
                byMonth[i] = new List<(DateOnly, double, double)>();

            foreach (var pair in paired)
            {
                if (double.IsNaN(pair.A) || double.IsNaN(pair.B))
                    continue;

                all.Add(pair);
                byMonth[pair.Date.Month - 1].Add(pair);
            }

            if (all.Count == 0)
                throw new ArgumentException("No usable pairs in the series.", nameof(paired));

            double pooled = Sanitised(SeriesStatistics.LatentCrossCorrelation(all, cdfA, cdfB));

            var monthly = new double[12];
            var counts = new int[12];

            for (int i = 0; i < 12; i++)
            {
                counts[i] = byMonth[i].Count;
                monthly[i] =
                    counts[i] >= MinimumSamplesPerMonth
                        ? Sanitised(SeriesStatistics.LatentCrossCorrelation(byMonth[i], cdfA, cdfB))
                        : pooled;
            }

            return new MonthlyCoupling(monthly, counts, pooled);
        }

        /// <summary>
        /// Twelve coefficients stated directly, for tests and for callers transplanting a coupling
        /// measured elsewhere.
        /// </summary>
        /// <param name="byMonth">Twelve values in January-to-December order, each in [-1, 1].</param>
        public static MonthlyCoupling FromValues(IReadOnlyList<double> byMonth)
        {
            if (byMonth is null)
                throw new ArgumentNullException(nameof(byMonth));
            if (byMonth.Count != 12)
                throw new ArgumentException(
                    $"Expected twelve monthly coefficients, got {byMonth.Count}.",
                    nameof(byMonth)
                );

            var monthly = new double[12];
            double sum = 0.0;

            for (int i = 0; i < 12; i++)
            {
                double rho = byMonth[i];
                if (double.IsNaN(rho) || rho < -1.0 || rho > 1.0)
                    throw new ArgumentOutOfRangeException(
                        nameof(byMonth),
                        rho,
                        $"Coefficient for month {i + 1} must be a correlation in [-1, 1]."
                    );

                monthly[i] = rho;
                sum += rho;
            }

            // No pairs behind these, so every month reports zero samples. The pooled value is the
            // mean of the twelve rather than a refit, which is all it can be.
            return new MonthlyCoupling(monthly, new int[12], sum / 12.0);
        }

        /// <summary>
        /// A correlation that cannot be estimated is no coupling, not a NaN: a NaN here would
        /// propagate into every generated day downstream and take both quantities with it.
        /// </summary>
        private static double Sanitised(double rho) =>
            double.IsNaN(rho) ? 0.0 : Math.Clamp(rho, -1.0, 1.0);

        private static int Checked(int month) =>
            month >= 1 && month <= 12
                ? month
                : throw new ArgumentOutOfRangeException(
                    nameof(month),
                    month,
                    "Month must be 1-12."
                );
    }
}

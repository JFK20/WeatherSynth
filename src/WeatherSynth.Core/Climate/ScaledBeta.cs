using System;
using System.Collections.Generic;

namespace WeatherSynth.Climate
{
    /// <summary>
    /// A Beta distribution stretched from its natural [0, 1] support onto [0, <see cref="Scale"/>].
    ///
    /// <para>Beta is the right family for the clear-sky index because it is bounded on both ends
    /// and can be skewed either way with two parameters: January piles up near zero, July piles
    /// up near the ceiling, and one family covers both. A normal distribution cannot, and would
    /// generate negative irradiance.</para>
    ///
    /// <para>The scaling is not cosmetic. The clear-sky index is <b>not</b> bounded by 1.0: the
    /// ceiling carries a monthly-mean turbidity, so a day cleaner than its month's average beats
    /// it, and 5.7% of the Bochum record does. Fitting on [0, 1] would force those days to the
    /// boundary and distort the whole clear end of the distribution.</para>
    /// </summary>
    public sealed class ScaledBeta
    {
        // log B(alpha, beta) is a constant of the distribution and both Density and
        // CumulativeProbability need it. Quantile calls those two ~7 times each per solve, so
        // recomputing it there would be three LogGamma evaluations per iteration for a number
        // that cannot change.
        private readonly double _logBeta;

        /// <summary>Shape parameter towards the low (overcast) end.</summary>
        public double Alpha { get; }

        /// <summary>Shape parameter towards the high (clear) end.</summary>
        public double Beta { get; }

        /// <summary>Upper end of the support. The lower end is always zero.</summary>
        public double Scale { get; }

        /// <summary>Number of observations the fit was made from. Zero if constructed directly.</summary>
        public int SampleCount { get; }

        /// <param name="alpha">Shape towards the low end. Must be positive.</param>
        /// <param name="beta">Shape towards the high end. Must be positive.</param>
        /// <param name="scale">Upper end of the support. Must be positive.</param>
        /// <param name="sampleCount">Observations behind the fit, for reporting confidence.</param>
        public ScaledBeta(double alpha, double beta, double scale, int sampleCount = 0)
        {
            if (!(alpha > 0.0))
                throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "Must be positive.");
            if (!(beta > 0.0))
                throw new ArgumentOutOfRangeException(nameof(beta), beta, "Must be positive.");
            if (!(scale > 0.0))
                throw new ArgumentOutOfRangeException(nameof(scale), scale, "Must be positive.");

            Alpha = alpha;
            Beta = beta;
            Scale = scale;
            SampleCount = sampleCount;
            _logBeta = SpecialFunctions.LogBeta(alpha, beta);
        }

        /// <summary>Distribution mean, in the scaled units (i.e. clear-sky index, not [0,1]).</summary>
        public double Mean => Scale * Alpha / (Alpha + Beta);

        /// <summary>Distribution variance, in the scaled units.</summary>
        public double Variance
        {
            get
            {
                double sum = Alpha + Beta;
                return Scale * Scale * Alpha * Beta / (sum * sum * (sum + 1.0));
            }
        }

        /// <summary>Distribution standard deviation, in the scaled units.</summary>
        public double StandardDeviation => Math.Sqrt(Variance);

        /// <summary>
        /// Fits by the <b>method of moments</b>: solve for the (alpha, beta) whose mean and
        /// variance match the sample's.
        ///
        /// <para>Maximum likelihood would be marginally tighter, but it needs an iterative
        /// digamma solve and the difference is far below the uncertainty already carried by a
        /// monthly-mean turbidity in the denominator. Moments are closed-form, deterministic and
        /// cannot fail to converge.</para>
        /// </summary>
        /// <param name="values">Observations, all within [0, <paramref name="scale"/>].</param>
        /// <param name="scale">Upper end of the support.</param>
        /// <exception cref="ArgumentException">
        /// If there are fewer than two values, if any lies outside the support, or if the sample
        /// is too dispersed for any Beta to match it (variance at or above <c>m(1-m)</c> in unit
        /// space, which is the U-shaped limit of the family).
        /// </exception>
        public static ScaledBeta FitByMoments(IEnumerable<double> values, double scale)
        {
            if (values is null)
                throw new ArgumentNullException(nameof(values));
            if (!(scale > 0.0))
                throw new ArgumentOutOfRangeException(nameof(scale), scale, "Must be positive.");

            // Welford, so a long series does not lose precision to a running sum of squares.
            int n = 0;
            double mean = 0.0;
            double m2 = 0.0;

            foreach (double value in values)
            {
                if (double.IsNaN(value))
                    throw new ArgumentException("Sample contains NaN.", nameof(values));
                if (value < 0.0 || value > scale)
                    throw new ArgumentException(
                        $"Value {value:F4} lies outside the support [0, {scale:F4}]. Widen the scale: "
                            + "a Beta cannot represent observations beyond its own bounds.",
                        nameof(values)
                    );

                n++;
                double delta = value - mean;
                mean += delta / n;
                m2 += delta * (value - mean);
            }

            if (n < 2)
                throw new ArgumentException(
                    "Need at least two observations to fit.",
                    nameof(values)
                );

            double variance = m2 / (n - 1);

            // Work in unit space; the scale comes back at the end.
            double m = mean / scale;
            double v = variance / (scale * scale);

            if (v <= 0.0)
                throw new ArgumentException(
                    "Sample has no variance; a Beta cannot be fitted.",
                    nameof(values)
                );

            double maxVariance = m * (1.0 - m);
            if (v >= maxVariance)
                throw new ArgumentException(
                    $"Sample is too dispersed for any Beta: variance {v:F5} is at or above the "
                        + $"family's limit {maxVariance:F5} for mean {m:F4}.",
                    nameof(values)
                );

            double common = maxVariance / v - 1.0;
            return new ScaledBeta(m * common, (1.0 - m) * common, scale, n);
        }

        /// <summary>
        /// Probability density at <paramref name="x"/>, in the scaled units, so that it
        /// integrates to 1 over [0, <see cref="Scale"/>]. Zero outside the support.
        /// </summary>
        public double Density(double x)
        {
            if (x < 0.0 || x > Scale)
                return 0.0;

            double u = x / Scale;

            // The endpoints are only finite when the corresponding shape is >= 1; otherwise the
            // density genuinely diverges and there is no useful number to return.
            if (u <= 0.0)
                return Alpha < 1.0 ? double.PositiveInfinity
                    : Alpha > 1.0 ? 0.0
                    : Beta / Scale;
            if (u >= 1.0)
                return Beta < 1.0 ? double.PositiveInfinity
                    : Beta > 1.0 ? 0.0
                    : Alpha / Scale;

            double logDensity =
                (Alpha - 1.0) * Math.Log(u) + (Beta - 1.0) * Math.Log(1.0 - u) - _logBeta;

            return Math.Exp(logDensity) / Scale;
        }

        /// <summary>
        /// Probability that a draw falls at or below <paramref name="x"/>, in the scaled units.
        ///
        /// <para>This is the regularised incomplete beta function. Its main use here is scoring
        /// the fit: comparing this against the empirical CDF of the measured record gives a
        /// Kolmogorov-Smirnov distance, which is the difference between "the histogram looks
        /// about right" and a number.</para>
        /// </summary>
        public double CumulativeProbability(double x)
        {
            double u = x / Scale;
            if (u <= 0.0)
                return 0.0;
            if (u >= 1.0)
                return 1.0;

            double front = Math.Exp(Alpha * Math.Log(u) + Beta * Math.Log(1.0 - u) - _logBeta);

            // The continued fraction converges quickly only on one side of the mode; the
            // symmetry relation I(u; a, b) = 1 - I(1-u; b, a) covers the other.
            return u < (Alpha + 1.0) / (Alpha + Beta + 2.0)
                ? front * IncompleteBetaFraction(u, Alpha, Beta) / Alpha
                : 1.0 - front * IncompleteBetaFraction(1.0 - u, Beta, Alpha) / Beta;
        }

        /// <summary>
        /// Lentz's algorithm for the continued fraction behind the incomplete beta function.
        /// </summary>
        private static double IncompleteBetaFraction(double x, double a, double b)
        {
            const double tiny = 1e-30;
            const double epsilon = 1e-14;

            double c = 1.0;
            double d = 1.0 - (a + b) * x / (a + 1.0);
            if (Math.Abs(d) < tiny)
                d = tiny;
            d = 1.0 / d;
            double result = d;

            // One iteration advances the fraction by two terms, whose numerators differ.
            static void Advance(double numerator, ref double c, ref double d, ref double result)
            {
                d = 1.0 + numerator * d;
                if (Math.Abs(d) < tiny)
                    d = tiny;
                d = 1.0 / d;

                c = 1.0 + numerator / c;
                if (Math.Abs(c) < tiny)
                    c = tiny;

                result *= c * d;
            }

            for (int m = 1; m <= 300; m++)
            {
                Advance(
                    m * (b - m) * x / ((a + 2.0 * m - 1.0) * (a + 2.0 * m)),
                    ref c,
                    ref d,
                    ref result
                );
                Advance(
                    -(a + m) * (a + b + m) * x / ((a + 2.0 * m) * (a + 2.0 * m + 1.0)),
                    ref c,
                    ref d,
                    ref result
                );

                if (Math.Abs(1.0 - c * d) < epsilon)
                    break;
            }

            return result;
        }

        /// <summary>
        /// The inverse of <see cref="CumulativeProbability"/>: the index value this distribution
        /// falls below with probability <paramref name="p"/>.
        ///
        /// <para>This is what turns a uniform into a draw with this exact marginal, which is how
        /// <see cref="LatentAr1Chain"/> injects day-to-day persistence without disturbing the
        /// fitted shape. <see cref="Sample"/> remains the right call for an independent draw -
        /// it is exact where this is iterative.</para>
        ///
        /// <para>Newton on the CDF, with the bracket kept on both sides and a bisection step
        /// taken whenever Newton would leave it. The safeguard is not decoration: the overcast
        /// winter months fit alpha below 1, where <see cref="Density"/> diverges at zero and an
        /// unguarded Newton step walks straight off the support.</para>
        /// </summary>
        public double Quantile(double p)
        {
            if (double.IsNaN(p) || p < 0.0 || p > 1.0)
                throw new ArgumentOutOfRangeException(
                    nameof(p),
                    p,
                    "Probability must be in [0, 1]."
                );

            if (p <= 0.0)
                return 0.0;
            if (p >= 1.0)
                return Scale;

            double lower = 0.0;
            double upper = Scale;
            double x = Mean;

            for (int i = 0; i < 200; i++)
            {
                double error = CumulativeProbability(x) - p;

                // Every evaluation tightens the bracket, so the bisection fallback below always
                // has somewhere smaller to go even when Newton is useless.
                if (error > 0.0)
                    upper = x;
                else
                    lower = x;

                if (Math.Abs(error) < 1e-15 || upper - lower < 1e-16 * Scale)
                    break;

                double density = Density(x);
                double next =
                    density > 0.0 && !double.IsInfinity(density) ? x - error / density : double.NaN;

                if (!(next > lower && next < upper))
                    next = 0.5 * (lower + upper);

                x = next;
            }

            return x;
        }

        /// <summary>
        /// Draws one value from the distribution, in the scaled units.
        ///
        /// <para>Uses the ratio of two Gamma draws, which is exact rather than an inverse-CDF
        /// approximation, and stays well behaved for the small shape parameters that the
        /// overcast winter months produce.</para>
        /// </summary>
        public double Sample(Random random)
        {
            if (random is null)
                throw new ArgumentNullException(nameof(random));

            double a = SampleStandardGamma(random, Alpha);
            double b = SampleStandardGamma(random, Beta);

            // Both underflowing to zero is astronomically unlikely but would be a silent NaN.
            double sum = a + b;
            return sum > 0.0 ? Scale * a / sum : Mean;
        }

        /// <summary>
        /// Marsaglia-Tsang (2000) squeeze method for Gamma(shape, 1).
        ///
        /// <para>Shapes below 1 are handled by drawing at shape+1 and applying Stuart's boost,
        /// because the squeeze is only valid for shape >= 1.</para>
        /// </summary>
        private static double SampleStandardGamma(Random random, double shape)
        {
            if (shape < 1.0)
            {
                double boosted = SampleStandardGamma(random, shape + 1.0);
                double u = random.NextDouble();
                return u <= 0.0 ? 0.0 : boosted * Math.Pow(u, 1.0 / shape);
            }

            double d = shape - 1.0 / 3.0;
            double c = 1.0 / Math.Sqrt(9.0 * d);

            while (true)
            {
                double x,
                    v;
                do
                {
                    x = Gaussian.Sample(random);
                    v = 1.0 + c * x;
                } while (v <= 0.0);

                v = v * v * v;
                double u = random.NextDouble();
                double xSquared = x * x;

                // Cheap squeeze first; the logarithmic test only runs on the few that fail it.
                if (u < 1.0 - 0.0331 * xSquared * xSquared)
                    return d * v;

                if (u > 0.0 && Math.Log(u) < 0.5 * xSquared + d * (1.0 - v + Math.Log(v)))
                    return d * v;
            }
        }

    }
}

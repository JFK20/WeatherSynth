using System;

namespace WeatherSynth.Climate
{
    /// <summary>
    /// The standard normal distribution: CDF, quantile, and a draw.
    ///
    /// <para>Present only because the persistence layer needs it. The clear-sky index itself is
    /// never modelled as normal - it is bounded on both ends and skewed, which is why
    /// <see cref="ScaledBeta"/> exists. What the normal is used for here is the <i>latent</i>
    /// variable of the AR(1) chain in <see cref="LatentAr1Chain"/>: an unbounded space where
    /// "yesterday times phi plus noise" is a sensible thing to write, from which
    /// <see cref="Cdf"/> maps back onto a uniform and thence onto the fitted Beta.</para>
    ///
    /// <para>.NET has no erf, and Core carries no numerics package, so both directions are
    /// implemented here. Between them they round-trip to about 1e-15.</para>
    /// </summary>
    public static class Gaussian
    {
        // Acklam's coefficients for Quantile. Static, because they are constants and Quantile runs
        // once per record day inside the persistence fit - as locals they would be four heap
        // allocations on every call.
        private static readonly double[] CentralNumerator =
        {
            -3.969683028665376e+01,
            2.209460984245205e+02,
            -2.759285104469687e+02,
            1.383577518672690e+02,
            -3.066479806614716e+01,
            2.506628277459239e+00,
        };

        private static readonly double[] CentralDenominator =
        {
            -5.447609879822406e+01,
            1.615858368580409e+02,
            -1.556989798598866e+02,
            6.680131188771972e+01,
            -1.328068155288572e+01,
        };

        private static readonly double[] TailNumerator =
        {
            -7.784894002430293e-03,
            -3.223964580411365e-01,
            -2.400758277161838e+00,
            -2.549732539343734e+00,
            4.374664141464968e+00,
            2.938163982698783e+00,
        };

        private static readonly double[] TailDenominator =
        {
            7.784695709041462e-03,
            3.224671290700398e-01,
            2.445134137142996e+00,
            3.754408661907416e+00,
        };

        /// <summary>
        /// Probability that a standard normal draw falls at or below <paramref name="z"/>.
        ///
        /// <para>Hart's rational approximation in the form given by West (2005), accurate to
        /// roughly double precision across the whole range. The split at |z| = 7.07 is where the
        /// upper branch stops being able to hold its accuracy; beyond it the tail is computed
        /// from a short continued fraction instead.</para>
        /// </summary>
        public static double Cdf(double z)
        {
            if (double.IsNaN(z))
                return double.NaN;

            double absZ = Math.Abs(z);

            // Beyond this the density itself underflows and the answer is 0 or 1 to the last bit.
            if (absZ > 37.0)
                return z > 0.0 ? 1.0 : 0.0;

            double exponential = Math.Exp(-0.5 * absZ * absZ);
            double tail;

            if (absZ < 7.07106781186547)
            {
                double numerator = 3.52624965998911e-02 * absZ + 0.700383064443688;
                numerator = numerator * absZ + 6.37396220353165;
                numerator = numerator * absZ + 33.912866078383;
                numerator = numerator * absZ + 112.079291497871;
                numerator = numerator * absZ + 221.213596169931;
                numerator = numerator * absZ + 220.206867912376;

                double denominator = 8.83883476483184e-02 * absZ + 1.75566716318264;
                denominator = denominator * absZ + 16.064177579207;
                denominator = denominator * absZ + 86.7807322029461;
                denominator = denominator * absZ + 296.564248779674;
                denominator = denominator * absZ + 637.333633378831;
                denominator = denominator * absZ + 793.826512519948;
                denominator = denominator * absZ + 440.413735824752;

                tail = exponential * numerator / denominator;
            }
            else
            {
                // Continued fraction for the far tail, where the rational form above loses digits.
                double fraction =
                    absZ + 1.0 / (absZ + 2.0 / (absZ + 3.0 / (absZ + 4.0 / (absZ + 0.65))));
                tail = exponential / (fraction * 2.506628274631);
            }

            return z > 0.0 ? 1.0 - tail : tail;
        }

        /// <summary>
        /// The inverse of <see cref="Cdf"/>: the value a standard normal falls below with
        /// probability <paramref name="p"/>.
        ///
        /// <para>Acklam's rational approximation, which is good to about 1e-9, followed by one
        /// Halley step against <see cref="Cdf"/> that takes it to full double precision. The
        /// refinement is cheap and worth it: this sits inside the fit, where a systematic 1e-9
        /// bias in every normal score would be indistinguishable from a real signal.</para>
        /// </summary>
        /// <returns>Negative infinity at 0 and positive infinity at 1; NaN outside [0, 1].</returns>
        public static double Quantile(double p)
        {
            if (double.IsNaN(p) || p < 0.0 || p > 1.0)
                return double.NaN;
            if (p <= 0.0)
                return double.NegativeInfinity;
            if (p >= 1.0)
                return double.PositiveInfinity;

            double[] a = CentralNumerator;
            double[] b = CentralDenominator;
            double[] c = TailNumerator;
            double[] d = TailDenominator;

            const double low = 0.02425;
            const double high = 1.0 - low;

            double x;
            if (p < low)
            {
                double q = Math.Sqrt(-2.0 * Math.Log(p));
                x =
                    (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                    / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
            }
            else if (p <= high)
            {
                double q = p - 0.5;
                double r = q * q;
                x =
                    (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5])
                    * q
                    / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1.0);
            }
            else
            {
                double q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
                x =
                    -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                    / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
            }

            // Halley refinement. The density is the derivative of the CDF, so this needs no
            // extra machinery; one step squares the ~1e-9 error into the noise.
            double error = Cdf(x) - p;
            double density = Math.Exp(-0.5 * x * x) / 2.506628274631000502;
            if (density > 0.0)
            {
                double u = error / density;
                x -= u / (1.0 + 0.5 * x * u);
            }

            return x;
        }

        /// <summary>
        /// One draw from the standard normal.
        ///
        /// <para>Box-Muller rather than the inverse of <see cref="Quantile"/>, which would be both
        /// slower and less accurate in the tails.</para>
        /// </summary>
        public static double Sample(Random random)
        {
            if (random is null)
                throw new ArgumentNullException(nameof(random));

            // NextDouble() can return exactly 0, which Log would send to -infinity.
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}

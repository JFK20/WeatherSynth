using System;

namespace WeatherSynth.Climate
{
    /// <summary>
    /// Numerical special functions shared by the distributions in this namespace.
    ///
    /// <para>Extracted from <see cref="ScaledBeta"/>, which owned <see cref="LogGamma"/> privately
    /// until <see cref="Weibull"/> needed the same function for its moment identities - mean and
    /// variance both go through Γ(1 + 1/k). Duplicating a Lanczos table is exactly the kind of
    /// thing that drifts between copies.</para>
    ///
    /// <para>Internal: this is machinery, not part of what the library is for. Callers wanting a
    /// distribution want <see cref="ScaledBeta"/> or <see cref="Weibull"/>.</para>
    /// </summary>
    internal static class SpecialFunctions
    {
        private static readonly double[] LanczosCoefficients =
        {
            0.99999999999980993,
            676.5203681218851,
            -1259.1392167224028,
            771.32342877765313,
            -176.61502916214059,
            12.507343278686905,
            -0.13857109526572012,
            9.9843695780195716e-6,
            1.5056327351493116e-7,
        };

        /// <summary>Lanczos approximation, g = 7, n = 9. Accurate to ~15 significant digits.</summary>
        public static double LogGamma(double x)
        {
            double[] coefficients = LanczosCoefficients;

            if (x < 0.5)
            {
                // Reflection, so the series is only ever evaluated where it converges well.
                return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1.0 - x);
            }

            x -= 1.0;
            double series = coefficients[0];
            for (int i = 1; i < coefficients.Length; i++)
                series += coefficients[i] / (x + i);

            double t = x + 7.5;
            return 0.5 * Math.Log(2.0 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(series);
        }

        /// <summary>
        /// The gamma function itself, for the places where the result is used directly rather
        /// than as a log - the Weibull moments multiply Γ terms together and are small enough to
        /// exponentiate safely.
        /// </summary>
        public static double Gamma(double x) => Math.Exp(LogGamma(x));

        /// <summary>log B(a, b), the log of the beta function.</summary>
        public static double LogBeta(double a, double b) =>
            LogGamma(a) + LogGamma(b) - LogGamma(a + b);

        /// <summary>
        /// <c>e^x − 1</c>, computed without the cancellation that <c>Math.Exp(x) - 1.0</c> suffers
        /// near zero. .NET has no <c>expm1</c>.
        ///
        /// <para>Kahan's identity: <c>e^x − 1 = (u − 1)·x / ln u</c> with <c>u = e^x</c>. The
        /// rounding errors in <c>u − 1</c> and <c>ln u</c> are the same error and divide out.
        /// Wanted by the Weibull CDF, which is <c>1 − e^(−t)</c> and would otherwise return a flat
        /// zero across the whole lower tail.</para>
        /// </summary>
        public static double ExpMinusOne(double x)
        {
            double u = Math.Exp(x);

            if (u == 1.0)
                return x;
            if (u - 1.0 == -1.0)
                return -1.0;

            return (u - 1.0) * x / Math.Log(u);
        }
    }
}

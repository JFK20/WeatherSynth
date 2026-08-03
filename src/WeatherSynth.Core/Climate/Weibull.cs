using System;
using System.Collections.Generic;
using System.Linq;

namespace WeatherSynth.Climate
{
    /// <summary>
    /// A three-parameter Weibull distribution on [<see cref="Location"/>, ∞), the marginal the
    /// wind model is built on.
    ///
    /// <para><b>Three parameters, not the textbook two.</b> The two-parameter Weibull is the
    /// standard wind distribution and it fits this station's daily means badly: 3 of 12 months
    /// pass a 5% KS test. Adding the location parameter γ takes that to 12 of 12. This is not
    /// curve-fitting for its own sake - the daily mean at a sheltered inland site essentially
    /// never falls below ~1 m/s (one day in 6,186 sits below 1.0 m/s over 17 years), and a
    /// two-parameter Weibull is obliged to put density all the way down to zero. It pays for that
    /// misplaced mass by inflating k to 2.6-3.4, which distorts the whole shape including the
    /// tail that carries the energy. Freeing γ pulls k back to 1.7-2.2, the canonical range for
    /// wind, and roughly halves the KS distances.</para>
    ///
    /// <para><b>The parameters belong to a resolution and a height.</b> k measured on daily means
    /// is not k measured on hourly values - 2.71 against 2.14 at the same station, because
    /// averaging narrows the distribution - and A and γ are in m/s at whatever height the
    /// anemometer sat. Published site values are almost always hourly or 10-minute, so comparing
    /// a daily-fitted k against one is a silent, large error.</para>
    ///
    /// <para><b><see cref="Scale"/> is not the mean.</b> The mean is
    /// <c>γ + A·Γ(1 + 1/k)</c>, about <c>γ + 0.886·A</c> at k = 2. Conflating the scale parameter
    /// with mean wind speed is the most common error in this literature.</para>
    /// </summary>
    public sealed class Weibull
    {
        /// <summary>Shape parameter, k. Around 1.7-2.2 for daily mean wind speeds.</summary>
        public double Shape { get; }

        /// <summary>
        /// Scale parameter, A, in the same units as the data. <b>Not the mean</b> - see the class
        /// remarks and <see cref="Mean"/>.
        /// </summary>
        public double Scale { get; }

        /// <summary>
        /// Location parameter, γ: the lower end of the support, in the same units as the data.
        ///
        /// <para>Physically, the speed below which this site's daily mean simply does not go. Zero
        /// reduces the distribution to the textbook two-parameter Weibull.</para>
        /// </summary>
        public double Location { get; }

        /// <summary>Number of observations the fit was made from. Zero if constructed directly.</summary>
        public int SampleCount { get; }

        /// <param name="shape">Shape k. Must be positive.</param>
        /// <param name="scale">Scale A. Must be positive.</param>
        /// <param name="location">Location γ, the lower end of the support. May be zero.</param>
        /// <param name="sampleCount">Observations behind the fit, for reporting confidence.</param>
        public Weibull(double shape, double scale, double location = 0.0, int sampleCount = 0)
        {
            if (!(shape > 0.0))
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Must be positive.");
            if (!(scale > 0.0))
                throw new ArgumentOutOfRangeException(nameof(scale), scale, "Must be positive.");
            if (double.IsNaN(location) || double.IsInfinity(location))
                throw new ArgumentOutOfRangeException(
                    nameof(location),
                    location,
                    "Must be finite."
                );

            Shape = shape;
            Scale = scale;
            Location = location;
            SampleCount = sampleCount;
        }

        /// <summary>Distribution mean: <c>γ + A·Γ(1 + 1/k)</c>.</summary>
        public double Mean => Location + Scale * SpecialFunctions.Gamma(1.0 + 1.0 / Shape);

        /// <summary>
        /// Distribution variance: <c>A²·[Γ(1 + 2/k) − Γ(1 + 1/k)²]</c>. Independent of γ, which
        /// only shifts the distribution.
        /// </summary>
        public double Variance
        {
            get
            {
                double first = SpecialFunctions.Gamma(1.0 + 1.0 / Shape);
                double second = SpecialFunctions.Gamma(1.0 + 2.0 / Shape);
                return Scale * Scale * (second - first * first);
            }
        }

        /// <summary>Distribution standard deviation.</summary>
        public double StandardDeviation => Math.Sqrt(Variance);

        /// <summary>Probability density at <paramref name="x"/>. Zero at or below γ.</summary>
        public double Density(double x)
        {
            double z = (x - Location) / Scale;
            if (z <= 0.0)
                return 0.0;

            return (Shape / Scale) * Math.Pow(z, Shape - 1.0) * Math.Exp(-Math.Pow(z, Shape));
        }

        /// <summary>
        /// Probability that a draw falls at or below <paramref name="x"/>:
        /// <c>1 − exp(−((x−γ)/A)^k)</c>.
        /// </summary>
        public double CumulativeProbability(double x)
        {
            double z = (x - Location) / Scale;
            if (z <= 0.0)
                return 0.0;

            return -SpecialFunctions.ExpMinusOne(-Math.Pow(z, Shape));
        }

        /// <summary>
        /// The inverse of <see cref="CumulativeProbability"/>: <c>γ + A·(−ln(1−p))^(1/k)</c>.
        ///
        /// <para><b>Closed form</b>, unlike <see cref="ScaledBeta.Quantile"/>, which has to run a
        /// bracketed Newton solve because the incomplete beta cannot be inverted analytically.
        /// Since the persistence chain calls a quantile once per generated day, that is the whole
        /// reason a synthetic wind year costs so much less than a solar one - there is no ceiling
        /// to integrate either.</para>
        /// </summary>
        public double Quantile(double p)
        {
            if (double.IsNaN(p) || p < 0.0 || p > 1.0)
                throw new ArgumentOutOfRangeException(nameof(p), p, "Probability must be in [0, 1].");

            if (p <= 0.0)
                return Location;
            if (p >= 1.0)
                return double.PositiveInfinity;

            return Location + Scale * Math.Pow(-Math.Log(1.0 - p), 1.0 / Shape);
        }

        /// <summary>
        /// Draws one value from the distribution.
        ///
        /// <para>Inverse-CDF, which here is exact rather than an approximation: one uniform, one
        /// logarithm, one power, and no rejection loop.</para>
        ///
        /// <para><b>The uniform is used as drawn, not reflected.</b> <c>NextDouble</c> spans
        /// [0, 1), so the lower end can come up and <see cref="Quantile"/> maps it to gamma - the
        /// support's infimum, finite and physically ordinary. Reflecting to <c>1 − u</c> to keep
        /// the support open below would trade that for a genuine hazard: any u smaller than the
        /// double epsilon rounds <c>1 − u</c> to exactly 1.0, and the quantile there is infinite.
        /// A one-in-10^16 infinite wind speed is worse than a one-in-10^16 draw sitting on the
        /// distribution's own lower bound.</para>
        /// </summary>
        public double Sample(Random random)
        {
            if (random is null)
                throw new ArgumentNullException(nameof(random));

            return Quantile(random.NextDouble());
        }

        /// <summary>
        /// This distribution with every value multiplied by <paramref name="factor"/>:
        /// <c>Weibull(k, A, γ) → Weibull(k, cA, cγ)</c>.
        ///
        /// <para><b>The property that makes the height transfer clean.</b> Moving a wind speed
        /// from the anemometer's height to a target height is a multiplication by a constant (the
        /// log-profile ratio), and k is invariant under it. So the transfer can be applied once to
        /// the fitted parameters instead of to every draw, and the two are provably identical -
        /// not approximately, exactly, since sampling is a monotone transform of one uniform.</para>
        ///
        /// <para>The transfer factor itself is where essentially all the error in a wind resource
        /// estimate lives; this identity says only that applying it here rather than downstream
        /// costs nothing.</para>
        /// </summary>
        public Weibull Scaled(double factor)
        {
            if (!(factor > 0.0))
                throw new ArgumentOutOfRangeException(nameof(factor), factor, "Must be positive.");

            return new Weibull(Shape, Scale * factor, Location * factor, SampleCount);
        }

        /// <summary>
        /// Fits by <b>maximum likelihood</b>, with the location parameter chosen by a KS-minimising
        /// search around it.
        ///
        /// <para><b>Why MLE here when <see cref="ScaledBeta"/> uses moments.</b> The Beta chose
        /// moments because they are closed-form and cannot fail to converge. That advantage does
        /// not exist for a Weibull: its moments run through Γ(1 + 1/k) and need a one-dimensional
        /// solve either way. With the tie-break gone, MLE wins - for fixed γ it is a
        /// well-conditioned Newton iteration on k with a monotone objective and a closed form for
        /// A given k.</para>
        ///
        /// <para>γ cannot come from the likelihood: for γ → min(v) the likelihood diverges when
        /// k &lt; 1, so a joint MLE is not well posed. It is found instead by a bounded
        /// coarse-to-fine scan over [0, min(v)) minimising the KS distance, which is cheap, cannot
        /// run away, and is smooth in practice.</para>
        /// </summary>
        /// <param name="values">Observations. All must exceed the fitted γ, which the scan ensures.</param>
        /// <exception cref="ArgumentException">If there are fewer than two values, or any is NaN.</exception>
        public static Weibull FitByMaximumLikelihood(IEnumerable<double> values)
        {
            var sorted = Validate(values, nameof(values));

            double minimum = sorted[0];

            // The scan stops short of min(v): at gamma = min(v) the smallest observation sits
            // exactly on the boundary, where the density is either zero or infinite and the
            // log-likelihood is undefined.
            double upperBound = Math.Max(0.0, minimum * (1.0 - 1e-6));

            double bestLocation = 0.0;
            double bestDistance = double.PositiveInfinity;
            Weibull? best = null;

            // Coarse to fine: three passes, each narrowing to one step either side of the winner.
            const int steps = 40;
            double low = 0.0;
            double high = upperBound;

            for (int pass = 0; pass < 3; pass++)
            {
                double width = (high - low) / steps;
                if (width <= 0.0)
                    break;

                for (int i = 0; i <= steps; i++)
                {
                    double location = low + i * width;
                    if (location >= minimum)
                        continue;

                    var candidate = FitAtLocation(sorted, location);
                    double distance = GoodnessOfFit.KolmogorovSmirnovDistance(
                        sorted,
                        candidate.CumulativeProbability
                    );

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestLocation = location;
                        best = candidate;
                    }
                }

                low = Math.Max(0.0, bestLocation - width);
                high = Math.Min(upperBound, bestLocation + width);
            }

            return best ?? FitAtLocation(sorted, 0.0);
        }

        /// <summary>
        /// Fits by the <b>method of moments</b>, holding γ at zero: the two-parameter fit.
        ///
        /// <para>Kept for two reasons. It is the starting point
        /// <see cref="FitByMaximumLikelihood(IEnumerable{double})"/> brackets k from, and it is the fallback when a
        /// sample is too small for the location scan to mean anything. As a model of daily wind
        /// speeds in its own right it is the one this project rejected - see the class remarks.</para>
        /// </summary>
        /// <param name="values">Observations. All must be positive.</param>
        public static Weibull FitByMoments(IEnumerable<double> values)
        {
            var sorted = Validate(values, nameof(values));
            return FitByMoments(sorted, location: 0.0);
        }

        /// <summary>
        /// MLE of (k, A) with γ held fixed.
        ///
        /// <para>The shape solves <c>1/k = Σwᵢ^k·ln wᵢ / Σwᵢ^k − mean(ln w)</c> for the shifted
        /// observations w = v − γ, which is Newton on a scalar; A then follows in closed form as
        /// <c>((1/n)·Σwᵢ^k)^(1/k)</c>.</para>
        /// </summary>
        private static Weibull FitAtLocation(IReadOnlyList<double> sorted, double location)
        {
            int n = sorted.Count;

            var shifted = new double[n];
            var logs = new double[n];
            double meanLog = 0.0;

            for (int i = 0; i < n; i++)
            {
                double w = sorted[i] - location;

                // The caller guarantees location < min(v), but a value can still land close
                // enough that the log underflows; clamping keeps the iteration finite rather
                // than letting one observation take the whole fit to NaN.
                shifted[i] = Math.Max(w, 1e-12);
                logs[i] = Math.Log(shifted[i]);
                meanLog += logs[i];
            }

            meanLog /= n;

            // Moments give a starting point already in the right neighbourhood, which keeps the
            // Newton iteration to a handful of steps.
            double k = FitByMoments(sorted, location).Shape;

            for (int iteration = 0; iteration < 100; iteration++)
            {
                double sumPower = 0.0;
                double sumPowerLog = 0.0;
                double sumPowerLogSquared = 0.0;

                for (int i = 0; i < n; i++)
                {
                    double power = Math.Pow(shifted[i], k);
                    sumPower += power;
                    sumPowerLog += power * logs[i];
                    sumPowerLogSquared += power * logs[i] * logs[i];
                }

                if (sumPower <= 0.0)
                    break;

                double ratio = sumPowerLog / sumPower;
                double objective = ratio - meanLog - 1.0 / k;

                // d/dk of the objective. Positive throughout, which is why the iteration is
                // well behaved: the objective is monotone in k.
                double derivative =
                    sumPowerLogSquared / sumPower - ratio * ratio + 1.0 / (k * k);

                if (!(Math.Abs(derivative) > 0.0))
                    break;

                double next = k - objective / derivative;

                // A Newton step that leaves the positive half-line means the iteration is lost,
                // not that the answer is there. Halving towards the current value recovers.
                if (!(next > 0.0) || double.IsNaN(next))
                    next = k * 0.5;

                bool converged = Math.Abs(next - k) < 1e-12 * Math.Max(1.0, k);
                k = next;

                if (converged)
                    break;
            }

            double sum = 0.0;
            for (int i = 0; i < n; i++)
                sum += Math.Pow(shifted[i], k);

            double scale = Math.Pow(sum / n, 1.0 / k);

            return new Weibull(k, scale, location, n);
        }

        /// <summary>
        /// Moment fit with γ fixed: k from the coefficient of variation of the shifted data,
        /// A from the mean.
        ///
        /// <para>The coefficient of variation depends on k alone, so k comes from a single
        /// monotone one-dimensional solve, and the empirical approximation
        /// <c>k ≈ (sd/mean)^−1.086</c> (Justus et al. 1978) starts it within a few percent.</para>
        /// </summary>
        private static Weibull FitByMoments(IReadOnlyList<double> sorted, double location)
        {
            int n = sorted.Count;

            double mean = 0.0;
            foreach (double value in sorted)
                mean += value - location;
            mean /= n;

            double variance = 0.0;
            foreach (double value in sorted)
            {
                double deviation = (value - location) - mean;
                variance += deviation * deviation;
            }
            variance /= n - 1;

            if (!(mean > 0.0) || !(variance > 0.0))
                throw new ArgumentException(
                    "Sample has no spread above the location parameter; a Weibull cannot be fitted."
                );

            double coefficientOfVariation = Math.Sqrt(variance) / mean;

            // Justus' approximation, then bisection on the exact relation. The bracket is wide
            // enough for anything from a near-exponential regime (k ≈ 1) to a very narrow one.
            double k = Math.Pow(coefficientOfVariation, -1.086);
            double low = Math.Max(0.05, k * 0.25);
            double high = Math.Min(50.0, Math.Max(k * 4.0, 1.0));

            static double CvOf(double shape)
            {
                double first = SpecialFunctions.Gamma(1.0 + 1.0 / shape);
                double second = SpecialFunctions.Gamma(1.0 + 2.0 / shape);
                return Math.Sqrt(second - first * first) / first;
            }

            // CV falls monotonically in k, so widening the bracket is unambiguous.
            while (CvOf(low) < coefficientOfVariation && low > 1e-3)
                low *= 0.5;
            while (CvOf(high) > coefficientOfVariation && high < 1e3)
                high *= 2.0;

            for (int i = 0; i < 200; i++)
            {
                double middle = 0.5 * (low + high);
                if (CvOf(middle) > coefficientOfVariation)
                    low = middle;
                else
                    high = middle;

                if (high - low < 1e-12 * Math.Max(1.0, high))
                    break;
            }

            k = 0.5 * (low + high);
            double scale = mean / SpecialFunctions.Gamma(1.0 + 1.0 / k);

            return new Weibull(k, scale, location, n);
        }

        /// <summary>
        /// Sorts the sample and rejects what cannot be fitted.
        ///
        /// <para><b>Throws rather than clipping</b>, the same house rule
        /// <see cref="ScaledBeta.FitByMoments"/> follows for observations outside its support: a
        /// value below the location parameter is data telling you the model is wrong, and
        /// silently moving it inside the support turns that signal into a slightly wrong
        /// answer.</para>
        /// </summary>
        private static List<double> Validate(IEnumerable<double> values, string parameterName)
        {
            if (values is null)
                throw new ArgumentNullException(parameterName);

            var sorted = new List<double>();
            foreach (double value in values)
            {
                if (double.IsNaN(value))
                    throw new ArgumentException("Sample contains NaN.", parameterName);
                if (double.IsInfinity(value))
                    throw new ArgumentException("Sample contains an infinity.", parameterName);

                sorted.Add(value);
            }

            if (sorted.Count < 2)
                throw new ArgumentException(
                    "Need at least two observations to fit.",
                    parameterName
                );

            sorted.Sort();

            if (!(sorted[0] > 0.0))
                throw new ArgumentException(
                    $"Value {sorted[0]:F4} is at or below zero, outside the support of a Weibull "
                        + "with a non-negative location. Shift the data rather than clipping it: "
                        + "a distribution cannot represent observations beyond its own bounds.",
                    parameterName
                );

            return sorted;
        }

        /// <summary>
        /// A fit at a stated location parameter, for callers that know γ rather than wanting it
        /// searched for - a transferred fit, or a reproduction of a published one.
        /// </summary>
        /// <param name="values">Observations. All must exceed <paramref name="location"/>.</param>
        /// <param name="location">The location parameter to hold fixed.</param>
        public static Weibull FitByMaximumLikelihood(IEnumerable<double> values, double location)
        {
            var sorted = Validate(values, nameof(values));

            if (location >= sorted[0])
                throw new ArgumentException(
                    $"Location {location:F4} is at or above the smallest observation "
                        + $"{sorted[0]:F4}. Every value must lie strictly inside the support; "
                        + "widen it rather than clipping the data.",
                    nameof(location)
                );

            return FitAtLocation(sorted, location);
        }
    }
}

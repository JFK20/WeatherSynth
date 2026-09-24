using WeatherSynth.Statistics;

namespace WeatherSynth.Wind;

/// <summary>
/// How a day's hours are <i>ordered</i>: the diurnal cycle and the hour-to-hour persistence that
/// turn a within-day distribution into a plausible sequence.
///
/// <para><b>What it adds to <see cref="IntradayShapeModel"/>.</b> That model already says how
/// widely a day's hourly speeds are spread, which is all an energy estimate needs. It says
/// nothing about <i>when</i> in the day the strong hours fall. This one does, with two ingredients
/// measured on the station's own hours:</para>
/// <list type="bullet">
/// <item>A <b>diurnal pattern</b> per month, by UTC hour. Near the ground the wind picks up in the
/// afternoon, when the sun mixes faster air down, and drops at night - most strongly in summer.
/// The weight <c>a</c> says how much of a day's hour-to-hour variation that pattern explains.</item>
/// <item>A pooled <b>hourly persistence</b> <c>rho</c>: the lag-1 correlation of what the pattern
/// leaves over. Wind at one hour is a good guess for the next.</item>
/// </list>
///
/// <para><b>Everything is measured on ranks, not speeds.</b> Each record day's twenty-four speeds
/// become normal scores of their ranks. That is exactly the inverse of how
/// <see cref="HourlyWindGenerator"/> uses this model - a standardised latent pushed through the
/// day's Weibull - so what is fitted is precisely what is generated, and the day's spread and mean
/// stay entirely the business of <see cref="IntradayShapeModel"/> and the daily chain.</para>
///
/// <para><b>What it does not do.</b> Each generated day is pinned to its own mean, so the step
/// from one day's mean to the next lands at midnight. Hourly persistence is continuous across it,
/// but the level is not.</para>
/// </summary>
internal sealed class HourlyWindModel
{
    /// <summary>Hours in a day, as the record and the generator both count them.</summary>
    public const int HoursPerDay = 24;

    /// <summary>
    /// Cap on the diurnal weight. At 1 the pattern would be the whole of every day and the
    /// residual chain would vanish; no station is that regular.
    /// </summary>
    public const double MaximumDiurnalWeight = 0.95;

    /// <summary>Cap on the hourly persistence, keeping the innovation term alive.</summary>
    public const double MaximumPersistence = 0.995;

    /// <summary>
    /// Days per calibration simulation; see <see cref="Calibrate"/>. The simulation's seed is
    /// fixed, so its sampling error is not noise but a small constant offset on every fit - about
    /// a thousand days per month keeps that below what the record itself can resolve.
    /// </summary>
    private const int CalibrationDays = 12000;

    private readonly double[] _weights;
    private readonly double[] _patterns;

    private HourlyWindModel(double persistence, double[] weights, double[] patterns, int sampleCount)
    {
        Persistence = persistence;
        _weights = weights;
        _patterns = patterns;
        SampleCount = sampleCount;
    }

    /// <summary>
    /// Lag-1 correlation of the hourly latent, rho. What <see cref="HourlyWindGenerator"/> runs its
    /// AR(1) with - already corrected for the per-day standardisation, see <see cref="Fit"/>.
    /// </summary>
    public double Persistence { get; }

    /// <summary>Record days the fit was measured over. Zero for a model built without a record.</summary>
    public int SampleCount { get; }

    /// <summary>The twelve diurnal weights, January first. Exposed for export.</summary>
    internal IReadOnlyList<double> DiurnalWeights => _weights;

    /// <summary>The twelve standardised patterns, 24 UTC hours each, January first. For export.</summary>
    internal IReadOnlyList<double> DiurnalPatterns => _patterns;

    /// <summary>How much of a day's hour-to-hour variation the month's diurnal pattern explains.</summary>
    /// <param name="month">Calendar month, 1-12.</param>
    public double DiurnalWeight(int month) => _weights[month - 1];

    /// <summary>The month's standardised diurnal pattern at a UTC hour: mean 0, sd 1 over the day.</summary>
    /// <param name="month">Calendar month, 1-12.</param>
    /// <param name="utcHour">Hour of the day in UTC, 0-23.</param>
    public double DiurnalPattern(int month, int utcHour) => _patterns[(month - 1) * HoursPerDay + utcHour];

    /// <summary>
    /// A model with no diurnal cycle, for providers built without an hourly record: hours are
    /// persistent but carry no time-of-day signal.
    /// </summary>
    /// <param name="persistence">The hourly lag-1 correlation to assume, in [0, 1).</param>
    public static HourlyWindModel Flat(double persistence)
    {
        if (!(persistence >= 0.0 && persistence < 1.0))
            throw new ArgumentOutOfRangeException(
                nameof(persistence),
                persistence,
                "Hourly persistence must be in [0, 1)."
            );

        return new HourlyWindModel(persistence, new double[12], new double[12 * HoursPerDay], 0);
    }

    /// <summary>Rebuilds a model from coefficients fitted earlier, without touching a record.</summary>
    /// <param name="persistence">The hourly persistence, rho.</param>
    /// <param name="diurnalWeights">Twelve weights, January first.</param>
    /// <param name="diurnalPatterns">Twelve times 24 standardised pattern values, January first.</param>
    /// <param name="sampleCount">Days the fit was measured over.</param>
    internal static HourlyWindModel FromCoefficients(
        double persistence,
        IReadOnlyList<double> diurnalWeights,
        IReadOnlyList<double> diurnalPatterns,
        int sampleCount
    )
    {
        ArgumentNullException.ThrowIfNull(diurnalWeights);
        ArgumentNullException.ThrowIfNull(diurnalPatterns);

        if (!(persistence >= 0.0 && persistence < 1.0))
            throw new ArgumentOutOfRangeException(nameof(persistence), persistence, "Must be in [0, 1).");
        if (diurnalWeights.Count != 12)
            throw new ArgumentException("Need twelve diurnal weights.", nameof(diurnalWeights));
        if (diurnalPatterns.Count != 12 * HoursPerDay)
            throw new ArgumentException("Need 12 x 24 diurnal pattern values.", nameof(diurnalPatterns));

        foreach (double weight in diurnalWeights)
            if (!(weight >= 0.0 && weight <= MaximumDiurnalWeight))
                throw new ArgumentOutOfRangeException(nameof(diurnalWeights), weight, "Out of [0, 0.95].");

        return new HourlyWindModel(
            persistence,
            diurnalWeights.ToArray(),
            diurnalPatterns.ToArray(),
            sampleCount
        );
    }

    /// <summary>
    /// Fits the diurnal patterns and the persistence from a record's complete days.
    ///
    /// <para><b>What is measured.</b> For each month, the mean normal score at each UTC hour: its
    /// shape, standardised, is the pattern, and its spread across the day - after removing what
    /// sampling noise alone would give - is the share of within-day variance it carries. Then the
    /// pooled lag-1 correlation, within days, of what the pattern leaves over. See
    /// <see cref="Measure"/>.</para>
    ///
    /// <para><b>Why neither number is used as measured.</b> Both are biased by the measurement
    /// itself, in ways no textbook correction covers. Removing a day's own mean from 24 strongly
    /// correlated values drags their lag-1 correlation far down: a latent at rho 0.9 measures
    /// about 0.75. And ranking each day divides it by its own spread, which is largest on the days
    /// whose weather happens to line up with the cycle - so the cycle is damped: a true weight of
    /// 0.45 measures about 0.37. The generator is subject to exactly the same effects, so what it
    /// needs are the parameters whose <i>measured</i> values match the record's. Those are found by
    /// simulating the generator itself, measuring the simulation with this very estimator, and
    /// iterating - see <see cref="Calibrate"/>. The patterns' shapes are used as measured.</para>
    /// </summary>
    /// <param name="days">
    /// Complete record days: the calendar month and the 24 hourly speeds indexed by UTC hour.
    /// Days whose speeds are all equal carry no ordering and are skipped.
    /// </param>
    public static HourlyWindModel Fit(IEnumerable<(int Month, IReadOnlyList<double> SpeedsByUtcHour)> days)
    {
        ArgumentNullException.ThrowIfNull(days);

        var months = new List<int>();
        var scores = new List<double[]>();

        foreach (var (month, speeds) in days)
        {
            if (month < 1 || month > 12)
                throw new ArgumentOutOfRangeException(nameof(days), month, "Month must be 1-12.");
            if (speeds is null || speeds.Count != HoursPerDay)
                throw new ArgumentException("Every day needs exactly 24 hourly speeds.", nameof(days));

            var day = new double[HoursPerDay];
            if (!StandardisedScores(speeds, day))
                continue;

            months.Add(month);
            scores.Add(day);
        }

        if (scores.Count == 0)
            throw new ArgumentException("No usable days: every day was constant.", nameof(days));

        var measured = Measure(months, scores);
        var (persistence, weights) = Calibrate(measured);

        return new HourlyWindModel(persistence, weights, measured.Patterns, scores.Count);
    }

    /// <summary>What <see cref="Measure"/> reads off a set of days.</summary>
    /// <param name="Weights">Twelve measured diurnal weights: the damped share, see <see cref="Fit"/>.</param>
    /// <param name="Patterns">Twelve standardised patterns, 24 UTC hours each.</param>
    /// <param name="Lag1">Pooled within-day lag-1 correlation of the residual.</param>
    internal sealed record Measurement(double[] Weights, double[] Patterns, double Lag1);

    /// <summary>
    /// The estimator, applied identically to the record and to every simulation.
    /// </summary>
    /// <param name="months">Each day's calendar month.</param>
    /// <param name="scores">Each day's standardised rank scores, by UTC hour.</param>
    internal static Measurement Measure(IReadOnlyList<int> months, IReadOnlyList<double[]> scores)
    {
        var weights = new double[12];
        var patterns = new double[12 * HoursPerDay];

        for (int month = 1; month <= 12; month++)
        {
            var profile = new double[HoursPerDay];
            int count = 0;

            for (int i = 0; i < scores.Count; i++)
            {
                if (months[i] != month)
                    continue;

                count++;
                for (int h = 0; h < HoursPerDay; h++)
                    profile[h] += scores[i][h];
            }

            if (count == 0)
                continue;

            double mean = 0.0;
            for (int h = 0; h < HoursPerDay; h++)
            {
                profile[h] /= count;
                mean += profile[h];
            }
            mean /= HoursPerDay;

            double spread = 0.0;
            for (int h = 0; h < HoursPerDay; h++)
                spread += (profile[h] - mean) * (profile[h] - mean);
            spread /= HoursPerDay;

            // Each hour's mean over n days carries about 1/n of noise variance even with no
            // pattern at all; without this a flat month would fit a small, spurious cycle.
            double explained = (spread - 1.0 / count) / (1.0 - 1.0 / count);
            if (!(explained > 0.0))
                continue;

            weights[month - 1] = Math.Min(Math.Sqrt(explained), MaximumDiurnalWeight);

            double sd = Math.Sqrt(spread);
            for (int h = 0; h < HoursPerDay; h++)
                patterns[(month - 1) * HoursPerDay + h] = (profile[h] - mean) / sd;
        }

        var lag = new Lag1();
        for (int i = 0; i < scores.Count; i++)
            lag.AddResidual(scores[i], months[i], weights, patterns);

        return new Measurement(weights, patterns, lag.Correlation);
    }

    /// <summary>
    /// The generator's parameters whose simulated measurement matches <paramref name="target"/>.
    ///
    /// <para>A fixed-point iteration, one simulation per round. Each weight is scaled by how far
    /// its simulated measurement fell short of the target; the persistence takes a secant step
    /// on the lag-1 correlation, which rises monotonically with it. The two barely interact, and
    /// four or five rounds settle both.</para>
    ///
    /// <para><b>How exact it is.</b> The simulation runs on the <i>measured</i> pattern shapes,
    /// which carry the record's sampling noise, and a noisy pattern is damped slightly differently
    /// from a smooth one. Recovering a known model therefore lands within about 0.002 of its
    /// persistence and 0.01 of its weights - well inside what seventeen years of hours can
    /// resolve.</para>
    /// </summary>
    internal static (double Persistence, double[] Weights) Calibrate(Measurement target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var weights = (double[])target.Weights.Clone();
        double persistence = Math.Clamp(target.Lag1 + 0.15, 0.0, MaximumPersistence);

        double previousPersistence = double.NaN,
            previousLag = double.NaN;

        for (int round = 0; round < MaximumCalibrationRounds; round++)
        {
            var simulated = Simulate(persistence, weights, target.Patterns);

            double lagError = target.Lag1 - simulated.Lag1;
            double worstWeightError = 0.0;

            for (int m = 0; m < 12; m++)
            {
                if (!(target.Weights[m] > 0.0))
                {
                    weights[m] = 0.0;
                    continue;
                }

                worstWeightError = Math.Max(
                    worstWeightError,
                    Math.Abs(target.Weights[m] - simulated.Weights[m])
                );

                // A weight too small to register in the simulation is doubled until it does.
                double ratio = simulated.Weights[m] > 0.0 ? target.Weights[m] / simulated.Weights[m] : 2.0;
                weights[m] = Math.Clamp(Math.Max(weights[m], 0.01) * ratio, 0.0, MaximumDiurnalWeight);
            }

            if (Math.Abs(lagError) < 1e-4 && worstWeightError < 1e-3)
                break;

            // Secant on rho; the first step, and any step whose slope looks wrong, uses the
            // measured slope of a flat simulation near high persistence, about 1.3.
            double slope =
                double.IsNaN(previousLag) || !(simulated.Lag1 - previousLag > 1e-6)
                    ? 1.3
                    : (simulated.Lag1 - previousLag) / (persistence - previousPersistence);

            if (!(slope > 0.1))
                slope = 1.3;

            previousPersistence = persistence;
            previousLag = simulated.Lag1;
            persistence = Math.Clamp(persistence + lagError / slope, 0.0, MaximumPersistence);
        }

        return (persistence, weights);
    }

    /// <summary>Rounds <see cref="Calibrate"/> may take.</summary>
    private const int MaximumCalibrationRounds = 25;

    /// <summary>
    /// Hours generated exactly as <see cref="HourlyWindGenerator"/> generates them, measured
    /// exactly as the record is.
    ///
    /// <para>Months cycle day by day so each carries equal weight. The seed is fixed - common
    /// random numbers - so the result is a smooth function of the parameters and the iteration in
    /// <see cref="Calibrate"/> cannot be fooled by noise.</para>
    /// </summary>
    internal static Measurement Simulate(double persistence, double[] weights, double[] patterns)
    {
        var random = new Random(20090101);
        double innovation = Math.Sqrt(1.0 - persistence * persistence);
        double latent = Gaussian.Sample(random);

        var months = new List<int>(CalibrationDays);
        var scores = new List<double[]>(CalibrationDays);
        var raw = new double[HoursPerDay];

        for (int d = 0; d < CalibrationDays; d++)
        {
            int month = d % 12 + 1;
            double a = weights[month - 1];
            double b = Math.Sqrt(1.0 - a * a);

            for (int h = 0; h < HoursPerDay; h++)
            {
                latent = persistence * latent + innovation * Gaussian.Sample(random);
                raw[h] = latent;
            }

            // Exactly as HourlyWindGenerator.FillDay mixes it.
            if (!Standardise(raw))
                continue;

            for (int h = 0; h < HoursPerDay; h++)
                raw[h] = a * patterns[(month - 1) * HoursPerDay + h] + b * raw[h];

            var day = new double[HoursPerDay];
            if (!StandardisedScores(raw, day))
                continue;

            months.Add(month);
            scores.Add(day);
        }

        return Measure(months, scores);
    }

    /// <summary>
    /// A day's speeds as normal scores of their ranks, standardised to mean 0 and sd 1. Ties share
    /// their average rank. False when every speed is equal.
    /// </summary>
    internal static bool StandardisedScores(IReadOnlyList<double> speeds, Span<double> scores)
    {
        int n = speeds.Count;
        Span<double> keys = stackalloc double[n];
        Span<int> order = stackalloc int[n];
        for (int i = 0; i < n; i++)
        {
            keys[i] = speeds[i];
            order[i] = i;
        }
        keys.Sort(order);

        for (int start = 0; start < n; )
        {
            int end = start;
            while (end + 1 < n && speeds[order[end + 1]] == speeds[order[start]])
                end++;

            // Ranks are 1-based; (rank - 0.5) / n keeps the extremes off 0 and 1.
            double rank = 0.5 * (start + end) + 1.0;
            double score = Gaussian.Quantile((rank - 0.5) / n);

            for (int k = start; k <= end; k++)
                scores[order[k]] = score;

            start = end + 1;
        }

        return Standardise(scores);
    }

    /// <summary>Shifts and scales in place to mean 0 and population sd 1. False when constant.</summary>
    internal static bool Standardise(Span<double> values)
    {
        double mean = 0.0;
        foreach (double value in values)
            mean += value;
        mean /= values.Length;

        double variance = 0.0;
        foreach (double value in values)
            variance += (value - mean) * (value - mean);
        variance /= values.Length;

        if (!(variance > 1e-24))
            return false;

        double sd = Math.Sqrt(variance);
        for (int i = 0; i < values.Length; i++)
            values[i] = (values[i] - mean) / sd;

        return true;
    }

    /// <summary>
    /// Pooled within-day lag-1 correlation. Pairs never straddle midnight, because the record's
    /// days are standardised one at a time and a pair across two of them compares two scales.
    /// </summary>
    private sealed class Lag1
    {
        private readonly double[] _residual = new double[HoursPerDay];
        private double _cross,
            _head,
            _tail;

        public double Correlation =>
            _head > 0.0 && _tail > 0.0 ? _cross / Math.Sqrt(_head * _tail) : 0.0;

        /// <summary>Adds one day's standardised scores, with the month's pattern taken out.</summary>
        public void AddResidual(double[] day, int month, double[] weights, double[] patterns)
        {
            int m = month - 1;
            double a = weights[m];
            double b = Math.Sqrt(1.0 - a * a);

            for (int h = 0; h < HoursPerDay; h++)
                _residual[h] = (day[h] - a * patterns[m * HoursPerDay + h]) / b;

            for (int h = 0; h + 1 < HoursPerDay; h++)
            {
                _cross += _residual[h] * _residual[h + 1];
                _head += _residual[h] * _residual[h];
                _tail += _residual[h + 1] * _residual[h + 1];
            }
        }
    }

    /// <summary>The fitted model, for reports that need to print it.</summary>
    public override string ToString() =>
        $"hourly rho {Persistence:F4}, diurnal weight "
        + $"{_weights.Min():F3}-{_weights.Max():F3}"
        + (SampleCount == 0 ? " (not fitted)" : $" ({SampleCount:N0} days)");
}

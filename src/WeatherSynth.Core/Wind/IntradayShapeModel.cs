using WeatherSynth.Climate;
using WeatherSynth.Statistics;

namespace WeatherSynth.Wind;

/// <summary>
/// How a day's wind is spread across its own hours - the piece a power curve needs and a daily
/// mean speed cannot supply.
///
/// <para><b>Why this exists.</b> A turbine's output is a strongly non-linear function of speed,
/// so the energy in a day depends on how the day's wind was distributed and not only on its
/// average. Evaluating a power curve at a daily mean understates this station's yield by 30% at
/// the anemometer and 15% at a hub height, always in the same direction. What is needed instead
/// is a distribution to integrate over, and this is the smallest honest one.</para>
///
/// <para><b>The energy pattern factor already names it.</b> For a two-parameter Weibull,</para>
/// <code>
/// EPF = mean(v³)/mean(v)³ = Gamma(1 + 3/k) / Gamma(1 + 1/k)³
/// </code>
/// <para>which is strictly decreasing in k - so the factor this library has carried since Phase
/// 0 <i>is</i> the within-day shape parameter, in disguise, and recovering one from the other is
/// a bijection rather than a fit. At this station's median EPF of 1.251 the implied within-day
/// k is about 3.86, comfortably above the 2.71 fitted on daily means across the year and the
/// 2.14 on hourly values. That ordering is the sanity check: a single day's spread is narrower
/// than a season's, which is narrower than a year's.</para>
///
/// <para><b>The factor depends on the day's speed, which is why this is fitted rather than
/// constant.</b> Calm days are relatively more variable: measured here,
/// <c>corr(daily mean, EPF) = -0.283</c>, running from 1.376 on the calmest quartile of days to
/// 1.211 on the windiest. <see cref="Climate.WindSpeedModel.MeanEnergyPatternFactor"/> is a
/// single number for the whole record, and using it for every day biases a yield estimate by
/// +8.7% at this station's anemometer height. The one-parameter fit here takes that to -1.0%.</para>
///
/// <para><b>What it still does not do.</b> It reproduces the <i>trend</i> in the factor, not its
/// scatter: the measured EPF at a given speed still runs roughly 1.08 to 1.58, and this returns
/// one value. Annual energy comes out right; the spread of daily energy is narrower than
/// reality. Anything sizing storage against a run of poor days should know that.</para>
/// </summary>
internal sealed class IntradayShapeModel
{
    /// <summary>
    /// Bounds on the recovered shape parameter.
    ///
    /// <para>The upper end matters more than it looks: as k grows the day becomes a steady
    /// wind, EPF approaches 1 from above, and the inverse becomes ill-conditioned - a factor of
    /// 1.0001 differs from 1.00001 by a huge stretch of k and by nothing physical. Capping it
    /// keeps a near-steady day steady instead of letting rounding pick an arbitrary k.
    /// </para>
    /// </summary>
    public const double MinimumShape = 1.05;

    /// <inheritdoc cref="MinimumShape" />
    public const double MaximumShape = 50.0;

    private readonly double _intercept;
    private readonly double _slope;

    private IntradayShapeModel(
        double intercept,
        double slope,
        double pooledFactor,
        int sampleCount
    )
    {
        _intercept = intercept;
        _slope = slope;
        PooledEnergyPatternFactor = pooledFactor;
        SampleCount = sampleCount;
    }

    /// <summary>Mean of the record's daily energy pattern factors, ignoring speed.</summary>
    /// <remarks>
    /// Carried for comparison rather than for use: it is what a caller gets from
    /// <see cref="Climate.WindSpeedModel.MeanEnergyPatternFactor"/>, and printing the two side by
    /// side is what shows the speed dependence is real.
    /// </remarks>
    public double PooledEnergyPatternFactor { get; }

    /// <summary>Days the fit was measured over. Zero for a model built from a stated factor.</summary>
    public int SampleCount { get; }

    /// <summary>
    /// Intercept of the log-log fit. Exposed so the fit can be written out and rebuilt without
    /// a record - see <see cref="FromCoefficients"/>.
    /// </summary>
    internal double Intercept => _intercept;

    /// <summary>
    /// Slope of the log-log fit: how much gustier a fast day is than a slow one. Zero means a
    /// single factor for every day, which is what <see cref="Constant"/> builds.
    /// </summary>
    internal double Slope => _slope;

    /// <summary>
    /// Rebuilds a shape model from coefficients fitted earlier, without touching a record.
    ///
    /// <para>Two numbers and a power law: <see cref="Fit"/> is a least squares of
    /// <c>log(EPF)</c> on <c>log(mean speed)</c>, so the intercept and the slope are the whole
    /// of it. The pooled factor and the sample count are carried for reporting.</para>
    /// </summary>
    /// <param name="intercept">Intercept of the log-log fit.</param>
    /// <param name="slope">Slope of the log-log fit. Zero is a speed-independent factor.</param>
    /// <param name="pooledEnergyPatternFactor">Record mean of the daily factors, ignoring speed.</param>
    /// <param name="sampleCount">Days the fit was measured over.</param>
    internal static IntradayShapeModel FromCoefficients(
        double intercept,
        double slope,
        double pooledEnergyPatternFactor,
        int sampleCount
    ) => new IntradayShapeModel(intercept, slope, pooledEnergyPatternFactor, sampleCount);

    /// <summary>
    /// A model with one factor for every day, for callers with no record to fit from.
    /// </summary>
    /// <param name="energyPatternFactor">
    /// The <c>mean(v³)/mean(v)³</c> to assume. Must exceed 1: at exactly 1 the day's wind never
    /// varies, which no real day manages, and below it Jensen's inequality is violated.
    /// </param>
    public static IntradayShapeModel Constant(double energyPatternFactor)
    {
        if (!(energyPatternFactor > 1.0))
            throw new ArgumentOutOfRangeException(
                nameof(energyPatternFactor),
                energyPatternFactor,
                "Energy pattern factor must exceed 1: mean(v³) > mean(v)³ for any day whose "
                    + "wind varies at all, with equality only for a perfectly steady one."
            );

        return new IntradayShapeModel(
            Math.Log(energyPatternFactor),
            0.0,
            energyPatternFactor,
            0
        );
    }

    /// <summary>
    /// Fits the speed dependence from a measured record.
    ///
    /// <para>Least squares of <c>log(EPF)</c> on <c>log(mean speed)</c> - a power law, which
    /// keeps the prediction positive at every speed and is what the measured relationship
    /// actually looks like. One parameter of curvature is all this deserves: the scatter about
    /// the trend is several times the trend itself, so a richer function would be fitting
    /// noise.</para>
    /// </summary>
    /// <param name="series">
    /// Daily speeds carrying <see cref="DailyWindSpeed.MeanCubedSpeed"/>. Days without one, or
    /// whose factor is not above 1, are skipped rather than clamped.
    /// </param>
    public static IntradayShapeModel Fit(IEnumerable<DailyWindSpeed> series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var logSpeed = new List<double>();
        var logFactor = new List<double>();
        double factorSum = 0.0;

        foreach (var day in series)
        {
            double speed = day.MeanSpeed;
            double factor = day.EnergyPatternFactor;

            if (double.IsNaN(speed) || speed <= 0.0)
                continue;
            if (double.IsNaN(factor) || factor <= 1.0)
                continue;

            logSpeed.Add(Math.Log(speed));
            logFactor.Add(Math.Log(factor));
            factorSum += factor;
        }

        if (logSpeed.Count == 0)
            throw new ArgumentException(
                "No usable days in the series: an intra-day shape needs mean cubed speeds.",
                nameof(series)
            );

        double pooled = factorSum / logSpeed.Count;

        // A single day, or a record with no spread in speed, leaves the slope unidentified.
        // Falling back to the pooled factor is the honest answer, and it is exactly what the
        // model did before this class existed.
        if (logSpeed.Count < 2)
            return Constant(pooled);

        double meanX = 0.0,
            meanY = 0.0;
        for (int i = 0; i < logSpeed.Count; i++)
        {
            meanX += logSpeed[i];
            meanY += logFactor[i];
        }
        meanX /= logSpeed.Count;
        meanY /= logSpeed.Count;

        double covariance = 0.0,
            variance = 0.0;
        for (int i = 0; i < logSpeed.Count; i++)
        {
            double dx = logSpeed[i] - meanX;
            covariance += dx * (logFactor[i] - meanY);
            variance += dx * dx;
        }

        if (!(variance > 0.0))
            return Constant(pooled);

        double slope = covariance / variance;

        return new IntradayShapeModel(meanY - slope * meanX, slope, pooled, logSpeed.Count);
    }

    /// <summary>
    /// The energy pattern factor this model expects of a day with the given mean speed.
    /// </summary>
    /// <param name="dailyMeanSpeed">
    /// The day's mean speed <b>at the height the model was fitted at</b>. See
    /// <see cref="ShapeFor"/> - this is the argument that is easy to get wrong.
    /// </param>
    public double EnergyPatternFactorFor(double dailyMeanSpeed)
    {
        if (double.IsNaN(dailyMeanSpeed) || dailyMeanSpeed <= 0.0)
            return PooledEnergyPatternFactor;

        double factor = Math.Exp(_intercept + _slope * Math.Log(dailyMeanSpeed));

        return double.IsFinite(factor) ? factor : PooledEnergyPatternFactor;
    }

    /// <summary>
    /// The within-day Weibull shape parameter for a day with the given mean speed.
    ///
    /// <para><b>Pass the speed at the fitting height, not at the target.</b> The shape is a
    /// property of the day's weather - how gusty it was - and the fit that predicts it was
    /// measured against anemometer speeds. Feeding a hub-height speed in asks what the shape
    /// would be on a much windier day, and returns a steadier one than the day really was.
    /// <see cref="SyntheticWindDay.MeanSpeedAtReference"/> is carried for exactly this,
    /// alongside the transferred <see cref="SyntheticWindDay.MeanSpeed"/>.</para>
    /// </summary>
    /// <param name="dailyMeanSpeedAtReference">The day's mean speed at the fitting height, m/s.</param>
    public double ShapeFor(double dailyMeanSpeedAtReference) =>
        ShapeFromEnergyPatternFactor(EnergyPatternFactorFor(dailyMeanSpeedAtReference));

    /// <summary>
    /// The within-day speed distribution for a day, ready to integrate a power curve over.
    /// </summary>
    /// <param name="dailyMeanSpeedAtReference">
    /// The day's mean speed at the fitting height, which sets the <i>shape</i>.
    /// </param>
    /// <param name="dailyMeanSpeedAtTarget">
    /// The day's mean speed at the site being generated for, which sets the <i>scale</i>. The
    /// same value as the first argument when generating at the fitting height.
    /// </param>
    /// <returns>
    /// A two-parameter Weibull - location zero - whose mean is
    /// <paramref name="dailyMeanSpeedAtTarget"/> by construction.
    /// </returns>
    public Weibull DistributionFor(
        double dailyMeanSpeedAtReference,
        double dailyMeanSpeedAtTarget
    )
    {
        if (!(dailyMeanSpeedAtTarget > 0.0))
            throw new ArgumentOutOfRangeException(
                nameof(dailyMeanSpeedAtTarget),
                dailyMeanSpeedAtTarget,
                "A day's mean speed must be positive to have a distribution."
            );

        double shape = ShapeFor(dailyMeanSpeedAtReference);

        // mean = A * Gamma(1 + 1/k), so the scale is whatever puts the mean where it belongs.
        // Getting this backwards - reading A as the mean - is the most common error in this
        // literature, and knowledge.md §14 lists it as such.
        double scale = dailyMeanSpeedAtTarget / SpecialFunctions.Gamma(1.0 + 1.0 / shape);

        return new Weibull(shape, scale);
    }

    /// <summary>
    /// Inverts <c>EPF(k) = Gamma(1 + 3/k) / Gamma(1 + 1/k)³</c>.
    ///
    /// <para>Bisection rather than Newton: the function is monotone decreasing on the whole
    /// bracket, so bisection cannot fail, and this runs once per generated day rather than in
    /// an inner loop. Fifty halvings take the interval to well under a part in 10^12, which is
    /// far finer than the factor it is inverting is known.</para>
    /// </summary>
    /// <param name="energyPatternFactor">A factor above 1; anything at or below returns the cap.</param>
    public static double ShapeFromEnergyPatternFactor(double energyPatternFactor)
    {
        if (double.IsNaN(energyPatternFactor))
            return MaximumShape;

        double lower = MinimumShape;
        double upper = MaximumShape;

        // Outside what the bracket can express, the answer is the nearer end rather than an
        // extrapolation: a factor below EPF(MaximumShape) means a steadier day than this model
        // distinguishes, and one above EPF(MinimumShape) a wilder one.
        if (energyPatternFactor <= FactorFromShape(upper))
            return upper;
        if (energyPatternFactor >= FactorFromShape(lower))
            return lower;

        for (int i = 0; i < 50; i++)
        {
            double middle = 0.5 * (lower + upper);

            if (FactorFromShape(middle) > energyPatternFactor)
                lower = middle;
            else
                upper = middle;
        }

        return 0.5 * (lower + upper);
    }

    /// <summary>
    /// <c>EPF(k)</c>, the forward direction of <see cref="ShapeFromEnergyPatternFactor"/>.
    /// </summary>
    public static double FactorFromShape(double shape)
    {
        if (!(shape > 0.0))
            throw new ArgumentOutOfRangeException(
                nameof(shape),
                shape,
                "Weibull shape must be positive."
            );

        // Through logs, because Gamma(1 + 1/k) cubed underflows nothing here but the log form
        // is the one that stays accurate as k approaches its cap and the ratio approaches 1.
        return Math.Exp(
            SpecialFunctions.LogGamma(1.0 + 3.0 / shape)
                - 3.0 * SpecialFunctions.LogGamma(1.0 + 1.0 / shape)
        );
    }

    /// <summary>The fitted relationship, for reports that need to print it.</summary>
    public override string ToString() =>
        SampleCount == 0
            ? $"constant EPF {PooledEnergyPatternFactor:F4}"
            : $"log EPF = {_intercept:+0.0000;-0.0000} {_slope:+0.0000;-0.0000}*log(v)  "
                + $"(pooled {PooledEnergyPatternFactor:F4}, {SampleCount:N0} days)";
}

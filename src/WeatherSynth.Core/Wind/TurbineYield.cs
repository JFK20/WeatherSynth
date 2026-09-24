using WeatherSynth.Climate;
using WeatherSynth.Statistics;
using WeatherSynth.Wind;

namespace WeatherSynth;

/// <summary>One generated day's turbine output.</summary>
/// <param name="Date">The day.</param>
/// <param name="MeanPowerKilowatts">
/// Mean electrical output across the day, kW - the power curve integrated over the day's own
/// within-day speed distribution, not evaluated at its mean speed.
/// </param>
/// <param name="MeanSpeed">The day's mean speed at the turbine, m/s. Carried for diagnosis.</param>
public readonly record struct TurbineDay(
    DateOnly Date,
    double MeanPowerKilowatts,
    double MeanSpeed
)
{
    /// <summary>Energy generated over the day, kWh. Twenty-four hours at the mean power.</summary>
    public double EnergyKilowattHours => MeanPowerKilowatts * 24.0;
}

/// <summary>
/// What a turbine would have produced over a run of generated days.
///
/// <para>The end of the wind pipeline, and the first point at which it answers a question in
/// the units anyone asks it in. Deliberately a separate object rather than a property of
/// <see cref="SyntheticWindYear"/>: a generated year is a statement about the weather and does
/// not know what machine is standing in it, and the same year should be costable against
/// several turbines without regenerating anything.</para>
///
/// <para><b>Every day is integrated, never evaluated.</b> See
/// <see cref="TurbinePowerCurve"/> for why that distinction is not a refinement: at this
/// library's station, evaluating the curve at each day's mean speed understates annual yield by
/// 30% at the anemometer and 15% at a 100 m hub.</para>
///
/// <para><b>The number this produces inherits the height transfer's uncertainty, cubed.</b> The
/// log law and the power law disagree by a factor 1.35 on a 15 m to 100 m extrapolation
/// (knowledge.md §14); through a power curve that becomes <b>2.5x in capacity factor</b>, because
/// the working region is cubic and neither profile gets the site anywhere near the rated
/// plateau. Quote a yield figure with the profile it was computed under, or it means nothing.</para>
/// </summary>
public sealed class TurbineYield
{
    private TurbineYield(
        IReadOnlyList<TurbineDay> days,
        TurbinePowerCurve curve,
        double meanPower,
        double belowCutInFraction
    )
    {
        Days = days;
        Curve = curve;
        MeanPowerKilowatts = meanPower;
        FractionOfDaysBelowCutIn = belowCutInFraction;
    }

    /// <summary>Every day costed, in the order supplied.</summary>
    public IReadOnlyList<TurbineDay> Days { get; }

    /// <summary>The curve these days were costed against.</summary>
    public TurbinePowerCurve Curve { get; }

    /// <summary>Mean electrical output across the whole run, kW.</summary>
    public double MeanPowerKilowatts { get; }

    /// <summary>
    /// Fraction of days whose <i>mean</i> speed sat below cut-in.
    ///
    /// <para>Not the fraction of days that generated nothing, and the gap between the two is
    /// the point of this whole class: a day averaging below cut-in still has windy hours, and
    /// the integration credits them. At this station's anemometer height most days fall in this
    /// bucket and the site still produces measurable energy.</para>
    /// </summary>
    public double FractionOfDaysBelowCutIn { get; }

    /// <summary>Total energy over the run, MWh.</summary>
    public double EnergyMegawattHours => MeanPowerKilowatts * 24.0 * Days.Count / 1000.0;

    /// <summary>
    /// Mean output as a fraction of nameplate capacity, in [0, 1] - the figure a wind resource
    /// is actually judged on.
    ///
    /// <para>For context: a good German onshore site runs 0.25-0.35, offshore 0.40-0.50. A
    /// figure near 0.02 is not a bug, it is a 15 m anemometer at a sheltered inland site, and
    /// it is what generating at the fitting height means.</para>
    /// </summary>
    public double CapacityFactor => MeanPowerKilowatts / Curve.RatedPowerKilowatts;

    /// <summary>
    /// Costs a run of generated days against a turbine.
    /// </summary>
    /// <param name="days">
    /// Generated days. Both <see cref="SyntheticWindDay.MeanSpeed"/> and
    /// <see cref="SyntheticWindDay.MeanSpeedAtReference"/> are used, and they are not
    /// interchangeable - the transferred speed sets each day's scale, the reference speed its
    /// shape. See <see cref="IntradayShapeModel.ShapeFor"/>.
    /// </param>
    /// <param name="curve">The turbine.</param>
    /// <param name="shape">
    /// The within-day distribution to integrate over. <see cref="IntradayShapeModel.Fit"/> from
    /// the same record the speeds were fitted from.
    /// </param>
    internal static TurbineYield Estimate(
        IEnumerable<SyntheticWindDay> days,
        TurbinePowerCurve curve,
        IntradayShapeModel shape
    )
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(shape);

        var costed = new List<TurbineDay>();
        double powerSum = 0.0;
        int belowCutIn = 0;

        foreach (var day in days)
        {
            double power = MeanPowerOverDay(
                curve,
                shape,
                day.MeanSpeedAtReference,
                day.MeanSpeed
            );

            costed.Add(new TurbineDay(day.Date, power, day.MeanSpeed));
            powerSum += power;

            if (day.MeanSpeed < curve.CutInMetersPerSecond)
                belowCutIn++;
        }

        if (costed.Count == 0)
            throw new ArgumentException("No days to cost.", nameof(days));

        return new TurbineYield(
            costed,
            curve,
            powerSum / costed.Count,
            (double)belowCutIn / costed.Count
        );
    }

    /// <summary>
    /// The power curve integrated over one day's within-day speed distribution.
    ///
    /// <para>Exposed on its own because it is the whole of the modelling claim, and because
    /// anything checking this library against a measured record needs to call it a day at a
    /// time rather than through a generated year.</para>
    /// </summary>
    /// <param name="curve">The turbine.</param>
    /// <param name="shape">The within-day distribution model.</param>
    /// <param name="meanSpeedAtReference">The day's mean speed at the fitting height - sets the shape.</param>
    /// <param name="meanSpeedAtTarget">The day's mean speed at the turbine - sets the scale.</param>
    internal static double MeanPowerOverDay(
        TurbinePowerCurve curve,
        IntradayShapeModel shape,
        double meanSpeedAtReference,
        double meanSpeedAtTarget
    )
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(shape);

        if (double.IsNaN(meanSpeedAtTarget) || meanSpeedAtTarget <= 0.0)
            return 0.0;

        var distribution = shape.DistributionFor(meanSpeedAtReference, meanSpeedAtTarget);

        return Integrate(curve, distribution);
    }

    /// <summary>Sub-intervals per segment of the curve. See <see cref="Integrate"/>.</summary>
    private const int StepsPerSegment = 128;

    /// <summary>
    /// <c>E[P(v)]</c> for a two-parameter Weibull, integrated segment by segment between the
    /// curve's own breakpoints.
    ///
    /// <para>Segmenting is what makes this accurate cheaply. A power curve has a corner at
    /// cut-in, another at rated and a cliff at cut-out; a quadrature whose intervals straddle
    /// those smears them, and the smearing does not cancel because the curve is convex below
    /// rated and flat above. Putting every kink on a segment boundary leaves each segment
    /// smooth, where the midpoint rule - the same choice <c>DailyClearSkyCalculator</c> makes for the
    /// clear-sky integral - converges quickly.</para>
    ///
    /// <para>Constant segments are handled <b>exactly</b> rather than numerically: over the
    /// rated plateau the integral is the rated power times the probability of landing in it,
    /// which the Weibull CDF gives in closed form. That covers most of the mass at a windy
    /// site, and it means the only approximated region is the cubic run-up.</para>
    /// </summary>
    private static double Integrate(TurbinePowerCurve curve, Weibull distribution)
    {
        var breakpoints = curve.Breakpoints;
        double total = 0.0;

        // Below the first breakpoint the curve is zero by definition of cut-in, and above the
        // last it is zero because the turbine has shut down - so neither contributes and the
        // integration runs over the working range alone.
        for (int i = 1; i < breakpoints.Count; i++)
        {
            double lower = breakpoints[i - 1];
            double upper = breakpoints[i];

            if (!(upper > lower))
                continue;

            double atLower = curve.PowerKilowatts(lower);
            double atUpper = curve.PowerKilowatts(upper);
            double atMiddle = curve.PowerKilowatts(0.5 * (lower + upper));

            // Flat here, so the exact answer is available and cheaper than the approximate one.
            if (atLower == atUpper && atMiddle == atLower)
            {
                total +=
                    atLower
                    * (
                        distribution.CumulativeProbability(upper)
                        - distribution.CumulativeProbability(lower)
                    );
                continue;
            }

            double step = (upper - lower) / StepsPerSegment;
            double segment = 0.0;

            for (int j = 0; j < StepsPerSegment; j++)
            {
                double v = lower + (j + 0.5) * step;
                segment += curve.PowerKilowatts(v) * distribution.Density(v);
            }

            total += segment * step;
        }

        return total;
    }
}

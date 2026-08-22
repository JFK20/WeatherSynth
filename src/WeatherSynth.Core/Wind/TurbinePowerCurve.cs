using System;
using System.Collections.Generic;
using System.Linq;

namespace WeatherSynth.Wind
{
    /// <summary>
    /// How much electrical power a turbine produces at a given wind speed.
    ///
    /// <para>The wind counterpart of turning GHI into PV yield, and the step that makes the rest of
    /// this library answer a question anyone actually asks. Nobody sizes anything in metres per
    /// second.</para>
    ///
    /// <para><b>The shape is what makes this awkward, and it is worth seeing before using it.</b> A
    /// power curve is zero below cut-in, roughly cubic up to rated, <i>flat</i> from rated to
    /// cut-out, and zero again above - the turbine shuts down to protect itself. Three of those
    /// four regions are places where <c>P(mean speed)</c> is not the mean of <c>P(speed)</c>, and
    /// the errors do not cancel: below cut-in the naive figure says nothing was generated while the
    /// day had windy hours, and above rated it credits full output for hours the turbine never
    /// reached. <b>Never evaluate a power curve at a daily mean speed.</b> Integrate it over a
    /// within-day distribution - <see cref="TurbineYield"/> is that, and
    /// <see cref="IntradayShapeModel"/> supplies the distribution.</para>
    ///
    /// <para>Two forms, behind one type so they can be compared rather than argued about, exactly
    /// as <see cref="WindProfile"/> carries the log law beside the power law.</para>
    ///
    /// <para><b>Air density is not modelled.</b> Power is proportional to it, and it varies a few
    /// percent with altitude and temperature; a manufacturer's table is quoted at sea-level
    /// 1.225 kg/m³. At the 150 m site this library is built on that is worth roughly -1.5%, which
    /// is an order of magnitude below the height-transfer uncertainty sitting upstream and is not
    /// worth modelling until that is fixed.</para>
    /// </summary>
    public abstract class TurbinePowerCurve
    {
        /// <summary>
        /// The textbook idealisation: zero, cubic, flat, zero.
        ///
        /// <para>Between cut-in and rated it uses <c>Pr·(v³ − vin³)/(vr³ − vin³)</c> rather than the
        /// also-common <c>Pr·(v/vr)³</c>. The two differ only in that this one is continuous at
        /// cut-in, where the other jumps to a few percent of rated the instant the turbine
        /// starts.</para>
        ///
        /// <para>Good enough for a resource estimate and wrong in detail: a real machine rounds
        /// over near rated and its coefficient of performance is not constant across the working
        /// range. Where a manufacturer's table exists, use <see cref="FromTable"/>.</para>
        /// </summary>
        /// <param name="cutInMetersPerSecond">Below this the rotor does not turn. Typically ~3 m/s.</param>
        /// <param name="ratedMetersPerSecond">Where full output is first reached. Typically 12-13 m/s.</param>
        /// <param name="cutOutMetersPerSecond">Above this the turbine shuts down. Typically ~25 m/s.</param>
        /// <param name="ratedPowerKilowatts">Nameplate capacity.</param>
        public static TurbinePowerCurve Idealised(
            double cutInMetersPerSecond,
            double ratedMetersPerSecond,
            double cutOutMetersPerSecond,
            double ratedPowerKilowatts
        ) =>
            new IdealisedCurve(
                cutInMetersPerSecond,
                ratedMetersPerSecond,
                cutOutMetersPerSecond,
                ratedPowerKilowatts
            );

        /// <summary>
        /// A manufacturer's table, interpolated linearly between its points and zero outside it.
        ///
        /// <para>What real turbines ship, and the form to prefer when one is available. Linear
        /// rather than cubic interpolation between points because the table is usually dense enough
        /// (1 m/s steps) that the curvature within a step is negligible, and because a cubic spline
        /// through measured points can overshoot into non-physical wiggles near rated.</para>
        ///
        /// <para><b>Zero outside the tabulated range, in both directions.</b> Below the first point
        /// that is right - it is below cut-in. Above the last it is right only if the table runs to
        /// cut-out, which manufacturers' tables do. A table that stops at rated would be read as a
        /// turbine that shuts down at rated, so include the plateau.</para>
        /// </summary>
        /// <param name="points">Speed/power pairs in kW. Order does not matter; duplicates do not.</param>
        public static TurbinePowerCurve FromTable(
            IEnumerable<(double SpeedMetersPerSecond, double PowerKilowatts)> points
        ) => new TabulatedCurve(points);

        /// <summary>Electrical output at a steady wind speed, in kW. Zero outside the working range.</summary>
        /// <param name="speedMetersPerSecond">Wind speed at hub height.</param>
        public abstract double PowerKilowatts(double speedMetersPerSecond);

        /// <summary>Nameplate capacity in kW - the denominator of a capacity factor.</summary>
        public abstract double RatedPowerKilowatts { get; }

        /// <summary>Lowest speed at which the turbine generates anything, m/s.</summary>
        public abstract double CutInMetersPerSecond { get; }

        /// <summary>Highest speed at which the turbine generates anything, m/s.</summary>
        public abstract double CutOutMetersPerSecond { get; }

        /// <summary>
        /// Speeds at which the curve changes slope, ascending, including cut-in and cut-out.
        ///
        /// <para>Exposed for the integration in <see cref="TurbineYield"/> rather than as a
        /// description of the turbine. A power curve is not smooth - it has a corner at cut-in,
        /// another at rated and a cliff at cut-out - and a quadrature whose intervals straddle
        /// those corners smears them. Integrating segment by segment between these points keeps
        /// every kink on a boundary, where it costs nothing.</para>
        /// </summary>
        public abstract IReadOnlyList<double> Breakpoints { get; }

        private sealed class IdealisedCurve : TurbinePowerCurve
        {
            private readonly double _rated;
            private readonly double _cubicSpan;

            public IdealisedCurve(double cutIn, double rated, double cutOut, double ratedPower)
            {
                if (!(cutIn > 0.0))
                    throw new ArgumentOutOfRangeException(
                        nameof(cutIn),
                        cutIn,
                        "Cut-in speed must be positive."
                    );
                if (!(rated > cutIn))
                    throw new ArgumentOutOfRangeException(
                        nameof(rated),
                        rated,
                        $"Rated speed must exceed cut-in ({cutIn:F1} m/s)."
                    );
                if (!(cutOut > rated))
                    throw new ArgumentOutOfRangeException(
                        nameof(cutOut),
                        cutOut,
                        $"Cut-out speed must exceed rated ({rated:F1} m/s)."
                    );
                if (!(ratedPower > 0.0))
                    throw new ArgumentOutOfRangeException(
                        nameof(ratedPower),
                        ratedPower,
                        "Rated power must be positive."
                    );

                CutInMetersPerSecond = cutIn;
                _rated = rated;
                CutOutMetersPerSecond = cutOut;
                RatedPowerKilowatts = ratedPower;

                _cubicSpan = rated * rated * rated - cutIn * cutIn * cutIn;
                Breakpoints = new[] { cutIn, rated, cutOut };
            }

            public override double RatedPowerKilowatts { get; }

            public override double CutInMetersPerSecond { get; }

            public override double CutOutMetersPerSecond { get; }

            public override IReadOnlyList<double> Breakpoints { get; }

            public override double PowerKilowatts(double speedMetersPerSecond)
            {
                double v = speedMetersPerSecond;

                if (double.IsNaN(v) || v < CutInMetersPerSecond || v > CutOutMetersPerSecond)
                    return 0.0;

                if (v >= _rated)
                    return RatedPowerKilowatts;

                double cutIn = CutInMetersPerSecond;
                return RatedPowerKilowatts * (v * v * v - cutIn * cutIn * cutIn) / _cubicSpan;
            }

            public override string ToString() =>
                $"idealised ({CutInMetersPerSecond:F1} / {_rated:F1} / "
                + $"{CutOutMetersPerSecond:F1} m/s, {RatedPowerKilowatts:N0} kW)";
        }

        private sealed class TabulatedCurve : TurbinePowerCurve
        {
            private readonly double[] _speeds;
            private readonly double[] _powers;

            public TabulatedCurve(
                IEnumerable<(double SpeedMetersPerSecond, double PowerKilowatts)> points
            )
            {
                if (points is null)
                    throw new ArgumentNullException(nameof(points));

                var ordered = points.OrderBy(p => p.SpeedMetersPerSecond).ToList();

                if (ordered.Count < 2)
                    throw new ArgumentException(
                        "A power curve needs at least two points to interpolate between.",
                        nameof(points)
                    );

                for (int i = 0; i < ordered.Count; i++)
                {
                    if (
                        double.IsNaN(ordered[i].SpeedMetersPerSecond)
                        || ordered[i].SpeedMetersPerSecond < 0.0
                    )
                        throw new ArgumentException(
                            $"Speed must be a non-negative number; got {ordered[i].SpeedMetersPerSecond}.",
                            nameof(points)
                        );
                    if (double.IsNaN(ordered[i].PowerKilowatts) || ordered[i].PowerKilowatts < 0.0)
                        throw new ArgumentException(
                            $"Power must be a non-negative number; got {ordered[i].PowerKilowatts} "
                                + $"at {ordered[i].SpeedMetersPerSecond} m/s.",
                            nameof(points)
                        );
                    if (
                        i > 0
                        && ordered[i].SpeedMetersPerSecond == ordered[i - 1].SpeedMetersPerSecond
                    )
                        throw new ArgumentException(
                            $"Two points share the speed {ordered[i].SpeedMetersPerSecond} m/s; a "
                                + "power curve is a function of speed.",
                            nameof(points)
                        );
                }

                _speeds = ordered.Select(p => p.SpeedMetersPerSecond).ToArray();
                _powers = ordered.Select(p => p.PowerKilowatts).ToArray();

                RatedPowerKilowatts = _powers.Max();
                CutOutMetersPerSecond = _speeds[^1];

                // Cut-in is the last tabulated speed still producing nothing, or the first point if
                // the table starts already generating. Reading it off the table rather than taking
                // it as an argument keeps the curve a single source of truth.
                int firstProducing = Array.FindIndex(_powers, p => p > 0.0);
                if (firstProducing < 0)
                    throw new ArgumentException(
                        "Every tabulated point produces zero power.",
                        nameof(points)
                    );

                CutInMetersPerSecond =
                    firstProducing > 0 ? _speeds[firstProducing - 1] : _speeds[0];

                Breakpoints = _speeds;
            }

            public override double RatedPowerKilowatts { get; }

            public override double CutInMetersPerSecond { get; }

            public override double CutOutMetersPerSecond { get; }

            public override IReadOnlyList<double> Breakpoints { get; }

            public override double PowerKilowatts(double speedMetersPerSecond)
            {
                double v = speedMetersPerSecond;

                if (double.IsNaN(v) || v < _speeds[0] || v > _speeds[^1])
                    return 0.0;

                int index = Array.BinarySearch(_speeds, v);
                if (index >= 0)
                    return _powers[index];

                // BinarySearch returns the bitwise complement of the first larger element, so the
                // bracketing interval is [upper - 1, upper] and upper is never 0 or past the end -
                // both of those were excluded by the range check above.
                int upper = ~index;
                int lower = upper - 1;

                double t = (v - _speeds[lower]) / (_speeds[upper] - _speeds[lower]);
                return _powers[lower] + t * (_powers[upper] - _powers[lower]);
            }

            public override string ToString() =>
                $"tabulated ({_speeds.Length} points, {CutInMetersPerSecond:F1} - "
                + $"{CutOutMetersPerSecond:F1} m/s, {RatedPowerKilowatts:N0} kW)";
        }
    }
}

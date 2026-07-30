using WeatherModel.Data;
using WeatherModel.Solar;

namespace WeatherModel.Sample;

/// <summary>
/// Fits Linke turbidity to the station's own cloudless days, and settles whether the Ineichen
/// model should carry the Perez enhancement.
///
/// <para>The two questions have to be answered together, because both scale clear-sky GHI and
/// a turbidity fit will happily absorb whatever the Perez choice does to the magnitude. So
/// overall error cannot separate them. What can is the <b>shape of the residual against air
/// mass</b>: the Perez term is <c>exp(0.01·airmass^1.8)</c>, so picking wrong leaves a residual
/// that slopes with air mass even after turbidity has soaked up the constant part.</para>
/// </summary>
public static class ClearSkyCalibration
{
    /// <summary>Sub-steps per hourly interval when integrating the model over a measurement interval.</summary>
    private const int SubStepsPerInterval = 4;

    /// <summary>
    /// Turbidity search range. Physical values sit between about 2 and 7; the floor is set
    /// below that deliberately so a fit that *wants* an unphysical value reveals it by landing
    /// there, rather than being silently clamped at the edge of a plausible-looking range.
    /// </summary>
    private const double MinTurbidity = 1.0;
    private const double MaxTurbidity = 8.0;
    private const double TurbidityStep = 0.01;

    /// <summary>
    /// Ignore intervals with the sun below this elevation. Refraction, horizon obstructions and
    /// the model's own validity all degrade together down there, and the measured values are
    /// too small to constrain a fit.
    /// </summary>
    private const double MinElevationDegrees = 5.0;

    public static void Run(IReadOnlyList<DwdSolarDay> days, DwdStation station)
    {
        var clearDays = SelectClearDays(days);
        Console.WriteLine($"=== Clear-day calibration set: {clearDays.Count} days ===");
        Console.WriteLine("  criteria: complete, sunshine ≥95% of possible, diffuse fraction <0.30");
        foreach (var group in clearDays.GroupBy(d => d.Date.Month).OrderBy(g => g.Key))
            Console.Write($"  {group.Key:00}:{group.Count(),-4}");
        Console.WriteLine();
        Console.WriteLine();

        var samples = BuildSamples(clearDays, station);
        Console.WriteLine($"Usable clear-sky sub-samples: {samples.Count:N0} " +
                          $"(sun above {MinElevationDegrees}°)");
        Console.WriteLine();

        var withPerez = Calibrate(samples, station, perezEnhancement: true);
        var withoutPerez = Calibrate(samples, station, perezEnhancement: false);

        ReportTurbidity(withPerez, withoutPerez);
        ReportAirMassResidual(samples, station, withPerez, withoutPerez);
        ReportDailyTotals(samples, station, withPerez, withoutPerez);
    }

    /// <summary>
    /// Accuracy on daily totals the quantity the clearness index actually divides by.
    ///
    /// The within-day tilt reported above matters for intra-day profiles, but Kt is a ratio of
    /// daily energies, and the high-air-mass hours that the tilt concerns carry little of a
    /// day's energy. Fitting turbidity per month also absorbs a constant bias by construction.
    /// So this is the number that decides the Perez question for this project's purposes.
    /// </summary>
    private static void ReportDailyTotals(
        IReadOnlyList<ClearSample> samples, DwdStation station, double[] withPerez, double[] withoutPerez)
    {
        Console.WriteLine("=== Daily-total accuracy on clear days (what Kt actually divides by) ===");
        Console.WriteLine("month     Perez on bias   |err|      Perez off bias   |err|    days");

        double onBiasAll = 0.0, onAbsAll = 0.0, offBiasAll = 0.0, offAbsAll = 0.0;
        int dayCount = 0;

        foreach (var monthGroup in samples.GroupBy(s => s.Month).OrderBy(g => g.Key))
        {
            double onBias = 0.0, onAbs = 0.0, offBias = 0.0, offAbs = 0.0;
            int days = 0;

            foreach (var dayGroup in monthGroup.GroupBy(s => s.Date))
            {
                double measured = dayGroup.Sum(s => s.MeasuredWhPerM2);
                if (measured <= 0.0) continue;

                double on = dayGroup.Sum(s => ModelledWhPerM2(s, withPerez[s.Month], station.AltitudeMeters, true));
                double off = dayGroup.Sum(s => ModelledWhPerM2(s, withoutPerez[s.Month], station.AltitudeMeters, false));

                double onRelative = (on - measured) / measured;
                double offRelative = (off - measured) / measured;

                onBias += onRelative;
                onAbs += Math.Abs(onRelative);
                offBias += offRelative;
                offAbs += Math.Abs(offRelative);
                days++;
            }

            if (days == 0) continue;

            Console.WriteLine($"{monthGroup.Key,5} {onBias / days,15:P2} {onAbs / days,8:P2} " +
                              $"{offBias / days,17:P2} {offAbs / days,8:P2} {days,7}");

            onBiasAll += onBias; onAbsAll += onAbs;
            offBiasAll += offBias; offAbsAll += offAbs;
            dayCount += days;
        }

        Console.WriteLine();
        Console.WriteLine($"Overall   Perez on : bias {onBiasAll / dayCount,7:P2}, mean |error| {onAbsAll / dayCount:P2}");
        Console.WriteLine($"          Perez off: bias {offBiasAll / dayCount,7:P2}, mean |error| {offAbsAll / dayCount:P2}");
        Console.WriteLine();
    }

    /// <summary>
    /// Cloudless days. Sunshine duration is the primary signal and diffuse fraction the second
    /// opinion on a genuinely clear day almost all radiation arrives direct, so a high
    /// diffuse fraction betrays thin cirrus that the sunshine sensor may still count.
    /// </summary>
    private static List<DwdSolarDay> SelectClearDays(IReadOnlyList<DwdSolarDay> days) =>
        days.Where(d => d.IsComplete
                        && d.SunshineFraction() >= 0.95
                        && d.DiffuseFraction < 0.30)
            .ToList();

    /// <summary>
    /// One measured hour, decomposed into the sub-steps the model is integrated over.
    /// Solar geometry is precomputed here because it is far more expensive than the Ineichen
    /// evaluation, and the turbidity search re-evaluates the model hundreds of times per sample.
    /// </summary>
    private sealed record ClearSample(
        DateOnly Date,
        int Month,
        int DayOfYear,
        double MeasuredWhPerM2,
        double[] ApparentZenithDegrees,
        double MeanAirMass);

    private static List<ClearSample> BuildSamples(IReadOnlyList<DwdSolarDay> clearDays, DwdStation station)
    {
        var position = new SolarPositionCalculator(station.LatitudeDegrees, station.LongitudeDegrees);
        double pressure = ClearSkyIneichen.PressureFromAltitude(station.AltitudeMeters);
        var samples = new List<ClearSample>();

        foreach (var day in clearDays)
        {
            foreach (var interval in day.Intervals)
            {
                if (interval.GlobalWhPerM2 is not { } measured || measured <= 0.0)
                    continue;

                var duration = interval.EndUtc - interval.StartUtc;
                var zeniths = new double[SubStepsPerInterval];
                double airMassSum = 0.0;
                bool usable = true;

                for (int i = 0; i < SubStepsPerInterval; i++)
                {
                    // Midpoint of each sub-step.
                    var instant = interval.StartUtc + duration * ((i + 0.5) / SubStepsPerInterval);

                    double geometricElevation = 90.0 - position.GeometricZenithDegrees(instant);
                    double elevation = geometricElevation
                                       + DailyClearSkyCalculator.RefractionCorrectionDegrees(geometricElevation);

                    if (elevation < MinElevationDegrees)
                    {
                        usable = false;
                        break;
                    }

                    zeniths[i] = 90.0 - elevation;
                    airMassSum += ClearSkyIneichen.AbsoluteAirMass(zeniths[i], pressure);
                }

                if (!usable) continue;

                samples.Add(new ClearSample(
                    day.Date,
                    day.Date.Month,
                    interval.MidpointUtc.DayOfYear,
                    measured,
                    zeniths,
                    airMassSum / SubStepsPerInterval));
            }
        }

        return samples;
    }

    /// <summary>Modelled irradiation for one measured hour, in Wh/m².</summary>
    private static double ModelledWhPerM2(ClearSample sample, double turbidity, double altitude, bool perez)
    {
        double sum = 0.0;
        foreach (double zenith in sample.ApparentZenithDegrees)
        {
            sum += ClearSkyIneichen.Estimate(
                zenith, sample.DayOfYear, turbidity, altitude,
                pressurePascal: null, perezEnhancement: perez).Ghi;
        }

        // Each sub-step stands for an equal share of the hour.
        return sum / SubStepsPerInterval;
    }

    /// <summary>
    /// Per-month turbidity minimising squared error against measured hourly irradiation.
    /// A plain scan: the objective is smooth and one-dimensional, and the range is narrow.
    /// </summary>
    private static double[] Calibrate(IReadOnlyList<ClearSample> samples, DwdStation station, bool perezEnhancement)
    {
        var fitted = new double[13];

        for (int month = 1; month <= 12; month++)
        {
            var monthSamples = samples.Where(s => s.Month == month).ToList();
            if (monthSamples.Count == 0)
            {
                fitted[month] = double.NaN;
                continue;
            }

            double bestTurbidity = double.NaN;
            double bestError = double.MaxValue;

            for (double turbidity = MinTurbidity; turbidity <= MaxTurbidity; turbidity += TurbidityStep)
            {
                double error = 0.0;
                foreach (var sample in monthSamples)
                {
                    double residual = ModelledWhPerM2(sample, turbidity, station.AltitudeMeters, perezEnhancement)
                                      - sample.MeasuredWhPerM2;
                    error += residual * residual;
                }

                if (error < bestError)
                {
                    bestError = error;
                    bestTurbidity = turbidity;
                }
            }

            fitted[month] = bestTurbidity;
        }

        return fitted;
    }

    private static void ReportTurbidity(double[] withPerez, double[] withoutPerez)
    {
        Console.WriteLine("=== Fitted Linke turbidity ===");
        Console.WriteLine("month   Perez on   Perez off   placeholder");

        for (int month = 1; month <= 12; month++)
        {
            var midMonth = new DateTime(2015, month, 15);
            Console.WriteLine($"{month,5} {withPerez[month],10:F2} {withoutPerez[month],11:F2} " +
                              $"{LinkeTurbidityTable.CentralEuropePlaceholder.Interpolated(midMonth),13:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("Physical values sit around 2-7, low in winter and high in summer.");
        Console.WriteLine();
    }

    /// <summary>
    /// The discriminator: how much the relative residual tilts with air mass <b>within a single
    /// month</b>.
    ///
    /// <para>Measuring the tilt across the whole year would not work, because turbidity is
    /// fitted per month and air mass is strongly confounded with season at 51°N the sun never
    /// gets above about 15° in December, so winter lives entirely at high air mass. A pooled
    /// air-mass profile is therefore partly just a seasonal profile.</para>
    ///
    /// <para>Within one month the fitted turbidity is a single constant, so any remaining slope
    /// against air mass is attributable to the model's air-mass dependence which is precisely
    /// what the Perez term changes. Note that winter months cannot discriminate at all: they
    /// have no low-air-mass samples to contrast against. The summer months carry the verdict.</para>
    /// </summary>
    private static void ReportAirMassResidual(
        IReadOnlyList<ClearSample> samples, DwdStation station, double[] withPerez, double[] withoutPerez)
    {
        Console.WriteLine("=== Within-month tilt of the residual against air mass ===");
        Console.WriteLine("tilt = mean residual at air mass >3  −  mean residual at air mass <2");
        Console.WriteLine();
        Console.WriteLine("month   Perez on   Perez off     n(<2)   n(>3)");

        double onTiltSum = 0.0, offTiltSum = 0.0;
        int discriminatingMonths = 0;

        for (int month = 1; month <= 12; month++)
        {
            var low = samples.Where(s => s.Month == month && s.MeanAirMass < 2.0).ToList();
            var high = samples.Where(s => s.Month == month && s.MeanAirMass > 3.0).ToList();

            if (low.Count < 30 || high.Count < 30)
            {
                Console.WriteLine($"{month,5}        --          --  {low.Count,8} {high.Count,7}" +
                                  "   (no air-mass contrast)");
                continue;
            }

            double onTilt = MeanResidual(high, station, withPerez, true)
                            - MeanResidual(low, station, withPerez, true);
            double offTilt = MeanResidual(high, station, withoutPerez, false)
                             - MeanResidual(low, station, withoutPerez, false);

            Console.WriteLine($"{month,5} {onTilt,10:P2} {offTilt,11:P2} {low.Count,8} {high.Count,7}");

            onTiltSum += Math.Abs(onTilt);
            offTiltSum += Math.Abs(offTilt);
            discriminatingMonths++;
        }

        Console.WriteLine();
        if (discriminatingMonths > 0)
        {
            Console.WriteLine($"Mean |tilt| over {discriminatingMonths} discriminating months:");
            Console.WriteLine($"  Perez on  : {onTiltSum / discriminatingMonths:P2}");
            Console.WriteLine($"  Perez off : {offTiltSum / discriminatingMonths:P2}");
            Console.WriteLine();
            Console.WriteLine("Smaller wins it means the model's air-mass dependence matches the sky's.");
        }

        Console.WriteLine();
    }

    private static double MeanResidual(
        IReadOnlyList<ClearSample> samples, DwdStation station, double[] turbidity, bool perez)
    {
        double sum = 0.0;
        foreach (var sample in samples)
        {
            sum += (ModelledWhPerM2(sample, turbidity[sample.Month], station.AltitudeMeters, perez)
                    - sample.MeasuredWhPerM2) / sample.MeasuredWhPerM2;
        }

        return sum / samples.Count;
    }
}

using WeatherSynth.Data;
using WeatherSynth.Solar;

namespace WeatherSynth.Sample;

/// <summary>
/// Validates this project's solar-position calculation against the ZENIT column DWD ships
/// alongside its radiation measurements.
///
/// The comparison is against the GEOMETRIC zenith: DWD's value carries no refraction
/// correction, so comparing it to the apparent zenith would show a spurious error that grows
/// towards the horizon.
/// </summary>
public static class ZenithValidation
{
    public static void Run(IReadOnlyList<DwdSolarInterval> intervals, DwdStation station)
    {
        Console.WriteLine("=== Solar position vs DWD ZENIT column ===");
        Console.WriteLine(
            $"Station {station.Id} {station.Name}  "
                + $"{station.LatitudeDegrees:F4}°N {station.LongitudeDegrees:F4}°E"
        );
        Console.WriteLine($"Samples: {intervals.Count:N0}");
        Console.WriteLine();

        var calculator = Build(station);

        double sumSquared = 0.0;
        double sumSigned = 0.0;
        double maxAbs = 0.0;
        DateTimeOffset worstAt = default;

        var monthlyBias = new double[13];
        var monthlyCount = new int[13];

        // Bucket by zenith to expose any error that grows towards the horizon the signature
        // of a refraction or air-mass mix-up rather than a coordinate error.
        var zenithBias = new double[10];
        var zenithCount = new int[10];

        foreach (var interval in intervals)
        {
            double computed = calculator.GeometricZenithDegrees(interval.MidpointUtc);
            double error = computed - interval.ZenithDegrees;

            sumSquared += error * error;
            sumSigned += error;

            if (Math.Abs(error) > maxAbs)
            {
                maxAbs = Math.Abs(error);
                worstAt = interval.MidpointUtc;
            }

            int month = interval.MidpointUtc.Month;
            monthlyBias[month] += error;
            monthlyCount[month]++;

            int bucket = Math.Clamp((int)(interval.ZenithDegrees / 18.0), 0, 9);
            zenithBias[bucket] += error;
            zenithCount[bucket]++;
        }

        int n = intervals.Count;
        Console.WriteLine($"RMSE      : {Math.Sqrt(sumSquared / n):F5}°");
        Console.WriteLine($"Mean bias : {sumSigned / n:+0.00000;-0.00000}°");
        Console.WriteLine($"Max error : {maxAbs:F5}°  at {worstAt:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine();

        Console.WriteLine(
            "Mean bias by month (a seasonal swing means the equation of time is wrong):"
        );
        for (int m = 1; m <= 12; m++)
            Console.WriteLine(
                $"  {m, 2}: {monthlyBias[m] / monthlyCount[m], +9:F5}°  (n={monthlyCount[m]:N0})"
            );
        Console.WriteLine();

        Console.WriteLine(
            "Mean bias by zenith band (growth towards the horizon means refraction):"
        );
        for (int b = 0; b < 10; b++)
        {
            if (zenithCount[b] == 0)
                continue;
            Console.WriteLine(
                $"  {b * 18, 3}-{(b + 1) * 18, 3}°: {zenithBias[b] / zenithCount[b], +9:F5}°  (n={zenithCount[b]:N0})"
            );
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Recovers the station's coordinates from the ZENIT column itself, by scanning a grid
    /// around the nominal position and keeping whichever latitude/longitude minimises the
    /// residual. Confirms the metadata rather than taking it on trust.
    /// </summary>
    public static void FitCoordinates(IReadOnlyList<DwdSolarInterval> intervals, DwdStation station)
    {
        Console.WriteLine("=== Fitting station coordinates from ZENIT ===");

        // Subsample: the residual surface is smooth, so a few thousand points locate the
        // minimum just as well as all 151k and keeps the scan interactive.
        var sample = intervals.Where((_, i) => i % 37 == 0).ToList();

        double bestLat = station.LatitudeDegrees;
        double bestLon = station.LongitudeDegrees;
        double bestRmse = double.MaxValue;

        // Coarse pass to find the basin, then successive refinement. The residual surface is
        // smooth and has a single minimum, so this converges quickly.
        foreach (double stepDegrees in new[] { 0.01, 0.002, 0.0005 })
        {
            const int stepsPerSide = 30;

            double centreLat = bestLat;
            double centreLon = bestLon;

            var grid =
                from iLat in Enumerable.Range(-stepsPerSide, 2 * stepsPerSide + 1)
                from iLon in Enumerable.Range(-stepsPerSide, 2 * stepsPerSide + 1)
                select (Lat: centreLat + iLat * stepDegrees, Lon: centreLon + iLon * stepDegrees);

            var best = grid.AsParallel()
                .Select(p =>
                    (
                        p.Lat,
                        p.Lon,
                        Rmse: Rmse(
                            sample,
                            station with
                            {
                                LatitudeDegrees = p.Lat,
                                LongitudeDegrees = p.Lon,
                            }
                        )
                    )
                )
                .MinBy(r => r.Rmse);

            bestLat = best.Lat;
            bestLon = best.Lon;
            bestRmse = best.Rmse;

            Console.WriteLine(
                $"  step {stepDegrees:F4}° -> {bestLat:F4}°N {bestLon:F4}°E  RMSE {bestRmse:F5}°"
            );
        }

        Console.WriteLine();

        double nominalRmse = Rmse(sample, station);
        Console.WriteLine(
            $"Nominal  : {station.LatitudeDegrees:F4}°N {station.LongitudeDegrees:F4}°E  RMSE {nominalRmse:F5}°"
        );
        Console.WriteLine($"Best fit : {bestLat:F4}°N {bestLon:F4}°E  RMSE {bestRmse:F5}°");
        Console.WriteLine(
            $"Offset   : {bestLat - station.LatitudeDegrees:+0.0000;-0.0000}° lat, "
                + $"{bestLon - station.LongitudeDegrees:+0.0000;-0.0000}° lon"
        );
        Console.WriteLine();
    }

    private static double Rmse(IReadOnlyList<DwdSolarInterval> intervals, DwdStation station)
    {
        var calculator = Build(station);
        double sumSquared = 0.0;

        foreach (var interval in intervals)
        {
            double error =
                calculator.GeometricZenithDegrees(interval.MidpointUtc) - interval.ZenithDegrees;
            sumSquared += error * error;
        }

        return Math.Sqrt(sumSquared / intervals.Count);
    }

    private static DailyClearSkyCalculator Build(DwdStation station) =>
        new(
            station.LatitudeDegrees,
            station.LongitudeDegrees,
            station.AltitudeMeters,
            TimeZoneInfo.Utc
        );
}

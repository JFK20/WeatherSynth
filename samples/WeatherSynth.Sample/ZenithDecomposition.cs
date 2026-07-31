using Innovative.SolarCalculator;
using WeatherSynth.Data;

namespace WeatherSynth.Sample;

/// <summary>
/// Splits the zenith residual into its two independent causes: the sun's declination
/// (a date/season quantity) and its hour angle (a time-of-day quantity).
///
/// This is possible because MESS_DATUM_WOZ is <i>true solar time</i>, so the hour angle at
/// each interval midpoint is exact by definition no equation of time required. With the hour
/// angle known, DWD's published zenith can be inverted for the declination it must have been
/// computed from, and that can be compared against the library's declination directly.
///
/// Reading the result:
///   * declination error dominates  → a date/epoch problem in the solar-position algorithm
///   * hour-angle error dominates   → equation of time, longitude, or timestamp convention
/// </summary>
public static class ZenithDecomposition
{
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    public static void Run(IReadOnlyList<DwdSolarInterval> intervals, DwdStation station)
    {
        Console.WriteLine("=== Decomposing the zenith residual ===");
        Console.WriteLine();

        double phi = station.LatitudeDegrees * DegToRad;

        var declErrorByMonth = new double[13];
        var eotErrorByMonth = new double[13];
        var countByMonth = new int[13];

        // Restrict to daylight: inverting for declination is ill-conditioned when the sun is
        // far below the horizon, and those intervals carry no radiation anyway.
        foreach (var interval in intervals.Where(i => i.IsDaylight))
        {
            var solarTimes = new SolarTimes(
                interval.MidpointUtc,
                station.LatitudeDegrees,
                station.LongitudeDegrees
            );

            // --- Declination ---
            double libraryDeclination = (double)solarTimes.SolarDeclination.Radians * RadToDeg;
            double dwdDeclination = ImpliedDeclinationDegrees(
                interval.ZenithDegrees,
                interval.HourAngleDegrees,
                phi
            );

            // --- Equation of time ---
            // True solar time = UTC + longitude correction + equation of time.
            double longitudeCorrectionMinutes = station.LongitudeDegrees / 15.0 * 60.0;
            double dwdEotMinutes =
                (interval.WozMidpoint - interval.MidpointUtc.UtcDateTime).TotalMinutes
                - longitudeCorrectionMinutes;
            double libraryEotMinutes = (double)solarTimes.EquationOfTime;

            int month = interval.MidpointUtc.Month;
            if (!double.IsNaN(dwdDeclination))
            {
                declErrorByMonth[month] += libraryDeclination - dwdDeclination;
                eotErrorByMonth[month] += libraryEotMinutes - dwdEotMinutes;
                countByMonth[month]++;
            }
        }

        Console.WriteLine("            declination error      equation-of-time error");
        Console.WriteLine("month       (library − DWD)        (library − DWD)");
        double totalDecl = 0.0,
            totalEot = 0.0;
        int totalCount = 0;

        for (int m = 1; m <= 12; m++)
        {
            if (countByMonth[m] == 0)
                continue;
            double decl = declErrorByMonth[m] / countByMonth[m];
            double eot = eotErrorByMonth[m] / countByMonth[m];

            Console.WriteLine($"{m, 4}   {decl, +16:F4}°   {eot, +18:F3} min");

            totalDecl += declErrorByMonth[m];
            totalEot += eotErrorByMonth[m];
            totalCount += countByMonth[m];
        }

        Console.WriteLine();
        Console.WriteLine($"Mean declination error : {totalDecl / totalCount:+0.0000;-0.0000}°");
        Console.WriteLine(
            $"Mean equation-of-time  : {totalEot / totalCount:+0.000;-0.000} min "
                + $"(1 min ≈ 0.25° of hour angle)"
        );
        Console.WriteLine();

        ProbeDeclinationWithinADay(station);
    }

    /// <summary>
    /// Prints the library's declination through a single equinox day, where declination moves
    /// fastest (~0.39°/day) and any date-handling error is most visible.
    ///
    /// If the value is constant across the day, declination is being evaluated once per date
    /// rather than at the requested instant which would show up in the monthly table as an
    /// error proportional to dδ/dt, i.e. largest at the equinoxes and zero at the solstices.
    /// </summary>
    private static void ProbeDeclinationWithinADay(DwdStation station)
    {
        Console.WriteLine(
            "=== Library declination through 2015-03-20 (equinox, fastest change) ==="
        );

        foreach (int hour in new[] { 0, 6, 12, 18 })
        {
            var instant = new DateTimeOffset(2015, 3, 20, hour, 0, 0, TimeSpan.Zero);
            var solarTimes = new SolarTimes(
                instant,
                station.LatitudeDegrees,
                station.LongitudeDegrees
            );
            Console.WriteLine(
                $"  {hour:00}:00 UTC : {(double)solarTimes.SolarDeclination.Radians * RadToDeg, +9:F4}°"
            );
        }

        Console.WriteLine();
        Console.WriteLine("  For reference the 2015 March equinox fell on 2015-03-20 22:45 UTC,");
        Console.WriteLine("  so the true declination at 12:00 UTC that day was about −0.17°.");
        Console.WriteLine();
    }

    /// <summary>
    /// Recovers the declination implied by a published zenith angle and a known hour angle.
    ///
    /// Solves <c>cos z = sin φ sin δ + cos φ cos δ cos H</c> for δ by collapsing the right-hand
    /// side into a single sinusoid: with <c>a = sin φ</c> and <c>b = cos φ cos H</c>, it becomes
    /// <c>R·sin(δ + ψ) = cos z</c> where <c>R = √(a² + b²)</c> and <c>ψ = atan2(b, a)</c>.
    /// </summary>
    private static double ImpliedDeclinationDegrees(
        double zenithDegrees,
        double hourAngleDegrees,
        double phiRadians
    )
    {
        double a = Math.Sin(phiRadians);
        double b = Math.Cos(phiRadians) * Math.Cos(hourAngleDegrees * DegToRad);

        double r = Math.Sqrt(a * a + b * b);
        double psi = Math.Atan2(b, a);

        double ratio = Math.Cos(zenithDegrees * DegToRad) / r;
        if (ratio is < -1.0 or > 1.0)
            return double.NaN;

        double declination = (Math.Asin(ratio) - psi) * RadToDeg;

        // Keep the physical branch: declination never leaves ±23.44°.
        return Math.Abs(declination) <= 23.5 ? declination : double.NaN;
    }
}

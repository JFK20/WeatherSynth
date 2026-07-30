using System;
using Innovative.SolarCalculator;
using WeatherModel.Solar;

internal static class Program
{
    private static void Main()
    {
        // Köln
        const double latitude = 51.02095;
        const double longitude = 6.89422;
        const double altitude = 50.0;

        TimeZoneInfo tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        
        var calculator = new DailyClearSkyCalculator(latitude, longitude, altitude, tz);
        
        double scaled = ClearSkyIneichen.PressureFromAltitude(altitude);
        
        Console.WriteLine($"Pressure at {altitude} m: {scaled:F0} Pa");

        double realtivaitmas = ClearSkyIneichen.RelativeAirMass(90);
        
        Console.WriteLine($"Relative air mass at 90° zenith: {realtivaitmas:F2}");

        // ---------------------------------------------------------------
        // 1. Instantaneous value — clear-sky GHI right now
        // ---------------------------------------------------------------
        var now = new DateTimeOffset(DateTime.Now, tz.GetUtcOffset(DateTime.Now));
        var sample = calculator.At(now);

        Console.WriteLine("=== Instantaneous ===");
        Console.WriteLine($"Time            : {sample.LocalTime:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"Apparent zenith : {sample.ApparentZenithDegrees:F2}°");
        Console.WriteLine($"Clear-sky GHI   : {sample.Irradiance.Ghi:F1} W/m²");
        Console.WriteLine($"Clear-sky DNI   : {sample.Irradiance.Dni:F1} W/m²");
        Console.WriteLine($"Clear-sky DHI   : {sample.Irradiance.Dhi:F1} W/m²");
        Console.WriteLine();

        // ---------------------------------------------------------------
        // 2. Daily total — the ceiling for the stochastic model
        // ---------------------------------------------------------------
        var today = calculator.ForDate(DateTime.Today);

        Console.WriteLine("=== Today (clear-sky ceiling) ===");
        Console.WriteLine($"Daily GHI  : {today.GhiKWhPerM2:F2} kWh/m²");
        Console.WriteLine($"Peak GHI   : {today.PeakGhiWPerM2:F0} W/m² (at solar noon)");
        Console.WriteLine($"Diffuse    : {today.DhiWhPerM2 / 1000.0:F2} kWh/m²");
        Console.WriteLine();

        // ---------------------------------------------------------------
        // 3. Sanity check — solstices should bracket everything else.
        //    At ~51°N expect roughly 8 kWh/m² in June, ~1 kWh/m² in December.
        //    If your numbers are far off these, something is wrong with the
        //    time zone, the coordinate signs, or the zenith angle units.
        // ---------------------------------------------------------------
        int year = DateTime.Today.Year;

        Console.WriteLine("=== Sanity check: annual range ===");
        foreach (var date in new[]
                 {
                     new DateTime(year, 3, 20),  // spring equinox
                     new DateTime(year, 6, 21),  // summer solstice
                     new DateTime(year, 9, 22),  // autumn equinox
                     new DateTime(year, 12, 21)  // winter solstice
                 })
        {
            var result = calculator.ForDate(date);
            var solarTimes = new SolarTimes(date, latitude, longitude);

            Console.WriteLine(
                $"{date:MMM dd}: {result.GhiKWhPerM2,5:F2} kWh/m²   " +
                $"peak {result.PeakGhiWPerM2,4:F0} W/m²   " +
                $"daylight {solarTimes.SunlightDuration:hh\\:mm}");
        }
        Console.WriteLine();

        // ---------------------------------------------------------------
        // 4. Where the stochastic part plugs in
        // ---------------------------------------------------------------
        // The clear-sky value above is the ceiling. Your synthetic day is simply:
        //
        //     double kt = SampleClearnessIndex(date);   // from historical Kt distribution
        //     double syntheticGhi = kt * today.GhiKWhPerM2;
        //
        // Kt for daily totals realistically spans about 0.05 (thick overcast) to 0.75
        // (cloudless). Clamp the result to [0, clear-sky] as a final safety net.
        const double exampleKt = 0.42; // stand-in for a sampled value
        double synthetic = Math.Min(exampleKt * today.GhiKWhPerM2, today.GhiKWhPerM2);

        Console.WriteLine("=== Synthetic day (example Kt) ===");
        Console.WriteLine($"Kt = {exampleKt:F2}  ->  {synthetic:F2} kWh/m²");
    }
}
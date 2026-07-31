using Innovative.SolarCalculator;
using WeatherSynth.Solar;

namespace WeatherSynth.Sample;

/// <summary>
/// The original sanity harness: instantaneous clear-sky values plus the four
/// equinox/solstice daily totals. Re-run this after any change to the clear-sky model
/// particularly after re-fitting Linke turbidity to confirm the magnitudes in
/// knowledge.md §7 still hold.
/// </summary>
public static class ClearSkySanity
{
    public static void Run(double latitude, double longitude, double altitude, string timeZoneId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var calculator = new DailyClearSkyCalculator(latitude, longitude, altitude, tz);

        Console.WriteLine(
            $"=== Site: {latitude:F4}°N {longitude:F4}°E, {altitude:F0} m, {timeZoneId} ==="
        );
        Console.WriteLine(
            $"Pressure at {altitude} m       : {ClearSkyIneichen.PressureFromAltitude(altitude):F0} Pa"
        );
        Console.WriteLine(
            $"Relative air mass at 90° zenith: {ClearSkyIneichen.RelativeAirMass(90):F2}"
        );
        Console.WriteLine();

        var now = new DateTimeOffset(DateTime.Now, tz.GetUtcOffset(DateTime.Now));
        var sample = calculator.At(now);

        Console.WriteLine("=== Instantaneous ===");
        Console.WriteLine($"Time            : {sample.LocalTime:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"Apparent zenith : {sample.ApparentZenithDegrees:F2}°");
        Console.WriteLine($"Clear-sky GHI   : {sample.Irradiance.Ghi:F1} W/m²");
        Console.WriteLine($"Clear-sky DNI   : {sample.Irradiance.Dni:F1} W/m²");
        Console.WriteLine($"Clear-sky DHI   : {sample.Irradiance.Dhi:F1} W/m²");
        Console.WriteLine();

        // At ~51°N expect roughly 8 kWh/m² in June and ~1 kWh/m² in December. Numbers far from
        // these mean a longitude sign flip, a time zone mismatch, or degree/radian confusion
        // check those three before suspecting the physics.
        int year = DateTime.Today.Year;

        Console.WriteLine("=== Annual range (clear-sky ceiling) ===");
        foreach (
            var date in new[]
            {
                new DateTime(year, 3, 20), // spring equinox
                new DateTime(year, 6, 21), // summer solstice
                new DateTime(year, 9, 22), // autumn equinox
                new DateTime(year, 12, 21), // winter solstice
            }
        )
        {
            var result = calculator.ForDate(date);
            var solarTimes = new SolarTimes(date, latitude, longitude);

            Console.WriteLine(
                $"{date:MMM dd}: {result.GhiKWhPerM2, 5:F2} kWh/m²   "
                    + $"peak {result.PeakGhiWPerM2, 4:F0} W/m²   "
                    + $"daylight {solarTimes.SunlightDuration:hh\\:mm}"
            );
        }
        Console.WriteLine();
    }
}

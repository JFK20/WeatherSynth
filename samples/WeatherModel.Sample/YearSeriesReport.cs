using WeatherModel.Climate;
using WeatherModel.Data;

namespace WeatherModel.Sample;

/// <summary>
/// Writes one synthetic year at daily resolution
///
/// <para>Everything else in this harness measures the model. This prints what it is for: a
/// plausible year of daily irradiation that never happened, one line per day.</para>
///
/// <para>The generating is all <see cref="SyntheticSolarProvider"/>; what is left here is
/// formatting. That is the point - this command is also the worked example of how a caller
/// asks the library for a year.</para>
/// </summary>
public static class YearSeriesReport
{
    public static void Run(IReadOnlyList<DwdSolarDay> days, DwdStation station, string[] args)
    {
        int year = ArgumentAt(args, 1) ?? DateTime.UtcNow.Year;
        int seed = ArgumentAt(args, 2) ?? IndexFitReport.Seed;

        var provider = SyntheticSolarProvider.FromStationDays(days, station);
        var generated = provider.GenerateYear(year, seed);

        Console.WriteLine($"=== Synthetic {generated.Year} at {station.Name}, seed {generated.Seed} ===");
        Console.WriteLine("Fitted at Bochum, generated on Bochum's clear-sky ceiling. This is one");
        Console.WriteLine("realisation, not a forecast: another seed is an equally plausible year.");
        Console.WriteLine();
        Console.WriteLine("date          index   clear-sky kWh/m²   GHI kWh/m²");

        foreach (var day in generated.Days)
        {
            Console.WriteLine($"{day.Date:yyyy-MM-dd} {day.ClearSkyIndex,9:F3} " +
                              $"{day.ClearSkyWhPerM2 / 1000.0,15:F3} {day.GhiKWhPerM2,12:F3}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Monthly totals ===");
        Console.WriteLine("month   days   mean index   GHI kWh/m²");

        foreach (var month in generated.Months)
        {
            Console.WriteLine($"{month.Month,5} {month.Days,6} {month.MeanClearSkyIndex,12:F3} " +
                              $"{month.GhiKWhPerM2,12:F1}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {generated.Days.Count} days, annual total {generated.GhiKWhPerM2:F0} kWh/m², " +
                          $"mean index {generated.MeanClearSkyIndex:F3}");
        Console.WriteLine($"  Against a clear-sky ceiling of {generated.ClearSkyKWhPerM2:F0} kWh/m², " +
                          $"so {generated.ClearSkyFraction:P1} of the year's available energy.");
    }

    /// <summary>Optional positional integer argument; null when absent, and a hard error when unparseable.</summary>
    private static int? ArgumentAt(string[] args, int index)
    {
        if (args.Length <= index) return null;

        if (!int.TryParse(args[index], out int value))
            throw new ArgumentException($"'{args[index]}' is not an integer.", nameof(args));

        return value;
    }
}

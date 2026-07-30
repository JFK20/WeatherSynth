using WeatherModel.Data;

namespace WeatherModel.Sample;

/// <summary>
/// Describes what actually came out of the station file: coverage, gaps, and daily totals.
/// Run this after any change to the reader the monthly maxima are a direct unit check.
/// </summary>
public static class DataSummary
{
    public static void Run(IReadOnlyList<DwdSolarInterval> intervals, IReadOnlyList<DwdSolarDay> days)
    {
        Console.WriteLine("=== Coverage ===");
        Console.WriteLine($"First interval : {intervals[0].StartUtc:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"Last  interval : {intervals[^1].EndUtc:yyyy-MM-dd HH:mm} UTC");

        int missingGlobal = intervals.Count(i => i.GlobalWhPerM2 is null);
        Console.WriteLine($"Missing global : {missingGlobal:N0} of {intervals.Count:N0} " +
                          $"({100.0 * missingGlobal / intervals.Count:F1}%)");
        Console.WriteLine();

        Console.WriteLine("=== Calendar gaps (these must break Markov chains later) ===");
        for (int i = 1; i < days.Count; i++)
        {
            int skipped = days[i].Date.DayNumber - days[i - 1].Date.DayNumber - 1;
            if (skipped > 0)
                Console.WriteLine($"  {days[i - 1].Date} -> {days[i].Date}  ({skipped} days absent)");
        }
        Console.WriteLine();

        Console.WriteLine("=== Daily GHI by month, complete days only ===");
        Console.WriteLine("month  days   mean kWh/m²   max kWh/m²");

        var complete = days.Where(d => d.IsComplete).ToList();
        foreach (var group in complete.GroupBy(d => d.Date.Month).OrderBy(g => g.Key))
        {
            Console.WriteLine($"{group.Key,5} {group.Count(),5} {group.Average(d => d.GlobalWhPerM2) / 1000.0,13:F2} " +
                              $"{group.Max(d => d.GlobalWhPerM2) / 1000.0,12:F2}");
        }

        // Weight by calendar month rather than averaging over complete days directly: outages
        // are seasonal (December has 401 usable days against May's 545), so a plain day-average
        // over-weights summer and overstates the annual total by roughly 7%.
        double annualTotal = complete
            .GroupBy(d => d.Date.Month)
            .Sum(g => g.Average(d => d.GlobalWhPerM2) / 1000.0 * DateTime.DaysInMonth(2001, g.Key));

        Console.WriteLine();
        Console.WriteLine($"Annual GHI : {annualTotal:F0} kWh/m²/yr, month-weighted " +
                          $"(NRW should be near 1050)");
        Console.WriteLine();

        Console.WriteLine("=== Clear-day candidates (calibration set for Phase 3) ===");
        foreach (double threshold in new[] { 0.95, 0.90, 0.80 })
        {
            var clear = complete.Where(d => d.SunshineFraction() >= threshold).ToList();
            if (clear.Count == 0) continue;

            Console.WriteLine($"  sunshine ≥{threshold:P0}: {clear.Count,4} days, " +
                              $"mean diffuse fraction {clear.Average(d => d.DiffuseFraction):F3}");
        }
        Console.WriteLine();
    }
}

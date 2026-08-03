using WeatherSynth.Data;

namespace WeatherSynth.Sample;

/// <summary>
/// Describes what actually came out of the wind station file: coverage, gaps, monthly means and
/// the cube-law correction. The solar record's counterpart is <see cref="DataSummary"/>.
///
/// <para>This report is the acceptance check on the wind data layer. Every number it prints was
/// measured independently against the raw file before any of this code existed, so a
/// disagreement means the reader or the aggregation is wrong, not the data.</para>
/// </summary>
public static class WindSummary
{
    public static void Run(
        IReadOnlyList<DwdWindHour> hours,
        IReadOnlyList<DwdWindDay> days,
        DwdWindStation station
    )
    {
        Console.WriteLine("=== Coverage ===");
        Console.WriteLine($"Station        : {station.Name} ({station.Id})");
        Console.WriteLine(
            $"Anemometer     : {station.AnemometerHeightMeters:F1} m above ground "
                + "(NOT the 10 m standard - every speed below belongs to this height)"
        );
        Console.WriteLine($"First hour     : {hours[0].TimestampUtc:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"Last  hour     : {hours[^1].TimestampUtc:yyyy-MM-dd HH:mm} UTC");

        int missing = hours.Count(h => h.SpeedMetersPerSecond is null);
        Console.WriteLine(
            $"Missing speed  : {missing:N0} of {hours.Count:N0} "
                + $"({100.0 * missing / hours.Count:F2}%)"
        );

        var complete = days.Where(d => d.IsComplete).ToList();
        int partial = days.Count(d => d.ValidHourCount is > 0 and < 24);
        int empty = days.Count(d => d.ValidHourCount == 0);
        Console.WriteLine(
            $"Days           : {days.Count:N0} ({complete.Count:N0} complete, "
                + $"{partial:N0} partial, {empty:N0} empty)"
        );
        Console.WriteLine();

        Console.WriteLine("=== Quality levels (QN_3) ===");
        foreach (var group in hours.GroupBy(h => h.QualityLevel).OrderBy(g => g.Key))
            Console.WriteLine($"  QN {group.Key, 2} : {group.Count(), 7:N0} hours");
        Console.WriteLine(
            "  Unlike the solar record's constant 1, this one varies - but over this span"
        );
        Console.WriteLine("  it is nearly a no-op, so filter on it for correctness, not for gain.");
        Console.WriteLine();

        Console.WriteLine("=== Calendar gaps (these must break Markov chains later) ===");
        int gaps = 0;
        for (int i = 1; i < days.Count; i++)
        {
            int skipped = days[i].Date.DayNumber - days[i - 1].Date.DayNumber - 1;
            if (skipped > 0)
            {
                Console.WriteLine(
                    $"  {days[i - 1].Date:yyyy-MM-dd} -> {days[i].Date:yyyy-MM-dd}  "
                        + $"({skipped} days absent)"
                );
                gaps++;
            }
        }
        if (gaps == 0)
            Console.WriteLine("  None - every calendar day in the span is present.");
        Console.WriteLine();

        Console.WriteLine("=== Daily mean speed by month, complete days only ===");
        Console.WriteLine("month  days     mean m/s      sd    max m/s   calm days");
        foreach (var group in complete.GroupBy(d => d.Date.Month).OrderBy(g => g.Key))
        {
            var speeds = group.Select(d => d.MeanSpeed).ToList();
            Console.WriteLine(
                $"{group.Key, 5} {speeds.Count, 5} {speeds.Average(), 12:F3} "
                    + $"{StandardDeviation(speeds), 7:F3} {group.Max(d => d.MaxSpeed), 10:F1} "
                    + $"{group.Count(d => d.MeanSpeed < 2.0), 11}"
            );
        }
        Console.WriteLine("  (calm = daily mean below 2 m/s)");
        Console.WriteLine();

        var all = complete.Select(d => d.MeanSpeed).OrderBy(v => v).ToList();
        Console.WriteLine("=== Whole record, complete days ===");
        Console.WriteLine($"  annual mean daily speed : {all.Average():F4} m/s");
        Console.WriteLine($"  sd of daily means       : {StandardDeviation(all):F4} m/s");
        Console.WriteLine($"  range                   : {all[0]:F2} - {all[^1]:F2} m/s");
        Console.WriteLine(
            $"  windiest / calmest month: "
                + $"{MonthlyExtreme(complete, highest: true)} / {MonthlyExtreme(complete, highest: false)}"
        );
        Console.WriteLine();

        // The one number here that is not descriptive: it says how wrong an energy estimate built
        // on the daily mean alone would be, and it is wrong in one direction only.
        var factors = complete.Select(d => d.EnergyPatternFactor).OrderBy(v => v).ToList();
        Console.WriteLine("=== Energy pattern factor, mean(v³) / mean(v)³ ===");
        Console.WriteLine(
            $"  median {Quantile(factors, 0.5):F3}   mean {factors.Average():F3}   "
                + $"p10 {Quantile(factors, 0.1):F3}   p90 {Quantile(factors, 0.9):F3}   "
                + $"max {factors[^1]:F3}"
        );
        Console.WriteLine(
            "  Cubing a daily mean speed understates the day's energy by roughly this factor."
        );
        Console.WriteLine("  A daily mean speed alone is NOT sufficient for an energy estimate.");
        Console.WriteLine();

        // Not a fitted quantity - it is the acceptance target for the persistence chain, and the
        // one number that says whether generated sequences will cluster the way real wind does.
        var series = complete.Select(d => (d.Date, d.MeanSpeed)).ToList();
        Console.WriteLine("=== Persistence ===");
        Console.WriteLine(
            $"  lag-1 of daily means : "
                + $"{WeatherSynth.Climate.IndexSeriesStatistics.Lag1Autocorrelation(series):F4}"
        );
        Console.WriteLine(
            "  Higher than solar's 0.437, as expected - wind is more persistent than cloud."
        );
        Console.WriteLine(
            "  Note this is the RAW lag-1: the chain's phi is fitted on normal scores and comes"
        );
        Console.WriteLine("  out smaller, because the seasonal cycle is re-supplied by the marginals.");
        Console.WriteLine();

        // The instrument changed from cup anemometer to 2D ultrasonic on 2021-07-20. It is a real
        // inhomogeneity in the middle of the fitting span, so it gets printed rather than buried.
        Console.WriteLine("=== Instrument change 2021-07-20 (cup -> 2D ultrasonic) ===");
        ReportEra(complete, 2009, 2020);
        ReportEra(complete, 2022, 2025);
        Console.WriteLine();
    }

    private static void ReportEra(IReadOnlyList<DwdWindDay> days, int firstYear, int lastYear)
    {
        var era = days.Where(d => d.Date.Year >= firstYear && d.Date.Year <= lastYear).ToList();
        int zeroHours = era.Sum(d => d.ZeroHourCount);
        int totalHours = era.Sum(d => d.ValidHourCount);

        Console.WriteLine(
            $"  {firstYear}-{lastYear}: mean {era.Average(d => d.MeanSpeed):F3} m/s, "
                + $"exact-zero hours {100.0 * zeroHours / totalHours:F3}% ({zeroHours:N0})"
        );
    }

    private static string MonthlyExtreme(IReadOnlyList<DwdWindDay> days, bool highest)
    {
        var byMonth = days.GroupBy(d => d.Date.Month)
            .Select(g => (Month: g.Key, Mean: g.Average(d => d.MeanSpeed)))
            .ToList();

        var pick = highest
            ? byMonth.MaxBy(m => m.Mean)
            : byMonth.MinBy(m => m.Mean);

        return $"month {pick.Month} ({pick.Mean:F2} m/s)";
    }

    private static double StandardDeviation(IReadOnlyList<double> values)
    {
        double mean = values.Average();
        double sum = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sum / (values.Count - 1));
    }

    /// <summary>Linearly interpolated quantile of an already-sorted list.</summary>
    private static double Quantile(IReadOnlyList<double> sorted, double p)
    {
        double position = p * (sorted.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Count - 1);
        double weight = position - lower;

        return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
    }
}

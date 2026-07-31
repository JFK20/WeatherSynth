using WeatherSynth.Data;
using WeatherSynth.Solar;

namespace WeatherSynth.Sample;

/// <summary>
/// Measures what the solar-position residual actually costs on the quantity that matters
/// the daily clear-sky total that forms the denominator of the clearness index.
///
/// An angular error is only worth fixing in proportion to what it does downstream, and the
/// station file lets that be measured directly rather than estimated: the same clear-sky model
/// is integrated twice, once over the library's zenith and once over DWD's own ZENIT column,
/// and the daily totals compared.
/// </summary>
public static class ZenithImpact
{
    public static void Run(IReadOnlyList<DwdSolarDay> days, DwdStation station)
    {
        Console.WriteLine("=== Effect of the zenith residual on daily clear-sky GHI ===");
        Console.WriteLine();

        var calculator = new DailyClearSkyCalculator(
            station.LatitudeDegrees,
            station.LongitudeDegrees,
            station.AltitudeMeters,
            TimeZoneInfo.Utc
        );

        var monthlyRelative = new double[13];
        var monthlyAbsolute = new double[13];
        var monthlyCount = new int[13];
        double worstRelative = 0.0;
        DateOnly worstDate = default;

        foreach (var day in days.Where(d => d.IsComplete))
        {
            double libraryWh = 0.0;
            double dwdWh = 0.0;

            foreach (var interval in day.Intervals)
            {
                double turbidity = LinkeTurbidity.Default(interval.MidpointUtc.UtcDateTime);
                int dayOfYear = interval.MidpointUtc.DayOfYear;

                libraryWh += ClearSkyIneichen
                    .Estimate(
                        calculator.ApparentZenithDegrees(interval.MidpointUtc),
                        dayOfYear,
                        turbidity,
                        station.AltitudeMeters
                    )
                    .Ghi;

                // DWD publishes the geometric zenith, so it needs the same refraction
                // correction the library path applies before reaching the air-mass formula.
                double dwdElevation = 90.0 - interval.ZenithDegrees;
                double dwdApparentZenith =
                    90.0
                    - (
                        dwdElevation
                        + DailyClearSkyCalculator.RefractionCorrectionDegrees(dwdElevation)
                    );

                dwdWh += ClearSkyIneichen
                    .Estimate(dwdApparentZenith, dayOfYear, turbidity, station.AltitudeMeters)
                    .Ghi;
            }

            if (dwdWh <= 0.0)
                continue;

            double relative = (libraryWh - dwdWh) / dwdWh;
            int month = day.Date.Month;

            monthlyRelative[month] += relative;
            monthlyAbsolute[month] += Math.Abs(relative);
            monthlyCount[month]++;

            if (Math.Abs(relative) > Math.Abs(worstRelative))
            {
                worstRelative = relative;
                worstDate = day.Date;
            }
        }

        Console.WriteLine("month   mean signed    mean absolute   days");
        double totalSigned = 0.0,
            totalAbsolute = 0.0;
        int totalCount = 0;

        for (int m = 1; m <= 12; m++)
        {
            if (monthlyCount[m] == 0)
                continue;
            Console.WriteLine(
                $"{m, 5} {monthlyRelative[m] / monthlyCount[m], 13:P3} "
                    + $"{monthlyAbsolute[m] / monthlyCount[m], 15:P3} {monthlyCount[m], 6}"
            );

            totalSigned += monthlyRelative[m];
            totalAbsolute += monthlyAbsolute[m];
            totalCount += monthlyCount[m];
        }

        Console.WriteLine();
        Console.WriteLine($"Overall mean signed   : {totalSigned / totalCount:P4}");
        Console.WriteLine($"Overall mean absolute : {totalAbsolute / totalCount:P4}");
        Console.WriteLine($"Worst day             : {worstDate} at {worstRelative:P3}");
        Console.WriteLine();
    }
}

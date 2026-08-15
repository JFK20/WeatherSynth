using WeatherSynth.Climate;
using WeatherSynth.Data;
using WeatherSynth.Wind;

namespace WeatherSynth.Sample;

/// <summary>
/// Writes one synthetic wind year at daily resolution.
///
/// <para>Everything else in the wind half of this harness measures the model. This prints what it
/// is for: a plausible year of daily mean wind speeds that never happened, one line per day.</para>
///
/// <para>The generating is all <see cref="SyntheticWindProvider"/>; what is left here is
/// formatting. That is the point - this command doubles as the worked example of how a caller asks
/// the library for a year.</para>
/// </summary>
public static class WindYearReport
{
    /// <summary>Default seed, shared with the fit report so runs are comparable.</summary>
    private const int DefaultSeed = 20260803;

    public static void Run(IReadOnlyList<DwdWindDay> days, DwdWindStation station, string[] args)
    {
        int year = ArgumentAt(args, 1) ?? DateTime.UtcNow.Year;
        int seed = ArgumentAt(args, 2) ?? DefaultSeed;

        var provider = SyntheticWindProvider.FromStationDays(days, station);
        var generated = provider.GenerateYear(year, seed);

        Console.WriteLine(
            $"=== Synthetic {generated.Year} at {station.Name}, seed {generated.Seed} ==="
        );
        Console.WriteLine(
            $"Fitted and generated at {station.AnemometerHeightMeters:F1} m above ground, so the "
                + "height transfer"
        );
        Console.WriteLine(
            "is exactly 1.0 and none of the profile's uncertainty is in these numbers. This is one"
        );
        Console.WriteLine("realisation, not a forecast: another seed is an equally plausible year.");
        Console.WriteLine();
        Console.WriteLine("date          speed m/s   implied mean v³");

        foreach (var day in generated.Days)
        {
            Console.WriteLine(
                $"{day.Date:yyyy-MM-dd} {day.MeanSpeed, 11:F2} {day.MeanCubedSpeed, 17:F1}"
            );
        }

        Console.WriteLine();
        Console.WriteLine("=== Monthly means ===");
        Console.WriteLine("month   days   mean m/s    max m/s   mean v³");

        foreach (var month in generated.Months)
        {
            Console.WriteLine(
                $"{month.Month, 5} {month.Days, 6} {month.MeanSpeed, 10:F2} "
                    + $"{month.MaxSpeed, 10:F2} {month.MeanCubedSpeed, 9:F1}"
            );
        }

        Console.WriteLine();
        Console.WriteLine(
            $"  {generated.Days.Count} days, annual mean {generated.MeanSpeed:F3} m/s, "
                + $"windiest day {generated.MaxSpeed:F2} m/s"
        );
        Console.WriteLine(
            $"  Mean of cubed speeds {generated.MeanCubedSpeed:F1} m³/s³, which is "
                + $"{generated.EnergyPatternFactor:F2}x the cube of the annual mean."
        );
        Console.WriteLine(
            "  Energy scales with that second number, not the first. Cubing the annual mean speed"
        );
        Console.WriteLine(
            "  understates the year twice over - once for the within-day spread and again for the"
        );
        Console.WriteLine("  day-to-day spread.");
        Console.WriteLine();

        TransferExample(provider, station, generated);
    }

    /// <summary>
    /// The same year at a turbine hub height, printed as a worked example - and as a warning.
    /// </summary>
    private static void TransferExample(
        SyntheticWindProvider provider,
        DwdWindStation station,
        SyntheticWindYear atAnemometer
    )
    {
        var reference = station.ToSite();
        var hub = new WindSite(HeightMeters: 100.0, RoughnessLengthMeters: 0.1);

        double logFactor = hub.TransferFactorFrom(reference);
        double powerFactor = hub.TransferFactorFrom(reference, WindProfile.PowerLaw());

        Console.WriteLine("=== What a height transfer would do, and what it is worth ===");
        Console.WriteLine(
            $"  To a 100 m hub over farmland (z0 0.1 m), from {station.AnemometerHeightMeters:F0} m "
                + $"at z0 {station.RoughnessLengthMeters:F2} m:"
        );
        Console.WriteLine($"    log law   factor {logFactor:F3}");
        Console.WriteLine($"    power law factor {powerFactor:F3}   (alpha 1/7)");
        Console.WriteLine(
            $"  The two laws disagree by {Math.Abs(logFactor - powerFactor) / logFactor:P0}, and "
                + "that disagreement is a fair"
        );
        Console.WriteLine(
            "  estimate of what this step is worth. It ignores atmospheric stability, the diurnal"
        );
        Console.WriteLine(
            "  cycle in the profile, and the terrain upwind - and the roughness length it rests on"
        );
        Console.WriteLine(
            "  is an estimate in a 0.3-0.5 m bracket. This multiplication carries far more error"
        );
        Console.WriteLine("  than everything in the fitted distributions put together.");
        Console.WriteLine();

        // The same year and seed as the run above, so the two means differ in nothing but the
        // transfer - which is the comparison worth printing.
        var lifted = provider.GenerateYear(atAnemometer.Year, atAnemometer.Seed, hub);

        Console.WriteLine(
            $"  The same year and seed at the hub: {lifted.MeanSpeed:F3} m/s, against "
                + $"{atAnemometer.MeanSpeed:F3} m/s at the anemometer."
        );
    }

    /// <summary>Optional positional integer argument; null when absent, and a hard error when unparseable.</summary>
    private static int? ArgumentAt(string[] args, int index)
    {
        if (args.Length <= index)
            return null;

        if (!int.TryParse(args[index], out int value))
            throw new ArgumentException($"'{args[index]}' is not a number.");

        return value;
    }
}

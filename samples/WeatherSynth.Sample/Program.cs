using WeatherSynth.Data;

namespace WeatherSynth.Sample;

/// <summary>
/// Analysis harness. Each sub-command is one step on the way from the raw station file to a
/// validated clearness-index dataset.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0] : "summary";

        if (command == "sanity")
        {
            // Köln the site the generator is aimed at, as opposed to Bochum where it is fitted.
            //check if the resulting values still makes sense
            ClearSkySanity.Run(
                latitude: 51.02095,
                longitude: 6.89422,
                altitude: 50.0,
                "Europe/Berlin"
            );
            return 0;
        }

        string? dataPath = RepositoryData.TryLocateBochum();
        if (dataPath is null)
        {
            Console.Error.WriteLine(
                $"Could not find data/{RepositoryData.BochumFileName} in any parent directory."
            );
            return 1;
        }

        var station = DwdStations.Bochum;

        Console.WriteLine($"Reading {dataPath} ...");
        var intervals = DwdSolarReader.Read(dataPath).ToList();
        var days = DwdSolarDayAggregator.ToDays(intervals);
        Console.WriteLine(
            $"{intervals.Count:N0} intervals, {days.Count:N0} days "
                + $"({days.Count(d => d.IsComplete):N0} complete)"
        );
        Console.WriteLine();

        switch (command)
        {
            case "summary":
                DataSummary.Run(intervals, days);
                break;

            case "zenith":
                ZenithValidation.Run(intervals, station);
                break;

            case "decompose":
                ZenithDecomposition.Run(intervals, station);
                break;

            case "kt":
                ClearnessIndexReport.Run(days, station);
                break;

            case "calibrate":
                ClearSkyCalibration.Run(days, station);
                break;

            case "fit":
                IndexFitReport.Run(days, station);
                break;

            case "year":
                YearSeriesReport.Run(days, station, args);
                break;

            case "viz":
                return VisualizationExport.Run(days, station);

            case "impact":
                ZenithImpact.Run(days, station);
                break;

            case "fitcoords":
                ZenithValidation.FitCoordinates(intervals, station);
                break;

            default:
                Console.Error.WriteLine(
                    $"Unknown command '{command}'. Try: summary, zenith, decompose, kt, "
                        + "calibrate, fit, year, viz, impact, fitcoords, sanity"
                );
                return 1;
        }

        return 0;
    }
}

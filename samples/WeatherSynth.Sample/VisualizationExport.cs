using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeatherSynth.Climate;
using WeatherSynth.Data;

namespace WeatherSynth.Sample;

/// <summary>
/// Writes the visualisation app: fits both models, generates a synthetic record over the same
/// span as each measured one, and bakes everything into a single self-contained HTML file with a
/// solar/wind switch.
///
/// <para>The data is inlined rather than fetched. A page opened from the filesystem cannot
/// <c>fetch</c> a sibling JSON file - the browser treats it as a cross-origin request - so a
/// separate data file would only work behind a web server. One file always works.</para>
///
/// <para><b>The wind half is optional.</b> The two records are different files at different
/// stations, and only the solar one is required to build. With the wind record absent the page is
/// written without its switch, which is the same data-aware contract the tests follow.</para>
/// </summary>
public static class VisualizationExport
{
    private const string TemplateFileName = "template.html";
    private const string OutputFileName = "index.html";
    private const string DataPlaceholder = "/*__DATA__*/";

    public static int Run(
        IReadOnlyList<DwdSolarDay> days,
        DwdStation station,
        IReadOnlyList<DwdWindDay>? windDays = null,
        DwdWindStation? windStation = null
    )
    {
        string? templatePath = LocateTemplate();
        if (templatePath is null)
        {
            Console.Error.WriteLine(
                $"Could not find viz/{TemplateFileName} in any parent directory."
            );
            return 1;
        }

        var series = IndexFitReport.BuildSeries(days, station);
        var model = ClearSkyIndexModel.Fit(series);

        Console.WriteLine(
            $"Fitted {series.Count:N0} solar days. Generating a matching synthetic record ..."
        );

        var synthetic = new SyntheticSolarGenerator(model, IndexFitReport.Ceiling(station))
            .Generate(series[0].Date, series[^1].Date, new Random(IndexFitReport.Seed))
            .ToList();

        // The same span and seed with persistence switched off, so the page can show what the
        // AR(1) term actually bought rather than asserting it. Indices only - the page quotes one
        // autocorrelation from this, and irradiance would not change it.
        var independent = IndexFitReport
            .IndexSeries(model, series[0].Date, series[^1].Date, persistence: 0.0)
            .ToList();

        var payload = new JsonObject
        {
            ["solar"] = BuildPayload(series, synthetic, independent, model, station),
        };

        if (windDays is not null && windStation is not null)
        {
            Console.WriteLine("Fitting the wind record and generating its synthetic twin ...");
            payload["wind"] = WindVisualizationPayload.Build(windDays, windStation);
        }
        else
        {
            Console.WriteLine(
                $"No wind record found - writing the solar half only. "
                    + $"Place data/{RepositoryData.EssenWindFileName} to include it."
            );
        }

        string template = File.ReadAllText(templatePath);
        if (!template.Contains(DataPlaceholder, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Template is missing its {DataPlaceholder} placeholder.");
            return 1;
        }

        string json = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        string outputPath = Path.Combine(Path.GetDirectoryName(templatePath)!, OutputFileName);

        File.WriteAllText(
            outputPath,
            template.Replace(DataPlaceholder, json, StringComparison.Ordinal),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );

        Console.WriteLine($"Wrote {outputPath} ({new FileInfo(outputPath).Length / 1024:N0} kB).");
        Console.WriteLine("Open it in a browser directly - it needs no server.");
        return 0;
    }

    private static JsonObject BuildPayload(
        IReadOnlyList<DailyClearness> observed,
        IReadOnlyList<SyntheticSolarDay> synthetic,
        IReadOnlyList<(DateOnly Date, double Index)> independent,
        ClearSkyIndexModel model,
        DwdStation station
    )
    {
        var start = observed[0].Date;

        double observedAutocorrelation = SeriesStatistics.Lag1Autocorrelation(
            observed.Select(d => (d.Date, d.ClearSkyIndex))
        );
        double syntheticAutocorrelation = SeriesStatistics.Lag1Autocorrelation(
            synthetic.Select(d => (d.Date, d.ClearSkyIndex))
        );
        double independentAutocorrelation = SeriesStatistics.Lag1Autocorrelation(independent);

        return new JsonObject
        {
            ["site"] = station.Name,
            ["latitude"] = Round(station.LatitudeDegrees, 4),
            ["longitude"] = Round(station.LongitudeDegrees, 4),
            ["support"] = model.Support,
            ["startDate"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["endDate"] = observed[^1].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["autocorrelation"] = new JsonObject
            {
                ["observed"] = Round(observedAutocorrelation, 4),
                ["synthetic"] = Round(syntheticAutocorrelation, 4),
                ["independent"] = Round(independentAutocorrelation, 4),
                ["phi"] = Round(model.Persistence, 4),
            },
            ["annualKWh"] = new JsonObject
            {
                ["observed"] = Round(
                    AnnualMean(observed.Select(d => (d.Date, d.ObservedWhPerM2))),
                    1
                ),
                ["synthetic"] = Round(AnnualMean(synthetic.Select(d => (d.Date, d.GhiWhPerM2))), 1),
            },
            ["months"] = MonthFits(observed, model),
            ["observed"] = Track(
                observed.Select(d => (d.Date, d.ClearSkyIndex, d.ClearSkyWhPerM2)),
                start
            ),
            ["synthetic"] = Track(
                synthetic.Select(d => (d.Date, d.ClearSkyIndex, d.ClearSkyWhPerM2)),
                start
            ),
        };
    }

    /// <summary>
    /// Per-month fit parameters, the density curve to draw, and the goodness-of-fit verdict.
    /// The density is evaluated here rather than in the browser so the page needs no
    /// reimplementation of the log-gamma function.
    /// </summary>
    private static JsonArray MonthFits(
        IReadOnlyList<DailyClearness> observed,
        ClearSkyIndexModel model
    )
    {
        var months = new JsonArray();

        foreach (var group in observed.GroupBy(d => d.Date.Month).OrderBy(g => g.Key))
        {
            var fit = model.ForMonth(group.Key);
            var values = group.Select(d => d.ClearSkyIndex).ToList();

            const int steps = 125;
            var density = new JsonArray();
            for (int i = 0; i <= steps; i++)
            {
                double x = model.Support * i / steps;
                density.Add(Round(fit.Density(x), 4));
            }

            months.Add(
                new JsonObject
                {
                    ["month"] = group.Key,
                    ["alpha"] = Round(fit.Alpha, 4),
                    ["beta"] = Round(fit.Beta, 4),
                    ["mean"] = Round(fit.Mean, 4),
                    ["sd"] = Round(fit.StandardDeviation, 4),
                    ["days"] = values.Count,
                    ["exceedingCeiling"] = values.Count(v => v > 1.0),
                    ["density"] = density,
                }
            );
        }

        return months;
    }

    /// <summary>
    /// One daily series as parallel arrays of day-offsets, index and ceiling.
    ///
    /// <para>Offsets rather than dates because the measured record has gaps, and the page has to
    /// know where they are: joining across a gap would draw a line through days that were never
    /// observed. Irradiation is not sent at all - it is exactly index x ceiling, so the page
    /// derives it and the payload carries two numbers per day instead of three.</para>
    /// </summary>
    private static JsonObject Track(
        IEnumerable<(DateOnly Date, double Index, double Ceiling)> series,
        DateOnly start
    )
    {
        var offsets = new JsonArray();
        var indices = new JsonArray();
        var ceilings = new JsonArray();

        foreach (var (date, index, ceiling) in series)
        {
            offsets.Add(date.DayNumber - start.DayNumber);
            indices.Add(Round(index, 4));
            ceilings.Add((int)Math.Round(ceiling));
        }

        return new JsonObject
        {
            ["offset"] = offsets,
            ["index"] = indices,
            ["ceiling"] = ceilings,
        };
    }

    /// <summary>Mean annual irradiation in kWh/m², over years with enough days to be comparable.</summary>
    private static double AnnualMean(IEnumerable<(DateOnly Date, double WhPerM2)> series) =>
        series
            .GroupBy(d => d.Date.Year)
            .Where(g => g.Count() > 300)
            .Average(g => g.Sum(d => d.WhPerM2)) / 1000.0;

    private static double Round(double value, int digits) =>
        double.IsFinite(value) ? Math.Round(value, digits) : 0.0;

    /// <summary>Walks up from the running assembly to find the checked-in template.</summary>
    private static string? LocateTemplate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "viz", TemplateFileName);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return null;
    }
}

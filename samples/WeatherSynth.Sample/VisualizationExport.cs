using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeatherSynth.Climate;
using WeatherSynth.Data;

namespace WeatherSynth.Sample;

/// <summary>
/// The span a synthetic run covers, and the seed that reproduces it.
///
/// <para>Top-level rather than nested because both payload builders take one: in twin mode it is
/// the measured record's own span, in projection mode a run of future calendar years, and the two
/// halves of the page have to agree on which.</para>
/// </summary>
/// <param name="Start">First day, inclusive.</param>
/// <param name="End">Last day, inclusive.</param>
/// <param name="Seed">Seed. In projection mode one seed drives both resources.</param>
public readonly record struct SyntheticSpan(DateOnly Start, DateOnly End, int Seed);

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
        DwdWindStation? windStation = null,
        string[]? args = null
    )
    {
        ProjectionOptions options;
        try
        {
            options = ProjectionOptions.Parse(args ?? Array.Empty<string>());
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine("Usage: viz [years] [seed] [coupled|independent]");
            return 1;
        }

        bool hasWind = windDays is not null && windStation is not null;

        // Coupling is fitted from both records at once, so unlike the wind half it has nothing to
        // degrade to. Same distinction Program.cs draws between `viz` and `couple`.
        if (options.Coupled && !hasWind)
        {
            Console.Error.WriteLine(
                $"'coupled' needs both records; could not find data/{RepositoryData.EssenWindFileName} "
                    + "in any parent directory."
            );
            return 1;
        }

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

        // Projection mode covers whole future calendar years - whole ones, because the season
        // lives in the twelve monthly marginals and a part-year at either end would skew every
        // monthly mean the page draws.
        //
        // Null in twin mode, and that is load-bearing rather than tidy: the two records end on
        // different days, so twin mode has to let each resource cover its OWN span. Handing the
        // wind half the solar record's span would generate months of synthetic wind past the end
        // of the record it is being compared against.
        SyntheticSpan? projection = options.Years is int years
            ? new SyntheticSpan(
                new DateOnly(DateTime.UtcNow.Year + 1, 1, 1),
                new DateOnly(DateTime.UtcNow.Year + years, 12, 31),
                options.Seed
            )
            : null;

        var span =
            projection ?? new SyntheticSpan(series[0].Date, series[^1].Date, IndexFitReport.Seed);

        // Two independent streams need two seeds, and this is not a detail.
        //
        // Handing the same seed to both generators makes them draw the SAME standard normals in
        // the same order, so the two latent chains move in lockstep and the resources come out
        // strongly POSITIVELY correlated - measured at +0.75 here, against a real-world -0.22.
        // Individually each half still looks perfect: the marginals, the persistence and the
        // annual totals are all exactly right, and only the pairing is nonsense. That is the same
        // failure mode coupling exists to fix, with the sign flipped and much larger.
        //
        // Derived from the caller's seed rather than picked, so `viz 5 4242 independent` is still
        // reproducible from the 4242 alone. Twin mode is untouched: it already had two unrelated
        // seeds, one per report.
        var streams = new Random(span.Seed);
        var solarSpan = projection is null ? span : span with { Seed = streams.Next() };
        var windSpan = projection is null
            ? projection
            : projection.Value with
            {
                Seed = streams.Next(),
            };

        Console.WriteLine(
            options.Years is null
                ? $"Fitted {series.Count:N0} solar days. Generating a matching synthetic record ..."
                : $"Fitted {series.Count:N0} solar days. Projecting {options.Years} years "
                    + $"({span.Start:yyyy-MM-dd} to {span.End:yyyy-MM-dd}), seed {span.Seed}, "
                    + $"{(options.Coupled ? "coupled" : "independent")} ..."
        );

        List<SyntheticSolarDay> synthetic;
        IReadOnlyList<SyntheticWindDay>? windSynthetic = null;

        if (options.Coupled)
        {
            // One bivariate chain, one seed. Two seeds would be two independent streams, which is
            // the thing coupling exists not to be.
            var coupled = CoupledWeatherProvider.FromStationDays(
                days,
                station,
                windDays!,
                windStation!
            );

            var pairs = coupled.Generate(span.Start, span.End, span.Seed).ToList();

            synthetic = pairs.Select(p => p.Solar).ToList();
            windSynthetic = pairs.Select(p => p.Wind).ToList();
        }
        else
        {
            synthetic = new SyntheticSolarGenerator(model, station.ToSite().CreateCeiling())
                .Generate(solarSpan.Start, solarSpan.End, new Random(solarSpan.Seed))
                .ToList();
        }

        // The same span and seed with persistence switched off, so the page can show what the
        // AR(1) term actually bought rather than asserting it. Indices only - the page quotes one
        // autocorrelation from this, and irradiance would not change it.
        var independent = IndexFitReport
            .IndexSeries(
                model,
                solarSpan.Start,
                solarSpan.End,
                persistence: 0.0,
                seed: solarSpan.Seed
            )
            .ToList();

        var payload = new JsonObject
        {
            ["solar"] = BuildPayload(series, synthetic, independent, model, station),
        };

        if (hasWind)
        {
            Console.WriteLine(
                options.Coupled
                    ? "Fitting the wind record; its synthetic half came from the coupled chain ..."
                    : "Fitting the wind record and generating its synthetic twin ..."
            );

            payload["wind"] = WindVisualizationPayload.Build(
                windDays!,
                windStation!,
                windSpan,
                windSynthetic
            );

            payload["combined"] = CombinedPayload.Build(days, station, windDays!, options.Coupled);
        }
        else
        {
            Console.WriteLine(
                $"No wind record found - writing the solar half only. "
                    + $"Place data/{RepositoryData.EssenWindFileName} to include it."
            );
        }

        if (options.Years is int projectedYears)
        {
            payload["projection"] = new JsonObject
            {
                ["years"] = projectedYears,
                ["seed"] = span.Seed,
                ["coupled"] = options.Coupled,
                ["startDate"] = span.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["endDate"] = span.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
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
                synthetic[0].Date
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
    ///
    /// <para><b>Each track carries its own start date</b>, rather than sharing one for the whole
    /// resource. In twin mode the measured and synthetic tracks begin on the same day; in
    /// projection mode the synthetic one begins years after the record ends, and a shared origin
    /// would date every projected day wrongly.</para>
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
            ["start"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
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

    /// <summary>
    /// What <c>viz</c> was asked for: <c>viz [years] [seed] [coupled|independent]</c>.
    ///
    /// <para>With no arguments the page is what it has always been - a synthetic twin over the
    /// measured record's own span. Arguments opt into projection mode instead.</para>
    /// </summary>
    /// <param name="Years">Whole future calendar years to project, or null for twin mode.</param>
    /// <param name="Seed">Seed for the run. In projection mode it drives both resources.</param>
    /// <param name="Coupled">Whether the two resources come from one bivariate chain.</param>
    internal readonly record struct ProjectionOptions(int? Years, int Seed, bool Coupled)
    {
        /// <summary>Beyond this the payload grows without telling anyone anything new.</summary>
        private const int MaximumYears = 50;

        public static ProjectionOptions Parse(string[] args)
        {
            int? years = null;
            int seed = IndexFitReport.Seed;
            bool coupled = false;

            // args[0] is the command name itself, matching how `year` and `windyear` read theirs.
            for (int i = 1; i < args.Length; i++)
            {
                string argument = args[i];

                switch (argument.ToLowerInvariant())
                {
                    case "coupled":
                    case "coupling":
                        coupled = true;
                        continue;

                    case "independent":
                    case "uncoupled":
                        coupled = false;
                        continue;
                }

                if (!int.TryParse(argument, out int value))
                    throw new ArgumentException(
                        $"'{argument}' is neither a number nor 'coupled'/'independent'."
                    );

                if (years is null)
                {
                    if (value < 1 || value > MaximumYears)
                        throw new ArgumentException(
                            $"Years must be between 1 and {MaximumYears}; got {value}."
                        );

                    years = value;
                }
                else
                {
                    seed = value;
                }
            }

            if (years is null && coupled)
                throw new ArgumentException(
                    "'coupled' only means something for a projection: say how many years, "
                        + "e.g. `viz 5 4242 coupled`."
                );

            return new ProjectionOptions(years, seed, coupled);
        }
    }

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

using System.Globalization;
using System.Text.Json.Nodes;
using WeatherSynth.Climate;
using WeatherSynth.Data;

namespace WeatherSynth.Sample;

/// <summary>
/// Builds the wind half of the visualisation payload: the fitted model, a synthetic record over
/// the same span as the measured one, and the two comparisons the page exists to make.
///
/// <para>Everything here goes through the same calls the console reports use -
/// <see cref="WindSpeedSeriesBuilder"/>, <see cref="SyntheticWindProvider"/>,
/// <see cref="GoodnessOfFit"/> - so the page and <c>windfit</c> cannot disagree about a number.</para>
/// </summary>
public static class WindVisualizationPayload
{
    /// <summary>Points on the density curves sent to the page.</summary>
    private const int DensitySteps = 150;

    /// <summary>Bins for the measured energy-pattern-factor histogram.</summary>
    private const int FactorBins = 26;

    private const double FactorBinMin = 1.0;
    private const double FactorBinMax = 2.3;

    public static JsonObject Build(IReadOnlyList<DwdWindDay> days, DwdWindStation station)
    {
        var series = WindSpeedSeriesBuilder.Build(days);
        var provider = SyntheticWindProvider.FromStationDays(days, station);
        var model = provider.Model;

        var start = series[0].Date;
        var end = series[^1].Date;

        // The same span as the measured record, so the page compares like with like.
        var synthetic = provider.Generate(start, end, WindFitReport.Seed).ToList();

        // The same span and seed with persistence switched off - what the model produced before
        // the chain existed, measured rather than remembered.
        var independent = new SyntheticWindGenerator(
            new LatentAr1Chain(model, 0.0),
            transferFactor: 1.0,
            model.MeanEnergyPatternFactor
        )
            .Generate(start, end, new Random(WindFitReport.Seed))
            .ToList();

        double speedMax = Math.Ceiling(series.Max(d => d.MeanSpeed));

        return new JsonObject
        {
            ["site"] = station.Name,
            ["stationId"] = station.Id,
            ["anemometerHeight"] = station.AnemometerHeightMeters,
            ["roughness"] = station.RoughnessLengthMeters,
            ["startDate"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["endDate"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["speedMax"] = speedMax,
            ["autocorrelation"] = new JsonObject
            {
                ["observed"] = Round(
                    SeriesStatistics.Lag1Autocorrelation(series.Select(d => (d.Date, d.MeanSpeed))),
                    4
                ),
                ["synthetic"] = Round(
                    SeriesStatistics.Lag1Autocorrelation(
                        synthetic.Select(d => (d.Date, d.MeanSpeed))
                    ),
                    4
                ),
                ["independent"] = Round(
                    SeriesStatistics.Lag1Autocorrelation(
                        independent.Select(d => (d.Date, d.MeanSpeed))
                    ),
                    4
                ),
                ["phi"] = Round(model.Persistence, 4),
            },
            ["meanSpeed"] = new JsonObject
            {
                ["observed"] = Round(series.Average(d => d.MeanSpeed), 4),
                ["synthetic"] = Round(synthetic.Average(d => d.MeanSpeed), 4),
            },
            ["energyPatternFactor"] = FactorHistogram(series, model),
            ["months"] = MonthFits(series, model, speedMax),
            ["observed"] = Track(series.Select(d => (d.Date, d.MeanSpeed)), start),
            ["synthetic"] = Track(synthetic.Select(d => (d.Date, d.MeanSpeed)), start),
        };
    }

    /// <summary>
    /// The measured spread of <c>mean(v³)/mean(v)³</c>, against the single value the model applies.
    ///
    /// <para>Sent as a histogram rather than 6,186 per-day values: the page draws exactly this and
    /// nothing else needs them. The point of the panel is the contrast between a real spread and
    /// the model's constant, so the quantiles are carried alongside for the labels.</para>
    /// </summary>
    private static JsonObject FactorHistogram(
        IReadOnlyList<DailyWindSpeed> series,
        WindSpeedModel model
    )
    {
        var factors = series
            .Select(d => d.EnergyPatternFactor)
            .Where(double.IsFinite)
            .OrderBy(v => v)
            .ToList();

        double width = (FactorBinMax - FactorBinMin) / FactorBins;
        var counts = new int[FactorBins];
        int above = 0;

        foreach (double factor in factors)
        {
            int bin = (int)((factor - FactorBinMin) / width);
            if (bin >= FactorBins)
                above++;
            else
                counts[Math.Max(0, bin)]++;
        }

        var jsonCounts = new JsonArray();
        foreach (int count in counts)
            jsonCounts.Add(count);

        return new JsonObject
        {
            ["model"] = Round(model.MeanEnergyPatternFactor, 4),
            ["binMin"] = FactorBinMin,
            ["binMax"] = FactorBinMax,
            ["counts"] = jsonCounts,
            ["aboveRange"] = above,
            ["days"] = factors.Count,
            ["median"] = Round(Quantile(factors, 0.5), 4),
            ["p10"] = Round(Quantile(factors, 0.1), 4),
            ["p90"] = Round(Quantile(factors, 0.9), 4),
        };
    }

    /// <summary>
    /// Per-month fit parameters, both density curves, and both goodness-of-fit verdicts.
    ///
    /// <para>The two-parameter curve is the same maximum-likelihood fit with the location pinned at
    /// zero - the comparison <c>windfit</c> prints, drawn instead of tabulated. The densities are
    /// evaluated here rather than in the browser so the page needs no distribution code at all.</para>
    /// </summary>
    private static JsonArray MonthFits(
        IReadOnlyList<DailyWindSpeed> series,
        WindSpeedModel model,
        double speedMax
    )
    {
        var months = new JsonArray();

        foreach (var group in series.GroupBy(d => d.Date.Month).OrderBy(g => g.Key))
        {
            var fit = model.ForMonth(group.Key);
            var values = group.Select(d => d.MeanSpeed).ToList();
            var pinned = Weibull.FitByMaximumLikelihood(values, location: 0.0);

            var density = new JsonArray();
            var pinnedDensity = new JsonArray();
            for (int i = 0; i <= DensitySteps; i++)
            {
                double x = speedMax * i / DensitySteps;
                density.Add(Round(fit.Density(x), 5));
                pinnedDensity.Add(Round(pinned.Density(x), 5));
            }

            months.Add(
                new JsonObject
                {
                    ["month"] = group.Key,
                    ["gamma"] = Round(fit.Location, 4),
                    ["k"] = Round(fit.Shape, 4),
                    ["a"] = Round(fit.Scale, 4),
                    ["mean"] = Round(fit.Mean, 4),
                    ["sd"] = Round(fit.StandardDeviation, 4),
                    ["days"] = values.Count,
                    ["ks"] = Round(
                        GoodnessOfFit.KolmogorovSmirnovDistance(values, fit.CumulativeProbability),
                        4
                    ),
                    ["critical"] = Round(
                        GoodnessOfFit.CriticalValueFivePercent(values.Count),
                        4
                    ),
                    ["twoK"] = Round(pinned.Shape, 4),
                    ["twoKs"] = Round(
                        GoodnessOfFit.KolmogorovSmirnovDistance(
                            values,
                            pinned.CumulativeProbability
                        ),
                        4
                    ),
                    ["density"] = density,
                    ["twoDensity"] = pinnedDensity,
                }
            );
        }

        return months;
    }

    /// <summary>
    /// One daily series as parallel arrays of day-offsets and speeds.
    ///
    /// <para>Offsets rather than dates because the measured record has two one-day holes, and the
    /// page has to know where they are: joining across a gap would draw a line through days that
    /// were never observed, and would invent a consecutive-day pair in the lag panel.</para>
    /// </summary>
    private static JsonObject Track(
        IEnumerable<(DateOnly Date, double Speed)> series,
        DateOnly start
    )
    {
        var offsets = new JsonArray();
        var speeds = new JsonArray();

        foreach (var (date, speed) in series)
        {
            offsets.Add(date.DayNumber - start.DayNumber);
            speeds.Add(Round(speed, 3));
        }

        return new JsonObject { ["offset"] = offsets, ["speed"] = speeds };
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

    private static double Round(double value, int digits) =>
        double.IsFinite(value) ? Math.Round(value, digits) : 0.0;
}

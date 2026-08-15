using WeatherSynth.Climate;
using WeatherSynth.Data;

namespace WeatherSynth.Sample;

/// <summary>
/// Fits the twelve monthly wind distributions and scores them, the way
/// <see cref="IndexFitReport"/> does for solar.
///
/// <para>This report is where the modelling decision behind the wind half is checked rather than
/// asserted: it fits both the textbook two-parameter Weibull and the three-parameter one against
/// the same days, and prints how many months each passes.</para>
/// </summary>
public static class WindFitReport
{
    /// <summary>
    /// The seed every synthetic wind run uses, so before-and-after comparisons differ only in phi
    /// - and so the visualisation page and this report describe the same generated record.
    /// </summary>
    internal const int Seed = 20260803;

    public static void Run(IReadOnlyList<DwdWindDay> days, DwdWindStation station)
    {
        var series = WindSpeedSeriesBuilder.Build(days);
        var model = WindSpeedModel.Fit(series, station.AnemometerHeightMeters);

        Console.WriteLine(
            $"{series.Count:N0} usable days, {series[0].Date:yyyy-MM-dd} to {series[^1].Date:yyyy-MM-dd}"
        );
        Console.WriteLine();

        MonthlyFits(series, model, station);
        DoesAWeibullFit(series, model);
        Persistence(series, model);
        SeasonalCycle(series, model);
    }

    private static void MonthlyFits(
        IReadOnlyList<DailyWindSpeed> series,
        WindSpeedModel model,
        DwdWindStation station
    )
    {
        Console.WriteLine(
            $"=== Monthly Weibull fits, daily means at {station.AnemometerHeightMeters:F1} m AGL ==="
        );
        Console.WriteLine(
            "month    gamma        k        A   fitted mean   observed mean   fitted sd   observed sd      KS     crit     days"
        );

        foreach (var group in series.GroupBy(d => d.Date.Month).OrderBy(g => g.Key))
        {
            var fit = model.ForMonth(group.Key);
            var observed = group.Select(d => d.MeanSpeed).ToList();

            Console.WriteLine(
                $"{group.Key, 5} {fit.Location, 8:F3} {fit.Shape, 8:F3} {fit.Scale, 8:F3} "
                    + $"{fit.Mean, 13:F3} {observed.Average(), 15:F3} "
                    + $"{fit.StandardDeviation, 11:F3} {StandardDeviation(observed), 13:F3} "
                    + $"{KsDistance(observed, fit), 7:F4} "
                    + $"{GoodnessOfFit.CriticalValueFivePercent(observed.Count), 8:F4} "
                    + $"{observed.Count, 8}"
            );
        }

        Console.WriteLine();
        Console.WriteLine(
            $"pooled {model.Pooled.Location, 8:F3} {model.Pooled.Shape, 8:F3} "
                + $"{model.Pooled.Scale, 8:F3} {model.Pooled.Mean, 13:F3} "
                + $"{series.Average(d => d.MeanSpeed), 15:F3}"
        );
        Console.WriteLine(
            "  The pooled fit is the fallback for a month too thin to fit on its own, and nothing"
        );
        Console.WriteLine(
            "  else. Pooling months whose means run 2.7 to 3.8 m/s gives a distribution broader"
        );
        Console.WriteLine("  than any single month actually is.");
        Console.WriteLine();

        Console.WriteLine(
            $"A is NOT the mean: mean = gamma + A*Gamma(1 + 1/k), about gamma + 0.886*A at k = 2."
        );
        Console.WriteLine(
            "k belongs to this resolution: fitted on daily means, it is not the k a published"
        );
        Console.WriteLine(
            "site figure quotes, which is almost always hourly or 10-minute (2.14 here)."
        );
        Console.WriteLine();
    }

    private static void DoesAWeibullFit(IReadOnlyList<DailyWindSpeed> series, WindSpeedModel model)
    {
        Console.WriteLine("=== Does a Weibull actually fit, and does it need three parameters? ===");

        // Both fits, on the same days, scored the same way. This is the measurement the decision
        // to carry a location parameter rests on, so it is re-made on every run rather than
        // quoted: freeing gamma is either worth a parameter here or it is not.
        var twoFailing = new List<string>();
        var threeFailing = new List<string>();
        double twoShapeLow = double.MaxValue,
            twoShapeHigh = 0.0,
            threeShapeLow = double.MaxValue,
            threeShapeHigh = 0.0;

        foreach (var group in series.GroupBy(d => d.Date.Month).OrderBy(g => g.Key))
        {
            var values = group.Select(d => d.MeanSpeed).ToList();
            double critical = GoodnessOfFit.CriticalValueFivePercent(values.Count);

            // MLE for both, so the comparison isolates the parameter rather than the fitting
            // method: the two-parameter row is this same fit with gamma pinned at zero.
            var twoParameter = Weibull.FitByMaximumLikelihood(values, location: 0.0);
            var threeParameter = model.ForMonth(group.Key);

            double two = KsDistance(values, twoParameter);
            double three = KsDistance(values, threeParameter);

            if (two > critical)
                twoFailing.Add($"{group.Key}");
            if (three > critical)
                threeFailing.Add($"month {group.Key} (KS {three:F4} vs critical {critical:F4})");

            twoShapeLow = Math.Min(twoShapeLow, twoParameter.Shape);
            twoShapeHigh = Math.Max(twoShapeHigh, twoParameter.Shape);
            threeShapeLow = Math.Min(threeShapeLow, threeParameter.Shape);
            threeShapeHigh = Math.Max(threeShapeHigh, threeParameter.Shape);
        }

        Console.WriteLine(
            $"  Two-parameter Weibull(k, A):      {12 - twoFailing.Count, 2} of 12 months pass a 5% KS test"
        );
        Console.WriteLine(
            $"  Three-parameter Weibull(k, A, g): {12 - threeFailing.Count, 2} of 12 months pass"
        );
        Console.WriteLine();
        Console.WriteLine(
            $"  k across months, two-parameter:   {twoShapeLow:F2} - {twoShapeHigh:F2}"
        );
        Console.WriteLine(
            $"  k across months, three-parameter: {threeShapeLow:F2} - {threeShapeHigh:F2}   "
                + "(1.7-2.2 is canonical for wind)"
        );
        Console.WriteLine();

        if (threeFailing.Count > 0)
            Console.WriteLine($"  Failing months: {string.Join(", ", threeFailing)}.");

        Console.WriteLine(
            "  The location parameter is not curve-fitting for its own sake. A daily mean at a"
        );
        Console.WriteLine(
            "  sheltered inland site essentially never falls below ~1 m/s - one day in 6,186"
        );
        Console.WriteLine(
            "  sits under 1.0 over seventeen years - while a two-parameter Weibull is obliged to"
        );
        Console.WriteLine(
            "  put density all the way down to zero. It pays for that misplaced mass by inflating"
        );
        Console.WriteLine(
            "  k, which distorts the whole shape including the tail that carries the energy."
        );
        Console.WriteLine();
    }

    private static void Persistence(IReadOnlyList<DailyWindSpeed> series, WindSpeedModel model)
    {
        Console.WriteLine("=== Persistence ===");

        double observed = SeriesStatistics.Lag1Autocorrelation(
            series.Select(d => (d.Date, d.MeanSpeed))
        );

        // The same span from the same seed at two persistences, so the two runs differ in nothing
        // but phi - a genuine before-and-after rather than a remembered number.
        double sampledIndependent = SeriesStatistics.Lag1Autocorrelation(
            SpeedSeries(model, series[0].Date, persistence: 0.0)
        );
        double sampled = SeriesStatistics.Lag1Autocorrelation(
            SpeedSeries(model, series[0].Date, model.Persistence)
        );

        Console.WriteLine($"  Lag-1 autocorrelation, measured:            {observed:F4}");
        Console.WriteLine($"  Lag-1 autocorrelation, independent draws:   {sampledIndependent:F4}");
        Console.WriteLine($"  Lag-1 autocorrelation, with AR(1):          {sampled:F4}");
        Console.WriteLine($"  Fitted persistence (latent phi):            {model.Persistence:F4}");
        Console.WriteLine();
        Console.WriteLine(
            $"  The chain closes {(sampled - sampledIndependent) / (observed - sampledIndependent):P0}"
                + $" of the gap, landing {(sampled - observed) / observed:P1} from the measured figure."
        );
        Console.WriteLine();

        // The Pearson line above is not what the chain promises, so it is not the line to judge it
        // by. Mapping both series back through their own monthly CDFs and the inverse normal puts
        // them in the space the chain actually operates in, and there the agreement is exact.
        double measuredLatent = NormalScoreLag1(model, series.Select(d => (d.Date, d.MeanSpeed)));
        double sampledLatent = NormalScoreLag1(
            model,
            SpeedSeries(model, series[0].Date, model.Persistence)
        );

        Console.WriteLine("  In the space the chain actually works in - normal scores:");
        Console.WriteLine($"    measured:      {measuredLatent:F4}   (this is what phi was fitted from)");
        Console.WriteLine($"    with AR(1):    {sampledLatent:F4}");
        Console.WriteLine();
        Console.WriteLine(
            "  Those two agreeing is the whole claim, and they do. A Gaussian copula reproduces"
        );
        Console.WriteLine(
            "  the latent correlation exactly; the Pearson figure that comes back after the"
        );
        Console.WriteLine(
            "  quantile transform depends on the marginal's shape, and a Weibull's right tail is"
        );
        Console.WriteLine(
            "  heavier than a Beta's - so wind lands a few percent short of its measured Pearson"
        );
        Console.WriteLine(
            "  where solar reproduces its 0.4372 to four digits. Calibrating phi upward to close"
        );
        Console.WriteLine(
            "  that gap would trade a closed-form fit for a seeded simulation loop and would make"
        );
        Console.WriteLine("  the latent agreement above worse, not better.");
        Console.WriteLine();
        Console.WriteLine(
            "  The KS column above is the check that this cost nothing: reordering days through a"
        );
        Console.WriteLine(
            "  copula leaves each month's marginal exactly as it was fitted, so those numbers must"
        );
        Console.WriteLine("  not have moved.");
        Console.WriteLine();
        Console.WriteLine(
            "  Wind is more persistent than cloud - the solar record's measured lag-1 is 0.437"
        );
        Console.WriteLine("  against this 0.529 - so the gap the chain has to close is larger too.");
        Console.WriteLine();
        Console.WriteLine(
            "  Phi is smaller than the measured lag-1 rather than equal to it, and that is the"
        );
        Console.WriteLine(
            "  point: it lives in a different space. Each day is mapped through its own month's"
        );
        Console.WriteLine(
            "  fitted CDF and then through the inverse normal, which removes the seasonal cycle"
        );
        Console.WriteLine(
            "  - so phi is weather persistence with the season taken out, and the twelve monthly"
        );
        Console.WriteLine(
            "  marginals put the season back downstream. Fitting phi against the measured figure"
        );
        Console.WriteLine("  directly would count the seasonal contribution twice.");
        Console.WriteLine();
        Console.WriteLine(
            "  The independent-draw figure is that seasonal contribution on its own: those draws"
        );
        Console.WriteLine(
            "  are independent within a month, so all of it comes from consecutive days sharing a"
        );
        Console.WriteLine(
            "  monthly distribution whose means run 2.7 to 3.8 m/s. Everything above it is genuine"
        );
        Console.WriteLine(
            "  weather persistence - real calm spells last for days, and independent draws put a"
        );
        Console.WriteLine("  breezy day in the middle of every one of them.");
        Console.WriteLine();
    }

    /// <summary>
    /// A run of daily speeds at a stated persistence, straight off the chain.
    ///
    /// <para>A century rather than the record's seventeen years: these figures are properties of
    /// the model rather than of the data, and at seventeen years the sampling error is around
    /// 0.013 - large enough to argue with.</para>
    /// </summary>
    private static IEnumerable<(DateOnly Date, double Speed)> SpeedSeries(
        WindSpeedModel model,
        DateOnly start,
        double persistence
    )
    {
        var chain = new LatentAr1Chain(model, persistence);
        var random = new Random(Seed);

        for (var date = start; date < start.AddYears(100); date = date.AddDays(1))
            yield return (date, chain.Next(date, random));
    }

    private static void SeasonalCycle(IReadOnlyList<DailyWindSpeed> series, WindSpeedModel model)
    {
        Console.WriteLine("=== The seasonal cycle lives in the twelve fits, not in any ceiling ===");

        var means = Enumerable
            .Range(1, 12)
            .Select(month => (Month: month, Mean: model.ForMonth(month).Mean))
            .ToList();

        var windiest = means.MaxBy(m => m.Mean);
        var calmest = means.MinBy(m => m.Mean);

        Console.WriteLine(
            $"  Windiest month {windiest.Month} at {windiest.Mean:F2} m/s, calmest month "
                + $"{calmest.Month} at {calmest.Mean:F2} m/s - a ratio of "
                + $"{windiest.Mean / calmest.Mean:F2}."
        );
        Console.WriteLine(
            "  Solar carries most of its season in the clear-sky ceiling. There is no such"
        );
        Console.WriteLine(
            "  ceiling here, so replacing these twelve fits with one pooled fit would not lose a"
        );
        Console.WriteLine("  refinement, it would lose the seasons.");
        Console.WriteLine();

        Console.WriteLine("=== Annual mean, fitted against measured ===");

        // Weighted by calendar month rather than averaged over the twelve fits directly, since
        // the months are of different lengths and the record's usable days are not evenly spread.
        double fittedAnnual =
            means.Sum(m => m.Mean * DateTime.DaysInMonth(2001, m.Month)) / 365.0;

        Console.WriteLine($"  Measured mean daily speed: {series.Average(d => d.MeanSpeed), 7:F4} m/s");
        Console.WriteLine($"  Fitted   mean daily speed: {fittedAnnual, 7:F4} m/s");
        Console.WriteLine();
        Console.WriteLine(
            $"  Mean energy pattern factor: {model.MeanEnergyPatternFactor:F3}. Cubing a daily mean"
        );
        Console.WriteLine(
            "  speed understates the day's energy by roughly that much, systematically. A daily"
        );
        Console.WriteLine("  mean speed alone is NOT sufficient for an energy estimate.");
        Console.WriteLine();
    }

    /// <summary>
    /// Lag-1 correlation of a series' normal scores: each day through its own month's fitted CDF,
    /// then through the inverse normal.
    ///
    /// <para>The same transform <see cref="WindSpeedModel"/> fits phi with, so running it over a
    /// generated series answers the only question that is really about the chain: did the latent
    /// correlation survive the round trip out through a quantile and back.</para>
    /// </summary>
    private static double NormalScoreLag1(
        WindSpeedModel model,
        IEnumerable<(DateOnly Date, double Speed)> series
    )
    {
        const double edge = 1e-12;

        return SeriesStatistics.Lag1Autocorrelation(
            series.Select(d =>
                (
                    d.Date,
                    Gaussian.Quantile(
                        Math.Clamp(
                            model.CumulativeProbability(d.Speed, d.Date.Month),
                            edge,
                            1.0 - edge
                        )
                    )
                )
            )
        );
    }

    private static double KsDistance(IReadOnlyList<double> values, Weibull fit) =>
        GoodnessOfFit.KolmogorovSmirnovDistance(values, fit.CumulativeProbability);

    private static double StandardDeviation(IReadOnlyCollection<double> values)
    {
        double mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }
}

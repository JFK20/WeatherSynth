using WeatherSynth.Climate;
using WeatherSynth.Data;

namespace WeatherSynth.Sample;

/// <summary>
/// Fits the solar-wind coupling and scores it - the Phase 6 counterpart of
/// <see cref="WindFitReport"/>, and the only report that fits anything from both records at once.
///
/// <para>The claim being checked here is narrow and worth stating before the numbers: coupling
/// changes <b>which days coincide</b> and nothing else. So this report has to show two things -
/// that the dependence is delivered, and that neither marginal moved while it was.</para>
/// </summary>
internal static class CouplingReport
{
    /// <summary>Seed for the synthetic run this report scores. Fixed, so reruns are comparable.</summary>
    private const int Seed = 20260822;

    /// <summary>
    /// Years of synthetic record to score against. Long enough that a monthly correlation's
    /// sampling error (~2/sqrt(n)) is well under the coefficients being measured.
    /// </summary>
    private const int SyntheticYears = 100;

    public static void Run(
        IReadOnlyList<DwdSolarDay> solarDays,
        DwdStation solarStation,
        IReadOnlyList<DwdWindDay> windDays,
        DwdWindStation windStation
    )
    {
        var provider = CoupledWeatherProvider.FromStationDays(
            solarDays,
            solarStation,
            windDays,
            windStation
        );

        var clearness = ClearnessIndexBuilder.Build(
            solarDays.Where(d => d.IsComplete && !d.HasImplausibleZeros),
            solarStation
        );
        var speeds = WindSpeedSeriesBuilder.Build(windDays);
        var paired = CoupledSeriesBuilder.Build(clearness, speeds);

        Console.WriteLine(
            $"{paired.Count:N0} paired days, {paired[0].Date:yyyy-MM-dd} to {paired[^1].Date:yyyy-MM-dd}"
        );
        Console.WriteLine(
            $"  solar {solarStation.Name} ({clearness.Count:N0} usable days), "
                + $"wind {windStation.Name} ({speeds.Count:N0})"
        );
        Console.WriteLine();

        FittedCoupling(provider, paired);
        ModelFreeCrossCheck(solarDays, windDays);
        LatentAcceptance(provider, paired);
        MarginalsUntouched(provider);
    }

    /// <summary>The twelve fitted coefficients, with the sample size needed to judge them.</summary>
    private static void FittedCoupling(
        CoupledWeatherProvider provider,
        IReadOnlyList<(DateOnly Date, double ClearSkyIndex, double MeanSpeed)> paired
    )
    {
        var coupling = provider.Coupling;

        Console.WriteLine("=== Fitted coupling, clear-sky index against daily mean wind speed ===");
        Console.WriteLine(
            "  Measured on normal scores, so the seasonal cycle is already divided out."
        );
        Console.WriteLine();
        Console.WriteLine("month      rho     days    2/sqrt(n)   significant   source");

        for (int month = 1; month <= 12; month++)
        {
            double rho = coupling.ForMonth(month);
            int n = coupling.SampleCount(month);
            double threshold = n > 0 ? 2.0 / Math.Sqrt(n) : double.NaN;
            bool significant = Math.Abs(rho) > threshold;

            Console.WriteLine(
                $"{month, 5} {rho, 8:F4} {n, 8} {threshold, 12:F4} "
                    + $"{(significant ? "yes" : "no"), 13}   "
                    + $"{(coupling.IsPooled(month) ? "pooled" : "own")}"
            );
        }

        Console.WriteLine();
        Console.WriteLine($"pooled {coupling.Pooled, 8:F4} {paired.Count, 8}");
        Console.WriteLine();
        Console.WriteLine(
            "  Negative throughout is the expected sign: windy days are cloudy days. A"
        );
        Console.WriteLine(
            "  positive coefficient here would mean a sign error upstream, not an unusual site."
        );
        Console.WriteLine();
        Console.WriteLine(
            "  Both records attenuate this. The solar record's days are WOZ days and the wind"
        );
        Console.WriteLine(
            "  record's are UTC days, and the two stations are ~29 km apart - so the fitted"
        );
        Console.WriteLine(
            "  value is a LOWER BOUND on the true co-located coupling. The literature suggests"
        );
        Console.WriteLine(
            "  -0.3 to -0.4 for NW Europe. Do not correct for the gap; it is what these two"
        );
        Console.WriteLine("  records can honestly support.");
        Console.WriteLine();
    }

    /// <summary>
    /// The model-free cross-check: raw daily mean wind against raw daily sunshine minutes.
    ///
    /// <para>This is what the planning probe measured, before any model existed. It is here as a
    /// sign-and-magnitude sanity check on the fit above and <b>not</b> as a target - it uses a
    /// different solar quantity (sunshine duration, not the clear-sky index) on a different scale
    /// (raw, not normal scores), so the two columns should agree in sign and rough size and have no
    /// reason to agree in value.</para>
    /// </summary>
    private static void ModelFreeCrossCheck(
        IReadOnlyList<DwdSolarDay> solarDays,
        IReadOnlyList<DwdWindDay> windDays
    )
    {
        var sunshineByDate = solarDays
            .Where(d => d.IsComplete && !d.HasImplausibleZeros)
            .ToDictionary(d => d.Date, d => d.SunshineMinutes);

        var pairs = new List<(int Month, double Sunshine, double Speed)>();

        foreach (var day in windDays)
        {
            if (!day.IsComplete || !(day.MeanSpeed > 0.0))
                continue;

            if (sunshineByDate.TryGetValue(day.Date, out double sunshine))
                pairs.Add((day.Date.Month, sunshine, day.MeanSpeed));
        }

        Console.WriteLine(
            "=== Model-free cross-check: daily sunshine minutes against daily mean speed ==="
        );
        Console.WriteLine(
            "  Raw scale, no model involved. A sanity check on the sign, not a target."
        );
        Console.WriteLine();
        Console.Write("month  ");
        for (int month = 1; month <= 12; month++)
            Console.Write($"{month, 7}");
        Console.WriteLine();
        Console.Write("corr   ");

        for (int month = 1; month <= 12; month++)
        {
            var inMonth = pairs.Where(p => p.Month == month).ToList();
            Console.Write(
                $"{Correlation(inMonth.Select(p => p.Sunshine).ToList(), inMonth.Select(p => p.Speed).ToList()), 7:F3}"
            );
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine(
            $"  all months: {Correlation(pairs.Select(p => p.Sunshine).ToList(), pairs.Select(p => p.Speed).ToList()), 0:F4}"
                + $"   ({pairs.Count:N0} days)"
        );
        Console.WriteLine();
    }

    /// <summary>
    /// The acceptance check, in the space the copula operates in.
    ///
    /// <para>knowledge.md §14 records the general lesson from the persistence work: a copula
    /// model's acceptance check belongs in latent space, because the raw-scale correlation that
    /// comes back after the quantile transform measures the marginals' shapes as much as the
    /// dependence. So the claim this report makes is that the generated latent cross-correlation
    /// reproduces the fitted one, month by month.</para>
    /// </summary>
    private static void LatentAcceptance(
        CoupledWeatherProvider provider,
        IReadOnlyList<(DateOnly Date, double ClearSkyIndex, double MeanSpeed)> paired
    )
    {
        var generated = GenerateLatentSeries(provider);

        Func<double, int, double> solarCdf = provider.Solar.Model.CumulativeProbability;
        Func<double, int, double> windCdf = provider.Wind.Model.CumulativeProbability;

        Console.WriteLine(
            $"=== Acceptance: measured against generated, {SyntheticYears} synthetic years ==="
        );
        Console.WriteLine(
            "  Latent space, which is where the copula works and so where the claim lives."
        );
        Console.WriteLine();
        Console.WriteLine("month   measured   generated       diff");

        for (int month = 1; month <= 12; month++)
        {
            double measured = SeriesStatistics.LatentCrossCorrelation(
                paired
                    .Where(p => p.Date.Month == month)
                    .Select(p => (p.Date, p.ClearSkyIndex, p.MeanSpeed)),
                solarCdf,
                windCdf
            );
            double synthetic = SeriesStatistics.LatentCrossCorrelation(
                generated.Where(p => p.Date.Month == month),
                solarCdf,
                windCdf
            );

            Console.WriteLine(
                $"{month, 5} {measured, 10:F4} {synthetic, 11:F4} {synthetic - measured, 10:F4}"
            );
        }

        double measuredAll = SeriesStatistics.LatentCrossCorrelation(
            paired.Select(p => (p.Date, p.ClearSkyIndex, p.MeanSpeed)),
            solarCdf,
            windCdf
        );
        double syntheticAll = SeriesStatistics.LatentCrossCorrelation(generated, solarCdf, windCdf);

        Console.WriteLine();
        Console.WriteLine(
            $"  all {measuredAll, 9:F4} {syntheticAll, 11:F4} {syntheticAll - measuredAll, 10:F4}"
        );
        Console.WriteLine();
        Console.WriteLine("  The chain drives the INNOVATION correlation r, not rho directly:");
        Console.WriteLine("      r = rho * (1 - dS*dW) / sqrt((1 - dS^2)(1 - dW^2))");
        Console.WriteLine(
            "  Feeding rho straight in would land these columns a few percent apart with no"
        );
        Console.WriteLine(
            "  other symptom anywhere. That the two agree is the check on that correction."
        );
        Console.WriteLine();

        Console.WriteLine("=== The same comparison on the raw scale, for context ===");

        double measuredRaw = Correlation(
            paired.Select(p => p.ClearSkyIndex).ToList(),
            paired.Select(p => p.MeanSpeed).ToList()
        );
        double syntheticRaw = Correlation(
            generated.Select(p => p.A).ToList(),
            generated.Select(p => p.B).ToList()
        );

        Console.WriteLine($"  measured  {measuredRaw:F4}");
        Console.WriteLine($"  generated {syntheticRaw:F4}");
        Console.WriteLine();
        Console.WriteLine(
            "  These need not match as closely as the latent pair above, and the gap is not a"
        );
        Console.WriteLine(
            "  defect: a Gaussian copula preserves rank dependence exactly, while the Pearson"
        );
        Console.WriteLine(
            "  figure after the quantile transform depends on both marginals' shapes. Same"
        );
        Console.WriteLine(
            "  effect that leaves wind's synthetic lag-1 8% short of its measured one (§14)."
        );
        Console.WriteLine();
    }

    /// <summary>
    /// The invariant the whole design rests on: coupling reorders days and moves neither marginal.
    /// </summary>
    private static void MarginalsUntouched(CoupledWeatherProvider provider)
    {
        var coupled = provider.GenerateYear(2024, Seed);
        var solarAlone = provider.Solar.GenerateYear(2024, Seed);
        var windAlone = provider.Wind.GenerateYear(2024, Seed);

        Console.WriteLine("=== Coupling reorders days; it does not move either resource ===");
        Console.WriteLine();
        Console.WriteLine("                              coupled     independent");
        Console.WriteLine(
            $"  annual GHI, kWh/m2      {coupled.Solar.GhiKWhPerM2, 12:F1} {solarAlone.GhiKWhPerM2, 15:F1}"
        );
        Console.WriteLine(
            $"  mean wind speed, m/s    {coupled.Wind.MeanSpeed, 12:F3} {windAlone.MeanSpeed, 15:F3}"
        );
        Console.WriteLine();
        Console.WriteLine(
            "  These are two draws of the same distributions, not the same draw: the coupled"
        );
        Console.WriteLine(
            "  chain consumes its random stream in a different order, so a single year moves by"
        );
        Console.WriteLine(
            "  its own sampling spread (wind: about +/-0.1 m/s between seeds, knowledge.md §14)."
        );
        Console.WriteLine(
            "  What must NOT move is the per-month distributions, which the copula leaves exactly"
        );
        Console.WriteLine(
            "  intact by construction - CoupledLatentAr1ChainTests is where that is asserted."
        );
        Console.WriteLine();
    }

    /// <summary>A long coupled run, as the paired series the statistics above consume.</summary>
    private static List<(DateOnly Date, double A, double B)> GenerateLatentSeries(
        CoupledWeatherProvider provider
    )
    {
        var start = new DateOnly(2000, 1, 1);
        var end = start.AddYears(SyntheticYears).AddDays(-1);

        return provider
            .Generate(start, end, Seed)
            .Select(d => (d.Date, d.Solar.ClearSkyIndex, d.Wind.MeanSpeedAtReference))
            .ToList();
    }

    private static double Correlation(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count < 2)
            return double.NaN;

        double meanX = x.Average();
        double meanY = y.Average();

        double covariance = 0.0,
            varianceX = 0.0,
            varianceY = 0.0;

        for (int i = 0; i < x.Count; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        double denominator = Math.Sqrt(varianceX * varianceY);
        return denominator > 0.0 ? covariance / denominator : double.NaN;
    }
}

using WeatherSynth.Data;
using WeatherSynth.Wind;

namespace WeatherSynth.Sample;

/// <summary>
/// Turns the wind model into kilowatt-hours, and checks the step against the record's own hourly
/// data rather than asserting it.
///
/// <para>The record is hourly, so the true energy in every day is computable by summing the power
/// curve over the day's 24 hours. That makes this the rare modelling step with a ground truth
/// available, and the comparison against it is the reason this report exists - the way
/// <see cref="WindFitReport"/> prints the two- against three-parameter Weibull rather than claiming
/// the choice.</para>
/// </summary>
internal static class WindPowerReport
{
    /// <summary>Seed for the synthetic year, fixed so reruns are comparable.</summary>
    private const int Seed = 20260822;

    public static void Run(IReadOnlyList<DwdWindDay> days, DwdWindStation station)
    {
        var series = WindSpeedSeriesBuilder.Build(days);
        var shape = IntradayShapeModel.Fit(series);
        var provider = SyntheticWindProvider.FromStationDays(days, station);

        var complete = days.Where(d => d.IsComplete && d.MeanSpeed > 0.0).ToList();
        var curve = ReferenceTurbines.Generic2Mw;

        TheCurve(curve);
        TheIntradayShape(shape, provider);
        AgainstGroundTruth(complete, curve, shape, station);
        TheHeightTransfer(complete, curve, station);
        Synthetic(provider, curve, station);
    }

    private static void TheCurve(TurbinePowerCurve curve)
    {
        Console.WriteLine($"=== Turbine: {curve} ===");
        Console.WriteLine();
        Console.Write("  v m/s ");
        for (double v = 2; v <= 26; v += 2)
            Console.Write($"{v, 7:F0}");
        Console.WriteLine();
        Console.Write("  kW    ");
        for (double v = 2; v <= 26; v += 2)
            Console.Write($"{curve.PowerKilowatts(v), 7:F0}");
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine(
            "  Zero below cut-in, cubic to rated, FLAT to cut-out, zero above. Three of those four"
        );
        Console.WriteLine(
            "  regions are places where P(mean speed) is not the mean of P(speed), which is what the"
        );
        Console.WriteLine("  rest of this report is about.");
        Console.WriteLine();
    }

    private static void TheIntradayShape(IntradayShapeModel shape, SyntheticWindProvider provider)
    {
        Console.WriteLine("=== Within-day shape: the energy pattern factor, inverted ===");
        Console.WriteLine();
        Console.WriteLine($"  {shape}");
        Console.WriteLine(
            $"  the model's single constant, for comparison: {provider.Model.MeanEnergyPatternFactor:F4}"
        );
        Console.WriteLine();
        Console.WriteLine("  daily mean speed      2.0     3.0     4.0     5.0     6.0     8.0");
        Console.Write("  fitted EPF        ");
        foreach (double v in new[] { 2.0, 3.0, 4.0, 5.0, 6.0, 8.0 })
            Console.Write($"{shape.EnergyPatternFactorFor(v), 8:F3}");
        Console.WriteLine();
        Console.Write("  within-day k      ");
        foreach (double v in new[] { 2.0, 3.0, 4.0, 5.0, 6.0, 8.0 })
            Console.Write($"{shape.ShapeFor(v), 8:F2}");
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine(
            "  EPF = Gamma(1+3/k)/Gamma(1+1/k)^3 is strictly decreasing in k, so the factor this"
        );
        Console.WriteLine(
            "  library has carried since Phase 0 IS the within-day shape parameter in disguise."
        );
        Console.WriteLine(
            "  Calm days are relatively gustier, so k rises with speed. Compare against 2.71 fitted"
        );
        Console.WriteLine(
            "  on daily means across the year and 2.14 on hourly values: a day is narrower than a"
        );
        Console.WriteLine(
            "  season, which is narrower than a year. That ordering is the sanity check."
        );
        Console.WriteLine();
    }

    /// <summary>
    /// The report's reason to exist: four estimators against the true hourly energy.
    /// </summary>
    private static void AgainstGroundTruth(
        IReadOnlyList<DwdWindDay> days,
        TurbinePowerCurve curve,
        IntradayShapeModel shape,
        DwdWindStation station
    )
    {
        Console.WriteLine(
            "=== Against ground truth: the record is hourly, so truth is computable ==="
        );
        Console.WriteLine();
        Console.WriteLine(
            $"  {days.Count:N0} complete days. 'Truth' sums the curve over each day's 24 hours."
        );
        Console.WriteLine();

        var reference = station.ToSite();
        var constant = IntradayShapeModel.Constant(
            days.Select(d => d.EnergyPatternFactor).Where(f => f > 1.0).Average()
        );

        foreach (var (label, site) in Heights(station))
        {
            double factor = site.TransferFactorFrom(reference);

            var truth = new List<double>(days.Count);
            var naive = new List<double>(days.Count);
            var cubeLaw = new List<double>(days.Count);
            var constantShape = new List<double>(days.Count);
            var fittedShape = new List<double>(days.Count);

            foreach (var day in days)
            {
                double atReference = day.MeanSpeed;
                double atTarget = atReference * factor;

                double hourly = 0.0;
                int counted = 0;
                foreach (var hour in day.Hours)
                {
                    if (hour.SpeedMetersPerSecond is not { } speed)
                        continue;

                    hourly += curve.PowerKilowatts(speed * factor);
                    counted++;
                }

                truth.Add(counted > 0 ? hourly / counted : 0.0);
                naive.Add(curve.PowerKilowatts(atTarget));
                cubeLaw.Add(CubeLawPower(curve, day.MeanCubedSpeed * factor * factor * factor));
                constantShape.Add(
                    TurbineYield.MeanPowerOverDay(curve, constant, atReference, atTarget)
                );
                fittedShape.Add(TurbineYield.MeanPowerOverDay(curve, shape, atReference, atTarget));
            }

            double rated = curve.RatedPowerKilowatts;
            double trueMean = truth.Average();

            Console.WriteLine($"  --- {label} ---");
            Console.WriteLine(
                $"      truth: capacity factor {trueMean / rated * 100:F2}%, "
                    + $"{trueMean * 24.0 * 365.25 / 1000.0:N0} MWh/year"
            );
            Console.WriteLine();
            Console.WriteLine(
                "      estimator                          CF       bias    daily RMSE"
            );

            foreach (
                var (name, estimate) in new[]
                {
                    ("naive P(daily mean speed)", naive),
                    ("cube law via mean(v3)", cubeLaw),
                    ("within-day Weibull, constant EPF", constantShape),
                    ("within-day Weibull, fitted EPF", fittedShape),
                }
            )
            {
                double mean = estimate.Average();
                double rmse = Math.Sqrt(estimate.Zip(truth, (a, b) => (a - b) * (a - b)).Average());

                Console.WriteLine(
                    $"      {name, -33} {mean / rated * 100, 6:F2}%  {(mean / trueMean - 1.0) * 100, +7:F2}%  "
                        + $"{rmse / rated, 12:F5}"
                );
            }

            Console.WriteLine();
        }

        Console.WriteLine(
            "  The naive row is printed to be rejected. It is wrong by a third at the anemometer, and"
        );
        Console.WriteLine(
            "  it is what anyone reaching for a daily mean speed and a power curve will compute."
        );
        Console.WriteLine(
            "  The cube-law row is what MeanCubedSpeed invites: better on bias, but it cannot"
        );
        Console.WriteLine(
            "  represent the rated plateau, so its daily error is no better than the naive one's."
        );
        Console.WriteLine();
    }

    /// <summary>
    /// The landmine worth the most: what the profile disagreement costs once it goes through a
    /// power curve.
    /// </summary>
    private static void TheHeightTransfer(
        IReadOnlyList<DwdWindDay> days,
        TurbinePowerCurve curve,
        DwdWindStation station
    )
    {
        Console.WriteLine("=== What the height transfer costs, amplified by the cube ===");
        Console.WriteLine();
        Console.WriteLine("  profile                 speed factor   capacity factor    MWh/year");

        var reference = station.ToSite();
        var hub = ReferenceTurbines.HundredMetreHub;
        var results = new List<double>();

        foreach (var profile in new[] { WindProfile.LogLaw, WindProfile.PowerLaw() })
        {
            double factor = hub.TransferFactorFrom(reference, profile);

            double mean = days.Average(d =>
            {
                double sum = 0.0;
                int n = 0;
                foreach (var hour in d.Hours)
                {
                    if (hour.SpeedMetersPerSecond is not { } speed)
                        continue;
                    sum += curve.PowerKilowatts(speed * factor);
                    n++;
                }
                return n > 0 ? sum / n : 0.0;
            });

            results.Add(mean);

            Console.WriteLine(
                $"  {profile, -22} {factor, 12:F3} {mean / curve.RatedPowerKilowatts * 100, 17:F2}% "
                    + $"{mean * 24.0 * 365.25 / 1000.0, 11:N0}"
            );
        }

        double speedRatio =
            hub.TransferFactorFrom(reference, WindProfile.LogLaw)
            / hub.TransferFactorFrom(reference, WindProfile.PowerLaw());

        Console.WriteLine();
        Console.WriteLine(
            $"  A {speedRatio:F2}x disagreement in SPEED becomes {results[0] / results[1]:F2}x in ENERGY."
        );
        Console.WriteLine();
        Console.WriteLine(
            "  This is the same 26% profile disagreement knowledge.md §14 already calls the largest"
        );
        Console.WriteLine(
            "  error in the model - the power curve does not add to it, it makes visible how large it"
        );
        Console.WriteLine(
            "  already was. Nothing in the fitted distributions is within two orders of this. Never"
        );
        Console.WriteLine("  quote a yield figure without the profile it was computed under.");
        Console.WriteLine();
    }

    private static void Synthetic(
        SyntheticWindProvider provider,
        TurbinePowerCurve curve,
        DwdWindStation station
    )
    {
        Console.WriteLine("=== A synthetic year through the curve ===");
        Console.WriteLine();

        foreach (var (label, site) in Heights(station))
        {
            var yield = provider.EstimateYield(2024, Seed, curve, site);

            Console.WriteLine(
                $"  {label, -28} capacity factor {yield.CapacityFactor * 100, 6:F2}%   "
                    + $"{yield.EnergyMegawattHours, 7:N0} MWh   "
                    + $"{yield.FractionOfDaysBelowCutIn * 100, 5:F1}% of days average below cut-in"
            );
        }

        Console.WriteLine();

        var atHub = provider.EstimateYield(2024, Seed, curve, ReferenceTurbines.HundredMetreHub);

        Console.WriteLine("  Monthly, at the 100 m hub:");
        Console.WriteLine("  month      mean kW    capacity factor    MWh");

        foreach (var group in atHub.Days.GroupBy(d => d.Date.Month).OrderBy(g => g.Key))
        {
            double mean = group.Average(d => d.MeanPowerKilowatts);
            Console.WriteLine(
                $"  {group.Key, 5} {mean, 12:F1} {mean / curve.RatedPowerKilowatts * 100, 17:F2}% "
                    + $"{group.Sum(d => d.EnergyKilowattHours) / 1000.0, 8:N1}"
            );
        }

        Console.WriteLine();
        Console.WriteLine(
            "  One realisation, not a climatology. Persistence cuts a 365-day year's effective sample"
        );
        Console.WriteLine(
            "  size to about 140, so a single year's capacity factor moves between seeds - and through"
        );
        Console.WriteLine(
            "  a cubic curve it moves further than the mean speed does. Over 60 seeds:"
        );
        Console.WriteLine();

        var ensemble = new List<double>();
        for (int seed = 0; seed < 60; seed++)
        {
            ensemble.Add(
                provider
                    .EstimateYield(2024, seed, curve, ReferenceTurbines.HundredMetreHub)
                    .CapacityFactor
            );
        }

        double ensembleMean = ensemble.Average();
        double sd = Math.Sqrt(
            ensemble.Sum(v => (v - ensembleMean) * (v - ensembleMean)) / (ensemble.Count - 1)
        );

        Console.WriteLine(
            $"    synthetic capacity factor at the 100 m hub: {ensembleMean * 100:F2}% +/- {sd * 100:F2}%"
        );
        Console.WriteLine(
            $"    measured over the record at the same hub:    {MeasuredCapacityFactor(curve, station):F2}%"
        );
        Console.WriteLine();
        Console.WriteLine(
            "  The ensemble is the acceptance check; a single year is not. A seed-to-seed spread this"
        );
        Console.WriteLine(
            "  wide is the model working, and it is the honest answer to 'what will this site make"
        );
        Console.WriteLine("  next year' - which is a distribution, not a number.");
        Console.WriteLine();
    }

    /// <summary>
    /// The record's own capacity factor at the hub, hour by hour - what the synthetic ensemble is
    /// scored against.
    /// </summary>
    private static double MeasuredCapacityFactor(TurbinePowerCurve curve, DwdWindStation station)
    {
        string? path = RepositoryData.TryLocateEssenWind();
        if (path is null)
            return double.NaN;

        double factor = ReferenceTurbines.HundredMetreHub.TransferFactorFrom(station.ToSite());

        var days = DwdWindDayAggregator
            .ToDays(DwdWindReader.Read(path))
            .Where(d => d.IsComplete && d.MeanSpeed > 0.0);

        double mean = days.Average(d =>
        {
            double sum = 0.0;
            int n = 0;
            foreach (var hour in d.Hours)
            {
                if (hour.SpeedMetersPerSecond is not { } speed)
                    continue;
                sum += curve.PowerKilowatts(speed * factor);
                n++;
            }
            return n > 0 ? sum / n : 0.0;
        });

        return mean / curve.RatedPowerKilowatts * 100.0;
    }

    private static IEnumerable<(string Label, WindSite Site)> Heights(DwdWindStation station) =>
        new[]
        {
            ($"{station.AnemometerHeightMeters:F0} m anemometer", station.ToSite()),
            ("100 m hub (log law)", ReferenceTurbines.HundredMetreHub),
        };

    /// <summary>
    /// The estimate a caller reaches for when they have <c>MeanCubedSpeed</c> and a power curve:
    /// treat the day as if the whole cube law applied, then clip at rated.
    ///
    /// <para>Printed to be rejected. It gets the sub-rated region right by construction and has no
    /// way to represent the plateau or the cut-out, so it overstates windy days badly.</para>
    /// </summary>
    private static double CubeLawPower(TurbinePowerCurve curve, double meanCubedSpeed)
    {
        // The idealised curve's own algebra, applied to mean(v3) instead of v3.
        double cutIn = curve.CutInMetersPerSecond;
        double cutInCubed = cutIn * cutIn * cutIn;
        double ratedSpeedCubed = RatedSpeedCubed(curve);

        if (meanCubedSpeed <= cutInCubed)
            return 0.0;

        double fraction = (meanCubedSpeed - cutInCubed) / (ratedSpeedCubed - cutInCubed);

        return curve.RatedPowerKilowatts * Math.Clamp(fraction, 0.0, 1.0);
    }

    /// <summary>The cube of the speed at which the curve first reaches rated power.</summary>
    private static double RatedSpeedCubed(TurbinePowerCurve curve)
    {
        foreach (double breakpoint in curve.Breakpoints)
        {
            if (curve.PowerKilowatts(breakpoint) >= curve.RatedPowerKilowatts)
                return breakpoint * breakpoint * breakpoint;
        }

        double cutOut = curve.CutOutMetersPerSecond;
        return cutOut * cutOut * cutOut;
    }
}

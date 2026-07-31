using WeatherModel.Climate;

namespace WeatherModel.Core.Tests;

/// <summary>
/// The synthetic record the distribution and persistence suites are both built on, and the
/// goodness-of-fit measure they both score against.
///
/// <para>Shared rather than copied, because the two suites only mean anything against each other:
/// the chain tests assert that persistence leaves the marginals alone, which is only a statement
/// about the marginals the model tests fit. Two private copies of the same seeded generator would
/// let them drift apart silently.</para>
/// </summary>
internal static class ClimateFixtures
{
    /// <summary>The Beta the fixture record draws from on a given date. Peaks in July, troughs in January.</summary>
    internal static ScaledBeta SeasonalShape(DateOnly date)
    {
        double seasonal = 0.55 + 0.20 * Math.Cos((date.DayOfYear - 196) / 365.25 * 2.0 * Math.PI);
        return new ScaledBeta(seasonal * 8.0, (1.0 - seasonal) * 8.0, ClearSkyIndexModel.DefaultSupport);
    }

    /// <summary>A record with a deliberate seasonal swing and no day-to-day persistence.</summary>
    internal static List<DailyClearness> SeasonalSeries(int years = 10)
    {
        var random = new Random(4242);
        var series = new List<DailyClearness>();

        for (var date = new DateOnly(2010, 1, 1); date.Year < 2010 + years; date = date.AddDays(1))
        {
            double index = SeasonalShape(date).Sample(random);
            series.Add(new DailyClearness(date, index * 5000.0, 5000.0, 8000.0));
        }

        return series;
    }

    /// <summary>
    /// The model fitted from <see cref="SeasonalSeries"/>, built once.
    ///
    /// <para>Fitting it costs twelve Beta fits and a full normal-score persistence pass over
    /// 3,652 days. The model is immutable and the construction is deterministic, so the eleven
    /// chain tests that need one can share it. Constructed from a record rather than directly
    /// because <see cref="ClearSkyIndexModel"/> has no public constructor.</para>
    /// </summary>
    internal static ClearSkyIndexModel SeasonalModel => LazySeasonalModel.Value;

    private static readonly Lazy<ClearSkyIndexModel> LazySeasonalModel =
        new(() => ClearSkyIndexModel.Fit(SeasonalSeries()));

    /// <summary>
    /// Largest absolute gap between the empirical CDF of <paramref name="values"/> and the fitted
    /// one. Checked on both sides of each step, since the empirical CDF jumps at every observation
    /// and the larger gap can be on either side of the jump.
    ///
    /// <para>Compare against <c>1.36 / sqrt(n)</c> for a 5% test. That critical value scales with
    /// n, so the check means the same thing at any sample size.</para>
    /// </summary>
    internal static double KolmogorovSmirnov(IEnumerable<double> values, ScaledBeta fit)
    {
        var sorted = values.OrderBy(v => v).ToList();
        double worst = 0.0;

        for (int i = 0; i < sorted.Count; i++)
        {
            double fitted = fit.CumulativeProbability(sorted[i]);
            worst = Math.Max(worst, Math.Abs((i + 1.0) / sorted.Count - fitted));
            worst = Math.Max(worst, Math.Abs(fitted - (double)i / sorted.Count));
        }

        return worst;
    }
}

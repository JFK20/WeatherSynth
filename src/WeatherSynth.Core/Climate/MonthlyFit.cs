using WeatherSynth.Statistics;

namespace WeatherSynth.Climate;

/// <summary>
/// The fitting sequence both marginal models share: twelve monthly fits with a pooled fallback,
/// then the persistence measured through them.
///
/// <para>The distribution family is the only thing that differs between the solar and the wind
/// model, so it is passed in as a fit function and a CDF. What each model filters out of its
/// series, and what extra it collects on the way (wind's energy pattern factors), stays with the
/// model.</para>
/// </summary>
internal static class MonthlyFit
{
    /// <summary>
    /// Fits twelve monthly distributions, a pooled one, and the latent persistence.
    /// </summary>
    /// <param name="dated">
    /// Usable days, already filtered and materialised, in record order. Must not be empty.
    /// </param>
    /// <param name="fit">Fits one distribution to a set of values.</param>
    /// <param name="cumulativeProbability">The CDF of a fitted distribution at a value.</param>
    /// <param name="minimumSamplesPerMonth">
    /// Fewer observations than this in a month and the pooled fit stands in for it.
    /// </param>
    public static (TDist[] Monthly, TDist Pooled, double Persistence) Fit<TDist>(
        IReadOnlyList<(DateOnly Date, double Value)> dated,
        Func<IEnumerable<double>, TDist> fit,
        Func<TDist, double, double> cumulativeProbability,
        int minimumSamplesPerMonth
    )
    {
        var byMonth = new List<double>[12];
        for (int i = 0; i < 12; i++)
            byMonth[i] = new List<double>();

        foreach (var (date, value) in dated)
            byMonth[date.Month - 1].Add(value);

        var pooled = fit(dated.Select(d => d.Value));

        var monthly = new TDist[12];
        for (int i = 0; i < 12; i++)
            monthly[i] = byMonth[i].Count >= minimumSamplesPerMonth ? fit(byMonth[i]) : pooled;

        // Order matters: phi is measured through the monthly CDFs, so they have to exist first.
        // The same ordering constraint the clear-sky ceiling imposes on the Betas, one level
        // further up.
        double persistence = SeriesStatistics.LatentPersistence(
            dated,
            (value, month) => cumulativeProbability(monthly[month - 1], value)
        );

        return (monthly, pooled, persistence);
    }

    /// <summary>
    /// Checks a set of stored monthly fits and copies it, for the <c>FromCoefficients</c> seams.
    /// </summary>
    public static TDist[] RequireTwelve<TDist>(IReadOnlyList<TDist> monthly, string paramName)
    {
        ArgumentNullException.ThrowIfNull(monthly, paramName);
        if (monthly.Count != 12)
            throw new ArgumentException(
                $"Expected twelve monthly fits, got {monthly.Count}.",
                paramName
            );

        return monthly.ToArray();
    }
}

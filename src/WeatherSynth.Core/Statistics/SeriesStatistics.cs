namespace WeatherSynth.Statistics;

/// <summary>
/// Diagnostics that describe a series of daily values rather than fit anything to it.
///
/// <para>Quantity-agnostic, and kept that way deliberately: a clear-sky index and a wind speed
/// are the same shape of thing here - one number a day, with gaps - and the persistence
/// question asked of both is identical.</para>
/// </summary>
internal static class SeriesStatistics
{
    /// <summary>
    /// Correlation between each day's value and the previous day's.
    ///
    /// <para>The single number that says whether a generator reproduces weather persistence.
    /// Independent sampling gives close to zero by construction whatever its histogram looks
    /// like; measured daily solar records land around 0.3-0.5 and daily wind speeds higher
    /// still. This is the acceptance check on <see cref="Climate.LatentAr1Chain"/>, and it is also
    /// what fits the chain's coefficient - run over a record's normal scores it yields
    /// <see cref="Climate.IMonthlyMarginals.Persistence"/> directly.</para>
    ///
    /// <para>Only genuinely consecutive calendar days count as pairs, so gaps in the record
    /// are skipped rather than being treated as adjacent.</para>
    /// </summary>
    /// <returns>The lag-1 correlation, or NaN if there are fewer than two consecutive pairs.</returns>
    public static double Lag1Autocorrelation(IEnumerable<(DateOnly Date, double Value)> series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var ordered = series.OrderBy(d => d.Date).ToList();

        var today = new List<double>();
        var yesterday = new List<double>();

        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Date.DayNumber - ordered[i - 1].Date.DayNumber != 1)
                continue;
            if (double.IsNaN(ordered[i].Value) || double.IsNaN(ordered[i - 1].Value))
                continue;

            today.Add(ordered[i].Value);
            yesterday.Add(ordered[i - 1].Value);
        }

        if (today.Count < 2)
            return double.NaN;

        return Correlation(yesterday, today);
    }

    /// <summary>
    /// Ceiling on a fitted persistence coefficient. A latent AR(1) needs |phi| &lt; 1 to be
    /// stationary, and anything this close to 1 is a sign the estimate has gone wrong rather
    /// than a site with extraordinary weather.
    /// </summary>
    public const double MaximumPersistence = 0.99;

    /// <summary>
    /// The lag-1 coefficient of the latent AR(1) process behind a dated series, fitted through
    /// that series' own monthly marginals.
    ///
    /// <para>Each day is mapped through its own month's fitted CDF, giving a value that is
    /// uniform if the fit is good, and then through the inverse normal. Because the transform
    /// is per month, the seasonal cycle comes out with it and what is left carries weather
    /// persistence alone - so the lag-1 correlation of the scores <i>is</i> phi, with no
    /// simulation loop or search required. Fitting against the raw series instead would count
    /// the season twice, since the twelve marginals re-supply it downstream.</para>
    ///
    /// <para>Quantity-agnostic, and shared by both halves of the library: a clear-sky index
    /// and a wind speed differ only in the CDF passed in. The caller supplies a delegate
    /// rather than an <see cref="Climate.IMonthlyMarginals"/> because this runs <i>during</i> a fit,
    /// before the model it belongs to exists.</para>
    /// </summary>
    /// <param name="series">Dated observations. NaN values must already be filtered out.</param>
    /// <param name="cumulativeProbability">
    /// The month's fitted CDF, called as <c>(value, month)</c> with a 1-12 month.
    /// </param>
    /// <returns>
    /// Phi in [0, <see cref="MaximumPersistence"/>]. A record too short or too broken to
    /// estimate from yields 0 - no persistence - rather than a NaN that would propagate into
    /// every generated day downstream. Negative persistence is not a thing daily weather does,
    /// so it clamps away too.
    /// </returns>
    public static double LatentPersistence(
        IEnumerable<(DateOnly Date, double Value)> series,
        Func<double, int, double> cumulativeProbability
    )
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(cumulativeProbability);

        var latent = new List<(DateOnly Date, double Score)>();

        foreach (var (date, value) in series)
            latent.Add((date, NormalScore(value, date.Month, cumulativeProbability)));

        // The gap-aware estimator above, which is exactly right here: only genuinely
        // consecutive days are informative about a lag-1 coefficient.
        double phi = Lag1Autocorrelation(latent);

        if (double.IsNaN(phi))
            return 0.0;

        return Math.Clamp(phi, 0.0, MaximumPersistence);
    }

    /// <summary>
    /// One observation mapped into latent space: through its own month's fitted CDF, and then
    /// through the inverse normal.
    ///
    /// <para>The single definition of "normal score" in this library, shared by the persistence
    /// fit above and the cross-correlation fit below. Both need it to mean exactly the same
    /// thing, because a coupling coefficient and a persistence coefficient are estimated in the
    /// same space and then driven by the same chain.</para>
    ///
    /// <para>A fitted CDF can reach 0 and 1 - exactly, at the ends of a Beta's support, or by
    /// rounding for a day far out in a Weibull's tail - and the inverse normal sends those to
    /// infinity. The nudge inside costs nothing at this magnitude.</para>
    /// </summary>
    /// <param name="value">The observation, in the model's own units.</param>
    /// <param name="month">Calendar month, 1-12.</param>
    /// <param name="cumulativeProbability">The month's fitted CDF, called as <c>(value, month)</c>.</param>
    public static double NormalScore(
        double value,
        int month,
        Func<double, int, double> cumulativeProbability
    )
    {
        ArgumentNullException.ThrowIfNull(cumulativeProbability);

        const double edge = 1e-12;

        double u = cumulativeProbability(value, month);
        return Gaussian.Quantile(Math.Clamp(u, edge, 1.0 - edge));
    }

    /// <summary>
    /// Correlation between two quantities observed on the same days, measured on their normal
    /// scores rather than on their raw values.
    ///
    /// <para>The cross-quantity counterpart of <see cref="LatentPersistence"/>, and it works in
    /// the same space for the same reason: mapping each day through its own month's fitted CDF
    /// removes the seasonal cycle from both series, so what is left is the day-to-day
    /// co-movement alone. Correlating raw daily wind speed against a raw clear-sky index would
    /// count the season twice over - both quantities have one, the twelve marginals re-supply
    /// both downstream, and the shared seasonality would inflate the estimate.</para>
    ///
    /// <para>This is also the space the coupled chain <i>drives</i> in, so the number this
    /// returns is directly the coefficient that chain needs - see
    /// <see cref="Climate.MonthlyCoupling"/>. No simulation loop, no search.</para>
    /// </summary>
    /// <param name="paired">
    /// Days on which both quantities were observed. Pair them before calling; an inner join is
    /// the caller's business, because what counts as "the same day" is a data question.
    /// </param>
    /// <param name="cumulativeProbabilityA">First quantity's fitted CDF, as <c>(value, month)</c>.</param>
    /// <param name="cumulativeProbabilityB">Second quantity's fitted CDF, as <c>(value, month)</c>.</param>
    /// <returns>
    /// The correlation of the normal scores, in [-1, 1], or NaN when fewer than two usable
    /// pairs were supplied.
    /// </returns>
    public static double LatentCrossCorrelation(
        IEnumerable<(DateOnly Date, double A, double B)> paired,
        Func<double, int, double> cumulativeProbabilityA,
        Func<double, int, double> cumulativeProbabilityB
    )
    {
        ArgumentNullException.ThrowIfNull(paired);
        ArgumentNullException.ThrowIfNull(cumulativeProbabilityA);
        ArgumentNullException.ThrowIfNull(cumulativeProbabilityB);

        var scoresA = new List<double>();
        var scoresB = new List<double>();

        foreach (var (date, a, b) in paired)
        {
            if (double.IsNaN(a) || double.IsNaN(b))
                continue;

            scoresA.Add(NormalScore(a, date.Month, cumulativeProbabilityA));
            scoresB.Add(NormalScore(b, date.Month, cumulativeProbabilityB));
        }

        // Unlike the lag-1 estimators above, gaps are irrelevant here: each pair is
        // self-contained, so a hole in the record costs a pair rather than corrupting one.
        if (scoresA.Count < 2)
            return double.NaN;

        return Correlation(scoresA, scoresB);
    }

    private static double Correlation(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
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

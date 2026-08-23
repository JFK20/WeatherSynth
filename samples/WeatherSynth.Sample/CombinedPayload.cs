using System.Text.Json.Nodes;
using WeatherSynth.Data;

namespace WeatherSynth.Sample;

/// <summary>
/// The measured half of the page's combined view: the two resources paired day by day.
///
/// <para>The page can build the <i>synthetic</i> pairing itself, by joining the two synthetic
/// tracks it already holds on their dates - and it should, because then the number it prints is
/// computed from exactly the points it draws. What it cannot build is the measured pairing, which
/// needs both records joined and neither is sent whole.</para>
///
/// <para>The join goes through <see cref="CoupledSeriesBuilder"/> rather than being redone here.
/// That is where the caveat lives: the solar record's days are true-solar-time days and the wind
/// record's are UTC days, the two stations are ~29 km apart, and both effects attenuate the
/// measured correlation. The figure this sends is a lower bound on the real one, and the page says
/// so rather than quietly presenting it as the truth.</para>
/// </summary>
internal static class CombinedPayload
{
    /// <summary>
    /// Cap on scatter points sent to the page.
    ///
    /// <para>The record pairs ~5,600 days, which draws fine - the lag panels already plot more -
    /// but a projection can run to 50 years and there is no reading a scatter that dense. Thinning
    /// is by a fixed stride rather than at random so the page is reproducible.</para>
    /// </summary>
    private const int MaximumPoints = 6000;

    public static JsonObject Build(
        IReadOnlyList<DwdSolarDay> solarDays,
        DwdStation station,
        IReadOnlyList<DwdWindDay> windDays,
        bool coupled
    )
    {
        var clearness = ClearnessIndexBuilder.Build(
            solarDays.Where(d => d.IsComplete && !d.HasImplausibleZeros),
            station
        );
        var speeds = WindSpeedSeriesBuilder.Build(windDays);
        var paired = CoupledSeriesBuilder.Build(clearness, speeds);

        int stride = Math.Max(1, paired.Count / MaximumPoints);

        var points = new JsonArray();
        for (int i = 0; i < paired.Count; i += stride)
        {
            points.Add(
                new JsonArray
                {
                    Math.Round(paired[i].ClearSkyIndex, 4),
                    Math.Round(paired[i].MeanSpeed, 3),
                }
            );
        }

        double correlation = Correlation(
            paired.Select(p => p.ClearSkyIndex).ToList(),
            paired.Select(p => p.MeanSpeed).ToList()
        );

        // Pooling twelve months puts windy-and-dull winter days in the same scatter as
        // calm-and-bright summer ones, and that shared season correlates them on its own - so the
        // pooled figure is roughly twice the day-to-day co-movement it looks like it measures.
        // knowledge.md §15 records the same trap on the fitted side, where the planning probe's
        // -0.221 turned out to be about half seasonal cycle.
        var monthly = new List<double>();
        foreach (var group in paired.GroupBy(p => p.Date.Month))
        {
            double r = Correlation(
                group.Select(p => p.ClearSkyIndex).ToList(),
                group.Select(p => p.MeanSpeed).ToList()
            );

            if (double.IsFinite(r))
                monthly.Add(r);
        }

        return new JsonObject
        {
            ["coupled"] = coupled,
            ["days"] = paired.Count,
            ["correlation"] = Math.Round(correlation, 4),
            ["withinMonth"] = Math.Round(monthly.Count > 0 ? monthly.Average() : double.NaN, 4),
            ["points"] = points,
            ["maximumPoints"] = MaximumPoints,
        };
    }

    /// <summary>
    /// Pearson correlation on the raw scale.
    ///
    /// <para>Raw rather than on normal scores, deliberately, and it is a different number from the
    /// rho the model is fitted with. The page draws raw index against raw speed, so the coefficient
    /// beside it has to describe those axes. knowledge.md §15 carries both: the fitted latent rho is
    /// -0.108, the raw-scale measured figure -0.215, and they differ because a Gaussian copula
    /// preserves rank dependence rather than Pearson.</para>
    /// </summary>
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

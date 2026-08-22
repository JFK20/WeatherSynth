using WeatherSynth.Climate;

namespace WeatherSynth.Data;

/// <summary>
/// Pairs the two records day by day, producing the series the coupling fit consumes.
/// </summary>
/// <remarks>
/// <para>An inner join and nothing more, but the join key is the part worth being careful about,
/// and the reason this is a named type rather than a LINQ expression at a call site.</para>
///
/// <para><b>The two records do not measure the same day.</b> The DWD solar product timestamps its
/// intervals in true solar time, so its calendar days are WOZ days; the wind product's are plain
/// UTC. They differ by up to about half an hour at the ends. And the two stations are different
/// places - Bochum has no wind record, so the wind fit happens at Essen-Bredeney, roughly 29 km
/// west.</para>
///
/// <para><b>Both effects attenuate the measured coupling</b>, so the fitted coefficient is a
/// <i>lower bound</i> on the true co-located one: some of the real co-movement is smeared across
/// the day boundary, and some is lost to 29 km of separate weather. The literature suggests -0.3
/// to -0.4 for north-west Europe against the -0.22 measured here, and that gap is largely this.
/// <b>Do not correct for it.</b> Any inflation factor would be an invention, and a coupling that
/// is slightly too weak is the honest version of what these two records can support.</para>
///
/// <para>Aligning on the date label - rather than attempting to re-window one record onto the
/// other's day boundaries - is the deliberate choice. Re-windowing would need sub-daily wind data
/// re-aggregated against a moving WOZ midnight, which recovers a fraction of a correlation of
/// -0.22 at the cost of a second aggregation path that could silently disagree with the one the
/// marginals were fitted on. The marginals and the coupling must see the same days.</para>
/// </remarks>
public static class CoupledSeriesBuilder
{
    /// <summary>
    /// Builds the paired series from two already-built daily series.
    /// </summary>
    /// <param name="clearness">
    /// The solar series, from <see cref="ClearnessIndexBuilder.Build"/>. Filter it the way the
    /// solar fit does before calling.
    /// </param>
    /// <param name="wind">The wind series, from <see cref="WindSpeedSeriesBuilder.Build"/>.</param>
    /// <returns>
    /// One entry per day present and usable in both records, in date order. Over the shipped
    /// 2009-2026 records this is roughly 5,500 of each side's ~5,800 and ~6,200 days.
    /// </returns>
    public static IReadOnlyList<(DateOnly Date, double ClearSkyIndex, double MeanSpeed)> Build(
        IEnumerable<DailyClearness> clearness,
        IEnumerable<DailyWindSpeed> wind
    )
    {
        if (clearness is null)
            throw new ArgumentNullException(nameof(clearness));
        if (wind is null)
            throw new ArgumentNullException(nameof(wind));

        var speedByDate = new Dictionary<DateOnly, double>();
        foreach (var day in wind)
        {
            if (double.IsNaN(day.MeanSpeed))
                continue;

            // Last one wins rather than throwing: a duplicated day is a data-splicing artefact
            // (the wind record is a historical file concatenated with a recent one), and one
            // duplicate should not deny the whole fit.
            speedByDate[day.Date] = day.MeanSpeed;
        }

        var paired = new List<(DateOnly Date, double ClearSkyIndex, double MeanSpeed)>();

        foreach (var day in clearness)
        {
            double index = day.ClearSkyIndex;
            if (double.IsNaN(index))
                continue;

            if (speedByDate.TryGetValue(day.Date, out double speed))
                paired.Add((day.Date, index, speed));
        }

        paired.Sort((left, right) => left.Date.CompareTo(right.Date));

        return paired;
    }
}

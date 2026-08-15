using WeatherSynth.Climate;

namespace WeatherSynth.Data;

/// <summary>
/// Turns measured station days into the daily wind-speed series the fit consumes.
/// </summary>
/// <remarks>
/// Much thinner than <see cref="ClearnessIndexBuilder"/>, and for a structural reason: there is
/// no ceiling to integrate and no ratio to form, so this is a filter and a projection rather than
/// a calculation. What it does carry is the decision about <i>which</i> days are admissible, which
/// is the part that would otherwise be re-made, differently, at each call site.
/// </remarks>
public static class WindSpeedSeriesBuilder
{
    /// <summary>
    /// Builds the daily wind-speed series for a station.
    ///
    /// <para><b>Complete days only.</b> A day averaged over some of its hours is not a daily mean
    /// - it is a mean of whichever hours the sensor happened to be up for, and daily wind has a
    /// diurnal cycle, so a day missing its afternoon is biased low rather than merely noisy.
    /// Nothing downstream can distinguish that from a genuinely calm day. The cost is small here:
    /// this record loses 21 days of 6,207.</para>
    /// </summary>
    /// <param name="days">Station days, unfiltered.</param>
    public static IReadOnlyList<DailyWindSpeed> Build(IEnumerable<DwdWindDay> days)
    {
        if (days is null)
            throw new ArgumentNullException(nameof(days));

        var series = new List<DailyWindSpeed>();

        foreach (var day in days)
        {
            if (!day.IsComplete)
                continue;

            // A daily mean of exactly zero is not a calm day, it is a stuck sensor: this station
            // has never recorded one, and the Weibull's support is open below anyway.
            if (!(day.MeanSpeed > 0.0))
                continue;

            series.Add(new DailyWindSpeed(day.Date, day.MeanSpeed, day.MeanCubedSpeed));
        }

        return series;
    }
}

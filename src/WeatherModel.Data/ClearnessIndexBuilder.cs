using WeatherModel.Climate;
using WeatherModel.Solar;

namespace WeatherModel.Data;

/// <summary>
/// Turns measured station days into a clearness-index series.
/// </summary>
public static class ClearnessIndexBuilder
{
    /// <summary>
    /// Builds the daily clearness series for a station.
    ///
    /// <para>The clear-sky ceiling is integrated over <b>exactly the intervals that carry a
    /// valid measurement</b>, rather than over an idealised calendar day. For fully complete
    /// days the two agree, but matching them keeps the ratio exact and leaves the door open to
    /// admitting partial days later without introducing a bias towards whichever part of the
    /// day went missing.</para>
    /// </summary>
    /// <param name="days">Station days. Filter to complete ones before calling unless you want partials.</param>
    /// <param name="station">Station location the ceiling must be computed where the sensor is.</param>
    /// <param name="turbidityProvider">Optional turbidity override; defaults to the site fit.</param>
    public static IReadOnlyList<DailyClearness> Build(
        IEnumerable<DwdSolarDay> days,
        DwdStation station,
        Func<DateTime, double>? turbidityProvider = null)
    {
        var calculator = new DailyClearSkyCalculator(
            station.LatitudeDegrees,
            station.LongitudeDegrees,
            station.AltitudeMeters,
            TimeZoneInfo.Utc,
            step: TimeSpan.FromMinutes(15),
            turbidityProvider: turbidityProvider);

        var series = new List<DailyClearness>();

        foreach (var day in days)
        {
            double observed = 0.0;
            double clearSky = 0.0;
            double extraterrestrial = 0.0;

            foreach (var interval in day.Intervals)
            {
                if (interval.GlobalWhPerM2 is not { } measured)
                    continue;

                observed += measured;
                clearSky += calculator.IntegrateGhiWhPerM2(interval.StartUtc, interval.EndUtc);
                extraterrestrial += calculator.IntegrateExtraterrestrialHorizontalWhPerM2(
                    interval.StartUtc, interval.EndUtc);
            }

            if (clearSky > 0.0)
                series.Add(new DailyClearness(day.Date, observed, clearSky, extraterrestrial));
        }

        return series;
    }
}

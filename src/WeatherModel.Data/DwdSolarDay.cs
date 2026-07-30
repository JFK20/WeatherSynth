namespace WeatherModel.Data;

/// <summary>
/// One true-solar-time day of DWD station data, aggregated from its hourly intervals.
/// </summary>
public sealed class DwdSolarDay
{
    /// <summary>Zenith angle below which an interval counts as carrying daylight.</summary>
    private const double DaylightZenithDegrees = 90.0;

    /// <summary>
    /// Zenith angle above which a reading of exactly zero is unremarkable. With the sun higher
    /// than this, even thick overcast delivers a clearly non-zero amount.
    /// </summary>
    private const double HighSunZenithDegrees = 80.0;

    internal DwdSolarDay(DateOnly date, IReadOnlyList<DwdSolarInterval> intervals)
    {
        Date = date;
        Intervals = intervals;

        foreach (var interval in intervals)
        {
            bool daylight = interval.ZenithDegrees < DaylightZenithDegrees;
            if (daylight)
                DaylightIntervalCount++;

            if (interval.GlobalWhPerM2 is not { } global)
                continue;

            if (daylight)
                ObservedDaylightIntervalCount++;

            if (global == 0.0 && interval.ZenithDegrees < HighSunZenithDegrees)
                HasImplausibleZeros = true;

            GlobalWhPerM2 += global;
            DiffuseWhPerM2 += interval.DiffuseWhPerM2 ?? 0.0;
            SunshineMinutes += interval.SunshineMinutes ?? 0.0;
        }
    }

    /// <summary>The true-solar-time date.</summary>
    public DateOnly Date { get; }

    /// <summary>All intervals belonging to this day, in chronological order.</summary>
    public IReadOnlyList<DwdSolarInterval> Intervals { get; }

    /// <summary>Measured daily global horizontal irradiation, Wh/m², summing valid intervals only.</summary>
    public double GlobalWhPerM2 { get; }

    /// <summary>Measured daily diffuse horizontal irradiation, Wh/m².</summary>
    public double DiffuseWhPerM2 { get; }

    /// <summary>Total sunshine duration over the day, in minutes.</summary>
    public double SunshineMinutes { get; }

    /// <summary>Number of intervals whose midpoint has the sun above the horizon.</summary>
    public int DaylightIntervalCount { get; }

    /// <summary>Of those, how many carry a valid global-radiation measurement.</summary>
    public int ObservedDaylightIntervalCount { get; }

    /// <summary>
    /// True when some hour reports exactly zero global radiation with the sun well up.
    ///
    /// <para>This is a sensor outage recorded as <i>valid zeros</i> rather than as the -999
    /// missing marker, so neither the missing-value handling nor the completeness check catches
    /// it and DWD's quality level is 1 for every row in this record, so it offers no help
    /// either. 2026-03-28 is the clear example: every hour reads 0.0, including midday at a
    /// zenith of 48.8°. A genuinely black overcast day still delivers a few percent of the
    /// clear-sky total, never zero.</para>
    ///
    /// <para>Left as a flag rather than folded into <see cref="IsComplete"/>, because the two
    /// describe different problems: one is missing data, this is wrong data.</para>
    /// </summary>
    public bool HasImplausibleZeros { get; }

    /// <summary>
    /// True when every daylight interval carries a measurement.
    ///
    /// Worth knowing about this station: outages are chunky rather than scattered no day in
    /// the 2009-2026 record sits between 95% and 99% complete. So this strict test discards
    /// only genuinely broken days, not marginal ones.
    /// </summary>
    public bool IsComplete => DaylightIntervalCount > 0
                              && ObservedDaylightIntervalCount == DaylightIntervalCount;

    /// <summary>
    /// Fraction of the day's measured energy arriving as diffuse rather than direct radiation.
    /// Around 0.15-0.25 on cloudless days, rising towards 1.0 under thick overcast which
    /// makes it a good second opinion when picking clear days for calibration.
    /// </summary>
    public double DiffuseFraction => GlobalWhPerM2 > 0.0 ? DiffuseWhPerM2 / GlobalWhPerM2 : double.NaN;

    /// <summary>
    /// Recorded sunshine as a fraction of what the intervals could have held.
    ///
    /// The denominator counts only intervals whose midpoint sun is higher than
    /// <paramref name="maxZenithDegrees"/>: near the horizon an interval can rarely accumulate
    /// its full 60 minutes, so including those intervals would penalise genuinely cloudless
    /// days in winter.
    /// </summary>
    public double SunshineFraction(double maxZenithDegrees = 85.0)
    {
        double possibleMinutes = 0.0;
        foreach (var interval in Intervals)
        {
            if (interval.ZenithDegrees < maxZenithDegrees && interval.GlobalWhPerM2 is not null)
                possibleMinutes += 60.0;
        }

        return possibleMinutes > 0.0 ? SunshineMinutes / possibleMinutes : double.NaN;
    }
}

/// <summary>Groups DWD hourly intervals into true-solar-time days.</summary>
public static class DwdSolarDayAggregator
{
    /// <summary>
    /// Aggregates a stream of intervals into days, keyed on true solar date.
    ///
    /// Days are returned in chronological order. Note that days missing from the source are
    /// simply absent rather than emitted empty the Bochum record is missing all of
    /// December 2023 and December 2024, and anything estimating day-to-day transition
    /// probabilities later must treat those as chain breaks rather than bridge them.
    /// </summary>
    public static IReadOnlyList<DwdSolarDay> ToDays(IEnumerable<DwdSolarInterval> intervals)
    {
        return intervals
            .GroupBy(i => i.WozDate)
            .OrderBy(g => g.Key)
            .Select(g => new DwdSolarDay(g.Key, g.OrderBy(i => i.StartUtc).ToList()))
            .ToList();
    }
}

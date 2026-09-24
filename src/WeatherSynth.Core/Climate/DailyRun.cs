namespace WeatherSynth.Climate;

/// <summary>
/// The calendar side of a generated run: the span check and the day-by-day walk every generator
/// shares.
/// </summary>
internal static class DailyRun
{
    /// <summary>Throws unless <paramref name="endInclusive"/> is on or after <paramref name="start"/>.</summary>
    public static void Validate(DateOnly start, DateOnly endInclusive)
    {
        if (endInclusive < start)
            throw new ArgumentException("End must not precede start.", nameof(endInclusive));
    }

    /// <summary>
    /// Every day from <paramref name="start"/> to <paramref name="endInclusive"/>, in order.
    /// Lazy, so a long run is never materialised.
    /// </summary>
    public static IEnumerable<DateOnly> Days(DateOnly start, DateOnly endInclusive)
    {
        for (var date = start; date <= endInclusive; date = date.AddDays(1))
            yield return date;
    }

    /// <summary>
    /// 1 January to 31 December of <paramref name="year"/>, inclusive - 366 days in a leap year.
    /// </summary>
    public static (DateOnly Start, DateOnly EndInclusive) Year(int year) =>
        (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
}

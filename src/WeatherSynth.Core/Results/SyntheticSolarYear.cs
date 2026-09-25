namespace WeatherSynth;

/// <summary>One calendar month of a generated year, aggregated.</summary>
/// <param name="Month">Calendar month, 1-12.</param>
/// <param name="Days">Days generated in it.</param>
/// <param name="MeanClearSkyIndex">Mean of the daily indices, unweighted.</param>
/// <param name="GhiKWhPerM2">Synthetic irradiation for the month.</param>
/// <param name="ClearSkyKWhPerM2">The month's clear-sky ceiling, for reference.</param>
public readonly record struct SyntheticSolarMonth(
    int Month,
    int Days,
    double MeanClearSkyIndex,
    double GhiKWhPerM2,
    double ClearSkyKWhPerM2
);

/// <summary>
/// One generated year at daily resolution, with its monthly and annual totals.
///
/// <para>This is the model's product: a plausible year that never happened. It is one
/// realisation, not a forecast and not a climatology - another seed gives an equally
/// plausible year, and the seed it was drawn with is carried along so any year can be
/// reproduced exactly from <see cref="Year"/> and <see cref="Seed"/> alone.</para>
///
/// <para>Materialised rather than streamed, because a year is small (366 days) and callers
/// asking for one generally want the totals too. The provider's <c>Generate(start, end, seed)</c>
/// is still there for long runs.</para>
/// </summary>
public sealed class SyntheticSolarYear
{
    private readonly HourGrid? _grid;
    private readonly double[]? _clearSkyByHour;

    /// <summary>
    /// Wraps an already-generated run of days and their hours.
    /// </summary>
    /// <param name="year">The calendar year the days belong to.</param>
    /// <param name="seed">Seed the run was drawn with, so it can be reproduced.</param>
    /// <param name="days">The generated days, in date order.</param>
    /// <param name="grid">Where each day's hours sit in time.</param>
    /// <param name="clearSkyByHour">
    /// The clear-sky ceiling of every hour, Wh/m², <see cref="HourGrid.HoursPerDay"/> per day in
    /// day order. The hour's irradiation is its day's index times this.
    /// </param>
    internal SyntheticSolarYear(
        int year,
        int seed,
        IReadOnlyList<SyntheticSolarDay> days,
        HourGrid grid,
        double[] clearSkyByHour
    )
        : this(year, seed, days)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(clearSkyByHour);

        if (grid.DayCount != days.Count || clearSkyByHour.Length != grid.Count)
            throw new ArgumentException(
                $"{days.Count} days need {days.Count * HourGrid.HoursPerDay} hours; got "
                    + $"{clearSkyByHour.Length} over {grid.DayCount} days.",
                nameof(clearSkyByHour)
            );

        _grid = grid;
        _clearSkyByHour = clearSkyByHour;
    }

    /// <summary>
    /// Wraps an already-generated run of days, without hours. Internal callers only: every
    /// public path builds a year with hours.
    /// </summary>
    /// <param name="year">The calendar year the days belong to.</param>
    /// <param name="seed">Seed the run was drawn with, so it can be reproduced.</param>
    /// <param name="days">The generated days, in date order.</param>
    internal SyntheticSolarYear(int year, int seed, IReadOnlyList<SyntheticSolarDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        if (days.Count == 0)
            throw new ArgumentException("A year needs at least one day.", nameof(days));

        Year = year;
        Seed = seed;
        Days = days;

        var months = new List<SyntheticSolarMonth>(12);
        var indexSum = new double[13];
        var ghiSum = new double[13];
        var ceilingSum = new double[13];
        var counts = new int[13];

        foreach (var day in days)
        {
            if (day.Date.Year != year)
                throw new ArgumentException(
                    $"{day.Date:yyyy-MM-dd} does not belong to {year}.",
                    nameof(days)
                );

            int month = day.Date.Month;
            counts[month]++;
            indexSum[month] += day.ClearSkyIndex;
            ghiSum[month] += day.GhiWhPerM2;
            ceilingSum[month] += day.ClearSkyWhPerM2;

            GhiKWhPerM2 += day.GhiWhPerM2 / 1000.0;
            ClearSkyKWhPerM2 += day.ClearSkyWhPerM2 / 1000.0;
            MeanClearSkyIndex += day.ClearSkyIndex;
        }

        MeanClearSkyIndex /= days.Count;

        for (int month = 1; month <= 12; month++)
        {
            if (counts[month] == 0)
                continue;

            months.Add(
                new SyntheticSolarMonth(
                    month,
                    counts[month],
                    indexSum[month] / counts[month],
                    ghiSum[month] / 1000.0,
                    ceilingSum[month] / 1000.0
                )
            );
        }

        Months = months;
    }

    /// <summary>The calendar year generated.</summary>
    public int Year { get; }

    /// <summary>
    /// Seed the run was drawn with. Same year, same seed, same site gives the same days -
    /// which is what makes a generated year quotable rather than merely plausible.
    /// </summary>
    public int Seed { get; }

    /// <summary>Every generated day, in date order. 366 entries in a leap year.</summary>
    public IReadOnlyList<SyntheticSolarDay> Days { get; }

    /// <summary>Monthly aggregates, ascending. Twelve entries for a whole year.</summary>
    public IReadOnlyList<SyntheticSolarMonth> Months { get; }

    /// <summary>Synthetic annual irradiation, the number a yield estimate starts from.</summary>
    public double GhiKWhPerM2 { get; }

    /// <summary>The year's clear-sky ceiling, the deterministic upper bound on the above.</summary>
    public double ClearSkyKWhPerM2 { get; }

    /// <summary>
    /// Mean of the daily clear-sky indices, unweighted - the figure that compares directly
    /// against the record's fitted monthly means.
    ///
    /// <para>Not the same as <see cref="GhiKWhPerM2"/> over <see cref="ClearSkyKWhPerM2"/>,
    /// which weights each day by how much energy was available that day and therefore runs
    /// higher: summer days carry more weight and summer is clearer.</para>
    /// </summary>
    public double MeanClearSkyIndex { get; }

    /// <summary>Energy-weighted index: the fraction of the year's available energy delivered.</summary>
    public double ClearSkyFraction =>
        ClearSkyKWhPerM2 > 0.0 ? GhiKWhPerM2 / ClearSkyKWhPerM2 : double.NaN;

    /// <summary>
    /// Every generated hour, in order: twenty-four per day, 8,784 in a leap year. Each day's hours
    /// add up to that day's <see cref="SyntheticSolarDay.GhiWhPerM2"/>.
    ///
    /// <para>Computed with the year and stored compactly; the records themselves are built as they
    /// are read, so holding many years in a cache costs one number per hour, not one record.</para>
    ///
    /// <para><b>Starts are unique only in a zone without daylight saving.</b> Every day's hours
    /// begin at its local midnight, at the UTC offset in force at noon. For a site in a DST zone,
    /// the spring switch day therefore begins an hour early and its first hour has the same
    /// <see cref="SyntheticSolarHour.Start"/> as the previous day's last. The autumn switch day
    /// begins an hour late, so one hour of the timeline has no entry. Do not key a dictionary
    /// on <c>Start</c> there. The default site is in UTC, where every hour is contiguous and
    /// unique.</para>
    /// </summary>
    public IReadOnlyList<SyntheticSolarHour> Hours => new HourView<SyntheticSolarHour>(Grid.Count, HourAt);

    /// <summary>
    /// The hours starting in <c>[startInclusive, endExclusive)</c>: 06:00 to 12:00 is six hours,
    /// 06:00 to 11:00 inclusive of their starts, which together cover exactly that interval.
    ///
    /// <para>Clamped to this year, so a range reaching into the next one returns this year's part
    /// of it, and a range outside the year returns nothing.</para>
    ///
    /// <para>In a DST zone, a range across the spring switch returns two hours with the same
    /// start. See <see cref="Hours"/>.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The end is before the start.</exception>
    public IReadOnlyList<SyntheticSolarHour> HoursBetween(
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive
    ) => Grid.IndicesBetween(startInclusive, endExclusive).ConvertAll(HourAt);

    /// <summary>
    /// <see cref="HoursBetween(DateTimeOffset, DateTimeOffset)"/> for <see cref="DateTime"/>s.
    ///
    /// <para>An unspecified <see cref="DateTime.Kind"/> is read as wall-clock time in the site's
    /// time zone - UTC for the default site - rather than through the machine's local zone, which
    /// is what the implicit conversion to <see cref="DateTimeOffset"/> would silently do.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The end is before the start.</exception>
    public IReadOnlyList<SyntheticSolarHour> HoursBetween(
        DateTime startInclusive,
        DateTime endExclusive
    ) => HoursBetween(Grid.ToInstant(startInclusive), Grid.ToInstant(endExclusive));

    internal HourGrid Grid =>
        _grid ?? throw new InvalidOperationException("This year was generated without hours.");

    internal SyntheticSolarHour HourAt(int index)
    {
        var day = Days[index / HourGrid.HoursPerDay];
        double clearSky = _clearSkyByHour![index];

        return new SyntheticSolarHour(
            Grid.StartOf(index),
            day.ClearSkyIndex,
            clearSky,
            day.ClearSkyIndex * clearSky
        );
    }
}

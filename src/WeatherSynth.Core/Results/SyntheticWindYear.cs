namespace WeatherSynth;

/// <summary>One calendar month of a generated year, aggregated.</summary>
/// <param name="Month">Calendar month, 1-12.</param>
/// <param name="Days">Days generated in it.</param>
/// <param name="MeanSpeed">Mean of the daily mean speeds, m/s.</param>
/// <param name="MaxSpeed">Windiest day in the month, m/s.</param>
/// <param name="MeanCubedSpeed">Mean of the daily mean-cubed speeds, m³/s³.</param>
public readonly record struct SyntheticWindMonth(
    int Month,
    int Days,
    double MeanSpeed,
    double MaxSpeed,
    double MeanCubedSpeed
);

/// <summary>
/// One generated year at daily resolution, with its monthly and annual aggregates.
///
/// <para>The model's product: a plausible year that never happened. One realisation, not a
/// forecast and not a climatology - another seed gives an equally plausible year, and the seed
/// is carried along so any year can be reproduced exactly from <see cref="Year"/> and
/// <see cref="Seed"/>.</para>
///
/// <para><b>Aggregated by averaging, not by summing</b>, which is the one place this differs
/// structurally from <see cref="SyntheticSolarYear"/>. A year of irradiance has a total -
/// annual kWh/m² is the product, and the number a yield estimate starts from. A year of wind
/// speeds has no total; adding daily speeds together produces a number with no physical
/// meaning. What a wind year has instead is a mean, a maximum, and - because power goes as the
/// cube - a mean of cubes.</para>
/// </summary>
public sealed class SyntheticWindYear
{
    private readonly HourGrid? _grid;
    private readonly double[]? _speedByHour;

    /// <summary>
    /// Wraps an already-generated run of days and their hours.
    /// </summary>
    /// <param name="year">The calendar year the days belong to.</param>
    /// <param name="seed">Seed the run was drawn with, so it can be reproduced.</param>
    /// <param name="days">The generated days, in date order.</param>
    /// <param name="grid">Where each day's hours sit in time.</param>
    /// <param name="speedByHour">
    /// Every hour's mean speed at the target site, m/s, <see cref="HourGrid.HoursPerDay"/> per day
    /// in day order. The speed at the fitting height is recovered through the day's own ratio,
    /// so the transfer keeps a single definition.
    /// </param>
    internal SyntheticWindYear(
        int year,
        int seed,
        IReadOnlyList<SyntheticWindDay> days,
        HourGrid grid,
        double[] speedByHour
    )
        : this(year, seed, days)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(speedByHour);

        if (grid.DayCount != days.Count || speedByHour.Length != grid.Count)
            throw new ArgumentException(
                $"{days.Count} days need {days.Count * HourGrid.HoursPerDay} hours; got "
                    + $"{speedByHour.Length} over {grid.DayCount} days.",
                nameof(speedByHour)
            );

        _grid = grid;
        _speedByHour = speedByHour;
    }

    /// <summary>
    /// Wraps an already-generated run of days, without hours. Internal callers only: every
    /// public path builds a year with hours.
    /// </summary>
    /// <param name="year">The calendar year the days belong to.</param>
    /// <param name="seed">Seed the run was drawn with, so it can be reproduced.</param>
    /// <param name="days">The generated days, in date order.</param>
    internal SyntheticWindYear(int year, int seed, IReadOnlyList<SyntheticWindDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        if (days.Count == 0)
            throw new ArgumentException("A year needs at least one day.", nameof(days));

        Year = year;
        Seed = seed;
        Days = days;

        var months = new List<SyntheticWindMonth>(12);
        var speedSum = new double[13];
        var cubedSum = new double[13];
        var maxima = new double[13];
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
            speedSum[month] += day.MeanSpeed;
            cubedSum[month] += day.MeanCubedSpeed;
            if (day.MeanSpeed > maxima[month])
                maxima[month] = day.MeanSpeed;

            MeanSpeed += day.MeanSpeed;
            MeanCubedSpeed += day.MeanCubedSpeed;
            if (day.MeanSpeed > MaxSpeed)
                MaxSpeed = day.MeanSpeed;
        }

        MeanSpeed /= days.Count;
        MeanCubedSpeed /= days.Count;

        for (int month = 1; month <= 12; month++)
        {
            if (counts[month] == 0)
                continue;

            months.Add(
                new SyntheticWindMonth(
                    month,
                    counts[month],
                    speedSum[month] / counts[month],
                    maxima[month],
                    cubedSum[month] / counts[month]
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
    public IReadOnlyList<SyntheticWindDay> Days { get; }

    /// <summary>Monthly aggregates, ascending. Twelve entries for a whole year.</summary>
    public IReadOnlyList<SyntheticWindMonth> Months { get; }

    /// <summary>
    /// Mean of the year's daily mean speeds, m/s - the figure that compares directly against
    /// the record's own annual mean, and the first thing to check on a suspicious year.
    /// </summary>
    public double MeanSpeed { get; }

    /// <summary>Windiest day of the year, m/s.</summary>
    public double MaxSpeed { get; }

    /// <summary>
    /// Mean of the daily mean-cubed speeds, m³/s³.
    ///
    /// <para><b>This, not <see cref="MeanSpeed"/>, is what an energy estimate scales with.</b>
    /// Cubing the annual mean speed instead understates the year twice over: once for the
    /// within-day variation and again for the day-to-day variation, since E[v³] exceeds
    /// (E[v])³ at every level of averaging.</para>
    /// </summary>
    public double MeanCubedSpeed { get; }

    /// <summary>
    /// How much the year's energy exceeds what its mean speed alone would suggest,
    /// <c>MeanCubedSpeed / MeanSpeed³</c>.
    ///
    /// <para>Larger than any single day's factor, because this one carries the day-to-day
    /// spread as well as the within-day spread.</para>
    /// </summary>
    public double EnergyPatternFactor =>
        MeanSpeed > 0.0 ? MeanCubedSpeed / (MeanSpeed * MeanSpeed * MeanSpeed) : double.NaN;

    /// <summary>
    /// Every generated hour, in order: twenty-four per day, 8,784 in a leap year. Each day's hours
    /// average exactly to that day's <see cref="SyntheticWindDay.MeanSpeed"/>.
    ///
    /// <para>Computed with the year and stored compactly; the records themselves are built as they
    /// are read, so holding many years in a cache costs one number per hour, not one record.</para>
    ///
    /// <para><b>Starts are unique only in a zone without daylight saving.</b> A wind year
    /// generated on its own is bounded in UTC, where every hour is contiguous and unique. Inside
    /// a <see cref="CoupledWeatherYear"/> it takes the solar site's zone. If that zone has DST,
    /// each day begins at local midnight at the offset in force at noon. The spring switch day
    /// then begins an hour early, and its first hour has the same
    /// <see cref="SyntheticWindHour.Start"/> as the previous day's last, with a different speed.
    /// The autumn switch day begins an hour late, so one hour of the timeline has no entry.
    /// Do not key a dictionary on <c>Start</c> there.</para>
    /// </summary>
    public IReadOnlyList<SyntheticWindHour> Hours => new HourView<SyntheticWindHour>(Grid.Count, HourAt);

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
    public IReadOnlyList<SyntheticWindHour> HoursBetween(
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive
    ) => Grid.IndicesBetween(startInclusive, endExclusive).ConvertAll(HourAt);

    /// <summary>
    /// <see cref="HoursBetween(DateTimeOffset, DateTimeOffset)"/> for <see cref="DateTime"/>s.
    ///
    /// <para>An unspecified <see cref="DateTime.Kind"/> is read as wall-clock time in the year's
    /// time zone - UTC unless the year was generated alongside a solar site in another zone -
    /// rather than through the machine's local zone, which is what the implicit conversion to
    /// <see cref="DateTimeOffset"/> would silently do.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The end is before the start.</exception>
    public IReadOnlyList<SyntheticWindHour> HoursBetween(
        DateTime startInclusive,
        DateTime endExclusive
    ) => HoursBetween(Grid.ToInstant(startInclusive), Grid.ToInstant(endExclusive));

    internal HourGrid Grid =>
        _grid ?? throw new InvalidOperationException("This year was generated without hours.");

    internal SyntheticWindHour HourAt(int index)
    {
        var day = Days[index / HourGrid.HoursPerDay];
        double speed = _speedByHour![index];

        // The transfer is one constant per site, so the day's own ratio undoes it exactly.
        double atReference =
            day.MeanSpeed > 0.0 ? speed * (day.MeanSpeedAtReference / day.MeanSpeed) : day.MeanSpeedAtReference;

        return new SyntheticWindHour(Grid.StartOf(index), atReference, speed);
    }
}

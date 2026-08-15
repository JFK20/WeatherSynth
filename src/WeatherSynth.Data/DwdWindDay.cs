namespace WeatherSynth.Data;

/// <summary>
/// One UTC calendar day of DWD wind data, aggregated from its hourly records.
/// </summary>
public sealed class DwdWindDay
{
    /// <summary>Hours in a complete day. DWD hourly wind files are strictly hourly.</summary>
    private const int HoursPerDay = 24;

    internal DwdWindDay(DateOnly date, IReadOnlyList<DwdWindHour> hours)
    {
        Date = date;
        Hours = hours;

        double sum = 0.0;
        double sumOfCubes = 0.0;

        foreach (var hour in hours)
        {
            if (hour.SpeedMetersPerSecond is not { } speed)
                continue;

            ValidHourCount++;
            sum += speed;
            sumOfCubes += speed * speed * speed;

            if (speed > MaxSpeed)
                MaxSpeed = speed;
            if (speed == 0.0)
                ZeroHourCount++;
        }

        if (ValidHourCount > 0)
        {
            MeanSpeed = sum / ValidHourCount;
            MeanCubedSpeed = sumOfCubes / ValidHourCount;
        }
        else
        {
            MeanSpeed = double.NaN;
            MeanCubedSpeed = double.NaN;
        }
    }

    /// <summary>The UTC calendar date.</summary>
    public DateOnly Date { get; }

    /// <summary>All hours belonging to this day, in chronological order.</summary>
    public IReadOnlyList<DwdWindHour> Hours { get; }

    /// <summary>How many of the day's hours carry a valid speed measurement.</summary>
    public int ValidHourCount { get; }

    /// <summary>
    /// Mean wind speed over the day's valid hours, m/s. NaN when the day holds none.
    ///
    /// <para><b>This is the quantity the model is fitted on</b>, and the resolution matters: the
    /// Weibull shape parameter measured on daily means is not the one measured on hourly values
    /// (2.71 against 2.14 at this very station). A daily-fitted k quoted in an hourly context is
    /// a silent, large error.</para>
    /// </summary>
    public double MeanSpeed { get; }

    /// <summary>Highest hourly mean speed within the day, m/s. Zero when the day holds none.</summary>
    public double MaxSpeed { get; }

    /// <summary>
    /// Mean of the <i>cubed</i> hourly speeds, m³/s³. NaN when the day holds no valid hour.
    ///
    /// <para>Carried from the start because wind power goes as v³ and E[v³] &gt; (E[v])³ always,
    /// so it cannot be recovered from <see cref="MeanSpeed"/> afterwards. See
    /// <see cref="EnergyPatternFactor"/> for the size of the gap.</para>
    /// </summary>
    public double MeanCubedSpeed { get; }

    /// <summary>
    /// How many hours read exactly zero.
    ///
    /// <para>Not a quality flag - a calm hour is physically ordinary, unlike the solar record's
    /// black midday. It is here as the diagnostic for the 2021-07-20 instrument change from cup
    /// anemometer to 2D ultrasonic: exact zeros fall from 0.050% of hours to 0.000% across it,
    /// alongside a 2.3% drop in the mean. That is a real inhomogeneity inside the fitting span,
    /// and it is exactly the kind of thing mistaken for a climate trend.</para>
    /// </summary>
    public int ZeroHourCount { get; }

    /// <summary>
    /// True when every hour of the day carries a measurement.
    ///
    /// <para>Strict on purpose, and affordable here: the Essen record is effectively gapless
    /// (11 missing values in 148,807 hours), so this discards 21 days out of 6,207 rather than a
    /// meaningful fraction of the record.</para>
    /// </summary>
    public bool IsComplete => ValidHourCount == HoursPerDay;

    /// <summary>
    /// The day's energy pattern factor, <c>mean(v³) / mean(v)³</c>.
    ///
    /// <para><b>Read this before deriving any energy figure from a daily mean speed.</b> Power is
    /// proportional to the cube of wind speed, so the energy in a day is set by mean(v³), not by
    /// the cube of the mean - and Jensen's inequality puts the first above the second for any day
    /// whose wind is not perfectly constant. At this station the factor has median 1.25, mean 1.31
    /// and a p90 of 1.58, so cubing a synthetic daily mean understates the day's energy by
    /// roughly a quarter, systematically and in one direction.</para>
    ///
    /// <para>Exposed rather than folded into a power calculation so that the correction is
    /// visible and quantified instead of hidden. <b>A daily mean speed on its own is not
    /// sufficient for an energy estimate.</b></para>
    /// </summary>
    public double EnergyPatternFactor =>
        MeanSpeed > 0.0 ? MeanCubedSpeed / (MeanSpeed * MeanSpeed * MeanSpeed) : double.NaN;
}

/// <summary>Groups DWD hourly wind records into UTC calendar days.</summary>
public static class DwdWindDayAggregator
{
    /// <summary>
    /// Aggregates a stream of hours into days, keyed on UTC date.
    ///
    /// <para>Days are returned in chronological order. Days absent from the source are absent
    /// here too rather than emitted empty, and anything estimating day-to-day persistence
    /// downstream has to treat those as chain breaks rather than bridge them - which is what
    /// <see cref="WeatherSynth.Climate.LatentAr1Chain"/>'s gap handling already does.</para>
    /// </summary>
    public static IReadOnlyList<DwdWindDay> ToDays(IEnumerable<DwdWindHour> hours)
    {
        return hours
            .GroupBy(h => h.UtcDate)
            .OrderBy(g => g.Key)
            .Select(g => new DwdWindDay(g.Key, g.OrderBy(h => h.TimestampUtc).ToList()))
            .ToList();
    }
}

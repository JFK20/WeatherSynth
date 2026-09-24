using System.Collections;

namespace WeatherSynth;

/// <summary>
/// Where each hour of a generated year sits in time: twenty-four per generated day, starting at
/// the instant that day's ceiling starts.
///
/// <para>Shared by the solar, wind and coupled years so the three agree on every timestamp. It
/// stores one start per day rather than one per hour, which is the whole of the reason an hourly
/// year stays small: the values live in one <c>double[]</c> per resource and the hour records are
/// built only when read.</para>
///
/// <para><b>Daylight saving is not smoothed over.</b> A day's start is its local midnight at the
/// offset in force at noon, exactly as <c>DailyClearSkyCalculator</c> bounds it, so in a zone with
/// DST the two switch days overlap or leave a gap of one hour. The default sites are in UTC, where
/// every hour is contiguous.</para>
/// </summary>
internal sealed class HourGrid
{
    /// <summary>Hours generated per day, whatever the time zone does to the wall clock.</summary>
    public const int HoursPerDay = 24;

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    private readonly DateTimeOffset[] _dayStarts;

    /// <param name="dayStarts">Start of each generated day, in date order.</param>
    /// <param name="timeZone">The zone the days are bounded in; also how hour starts are labelled.</param>
    public HourGrid(IReadOnlyList<DateTimeOffset> dayStarts, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(dayStarts);

        _dayStarts = dayStarts.ToArray();
        TimeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
    }

    /// <summary>The zone the days are bounded in.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>Days covered.</summary>
    public int DayCount => _dayStarts.Length;

    /// <summary>Hours covered, <see cref="HoursPerDay"/> per day.</summary>
    public int Count => _dayStarts.Length * HoursPerDay;

    /// <summary>When hour <paramref name="index"/> of the year starts, at the zone's offset.</summary>
    public DateTimeOffset StartOf(int index)
    {
        var start = _dayStarts[index / HoursPerDay] + (index % HoursPerDay) * OneHour;

        return TimeZone == TimeZoneInfo.Utc ? start : TimeZoneInfo.ConvertTime(start, TimeZone);
    }

    /// <summary>
    /// Indices of the hours whose start falls in <c>[startInclusive, endExclusive)</c>, in order.
    /// Clamped to the year: a range outside it yields nothing.
    /// </summary>
    public List<int> IndicesBetween(DateTimeOffset startInclusive, DateTimeOffset endExclusive)
    {
        if (endExclusive < startInclusive)
            throw new ArgumentException(
                "The end of the range must not be before its start.",
                nameof(endExclusive)
            );

        var indices = new List<int>();

        // First day that has not ended before the range starts. Day starts are ascending, so a
        // binary search on them is safe even where DST makes two days overlap by an hour.
        int low = 0,
            high = _dayStarts.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (_dayStarts[middle] + OneDay <= startInclusive)
                low = middle + 1;
            else
                high = middle;
        }

        for (int day = low; day < _dayStarts.Length && _dayStarts[day] < endExclusive; day++)
        {
            for (int hour = 0; hour < HoursPerDay; hour++)
            {
                var start = _dayStarts[day] + hour * OneHour;
                if (start >= startInclusive && start < endExclusive)
                    indices.Add(day * HoursPerDay + hour);
            }
        }

        return indices;
    }

    /// <summary>
    /// A <see cref="DateTime"/> as an instant, without the implicit conversion's trap.
    ///
    /// <para><c>DateTime</c> converts to <c>DateTimeOffset</c> implicitly using the <i>machine's</i>
    /// local zone, which on a server is an accident of deployment. Here an unspecified time is read
    /// as wall-clock time in the year's own zone - UTC for the default sites - a UTC time as UTC,
    /// and only an explicitly local one through the machine's zone.</para>
    /// </summary>
    public DateTimeOffset ToInstant(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero),
            _ => new DateTimeOffset(value, TimeZone.GetUtcOffset(value)),
        };
}

/// <summary>
/// A read-only list whose items are built on access, so a year's 8,784 hour records never all
/// exist at once unless a caller copies them.
/// </summary>
internal sealed class HourView<T> : IReadOnlyList<T>
{
    private readonly int _count;
    private readonly Func<int, T> _at;

    public HourView(int count, Func<int, T> at)
    {
        _count = count;
        _at = at;
    }

    public T this[int index] =>
        (uint)index < (uint)_count ? _at(index) : throw new ArgumentOutOfRangeException(nameof(index));

    public int Count => _count;

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
            yield return _at(i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

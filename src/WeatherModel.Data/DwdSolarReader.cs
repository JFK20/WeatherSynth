using System.Globalization;

namespace WeatherModel.Data;

/// <summary>
/// One hourly record from a DWD solar station file.
///
/// DWD reports these intervals in <b>true solar time</b> (wahre Ortszeit, WOZ), not in UTC or
/// local clock time: the interval boundaries are whole WOZ hours, and the UTC timestamp that
/// accompanies them therefore lands on odd minutes that drift through the year with the
/// equation of time. All timestamps here are UTC instants; <see cref="WozDate"/> carries the
/// solar-time day the interval belongs to.
/// </summary>
public sealed record DwdSolarInterval
{
    /// <summary>Start of the reporting interval, UTC.</summary>
    public required DateTimeOffset StartUtc { get; init; }

    /// <summary>End of the reporting interval, UTC. This is the raw MESS_DATUM value.</summary>
    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>
    /// Midpoint of the reporting interval, UTC. This is the instant DWD's ZENIT refers to,
    /// and the correct instant at which to evaluate solar position for this interval.
    /// </summary>
    public DateTimeOffset MidpointUtc => StartUtc.AddTicks((EndUtc - StartUtc).Ticks / 2);

    /// <summary>
    /// The true-solar-time date this interval belongs to. Daily aggregation keys on this:
    /// because intervals are WOZ-aligned, a WOZ day contains exactly 24 of them.
    /// </summary>
    public required DateOnly WozDate { get; init; }

    /// <summary>
    /// End of the interval in true solar time (the raw MESS_DATUM_WOZ value). Always a whole hour.
    /// </summary>
    public required DateTime WozEnd { get; init; }

    /// <summary>
    /// Midpoint of the interval in true solar time — always a half hour past.
    ///
    /// Because this is <i>true</i> solar time, the sun's hour angle here is known exactly
    /// without any equation-of-time calculation: it is <c>(hours − 12) × 15°</c>. That makes
    /// this column an independent reference for validating solar-position code.
    /// </summary>
    public DateTime WozMidpoint => WozEnd.AddMinutes(-30);

    /// <summary>
    /// The sun's hour angle at the interval midpoint, degrees, negative before solar noon.
    /// Exact by the definition of true solar time.
    /// </summary>
    public double HourAngleDegrees => (WozMidpoint.TimeOfDay.TotalHours - 12.0) * 15.0;

    /// <summary>Global horizontal irradiation for the interval, Wh/m². Null when missing.</summary>
    public required double? GlobalWhPerM2 { get; init; }

    /// <summary>Diffuse horizontal irradiation for the interval, Wh/m². Null when missing.</summary>
    public required double? DiffuseWhPerM2 { get; init; }

    /// <summary>Sunshine duration within the interval, in minutes (0-60). Null when missing.</summary>
    public required double? SunshineMinutes { get; init; }

    /// <summary>
    /// Solar zenith angle at the interval midpoint, degrees, as reported by DWD. This is the
    /// true (geometric) zenith — it carries no refraction correction.
    /// </summary>
    public required double ZenithDegrees { get; init; }

    /// <summary>True when the sun is above the horizon at the interval midpoint.</summary>
    public bool IsDaylight => ZenithDegrees < 90.0;
}

/// <summary>
/// Reads DWD hourly solar station files (<c>stundenwerte_ST_*</c>), semicolon-delimited.
/// </summary>
public static class DwdSolarReader
{
    /// <summary>
    /// DWD's missing-value sentinel. Reading it as a number is the single most damaging
    /// mistake available with this format: a missing hour would contribute roughly
    /// −2,775 Wh/m² to a daily total, quietly producing negative irradiance.
    /// </summary>
    private const double MissingSentinel = -999.0;

    /// <summary>
    /// DWD publishes hourly radiation sums in J/cm². Converting to Wh/m²:
    /// 1 J/cm² = 10⁴ J/m², and 1 Wh = 3600 J, so the factor is 10000/3600.
    /// </summary>
    private const double JoulePerCm2ToWhPerM2 = 10000.0 / 3600.0;

    /// <summary>The reporting interval length. DWD hourly solar files are strictly hourly.</summary>
    private static readonly TimeSpan IntervalLength = TimeSpan.FromHours(1);

    /// <summary>
    /// Streams the intervals in a DWD solar file, skipping the header.
    /// </summary>
    /// <param name="csvPath">Path to the decompressed <c>produkt_st_stunde_*.txt</c> / CSV file.</param>
    public static IEnumerable<DwdSolarInterval> Read(string csvPath)
    {
        using var reader = new StreamReader(csvPath);

        // Header.
        if (reader.ReadLine() is null)
            yield break;

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            yield return ParseLine(line);
        }
    }

    /// <summary>
    /// Parses a single data row. Column order is fixed by the DWD format:
    /// STATIONS_ID; MESS_DATUM; QN_592; ATMO_LBERG; FD_LBERG; FG_LBERG; SD_LBERG; ZENIT;
    /// MESS_DATUM_WOZ; eor
    /// </summary>
    internal static DwdSolarInterval ParseLine(string line)
    {
        var columns = line.Split(';');
        if (columns.Length < 9)
            throw new FormatException($"Expected at least 9 columns, found {columns.Length}: {line}");

        // MESS_DATUM is the END of the interval, in UTC.
        var endUtc = ParseTimestampUtc(columns[1]);
        var wozEnd = ParseTimestamp(columns[8]);

        // The interval ending at WOZ midnight belongs to the previous solar day. Those hours
        // are always dark, so this never moves energy between days — but keeping it correct
        // means a WOZ day holds exactly 24 intervals, which the completeness check relies on.
        var wozDate = DateOnly.FromDateTime(wozEnd.AddTicks(-1));

        return new DwdSolarInterval
        {
            StartUtc = endUtc - IntervalLength,
            EndUtc = endUtc,
            WozDate = wozDate,
            WozEnd = wozEnd,
            DiffuseWhPerM2 = ParseRadiation(columns[4]),
            GlobalWhPerM2 = ParseRadiation(columns[5]),
            SunshineMinutes = ParseOptional(columns[6]),
            ZenithDegrees = double.Parse(columns[7], CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Radiation column: J/cm², or the missing sentinel, converted to Wh/m².</summary>
    private static double? ParseRadiation(string value)
    {
        double? raw = ParseOptional(value);
        return raw * JoulePerCm2ToWhPerM2;
    }

    /// <summary>Parses a numeric column, mapping DWD's −999 sentinel to null.</summary>
    private static double? ParseOptional(string value)
    {
        double parsed = double.Parse(value.Trim(), CultureInfo.InvariantCulture);
        return parsed == MissingSentinel ? null : parsed;
    }

    private static DateTime ParseTimestamp(string value) =>
        DateTime.ParseExact(value.Trim(), "yyyyMMddHH:mm", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestampUtc(string value) =>
        new(DateTime.SpecifyKind(ParseTimestamp(value), DateTimeKind.Utc));
}

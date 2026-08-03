using System.Globalization;

namespace WeatherSynth.Data;

/// <summary>
/// One hourly record from a DWD wind station file.
///
/// <para>Unlike the solar product there is no true-solar-time column here: the timestamps are
/// plain UTC hours and a day is a UTC calendar day. The two records therefore disagree on where
/// a day begins by up to ~30 minutes, which is irrelevant within either model and matters only
/// when they are joined.</para>
/// </summary>
public sealed record DwdWindHour
{
    /// <summary>
    /// The hour this observation is labelled with, UTC. This is the raw MESS_DATUM value.
    ///
    /// <para><b>Taken at face value, not as an interval end.</b> The daily aggregation groups the
    /// 24 hours 00-23 bearing the same date, and every fitted parameter and validation target in
    /// this project was measured under that convention. Reinterpreting MESS_DATUM as the end of
    /// the preceding hour would shift every day boundary by one hour and silently invalidate all
    /// of them; if that reading is ever adopted, re-measure the acceptance table first.</para>
    /// </summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The UTC calendar date this hour belongs to. Daily aggregation keys on this.</summary>
    public required DateOnly UtcDate { get; init; }

    /// <summary>Mean wind speed over the hour, m/s. Null when missing.</summary>
    public required double? SpeedMetersPerSecond { get; init; }

    /// <summary>
    /// Mean wind direction over the hour, degrees clockwise from north. Null when missing.
    ///
    /// <para><b>Quantised to a 36-point rose</b> - 10° steps - and to 32 points before 1975. Any
    /// circular statistic fitted to this has to treat it as binned rather than continuous.</para>
    /// </summary>
    public required double? DirectionDegrees { get; init; }

    /// <summary>
    /// DWD's quality level for the row (QN_3).
    ///
    /// <para>Worth carrying, unlike the solar record's, because it actually varies: the Essen
    /// record holds 1, 3 and 10. Over the fitting span it is nearly a no-op - 744 rows at 3 and
    /// the rest at 10 - so filter on it for correctness rather than for an expected gain.</para>
    /// </summary>
    public required int QualityLevel { get; init; }

    /// <summary>True when the hour carries a speed measurement.</summary>
    public bool HasSpeed => SpeedMetersPerSecond is not null;
}

/// <summary>
/// Reads DWD hourly wind station files (<c>stundenwerte_FF_*</c>), semicolon-delimited.
/// </summary>
public static class DwdWindReader
{
    /// <summary>
    /// DWD's missing-value sentinel, shared with the solar product and just as damaging read as
    /// a number: a single missing hour would drag a daily mean speed to roughly −40 m/s.
    /// </summary>
    private const double MissingSentinel = -999.0;

    /// <summary>
    /// Streams the hours in a DWD wind file, skipping the header.
    /// </summary>
    /// <param name="csvPath">Path to the decompressed <c>produkt_ff_stunde_*.txt</c> / CSV file.</param>
    public static IEnumerable<DwdWindHour> Read(string csvPath)
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
    /// STATIONS_ID; MESS_DATUM; QN_3; F; D; eor
    /// </summary>
    internal static DwdWindHour ParseLine(string line)
    {
        var columns = line.Split(';');
        if (columns.Length < 5)
            throw new FormatException(
                $"Expected at least 5 columns, found {columns.Length}: {line}"
            );

        var timestamp = ParseTimestampUtc(columns[1]);

        return new DwdWindHour
        {
            TimestampUtc = timestamp,
            UtcDate = DateOnly.FromDateTime(timestamp.UtcDateTime),
            QualityLevel = int.Parse(columns[2].Trim(), CultureInfo.InvariantCulture),
            SpeedMetersPerSecond = ParseOptional(columns[3]),
            DirectionDegrees = ParseOptional(columns[4]),
        };
    }

    /// <summary>Parses a numeric column, mapping DWD's −999 sentinel to null.</summary>
    private static double? ParseOptional(string value)
    {
        double parsed = double.Parse(value.Trim(), CultureInfo.InvariantCulture);
        return parsed == MissingSentinel ? null : parsed;
    }

    /// <summary>
    /// Whole hours, <c>yyyyMMddHH</c> - a different format string from the solar file's
    /// <c>yyyyMMddHH:mm</c>, because solar intervals are WOZ-aligned and land on odd minutes
    /// while these are plain clock hours.
    /// </summary>
    private static DateTimeOffset ParseTimestampUtc(string value)
    {
        var local = DateTime.ParseExact(
            value.Trim(),
            "yyyyMMddHH",
            CultureInfo.InvariantCulture
        );
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc));
    }
}

using System.Globalization;

namespace WeatherSynth.Data;

/// <summary>
/// What the DWD hourly products share at the file level: a header line, semicolon-delimited rows,
/// the −999 missing-value sentinel and <c>yyyyMMddHH</c>-style UTC timestamps. Column layouts
/// differ per product and stay with their readers.
/// </summary>
internal static class DwdCsv
{
    /// <summary>
    /// DWD's missing-value sentinel. Reading it as a number is the single most damaging mistake
    /// available with this format: a missing solar hour would contribute roughly −2,775 Wh/m² to a
    /// daily total, and a missing wind hour would drag a daily mean speed to roughly −40 m/s.
    /// </summary>
    public const double MissingSentinel = -999.0;

    /// <summary>
    /// Streams the data rows of a DWD file: the header is skipped, and so are blank lines.
    /// </summary>
    /// <param name="csvPath">Path to the decompressed <c>produkt_*_stunde_*.txt</c> / CSV file.</param>
    public static IEnumerable<string> ReadDataLines(string csvPath)
    {
        using var reader = new StreamReader(csvPath);

        // Header.
        if (reader.ReadLine() is null)
            yield break;

        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
        }
    }

    /// <summary>Parses a numeric column, mapping DWD's −999 sentinel to null.</summary>
    public static double? ParseOptional(string value)
    {
        double parsed = double.Parse(value.Trim(), CultureInfo.InvariantCulture);
        return parsed == MissingSentinel ? null : parsed;
    }

    /// <summary>Parses a timestamp column in the given exact format.</summary>
    public static DateTime ParseTimestamp(string value, string format) =>
        DateTime.ParseExact(value.Trim(), format, CultureInfo.InvariantCulture);

    /// <summary>Parses a timestamp column in the given exact format, as UTC.</summary>
    public static DateTimeOffset ParseTimestampUtc(string value, string format) =>
        new(DateTime.SpecifyKind(ParseTimestamp(value, format), DateTimeKind.Utc));
}

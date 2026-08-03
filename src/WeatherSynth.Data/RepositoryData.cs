namespace WeatherSynth.Data;

/// <summary>
/// Locates the repository's <c>data/</c> directory.
///
/// The station files are large and live outside the source tree, so tests and samples have to
/// find them by walking up from the running assembly rather than by relative path.
/// </summary>
public static class RepositoryData
{
    /// <summary>Filename of the DWD Bochum hourly solar record.</summary>
    public const string BochumFileName = "dwd_bochum_solar.csv";

    /// <summary>Filename of the DWD Essen-Bredeney hourly wind record.</summary>
    public const string EssenWindFileName = "dwd_essen_wind.csv";

    /// <summary>
    /// Full path to a file in the repository's <c>data/</c> directory, or null when the
    /// directory cannot be found or the file is not present. Callers should treat null as
    /// "skip this" rather than as an error: the data files are not required to build.
    /// </summary>
    public static string? TryLocate(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "data", fileName);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Full path to the DWD Bochum solar record, or null when it is not present.</summary>
    public static string? TryLocateBochum() => TryLocate(BochumFileName);

    /// <summary>Full path to the DWD Essen-Bredeney wind record, or null when it is not present.</summary>
    public static string? TryLocateEssenWind() => TryLocate(EssenWindFileName);
}

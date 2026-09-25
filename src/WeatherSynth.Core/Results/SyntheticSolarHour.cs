namespace WeatherSynth;

/// <summary>
/// One generated hour of sun: the day's index, the hour's own clear-sky ceiling, and the result.
///
/// <para><b>The index is the day's, on every hour of it.</b> The model draws cloudiness once per
/// day, so an hour is its day spread along that hour's share of the clear-sky curve - the right
/// shape and the right total, but no passing clouds. Twenty-four of these always add up to the
/// day's <see cref="SyntheticSolarDay.GhiWhPerM2"/>.</para>
/// </summary>
/// <param name="Start">When the hour begins. It covers <c>[Start, Start + 1 h)</c>.</param>
/// <param name="ClearSkyIndex">The day's drawn clear-sky index.</param>
/// <param name="ClearSkyWhPerM2">The clear-sky ceiling integrated over this hour.</param>
/// <param name="GhiWhPerM2">
/// Synthetic global horizontal irradiation over the hour, the product of the two. Over one hour,
/// Wh/m² is also the hour's mean irradiance in W/m².
/// </param>
public readonly record struct SyntheticSolarHour(
    DateTimeOffset Start,
    double ClearSkyIndex,
    double ClearSkyWhPerM2,
    double GhiWhPerM2
)
{
    /// <summary>Synthetic irradiation over the hour in kWh/m².</summary>
    public double GhiKWhPerM2 => GhiWhPerM2 / 1000.0;
}

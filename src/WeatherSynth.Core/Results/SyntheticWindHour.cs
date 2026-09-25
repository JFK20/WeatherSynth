namespace WeatherSynth;

/// <summary>
/// One generated hour of wind: its mean speed before and after the height transfer.
///
/// <para>Drawn so that the day's twenty-four hours average exactly to
/// <see cref="SyntheticWindDay.MeanSpeed"/>, spread with the day's own within-day distribution
/// and ordered by the fitted diurnal cycle and hour-to-hour persistence. Cubing these and averaging
/// is how an energy estimate should use them - no energy pattern factor needed at this
/// resolution.</para>
/// </summary>
/// <param name="Start">When the hour begins. It covers <c>[Start, Start + 1 h)</c>.</param>
/// <param name="SpeedAtReference">Hourly mean speed at the fitting height, m/s.</param>
/// <param name="Speed">Hourly mean speed at the target site, m/s. The number a caller asked for.</param>
public readonly record struct SyntheticWindHour(
    DateTimeOffset Start,
    double SpeedAtReference,
    double Speed
);

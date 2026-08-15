using System;

namespace WeatherSynth.Climate
{
    /// <summary>
    /// One day's wind speed, as the fit consumes it.
    ///
    /// <para>The counterpart of <see cref="DailyClearness"/>, and deliberately much thinner: there
    /// is no ceiling to divide by and so no ratio to form. What is modelled here is the speed
    /// itself, <b>dimensional</b> - m/s, at the height the anemometer sat and over the averaging
    /// window it was measured on. Both of those have to be settled before fitting and cannot be
    /// recovered from the numbers afterwards.</para>
    /// </summary>
    /// <param name="Date">The day, a UTC calendar day where the source data is DWD's wind product.</param>
    /// <param name="MeanSpeed">Mean wind speed over the day, m/s. The fitted quantity.</param>
    /// <param name="MeanCubedSpeed">
    /// Mean of the day's <i>cubed</i> hourly speeds, m³/s³. Carried alongside rather than derived,
    /// because it cannot be recovered from <paramref name="MeanSpeed"/> - see
    /// <see cref="EnergyPatternFactor"/>.
    /// </param>
    public readonly record struct DailyWindSpeed(
        DateOnly Date,
        double MeanSpeed,
        double MeanCubedSpeed
    )
    {
        /// <summary>
        /// The day's energy pattern factor, <c>mean(v³) / mean(v)³</c>.
        ///
        /// <para>Wind power goes as the cube of speed, and E[v³] exceeds (E[v])³ for any day whose
        /// wind is not perfectly constant. So a synthetic daily mean speed, cubed, understates the
        /// day's energy - by a median of about 25% at the station this was measured on, and always
        /// in that direction. The factor is carried through the model rather than folded into a
        /// power calculation so that the correction stays visible.</para>
        /// </summary>
        public double EnergyPatternFactor =>
            MeanSpeed > 0.0 ? MeanCubedSpeed / (MeanSpeed * MeanSpeed * MeanSpeed) : double.NaN;
    }
}

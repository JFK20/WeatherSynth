using System;

namespace WeatherSynth
{
    /// <summary>One generated day: the speed as fitted, the speed after the transfer, and the energy proxy.</summary>
    /// <param name="Date">The day generated.</param>
    /// <param name="MeanSpeedAtReference">
    /// The value drawn from the fitted distribution, in m/s at the height the model was fitted at.
    ///
    /// <para>Carried alongside the transferred figure rather than folded away, because the single
    /// most likely error in a wind pipeline is a height transfer applied twice - and the only cheap
    /// way to catch it is to be able to see what the number was before.</para>
    /// </param>
    /// <param name="MeanSpeed">
    /// Daily mean wind speed at the target site, m/s. The number a caller asked for.
    /// </param>
    /// <param name="MeanCubedSpeed">
    /// Mean of the cubed speeds implied for the day, m³/s³ - what an energy estimate needs.
    ///
    /// <para><b>Derived from the record's mean energy pattern factor, not drawn.</b> Power goes as
    /// v³ and E[v³] exceeds (E[v])³ for any day whose wind varies at all, so cubing
    /// <see cref="MeanSpeed"/> understates the day by a median of about 25% at this station. This
    /// applies the record's average correction, which fixes the systematic bias but flattens the
    /// day-to-day spread in it - measured p10 to p90 is 1.08 to 1.58. Right for an annual yield,
    /// too smooth for anything that cares about the distribution of daily energy.</para>
    /// </param>
    public readonly record struct SyntheticWindDay(
        DateOnly Date,
        double MeanSpeedAtReference,
        double MeanSpeed,
        double MeanCubedSpeed
    )
    {
        /// <summary>
        /// The factor by which this day's energy exceeds what its mean speed alone would suggest.
        /// Constant across days by construction - see <see cref="MeanCubedSpeed"/>.
        /// </summary>
        public double EnergyPatternFactor =>
            MeanSpeed > 0.0 ? MeanCubedSpeed / (MeanSpeed * MeanSpeed * MeanSpeed) : double.NaN;
    }
}

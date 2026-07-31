using System;

namespace WeatherModel.Climate
{
    /// <summary>
    /// One day's clearness index: how much of the physically available solar energy actually
    /// reached the ground.
    ///
    /// <para>This is the quantity the stochastic model is built on, rather than irradiance
    /// itself. Irradiance has a hard ceiling that varies enormously with season and latitude;
    /// dividing it out leaves a bounded number that means "how cloudy was it" and nothing
    /// else which is what makes a single distribution fittable across the year.</para>
    /// </summary>
    /// <param name="Date">The day, in true solar time where the source data is solar-aligned.</param>
    /// <param name="ObservedWhPerM2">Measured global horizontal irradiation for the day.</param>
    /// <param name="ClearSkyWhPerM2">
    /// Modelled clear-sky irradiation, integrated over exactly the intervals the observation
    /// covers so the ratio is matched rather than approximate.
    /// </param>
    /// <param name="ExtraterrestrialWhPerM2">
    /// Irradiation that would arrive on the horizontal with no atmosphere the denominator of
    /// the classical clearness index.
    /// </param>
    public readonly record struct DailyClearness(
        DateOnly Date,
        double ObservedWhPerM2,
        double ClearSkyWhPerM2,
        double ExtraterrestrialWhPerM2)
    {
        /// <summary>
        /// The <b>clear-sky index</b>: measured energy over modelled clear-sky energy.
        ///
        /// <para>This is the one to model. It divides out both the solar geometry and the
        /// turbidity climatology, leaving a quantity that means "how cloudy was it" and little
        /// else which is what lets one distribution serve the whole year.</para>
        ///
        /// <para><b>It reaches 1.0 on a cloudless day, by construction</b>, because the ceiling
        /// is fitted to reproduce measured cloudless days. It is <i>not</i> bounded by 1: the
        /// ceiling uses a monthly-mean turbidity, so a day cleaner than that month's average
        /// legitimately exceeds it. In the Bochum record 5.7% of days do, almost all between
        /// 1.02 and 1.09, consistent with the ±3.4% clear-day scatter that a monthly
        /// climatology cannot capture. Support for fitting is roughly [0, 1.25].</para>
        ///
        /// <para>Do not confuse with <see cref="ClearnessIndex"/> the two differ by the
        /// atmosphere's own transmittance and have quite different ranges.</para>
        /// </summary>
        public double ClearSkyIndex => ClearSkyWhPerM2 > 0.0 ? ObservedWhPerM2 / ClearSkyWhPerM2 : double.NaN;

        /// <summary>
        /// The <b>classical clearness index</b> Kt: measured energy over extraterrestrial energy.
        ///
        /// <para>This is what the literature usually means by "Kt", and where the familiar
        /// 0.05-0.75 range comes from: a cloudless day tops out near 0.75 because even a clear
        /// atmosphere removes about a quarter of the incoming energy.</para>
        ///
        /// <para>Kept for cross-checking rather than modelling. It retains a seasonal signal
        /// that the clear-sky index removes, since clear-sky transmittance itself varies with
        /// sun elevation which is precisely the structure the model is trying to divide out.</para>
        /// </summary>
        public double ClearnessIndex =>
            ExtraterrestrialWhPerM2 > 0.0 ? ObservedWhPerM2 / ExtraterrestrialWhPerM2 : double.NaN;
    }
}

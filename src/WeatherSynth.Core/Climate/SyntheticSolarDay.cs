namespace WeatherSynth
{
    /// <summary>One generated day: the sampled index, the ceiling it was applied to, and the result.</summary>
    /// <param name="Date">The day generated.</param>
    /// <param name="ClearSkyIndex">The value drawn from the fitted distribution for that month.</param>
    /// <param name="ClearSkyWhPerM2">The deterministic ceiling for that date and location.</param>
    /// <param name="GhiWhPerM2">Synthetic global horizontal irradiation, the product of the two.</param>
    public readonly record struct SyntheticSolarDay(
        DateOnly Date,
        double ClearSkyIndex,
        double ClearSkyWhPerM2,
        double GhiWhPerM2
    )
    {
        /// <summary>Synthetic daily irradiation in kWh/m², the unit the totals are usually quoted in.</summary>
        public double GhiKWhPerM2 => GhiWhPerM2 / 1000.0;
    }
}

namespace WeatherSynth
{
    /// <summary>One generated day of both resources, drawn together.</summary>
    /// <param name="Date">The day generated. The same day in both halves, by construction.</param>
    /// <param name="Solar">The solar half: index, ceiling, and synthetic irradiation.</param>
    /// <param name="Wind">The wind half: speed at the fitting height, at the target, and the energy proxy.</param>
    public readonly record struct CoupledWeatherDay(
        DateOnly Date,
        SyntheticSolarDay Solar,
        SyntheticWindDay Wind
    );

    /// <summary>
    /// One generated year of both resources, drawn together so that the two are jointly plausible
    /// and not merely plausible one at a time.
    ///
    /// <para><b>What this adds over generating two years separately</b> is the pairing, and only
    /// the pairing. Each half is statistically identical to what the independent providers produce
    /// - the same twelve marginals, the same persistence, the same annual totals in distribution -
    /// because the coupling reorders which days land together and touches nothing else. What
    /// changes is that a still, overcast week now looks like one, and a windy December no longer
    /// arrives with a December of sunshine.</para>
    ///
    /// <para><b>The two aggregates are not the same kind of number, and this class does not hide
    /// that.</b> <see cref="Solar"/> carries an annual <i>total</i> - kWh/m², the figure a yield
    /// estimate starts from. <see cref="Wind"/> carries an annual <i>mean</i>, because adding daily
    /// wind speeds together produces a number with no physical meaning. Both are built by handing
    /// the split day lists to the existing aggregates rather than by a third aggregator, so
    /// whatever those two say elsewhere they say here.</para>
    /// </summary>
    public sealed class CoupledWeatherYear
    {
        /// <summary>
        /// Wraps an already-generated run of paired days.
        /// </summary>
        /// <param name="year">The calendar year the days belong to.</param>
        /// <param name="seed">Seed the run was drawn with, so it can be reproduced.</param>
        /// <param name="days">The generated days, in date order.</param>
        internal CoupledWeatherYear(int year, int seed, IReadOnlyList<CoupledWeatherDay> days)
        {
            if (days is null)
                throw new ArgumentNullException(nameof(days));
            if (days.Count == 0)
                throw new ArgumentException("A year needs at least one day.", nameof(days));

            Year = year;
            Seed = seed;
            Days = days;

            var solar = new List<SyntheticSolarDay>(days.Count);
            var wind = new List<SyntheticWindDay>(days.Count);

            foreach (var day in days)
            {
                solar.Add(day.Solar);
                wind.Add(day.Wind);
            }

            // Reused rather than re-derived: the solar year validates that every day belongs to the
            // year and sums the totals, the wind year averages them. Both checks still apply here.
            Solar = new SyntheticSolarYear(year, seed, solar);
            Wind = new SyntheticWindYear(year, seed, wind);
        }

        /// <summary>The calendar year generated.</summary>
        public int Year { get; }

        /// <summary>
        /// Seed the run was drawn with. Same year, same seed, same two sites gives the same days.
        ///
        /// <para>One seed drives both halves, and it has to: two seeds would mean two independent
        /// streams, which is the thing this class exists not to be.</para>
        /// </summary>
        public int Seed { get; }

        /// <summary>Every generated day, in date order, with both resources on each.</summary>
        public IReadOnlyList<CoupledWeatherDay> Days { get; }

        /// <summary>The solar half, with its monthly and annual totals.</summary>
        public SyntheticSolarYear Solar { get; }

        /// <summary>The wind half, with its monthly and annual means.</summary>
        public SyntheticWindYear Wind { get; }
    }
}

namespace WeatherSynth.Climate
{
    /// <summary>
    /// Twelve fitted distributions, one per calendar month, plus the day-to-day persistence that
    /// belongs with them. What <see cref="LatentAr1Chain"/> needs from a model, and nothing else.
    ///
    /// <para><b>Why the chain sees this rather than a model.</b> The persistence layer is a
    /// statement about ordering, not about any particular quantity: it reorders days through a
    /// hidden normal variable and reads the result back through whatever marginal the month has.
    /// It never needs to know that a clear-sky index is bounded at 1.25 or that a wind speed is
    /// bounded below by a location parameter - only that the month's distribution can answer two
    /// questions. Keeping the interface this narrow is what lets one chain serve both halves of
    /// this library, and it is why adding a third quantity later would need no new chain.</para>
    ///
    /// <para>Month-indexed rather than returning a distribution object, deliberately. Handing back
    /// a <see cref="ScaledBeta"/> or a <see cref="Weibull"/> would put the distribution's type in
    /// the chain's signature and undo the separation.</para>
    /// </summary>
    public interface IMonthlyMarginals
    {
        /// <summary>
        /// The value this month's distribution falls below with probability
        /// <paramref name="probability"/>.
        ///
        /// <para>The transform that carries a uniform back to the fitted marginal, and so the one
        /// the copula runs on. It must reproduce the month's distribution <i>exactly</i>, or the
        /// persistence layer stops being free.</para>
        /// </summary>
        /// <param name="probability">Probability in [0, 1].</param>
        /// <param name="month">Calendar month, 1-12.</param>
        double Quantile(double probability, int month);

        /// <summary>
        /// Probability that a draw from this month's distribution falls at or below
        /// <paramref name="value"/>.
        ///
        /// <para>The inverse direction, used when fitting: mapping a measured day through its own
        /// month's CDF is what removes the seasonal cycle before the persistence coefficient is
        /// measured.</para>
        /// </summary>
        /// <param name="value">The observation, in whatever units the model is fitted in.</param>
        /// <param name="month">Calendar month, 1-12.</param>
        double CumulativeProbability(double value, int month);

        /// <summary>
        /// The fitted lag-1 coefficient of the latent process, in [0, 1). The thirteenth parameter
        /// of a model that is otherwise twelve shapes.
        /// </summary>
        double Persistence { get; }
    }
}

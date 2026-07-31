using System;

namespace WeatherModel.Climate
{
    /// <summary>
    /// Draws a sequence of clear-sky indices that both matches the fitted monthly distributions
    /// and clusters the way real weather does.
    ///
    /// <para><b>The problem this solves.</b> <see cref="ClearSkyIndexModel"/> gets the histogram
    /// right and the sequence wrong: sampled independently, its lag-1 autocorrelation at Bochum
    /// is 0.137 against a measured 0.437, and all 0.137 of that comes from the seasonal cycle
    /// rather than from weather. Overcast spells in the real record last for days; independent
    /// draws produce a bright day in the middle of every one of them.</para>
    ///
    /// <para><b>How.</b> The autoregression runs on a hidden standard-normal variable rather than
    /// on the index itself, and the index is recovered from it by matching quantiles:</para>
    /// <code>
    /// z_t = phi^gap * z_(t-1) + sqrt(1 - phi^(2*gap)) * N(0,1)
    /// k_t = ScaledBeta[month].Quantile(Gaussian.Cdf(z_t))
    /// </code>
    ///
    /// <para>Doing it this way rather than putting an AR(1) directly on the index is what keeps
    /// the twelve fitted shapes <b>exactly</b> intact. <c>z</c> is stationary N(0,1), so
    /// <c>Cdf(z)</c> is exactly uniform, so the quantile lookup reproduces the month's marginal
    /// to the last bit. Only the order in which values arrive changes. An AR(1) written directly
    /// on the index would need an innovation distribution that has no closed form, and would walk
    /// outside [0, Scale] besides.</para>
    ///
    /// <para>Month boundaries need no special handling: <c>z</c> carries straight across and only
    /// the marginal it is read through changes. That is the whole reason for working in latent
    /// space.</para>
    ///
    /// <para><b>Stateful and order-dependent.</b> Each call depends on the previous one. Generate
    /// a run in date order, and use <see cref="Reset"/> between independent runs. One instance
    /// per thread.</para>
    /// </summary>
    public sealed class ClearSkyIndexChain
    {
        private readonly ClearSkyIndexModel _model;

        private double _latent;
        private DateOnly? _lastDate;

        /// <summary>A chain at the persistence the model was fitted with.</summary>
        /// <param name="model">Fitted index distributions, which also carry the fitted phi.</param>
        public ClearSkyIndexChain(ClearSkyIndexModel model)
            : this(model, (model ?? throw new ArgumentNullException(nameof(model))).Persistence)
        {
        }

        /// <summary>A chain at a stated persistence, whatever the model was fitted with.</summary>
        /// <param name="model">Fitted index distributions, which supply the monthly marginals.</param>
        /// <param name="persistence">
        /// The AR(1) coefficient to run at, in [0, 1). Zero reduces the chain exactly to
        /// independent sampling, which is what the reports use as the before-and-after baseline.
        /// </param>
        public ClearSkyIndexChain(ClearSkyIndexModel model, double persistence)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));

            if (!(persistence >= 0.0) || persistence >= 1.0)
                throw new ArgumentOutOfRangeException(
                    nameof(persistence), persistence,
                    "Persistence must be in [0, 1); at 1 the process has no stationary distribution.");

            Persistence = persistence;
        }

        /// <summary>The AR(1) coefficient this chain is running at.</summary>
        public double Persistence { get; }

        /// <summary>
        /// Forgets the previous day, so the next <see cref="Next"/> starts a fresh run.
        ///
        /// <para>No burn-in follows: the first draw comes straight from the stationary N(0,1),
        /// so the chain is in equilibrium from its first day.</para>
        /// </summary>
        public void Reset()
        {
            _lastDate = null;
            _latent = 0.0;
        }

        /// <summary>
        /// The next index in the sequence, for the given date.
        /// </summary>
        /// <param name="date">
        /// The day being generated. Both its month, which selects the marginal, and its distance
        /// from the previous call, which sets how much correlation survives, are used.
        /// </param>
        /// <param name="random">Source of randomness.</param>
        public double Next(DateOnly date, Random random)
        {
            if (random is null) throw new ArgumentNullException(nameof(random));

            int gap = _lastDate is { } previous ? date.DayNumber - previous.DayNumber : 0;

            // Gap-aware, and this is not cosmetic: 7.4% of the DWD record is missing, so a two-day
            // hole has to decay the correlation twice. The matching sqrt(1 - decay^2) is what keeps
            // the latent variable standard normal across the gap - use phi with a plain 1 - phi^2
            // and the process drifts off its own scale.
            //
            // A fresh start, or a caller that went backwards, is total decay: the update below
            // reduces to a draw from the stationary distribution, which is exactly right when the
            // previous value's lag is meaningless.
            double decay = gap > 0 ? Math.Pow(Persistence, gap) : 0.0;

            _latent = decay * _latent
                      + Math.Sqrt(Math.Max(0.0, 1.0 - decay * decay)) * Gaussian.Sample(random);

            _lastDate = date;

            return _model.ForMonth(date.Month).Quantile(Gaussian.Cdf(_latent));
        }
    }
}

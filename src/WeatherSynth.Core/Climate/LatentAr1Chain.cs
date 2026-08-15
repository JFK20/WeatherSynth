using System;

namespace WeatherSynth.Climate
{
    /// <summary>
    /// Draws a sequence of daily values that both matches a set of fitted monthly distributions
    /// and clusters the way real weather does.
    ///
    /// <para><b>The problem this solves.</b> Twelve monthly marginals get the histogram right and
    /// the sequence wrong. Sampled independently, the clear-sky index at Bochum has a lag-1
    /// autocorrelation of 0.137 against a measured 0.437, and daily wind speed at Essen 0.093
    /// against 0.529 - in both cases all of what the model does reproduce is the seasonal cycle
    /// alone, since consecutive days share a monthly distribution. Real overcast spells and real
    /// calm spells last for days; independent draws put a bright, breezy day in the middle of
    /// every one of them.</para>
    ///
    /// <para><b>How.</b> The autoregression runs on a hidden standard-normal variable rather than
    /// on the modelled quantity, and the quantity is recovered from it by matching quantiles:</para>
    /// <code>
    /// z_t = phi^gap * z_(t-1) + sqrt(1 - phi^(2*gap)) * N(0,1)
    /// x_t = marginals.Quantile(Gaussian.Cdf(z_t), month)
    /// </code>
    ///
    /// <para>Doing it this way rather than putting an AR(1) directly on the quantity is what keeps
    /// the twelve fitted shapes <b>exactly</b> intact. <c>z</c> is stationary N(0,1), so
    /// <c>Cdf(z)</c> is exactly uniform, so the quantile lookup reproduces the month's marginal to
    /// the last bit. Only the order in which values arrive changes. An AR(1) written directly on
    /// the quantity would need an innovation distribution that has no closed form, and would walk
    /// outside the marginal's support besides - off the top of a Beta, or below a Weibull's
    /// location parameter.</para>
    ///
    /// <para><b>What it does not do.</b> A Gaussian copula preserves <i>rank</i> correlation
    /// exactly. The Pearson correlation that comes back after the quantile transform depends on
    /// the marginal's shape, so it need not land exactly on the figure phi was fitted from: solar
    /// reproduces its 0.437 to four digits, while wind comes back around 3% short of its 0.529.
    /// That is the copula behaving as designed, not a bug to calibrate away.</para>
    ///
    /// <para>Month boundaries need no special handling: <c>z</c> carries straight across and only
    /// the marginal it is read through changes. That is the whole reason for working in latent
    /// space, and it is also why this class knows nothing about what it is generating - see
    /// <see cref="IMonthlyMarginals"/>.</para>
    ///
    /// <para><b>Stateful and order-dependent.</b> Each call depends on the previous one. Generate
    /// a run in date order, and use <see cref="Reset"/> between independent runs. One instance
    /// per thread.</para>
    /// </summary>
    public sealed class LatentAr1Chain
    {
        private readonly IMonthlyMarginals _marginals;

        private double _latent;
        private DateOnly? _lastDate;

        /// <summary>A chain at the persistence its marginals were fitted with.</summary>
        /// <param name="marginals">Fitted monthly distributions, which also carry the fitted phi.</param>
        public LatentAr1Chain(IMonthlyMarginals marginals)
            : this(
                marginals,
                (marginals ?? throw new ArgumentNullException(nameof(marginals))).Persistence
            ) { }

        /// <summary>A chain at a stated persistence, whatever its marginals were fitted with.</summary>
        /// <param name="marginals">Fitted monthly distributions.</param>
        /// <param name="persistence">
        /// The AR(1) coefficient to run at, in [0, 1). Zero reduces the chain exactly to
        /// independent sampling, which is what the reports use as the before-and-after baseline.
        /// </param>
        public LatentAr1Chain(IMonthlyMarginals marginals, double persistence)
        {
            _marginals = marginals ?? throw new ArgumentNullException(nameof(marginals));

            if (!(persistence >= 0.0) || persistence >= 1.0)
                throw new ArgumentOutOfRangeException(
                    nameof(persistence),
                    persistence,
                    "Persistence must be in [0, 1); at 1 the process has no stationary distribution."
                );

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
        /// The next value in the sequence, for the given date.
        /// </summary>
        /// <param name="date">
        /// The day being generated. Both its month, which selects the marginal, and its distance
        /// from the previous call, which sets how much correlation survives, are used.
        /// </param>
        /// <param name="random">Source of randomness.</param>
        public double Next(DateOnly date, Random random)
        {
            if (random is null)
                throw new ArgumentNullException(nameof(random));

            int gap = _lastDate is { } previous ? date.DayNumber - previous.DayNumber : 0;

            // Gap-aware, and this is not cosmetic: 7.4% of the solar record is missing, so a
            // two-day hole has to decay the correlation twice. The matching sqrt(1 - decay^2) is
            // what keeps the latent variable standard normal across the gap - use phi with a plain
            // 1 - phi^2 and the process drifts off its own scale.
            //
            // A fresh start, or a caller that went backwards, is total decay: the update below
            // reduces to a draw from the stationary distribution, which is exactly right when the
            // previous value's lag is meaningless.
            double decay = gap > 0 ? Math.Pow(Persistence, gap) : 0.0;

            _latent =
                decay * _latent
                + Math.Sqrt(Math.Max(0.0, 1.0 - decay * decay)) * Gaussian.Sample(random);

            _lastDate = date;

            return _marginals.Quantile(Gaussian.Cdf(_latent), date.Month);
        }
    }
}

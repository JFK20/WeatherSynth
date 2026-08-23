namespace WeatherSynth.Climate
{
    /// <summary>
    /// Draws two daily quantities at once, so that each matches its own fitted monthly
    /// distributions, each carries its own day-to-day persistence, and the two move together the
    /// way the record says they do.
    ///
    /// <para><b>The problem this solves.</b> Two <see cref="LatentAr1Chain"/>s run side by side
    /// produce series that are individually perfect and jointly wrong: a synthetic year pairs a
    /// brilliant day with a gale as happily as with a calm. At the stations this was built on,
    /// daily wind speed and daily sunshine correlate at about -0.22 - windy days are cloudy days.
    /// That is small enough to ignore for either quantity alone and decisive for anything that
    /// asks about both at once, which is the whole question when sizing a hybrid PV and wind
    /// system: whether the two resources fill in for each other.</para>
    ///
    /// <para><b>How.</b> Two latent AR(1) processes, exactly as in <see cref="LatentAr1Chain"/>,
    /// but driven by <i>correlated</i> innovations rather than independent ones:</para>
    /// <code>
    /// zA = dA*zA' + sqrt(1 - dA^2) * eA
    /// zB = dB*zB' + sqrt(1 - dB^2) * eB       corr(eA, eB) = r
    /// </code>
    ///
    /// <para>Each latent is still marginally standard normal, so <c>Cdf(z)</c> is still exactly
    /// uniform and the quantile lookup still reproduces each month's fitted marginal <b>to the last
    /// bit</b>. That is the same argument that makes the univariate chain safe, and it is why
    /// coupling costs nothing: it changes which days land together, and nothing else. Neither
    /// model's twelve shapes move, and neither model's phi moves.</para>
    ///
    /// <para><b>The correction that is easy to miss: r is not rho.</b> For a first-order vector
    /// autoregression with diagonal coefficient matrix diag(dA, dB), the stationary
    /// cross-correlation obeys <c>rho = dA*dB*rho + sqrt((1-dA^2)(1-dB^2))*r</c>. Feeding the
    /// target rho straight in as the innovation correlation therefore <i>under</i>-delivers the
    /// coupling, by a factor that depends on both persistences and leaves no other trace anywhere
    /// in the output. Inverting it gives what this class actually uses:</para>
    /// <code>
    /// r = rho * (1 - dA*dB) / sqrt((1 - dA^2)(1 - dB^2))
    /// </code>
    ///
    /// <para>At the fitted solar and wind persistences (0.353 and 0.444) that inflation is 1.006 at
    /// a one-day step and falls back towards 1 as gaps widen. Small here - but it is the term that
    /// makes the fitted rho and the generated rho the same number, and
    /// <c>CoupledLatentAr1ChainTests</c> fails loudly without it.</para>
    ///
    /// <para><b>Rho is per month</b>, taken from the month of the day being generated. The
    /// coefficient therefore steps at month boundaries while the latent state carries across, the
    /// same way the marginals do. That is a discontinuity in the dependence and not in the values,
    /// and it is invisible in the output.</para>
    ///
    /// <para><b>Stateful and order-dependent</b>, like the chain it generalises: each call depends
    /// on the one before it. Generate a run in date order, and use <see cref="Reset"/> between
    /// independent runs. One instance per thread.</para>
    /// </summary>
    internal sealed class CoupledLatentAr1Chain
    {
        private readonly IMonthlyMarginals _marginalsA;
        private readonly IMonthlyMarginals _marginalsB;
        private readonly MonthlyCoupling _coupling;
        private readonly double _persistenceA;
        private readonly double _persistenceB;

        private double _latentA;
        private double _latentB;
        private DateOnly? _lastDate;

        /// <summary>A coupled chain at the persistences its two models were fitted with.</summary>
        /// <param name="marginalsA">Fitted monthly distributions for the first quantity.</param>
        /// <param name="marginalsB">Fitted monthly distributions for the second quantity.</param>
        /// <param name="coupling">
        /// Fitted monthly cross-correlation, in the same order as the two marginals: swapping the
        /// arguments here without swapping them in the fit inverts nothing and simply mislabels
        /// which quantity is which, so keep the pairing that <see cref="MonthlyCoupling.Fit"/> saw.
        /// </param>
        public CoupledLatentAr1Chain(
            IMonthlyMarginals marginalsA,
            IMonthlyMarginals marginalsB,
            MonthlyCoupling coupling
        )
            : this(
                marginalsA,
                marginalsB,
                coupling,
                (marginalsA ?? throw new ArgumentNullException(nameof(marginalsA))).Persistence,
                (marginalsB ?? throw new ArgumentNullException(nameof(marginalsB))).Persistence
            ) { }

        /// <summary>A coupled chain at stated persistences, whatever its models were fitted with.</summary>
        /// <param name="marginalsA">Fitted monthly distributions for the first quantity.</param>
        /// <param name="marginalsB">Fitted monthly distributions for the second quantity.</param>
        /// <param name="coupling">Fitted monthly cross-correlation.</param>
        /// <param name="persistenceA">First quantity's AR(1) coefficient, in [0, 1).</param>
        /// <param name="persistenceB">Second quantity's AR(1) coefficient, in [0, 1).</param>
        public CoupledLatentAr1Chain(
            IMonthlyMarginals marginalsA,
            IMonthlyMarginals marginalsB,
            MonthlyCoupling coupling,
            double persistenceA,
            double persistenceB
        )
        {
            _marginalsA = marginalsA ?? throw new ArgumentNullException(nameof(marginalsA));
            _marginalsB = marginalsB ?? throw new ArgumentNullException(nameof(marginalsB));
            _coupling = coupling ?? throw new ArgumentNullException(nameof(coupling));

            _persistenceA = Validated(persistenceA, nameof(persistenceA));
            _persistenceB = Validated(persistenceB, nameof(persistenceB));
        }

        /// <summary>The first quantity's AR(1) coefficient.</summary>
        public double PersistenceA => _persistenceA;

        /// <summary>The second quantity's AR(1) coefficient.</summary>
        public double PersistenceB => _persistenceB;

        /// <summary>The coupling this chain is driving.</summary>
        public MonthlyCoupling Coupling => _coupling;

        /// <summary>
        /// Forgets the previous day, so the next <see cref="Next"/> starts a fresh run.
        ///
        /// <para>No burn-in follows. The first draw comes from the stationary <i>joint</i>
        /// distribution - both decays are zero, so the innovation correlation reduces to rho itself
        /// and the very first pair already carries the coupling.</para>
        /// </summary>
        public void Reset()
        {
            _lastDate = null;
            _latentA = 0.0;
            _latentB = 0.0;
        }

        /// <summary>
        /// The next pair in the sequence, for the given date.
        ///
        /// <para>Both values are returned together, and that is the point: the two cannot be drawn
        /// from separate calls without losing the dependence between them.</para>
        /// </summary>
        /// <param name="date">
        /// The day being generated. Its month selects both marginals and the coupling coefficient;
        /// its distance from the previous call sets how much of each correlation survives.
        /// </param>
        /// <param name="random">Source of randomness, shared by both streams.</param>
        public (double A, double B) Next(DateOnly date, Random random)
        {
            if (random is null)
                throw new ArgumentNullException(nameof(random));

            int gap = _lastDate is { } previous ? date.DayNumber - previous.DayNumber : 0;

            // Gap-aware exactly as the univariate chain is, and for the same reason: a hole in the
            // record has to decay the correlation once per day it spans, with the matching
            // sqrt(1 - decay^2) keeping each latent standard normal across it. A fresh start or a
            // backwards step is total decay, which reduces the update to a draw from the stationary
            // joint distribution.
            double decayA = gap > 0 ? Math.Pow(_persistenceA, gap) : 0.0;
            double decayB = gap > 0 ? Math.Pow(_persistenceB, gap) : 0.0;

            double innovationCorrelation = InnovationCorrelation(
                _coupling.ForMonth(date.Month),
                decayA,
                decayB
            );

            // Cholesky of a 2x2 correlation matrix, which is all this is: one shared draw and one
            // independent one. Drawing both from the same Random is what ties the two streams to a
            // single seed.
            double shared = Gaussian.Sample(random);
            double independent = Gaussian.Sample(random);

            double innovationA = shared;
            double innovationB =
                innovationCorrelation * shared
                + Math.Sqrt(Math.Max(0.0, 1.0 - innovationCorrelation * innovationCorrelation))
                    * independent;

            _latentA =
                decayA * _latentA + Math.Sqrt(Math.Max(0.0, 1.0 - decayA * decayA)) * innovationA;
            _latentB =
                decayB * _latentB + Math.Sqrt(Math.Max(0.0, 1.0 - decayB * decayB)) * innovationB;

            _lastDate = date;

            return (
                _marginalsA.Quantile(Gaussian.Cdf(_latentA), date.Month),
                _marginalsB.Quantile(Gaussian.Cdf(_latentB), date.Month)
            );
        }

        /// <summary>
        /// The innovation correlation that makes the two latent processes settle at
        /// <paramref name="rho"/> - see the class remarks for the derivation.
        /// </summary>
        /// <remarks>
        /// The clamp is a guard rather than a working part. The inflation factor exceeds 1 only
        /// when the two persistences differ, it is 1.006 at the pair this library ships with, and
        /// it returns to 1 as the gap grows. It could bind for a pair of quantities whose
        /// persistences are far apart and whose rho is already near the limit - a very sticky
        /// quantity coupled to a memoryless one - and the honest failure there is a slightly weaker
        /// coupling than asked for rather than a NaN propagating into every generated day.
        /// </remarks>
        internal static double InnovationCorrelation(double rho, double decayA, double decayB)
        {
            if (rho == 0.0)
                return 0.0;

            double numerator = 1.0 - decayA * decayB;
            double denominator = Math.Sqrt((1.0 - decayA * decayA) * (1.0 - decayB * decayB));

            if (!(denominator > 0.0))
                return Math.Clamp(rho, -MaximumInnovationCorrelation, MaximumInnovationCorrelation);

            return Math.Clamp(
                rho * numerator / denominator,
                -MaximumInnovationCorrelation,
                MaximumInnovationCorrelation
            );
        }

        // Short of 1 so that sqrt(1 - r^2) stays a real, non-zero number: at exactly 1 the second
        // stream would become a deterministic copy of the first.
        private const double MaximumInnovationCorrelation = 0.999999;

        private static double Validated(double persistence, string name)
        {
            if (!(persistence >= 0.0) || persistence >= 1.0)
                throw new ArgumentOutOfRangeException(
                    name,
                    persistence,
                    "Persistence must be in [0, 1); at 1 the process has no stationary distribution."
                );

            return persistence;
        }
    }
}

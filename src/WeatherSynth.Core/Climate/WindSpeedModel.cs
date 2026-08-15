using System;
using System.Collections.Generic;
using System.Linq;

namespace WeatherSynth.Climate
{
    /// <summary>
    /// The fitted distribution of daily mean wind speed: one three-parameter
    /// <see cref="Weibull"/> per calendar month, plus a pooled fit over the whole year.
    ///
    /// <para>The wind counterpart of <see cref="ClearSkyIndexModel"/>, and structurally its twin -
    /// twelve monthly marginals plus a single <see cref="Persistence"/> coefficient is the entire
    /// learned model. One difference matters, and it is not cosmetic.</para>
    ///
    /// <para><b>These twelve fits carry the whole seasonal cycle.</b> Solar divides irradiance by a
    /// computed clear-sky ceiling, which puts most of the season in the deterministic half and
    /// leaves the twelve Betas describing cloud alone. Wind has no ceiling to divide by, so
    /// nothing is normalised out: the swing from a December mean of 3.83 m/s to an August 2.72
    /// lives entirely in these parameters. Anything that replaces the monthly resolution with a
    /// single pooled fit does not lose a refinement, it loses the seasons.</para>
    ///
    /// <para><b>The parameters belong to a resolution and a height.</b> They are m/s at
    /// <see cref="ReferenceHeightMeters"/>, fitted on daily means. A shape parameter fitted on
    /// daily means is not the one fitted on hourly values - 2.71 against 2.14 at the station this
    /// was built on - so neither the fit nor anything derived from it may be quoted at another
    /// resolution without refitting.</para>
    /// </summary>
    public sealed class WindSpeedModel
    {
        /// <summary>Fewer observations than this in a month and the pooled fit is used instead.</summary>
        public const int MinimumSamplesPerMonth = 30;

        /// <summary>
        /// Ceiling on the fitted persistence. A latent AR(1) needs |phi| &lt; 1 to be stationary,
        /// and anything this close to 1 is a sign the estimate has gone wrong rather than a site
        /// with extraordinary weather.
        /// </summary>
        private const double MaximumPersistence = 0.99;

        private readonly Weibull[] _monthly;

        private WindSpeedModel(
            Weibull[] monthly,
            Weibull pooled,
            double persistence,
            double referenceHeightMeters,
            double meanEnergyPatternFactor
        )
        {
            _monthly = monthly;
            Pooled = pooled;
            Persistence = persistence;
            ReferenceHeightMeters = referenceHeightMeters;
            MeanEnergyPatternFactor = meanEnergyPatternFactor;
        }

        /// <summary>The fit over every day in the record, ignoring season.</summary>
        /// <remarks>
        /// The fallback for months too thin to fit on their own, and nothing else. As a model of
        /// this site's wind it is wrong in a specific way: pooling twelve months whose means run
        /// from 2.7 to 3.8 m/s produces a distribution broader than any month actually is.
        /// </remarks>
        public Weibull Pooled { get; }

        /// <summary>
        /// Height above ground the fitted speeds belong to, in metres, or NaN when the caller did
        /// not state one.
        ///
        /// <para>Carried because A and gamma are dimensional and meaningless without it, and
        /// because the height that will be assumed otherwise is 10 m - which is the standard
        /// nearly everywhere and wrong at the station this model was built on, where the
        /// anemometer sits at 15 m.</para>
        /// </summary>
        public double ReferenceHeightMeters { get; }

        /// <summary>
        /// Mean of the record's daily energy pattern factors, <c>mean(v³)/mean(v)³</c>.
        ///
        /// <para>Not part of the distribution - it is the size of the error that anyone deriving
        /// energy from a synthetic daily mean speed is about to make. Kept on the model so that
        /// the correction travels with the fit instead of having to be remembered.</para>
        /// </summary>
        public double MeanEnergyPatternFactor { get; }

        /// <summary>
        /// Lag-1 coefficient of the latent AR(1) process behind the speed series, in [0, 1).
        ///
        /// <para>Measured on the record's <i>normal scores</i> - each day mapped through its own
        /// month's fitted CDF and then through the inverse normal - not on the raw speeds. That
        /// transform divides out the seasonal cycle by construction, so what is left is weather
        /// persistence alone, and the twelve marginals re-supply the season downstream.</para>
        ///
        /// <para>At Essen-Bredeney this comes out at 0.455 against a raw daily lag-1 of 0.529.
        /// Fitting against the 0.529 would count the season twice. Wind is more persistent than
        /// cloud, as expected: the solar equivalent is 0.353.</para>
        /// </summary>
        public double Persistence { get; }

        /// <summary>The fit for one calendar month, 1-12.</summary>
        public Weibull ForMonth(int month)
        {
            if (month < 1 || month > 12)
                throw new ArgumentOutOfRangeException(nameof(month), month, "Month must be 1-12.");

            return _monthly[month - 1];
        }

        /// <summary>
        /// Fits the model to a measured series.
        /// </summary>
        /// <param name="series">
        /// Daily wind speeds. Filter out incomplete days first: a day averaged over eight hours is
        /// not a daily mean, and nothing downstream can tell the two apart.
        /// </param>
        /// <param name="referenceHeightMeters">
        /// Height above ground the speeds were measured at. Optional only because a synthetic
        /// series has no height; for a real record, state it.
        /// </param>
        public static WindSpeedModel Fit(
            IEnumerable<DailyWindSpeed> series,
            double referenceHeightMeters = double.NaN
        )
        {
            if (series is null)
                throw new ArgumentNullException(nameof(series));

            var byMonth = new List<double>[12];
            for (int i = 0; i < 12; i++)
                byMonth[i] = new List<double>();

            // Dates are kept alongside the values because the persistence fit below needs them,
            // and the caller's sequence may be lazy - enumerating it a second time is not safe.
            var dated = new List<(DateOnly Date, double Speed)>();
            var patternFactors = new List<double>();

            foreach (var day in series)
            {
                double speed = day.MeanSpeed;
                if (double.IsNaN(speed))
                    continue;

                byMonth[day.Date.Month - 1].Add(speed);
                dated.Add((day.Date, speed));

                double factor = day.EnergyPatternFactor;
                if (!double.IsNaN(factor))
                    patternFactors.Add(factor);
            }

            if (dated.Count == 0)
                throw new ArgumentException("No usable days in the series.", nameof(series));

            var pooled = Weibull.FitByMaximumLikelihood(dated.Select(d => d.Speed));

            var monthly = new Weibull[12];
            for (int i = 0; i < 12; i++)
            {
                monthly[i] =
                    byMonth[i].Count >= MinimumSamplesPerMonth
                        ? Weibull.FitByMaximumLikelihood(byMonth[i])
                        : pooled;
            }

            // Order matters: phi is measured through the monthly CDFs, so they have to exist
            // first. The same ordering constraint the solar model works under.
            double persistence = FitPersistence(dated, monthly);

            return new WindSpeedModel(
                monthly,
                pooled,
                persistence,
                referenceHeightMeters,
                patternFactors.Count > 0 ? patternFactors.Average() : double.NaN
            );
        }

        /// <summary>
        /// Lag-1 correlation of the record's normal scores, which is the AR(1) coefficient the
        /// generator needs.
        ///
        /// <para>Each day is mapped through its own month's fitted CDF, giving a value that is
        /// uniform if the fit is good, and then through the inverse normal. Because the transform
        /// is per month, the seasonal cycle comes out with it and the resulting series carries
        /// weather persistence and nothing else - so its lag-1 correlation <i>is</i> phi, with no
        /// simulation loop or search required.</para>
        /// </summary>
        private static double FitPersistence(
            IReadOnlyList<(DateOnly Date, double Speed)> dated,
            Weibull[] monthly
        )
        {
            // A Weibull CDF reaches 1 only in the limit, but a day far out in the tail can still
            // round to it, and the inverse normal sends that to infinity. Nudging inside costs
            // nothing at this magnitude.
            const double edge = 1e-12;

            var latent = new List<(DateOnly Date, double Score)>(dated.Count);

            foreach (var (date, speed) in dated)
            {
                double u = monthly[date.Month - 1].CumulativeProbability(speed);
                u = Math.Clamp(u, edge, 1.0 - edge);
                latent.Add((date, Gaussian.Quantile(u)));
            }

            // Reuses the gap-aware estimator: only genuinely consecutive days are informative
            // about a lag-1 coefficient, and this record does have two one-day holes.
            double phi = IndexSeriesStatistics.Lag1Autocorrelation(latent);

            // A record too short or too broken to estimate from means no persistence, not NaN
            // speeds downstream. Negative persistence is not a thing daily wind does.
            if (double.IsNaN(phi))
                return 0.0;

            return Math.Clamp(phi, 0.0, MaximumPersistence);
        }

        /// <summary>
        /// Draws one daily mean wind speed for the given calendar month, independently of every
        /// other draw.
        ///
        /// <para>Right for a single day, wrong for a series: this is the marginal on its own,
        /// which gets the histogram right and the sequence wrong. Real calm spells and real windy
        /// spells both last for days; independent draws scatter them. The persistence layer that
        /// fixes this arrives with the chain.</para>
        /// </summary>
        public double Sample(int month, Random random) => ForMonth(month).Sample(random);
    }
}

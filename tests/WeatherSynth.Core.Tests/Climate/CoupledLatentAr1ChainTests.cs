using FluentAssertions;
using WeatherSynth.Climate;
using WeatherSynth.Statistics;
using Xunit;

namespace WeatherSynth.Core.Tests;

/// <summary>
/// The coupled persistence layer. Three things have to hold at once and they pull against each
/// other, which is why they are tested together: each marginal must survive untouched, each
/// persistence must survive untouched, and the dependence between the two must appear.
/// </summary>
public class CoupledLatentAr1ChainTests
{
    private static ClearSkyIndexModel SolarModel() => ClimateFixtures.SeasonalModel;

    private static WindSpeedModel WindModel() => WindFixtures.SeasonalModel;

    /// <summary>
    /// Ratio of Kolmogorov's 0.1% critical value to its 5% one, 1.95/1.36. Applied wherever a
    /// per-month KS table is scored as a whole rather than one month at a time - see
    /// <see cref="Leaves_both_marginals_untouched"/>.
    /// </summary>
    private const double MultipleComparisonAllowance = 1.95 / 1.36;

    private static MonthlyCoupling Flat(double rho) =>
        MonthlyCoupling.FromValues(Enumerable.Repeat(rho, 12).ToList());

    private static List<(DateOnly Date, double A, double B)> Run(
        CoupledLatentAr1Chain chain,
        int days,
        int seed = 1234,
        DateOnly? start = null,
        int step = 1
    )
    {
        var random = new Random(seed);
        var date = start ?? new DateOnly(2010, 1, 1);
        var result = new List<(DateOnly, double, double)>(days);

        for (int i = 0; i < days; i++, date = date.AddDays(step))
        {
            var (a, b) = chain.Next(date, random);
            result.Add((date, a, b));
        }

        return result;
    }

    private static double RealisedCoupling(List<(DateOnly Date, double A, double B)> run) =>
        SeriesStatistics.LatentCrossCorrelation(
            run,
            SolarModel().CumulativeProbability,
            WindModel().CumulativeProbability
        );

    /// <summary>
    /// The invariant the whole design rests on: a Gaussian copula reorders which days coincide and
    /// leaves both marginals exactly where they were. If this fails, coupling is not free and the
    /// twelve fitted shapes of each model no longer describe the output.
    ///
    /// <para><b>Run at zero persistence deliberately.</b> A Kolmogorov-Smirnov critical value
    /// assumes independent draws, and the chain's whole purpose is to make consecutive days
    /// dependent - at the fitted persistences a month's effective sample size is a fraction of its
    /// day count, so the iid critical value is far too tight and a correct model fails it. Setting
    /// both persistences to zero isolates the copula, which is what this test is about; the
    /// persistent case is covered comparatively below.</para>
    ///
    /// <para><b>And scored at a stricter level than 5%, because this runs twenty-four tests.</b>
    /// A 5% critical value is <i>meant</i> to be exceeded one time in twenty: at twelve months
    /// times two quantities, a perfectly correct model fails at least one of them about 70% of the
    /// time. That is not a property of this chain - drawing straight from a marginal with no chain
    /// involved does the same. The threshold below is Kolmogorov's 0.1% value (c = 1.95 against
    /// 1.36), which is roughly a Bonferroni correction for twenty-four comparisons and turns a
    /// coin-flip of a test into one that means something.</para>
    /// </summary>
    [Fact]
    public void Leaves_both_marginals_untouched()
    {
        var solar = SolarModel();
        var wind = WindModel();

        var run = Run(
            new CoupledLatentAr1Chain(
                solar,
                wind,
                Flat(-0.5),
                persistenceA: 0.0,
                persistenceB: 0.0
            ),
            40000
        );

        for (int month = 1; month <= 12; month++)
        {
            int captured = month;

            var drawnSolar = run.Where(d => d.Date.Month == captured).Select(d => d.A).ToList();
            var drawnWind = run.Where(d => d.Date.Month == captured).Select(d => d.B).ToList();

            double critical =
                MultipleComparisonAllowance
                * GoodnessOfFit.CriticalValueFivePercent(drawnSolar.Count);

            GoodnessOfFit
                .KolmogorovSmirnovDistance(
                    drawnSolar,
                    v => solar.CumulativeProbability(v, captured)
                )
                .Should()
                .BeLessThan(critical);
            GoodnessOfFit
                .KolmogorovSmirnovDistance(drawnWind, v => wind.CumulativeProbability(v, captured))
                .Should()
                .BeLessThan(critical);
        }
    }

    /// <summary>
    /// The same claim at the fitted persistences, where an absolute critical value does not apply
    /// (see above) - so it is made comparatively instead: coupling must not degrade either marginal
    /// relative to the same chain with the coupling switched off. Both runs carry the same
    /// autocorrelation, so whatever the effective sample size is, it is the same on both sides.
    /// </summary>
    [Fact]
    public void Leaves_both_marginals_untouched_under_persistence()
    {
        var solar = SolarModel();
        var wind = WindModel();

        var coupled = Run(new CoupledLatentAr1Chain(solar, wind, Flat(-0.5)), 40000);
        var uncoupled = Run(new CoupledLatentAr1Chain(solar, wind, MonthlyCoupling.None), 40000);

        for (int month = 1; month <= 12; month++)
        {
            int captured = month;

            double CoupledKs(
                Func<(DateOnly Date, double A, double B), double> pick,
                Func<double, double> cdf
            ) =>
                GoodnessOfFit.KolmogorovSmirnovDistance(
                    coupled.Where(d => d.Date.Month == captured).Select(pick),
                    cdf
                );

            double UncoupledKs(
                Func<(DateOnly Date, double A, double B), double> pick,
                Func<double, double> cdf
            ) =>
                GoodnessOfFit.KolmogorovSmirnovDistance(
                    uncoupled.Where(d => d.Date.Month == captured).Select(pick),
                    cdf
                );

            double solarCritical = GoodnessOfFit.CriticalValueFivePercent(
                coupled.Count(d => d.Date.Month == captured)
            );

            CoupledKs(d => d.A, v => solar.CumulativeProbability(v, captured))
                .Should()
                .BeLessThan(
                    UncoupledKs(d => d.A, v => solar.CumulativeProbability(v, captured))
                        + solarCritical
                );

            CoupledKs(d => d.B, v => wind.CumulativeProbability(v, captured))
                .Should()
                .BeLessThan(
                    UncoupledKs(d => d.B, v => wind.CumulativeProbability(v, captured))
                        + solarCritical
                );
        }
    }

    /// <summary>Each stream keeps its own persistence; coupling is orthogonal to it.</summary>
    [Fact]
    public void Leaves_both_persistences_untouched()
    {
        var solar = SolarModel();
        var wind = WindModel();

        var run = Run(new CoupledLatentAr1Chain(solar, wind, Flat(-0.5)), 40000);

        SeriesStatistics
            .LatentPersistence(run.Select(d => (d.Date, d.A)), solar.CumulativeProbability)
            .Should()
            .BeApproximately(solar.Persistence, 0.02);

        SeriesStatistics
            .LatentPersistence(run.Select(d => (d.Date, d.B)), wind.CumulativeProbability)
            .Should()
            .BeApproximately(wind.Persistence, 0.02);
    }

    [Theory]
    [InlineData(-0.6)]
    [InlineData(-0.22)]
    [InlineData(0.0)]
    [InlineData(0.45)]
    public void Delivers_the_coupling_it_was_asked_for(double rho)
    {
        var chain = new CoupledLatentAr1Chain(SolarModel(), WindModel(), Flat(rho));

        RealisedCoupling(Run(chain, 40000)).Should().BeApproximately(rho, 0.02);
    }

    /// <summary>
    /// <b>The test that the r-correction exists.</b>
    ///
    /// <para>The chain drives an innovation correlation
    /// <c>r = rho*(1 - dA*dB)/sqrt((1 - dA^2)(1 - dB^2))</c>, not rho itself. At the two
    /// persistences this library ships with (0.353 and 0.444) the inflation is 1.006, which no test
    /// could distinguish from sampling noise - so this one uses persistences far apart, where the
    /// factor is 2.1. Passing rho straight through as the innovation correlation lands the realised
    /// figure near -0.19 instead of -0.40, and nothing else anywhere in the output would say so.
    /// </para>
    /// </summary>
    [Fact]
    public void Corrects_the_innovation_correlation_for_the_two_persistences()
    {
        const double rho = -0.40;

        var chain = new CoupledLatentAr1Chain(
            SolarModel(),
            WindModel(),
            Flat(rho),
            persistenceA: 0.9,
            persistenceB: 0.1
        );

        double realised = RealisedCoupling(Run(chain, 60000));

        realised.Should().BeApproximately(rho, 0.03);

        // The value an uncorrected implementation would produce, stated so the test says what it
        // is defending against rather than only that a number moved.
        double uncorrected = rho * Math.Sqrt((1.0 - 0.81) * (1.0 - 0.01)) / (1.0 - 0.09);
        realised.Should().NotBeApproximately(uncorrected, 0.05);
    }

    [Fact]
    public void Innovation_correlation_reduces_to_rho_on_a_fresh_start()
    {
        // Both decays zero: the first day of a run must already carry the full coupling, or every
        // short run would start uncoupled and drift into it.
        CoupledLatentAr1Chain
            .InnovationCorrelation(-0.4, decayA: 0.0, decayB: 0.0)
            .Should()
            .BeApproximately(-0.4, 1e-12);
    }

    [Fact]
    public void Innovation_correlation_is_clamped_rather_than_allowed_past_one()
    {
        // A very sticky quantity coupled to a memoryless one, at a rho already near the limit.
        double r = CoupledLatentAr1Chain.InnovationCorrelation(-0.95, decayA: 0.95, decayB: 0.0);

        Math.Abs(r).Should().BeLessThan(1.0);
        double.IsNaN(r).Should().BeFalse();
    }

    /// <summary>
    /// A clamped innovation correlation must still produce finite, correctly signed output rather
    /// than a NaN propagating into both quantities for every remaining day.
    /// </summary>
    [Fact]
    public void Survives_a_coupling_it_cannot_fully_deliver()
    {
        var chain = new CoupledLatentAr1Chain(
            SolarModel(),
            WindModel(),
            Flat(-0.95),
            persistenceA: 0.95,
            persistenceB: 0.0
        );

        var run = Run(chain, 20000);

        run.Should().OnlyContain(d => !double.IsNaN(d.A) && !double.IsNaN(d.B));
        RealisedCoupling(run).Should().BeNegative();
    }

    /// <summary>
    /// Gaps decay the persistence but not the coupling: two quantities measured on the same day
    /// still co-vary regardless of when the previous day was.
    /// </summary>
    [Fact]
    public void Delivers_the_coupling_across_gaps_in_the_run()
    {
        var chain = new CoupledLatentAr1Chain(SolarModel(), WindModel(), Flat(-0.5));

        // Every fifth day only, so no two generated days are ever consecutive.
        RealisedCoupling(Run(chain, 20000, step: 5)).Should().BeApproximately(-0.5, 0.03);
    }

    /// <summary>
    /// With no coupling the chain reduces to two independent latent AR(1)s <b>in distribution</b>.
    ///
    /// <para>Deliberately not asserted as an equality. The coupled chain draws two normals per day
    /// from one <see cref="Random"/>, where two separate chains draw one each from two; the streams
    /// are consumed in a different order and the day-by-day values differ even at rho = 0. Turning
    /// this into a bit-for-bit comparison would be asserting an implementation detail that is not
    /// true, and the way to get byte-identical independent output is to use the two independent
    /// providers, which this design leaves untouched precisely so that stays available.</para>
    /// </summary>
    [Fact]
    public void Reduces_to_independence_in_distribution_when_uncoupled()
    {
        var run = Run(
            new CoupledLatentAr1Chain(SolarModel(), WindModel(), MonthlyCoupling.None),
            40000
        );

        RealisedCoupling(run).Should().BeApproximately(0.0, 0.02);
    }

    [Fact]
    public void Reset_starts_a_fresh_run()
    {
        var chain = new CoupledLatentAr1Chain(SolarModel(), WindModel(), Flat(-0.4));

        var first = Run(chain, 500, seed: 7);
        chain.Reset();
        var second = Run(chain, 500, seed: 7);

        second.Should().Equal(first);
    }

    [Fact]
    public void Rejects_a_persistence_that_has_no_stationary_distribution()
    {
        FluentActions
            .Invoking(() =>
                new CoupledLatentAr1Chain(
                    SolarModel(),
                    WindModel(),
                    MonthlyCoupling.None,
                    persistenceA: 1.0,
                    persistenceB: 0.4
                )
            )
            .Should()
            .Throw<ArgumentOutOfRangeException>();
    }
}

using FluentAssertions;
using WeatherSynth.Climate;
using Xunit;

namespace WeatherSynth.Core.Tests;

public class WeibullTests
{
    /// <summary>Roughly the January fit at Essen-Bredeney: k 1.93, A 3.12, gamma 0.89.</summary>
    private static readonly Weibull January = new(shape: 1.927, scale: 3.12, location: 0.89);

    [Fact]
    public void Quantile_inverts_the_cdf()
    {
        foreach (double p in new[] { 1e-9, 0.001, 0.05, 0.25, 0.5, 0.75, 0.95, 0.999 })
        {
            double x = January.Quantile(p);
            January.CumulativeProbability(x).Should().BeApproximately(p, 1e-12);
        }

        foreach (double x in new[] { 0.9, 1.0, 2.0, 3.5, 6.0, 12.0 })
        {
            double p = January.CumulativeProbability(x);
            January.Quantile(p).Should().BeApproximately(x, 1e-9);
        }
    }

    [Fact]
    public void The_support_starts_at_the_location_parameter()
    {
        // A day below gamma is impossible by construction, which is the whole point of the third
        // parameter: this station's daily mean essentially never falls below ~1 m/s.
        January.CumulativeProbability(January.Location).Should().Be(0.0);
        January.CumulativeProbability(January.Location - 0.5).Should().Be(0.0);
        January.Density(January.Location - 0.5).Should().Be(0.0);

        January.Quantile(0.0).Should().Be(January.Location);
    }

    [Fact]
    public void The_cdf_stays_accurate_in_the_lower_tail()
    {
        // 1 - exp(-t) written naively collapses to a coarse multiple of the double epsilon down
        // here, and to a flat zero below it. The tail is where the KS distance is measured
        // against the smallest observations, so it has to survive.
        //
        // The tolerance is relative and loose because the round trip out through Quantile is
        // itself lossy at this p - log(1 - 1e-15) is where the digits go, not the CDF. Naive
        // arithmetic misses by 10% or returns zero, so this still fails hard if it comes back.
        // Stops at 1e-16 because that is where the round trip, not the CDF, gives out: Quantile
        // computes log(1 - p), and below the double epsilon 1 - p is exactly 1.
        foreach (double p in new[] { 1e-9, 1e-12, 1e-15 })
        {
            double x = January.Quantile(p);
            January.CumulativeProbability(x).Should().BeApproximately(p, p * 0.01);
        }
    }

    [Fact]
    public void Mean_and_variance_match_the_gamma_closed_forms()
    {
        // At k = 2 the mean is gamma + A*sqrt(pi)/2, which is an independent check on the
        // LogGamma path rather than a restatement of the implementation.
        var atShapeTwo = new Weibull(shape: 2.0, scale: 3.0, location: 1.0);

        atShapeTwo.Mean.Should().BeApproximately(1.0 + 3.0 * Math.Sqrt(Math.PI) / 2.0, 1e-12);
        atShapeTwo
            .Variance.Should()
            .BeApproximately(9.0 * (1.0 - Math.PI / 4.0), 1e-12);

        // k = 1 is the exponential: mean gamma + A, sd A.
        var exponential = new Weibull(shape: 1.0, scale: 2.5, location: 0.5);
        exponential.Mean.Should().BeApproximately(3.0, 1e-9);
        exponential.StandardDeviation.Should().BeApproximately(2.5, 1e-9);
    }

    [Fact]
    public void Sampling_reproduces_the_distributions_own_moments()
    {
        var random = new Random(20260803);
        var draws = Enumerable.Range(0, 200_000).Select(_ => January.Sample(random)).ToList();

        draws.Average().Should().BeApproximately(January.Mean, 0.02);

        // Never below the support and never infinite: the second is not hypothetical, since
        // sampling through 1 - u instead of u sends a small enough uniform to +infinity.
        draws.Should().OnlyContain(v => v >= January.Location && double.IsFinite(v));

        double mean = draws.Average();
        double sd = Math.Sqrt(draws.Sum(v => (v - mean) * (v - mean)) / (draws.Count - 1));
        sd.Should().BeApproximately(January.StandardDeviation, 0.02);
    }

    [Fact]
    public void Scaling_the_parameters_equals_scaling_every_draw()
    {
        // The identity the height transfer rests on: k is invariant, A and gamma scale, and
        // applying the factor to the fitted parameters is exactly - not approximately - the same
        // as applying it to each generated speed.
        const double factor = 1.35;
        var transferred = January.Scaled(factor);

        transferred.Shape.Should().Be(January.Shape);
        transferred.Scale.Should().BeApproximately(January.Scale * factor, 1e-12);
        transferred.Location.Should().BeApproximately(January.Location * factor, 1e-12);

        foreach (double p in new[] { 0.01, 0.1, 0.5, 0.9, 0.99 })
        {
            transferred
                .Quantile(p)
                .Should()
                .BeApproximately(January.Quantile(p) * factor, 1e-9);
        }

        transferred.Mean.Should().BeApproximately(January.Mean * factor, 1e-9);
    }

    [Fact]
    public void Maximum_likelihood_recovers_the_parameters_it_was_drawn_from()
    {
        var random = new Random(7365);
        var draws = Enumerable.Range(0, 50_000).Select(_ => January.Sample(random)).ToList();

        var fit = Weibull.FitByMaximumLikelihood(draws);

        fit.Shape.Should().BeApproximately(January.Shape, 0.08);
        fit.Scale.Should().BeApproximately(January.Scale, 0.15);
        fit.Location.Should().BeApproximately(January.Location, 0.15);
        fit.Mean.Should().BeApproximately(January.Mean, 0.03);
        fit.SampleCount.Should().Be(draws.Count);
    }

    [Fact]
    public void Maximum_likelihood_at_a_known_location_recovers_shape_and_scale()
    {
        var random = new Random(1303);
        var draws = Enumerable.Range(0, 50_000).Select(_ => January.Sample(random)).ToList();

        var fit = Weibull.FitByMaximumLikelihood(draws, January.Location);

        fit.Location.Should().Be(January.Location);
        fit.Shape.Should().BeApproximately(January.Shape, 0.02);
        fit.Scale.Should().BeApproximately(January.Scale, 0.03);
    }

    [Fact]
    public void The_two_parameter_moment_fit_recovers_a_two_parameter_distribution()
    {
        var source = new Weibull(shape: 2.1, scale: 4.0);
        var random = new Random(4242);
        var draws = Enumerable.Range(0, 50_000).Select(_ => source.Sample(random)).ToList();

        var fit = Weibull.FitByMoments(draws);

        fit.Location.Should().Be(0.0);
        fit.Shape.Should().BeApproximately(source.Shape, 0.03);
        fit.Scale.Should().BeApproximately(source.Scale, 0.03);
    }

    [Fact]
    public void Freeing_the_location_parameter_beats_holding_it_at_zero()
    {
        // The measurement behind the decision to model three parameters rather than two: on data
        // that genuinely starts above zero, the two-parameter fit inflates k and fits worse.
        var random = new Random(99);
        var draws = Enumerable.Range(0, 20_000).Select(_ => January.Sample(random)).ToList();

        var threeParameter = Weibull.FitByMaximumLikelihood(draws);
        var twoParameter = Weibull.FitByMoments(draws);

        double three = GoodnessOfFit.KolmogorovSmirnovDistance(
            draws,
            threeParameter.CumulativeProbability
        );
        double two = GoodnessOfFit.KolmogorovSmirnovDistance(
            draws,
            twoParameter.CumulativeProbability
        );

        three.Should().BeLessThan(two);
        twoParameter.Shape.Should().BeGreaterThan(threeParameter.Shape);
    }

    [Fact]
    public void Fitting_refuses_rather_than_clipping_when_the_data_leaves_the_support()
    {
        // Same house rule as ScaledBeta.FitByMoments: an observation outside the support is the
        // data saying the model is wrong, and clipping it turns that into a slightly wrong answer.
        Action belowZero = () => Weibull.FitByMoments(new[] { 1.0, 2.0, -0.5 });
        belowZero.Should().Throw<ArgumentException>();

        Action atTheLocation = () =>
            Weibull.FitByMaximumLikelihood(new[] { 1.0, 2.0, 3.0 }, location: 1.0);
        atTheLocation
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*smallest observation*");

        Action tooFew = () => Weibull.FitByMaximumLikelihood(new[] { 3.0 });
        tooFew.Should().Throw<ArgumentException>();

        Action notANumber = () => Weibull.FitByMaximumLikelihood(new[] { 1.0, double.NaN });
        notANumber.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_density_integrates_to_one()
    {
        const double step = 0.001;
        double total = 0.0;

        for (double x = January.Location; x < 30.0; x += step)
            total += January.Density(x + step / 2.0) * step;

        total.Should().BeApproximately(1.0, 1e-4);
    }

    [Fact]
    public void Rejects_parameters_that_are_not_a_distribution()
    {
        Action zeroShape = () => new Weibull(shape: 0.0, scale: 1.0);
        zeroShape.Should().Throw<ArgumentOutOfRangeException>();

        Action negativeScale = () => new Weibull(shape: 2.0, scale: -1.0);
        negativeScale.Should().Throw<ArgumentOutOfRangeException>();

        Action outOfRange = () => January.Quantile(1.5);
        outOfRange.Should().Throw<ArgumentOutOfRangeException>();
    }
}

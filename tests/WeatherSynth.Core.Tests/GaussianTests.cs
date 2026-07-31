using FluentAssertions;
using WeatherSynth.Climate;
using Xunit;

namespace WeatherSynth.Core.Tests;

/// <summary>
/// The normal distribution is not part of the weather model - the index is bounded and skewed,
/// which is what <see cref="ScaledBeta"/> is for. It exists only as the latent space the AR(1)
/// persistence runs in (knowledge.md §13), and it earns its place there by round-tripping
/// cleanly: any bias in <see cref="Gaussian.Cdf"/> or <see cref="Gaussian.Quantile"/> shows up as
/// a distorted marginal after the copula transform.
/// </summary>
public class GaussianTests
{
    // Reference values from Python's math.erfc, via Phi(z) = erfc(-z/sqrt(2)) / 2.
    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(0.5, 0.6914624612740131)]
    [InlineData(-0.5, 0.30853753872598694)]
    [InlineData(1.0, 0.8413447460685429)]
    [InlineData(-1.0, 0.15865525393145707)]
    [InlineData(1.96, 0.9750021048517795)]
    [InlineData(2.5, 0.9937903346742238)]
    [InlineData(-3.0, 0.0013498980316300957)]
    [InlineData(5.0, 0.9999997133484281)]
    public void Cdf_matches_an_external_reference(double z, double expected)
    {
        Gaussian.Cdf(z).Should().BeApproximately(expected, 1e-14);
    }

    [Fact]
    public void Cdf_holds_its_relative_accuracy_deep_in_the_tail()
    {
        // The rational approximation hands over to a continued fraction at |z| = 7.07, and an
        // absolute tolerance would pass trivially out here - the answer is 6e-16 either way.
        // Relative accuracy is the only meaningful check. The continued fraction is the weaker of
        // the two branches, good to ~1e-8 rather than the ~1e-15 above the crossover, which is
        // ample: the latent variable reaches this far out with probability 1e-15.
        (Gaussian.Cdf(-8.0) / 6.22096057427182e-16)
            .Should()
            .BeApproximately(1.0, 2e-8);
    }

    [Fact]
    public void Cdf_is_symmetric_about_zero()
    {
        foreach (double z in new[] { 0.25, 1.0, 2.7, 4.5, 6.0, 9.0 })
            Gaussian.Cdf(-z).Should().BeApproximately(1.0 - Gaussian.Cdf(z), 1e-15);
    }

    [Fact]
    public void Cdf_saturates_rather_than_returning_values_outside_zero_and_one()
    {
        Gaussian.Cdf(-50.0).Should().Be(0.0);
        Gaussian.Cdf(50.0).Should().Be(1.0);
    }

    [Fact]
    public void Quantile_inverts_the_cdf()
    {
        // Across seven orders of magnitude in probability, since the tails are where the chain
        // spends its extreme days and where Acklam's approximation is weakest before refinement.
        foreach (
            double p in new[]
            {
                1e-7,
                1e-4,
                0.01,
                0.02425,
                0.1,
                0.5,
                0.9,
                0.97575,
                0.99,
                1.0 - 1e-4,
                1.0 - 1e-7,
            }
        )
        {
            Gaussian.Cdf(Gaussian.Quantile(p)).Should().BeApproximately(p, 1e-14 + 1e-12 * p);
        }
    }

    [Fact]
    public void Quantile_recovers_the_value_the_cdf_was_taken_of()
    {
        foreach (double z in new[] { -4.0, -1.5, -0.3, 0.0, 0.3, 1.5, 4.0 })
            Gaussian.Quantile(Gaussian.Cdf(z)).Should().BeApproximately(z, 1e-9);
    }

    [Fact]
    public void Quantile_is_pinned_at_the_ends_of_the_unit_interval()
    {
        Gaussian.Quantile(0.5).Should().BeApproximately(0.0, 1e-15);
        Gaussian.Quantile(0.0).Should().Be(double.NegativeInfinity);
        Gaussian.Quantile(1.0).Should().Be(double.PositiveInfinity);
        Gaussian.Quantile(-0.1).Should().Be(double.NaN);
        Gaussian.Quantile(1.1).Should().Be(double.NaN);
    }

    [Fact]
    public void Samples_have_the_moments_of_a_standard_normal()
    {
        var random = new Random(20260731);
        var draws = Enumerable.Range(0, 200_000).Select(_ => Gaussian.Sample(random)).ToList();

        double mean = draws.Average();
        double variance = draws.Average(d => (d - mean) * (d - mean));

        mean.Should().BeApproximately(0.0, 0.01);
        variance.Should().BeApproximately(1.0, 0.02);
    }
}

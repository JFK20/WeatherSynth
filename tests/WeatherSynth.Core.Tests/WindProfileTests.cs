using FluentAssertions;
using WeatherSynth.Wind;
using Xunit;

namespace WeatherSynth.Core.Tests;

public class WindProfileTests
{
    private static readonly WindSite Anemometer = new(HeightMeters: 15.0, RoughnessLengthMeters: 0.3);

    [Fact]
    public void The_same_place_transfers_by_exactly_one()
    {
        // The property that keeps the profile's uncertainty out of the default path: generating at
        // the fitting station must not touch the numbers at all, and "not at all" has to mean
        // exactly 1.0 rather than 0.9999999.
        Anemometer.TransferFactorFrom(Anemometer).Should().Be(1.0);
        Anemometer.TransferFactorFrom(Anemometer, WindProfile.PowerLaw()).Should().Be(1.0);
    }

    [Fact]
    public void The_log_law_matches_the_hand_computation()
    {
        var hub = new WindSite(HeightMeters: 100.0, RoughnessLengthMeters: 0.1);

        double expected = Math.Log(100.0 / 0.1) / Math.Log(15.0 / 0.3);

        hub.TransferFactorFrom(Anemometer).Should().BeApproximately(expected, 1e-12);
        hub.TransferFactorFrom(Anemometer).Should().BeApproximately(1.766, 0.001);
    }

    [Fact]
    public void The_power_law_ignores_roughness_entirely()
    {
        // Worth pinning down, because a caller who carefully picks a roughness class and then
        // selects this profile will find it changed nothing - better a test says so than a bug
        // report.
        var smooth = new WindSite(HeightMeters: 100.0, RoughnessLengthMeters: 0.03);
        var rough = new WindSite(HeightMeters: 100.0, RoughnessLengthMeters: 1.0);
        var profile = WindProfile.PowerLaw();

        smooth
            .TransferFactorFrom(Anemometer, profile)
            .Should()
            .Be(rough.TransferFactorFrom(Anemometer, profile));

        smooth
            .TransferFactorFrom(Anemometer, profile)
            .Should()
            .BeApproximately(Math.Pow(100.0 / 15.0, 1.0 / 7.0), 1e-12);
    }

    [Fact]
    public void The_two_laws_disagree_by_enough_to_matter()
    {
        // Not a correctness check - a calibration of how much the height transfer is worth. The
        // gap between two defensible laws over the same extrapolation is a fair lower bound on
        // the uncertainty, and it dwarfs anything in the fitted distributions.
        var hub = new WindSite(HeightMeters: 100.0, RoughnessLengthMeters: 0.1);

        double log = hub.TransferFactorFrom(Anemometer);
        double power = hub.TransferFactorFrom(Anemometer, WindProfile.PowerLaw());

        (Math.Abs(log - power) / log).Should().BeGreaterThan(0.2);
    }

    [Fact]
    public void Wind_speed_rises_with_height_and_falls_with_smoother_reference_terrain()
    {
        var high = new WindSite(HeightMeters: 50.0, RoughnessLengthMeters: 0.3);
        var low = new WindSite(HeightMeters: 20.0, RoughnessLengthMeters: 0.3);

        high.TransferFactorFrom(Anemometer).Should().BeGreaterThan(low.TransferFactorFrom(Anemometer));
        high.TransferFactorFrom(Anemometer).Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void A_height_at_or_below_the_roughness_length_is_refused()
    {
        // The guard that matters: at z = z0 the logarithm is zero, and below it the factor goes
        // negative and the model returns a negative wind speed with no other sign of trouble.
        Action atRoughness = () => new WindSite(HeightMeters: 0.4, RoughnessLengthMeters: 0.4);
        atRoughness.Should().Throw<ArgumentOutOfRangeException>();

        Action belowRoughness = () => new WindSite(HeightMeters: 0.2, RoughnessLengthMeters: 0.4);
        belowRoughness
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("*exceed the roughness length*");

        Action noRoughness = () => new WindSite(HeightMeters: 10.0, RoughnessLengthMeters: 0.0);
        noRoughness.Should().Throw<ArgumentOutOfRangeException>();

        Action noHeight = () => new WindSite(HeightMeters: -1.0, RoughnessLengthMeters: 0.3);
        noHeight.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_negative_shear_exponent_is_refused()
    {
        Action negative = () => WindProfile.PowerLaw(-0.1);
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }
}

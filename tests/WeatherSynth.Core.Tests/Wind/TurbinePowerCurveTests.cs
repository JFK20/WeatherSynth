using FluentAssertions;
using Xunit;

namespace WeatherSynth.Core.Tests;

/// <summary>
/// The power curve itself - four regions and the boundaries between them, which is where every
/// error in this shape hides.
/// </summary>
public class TurbinePowerCurveTests
{
    private static TurbinePowerCurve Idealised() =>
        TurbinePowerCurve.Idealised(3.0, 12.5, 25.0, 2000.0);

    private static TurbinePowerCurve Tabulated() =>
        TurbinePowerCurve.FromTable(
            new[] { (3.0, 0.0), (5.0, 200.0), (10.0, 1200.0), (12.0, 2000.0), (25.0, 2000.0) }
        );

    [Fact]
    public void Produces_nothing_outside_the_working_range()
    {
        var curve = Idealised();

        curve.PowerKilowatts(0.0).Should().Be(0.0);
        curve.PowerKilowatts(2.99).Should().Be(0.0);
        curve.PowerKilowatts(25.01).Should().Be(0.0);
        curve.PowerKilowatts(100.0).Should().Be(0.0);
        curve.PowerKilowatts(double.NaN).Should().Be(0.0);
    }

    /// <summary>
    /// The plateau is the region a cube law cannot represent, so it has to be exactly flat and
    /// exactly rated - not "approximately rated near the top".
    /// </summary>
    [Fact]
    public void Holds_rated_power_from_rated_speed_to_cut_out()
    {
        var curve = Idealised();

        curve.PowerKilowatts(12.5).Should().Be(2000.0);
        curve.PowerKilowatts(18.0).Should().Be(2000.0);
        curve.PowerKilowatts(25.0).Should().Be(2000.0);
    }

    /// <summary>
    /// The reason for the <c>(v³ − vin³)/(vr³ − vin³)</c> form over <c>(v/vr)³</c>: the turbine
    /// starts from nothing rather than jumping to a few percent of rated the instant it cuts in.
    /// </summary>
    [Fact]
    public void Is_continuous_at_cut_in()
    {
        var curve = Idealised();

        curve.PowerKilowatts(3.0).Should().Be(0.0);
        curve.PowerKilowatts(3.001).Should().BeGreaterThan(0.0).And.BeLessThan(1.0);
    }

    [Fact]
    public void Rises_monotonically_between_cut_in_and_rated()
    {
        var curve = Idealised();

        double previous = -1.0;
        for (double v = 3.0; v <= 12.5; v += 0.1)
        {
            double power = curve.PowerKilowatts(v);
            power.Should().BeGreaterThan(previous);
            previous = power;
        }
    }

    [Fact]
    public void A_tabulated_curve_reproduces_its_own_points()
    {
        var curve = Tabulated();

        curve.PowerKilowatts(3.0).Should().Be(0.0);
        curve.PowerKilowatts(5.0).Should().Be(200.0);
        curve.PowerKilowatts(10.0).Should().Be(1200.0);
        curve.PowerKilowatts(12.0).Should().Be(2000.0);
    }

    [Fact]
    public void A_tabulated_curve_interpolates_linearly_between_them()
    {
        var curve = Tabulated();

        curve.PowerKilowatts(7.5).Should().BeApproximately(700.0, 1e-9);
        curve.PowerKilowatts(11.0).Should().BeApproximately(1600.0, 1e-9);
    }

    /// <summary>
    /// Cut-in is read off the table rather than taken as an argument, so the curve stays a single
    /// source of truth about the machine it describes.
    /// </summary>
    [Fact]
    public void A_tabulated_curve_reads_its_own_cut_in_and_cut_out()
    {
        var curve = Tabulated();

        curve.CutInMetersPerSecond.Should().Be(3.0);
        curve.CutOutMetersPerSecond.Should().Be(25.0);
        curve.RatedPowerKilowatts.Should().Be(2000.0);
    }

    /// <summary>
    /// Breakpoints exist for the integration, not for description - every kink must be on one, or
    /// a quadrature will straddle it and smear the corner.
    /// </summary>
    [Fact]
    public void Breakpoints_cover_every_kink()
    {
        Idealised().Breakpoints.Should().Equal(3.0, 12.5, 25.0);
        Tabulated().Breakpoints.Should().Equal(3.0, 5.0, 10.0, 12.0, 25.0);
    }

    [Theory]
    [InlineData(0.0, 12.5, 25.0, 2000.0)] // cut-in not positive
    [InlineData(3.0, 3.0, 25.0, 2000.0)] // rated not above cut-in
    [InlineData(3.0, 12.5, 12.5, 2000.0)] // cut-out not above rated
    [InlineData(3.0, 12.5, 25.0, 0.0)] // no rated power
    public void Refuses_to_build_a_curve_that_makes_no_sense(
        double cutIn,
        double rated,
        double cutOut,
        double power
    )
    {
        FluentActions
            .Invoking(() => TurbinePowerCurve.Idealised(cutIn, rated, cutOut, power))
            .Should()
            .Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Refuses_a_table_that_is_not_a_function_of_speed()
    {
        FluentActions
            .Invoking(() =>
                TurbinePowerCurve.FromTable(new[] { (5.0, 100.0), (5.0, 200.0), (9.0, 900.0) })
            )
            .Should()
            .Throw<ArgumentException>();

        FluentActions
            .Invoking(() => TurbinePowerCurve.FromTable(new[] { (5.0, 100.0) }))
            .Should()
            .Throw<ArgumentException>();

        FluentActions
            .Invoking(() => TurbinePowerCurve.FromTable(new[] { (3.0, 0.0), (5.0, -1.0) }))
            .Should()
            .Throw<ArgumentException>();

        FluentActions
            .Invoking(() => TurbinePowerCurve.FromTable(new[] { (3.0, 0.0), (5.0, 0.0) }))
            .Should()
            .Throw<ArgumentException>();
    }
}

using FluentAssertions;
using WeatherSynth.Wind;
using Xunit;

namespace WeatherSynth.Core.Tests;

/// <summary>
/// The integration - the step that turns a daily mean speed into energy, and the one place in the
/// wind half where a plausible-looking answer can be wrong by a third.
/// </summary>
public class TurbineYieldTests
{
    private static TurbinePowerCurve Curve() =>
        TurbinePowerCurve.Idealised(3.0, 12.5, 25.0, 2000.0);

    private static IntradayShapeModel Shape(double factor = 1.25) =>
        IntradayShapeModel.Constant(factor);

    private static double Power(double meanSpeed, double factor = 1.25) =>
        TurbineYield.MeanPowerOverDay(Curve(), Shape(factor), meanSpeed, meanSpeed);

    /// <summary>
    /// The quadrature's normalisation check: a curve producing the same power at every speed must
    /// integrate to exactly that power, whatever the day looked like. A bug in the segment widths,
    /// the density or the CDF branch shows up here and nowhere else so cleanly.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(4.0)]
    [InlineData(15.0)]
    public void A_flat_curve_integrates_to_its_own_value(double meanSpeed)
    {
        // Flat from 0 to 100 m/s, so the whole Weibull sits inside the working range.
        var flat = TurbinePowerCurve.FromTable(new[] { (0.0, 500.0), (100.0, 500.0) });

        TurbineYield
            .MeanPowerOverDay(flat, Shape(), meanSpeed, meanSpeed)
            .Should()
            .BeApproximately(500.0, 1.0);
    }

    /// <summary>
    /// As the within-day spread vanishes the integral must collapse onto the curve evaluated at the
    /// mean - the limit in which the naive estimate is finally correct. That it is <i>only</i>
    /// correct there is the point of the whole class.
    /// </summary>
    [Fact]
    public void Approaches_the_naive_estimate_as_the_day_becomes_steady()
    {
        var curve = Curve();

        // A factor barely above 1 is a day whose wind never varies.
        double steady = TurbineYield.MeanPowerOverDay(
            curve,
            IntradayShapeModel.Constant(1.000001),
            8.0,
            8.0
        );

        steady
            .Should()
            .BeApproximately(curve.PowerKilowatts(8.0), curve.PowerKilowatts(8.0) * 0.02);
    }

    /// <summary>
    /// A day averaging below cut-in still generates, because some of its hours were not. This is
    /// the failure the naive estimate makes most often at a low-wind site, and it is one-directional.
    /// </summary>
    [Fact]
    public void A_day_averaging_below_cut_in_still_generates_something()
    {
        double power = Power(2.0);

        power.Should().BeGreaterThan(0.0);
        Curve().PowerKilowatts(2.0).Should().Be(0.0);
    }

    /// <summary>The mirror error: a day averaging above rated has hours that were not.</summary>
    [Fact]
    public void A_day_averaging_above_rated_generates_less_than_rated()
    {
        double power = Power(14.0);

        power.Should().BeLessThan(2000.0);
        Curve().PowerKilowatts(14.0).Should().Be(2000.0);
    }

    [Fact]
    public void Output_rises_with_the_days_mean_speed_across_the_useful_range()
    {
        double previous = -1.0;

        for (double speed = 1.0; speed <= 15.0; speed += 0.5)
        {
            double power = Power(speed);
            power.Should().BeGreaterThan(previous);
            previous = power;
        }
    }

    /// <summary>
    /// <b>And falls again beyond it</b>, which is not a defect and is worth a test of its own.
    /// Past about 16 m/s an ever larger share of the day's hours sits above cut-out, where the
    /// turbine shuts down to protect itself, so the day's energy declines even as its mean speed
    /// climbs. Storm shutdown is real, and it is a thing the naive estimate cannot represent at all:
    /// evaluating the curve at the mean holds flat at rated all the way to 25 m/s and then drops to
    /// zero off a cliff.
    /// </summary>
    [Fact]
    public void Output_falls_again_once_the_day_starts_hitting_cut_out()
    {
        double peak = Power(16.0);

        peak.Should().BeGreaterThan(Power(20.0));
        Power(20.0).Should().BeGreaterThan(Power(26.0));

        // The naive estimate is blind to all of it.
        Curve().PowerKilowatts(16.0).Should().Be(Curve().PowerKilowatts(20.0));
    }

    [Fact]
    public void Output_never_exceeds_the_nameplate()
    {
        for (double speed = 0.5; speed <= 40.0; speed += 0.5)
            Power(speed).Should().BeInRange(0.0, 2000.0);
    }

    /// <summary>
    /// A gustier day - a higher energy pattern factor - carries more energy at the same mean speed,
    /// while the turbine is in its cubic region. Below cut-in that is the whole reason it generates
    /// at all.
    /// </summary>
    [Fact]
    public void A_gustier_day_yields_more_at_the_same_mean_speed()
    {
        Power(5.0, factor: 1.5).Should().BeGreaterThan(Power(5.0, factor: 1.1));
        Power(2.0, factor: 1.5).Should().BeGreaterThan(Power(2.0, factor: 1.1));
    }

    [Fact]
    public void Aggregates_a_run_of_days()
    {
        var days = new List<SyntheticWindDay>();
        for (int i = 0; i < 365; i++)
        {
            var date = new DateOnly(2024, 1, 1).AddDays(i);
            days.Add(new SyntheticWindDay(date, 5.0, 5.0, 1.25 * 125.0));
        }

        var yield = TurbineYield.Estimate(days, Curve(), Shape());

        yield.Days.Should().HaveCount(365);
        yield.CapacityFactor.Should().BeInRange(0.0, 1.0);
        yield.MeanPowerKilowatts.Should().BeApproximately(Power(5.0), 1e-9);

        // 365 identical days at that power, in MWh.
        yield
            .EnergyMegawattHours.Should()
            .BeApproximately(Power(5.0) * 24.0 * 365.0 / 1000.0, 1e-6);

        yield.FractionOfDaysBelowCutIn.Should().Be(0.0);
        yield.Days[0].EnergyKilowattHours.Should().BeApproximately(Power(5.0) * 24.0, 1e-9);
    }

    /// <summary>
    /// The transfer sets the scale and the reference speed the shape. Muddling them is the error
    /// <see cref="SyntheticWindDay.MeanSpeedAtReference"/> is carried to prevent.
    /// </summary>
    [Fact]
    public void Uses_the_transferred_speed_for_the_scale()
    {
        var atStation = new SyntheticWindDay(new DateOnly(2024, 6, 1), 4.0, 4.0, 80.0);
        var atHub = new SyntheticWindDay(new DateOnly(2024, 6, 1), 4.0, 4.0 * 1.766, 80.0);

        var shape = IntradayShapeModel.Constant(1.25);

        TurbineYield
            .Estimate(new[] { atHub }, Curve(), shape)
            .MeanPowerKilowatts.Should()
            .BeGreaterThan(
                TurbineYield.Estimate(new[] { atStation }, Curve(), shape).MeanPowerKilowatts
            );
    }

    [Fact]
    public void Rejects_a_run_with_no_days_in_it()
    {
        FluentActions
            .Invoking(() =>
                TurbineYield.Estimate(Array.Empty<SyntheticWindDay>(), Curve(), Shape())
            )
            .Should()
            .Throw<ArgumentException>();
    }
}

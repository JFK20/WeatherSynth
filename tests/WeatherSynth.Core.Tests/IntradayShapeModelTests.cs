using FluentAssertions;
using WeatherSynth.Climate;
using WeatherSynth.Wind;
using Xunit;

namespace WeatherSynth.Core.Tests;

/// <summary>
/// The within-day shape, whose whole content is one bijection and one regression.
/// </summary>
public class IntradayShapeModelTests
{
    /// <summary>
    /// A record whose energy pattern factor genuinely depends on speed, which
    /// <see cref="WindFixtures.SeasonalSeries"/> deliberately does not - it applies a flat 1.25 to
    /// every day, so a model fitted from it has no slope to find. That makes it the right fixture
    /// for <see cref="Fits_a_flat_model_to_a_record_with_a_constant_factor"/> and the wrong one for
    /// everything about the speed dependence.
    /// </summary>
    private static List<DailyWindSpeed> SpeedDependentSeries(
        double intercept = 0.40,
        double slope = -0.13,
        int count = 4000
    )
    {
        var series = new List<DailyWindSpeed>(count);
        var random = new Random(2024);

        for (int i = 0; i < count; i++)
        {
            double speed = 0.5 + 9.5 * random.NextDouble();
            double factor = Math.Exp(intercept + slope * Math.Log(speed));

            series.Add(
                new DailyWindSpeed(
                    new DateOnly(2010, 1, 1).AddDays(i),
                    speed,
                    factor * speed * speed * speed
                )
            );
        }

        return series;
    }

    /// <summary>
    /// The claim the class rests on: <c>EPF(k) = Gamma(1+3/k)/Gamma(1+1/k)³</c> is invertible, so
    /// the energy pattern factor and the within-day shape parameter are the same information.
    /// </summary>
    [Theory]
    [InlineData(1.08)]
    [InlineData(1.251)]
    [InlineData(1.305)]
    [InlineData(1.58)]
    [InlineData(2.5)]
    public void The_shape_and_the_factor_round_trip(double factor)
    {
        double shape = IntradayShapeModel.ShapeFromEnergyPatternFactor(factor);

        IntradayShapeModel.FactorFromShape(shape).Should().BeApproximately(factor, 1e-9);
    }

    [Fact]
    public void The_factor_falls_monotonically_as_the_shape_rises()
    {
        double previous = double.MaxValue;

        for (double k = 1.1; k <= 20.0; k += 0.1)
        {
            double factor = IntradayShapeModel.FactorFromShape(k);
            factor.Should().BeLessThan(previous);
            previous = factor;
        }
    }

    /// <summary>
    /// The station's median factor, and the ordering that says the model is the right way up: a
    /// single day is steadier than a season, which is steadier than a year.
    /// </summary>
    [Fact]
    public void The_stations_median_factor_implies_a_plausible_within_day_shape()
    {
        double shape = IntradayShapeModel.ShapeFromEnergyPatternFactor(1.251);

        shape.Should().BeApproximately(3.86, 0.05);

        // Above the 2.71 fitted on daily means across the year and the 2.14 on hourly values.
        shape.Should().BeGreaterThan(2.71);
    }

    /// <summary>
    /// A factor at or below 1 is physically impossible - Jensen's inequality puts mean(v³) above
    /// mean(v)³ for any day whose wind varies. The bracket has no root there, so it caps rather
    /// than extrapolating into nonsense.
    /// </summary>
    [Fact]
    public void Caps_rather_than_extrapolating_outside_what_a_shape_can_express()
    {
        IntradayShapeModel
            .ShapeFromEnergyPatternFactor(1.0)
            .Should()
            .Be(IntradayShapeModel.MaximumShape);

        IntradayShapeModel
            .ShapeFromEnergyPatternFactor(100.0)
            .Should()
            .Be(IntradayShapeModel.MinimumShape);

        IntradayShapeModel
            .ShapeFromEnergyPatternFactor(double.NaN)
            .Should()
            .Be(IntradayShapeModel.MaximumShape);
    }

    [Fact]
    public void A_constant_model_returns_its_factor_at_every_speed()
    {
        var model = IntradayShapeModel.Constant(1.305);

        foreach (double speed in new[] { 1.0, 3.0, 8.0, 20.0 })
            model.EnergyPatternFactorFor(speed).Should().BeApproximately(1.305, 1e-12);

        model.SampleCount.Should().Be(0);
    }

    [Fact]
    public void A_constant_model_refuses_a_factor_below_one()
    {
        FluentActions
            .Invoking(() => IntradayShapeModel.Constant(0.9))
            .Should()
            .Throw<ArgumentOutOfRangeException>();

        FluentActions
            .Invoking(() => IntradayShapeModel.Constant(1.0))
            .Should()
            .Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Recovers a known power law from a record built to carry one.</summary>
    [Fact]
    public void Recovers_a_speed_dependent_factor()
    {
        var model = IntradayShapeModel.Fit(SpeedDependentSeries());

        model
            .EnergyPatternFactorFor(2.0)
            .Should()
            .BeApproximately(Math.Exp(0.40 - 0.13 * Math.Log(2.0)), 1e-6);
        model
            .EnergyPatternFactorFor(8.0)
            .Should()
            .BeApproximately(Math.Exp(0.40 - 0.13 * Math.Log(8.0)), 1e-6);
        model.SampleCount.Should().Be(4000);
    }

    /// <summary>
    /// The converse, and worth its own test: a record with no speed dependence must not have one
    /// invented for it. <see cref="WindFixtures.SeasonalSeries"/> applies a flat 1.25 to every day.
    /// </summary>
    [Fact]
    public void Fits_a_flat_model_to_a_record_with_a_constant_factor()
    {
        var model = IntradayShapeModel.Fit(WindFixtures.SeasonalSeries());

        model.PooledEnergyPatternFactor.Should().BeApproximately(1.25, 1e-9);

        foreach (double speed in new[] { 1.5, 3.0, 6.0, 9.0 })
            model.EnergyPatternFactorFor(speed).Should().BeApproximately(1.25, 1e-9);
    }

    /// <summary>Calm days are gustier than windy ones, so the shape must rise with speed.</summary>
    [Fact]
    public void Fits_a_steadier_shape_to_windier_days()
    {
        var model = IntradayShapeModel.Fit(SpeedDependentSeries());

        model.ShapeFor(6.0).Should().BeGreaterThan(model.ShapeFor(2.0));
    }

    /// <summary>
    /// The distribution's mean has to land on the day's mean speed, or every yield downstream is
    /// scaled wrong - reading the Weibull scale as the mean is the classic error in this field.
    /// </summary>
    [Theory]
    [InlineData(2.0, 2.0)]
    [InlineData(4.0, 7.0)]
    [InlineData(6.0, 10.6)]
    public void The_distribution_has_the_days_mean_speed_as_its_mean(
        double reference,
        double target
    )
    {
        var model = IntradayShapeModel.Fit(SpeedDependentSeries());

        model.DistributionFor(reference, target).Mean.Should().BeApproximately(target, 1e-9);
    }

    /// <summary>
    /// The shape comes from the speed at the fitting height and the scale from the speed at the
    /// turbine. Passing a hub-height speed for the shape asks what a much windier day looks like
    /// and returns a steadier one than the day really was.
    /// </summary>
    [Fact]
    public void The_shape_follows_the_reference_speed_and_the_scale_the_target()
    {
        var model = IntradayShapeModel.Fit(SpeedDependentSeries());

        var correct = model.DistributionFor(3.0, 3.0 * 1.766);
        var muddled = model.DistributionFor(3.0 * 1.766, 3.0 * 1.766);

        correct.Mean.Should().BeApproximately(muddled.Mean, 1e-9);
        muddled.Shape.Should().BeGreaterThan(correct.Shape);
    }

    [Fact]
    public void Rejects_a_series_carrying_no_usable_days()
    {
        FluentActions
            .Invoking(() => IntradayShapeModel.Fit(new List<DailyWindSpeed>()))
            .Should()
            .Throw<ArgumentException>();
    }
}

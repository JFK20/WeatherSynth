using FluentAssertions;
using WeatherSynth.Data;
using WeatherSynth.Wind;
using Xunit;

namespace WeatherSynth.Core.Tests;

/// <summary>
/// The power curve against the real record, where a ground truth exists.
///
/// <para>This is the rare modelling step in this library that can be checked rather than argued:
/// the DWD record is hourly, so the true energy in each day is computable by summing the curve over
/// its 24 hours. Everything here compares an estimator built on a <i>daily mean</i> against that.</para>
///
/// <para><b>Data-aware</b>, following <see cref="EssenWindRecordTests"/>: with
/// <c>data/dwd_essen_wind.csv</c> absent every test passes silently.</para>
/// </summary>
public class TurbineYieldRecordTests
{
    private static readonly IReadOnlyList<DwdWindDay>? Days = LoadDays();

    private static IReadOnlyList<DwdWindDay>? LoadDays()
    {
        string? path = RepositoryData.TryLocateEssenWind();
        return path is null ? null : DwdWindDayAggregator.ToDays(DwdWindReader.Read(path));
    }

    private static List<DwdWindDay> Complete() =>
        Days!.Where(d => d.IsComplete && d.MeanSpeed > 0.0).ToList();

    private static readonly TurbinePowerCurve Curve = ReferenceTurbines.Generic2Mw;

    /// <summary>Mean power over a day, summed hour by hour. The truth every estimate is scored against.</summary>
    private static double TrueMeanPower(DwdWindDay day, double transferFactor)
    {
        double sum = 0.0;
        int counted = 0;

        foreach (var hour in day.Hours)
        {
            if (hour.SpeedMetersPerSecond is not { } speed)
                continue;

            sum += Curve.PowerKilowatts(speed * transferFactor);
            counted++;
        }

        return counted > 0 ? sum / counted : 0.0;
    }

    private static (double Truth, double Estimated) CapacityFactors(
        double transferFactor,
        IntradayShapeModel shape
    )
    {
        var days = Complete();

        double truth = days.Average(d => TrueMeanPower(d, transferFactor));
        double estimated = days.Average(d =>
            TurbineYield.MeanPowerOverDay(Curve, shape, d.MeanSpeed, d.MeanSpeed * transferFactor)
        );

        return (truth / Curve.RatedPowerKilowatts, estimated / Curve.RatedPowerKilowatts);
    }

    private static IntradayShapeModel FittedShape() =>
        IntradayShapeModel.Fit(WindSpeedSeriesBuilder.Build(Days!));

    /// <summary>
    /// The acceptance number for the whole phase: integrating the curve over a fitted within-day
    /// distribution reproduces the hourly truth to within 2%, at both heights.
    /// </summary>
    [Theory]
    [InlineData(1.0)] // the 15 m anemometer
    [InlineData(1.766)] // a 100 m hub under the log law
    public void The_fitted_shape_reproduces_the_hourly_truth(double transferFactor)
    {
        if (Days is null)
            return;

        var (truth, estimated) = CapacityFactors(transferFactor, FittedShape());

        estimated.Should().BeApproximately(truth, truth * 0.02);
    }

    /// <summary>
    /// The measured capacity factors themselves, so a drift in the reader, the aggregation or the
    /// curve shows up as a number rather than as a ratio that still happens to hold.
    /// </summary>
    [Fact]
    public void The_record_produces_the_expected_capacity_factors()
    {
        if (Days is null)
            return;

        CapacityFactors(1.0, FittedShape()).Truth.Should().BeApproximately(0.0227, 0.0005);
        CapacityFactors(1.766, FittedShape()).Truth.Should().BeApproximately(0.1519, 0.0010);
    }

    /// <summary>
    /// The estimate this replaces, kept as a test so the size of the error is a fact rather than a
    /// claim in a comment. If this ever comes out close to truth, the integration has quietly
    /// collapsed into a point evaluation.
    /// </summary>
    [Fact]
    public void Evaluating_the_curve_at_the_daily_mean_is_wrong_by_a_third()
    {
        if (Days is null)
            return;

        var days = Complete();

        double truth = days.Average(d => TrueMeanPower(d, 1.0));
        double naive = days.Average(d => Curve.PowerKilowatts(d.MeanSpeed));

        (naive / truth - 1.0).Should().BeApproximately(-0.30, 0.02);
    }

    /// <summary>
    /// Why the shape is fitted against speed rather than taken as the record's single mean factor.
    /// The constant is what <c>WindSpeedModel.MeanEnergyPatternFactor</c> supplies, and at the
    /// fitting height it biases the estimate by roughly +9%.
    /// </summary>
    [Fact]
    public void A_single_constant_factor_biases_the_estimate_at_the_fitting_height()
    {
        if (Days is null)
            return;

        var series = WindSpeedSeriesBuilder.Build(Days);
        var constant = IntradayShapeModel.Constant(
            series.Select(d => d.EnergyPatternFactor).Where(f => f > 1.0).Average()
        );

        var (truth, withConstant) = CapacityFactors(1.0, constant);
        var (_, withFitted) = CapacityFactors(1.0, FittedShape());

        (withConstant / truth - 1.0).Should().BeApproximately(0.088, 0.02);
        Math.Abs(withFitted / truth - 1.0)
            .Should()
            .BeLessThan(Math.Abs(withConstant / truth - 1.0));
    }

    /// <summary>
    /// The landmine: the profile disagreement, amplified by the cube into a factor of 2.5 in energy.
    /// </summary>
    [Fact]
    public void The_profile_disagreement_is_far_larger_in_energy_than_in_speed()
    {
        if (Days is null)
            return;

        var reference = DwdWindStations.EssenBredeney.ToSite();
        var hub = ReferenceTurbines.HundredMetreHub;

        double log = hub.TransferFactorFrom(reference, WindProfile.LogLaw);
        double power = hub.TransferFactorFrom(reference, WindProfile.PowerLaw());

        var days = Complete();
        double energyRatio =
            days.Average(d => TrueMeanPower(d, log)) / days.Average(d => TrueMeanPower(d, power));

        (log / power).Should().BeApproximately(1.35, 0.02);
        energyRatio.Should().BeApproximately(2.50, 0.05);
    }

    /// <summary>
    /// A synthetic year is one realisation and a cubic curve amplifies its spread, so the
    /// acceptance check on yield belongs on an ensemble - not on a single seed, exactly as
    /// knowledge.md §14 says of the annual mean speed.
    /// </summary>
    [Fact]
    public void Synthetic_yield_matches_the_record_on_average_over_seeds()
    {
        if (Days is null)
            return;

        var provider = SyntheticWindProvider.FromStationDays(Days, DwdWindStations.EssenBredeney);

        double truth = CapacityFactors(1.766, FittedShape()).Truth;

        var factors = new List<double>();
        for (int seed = 0; seed < 60; seed++)
        {
            factors.Add(
                provider
                    .EstimateYield(2024, seed, Curve, ReferenceTurbines.HundredMetreHub)
                    .CapacityFactor
            );
        }

        factors.Average().Should().BeApproximately(truth, truth * 0.10);
    }
}

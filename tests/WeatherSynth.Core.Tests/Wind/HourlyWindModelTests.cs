using FluentAssertions;
using WeatherSynth.Data;
using WeatherSynth.Wind;
using Xunit;

namespace WeatherSynth.Core.Tests;

public class HourlyWindModelTests
{
    /// <summary>
    /// A known diurnal pattern: a single afternoon bump, standardised.
    /// </summary>
    private static double[] AfternoonPeak()
    {
        var pattern = Enumerable.Range(0, 24).Select(h => Math.Cos(2.0 * Math.PI * (h - 13) / 24.0)).ToArray();
        HourlyWindModel.Standardise(pattern);
        return pattern;
    }

    private static HourlyWindModel Known(double persistence, double weight)
    {
        var pattern = AfternoonPeak();

        return HourlyWindModel.FromCoefficients(
            persistence,
            Enumerable.Repeat(weight, 12).ToArray(),
            Enumerable.Range(0, 12).SelectMany(_ => pattern).ToArray(),
            0
        );
    }

    /// <summary>Hours generated from a known model, in the shape <see cref="HourlyWindModel.Fit"/> reads.</summary>
    private static List<(int, IReadOnlyList<double>)> Generate(HourlyWindModel model, int years)
    {
        var generator = new HourlyWindGenerator(model, IntradayShapeModel.Constant(1.25));
        var random = new Random(7);
        var daily = new Random(8);
        var days = new List<(int, IReadOnlyList<double>)>();

        for (var date = new DateOnly(2001, 1, 1); date < new DateOnly(2001 + years, 1, 1); date = date.AddDays(1))
        {
            double mean = 2.0 + 4.0 * daily.NextDouble();
            var speeds = new double[24];

            generator.FillDay(
                new SyntheticWindDay(date, mean, mean, double.NaN),
                new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                random,
                speeds
            );

            days.Add((date.Month, speeds));
        }

        return days;
    }

    [Fact]
    public void Fit_recovers_a_known_persistence_and_diurnal_cycle()
    {
        var fitted = HourlyWindModel.Fit(Generate(Known(0.85, 0.45), years: 8));

        fitted.Persistence.Should().BeApproximately(0.85, 0.03);

        for (int month = 1; month <= 12; month++)
        {
            fitted.DiurnalWeight(month).Should().BeApproximately(0.45, 0.07);
            fitted.DiurnalPattern(month, 13).Should().BeGreaterThan(1.0);
            fitted.DiurnalPattern(month, 1).Should().BeLessThan(-1.0);
        }
    }

    [Fact]
    public void Fit_finds_no_cycle_where_there_is_none()
    {
        var fitted = HourlyWindModel.Fit(Generate(Known(0.6, 0.0), years: 4));

        fitted.Persistence.Should().BeApproximately(0.6, 0.04);
        for (int month = 1; month <= 12; month++)
            fitted.DiurnalWeight(month).Should().BeLessThan(0.12);
    }

    [Fact]
    public void Calibration_undoes_the_bias_of_measuring_standardised_days()
    {
        // Both numbers come out of the measurement well below what generated them: a day's own
        // mean soaks up much of a persistent latent, and ranking each day damps the cycle. The
        // calibration is the inverse of that measurement.
        var pattern = AfternoonPeak();
        var weights = Enumerable.Repeat(0.4, 12).ToArray();
        var patterns = Enumerable.Range(0, 12).SelectMany(_ => pattern).ToArray();

        var measured = HourlyWindModel.Simulate(0.9, weights, patterns);

        measured.Lag1.Should().BeLessThan(0.85);
        measured.Weights.Should().OnlyContain(w => w < 0.37);

        var (persistence, recovered) = HourlyWindModel.Calibrate(measured);

        // Not exact: the calibration simulates with the measured pattern shape, which carries
        // the measurement's own sampling noise. See HourlyWindModel.Calibrate.
        persistence.Should().BeApproximately(0.9, 0.005);
        recovered.Should().OnlyContain(w => Math.Abs(w - 0.4) < 0.015);
    }

    [Fact]
    public void Scores_share_their_rank_on_ties_and_refuse_a_constant_day()
    {
        var speeds = Enumerable.Range(0, 24).Select(h => h < 2 ? 3.0 : h).ToArray();
        var scores = new double[24];

        HourlyWindModel.StandardisedScores(speeds, scores).Should().BeTrue();
        scores[0].Should().Be(scores[1]);
        scores.Average().Should().BeApproximately(0.0, 1e-12);

        HourlyWindModel.StandardisedScores(Enumerable.Repeat(4.0, 24).ToArray(), scores).Should().BeFalse();
    }

    [Fact]
    public void A_calm_day_is_calm_every_hour()
    {
        var generator = new HourlyWindGenerator(Known(0.8, 0.3), IntradayShapeModel.Constant(1.25));
        var speeds = new double[24];
        var date = new DateOnly(2025, 1, 1);

        generator.FillDay(
            new SyntheticWindDay(date, 0.0, 0.0, 0.0),
            new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new Random(1),
            speeds
        );

        speeds.Should().OnlyContain(v => v == 0.0);
    }

    [Fact]
    public void Coefficients_out_of_range_are_refused()
    {
        var pattern = new double[12 * 24];

        Action weight = () => HourlyWindModel.FromCoefficients(0.8, Enumerable.Repeat(0.99, 12).ToArray(), pattern, 0);
        Action persistence = () => HourlyWindModel.FromCoefficients(1.0, new double[12], pattern, 0);
        Action length = () => HourlyWindModel.FromCoefficients(0.8, new double[11], pattern, 0);

        weight.Should().Throw<ArgumentOutOfRangeException>();
        persistence.Should().Throw<ArgumentOutOfRangeException>();
        length.Should().Throw<ArgumentException>();
    }

    /// <summary>The record itself: data-aware, like <see cref="EssenWindRecordTests"/>.</summary>
    [Fact]
    public void The_Essen_record_has_a_summer_afternoon_cycle()
    {
        string? path = RepositoryData.TryLocateEssenWind();
        if (path is null)
            return;

        var days = DwdWindDayAggregator.ToDays(DwdWindReader.Read(path));
        var fitted = HourlyWindModel.Fit(WindSpeedSeriesBuilder.BuildHourly(days));

        fitted.SampleCount.Should().BeGreaterThan(6000);
        fitted.Persistence.Should().BeInRange(0.75, 0.95);

        fitted.DiurnalWeight(7).Should().BeGreaterThan(fitted.DiurnalWeight(12));
        Enumerable.Range(0, 24).MaxBy(h => fitted.DiurnalPattern(7, h)).Should().BeInRange(10, 15);
    }
}

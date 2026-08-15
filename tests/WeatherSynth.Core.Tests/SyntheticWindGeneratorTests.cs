using FluentAssertions;
using WeatherSynth.Climate;
using WeatherSynth.Wind;
using Xunit;

namespace WeatherSynth.Core.Tests;

public class SyntheticWindGeneratorTests
{
    private static WindSpeedModel Model => WindFixtures.SeasonalModel;

    private static readonly WindSite Anemometer = new(HeightMeters: 15.0, RoughnessLengthMeters: 0.3);

    [Fact]
    public void A_year_is_reproducible_from_its_seed()
    {
        var first = new SyntheticWindGenerator(Model).GenerateYear(2026, seed: 42);
        var second = new SyntheticWindGenerator(Model).GenerateYear(2026, seed: 42);

        second.Days.Should().Equal(first.Days);
        second.MeanSpeed.Should().Be(first.MeanSpeed);
        second.Seed.Should().Be(42);
    }

    [Fact]
    public void A_different_seed_is_a_different_but_equally_plausible_year()
    {
        var first = new SyntheticWindGenerator(Model).GenerateYear(2026, seed: 42);
        var second = new SyntheticWindGenerator(Model).GenerateYear(2026, seed: 43);

        second.Days.Should().NotEqual(first.Days);
        second.MeanSpeed.Should().BeApproximately(first.MeanSpeed, 0.5);
    }

    [Fact]
    public void A_year_covers_the_calendar_including_the_leap_day()
    {
        // 29 February is a real day with real wind; dropping it would put a one-day hole in the
        // persistence chain for no gain.
        new SyntheticWindGenerator(Model).GenerateYear(2026, 1).Days.Should().HaveCount(365);
        new SyntheticWindGenerator(Model).GenerateYear(2024, 1).Days.Should().HaveCount(366);
    }

    [Fact]
    public void Generate_resets_so_a_run_depends_only_on_its_seed()
    {
        var generator = new SyntheticWindGenerator(Model);
        var start = new DateOnly(2026, 1, 1);
        var end = new DateOnly(2026, 3, 31);

        var first = generator.Generate(start, end, new Random(7)).ToList();

        // Drive the generator somewhere else entirely, then ask for the same run again.
        generator.Generate(start, end, new Random(999)).ToList();
        var second = generator.Generate(start, end, new Random(7)).ToList();

        second.Should().Equal(first);
    }

    [Fact]
    public void The_generated_run_carries_the_chains_persistence_through_to_the_speeds()
    {
        // Two runs differing in nothing but phi. Stated explicitly rather than taken from the
        // model, because the fixture record is drawn independently and so fits phi at essentially
        // zero - there would be nothing to see.
        double Lag1Of(double persistence) =>
            SeriesStatistics.Lag1Autocorrelation(
                new SyntheticWindGenerator(
                    new LatentAr1Chain(Model, persistence),
                    transferFactor: 1.0,
                    Model.MeanEnergyPatternFactor
                )
                    .Generate(new DateOnly(2000, 1, 1), new DateOnly(2060, 12, 31), new Random(3))
                    .Select(d => (d.Date, d.MeanSpeed))
            );

        double independent = Lag1Of(0.0);
        double persistent = Lag1Of(0.6);

        // Not zero even with no memory: consecutive days share a monthly marginal, and the fixture
        // has a real seasonal swing. That floor is what the chain adds to rather than replaces.
        independent.Should().BeInRange(0.0, 0.2);

        // Near phi but not equal to it, and the two reasons pull opposite ways: the seasonal floor
        // above adds to the raw figure, while the quantile transform back into speeds shrinks what
        // the copula preserved. Here the season wins and the raw lag-1 lands just above 0.6 - the
        // same relationship the record shows, where a raw 0.529 comes from a fitted phi of 0.444.
        persistent.Should().BeInRange(0.5, 0.75);
        persistent.Should().BeGreaterThan(independent + 0.3);
    }

    [Fact]
    public void The_transfer_scales_every_day_and_leaves_the_reference_speed_visible()
    {
        var hub = new WindSite(HeightMeters: 100.0, RoughnessLengthMeters: 0.1);
        double factor = hub.TransferFactorFrom(Anemometer);

        var year = new SyntheticWindGenerator(Model, factor).GenerateYear(2026, seed: 42);

        year.Days.Should()
            .OnlyContain(d => d.MeanSpeed > d.MeanSpeedAtReference)
            .And.OnlyContain(d => Math.Abs(d.MeanSpeed - d.MeanSpeedAtReference * factor) < 1e-12);
    }

    [Fact]
    public void Transferring_the_parameters_and_scaling_the_draws_are_the_same_thing()
    {
        // The identity that licenses applying the factor per draw instead of to the fitted
        // parameters: scaling every speed by c maps Weibull(k, A, gamma) to Weibull(k, cA, c*gamma)
        // with k invariant, and sampling is a monotone transform of one uniform - so the two agree
        // exactly, not approximately.
        const double factor = 1.766;

        var atReference = new SyntheticWindGenerator(Model).GenerateYear(2026, seed: 42);
        var transferred = new SyntheticWindGenerator(Model, factor).GenerateYear(2026, seed: 42);

        for (int i = 0; i < atReference.Days.Count; i++)
        {
            double scaledDraw = atReference.Days[i].MeanSpeed * factor;
            transferred.Days[i].MeanSpeed.Should().BeApproximately(scaledDraw, 1e-12);
        }

        // And the same claim stated on the distribution itself, which is where it comes from.
        var july = Model.ForMonth(7);
        var scaled = july.Scaled(factor);

        foreach (double p in new[] { 0.01, 0.1, 0.5, 0.9, 0.99 })
            scaled.Quantile(p).Should().BeApproximately(july.Quantile(p) * factor, 1e-9);
    }

    [Fact]
    public void The_energy_proxy_applies_the_records_pattern_factor()
    {
        var year = new SyntheticWindGenerator(Model).GenerateYear(2026, seed: 42);

        // Constant per day by construction - it is the record's mean factor, not a draw.
        year.Days.Should()
            .OnlyContain(d =>
                Math.Abs(d.EnergyPatternFactor - Model.MeanEnergyPatternFactor) < 1e-9
            );

        // The year's own factor is larger, because it carries the day-to-day spread on top of the
        // within-day spread the per-day factor describes.
        year.EnergyPatternFactor.Should().BeGreaterThan(Model.MeanEnergyPatternFactor);
    }

    [Fact]
    public void Rejects_a_transfer_factor_or_pattern_factor_that_is_not_physical()
    {
        Action negativeTransfer = () => new SyntheticWindGenerator(Model, transferFactor: -1.0);
        negativeTransfer.Should().Throw<ArgumentOutOfRangeException>();

        // mean(v³) >= mean(v)³ always, with equality only for a perfectly steady day.
        Action belowUnity = () =>
            new SyntheticWindGenerator(new LatentAr1Chain(Model), 1.0, energyPatternFactor: 0.9);
        belowUnity.Should().Throw<ArgumentOutOfRangeException>();

        Action backwards = () =>
            new SyntheticWindGenerator(Model)
                .Generate(new DateOnly(2026, 3, 1), new DateOnly(2026, 1, 1), new Random(1));
        backwards.Should().Throw<ArgumentException>();
    }
}

using FluentAssertions;
using WeatherSynth.Climate;
using WeatherSynth.Statistics;
using Xunit;

namespace WeatherSynth.Core.Tests;

/// <summary>
/// The coupling fit, tested without either record in the way. What it has to get right is a
/// property of paired series alone: a known dependence must come back, an absent one must not be
/// invented, and a month too thin to speak for itself must fall back rather than report noise.
/// </summary>
public class MonthlyCouplingTests
{
    private static ClearSkyIndexModel SolarModel() => ClimateFixtures.SeasonalModel;

    private static WindSpeedModel WindModel() => WindFixtures.SeasonalModel;

    /// <summary>
    /// A paired record with a known latent correlation and no persistence.
    ///
    /// <para>Built by drawing correlated normals and pushing each through the fixture models' own
    /// quantiles, so the dependence is exactly <paramref name="rho"/> in the space the fit measures
    /// in - and the recovery is limited by sampling noise alone rather than by how well the
    /// fixture models describe their own fixture data.</para>
    /// </summary>
    private static List<(DateOnly Date, double A, double B)> Paired(
        double rho,
        int days = 6000,
        int seed = 90210
    )
    {
        var random = new Random(seed);
        var solar = SolarModel();
        var wind = WindModel();
        var series = new List<(DateOnly, double, double)>(days);

        var date = new DateOnly(2010, 1, 1);
        for (int i = 0; i < days; i++, date = date.AddDays(1))
        {
            double shared = Gaussian.Sample(random);
            double independent = Gaussian.Sample(random);
            double second = rho * shared + Math.Sqrt(1.0 - rho * rho) * independent;

            series.Add(
                (
                    date,
                    solar.Quantile(Gaussian.Cdf(shared), date.Month),
                    wind.Quantile(Gaussian.Cdf(second), date.Month)
                )
            );
        }

        return series;
    }

    [Theory]
    [InlineData(-0.45)]
    [InlineData(-0.12)]
    [InlineData(0.30)]
    public void Recovers_a_known_coupling(double rho)
    {
        var coupling = MonthlyCoupling.Fit(Paired(rho), SolarModel(), WindModel());

        coupling.Pooled.Should().BeApproximately(rho, 0.03);

        for (int month = 1; month <= 12; month++)
            coupling.ForMonth(month).Should().BeApproximately(rho, 0.10);
    }

    [Fact]
    public void Reports_no_coupling_when_the_two_series_are_independent()
    {
        var coupling = MonthlyCoupling.Fit(Paired(0.0), SolarModel(), WindModel());

        coupling.Pooled.Should().BeApproximately(0.0, 0.03);
    }

    /// <summary>
    /// The sign is the whole diagnostic in this domain - windy days are cloudy days - so it has to
    /// survive the round trip through two differently shaped marginals rather than being an
    /// artefact of which quantity was passed first.
    /// </summary>
    [Fact]
    public void Preserves_the_sign_of_the_dependence()
    {
        MonthlyCoupling.Fit(Paired(-0.4), SolarModel(), WindModel()).Pooled.Should().BeNegative();
        MonthlyCoupling.Fit(Paired(0.4), SolarModel(), WindModel()).Pooled.Should().BePositive();
    }

    [Fact]
    public void Falls_back_to_the_pooled_coefficient_for_a_thin_month()
    {
        // Every January day but a handful removed, so that month alone drops under the threshold.
        var trimmed = Paired(-0.35).Where(p => p.Date.Month != 1 || p.Date.Day <= 2).ToList();

        var coupling = MonthlyCoupling.Fit(trimmed, SolarModel(), WindModel());

        coupling.SampleCount(1).Should().BeLessThan(MonthlyCoupling.MinimumSamplesPerMonth);
        coupling.IsPooled(1).Should().BeTrue();
        coupling.ForMonth(1).Should().Be(coupling.Pooled);

        coupling.IsPooled(7).Should().BeFalse();
    }

    /// <summary>
    /// A month is only fitted on its own days. Verified by giving one month a dependence the rest
    /// of the year does not have, which a pooled-everywhere fit could not reproduce.
    /// </summary>
    [Fact]
    public void Fits_each_month_from_its_own_days()
    {
        var mixed = Paired(0.0).ToList();
        var strong = Paired(-0.6, seed: 5150);

        for (int i = 0; i < mixed.Count; i++)
        {
            if (mixed[i].Date.Month == 7)
                mixed[i] = strong[i];
        }

        var coupling = MonthlyCoupling.Fit(mixed, SolarModel(), WindModel());

        coupling.ForMonth(7).Should().BeApproximately(-0.6, 0.10);
        coupling.ForMonth(1).Should().BeApproximately(0.0, 0.10);
    }

    [Fact]
    public void None_is_twelve_zeroes()
    {
        for (int month = 1; month <= 12; month++)
            MonthlyCoupling.None.ForMonth(month).Should().Be(0.0);

        MonthlyCoupling.None.Pooled.Should().Be(0.0);
    }

    [Fact]
    public void FromValues_round_trips_and_rejects_non_correlations()
    {
        var values = Enumerable.Range(1, 12).Select(m => -0.05 * m).ToList();
        var coupling = MonthlyCoupling.FromValues(values);

        for (int month = 1; month <= 12; month++)
            coupling.ForMonth(month).Should().Be(values[month - 1]);

        var tooLarge = Enumerable.Repeat(1.5, 12).ToList();
        FluentActions
            .Invoking(() => MonthlyCoupling.FromValues(tooLarge))
            .Should()
            .Throw<ArgumentOutOfRangeException>();

        FluentActions
            .Invoking(() => MonthlyCoupling.FromValues(new List<double> { 0.1 }))
            .Should()
            .Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_a_series_with_nothing_usable_in_it()
    {
        FluentActions
            .Invoking(() =>
                MonthlyCoupling.Fit(
                    new List<(DateOnly, double, double)>(),
                    SolarModel(),
                    WindModel()
                )
            )
            .Should()
            .Throw<ArgumentException>();
    }
}

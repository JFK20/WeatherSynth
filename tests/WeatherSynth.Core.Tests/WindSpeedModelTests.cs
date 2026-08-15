using FluentAssertions;
using WeatherSynth.Climate;
using Xunit;

namespace WeatherSynth.Core.Tests;

public class WindSpeedModelTests
{
    // The fixture record lives in WindFixtures, shared with the chain suite so that "the marginal
    // survives persistence" is a claim about the same marginals this suite scores.
    private static Weibull SeasonalShape(DateOnly date) => WindFixtures.SeasonalShape(date);

    private static List<DailyWindSpeed> SeasonalSeries(int years = 15, int seed = 4242) =>
        WindFixtures.SeasonalSeries(years, seed);

    [Fact]
    public void Recovers_the_monthly_distributions_it_was_fitted_from()
    {
        // Thirty years rather than the fixtures' fifteen: this test compares the fit against the
        // distribution the days were drawn from, not against the days themselves, so it carries
        // the sampling error of a month's draws. At 930 days a month that is about 0.05 m/s on
        // the mean, and the tolerance below is two of them.
        var series = SeasonalSeries(years: 30);
        var model = WindSpeedModel.Fit(series);

        for (int month = 1; month <= 12; month++)
        {
            // Against the mean of the source shapes across the month's days, not the shape at
            // mid-month: the cosine moves within a month, and January sits on the seasonal peak.
            double sourceMean = series
                .Where(d => d.Date.Month == month)
                .Average(d => SeasonalShape(d.Date).Mean);

            var fit = model.ForMonth(month);

            fit.Mean.Should().BeApproximately(sourceMean, 0.1);
            fit.Shape.Should().BeApproximately(1.9, 0.35);
        }
    }

    [Fact]
    public void Each_month_fits_its_own_days_rather_than_the_pooled_record()
    {
        var series = SeasonalSeries();
        var model = WindSpeedModel.Fit(series);

        foreach (var group in series.GroupBy(d => d.Date.Month))
        {
            var observed = group.Select(d => d.MeanSpeed).ToList();

            model.ForMonth(group.Key).Mean.Should().BeApproximately(observed.Average(), 0.05);
        }

        // The seasonal swing survives the fit. If it did not, the model would have averaged the
        // year away - which for wind means losing the seasons outright, since nothing upstream
        // carries them.
        (model.ForMonth(1).Mean / model.ForMonth(7).Mean).Should().BeGreaterThan(1.2);
    }

    [Fact]
    public void Every_month_passes_a_goodness_of_fit_test_against_its_own_days()
    {
        var series = SeasonalSeries();
        var model = WindSpeedModel.Fit(series);

        foreach (var group in series.GroupBy(d => d.Date.Month))
        {
            var observed = group.Select(d => d.MeanSpeed).ToList();

            GoodnessOfFit
                .KolmogorovSmirnovDistance(observed, model.ForMonth(group.Key).CumulativeProbability)
                .Should()
                .BeLessThan(
                    GoodnessOfFit.CriticalValueFivePercent(observed.Count),
                    "month {0} must pass a 5% KS test",
                    group.Key
                );
        }
    }

    [Fact]
    public void A_thin_month_falls_back_to_the_pooled_fit()
    {
        // Two years of data with March all but absent: 24 days is under the minimum, so March
        // gets the pooled shape rather than a fit nobody should trust.
        var series = SeasonalSeries(years: 2)
            .Where(d => d.Date.Month != 3 || d.Date.Day <= 12)
            .ToList();

        series.Count(d => d.Date.Month == 3).Should().BeLessThan(
            WindSpeedModel.MinimumSamplesPerMonth
        );

        var model = WindSpeedModel.Fit(series);

        model.ForMonth(3).Should().BeSameAs(model.Pooled);
        model.ForMonth(4).Should().NotBeSameAs(model.Pooled);
    }

    [Fact]
    public void Fits_persistence_on_normal_scores_so_the_season_is_not_counted_twice()
    {
        var series = SeasonalSeries();
        var model = WindSpeedModel.Fit(series);

        // The fixture record has a seasonal cycle and no day-to-day memory, so phi - which is
        // measured with the season transformed out - must come back at essentially zero even
        // though the raw lag-1 does not.
        double raw = SeriesStatistics.Lag1Autocorrelation(
            series.Select(d => (d.Date, d.MeanSpeed))
        );

        raw.Should().BeGreaterThan(0.05);
        model.Persistence.Should().BeLessThan(0.05);
    }

    [Fact]
    public void Persistence_is_never_negative_or_unstable()
    {
        var model = WindSpeedModel.Fit(SeasonalSeries());

        // A latent AR(1) needs |phi| < 1 to have a stationary distribution at all.
        model.Persistence.Should().BeInRange(0.0, 0.99);
    }

    [Fact]
    public void Carries_the_height_and_the_cube_law_correction_alongside_the_shapes()
    {
        var model = WindSpeedModel.Fit(SeasonalSeries(), referenceHeightMeters: 15.0);

        // A and gamma are m/s at a height, and the height that gets assumed otherwise is 10 m.
        model.ReferenceHeightMeters.Should().Be(15.0);

        // The fixture puts every day's mean(v³) at 1.25 times the cube of its mean.
        model.MeanEnergyPatternFactor.Should().BeApproximately(1.25, 1e-9);
    }

    [Fact]
    public void Sampling_a_month_reproduces_that_months_marginal()
    {
        var model = WindSpeedModel.Fit(SeasonalSeries());
        var random = new Random(1303);

        var draws = Enumerable.Range(0, 50_000).Select(_ => model.Sample(1, random)).ToList();

        draws.Average().Should().BeApproximately(model.ForMonth(1).Mean, 0.03);
        draws.Min().Should().BeGreaterThan(model.ForMonth(1).Location);
    }

    [Fact]
    public void Rejects_a_month_outside_the_calendar_and_an_empty_series()
    {
        var model = WindSpeedModel.Fit(SeasonalSeries(years: 2));

        Action month0 = () => model.ForMonth(0);
        month0.Should().Throw<ArgumentOutOfRangeException>();

        Action month13 = () => model.ForMonth(13);
        month13.Should().Throw<ArgumentOutOfRangeException>();

        Action empty = () => WindSpeedModel.Fit(new List<DailyWindSpeed>());
        empty.Should().Throw<ArgumentException>();
    }
}

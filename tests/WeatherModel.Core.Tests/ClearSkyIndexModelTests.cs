using FluentAssertions;
using WeatherModel.Climate;
using Xunit;

namespace WeatherModel.Core.Tests;

public class ScaledBetaTests
{
    [Fact]
    public void Fit_recovers_the_parameters_it_was_sampled_from()
    {
        // Round trip: draw from a known Beta, fit the draws, get the original back. This
        // exercises the sampler and the moment fit against each other, so a sign error or a
        // mis-scaled moment in either one cannot hide.
        var truth = new ScaledBeta(alpha: 4.0, beta: 2.0, scale: 1.25);
        var random = new Random(20260731);

        var draws = Enumerable.Range(0, 200_000).Select(_ => truth.Sample(random)).ToList();
        var fitted = ScaledBeta.FitByMoments(draws, scale: 1.25);

        fitted.Alpha.Should().BeApproximately(truth.Alpha, 0.05);
        fitted.Beta.Should().BeApproximately(truth.Beta, 0.05);
        fitted.SampleCount.Should().Be(200_000);
    }

    [Fact]
    public void Samples_stay_inside_the_support()
    {
        // The structural guarantee the whole design leans on: because the draw is bounded, the
        // generator needs no clamp and can never produce negative irradiance.
        var distribution = new ScaledBeta(alpha: 0.4, beta: 0.6, scale: 1.25);
        var random = new Random(7);

        for (int i = 0; i < 100_000; i++)
        {
            double value = distribution.Sample(random);
            value.Should().BeInRange(0.0, 1.25);
        }
    }

    [Fact]
    public void Density_integrates_to_one_across_the_scaled_support()
    {
        // Catches the easiest error in a rescaled distribution: forgetting the 1/scale Jacobian,
        // which would leave the density integrating to `scale` instead of 1.
        var distribution = new ScaledBeta(alpha: 3.0, beta: 5.0, scale: 1.25);

        const int steps = 100_000;
        double width = 1.25 / steps;
        double integral = 0.0;

        for (int i = 0; i < steps; i++)
            integral += distribution.Density((i + 0.5) * width) * width;

        integral.Should().BeApproximately(1.0, 1e-4);
    }

    [Fact]
    public void Mean_and_variance_match_the_samples_they_describe()
    {
        var distribution = new ScaledBeta(alpha: 2.5, beta: 1.5, scale: 1.25);
        var random = new Random(99);

        var draws = Enumerable.Range(0, 200_000).Select(_ => distribution.Sample(random)).ToList();

        draws.Average().Should().BeApproximately(distribution.Mean, 0.005);
        draws.Sum(d => Math.Pow(d - distribution.Mean, 2)).Should()
            .BeApproximately(distribution.Variance * draws.Count, distribution.Variance * draws.Count * 0.02);
    }

    // Reference values from scipy.stats.beta.cdf. An outside reference rather than this
    // library's own density: the shapes below 1 put a singularity at the origin, and numerical
    // integration of the density is the less accurate of the two there, so a self-consistency
    // check would be testing the quadrature rather than the distribution.
    [Theory]
    // Both branches of the symmetry relation, and a U-shaped case with both shapes below 1.
    [InlineData(4.0, 2.0, 0.10, 0.00046)]
    [InlineData(4.0, 2.0, 0.50, 0.1875)]
    [InlineData(4.0, 2.0, 0.75, 0.6328125)]
    [InlineData(0.5, 0.7, 0.10, 0.255025266685)]
    [InlineData(0.5, 0.7, 0.50, 0.600364232133)]
    [InlineData(0.5, 0.7, 0.90, 0.883788956771)]
    [InlineData(12.0, 3.0, 0.25, 0.0000032112)]
    [InlineData(12.0, 3.0, 0.75, 0.281127624214)]
    [InlineData(12.0, 3.0, 0.90, 0.841640018713)]
    public void Cumulative_probability_matches_an_external_reference(
        double alpha, double beta, double unitPoint, double expected)
    {
        const double scale = 1.25;
        var distribution = new ScaledBeta(alpha, beta, scale);

        distribution.CumulativeProbability(unitPoint * scale)
            .Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Cumulative_probability_is_pinned_at_the_ends_of_the_support()
    {
        var distribution = new ScaledBeta(3.0, 5.0, scale: 1.25);

        distribution.CumulativeProbability(0.0).Should().Be(0.0);
        distribution.CumulativeProbability(-1.0).Should().Be(0.0);
        distribution.CumulativeProbability(1.25).Should().Be(1.0);
        distribution.CumulativeProbability(99.0).Should().Be(1.0);
    }

    [Fact]
    public void Cumulative_probability_agrees_with_the_integral_of_the_density()
    {
        // Where the density is finite, the two are independent routes to the same number and
        // must agree. This is what ties the continued fraction to the rest of the class.
        var distribution = new ScaledBeta(alpha: 4.0, beta: 2.0, scale: 1.25);

        const int steps = 200_000;
        double width = 1.25 / steps;
        double integral = 0.0;

        for (int i = 0; i < steps; i++)
        {
            integral += distribution.Density((i + 0.5) * width) * width;

            if ((i + 1) % 20_000 == 0)
                distribution.CumulativeProbability((i + 1) * width)
                    .Should().BeApproximately(integral, 1e-6);
        }
    }

    [Fact]
    public void Fit_refuses_values_beyond_the_support_rather_than_clipping_them()
    {
        // knowledge.md §11: 5.7% of Bochum days exceed 1.0 and they are real. If a site ever
        // exceeds the configured support, the right answer is a loud failure telling the caller
        // to widen it, not a silent fit to truncated data.
        var values = new[] { 0.2, 0.5, 0.9, 1.4 };

        var fit = () => ScaledBeta.FitByMoments(values, scale: 1.25);

        fit.Should().Throw<ArgumentException>().WithMessage("*outside the support*");
    }

    [Fact]
    public void Fit_refuses_a_sample_no_beta_can_represent()
    {
        // Half at each end is more dispersed than the U-shaped limit of the family. Method of
        // moments would otherwise return a negative alpha and beta, which the constructor
        // rejects anyway, but with a far less informative message.
        var values = Enumerable.Repeat(0.0, 50).Concat(Enumerable.Repeat(1.25, 50));

        var fit = () => ScaledBeta.FitByMoments(values, scale: 1.25);

        fit.Should().Throw<ArgumentException>().WithMessage("*too dispersed*");
    }
}

public class ClearSkyIndexModelTests
{
    /// <summary>
    /// A synthetic record with a deliberate seasonal signal: dark winters, clear summers.
    /// </summary>
    private static List<DailyClearness> SeasonalSeries(int years = 10)
    {
        var random = new Random(4242);
        var series = new List<DailyClearness>();

        for (var date = new DateOnly(2010, 1, 1); date.Year < 2010 + years; date = date.AddDays(1))
        {
            // Peaks in July, troughs in January.
            double seasonal = 0.55 + 0.20 * Math.Cos((date.DayOfYear - 196) / 365.25 * 2.0 * Math.PI);
            var month = new ScaledBeta(seasonal * 8.0, (1.0 - seasonal) * 8.0, 1.25);

            double index = month.Sample(random);
            series.Add(new DailyClearness(date, index * 5000.0, 5000.0, 8000.0));
        }

        return series;
    }

    [Fact]
    public void Monthly_fits_track_the_seasonal_signal_in_the_data()
    {
        var model = ClearSkyIndexModel.Fit(SeasonalSeries());

        // July should sit well clear of January, and the pooled fit between them.
        model.ForMonth(7).Mean.Should().BeGreaterThan(model.ForMonth(1).Mean + 0.2);
        model.Pooled.Mean.Should().BeInRange(model.ForMonth(1).Mean, model.ForMonth(7).Mean);

        for (int month = 1; month <= 12; month++)
            model.ForMonth(month).SampleCount.Should().BeGreaterThan(250);
    }

    [Fact]
    public void Sparse_months_fall_back_to_the_pooled_fit()
    {
        // One year of data with December mostly missing. A 3-sample December fit would be
        // meaningless; borrowing the pooled shape is wrong but honestly wrong.
        var series = SeasonalSeries(years: 1)
            .Where(d => d.Date.Month != 12 || d.Date.Day <= 3)
            .ToList();

        var model = ClearSkyIndexModel.Fit(series);

        model.ForMonth(12).Should().BeSameAs(model.Pooled);
        model.ForMonth(6).Should().NotBeSameAs(model.Pooled);
    }

    [Fact]
    public void Fitted_model_reproduces_the_monthly_means_of_the_data_it_was_fitted_to()
    {
        // The acceptance criterion for the fit itself: sampling the model must give back the
        // seasonal profile of the record. If this drifts, the generator is producing a different
        // climate from the one measured.
        var series = SeasonalSeries();
        var model = ClearSkyIndexModel.Fit(series);
        var random = new Random(1234);

        foreach (var group in series.GroupBy(d => d.Date.Month))
        {
            double observed = group.Average(d => d.ClearSkyIndex);
            double sampled = Enumerable.Range(0, 20_000)
                .Average(_ => model.Sample(group.Key, random));

            sampled.Should().BeApproximately(observed, 0.01);
        }
    }
}

public class IndexSeriesStatisticsTests
{
    [Fact]
    public void Independent_days_show_no_persistence()
    {
        // The baseline the current model sits at, and the reason the Markov chain is the next
        // open item: i.i.d. sampling cannot produce autocorrelation whatever its histogram is.
        var random = new Random(11);
        var start = new DateOnly(2020, 1, 1);

        var series = Enumerable.Range(0, 5000)
            .Select(i => (Date: start.AddDays(i), Index: random.NextDouble()))
            .ToList();

        IndexSeriesStatistics.Lag1Autocorrelation(series).Should().BeApproximately(0.0, 0.05);
    }

    [Fact]
    public void A_persistent_series_reports_high_correlation()
    {
        var random = new Random(12);
        var start = new DateOnly(2020, 1, 1);
        var series = new List<(DateOnly, double)>();

        double value = 0.5;
        for (int i = 0; i < 5000; i++)
        {
            // Strongly autoregressive: today is mostly yesterday.
            value = 0.9 * value + 0.1 * random.NextDouble();
            series.Add((start.AddDays(i), value));
        }

        IndexSeriesStatistics.Lag1Autocorrelation(series).Should().BeGreaterThan(0.8);
    }

    [Fact]
    public void Gaps_in_the_record_are_not_treated_as_consecutive_days()
    {
        // Two isolated blocks, each alternating dark-clear-dark. Pairing across the gap would
        // invent a transition that never happened, which is the error that skews persistence on
        // a record with missing days, and the Bochum record has 7.4% missing rows.
        var series = new List<(DateOnly, double)>
        {
            (new DateOnly(2020, 1, 1), 0.2),
            (new DateOnly(2020, 1, 2), 0.8),
            (new DateOnly(2020, 1, 3), 0.2),
            (new DateOnly(2020, 6, 1), 0.2),
            (new DateOnly(2020, 6, 2), 0.8),
            (new DateOnly(2020, 6, 3), 0.2),
        };

        // The four genuine pairs alternate perfectly, so the correlation is exactly -1.
        // Admitting the cross-gap pair (2020-01-03 to 2020-06-01, both 0.2) would drag it to
        // -0.667, so this pins the gap handling rather than merely sampling near it.
        IndexSeriesStatistics.Lag1Autocorrelation(series).Should().BeApproximately(-1.0, 1e-9);
    }
}

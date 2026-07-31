using FluentAssertions;
using WeatherModel.Climate;
using Xunit;

namespace WeatherModel.Core.Tests;

/// <summary>
/// The persistence layer, tested without a solar calculator in the way. Everything the chain has
/// to get right is a property of the index sequence alone: the marginal must survive untouched,
/// the correlation must appear, and gaps must break it (knowledge.md §11-12).
/// </summary>
public class ClearSkyIndexChainTests
{
    private const double Scale = 1.25;

    /// <summary>
    /// A model with a deliberate seasonal swing, fitted from a record rather than constructed,
    /// because <see cref="ClearSkyIndexModel"/> has no public constructor.
    /// </summary>
    private static ClearSkyIndexModel SeasonalModel()
    {
        var random = new Random(4242);
        var series = new List<DailyClearness>();

        for (var date = new DateOnly(2010, 1, 1); date.Year < 2020; date = date.AddDays(1))
        {
            double seasonal = 0.55 + 0.20 * Math.Cos((date.DayOfYear - 196) / 365.25 * 2.0 * Math.PI);
            double index = new ScaledBeta(seasonal * 8.0, (1.0 - seasonal) * 8.0, Scale).Sample(random);
            series.Add(new DailyClearness(date, index * 5000.0, 5000.0, 8000.0));
        }

        return ClearSkyIndexModel.Fit(series);
    }

    private static List<(DateOnly Date, double Index)> Run(
        ClearSkyIndexChain chain, DateOnly start, int days, Random random)
    {
        var result = new List<(DateOnly, double)>(days);
        for (int i = 0; i < days; i++)
        {
            var date = start.AddDays(i);
            result.Add((date, chain.Next(date, random)));
        }

        return result;
    }

    /// <summary>
    /// Lag-1 correlation measured inside July and nowhere else: many independent 28-day runs,
    /// pairs pooled across them.
    ///
    /// <para>Running the chain over consecutive years instead would measure something different
    /// and larger, because the seasonal cycle correlates neighbouring days on its own - that is
    /// the 0.137 the independent model already produced, and mixing it in here would stop this
    /// being a test of the chain. Holding the month fixed holds the marginal fixed with it.</para>
    /// </summary>
    private static double WithinJulyLag1(ClearSkyIndexChain chain, int runs, Random random)
    {
        var start = new DateOnly(2000, 7, 1);
        var yesterday = new List<double>();
        var today = new List<double>();

        for (int run = 0; run < runs; run++)
        {
            chain.Reset();
            double previous = chain.Next(start, random);

            for (int day = 1; day < 28; day++)
            {
                double current = chain.Next(start.AddDays(day), random);
                yesterday.Add(previous);
                today.Add(current);
                previous = current;
            }
        }

        return Correlation(yesterday, today);
    }

    [Fact]
    public void The_marginal_survives_the_persistence_layer_untouched()
    {
        // The property the whole copula approach exists for, and the regression guard on
        // ScaledBeta.Quantile: correlating consecutive days must reorder the sequence without
        // moving the histogram. Checked against July's own fitted CDF by KS at the 5% level.
        var model = SeasonalModel();
        var july = model.ForMonth(7);
        var chain = new ClearSkyIndexChain(model, persistenceOverride: 0.6);
        var random = new Random(7);

        // A single month, so one marginal governs the whole sample: July of 400 successive years.
        var draws = new List<double>();
        for (int year = 0; year < 400; year++)
        {
            chain.Reset();
            var start = new DateOnly(2000, 7, 1);
            for (int day = 0; day < 31; day++)
                draws.Add(chain.Next(start.AddDays(day), random));
        }

        draws.Should().OnlyContain(v => v >= 0.0 && v <= Scale);

        var sorted = draws.OrderBy(v => v).ToList();
        double worst = 0.0;
        for (int i = 0; i < sorted.Count; i++)
        {
            double fitted = july.CumulativeProbability(sorted[i]);
            worst = Math.Max(worst, Math.Abs((i + 1.0) / sorted.Count - fitted));
            worst = Math.Max(worst, Math.Abs(fitted - (double)i / sorted.Count));
        }

        worst.Should().BeLessThan(1.36 / Math.Sqrt(sorted.Count));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(0.7)]
    public void A_long_run_reproduces_the_persistence_it_was_given(double phi)
    {
        // The copula transform is monotone but nonlinear, so a little shrinkage on the way from
        // latent space back to the index is expected and the band below is asymmetric to allow
        // for it. This is exactly why the acceptance check in knowledge.md §12 is run on the full
        // synthetic series rather than inferred from phi.
        var chain = new ClearSkyIndexChain(SeasonalModel(), persistenceOverride: phi);

        WithinJulyLag1(chain, runs: 2_000, random: new Random(20260731))
            .Should().BeInRange(phi - 0.08, phi + 0.03);
    }

    [Fact]
    public void Zero_persistence_reduces_to_independent_sampling()
    {
        // The path the reports use to show the before-and-after, so it has to be exactly the old
        // behaviour and not merely a weak version of the new one.
        var chain = new ClearSkyIndexChain(SeasonalModel(), persistenceOverride: 0.0);

        WithinJulyLag1(chain, runs: 2_000, random: new Random(11))
            .Should().BeApproximately(0.0, 0.02);
    }

    [Theory]
    // phi^gap, so at phi = 0.7 a ten-day hole leaves 0.028 - indistinguishable from nothing.
    [InlineData(1, 0.60, 0.75)]
    [InlineData(3, 0.25, 0.42)]
    [InlineData(10, -0.03, 0.06)]
    public void Correlation_decays_with_the_size_of_the_gap(int gap, double low, double high)
    {
        // 7.4% of the DWD record is missing, so this is not a corner case. Carrying phi unchanged
        // across a hole would invent a dependence between days that are weeks apart. Both days of
        // each pair sit in July, so the marginal is constant and the correlation measured here is
        // the chain's alone, with no seasonal component mixed in.
        var model = SeasonalModel();
        var chain = new ClearSkyIndexChain(model, persistenceOverride: 0.7);
        var random = new Random(12);
        var start = new DateOnly(2000, 7, 1);

        var before = new List<double>();
        var after = new List<double>();

        for (int trial = 0; trial < 30_000; trial++)
        {
            chain.Reset();
            before.Add(chain.Next(start, random));
            after.Add(chain.Next(start.AddDays(gap), random));
        }

        Correlation(before, after).Should().BeInRange(low, high);
    }

    private static double Correlation(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        double meanX = x.Average();
        double meanY = y.Average();

        double covariance = 0.0, varianceX = 0.0, varianceY = 0.0;
        for (int i = 0; i < x.Count; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        return covariance / Math.Sqrt(varianceX * varianceY);
    }

    [Fact]
    public void Reset_starts_a_fresh_run_rather_than_carrying_yesterday_forward()
    {
        var model = SeasonalModel();
        var chain = new ClearSkyIndexChain(model, persistenceOverride: 0.9);
        var start = new DateOnly(2000, 7, 1);

        var first = Run(chain, start, 50, new Random(5));

        chain.Reset();
        var second = Run(chain, start, 50, new Random(5));

        second.Select(d => d.Index).Should().Equal(first.Select(d => d.Index));
    }

    [Fact]
    public void A_chain_defaults_to_the_persistence_its_model_was_fitted_with()
    {
        var model = SeasonalModel();

        new ClearSkyIndexChain(model).Persistence.Should().Be(model.Persistence);
    }

    [Fact]
    public void Persistence_of_one_is_refused_because_it_has_no_stationary_distribution()
    {
        var model = SeasonalModel();

        FluentActions.Invoking(() => new ClearSkyIndexChain(model, persistenceOverride: 1.0))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new ClearSkyIndexChain(model, persistenceOverride: -0.2))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}

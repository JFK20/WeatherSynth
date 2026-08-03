using FluentAssertions;
using WeatherSynth.Climate;
using WeatherSynth.Data;
using Xunit;

namespace WeatherSynth.Core.Tests;

/// <summary>
/// The wind data layer against the real record.
///
/// <para><b>Data-aware, following the ZENIT validation's precedent</b>: when
/// <c>data/dwd_essen_wind.csv</c> is absent every test here passes silently, so a machine without
/// the station files can still build and test the library. When it is present, these are the
/// numbers the whole wind model is built on - each was measured against the raw file
/// independently of this code, so a disagreement means the reader or the aggregation drifted,
/// not that the data changed.</para>
/// </summary>
public class EssenWindRecordTests
{
    private static readonly IReadOnlyList<DwdWindDay>? Days = LoadDays();

    private static IReadOnlyList<DwdWindDay>? LoadDays()
    {
        string? path = RepositoryData.TryLocateEssenWind();
        return path is null ? null : DwdWindDayAggregator.ToDays(DwdWindReader.Read(path));
    }

    private static IReadOnlyList<DwdWindDay>? CompleteDays() =>
        Days?.Where(d => d.IsComplete).ToList();

    [Fact]
    public void Covers_the_expected_span_with_the_expected_completeness()
    {
        if (Days is null)
            return;

        var complete = CompleteDays()!;

        Days[0].Date.Should().Be(new DateOnly(2009, 1, 1));
        Days[^1].Date.Should().Be(new DateOnly(2025, 12, 31));

        // 21 days short of complete out of 6,207: this record is effectively gapless, unlike the
        // solar one's 7.4%. Gap-aware code is still required; gaps just do not drive decisions.
        Days.Count.Should().Be(6207);
        complete.Count.Should().Be(6186);
    }

    [Fact]
    public void Reproduces_the_records_daily_speed_statistics()
    {
        if (CompleteDays() is not { } complete)
            return;

        var speeds = complete.Select(d => d.MeanSpeed).ToList();

        speeds.Average().Should().BeApproximately(3.2219, 0.0005);
        speeds.Min().Should().BeApproximately(0.95, 0.005);
        speeds.Max().Should().BeApproximately(9.10, 0.005);

        double mean = speeds.Average();
        double sd = Math.Sqrt(speeds.Sum(v => (v - mean) * (v - mean)) / (speeds.Count - 1));
        sd.Should().BeApproximately(1.2545, 0.0005);
    }

    [Fact]
    public void Reproduces_the_measured_seasonal_swing()
    {
        if (CompleteDays() is not { } complete)
            return;

        var byMonth = complete
            .GroupBy(d => d.Date.Month)
            .ToDictionary(g => g.Key, g => g.Average(d => d.MeanSpeed));

        // Winter windy, late summer calm, ratio about 1.4. The whole seasonal cycle lives in
        // these twelve numbers, because wind has no clear-sky ceiling to carry it.
        byMonth[12].Should().BeApproximately(3.827, 0.002);
        byMonth[2].Should().BeApproximately(3.824, 0.002);
        byMonth[8].Should().BeApproximately(2.721, 0.002);

        (byMonth.Values.Max() / byMonth.Values.Min()).Should().BeApproximately(1.41, 0.02);
    }

    [Fact]
    public void Reproduces_the_measured_persistence()
    {
        if (CompleteDays() is not { } complete)
            return;

        // The acceptance target for the persistence chain, and higher than solar's 0.437 - wind
        // is more persistent than cloud. This is the RAW lag-1; the chain's phi is fitted on
        // normal scores and comes out smaller, because the marginals re-supply the season.
        var series = complete.Select(d => (d.Date, d.MeanSpeed));

        IndexSeriesStatistics
            .Lag1Autocorrelation(series)
            .Should()
            .BeApproximately(0.5287, 0.0005);
    }

    [Fact]
    public void Reproduces_the_measured_energy_pattern_factor()
    {
        if (CompleteDays() is not { } complete)
            return;

        // Cubing a daily mean speed understates the day's energy by this much, systematically.
        var factors = complete.Select(d => d.EnergyPatternFactor).OrderBy(v => v).ToList();

        factors[factors.Count / 2].Should().BeApproximately(1.251, 0.002);
        factors.Average().Should().BeApproximately(1.305, 0.002);
        factors.Should().OnlyContain(f => f >= 1.0);
    }

    [Fact]
    public void Fits_a_three_parameter_weibull_that_passes_every_month()
    {
        if (CompleteDays() is not { } complete)
            return;

        // The finding that decided the distribution: the textbook two-parameter Weibull passes
        // 3 months of 12 on this record, the three-parameter one passes all 12, and freeing gamma
        // pulls k from 2.6-3.4 back into the 1.7-2.2 canonical for wind.
        foreach (var group in complete.GroupBy(d => d.Date.Month).OrderBy(g => g.Key))
        {
            var speeds = group.Select(d => d.MeanSpeed).ToList();

            var fit = Weibull.FitByMaximumLikelihood(speeds);
            double distance = GoodnessOfFit.KolmogorovSmirnovDistance(
                speeds,
                fit.CumulativeProbability
            );

            distance
                .Should()
                .BeLessThan(
                    GoodnessOfFit.CriticalValueFivePercent(speeds.Count),
                    "month {0} must pass a 5% KS test with a three-parameter Weibull",
                    group.Key
                );

            fit.Shape.Should().BeInRange(1.6, 2.3);
            fit.Location.Should().BeInRange(0.5, 1.5);
            fit.Mean.Should().BeApproximately(speeds.Average(), 0.05);
        }
    }
}

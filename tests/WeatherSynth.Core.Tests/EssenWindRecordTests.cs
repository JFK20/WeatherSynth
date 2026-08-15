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

    /// <summary>The model fitted from the real record, built once - twelve MLE fits is not free.</summary>
    private static readonly Lazy<WindSpeedModel?> LazyModel = new(() =>
        Days is null
            ? null
            : WindSpeedModel.Fit(
                WindSpeedSeriesBuilder.Build(Days),
                DwdWindStations.EssenBredeney.AnemometerHeightMeters
            )
    );

    [Fact]
    public void Fits_a_three_parameter_weibull_that_passes_every_month()
    {
        if (LazyModel.Value is not { } model || CompleteDays() is not { } complete)
            return;

        // The acceptance check on the whole wind fit, through the path the library actually uses.
        foreach (var group in complete.GroupBy(d => d.Date.Month).OrderBy(g => g.Key))
        {
            var speeds = group.Select(d => d.MeanSpeed).ToList();
            var fit = model.ForMonth(group.Key);

            GoodnessOfFit
                .KolmogorovSmirnovDistance(speeds, fit.CumulativeProbability)
                .Should()
                .BeLessThan(
                    GoodnessOfFit.CriticalValueFivePercent(speeds.Count),
                    "month {0} must pass a 5% KS test with a three-parameter Weibull",
                    group.Key
                );

            // k back in the range canonical for wind, which is what freeing gamma buys: pinned
            // at zero these same months fit k at 2.56-3.43 instead.
            fit.Shape.Should().BeInRange(1.6, 2.3);
            fit.Location.Should().BeInRange(0.5, 1.5);
            fit.Mean.Should().BeApproximately(speeds.Average(), 0.05);
        }
    }

    [Fact]
    public void The_third_parameter_is_what_makes_the_fit_pass()
    {
        if (LazyModel.Value is not { } model || CompleteDays() is not { } complete)
            return;

        // The measurement the modelling decision rests on, re-made rather than quoted. Both fits
        // by MLE on the same days, so what is compared is the parameter and not the method.
        int twoParameterPasses = 0;

        foreach (var group in complete.GroupBy(d => d.Date.Month))
        {
            var speeds = group.Select(d => d.MeanSpeed).ToList();
            var pinned = Weibull.FitByMaximumLikelihood(speeds, location: 0.0);

            if (
                GoodnessOfFit.KolmogorovSmirnovDistance(speeds, pinned.CumulativeProbability)
                < GoodnessOfFit.CriticalValueFivePercent(speeds.Count)
            )
                twoParameterPasses++;

            // Pinning gamma at zero forces density down to speeds this site never sees, and the
            // fit pays for it by inflating k well outside the range wind actually occupies.
            pinned.Shape.Should().BeGreaterThan(model.ForMonth(group.Key).Shape);
        }

        twoParameterPasses.Should().Be(3);
    }

    [Fact]
    public void Fits_the_persistence_the_chain_will_need()
    {
        if (LazyModel.Value is not { } model)
            return;

        // Smaller than the raw 0.5287 measured above, and that is the point: phi is fitted on
        // normal scores, which takes the seasonal cycle out. The twelve marginals put it back.
        model.Persistence.Should().BeApproximately(0.444, 0.005);
        model.Persistence.Should().BeLessThan(0.5287);
    }

    [Fact]
    public void The_fitted_model_reproduces_the_records_annual_mean()
    {
        if (LazyModel.Value is not { } model || CompleteDays() is not { } complete)
            return;

        // Weighted by calendar month, since the months differ in length. Far off 3.22 m/s would
        // mean a height transfer applied twice, A read as the mean, or the wrong resolution.
        double fitted =
            Enumerable
                .Range(1, 12)
                .Sum(month => model.ForMonth(month).Mean * DateTime.DaysInMonth(2001, month)) / 365.0;

        fitted.Should().BeApproximately(complete.Average(d => d.MeanSpeed), 0.01);
        model.ReferenceHeightMeters.Should().Be(15.0);
    }
}

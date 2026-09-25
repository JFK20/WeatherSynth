using FluentAssertions;
using WeatherSynth.Wind;
using Xunit;

namespace WeatherSynth.Core.Tests;

/// <summary>
/// Hours from <c>GenerateYear</c>, against the bundled model - no station files needed, so these
/// always run.
/// </summary>
public class HourlyValuesTests
{
    private const int Seed = 4242;

    private static readonly CoupledWeatherYear Year2025 = SyntheticWeather.Default.GenerateYear(2025, Seed);

    [Fact]
    public void Every_day_has_twenty_four_hours()
    {
        Year2025.Hours.Should().HaveCount(365 * 24);
        SyntheticWeather.Default.GenerateYear(2024, Seed).Hours.Should().HaveCount(366 * 24);

        Year2025.Hours[0].Start.Should().Be(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Year2025.Hours[^1].Start.Should().Be(new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Hours_are_contiguous_in_utc()
    {
        var hours = Year2025.Hours;

        for (int i = 1; i < hours.Count; i++)
            (hours[i].Start - hours[i - 1].Start).Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Solar_hours_add_up_to_the_day()
    {
        for (int d = 0; d < Year2025.Days.Count; d++)
        {
            var day = Year2025.Days[d].Solar;
            double sum = 0.0,
                ceiling = 0.0;

            for (int h = 0; h < 24; h++)
            {
                var hour = Year2025.Hours[d * 24 + h].Solar;
                hour.ClearSkyIndex.Should().Be(day.ClearSkyIndex);
                sum += hour.GhiWhPerM2;
                ceiling += hour.ClearSkyWhPerM2;
            }

            sum.Should().BeApproximately(day.GhiWhPerM2, 1e-9 * Math.Max(1.0, day.GhiWhPerM2));
            ceiling.Should().BeApproximately(day.ClearSkyWhPerM2, 1e-9 * Math.Max(1.0, day.ClearSkyWhPerM2));
        }
    }

    [Fact]
    public void The_sun_shines_by_day_only_and_peaks_near_noon()
    {
        // Bochum is at 7.2°E, so solar noon is about 11:30 UTC: the 11h hour is the brightest.
        var midsummer = Year2025.HoursBetween(
            new DateTime(2025, 6, 21, 0, 0, 0),
            new DateTime(2025, 6, 22, 0, 0, 0)
        );

        midsummer[0].Solar.ClearSkyWhPerM2.Should().Be(0.0);
        midsummer[23].Solar.ClearSkyWhPerM2.Should().Be(0.0);

        int brightest = Enumerable.Range(0, 24).MaxBy(h => midsummer[h].Solar.ClearSkyWhPerM2);
        brightest.Should().BeInRange(11, 12);

        var midwinter = Year2025.HoursBetween(
            new DateTime(2025, 12, 21, 0, 0, 0),
            new DateTime(2025, 12, 22, 0, 0, 0)
        );
        midwinter.Take(7).Should().OnlyContain(h => h.Solar.GhiWhPerM2 == 0.0);
        midwinter.Skip(17).Should().OnlyContain(h => h.Solar.GhiWhPerM2 == 0.0);
    }

    [Fact]
    public void Wind_hours_average_exactly_to_the_day()
    {
        for (int d = 0; d < Year2025.Days.Count; d++)
        {
            var day = Year2025.Days[d].Wind;
            double sum = 0.0,
                sumAtReference = 0.0;

            for (int h = 0; h < 24; h++)
            {
                var hour = Year2025.Hours[d * 24 + h].Wind;
                hour.Speed.Should().BeGreaterThanOrEqualTo(0.0);
                sum += hour.Speed;
                sumAtReference += hour.SpeedAtReference;
            }

            (sum / 24).Should().BeApproximately(day.MeanSpeed, 1e-9 * day.MeanSpeed);
            (sumAtReference / 24).Should().BeApproximately(day.MeanSpeedAtReference, 1e-9 * day.MeanSpeedAtReference);
        }
    }

    [Fact]
    public void Wind_hours_carry_the_energy_the_intraday_shape_expects()
    {
        var provider = SyntheticWeather.DefaultWind;
        double ratio = 0.0;
        int days = 0;

        foreach (int seed in new[] { 1, 2, 3 })
        {
            var year = SyntheticWeather.Default.GenerateYear(2025, seed);

            for (int d = 0; d < year.Days.Count; d++)
            {
                var day = year.Days[d].Wind;
                double cubes = 0.0;

                for (int h = 0; h < 24; h++)
                    cubes += Math.Pow(year.Hours[d * 24 + h].Wind.Speed, 3);

                double measured = cubes / 24 / Math.Pow(day.MeanSpeed, 3);
                ratio += measured / provider.IntradayShape.EnergyPatternFactorFor(day.MeanSpeedAtReference);
                days++;
            }
        }

        (ratio / days).Should().BeInRange(0.95, 1.05);
    }

    [Fact]
    public void Wind_hours_fit_back_to_the_bundled_ordering()
    {
        // The round trip: the hourly model fitted on generated hours is the one they came from.
        var hours = new List<(int, IReadOnlyList<double>)>();

        foreach (int seed in new[] { 11, 12, 13 })
        {
            var year = SyntheticWeather.Default.GenerateYear(2025, seed);

            for (int d = 0; d < year.Days.Count; d++)
                hours.Add((
                    year.Days[d].Date.Month,
                    Enumerable.Range(0, 24).Select(h => year.Hours[d * 24 + h].Wind.Speed).ToArray()
                ));
        }

        var bundled = SyntheticWeather.DefaultWind.HourlyModel;
        var refitted = HourlyWindModel.Fit(hours);

        refitted.Persistence.Should().BeApproximately(bundled.Persistence, 0.03);
        refitted.DiurnalWeight(7).Should().BeApproximately(bundled.DiurnalWeight(7), 0.08);
    }

    [Fact]
    public void Summer_afternoons_are_windier_than_summer_nights()
    {
        double afternoon = 0.0,
            night = 0.0;

        foreach (var hour in Year2025.HoursBetween(new DateTime(2025, 6, 1), new DateTime(2025, 9, 1)))
        {
            int utc = hour.Start.UtcDateTime.Hour;
            if (utc is >= 11 and <= 15)
                afternoon += hour.Wind.Speed;
            else if (utc is >= 0 and <= 4)
                night += hour.Wind.Speed;
        }

        afternoon.Should().BeGreaterThan(1.2 * night);
    }

    [Fact]
    public void Asking_for_hours_changes_no_day()
    {
        var streamed = SyntheticWeather.Default.Generate(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), Seed);

        Year2025.Days.Should().Equal(streamed);

        SyntheticWeather.DefaultWind.GenerateYear(2025, Seed).Days.Should()
            .Equal(SyntheticWeather.DefaultWind.Generate(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), Seed));

        SyntheticWeather.DefaultSolar.GenerateYear(2025, Seed).Days.Should()
            .Equal(SyntheticWeather.DefaultSolar.Generate(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), Seed));
    }

    [Fact]
    public void The_same_seed_reproduces_the_hours()
    {
        var again = SyntheticWeather.Default.GenerateYear(2025, Seed);
        var other = SyntheticWeather.Default.GenerateYear(2025, Seed + 1);

        again.Hours.Should().Equal(Year2025.Hours);
        other.Hours.Select(h => h.Wind.Speed).Should().NotEqual(Year2025.Hours.Select(h => h.Wind.Speed));
    }

    [Fact]
    public void Single_resource_years_carry_hours_too()
    {
        var solar = SyntheticWeather.DefaultSolar.GenerateYear(2025, Seed);
        var wind = SyntheticWeather.DefaultWind.GenerateYear(2025, Seed);

        solar.Hours.Should().HaveCount(365 * 24);
        wind.Hours.Should().HaveCount(365 * 24);

        for (int d = 0; d < wind.Days.Count; d++)
            Enumerable.Range(0, 24).Average(h => wind.Hours[d * 24 + h].Speed)
                .Should().BeApproximately(wind.Days[d].MeanSpeed, 1e-9 * wind.Days[d].MeanSpeed);
    }

    [Fact]
    public void Hours_at_a_hub_scale_with_the_transfer()
    {
        var hub = SyntheticWeather.Default.GenerateYear(2025, Seed, solarSite: null, ReferenceTurbines.HundredMetreHub);

        foreach (var hour in hub.HoursBetween(new DateTime(2025, 3, 1), new DateTime(2025, 3, 2)))
            (hour.Wind.Speed / hour.Wind.SpeedAtReference).Should().BeApproximately(
                hub.Days[59].Wind.MeanSpeed / hub.Days[59].Wind.MeanSpeedAtReference,
                1e-9
            );
    }

    [Fact]
    public void HoursBetween_returns_the_hours_starting_in_the_range()
    {
        var hours = Year2025.HoursBetween(new DateTime(2025, 1, 1, 6, 0, 0), new DateTime(2025, 1, 1, 12, 0, 0));

        hours.Select(h => h.Start.Hour).Should().Equal(6, 7, 8, 9, 10, 11);
        hours.Should().Equal(Year2025.Hours.Skip(6).Take(6));

        var offset = Year2025.HoursBetween(
            new DateTimeOffset(2025, 1, 1, 7, 0, 0, TimeSpan.FromHours(1)),
            new DateTimeOffset(2025, 1, 1, 13, 0, 0, TimeSpan.FromHours(1))
        );
        offset.Should().Equal(hours);

        // Part-hours: an hour belongs to the range when it starts in it.
        Year2025.HoursBetween(new DateTime(2025, 1, 1, 6, 30, 0), new DateTime(2025, 1, 1, 8, 30, 0))
            .Select(h => h.Start.Hour).Should().Equal(7, 8);
    }

    [Fact]
    public void HoursBetween_is_clamped_to_the_year()
    {
        Year2025.HoursBetween(new DateTime(2024, 12, 31, 22, 0, 0), new DateTime(2025, 1, 1, 2, 0, 0))
            .Select(h => h.Start.Hour).Should().Equal(0, 1);

        Year2025.HoursBetween(new DateTime(2025, 12, 31, 22, 0, 0), new DateTime(2026, 1, 1, 2, 0, 0))
            .Select(h => h.Start.Hour).Should().Equal(22, 23);

        Year2025.HoursBetween(new DateTime(2026, 3, 1), new DateTime(2026, 3, 2)).Should().BeEmpty();
        Year2025.HoursBetween(new DateTime(2025, 3, 1), new DateTime(2025, 3, 1)).Should().BeEmpty();
    }

    [Fact]
    public void HoursBetween_rejects_a_backwards_range()
    {
        Action backwards = () =>
            Year2025.HoursBetween(new DateTime(2025, 1, 1, 12, 0, 0), new DateTime(2025, 1, 1, 6, 0, 0));

        backwards.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_unspecified_DateTime_is_read_in_the_site_zone_not_the_machine_zone()
    {
        var unspecified = Year2025.HoursBetween(new DateTime(2025, 7, 1, 6, 0, 0), new DateTime(2025, 7, 1, 7, 0, 0));
        var utc = Year2025.HoursBetween(
            new DateTime(2025, 7, 1, 6, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 7, 1, 7, 0, 0, DateTimeKind.Utc)
        );

        unspecified.Should().ContainSingle().Which.Start.Should().Be(new DateTimeOffset(2025, 7, 1, 6, 0, 0, TimeSpan.Zero));
        utc.Should().Equal(unspecified);
    }

    [Fact]
    public void A_site_in_another_zone_labels_its_hours_there()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var site = DwdSolarStations.Bochum.ToSite() with { TimeZone = berlin };

        var year = SyntheticWeather.Default.GenerateYear(2025, Seed, site, windSite: null);

        // Local midnight in January is 23:00 UTC the evening before.
        year.Hours[0].Start.Should().Be(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.FromHours(1)));
        year.Hours[0].Start.Offset.Should().Be(TimeSpan.FromHours(1));

        var morning = year.HoursBetween(new DateTime(2025, 7, 1, 6, 0, 0), new DateTime(2025, 7, 1, 7, 0, 0));
        morning.Should().ContainSingle().Which.Start.Should().Be(new DateTimeOffset(2025, 7, 1, 6, 0, 0, TimeSpan.FromHours(2)));
    }
}

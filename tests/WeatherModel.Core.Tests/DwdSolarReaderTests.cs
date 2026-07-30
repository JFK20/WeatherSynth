using FluentAssertions;
using WeatherModel.Data;
using Xunit;

namespace WeatherModel.Core.Tests;

public class DwdSolarReaderTests
{
    // A real row shape from the Bochum file, with the leading whitespace DWD emits.
    private const string DaytimeRow =
        "       7365;2015062111:34;    1;   2500;   80.0;  300.0;  60;    28.60;2015062112:00;eor";

    private const string MissingRow =
        "       7365;2009010100:34;    1;   -999;   -999;   -999;   0;   150.98;2009010101:00;eor";

    [Fact]
    public void Converts_radiation_from_joule_per_cm2_to_watt_hours_per_m2()
    {
        var interval = DwdSolarReader.ParseLine(DaytimeRow);

        // 300 J/cm² = 3.0e6 J/m², and 1 Wh = 3600 J.
        interval.GlobalWhPerM2.Should().BeApproximately(300.0 * 10000.0 / 3600.0, 1e-9);
        interval.GlobalWhPerM2.Should().BeApproximately(833.333, 0.001);
        interval.DiffuseWhPerM2.Should().BeApproximately(80.0 * 10000.0 / 3600.0, 1e-9);
    }

    [Fact]
    public void Maps_the_minus_999_sentinel_to_null_rather_than_a_value()
    {
        // The expensive failure mode: read as a number, a missing hour drags a daily total
        // down by roughly 2,775 Wh/m² and can push it negative.
        var interval = DwdSolarReader.ParseLine(MissingRow);

        interval.GlobalWhPerM2.Should().BeNull();
        interval.DiffuseWhPerM2.Should().BeNull();
    }

    [Fact]
    public void Reads_MESS_DATUM_as_the_interval_end_in_utc()
    {
        var interval = DwdSolarReader.ParseLine(DaytimeRow);

        interval.EndUtc.Should().Be(new DateTimeOffset(2015, 6, 21, 11, 34, 0, TimeSpan.Zero));
        interval.EndUtc.Offset.Should().Be(TimeSpan.Zero);
        interval.StartUtc.Should().Be(new DateTimeOffset(2015, 6, 21, 10, 34, 0, TimeSpan.Zero));
        interval.MidpointUtc.Should().Be(new DateTimeOffset(2015, 6, 21, 11, 4, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Assigns_the_interval_ending_at_woz_midnight_to_the_previous_solar_day()
    {
        // WOZ 00:00 closes the 23:00-24:00 interval, which belongs to the day before. Those
        // hours are always dark so no energy moves, but it keeps a WOZ day at exactly 24
        // intervals, which the completeness check depends on.
        var interval = DwdSolarReader.ParseLine(
            "       7365;2015062122:34;    1;   -999;    0.0;    0.0;   0;   145.00;2015062200:00;eor");

        interval.WozDate.Should().Be(new DateOnly(2015, 6, 21));
    }

    [Fact]
    public void Reports_daylight_from_the_zenith_angle()
    {
        DwdSolarReader.ParseLine(DaytimeRow).IsDaylight.Should().BeTrue();
        DwdSolarReader.ParseLine(MissingRow).IsDaylight.Should().BeFalse();
    }
}

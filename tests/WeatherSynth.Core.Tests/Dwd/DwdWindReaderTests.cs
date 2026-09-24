using FluentAssertions;
using WeatherSynth.Data;
using Xunit;

namespace WeatherSynth.Core.Tests;

public class DwdWindReaderTests
{
    // Real row shapes from the Essen-Bredeney file, with the whitespace DWD emits.
    private const string OrdinaryRow = "       1303;2009010100;   10;   1.8; 330;eor";

    private const string MissingRow = "       1303;2015031412;   10;-999;-999;eor";

    [Fact]
    public void Reads_speed_and_direction()
    {
        var hour = DwdWindReader.ParseLine(OrdinaryRow);

        hour.SpeedMetersPerSecond.Should().Be(1.8);
        hour.DirectionDegrees.Should().Be(330.0);
        hour.QualityLevel.Should().Be(10);
        hour.HasSpeed.Should().BeTrue();
    }

    [Fact]
    public void Maps_the_minus_999_sentinel_to_null_rather_than_a_value()
    {
        // The expensive failure mode: read as a number, one missing hour drags the day's mean
        // speed from about 3 m/s to about -38 m/s.
        var hour = DwdWindReader.ParseLine(MissingRow);

        hour.SpeedMetersPerSecond.Should().BeNull();
        hour.DirectionDegrees.Should().BeNull();
        hour.HasSpeed.Should().BeFalse();
    }

    [Fact]
    public void Reads_MESS_DATUM_as_a_whole_utc_hour()
    {
        // Note the format: yyyyMMddHH, not the solar file's yyyyMMddHH:mm. Solar intervals are
        // WOZ-aligned and land on odd minutes; these are plain clock hours.
        var hour = DwdWindReader.ParseLine(OrdinaryRow);

        hour.TimestampUtc.Should().Be(new DateTimeOffset(2009, 1, 1, 0, 0, 0, TimeSpan.Zero));
        hour.TimestampUtc.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Assigns_the_hour_to_the_utc_date_it_is_labelled_with()
    {
        // Taken at face value rather than as an interval end. Every fitted parameter and
        // validation target in this project was measured under that convention, so the hour
        // labelled 23:00 belongs to the day it names, not to the following one.
        DwdWindReader
            .ParseLine("       1303;2015031423;   10;   4.2; 250;eor")
            .UtcDate.Should()
            .Be(new DateOnly(2015, 3, 14));

        DwdWindReader
            .ParseLine("       1303;2015031500;   10;   4.1; 250;eor")
            .UtcDate.Should()
            .Be(new DateOnly(2015, 3, 15));
    }

    [Fact]
    public void Rejects_a_row_with_too_few_columns()
    {
        Action parse = () => DwdWindReader.ParseLine("       1303;2009010100;   10;eor");

        parse.Should().Throw<FormatException>();
    }
}

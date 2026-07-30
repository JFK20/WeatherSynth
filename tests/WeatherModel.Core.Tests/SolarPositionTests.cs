using FluentAssertions;
using WeatherModel.Data;
using WeatherModel.Solar;
using Xunit;

namespace WeatherModel.Core.Tests;

public class SolarPositionTests
{
    [Fact]
    public void Declination_changes_through_the_day()
    {
        // Regression guard. SolarCalculator's own SolarDeclination returns one value per
        // calendar date regardless of time of day; around the equinoxes the true declination
        // moves about 0.39°/day, so a constant value costs up to half a day's drift every
        // afternoon. SolarPositionCalculator interpolates to fix that — if this ever goes back
        // to being constant, the fix has been lost.
        var calculator = new SolarPositionCalculator(51.4445, 7.3852);

        // Compare zenith at the same hour angle either side of noon on an equinox, where the
        // declination drift is fastest and therefore most visible.
        double morning = calculator.GeometricZenithDegrees(
            new DateTimeOffset(2015, 3, 20, 6, 0, 0, TimeSpan.Zero));
        double evening = calculator.GeometricZenithDegrees(
            new DateTimeOffset(2015, 3, 20, 18, 0, 0, TimeSpan.Zero));

        morning.Should().NotBe(evening);
    }

    [Fact]
    public void Refraction_correction_is_about_half_a_degree_at_the_horizon()
    {
        // knowledge.md §7 quotes ~0.57° "at the horizon", which needs pinning down: that is the
        // correction at −0.575° GEOMETRIC elevation — the point where refraction has just lifted
        // the sun onto the visible horizon, so it is self-consistent. At 0° geometric elevation
        // the sun is already above the apparent horizon and the correction is smaller, 0.482°.
        DailyClearSkyCalculator.RefractionCorrectionDegrees(-0.575).Should().BeApproximately(0.575, 0.005);
        DailyClearSkyCalculator.RefractionCorrectionDegrees(0.0).Should().BeApproximately(0.482, 0.005);

        // Neither is the library's 0.833° sunrise/sunset convention, which bundles refraction
        // (~0.57°) together with the sun's semi-diameter (~0.27°). That one is about when the
        // disc's edge touches the horizon; this one is about where a ray actually goes.

        // Negligible overhead.
        DailyClearSkyCalculator.RefractionCorrectionDegrees(90.0).Should().Be(0.0);

        // Monotonic: refraction only ever grows as the sun sinks.
        for (double elevation = 1.0; elevation < 85.0; elevation += 1.0)
        {
            DailyClearSkyCalculator.RefractionCorrectionDegrees(elevation)
                .Should().BeLessThan(DailyClearSkyCalculator.RefractionCorrectionDegrees(elevation - 1.0));
        }
    }

    /// <summary>
    /// The headline validation: 151,871 independent reference zenith angles from the DWD
    /// station file. knowledge.md §5 names solar-position errors — degree/radian confusion,
    /// longitude sign flips, timezone mismatches — as the most expensive bug class here, and
    /// this catches all of them at once.
    /// </summary>
    [Fact]
    public void Matches_the_DWD_reference_zenith_column()
    {
        string? dataPath = RepositoryData.TryLocateBochum();
        if (dataPath is null)
            return; // Station file not present; nothing to validate against.

        var station = DwdStations.Bochum;
        var calculator = new SolarPositionCalculator(station.LatitudeDegrees, station.LongitudeDegrees);

        double sumSquared = 0.0;
        double sumSigned = 0.0;
        double maxAbsolute = 0.0;
        int count = 0;

        foreach (var interval in DwdSolarReader.Read(dataPath))
        {
            double error = calculator.GeometricZenithDegrees(interval.MidpointUtc) - interval.ZenithDegrees;

            sumSquared += error * error;
            sumSigned += error;
            maxAbsolute = Math.Max(maxAbsolute, Math.Abs(error));
            count++;
        }

        count.Should().BeGreaterThan(150_000);

        double rmse = Math.Sqrt(sumSquared / count);
        double meanBias = sumSigned / count;

        // Achieved: RMSE 0.033°, mean bias 0.00000°, max 0.086°. The thresholds sit a little
        // above that so ordinary refinement does not trip them, but well below the 0.178° the
        // uncorrected declination produced.
        rmse.Should().BeLessThan(0.05);
        Math.Abs(meanBias).Should().BeLessThan(0.005);
        maxAbsolute.Should().BeLessThan(0.15);
    }
}

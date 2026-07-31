using System;

namespace WeatherModel.Solar
{
    /// <summary>
    /// A place to generate for: everything the clear-sky ceiling needs to know about geometry.
    ///
    /// <para>The index distribution is fitted at one station and applied anywhere sharing its
    /// cloud climate; this is the half that is <i>not</i> transferable. Kt divides out geometry,
    /// so putting it back is exactly what a site does.</para>
    /// </summary>
    /// <param name="LatitudeDegrees">Latitude in degrees, north positive.</param>
    /// <param name="LongitudeDegrees">Longitude in degrees, east positive.</param>
    /// <param name="AltitudeMeters">Elevation above sea level in metres.</param>
    /// <param name="TimeZone">
    /// Time zone the site's days are bounded by. Only affects where one day ends and the next
    /// begins; the physics is computed in UTC either way.
    /// </param>
    public sealed record SolarSite(
        double LatitudeDegrees,
        double LongitudeDegrees,
        double AltitudeMeters,
        TimeZoneInfo TimeZone)
    {
        /// <summary>
        /// Integration step every ceiling in this library is built with.
        ///
        /// <para>15 minutes rather than <see cref="DailyClearSkyCalculator"/>'s own 10-minute
        /// default, and that is not a taste choice: the fitted index is a ratio whose denominator
        /// was integrated at 15 minutes. Generating against a ceiling built at a different step
        /// would divide by one number and multiply by another.</para>
        /// </summary>
        public static readonly TimeSpan DefaultStep = TimeSpan.FromMinutes(15);

        /// <summary>A site in UTC, which is what a station record aligned to solar time wants.</summary>
        public SolarSite(double latitudeDegrees, double longitudeDegrees, double altitudeMeters)
            : this(latitudeDegrees, longitudeDegrees, altitudeMeters, TimeZoneInfo.Utc)
        {
        }

        /// <summary>
        /// Builds this site's clear-sky ceiling.
        ///
        /// <para>The result memoises per-date terms and is therefore not thread-safe: one per
        /// thread, or one per request.</para>
        /// </summary>
        /// <param name="step">Integration step. Leave alone unless the fit was refitted to match.</param>
        /// <param name="turbidityProvider">Optional turbidity override; defaults to the Bochum site fit.</param>
        public DailyClearSkyCalculator CreateCeiling(
            TimeSpan? step = null,
            Func<DateTime, double>? turbidityProvider = null) => new(
                LatitudeDegrees,
                LongitudeDegrees,
                AltitudeMeters,
                TimeZone,
                step: step ?? DefaultStep,
                turbidityProvider: turbidityProvider);
    }
}

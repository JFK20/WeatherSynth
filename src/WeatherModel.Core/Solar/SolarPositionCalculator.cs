using Innovative.SolarCalculator;

namespace WeatherModel.Solar
{
    /// <summary>
    /// Computes the sun's zenith angle, correcting a defect in SolarCalculator's declination.
    ///
    /// <para><b>Why this exists.</b> SolarCalculator evaluates <c>SolarDeclination</c> once per
    /// calendar date and returns the same value for every time of day querying 2015-03-20 at
    /// 00:00, 06:00, 12:00 and 18:00 UTC all yield −0.3732°, which is the declination at
    /// midnight. But declination moves about 0.39°/day around the equinoxes, so every
    /// afternoon inherits an error of up to half a day's drift.</para>
    ///
    /// <para><b>The correction.</b> Take the library's declination and equation of time at the
    /// surrounding midnights and interpolate to the requested instant, then rebuild the zenith
    /// from the standard spherical relation. Both quantities are smooth over a day, so linear
    /// interpolation leaves well under a thousandth of a degree.</para>
    ///
    /// <para>Not thread-safe: it memoises per-date terms. Use one instance per thread.</para>
    /// </summary>
    public sealed class SolarPositionCalculator
    {
        private const double DegToRad = Math.PI / 180.0;
        private const double RadToDeg = 180.0 / Math.PI;

        private readonly double _latitude;
        private readonly double _longitude;

        /// <summary>
        /// Per-date declination and equation of time. Integrating a day at a 15-minute step
        /// asks for the same date 96 times over, and each miss costs a SolarTimes construction.
        /// </summary>
        private readonly Dictionary<DateTime, DayTerms> _cache = new();

        /// <param name="latitude">Site latitude in degrees, north positive.</param>
        /// <param name="longitude">Site longitude in degrees, east positive.</param>
        public SolarPositionCalculator(double latitude, double longitude)
        {
            _latitude = latitude;
            _longitude = longitude;
        }

        /// <summary>
        /// The sun's true (geometric) zenith angle in degrees, with no refraction correction.
        /// </summary>
        public double GeometricZenithDegrees(DateTimeOffset instant)
        {
            var utc = instant.UtcDateTime;
            var date = utc.Date;

            var today = TermsFor(date);
            var tomorrow = TermsFor(date.AddDays(1));

            // The library's values belong to midnight, so interpolate across to the instant.
            double fraction = utc.TimeOfDay.TotalDays;
            double declinationDeg = today.DeclinationDegrees
                                    + (tomorrow.DeclinationDegrees - today.DeclinationDegrees) * fraction;
            double eotMinutes = today.EquationOfTimeMinutes
                                + (tomorrow.EquationOfTimeMinutes - today.EquationOfTimeMinutes) * fraction;

            // True solar time, and from it the hour angle: zero at solar noon, +15°/hour after.
            double trueSolarHours = utc.TimeOfDay.TotalHours
                                    + _longitude / 15.0
                                    + eotMinutes / 60.0;
            double hourAngleRad = (trueSolarHours - 12.0) * 15.0 * DegToRad;

            double phi = _latitude * DegToRad;
            double delta = declinationDeg * DegToRad;

            double cosZenith = Math.Sin(phi) * Math.Sin(delta)
                               + Math.Cos(phi) * Math.Cos(delta) * Math.Cos(hourAngleRad);

            return Math.Acos(Math.Clamp(cosZenith, -1.0, 1.0)) * RadToDeg;
        }

        private DayTerms TermsFor(DateTime date)
        {
            if (_cache.TryGetValue(date, out var cached))
                return cached;

            var solarTimes = new SolarTimes(new DateTimeOffset(date, TimeSpan.Zero), _latitude, _longitude);

            // Read .Radians rather than .Degrees on the Angle type, .Degrees is only the
            // whole-degree component, not the total angle.
            var terms = new DayTerms(
                (double)solarTimes.SolarDeclination.Radians * RadToDeg,
                (double)solarTimes.EquationOfTime);

            _cache[date] = terms;
            return terms;
        }

        private readonly record struct DayTerms(double DeclinationDegrees, double EquationOfTimeMinutes);
    }
}

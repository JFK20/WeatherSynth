using System;
using System.Collections.Generic;
using Innovative.SolarCalculator;

namespace WeatherModel.Solar
{
    /// <summary>A single instantaneous sample of clear-sky irradiance.</summary>
    public readonly struct ClearSkySample
    {
        /// <summary>The instant this sample was taken at.</summary>
        public DateTimeOffset LocalTime { get; }

        /// <summary>Refraction-corrected solar zenith angle at that instant, in degrees.</summary>
        public double ApparentZenithDegrees { get; }

        /// <summary>Clear-sky irradiance components at that instant.</summary>
        public ClearSkyIrradiance Irradiance { get; }

        /// <summary>Creates a sample.</summary>
        public ClearSkySample(DateTimeOffset localTime, double apparentZenithDegrees, ClearSkyIrradiance irradiance)
        {
            LocalTime = localTime;
            ApparentZenithDegrees = apparentZenithDegrees;
            Irradiance = irradiance;
        }
    }

    /// <summary>Daily-aggregated clear-sky result.</summary>
    public readonly struct DailyClearSky
    {
        /// <summary>Clear-sky global horizontal irradiation for the day, in Wh/m².</summary>
        public double GhiWhPerM2 { get; }

        /// <summary>Clear-sky global horizontal irradiation for the day, in kWh/m².</summary>
        public double GhiKWhPerM2 => GhiWhPerM2 / 1000.0;

        /// <summary>Peak instantaneous clear-sky GHI during the day, in W/m² (occurs at solar noon).</summary>
        public double PeakGhiWPerM2 { get; }

        /// <summary>Clear-sky diffuse horizontal irradiation for the day, in Wh/m².</summary>
        public double DhiWhPerM2 { get; }

        /// <summary>The per-timestep samples the daily totals were integrated from.</summary>
        public IReadOnlyList<ClearSkySample> Samples { get; }

        /// <summary>Creates a daily aggregate.</summary>
        public DailyClearSky(double ghiWhPerM2, double dhiWhPerM2, double peakGhiWPerM2,
                             IReadOnlyList<ClearSkySample> samples)
        {
            GhiWhPerM2 = ghiWhPerM2;
            DhiWhPerM2 = dhiWhPerM2;
            PeakGhiWPerM2 = peakGhiWPerM2;
            Samples = samples;
        }
    }

    /// <summary>
    /// Computes the clear-sky ceiling for a whole day at a fixed location.
    ///
    /// The Ineichen model is instantaneous it gives W/m² at one moment. A daily total in
    /// Wh/m² requires integrating over the day, which is what this class does: it steps
    /// through the day, asks SolarCalculator for the sun's position at each step, evaluates
    /// the clear-sky model, and sums the energy.
    /// </summary>
    public sealed class DailyClearSkyCalculator
    {
        private readonly double _latitude;
        private readonly double _longitude;
        private readonly double _altitudeMeters;
        private readonly TimeZoneInfo _timeZone;
        private readonly Func<DateTime, double> _turbidityProvider;
        private readonly TimeSpan _step;
        private readonly bool _perezEnhancement;
        private readonly SolarPositionCalculator _solarPosition;

        /// <param name="latitude">Site latitude in degrees, −90 to +90.</param>
        /// <param name="longitude">Site longitude in degrees, −180 to +180 (east positive).</param>
        /// <param name="altitudeMeters">Site elevation above sea level in metres.</param>
        /// <param name="timeZone">Time zone of the site. Used to resolve UTC offsets correctly across DST.</param>
        /// <param name="step">
        /// Integration timestep. 10 minutes is a good default: fine enough that the daily
        /// total is within a fraction of a percent of the true integral, coarse enough to
        /// stay cheap (144 samples per day).
        /// </param>
        /// <param name="turbidityProvider">
        /// Supplies the Linke turbidity for a given date. Defaults to
        /// <see cref="LinkeTurbidityTable.BochumFitted"/>, fitted to measured cloudless days in
        /// the Ruhr area. Inject a different table for a site with a materially different
        /// aerosol climate.
        /// </param>
        /// <param name="perezEnhancement">
        /// Whether the Ineichen model applies the Perez enhancement term.
        ///
        /// <para>Leave this alone unless you also refit turbidity: the two are coupled, and the
        /// default table was fitted with the enhancement ON. See
        /// <see cref="ClearSkyIneichen.Estimate"/>.</para>
        /// </param>
        public DailyClearSkyCalculator(
            double latitude,
            double longitude,
            double altitudeMeters,
            TimeZoneInfo timeZone,
            TimeSpan? step = null,
            Func<DateTime, double>? turbidityProvider = null,
            bool perezEnhancement = true)
        {
            if (latitude < -90.0 || latitude > 90.0)
                throw new ArgumentOutOfRangeException(nameof(latitude));
            if (longitude < -180.0 || longitude > 180.0)
                throw new ArgumentOutOfRangeException(nameof(longitude));

            _latitude = latitude;
            _longitude = longitude;
            _altitudeMeters = altitudeMeters;
            _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
            _turbidityProvider = turbidityProvider ?? LinkeTurbidity.Default;
            _perezEnhancement = perezEnhancement;
            _solarPosition = new SolarPositionCalculator(latitude, longitude);
            _step = step ?? TimeSpan.FromMinutes(15);

            if (_step <= TimeSpan.Zero || _step > TimeSpan.FromHours(1))
                throw new ArgumentOutOfRangeException(nameof(step), "Use a step between 1 minute and 1 hour.");
        }

        /// <summary>
        /// Instantaneous clear-sky irradiance at one moment in local time.
        /// </summary>
        public ClearSkySample At(DateTimeOffset localTime)
        {
            double apparentZenithDeg = ApparentZenithDegrees(localTime);
            double turbidity = _turbidityProvider(localTime.Date);

            var irradiance = ClearSkyIneichen.Estimate(
                apparentZenithDeg,
                localTime.DayOfYear,
                turbidity,
                _altitudeMeters,
                pressurePascal: null,
                perezEnhancement: _perezEnhancement);

            return new ClearSkySample(localTime, apparentZenithDeg, irradiance);
        }

        /// <summary>
        /// The sun's true (geometric) zenith angle in degrees, with no refraction correction.
        ///
        /// This is the quantity meteorological archives normally publish, so it is the one to
        /// compare against when validating solar position against reference data.
        /// </summary>
        public double GeometricZenithDegrees(DateTimeOffset instant) =>
            _solarPosition.GeometricZenithDegrees(instant);

        /// <summary>
        /// The sun's apparent (refraction-corrected) zenith angle in degrees.
        ///
        /// This is what the Kasten-Young air mass formula expects: refraction is negligible
        /// overhead but around half a degree at the horizon, which is exactly where air mass
        /// is largest and matters most.
        /// </summary>
        public double ApparentZenithDegrees(DateTimeOffset instant)
        {
            double geometricElevationDeg = 90.0 - GeometricZenithDegrees(instant);
            return 90.0 - (geometricElevationDeg + RefractionCorrectionDegrees(geometricElevationDeg));
        }
        
        /// <summary>
        /// Atmospheric refraction correction in degrees, to be ADDED to the geometric solar
        /// elevation to obtain the apparent (observed) elevation.
        ///
        /// This is the piecewise approximation from the NOAA Solar Calculations spreadsheet
        /// (column AF). Refraction makes the sun
        /// appear higher than it geometrically is negligible overhead, but around half a
        /// degree at the horizon, which is roughly the sun's own angular diameter.
        /// </summary>
        public static double RefractionCorrectionDegrees(double elevationDegrees)
        {
            if (elevationDegrees > 85.0)
                return 0.0;
 
            double tanElevation = Math.Tan(elevationDegrees * Math.PI / 180.0);
            double arcseconds;
 
            if (elevationDegrees > 5.0)
            {
                arcseconds = 58.1 / tanElevation
                             - 0.07 / Math.Pow(tanElevation, 3)
                             + 0.000086 / Math.Pow(tanElevation, 5);
            }
            else if (elevationDegrees > -0.575)
            {
                // Polynomial fit near the horizon, where the formula above breaks down.
                arcseconds = 1735.0 + elevationDegrees
                    * (-518.2 + elevationDegrees
                        * (103.4 + elevationDegrees
                            * (-12.79 + elevationDegrees * 0.711)));
            }
            else
            {
                arcseconds = -20.772 / tanElevation;
            }
 
            return arcseconds / 3600.0;
        }


        /// <summary>
        /// Integrates clear-sky irradiance across a whole calendar day.
        /// This is the physical ceiling.
        /// </summary>
        public DailyClearSky ForDate(DateTime date)
        {
            var day = date.Date;

            // Resolve the UTC offset once from noon, which avoids picking up the wrong
            // offset on DST transition days (midnight and noon can differ).
            TimeSpan utcOffset = _timeZone.GetUtcOffset(
                DateTime.SpecifyKind(day.AddHours(12), DateTimeKind.Unspecified));

            var samples = new List<ClearSkySample>();
            double ghiWh = 0.0;
            double dhiWh = 0.0;
            double peakGhi = 0.0;

            var dayStart = new DateTimeOffset(day, utcOffset);

            foreach (var (sample, hours) in Walk(dayStart, dayStart.AddDays(1)))
            {
                samples.Add(sample);
                ghiWh += sample.Irradiance.Ghi * hours;
                dhiWh += sample.Irradiance.Dhi * hours;

                if (sample.Irradiance.Ghi > peakGhi)
                    peakGhi = sample.Irradiance.Ghi;
            }

            return new DailyClearSky(ghiWh, dhiWh, peakGhi, samples);
        }

        /// <summary>
        /// Integrates clear-sky GHI over an arbitrary interval, in Wh/m².
        ///
        /// Needed to compare against measured data whose reporting intervals do not line up
        /// with calendar days: integrating the ceiling over exactly the intervals that carry a
        /// valid observation keeps the clearness index an exactly matched ratio, so partial
        /// days stay usable without introducing a bias.
        /// </summary>
        public double IntegrateGhiWhPerM2(DateTimeOffset startInclusive, DateTimeOffset endExclusive)
        {
            if (endExclusive <= startInclusive)
                throw new ArgumentException("End must be after start.", nameof(endExclusive));

            double wh = 0.0;
            foreach (var (sample, hours) in Walk(startInclusive, endExclusive))
                wh += sample.Irradiance.Ghi * hours;

            return wh;
        }

        /// <summary>
        /// Integrates extraterrestrial irradiance on a horizontal surface over an interval, in
        /// Wh/m² the energy that would arrive with no atmosphere at all.
        ///
        /// <para>This is the denominator of the <i>classical</i> clearness index, as distinct
        /// from the clear-sky index which divides by the modelled clear-sky ceiling. The two are
        /// easily conflated and have different ranges: against this denominator even a
        /// cloudless day only reaches about 0.75, because a clear atmosphere still removes a
        /// quarter of the incoming energy.</para>
        /// </summary>
        public double IntegrateExtraterrestrialHorizontalWhPerM2(
            DateTimeOffset startInclusive, DateTimeOffset endExclusive)
        {
            if (endExclusive <= startInclusive)
                throw new ArgumentException("End must be after start.", nameof(endExclusive));

            double wh = 0.0;

            for (var t = startInclusive; t < endExclusive; t = t.Add(_step))
            {
                var segmentEnd = t.Add(_step);
                if (segmentEnd > endExclusive)
                    segmentEnd = endExclusive;

                var duration = segmentEnd - t;
                var midpoint = t.AddTicks(duration.Ticks / 2);

                // Geometric zenith, not apparent: refraction bends light through an atmosphere
                // that by definition is not present in this quantity.
                double cosZenith = Math.Cos(GeometricZenithDegrees(midpoint) * Math.PI / 180.0);
                if (cosZenith <= 0.0)
                    continue;

                wh += ClearSkyIneichen.ExtraterrestrialNormalIrradiance(midpoint.DayOfYear)
                      * cosZenith * duration.TotalHours;
            }

            return wh;
        }

        /// <summary>
        /// Steps across an interval, yielding each sample with the number of hours it stands
        /// for. Samples are taken at the MIDPOINT of each step (midpoint rule) noticeably
        /// more accurate than sampling at step boundaries, especially around sunrise and
        /// sunset where irradiance changes fastest. A final partial step is weighted by its
        /// true duration, so the interval need not be a whole multiple of the step.
        /// </summary>
        private IEnumerable<(ClearSkySample Sample, double Hours)> Walk(
            DateTimeOffset startInclusive, DateTimeOffset endExclusive)
        {
            for (var t = startInclusive; t < endExclusive; t = t.Add(_step))
            {
                var segmentEnd = t.Add(_step);
                if (segmentEnd > endExclusive)
                    segmentEnd = endExclusive;

                var duration = segmentEnd - t;
                var midpoint = t.AddTicks(duration.Ticks / 2);

                yield return (At(midpoint), duration.TotalHours);
            }
        }

        /// <summary>
        /// Convenience overload: the clear-sky daily GHI in kWh/m², which is the single
        /// number the stochastic model needs.
        /// </summary>
        public double DailyGhiKWhPerM2(DateTime date) => ForDate(date).GhiKWhPerM2;
    }
}
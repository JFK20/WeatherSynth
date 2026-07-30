using System;
using System.Collections.Generic;
using Innovative.SolarCalculator;

namespace WeatherModel.Solar
{
    /// <summary>A single instantaneous sample of clear-sky irradiance.</summary>
    public readonly struct ClearSkySample
    {
        public DateTimeOffset LocalTime { get; }
        public double ApparentZenithDegrees { get; }
        public ClearSkyIrradiance Irradiance { get; }

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

        /// <param name="latitude">Site latitude in degrees, −90 to +90.</param>
        /// <param name="longitude">Site longitude in degrees, −180 to +180 (east positive).</param>
        /// <param name="altitudeMeters">Site elevation above sea level in metres.</param>
        /// <param name="timeZone">Time zone of the site. Used to resolve UTC offsets correctly across DST.</param>
        /// <param name="step">
        /// Integration timestep. 10 minutes is a good default: fine enough that the daily
        /// total is within a fraction of a percent of the true integral, coarse enough to
        /// stay cheap (144 samples per day).
        /// </param>
        public DailyClearSkyCalculator(
            double latitude,
            double longitude,
            double altitudeMeters,
            TimeZoneInfo timeZone,
            TimeSpan? step = null)
        {
            if (latitude < -90.0 || latitude > 90.0)
                throw new ArgumentOutOfRangeException(nameof(latitude));
            if (longitude < -180.0 || longitude > 180.0)
                throw new ArgumentOutOfRangeException(nameof(longitude));

            _latitude = latitude;
            _longitude = longitude;
            _altitudeMeters = altitudeMeters;
            _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
            _turbidityProvider = LinkeTurbidity.InterpolatedForCentralEurope;
            _step = step ?? TimeSpan.FromMinutes(15);

            if (_step <= TimeSpan.Zero || _step > TimeSpan.FromHours(1))
                throw new ArgumentOutOfRangeException(nameof(step), "Use a step between 1 minute and 1 hour.");
        }

        /// <summary>
        /// Instantaneous clear-sky irradiance at one moment in local time.
        /// </summary>
        public ClearSkySample At(DateTimeOffset localTime)
        {
            // SolarTimes recomputes the sun's position for whatever instant it is given,
            // so a fresh instance is needed per timestep.
            var solarTimes = new SolarTimes(localTime, _latitude, _longitude);

            // Use the refraction-corrected elevation: the Kasten-Young air mass formula
            // expects an APPARENT zenith angle, and refraction matters most near the horizon
            // where air mass is largest.
            //
            // Read .Radians rather than .Degrees — on the Angle type, .Degrees is only the
            // whole-degree component, not the total angle.
            double geometricElevationDeg = (double)solarTimes.SolarElevation.Radians * 180.0 / Math.PI;
            double elevationDeg = geometricElevationDeg + RefractionCorrectionDegrees(geometricElevationDeg);
            double apparentZenithDeg = 90.0 - elevationDeg;


            double turbidity = _turbidityProvider(localTime.Date);

            var irradiance = ClearSkyIneichen.Estimate(
                apparentZenithDeg,
                localTime.DayOfYear,
                turbidity,
                _altitudeMeters);

            return new ClearSkySample(localTime, apparentZenithDeg, irradiance);
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
        private static double RefractionCorrectionDegrees(double elevationDegrees)
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

            double stepHours = _step.TotalHours;
            var dayStart = new DateTimeOffset(day, utcOffset);
            var dayEnd = dayStart.AddDays(1);

            // Sample at the MIDPOINT of each interval (midpoint rule) — noticeably more
            // accurate than sampling at interval boundaries, especially around sunrise
            // and sunset where irradiance changes fastest.
            for (var t = dayStart; t < dayEnd; t = t.Add(_step))
            {
                var midpoint = t.Add(TimeSpan.FromTicks(_step.Ticks / 2));
                var sample = At(midpoint);

                samples.Add(sample);
                ghiWh += sample.Irradiance.Ghi * stepHours;
                dhiWh += sample.Irradiance.Dhi * stepHours;

                if (sample.Irradiance.Ghi > peakGhi)
                    peakGhi = sample.Irradiance.Ghi;
            }

            return new DailyClearSky(ghiWh, dhiWh, peakGhi, samples);
        }

        /// <summary>
        /// Convenience overload: the clear-sky daily GHI in kWh/m², which is the single
        /// number the stochastic model needs.
        /// </summary>
        public double DailyGhiKWhPerM2(DateTime date) => ForDate(date).GhiKWhPerM2;
    }
}
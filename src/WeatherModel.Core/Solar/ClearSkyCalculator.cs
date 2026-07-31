using System;

namespace WeatherModel.Solar
{
    /// <summary>
    /// Instantaneous clear-sky irradiance components, in W/m².
    /// </summary>
    public readonly struct ClearSkyIrradiance
    {
        /// <summary>Global horizontal irradiance (W/m²).</summary>
        public double Ghi { get; }

        /// <summary>Direct normal irradiance (W/m²).</summary>
        public double Dni { get; }

        /// <summary>Diffuse horizontal irradiance (W/m²).</summary>
        public double Dhi { get; }

        /// <summary>Creates an irradiance triple, all in W/m².</summary>
        public ClearSkyIrradiance(double ghi, double dni, double dhi)
        {
            Ghi = ghi;
            Dni = dni;
            Dhi = dhi;
        }

        /// <summary>Sun below the horizon: no irradiance.</summary>
        public static ClearSkyIrradiance Night => new ClearSkyIrradiance(0.0, 0.0, 0.0);
    }

    /// <summary>
    /// Ineichen–Perez (2002) clear-sky irradiance model.
    ///
    /// Follows the formulation used by pvlib-python (pvlib.clearsky.ineichen), which is the
    /// de-facto reference implementation. Inputs are the apparent (refraction-corrected)
    /// solar zenith angle, the day of year, and the Linke turbidity factor for the site.
    /// </summary>
    public static class ClearSkyIneichen
    {
        /// <summary>
        /// Mean solar constant at 1 AU (W/m²).
        /// https://github.com/pvlib/pvlib-python/issues/1566
        /// </summary>
        public const double SolarConstant = 1361.1;

        /// <summary>
        /// Standard sea-level atmospheric pressure (Pa).
        /// https://en.wikipedia.org/wiki/Atmospheric_pressure
        /// </summary>
        public const int StandardPressure = 101325;

        private const double DegToRad = Math.PI / 180.0;

        /// <summary>
        /// Computes clear-sky GHI, DNI and DHI for a single instant.
        /// https://pvlib-python.readthedocs.io/en/stable/_modules/pvlib/clearsky.html#ineichen
        /// </summary>
        /// <param name="apparentZenithDegrees">
        /// Refraction-corrected solar zenith angle in degrees (0 = sun overhead,
        /// 90 = sun at the horizon). Values >= 90 return zero irradiance.
        /// </param>
        /// <param name="dayOfYear">Day of year, 1-366. Used for the Earth-Sun distance correction.</param>
        /// <param name="linkeTurbidity">
        /// Linke turbidity factor (dimensionless, typically 2-7). Higher = hazier atmosphere.
        /// See <see cref="LinkeTurbidity"/> for monthly climatological values.
        /// </param>
        /// <param name="altitudeMeters">Site elevation above sea level in metres.</param>
        /// <param name="pressurePascal">
        /// Station pressure in Pa. If null, estimated from <paramref name="altitudeMeters"/>
        /// using the standard atmosphere.
        /// </param>
        /// <param name="perezEnhancement">
        /// Whether to apply the <c>exp(0.01 · airmass^1.8)</c> Perez enhancement term.
        /// </param>
        public static ClearSkyIrradiance Estimate(
            double apparentZenithDegrees,
            int dayOfYear,
            double linkeTurbidity,
            double altitudeMeters = 0.0,
            double? pressurePascal = null,
            bool perezEnhancement = true)
        {
            if (dayOfYear < 1 || dayOfYear > 366)
                throw new ArgumentOutOfRangeException(nameof(dayOfYear));
            if (linkeTurbidity <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(linkeTurbidity));

            // Sun at or below the horizon.
            if (apparentZenithDegrees >= 90.0)
                return ClearSkyIrradiance.Night;

            double pressure = pressurePascal ?? PressureFromAltitude(altitudeMeters);
            double airMass = AbsoluteAirMass(apparentZenithDegrees, pressure);
            double dni_extra = ExtraterrestrialNormalIrradiance(dayOfYear);
            double cosZenith = Math.Cos(apparentZenithDegrees * DegToRad);

            // Altitude correction terms.
            double fh1 = Math.Exp(-altitudeMeters / 8000.0);
            double fh2 = Math.Exp(-altitudeMeters / 1250.0);
            double cg1 = 5.09e-5 * altitudeMeters + 0.868;
            double cg2 = 3.92e-5 * altitudeMeters + 0.0387;

            // --- Global horizontal irradiance ---
            double tempGhi = Math.Exp(-cg2 * airMass * (fh1 + fh2 * (linkeTurbidity - 1.0)));

            if (perezEnhancement)
                tempGhi *= Math.Exp(0.01 * Math.Pow(airMass, 1.8));

            double ghi = cg1 * dni_extra * cosZenith * tempGhi;


            if (ghi < 0.0 || double.IsNaN(ghi))
                ghi = 0.0;

            // --- Direct normal irradiance ---
            // Two independent estimates; the model takes the smaller of the two.
            double b = 0.664 + 0.163 / fh1;
            double bnci = Math.Max(b * Math.Exp(-0.09 * airMass * (linkeTurbidity - 1.0)), 0.0);
            double dniDirect = dni_extra * bnci;

            double bnci_2 = (1.0 - (0.1 - 0.2 * Math.Exp(-linkeTurbidity))
                                        / (0.1 + 0.882 / fh1)) / cosZenith;
            double dniFromGhi = ghi * Math.Max(bnci_2, 0.0);

            double dni = Math.Min(dniDirect, dniFromGhi);
            if (dni < 0.0 || double.IsNaN(dni))
                dni = 0.0;

            // --- Diffuse horizontal irradiance (closure) ---
            double dhi = ghi - dni * cosZenith;
            if (dhi < 0.0)
                dhi = 0.0;

            return new ClearSkyIrradiance(ghi, dni, dhi);
        }

        /// <summary>
        /// Extraterrestrial normal irradiance for a given day of year (W/m²),
        /// accounting for the eccentricity of Earth's orbit.
        ///
        /// Uses the Spencer (1971) Fourier expansion of the Earth-Sun distance
        /// correction (R0/R)², equivalent to pvlib's
        /// <c>irradiance.get_extra_radiation(method='spencer')</c>. It is accurate to
        /// about 0.01% versus roughly 0.1% for the single-cosine ASCE form.
        /// </summary>
        public static double ExtraterrestrialNormalIrradiance(int dayOfYear)
        {
            // Day angle, radians. The (dayOfYear - 1) offset is pvlib's default:
            // Jan 1 is angle zero. The ASCE variant omits it.
            double b = 2.0 * Math.PI * (dayOfYear - 1) / 365.0;

            double rOverR0Sqrd = 1.00011
                                 + 0.034221 * Math.Cos(b)
                                 + 0.00128 * Math.Sin(b)
                                 + 0.000719 * Math.Cos(2.0 * b)
                                 + 7.7e-05 * Math.Sin(2.0 * b);

            return SolarConstant * rOverR0Sqrd;
        }

        /// <summary>
        /// Relative optical air mass after Kasten and Young (1989).
        /// https://pvlib-python.readthedocs.io/en/v0.9.0/generated/pvlib.atmosphere.get_relative_airmass.html#pvlib.atmosphere.get_relative_airmass
        /// Returns <see cref="double.PositiveInfinity"/> when the sun is at or below the horizon.
        /// </summary>
        public static double RelativeAirMass(double apparentZenithDegrees)
        {
            if (apparentZenithDegrees > 90.0)
                return double.PositiveInfinity;

            double cosZenith = Math.Cos(apparentZenithDegrees * DegToRad);
            return 1.0 / (cosZenith
                          + 0.50572 * Math.Pow(6.07995 + (90.0 - apparentZenithDegrees), -1.6364));
        }

        /// <summary>
        /// Pressure-corrected (absolute) air mass. This is what the Ineichen model expects.
        /// https://pvlib-python.readthedocs.io/en/v0.9.0/generated/pvlib.atmosphere.get_absolute_airmass.html
        /// </summary>
        public static double AbsoluteAirMass(double apparentZenithDegrees, double pressurePascal)
        {
            return RelativeAirMass(apparentZenithDegrees) * (pressurePascal / StandardPressure);
        }

        /// <summary>
        /// Station pressure estimated from altitude using the ISA standard atmosphere (Pa).
        /// https://www.calculate.co.nz/air-pressure-at-altitude-calculator.php
        /// </summary>
        public static double PressureFromAltitude(double altitudeMeters)
        {
            return StandardPressure * Math.Pow(1.0 - 2.25577e-5 * altitudeMeters, 5.25588);
        }
    }
}
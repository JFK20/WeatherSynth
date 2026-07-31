using System;

namespace WeatherSynth.Solar
{
    /// <summary>
    /// A monthly Linke turbidity climatology for the Ineichen-Perez clear-sky model.
    ///
    /// <para>The Linke turbidity factor describes how much a cloudless atmosphere attenuates
    /// direct sunlight relative to a perfectly clean, dry one. TL = 1 is a pure Rayleigh
    /// atmosphere; real mid-latitude values run roughly 2-7, low in winter and high in summer.
    /// There is no formula for it. It's a lookup, or a fit.</para>
    /// </summary>
    public sealed class LinkeTurbidityTable
    {
        private readonly double[] _monthly;

        /// <param name="monthlyValues">Twelve values, January first.</param>
        public LinkeTurbidityTable(double[] monthlyValues)
        {
            if (monthlyValues is null)
                throw new ArgumentNullException(nameof(monthlyValues));
            if (monthlyValues.Length != 12)
                throw new ArgumentException("Expected 12 monthly values.", nameof(monthlyValues));

            _monthly = (double[])monthlyValues.Clone();
        }

        /// <summary>
        /// Fitted to the DWD Bochum station's own cloudless days 577 days selected by
        /// sunshine duration at least 95% of possible and diffuse fraction below 0.30,
        /// spanning 2009-2026.
        ///
        /// <para>This is preferable to extracting the SoDa/pvlib global grid for the site: a
        /// local fit absorbs the actual aerosol climate, and also quietly absorbs error in the
        /// assumed station altitude, since both act on the clear-sky magnitude.</para>
        ///
        /// <para>Fitted with the Perez enhancement ON the two are not independent, and these
        /// numbers are not valid for the other variant. Fitting with it off drives December to
        /// 1.44 and January to 1.95, below the physical floor, which is part of why the
        /// enhancement is kept. See <see cref="ClearSkyIneichen.Estimate"/>.</para>
        ///
        /// <para>Confidence is uneven: March-October rest on 38-86 clear days each, but
        /// December has only 10 and January 18. Winter values are the noisiest, and winter is
        /// also where no low-air-mass measurements exist to constrain the shape.</para>
        ///
        /// <para>Against the old Central Europe placeholder these are markedly lower in summer
        /// (3.1-3.9 against 4.0-4.2). The placeholder was overstating summer haze, which
        /// depressed the clear-sky ceiling and would have inflated summer Kt.</para>
        /// </summary>
        public static LinkeTurbidityTable BochumFitted { get; } =
            new(
                new[]
                {
                    2.68, // Jan
                    2.72, // Feb
                    2.91, // Mar
                    3.12, // Apr
                    3.14, // May
                    3.38, // Jun
                    3.65, // Jul
                    3.92, // Aug
                    3.42, // Sep
                    3.09, // Oct
                    2.72, // Nov
                    2.31, // Dec
                }
            );

        /// <summary>
        /// The original hand-entered Central/Western Europe climatology. Kept for comparison
        /// only it is not site data and was never fitted to anything.
        /// </summary>
        public static LinkeTurbidityTable CentralEuropePlaceholder { get; } =
            new(new[] { 2.7, 2.9, 3.3, 3.7, 4.0, 4.2, 4.2, 4.1, 3.6, 3.1, 2.8, 2.6 });

        /// <summary>The turbidity for a whole month, 1-12.</summary>
        public double ForMonth(int month)
        {
            if (month < 1 || month > 12)
                throw new ArgumentOutOfRangeException(nameof(month));

            return _monthly[month - 1];
        }

        /// <summary>
        /// Turbidity interpolated smoothly across the year, avoiding the discontinuity that
        /// stepping between monthly values produces at month boundaries. Each monthly value is
        /// anchored at the middle of its month.
        /// </summary>
        public double Interpolated(DateTime date)
        {
            // Fractional position within the month, measured from mid-month.
            int daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
            double midMonth = (daysInMonth + 1) / 2.0;
            double offset = (date.Day - midMonth) / daysInMonth;

            int monthA = date.Month;
            int monthB;
            double weight;

            if (offset >= 0.0)
            {
                monthB = date.Month == 12 ? 1 : date.Month + 1;
                weight = offset;
            }
            else
            {
                monthB = date.Month == 1 ? 12 : date.Month - 1;
                weight = -offset;
            }

            double a = ForMonth(monthA);
            double b = ForMonth(monthB);
            return a + (b - a) * weight;
        }
    }

    /// <summary>Convenience accessors over <see cref="LinkeTurbidityTable"/>.</summary>
    public static class LinkeTurbidity
    {
        /// <summary>
        /// The default provider: the Bochum site fit. Suitable as-is for the Lower Rhine and
        /// Ruhr area; refit for anywhere materially different.
        /// </summary>
        public static double Default(DateTime date) =>
            LinkeTurbidityTable.BochumFitted.Interpolated(date);
    }
}

using System;

namespace WeatherModel.Solar
{
    /// <summary>
    /// Linke turbidity factors for the Ineichen–Perez clear-sky model.
    ///
    /// The Linke turbidity factor describes how much the cloudless atmosphere attenuates
    /// direct sunlight relative to a perfectly clean, dry atmosphere. It varies by site
    /// (aerosols, water vapour, altitude) and by season typically low in winter and
    /// high in summer at mid latitudes.
    ///
    /// IMPORTANT: the values below are a coarse monthly climatology for Central/Western
    /// Europe (roughly 48-54°N, low altitude, semi-rural). They are fine for a stochastic
    /// weather generator, where the Kt sampling dominates the uncertainty, but they are NOT
    /// a substitute for site-specific data. For real values, use the SoDa / MINES ParisTech
    /// global Linke turbidity maps (the same 2003 Remund et al. dataset pvlib ships as a
    /// lookup grid), or fit the factor to your own measured clear-sky days.
    /// </summary>
    public static class LinkeTurbidity
    {
        // Index 0 = January ... index 11 = December.
        private static readonly double[] CentralEuropeMonthly =
        {
            2.7, // Jan
            2.9, // Feb
            3.3, // Mar
            3.7, // Apr
            4.0, // May
            4.2, // Jun
            4.2, // Jul
            4.1, // Aug
            3.6, // Sep
            3.1, // Oct
            2.8, // Nov
            2.6  // Dec
        };

        /// <summary>
        /// Monthly climatological Linke turbidity for Central/Western Europe.
        /// Replace this with site-specific data when you have it.
        /// </summary>
        public static double ForCentralEurope(int month)
        {
            if (month < 1 || month > 12)
                throw new ArgumentOutOfRangeException(nameof(month));

            return CentralEuropeMonthly[month - 1];
        }

        /// <summary>
        /// Smoothly interpolated turbidity across the year, avoiding the discontinuity
        /// you get from stepping between monthly values at month boundaries.
        /// Anchors each monthly value at the middle of its month.
        /// </summary>
        public static double InterpolatedForCentralEurope(DateTime date)
        {
            // Fractional position within the month, measured from mid-month.
            int daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
            double midMonth = (daysInMonth + 1) / 2.0;
            double offset = (date.Day - midMonth) / daysInMonth;

            int monthA, monthB;
            double weight;

            if (offset >= 0.0)
            {
                monthA = date.Month;
                monthB = date.Month == 12 ? 1 : date.Month + 1;
                weight = offset;
            }
            else
            {
                monthA = date.Month;
                monthB = date.Month == 1 ? 12 : date.Month - 1;
                weight = -offset;
            }

            double a = ForCentralEurope(monthA);
            double b = ForCentralEurope(monthB);
            return a + (b - a) * weight;
        }
    }
}
using System;
using System.Collections.Generic;

namespace WeatherSynth.Climate
{
    /// <summary>One calendar month of a generated year, aggregated.</summary>
    /// <param name="Month">Calendar month, 1-12.</param>
    /// <param name="Days">Days generated in it.</param>
    /// <param name="MeanSpeed">Mean of the daily mean speeds, m/s.</param>
    /// <param name="MaxSpeed">Windiest day in the month, m/s.</param>
    /// <param name="MeanCubedSpeed">Mean of the daily mean-cubed speeds, m³/s³.</param>
    public readonly record struct SyntheticWindMonth(
        int Month,
        int Days,
        double MeanSpeed,
        double MaxSpeed,
        double MeanCubedSpeed
    );

    /// <summary>
    /// One generated year at daily resolution, with its monthly and annual aggregates.
    ///
    /// <para>The model's product: a plausible year that never happened. One realisation, not a
    /// forecast and not a climatology - another seed gives an equally plausible year, and the seed
    /// is carried along so any year can be reproduced exactly from <see cref="Year"/> and
    /// <see cref="Seed"/>.</para>
    ///
    /// <para><b>Aggregated by averaging, not by summing</b>, which is the one place this differs
    /// structurally from <see cref="SyntheticSolarYear"/>. A year of irradiance has a total -
    /// annual kWh/m² is the product, and the number a yield estimate starts from. A year of wind
    /// speeds has no total; adding daily speeds together produces a number with no physical
    /// meaning. What a wind year has instead is a mean, a maximum, and - because power goes as the
    /// cube - a mean of cubes.</para>
    /// </summary>
    public sealed class SyntheticWindYear
    {
        /// <summary>
        /// Wraps an already-generated run of days.
        /// </summary>
        /// <param name="year">The calendar year the days belong to.</param>
        /// <param name="seed">Seed the run was drawn with, so it can be reproduced.</param>
        /// <param name="days">The generated days, in date order.</param>
        public SyntheticWindYear(int year, int seed, IReadOnlyList<SyntheticWindDay> days)
        {
            if (days is null)
                throw new ArgumentNullException(nameof(days));
            if (days.Count == 0)
                throw new ArgumentException("A year needs at least one day.", nameof(days));

            Year = year;
            Seed = seed;
            Days = days;

            var months = new List<SyntheticWindMonth>(12);
            var speedSum = new double[13];
            var cubedSum = new double[13];
            var maxima = new double[13];
            var counts = new int[13];

            foreach (var day in days)
            {
                if (day.Date.Year != year)
                    throw new ArgumentException(
                        $"{day.Date:yyyy-MM-dd} does not belong to {year}.",
                        nameof(days)
                    );

                int month = day.Date.Month;
                counts[month]++;
                speedSum[month] += day.MeanSpeed;
                cubedSum[month] += day.MeanCubedSpeed;
                if (day.MeanSpeed > maxima[month])
                    maxima[month] = day.MeanSpeed;

                MeanSpeed += day.MeanSpeed;
                MeanCubedSpeed += day.MeanCubedSpeed;
                if (day.MeanSpeed > MaxSpeed)
                    MaxSpeed = day.MeanSpeed;
            }

            MeanSpeed /= days.Count;
            MeanCubedSpeed /= days.Count;

            for (int month = 1; month <= 12; month++)
            {
                if (counts[month] == 0)
                    continue;

                months.Add(
                    new SyntheticWindMonth(
                        month,
                        counts[month],
                        speedSum[month] / counts[month],
                        maxima[month],
                        cubedSum[month] / counts[month]
                    )
                );
            }

            Months = months;
        }

        /// <summary>The calendar year generated.</summary>
        public int Year { get; }

        /// <summary>
        /// Seed the run was drawn with. Same year, same seed, same site gives the same days -
        /// which is what makes a generated year quotable rather than merely plausible.
        /// </summary>
        public int Seed { get; }

        /// <summary>Every generated day, in date order. 366 entries in a leap year.</summary>
        public IReadOnlyList<SyntheticWindDay> Days { get; }

        /// <summary>Monthly aggregates, ascending. Twelve entries for a whole year.</summary>
        public IReadOnlyList<SyntheticWindMonth> Months { get; }

        /// <summary>
        /// Mean of the year's daily mean speeds, m/s - the figure that compares directly against
        /// the record's own annual mean, and the first thing to check on a suspicious year.
        /// </summary>
        public double MeanSpeed { get; }

        /// <summary>Windiest day of the year, m/s.</summary>
        public double MaxSpeed { get; }

        /// <summary>
        /// Mean of the daily mean-cubed speeds, m³/s³.
        ///
        /// <para><b>This, not <see cref="MeanSpeed"/>, is what an energy estimate scales with.</b>
        /// Cubing the annual mean speed instead understates the year twice over: once for the
        /// within-day variation and again for the day-to-day variation, since E[v³] exceeds
        /// (E[v])³ at every level of averaging.</para>
        /// </summary>
        public double MeanCubedSpeed { get; }

        /// <summary>
        /// How much the year's energy exceeds what its mean speed alone would suggest,
        /// <c>MeanCubedSpeed / MeanSpeed³</c>.
        ///
        /// <para>Larger than any single day's factor, because this one carries the day-to-day
        /// spread as well as the within-day spread.</para>
        /// </summary>
        public double EnergyPatternFactor =>
            MeanSpeed > 0.0 ? MeanCubedSpeed / (MeanSpeed * MeanSpeed * MeanSpeed) : double.NaN;
    }
}

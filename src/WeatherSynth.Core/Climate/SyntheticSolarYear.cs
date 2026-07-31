using System;
using System.Collections.Generic;

namespace WeatherSynth.Climate
{
    /// <summary>One calendar month of a generated year, aggregated.</summary>
    /// <param name="Month">Calendar month, 1-12.</param>
    /// <param name="Days">Days generated in it.</param>
    /// <param name="MeanClearSkyIndex">Mean of the daily indices, unweighted.</param>
    /// <param name="GhiKWhPerM2">Synthetic irradiation for the month.</param>
    /// <param name="ClearSkyKWhPerM2">The month's clear-sky ceiling, for reference.</param>
    public readonly record struct SyntheticSolarMonth(
        int Month,
        int Days,
        double MeanClearSkyIndex,
        double GhiKWhPerM2,
        double ClearSkyKWhPerM2
    );

    /// <summary>
    /// One generated year at daily resolution, with its monthly and annual totals.
    ///
    /// <para>This is the model's product: a plausible year that never happened. It is one
    /// realisation, not a forecast and not a climatology - another seed gives an equally
    /// plausible year, and the seed it was drawn with is carried along so any year can be
    /// reproduced exactly from <see cref="Year"/> and <see cref="Seed"/> alone.</para>
    ///
    /// <para>Materialised rather than streamed, because a year is small (366 days) and callers
    /// asking for one generally want the totals too. <see cref="SyntheticSolarGenerator.Generate"/>
    /// is still there for long runs.</para>
    /// </summary>
    public sealed class SyntheticSolarYear
    {
        /// <summary>
        /// Wraps an already-generated run of days.
        /// </summary>
        /// <param name="year">The calendar year the days belong to.</param>
        /// <param name="seed">Seed the run was drawn with, so it can be reproduced.</param>
        /// <param name="days">The generated days, in date order.</param>
        public SyntheticSolarYear(int year, int seed, IReadOnlyList<SyntheticSolarDay> days)
        {
            if (days is null)
                throw new ArgumentNullException(nameof(days));
            if (days.Count == 0)
                throw new ArgumentException("A year needs at least one day.", nameof(days));

            Year = year;
            Seed = seed;
            Days = days;

            var months = new List<SyntheticSolarMonth>(12);
            var indexSum = new double[13];
            var ghiSum = new double[13];
            var ceilingSum = new double[13];
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
                indexSum[month] += day.ClearSkyIndex;
                ghiSum[month] += day.GhiWhPerM2;
                ceilingSum[month] += day.ClearSkyWhPerM2;

                GhiKWhPerM2 += day.GhiWhPerM2 / 1000.0;
                ClearSkyKWhPerM2 += day.ClearSkyWhPerM2 / 1000.0;
                MeanClearSkyIndex += day.ClearSkyIndex;
            }

            MeanClearSkyIndex /= days.Count;

            for (int month = 1; month <= 12; month++)
            {
                if (counts[month] == 0)
                    continue;

                months.Add(
                    new SyntheticSolarMonth(
                        month,
                        counts[month],
                        indexSum[month] / counts[month],
                        ghiSum[month] / 1000.0,
                        ceilingSum[month] / 1000.0
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
        public IReadOnlyList<SyntheticSolarDay> Days { get; }

        /// <summary>Monthly aggregates, ascending. Twelve entries for a whole year.</summary>
        public IReadOnlyList<SyntheticSolarMonth> Months { get; }

        /// <summary>Synthetic annual irradiation, the number a yield estimate starts from.</summary>
        public double GhiKWhPerM2 { get; }

        /// <summary>The year's clear-sky ceiling, the deterministic upper bound on the above.</summary>
        public double ClearSkyKWhPerM2 { get; }

        /// <summary>
        /// Mean of the daily clear-sky indices, unweighted - the figure that compares directly
        /// against the record's fitted monthly means.
        ///
        /// <para>Not the same as <see cref="GhiKWhPerM2"/> over <see cref="ClearSkyKWhPerM2"/>,
        /// which weights each day by how much energy was available that day and therefore runs
        /// higher: summer days carry more weight and summer is clearer.</para>
        /// </summary>
        public double MeanClearSkyIndex { get; }

        /// <summary>Energy-weighted index: the fraction of the year's available energy delivered.</summary>
        public double ClearSkyFraction =>
            ClearSkyKWhPerM2 > 0.0 ? GhiKWhPerM2 / ClearSkyKWhPerM2 : double.NaN;
    }
}

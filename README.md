# WeatherSynth

A package to create synthetic weather data.

The data itself is realistic, as it is derived from the Clear Sky Index and historical data.

Currently, the data used to create the synthetic data is from Bochum, which is in western Germany.
This data is required without it, the project doesn't work.

**Why Bochum?** Because it is the best station with data that I could find from the DWD. So if your location differs wildly from Bochum, I would change the underlying data.

**This produces synthetic weather, not a forecast.** Every year returned is one statistically
plausible realisation for the site, reproducible from its seed never a prediction of what a
specific real-world date will actually do.

## Installation

```
dotnet add package WeatherSynth
```

That pulls in `WeatherSynth.Core` (the physics) automatically

## Quick start

No files, no fitting: `SyntheticWeather.Default` bundles a model already fitted from the Bochum
(solar) and Essen-Bredeney (wind) records as a few dozen coefficients, and generates from that.

```csharp
using WeatherSynth;

CoupledWeatherYear year = SyntheticWeather.Default.GenerateYear(2027, seed: 4242);

foreach (var day in year.Days)
    Console.WriteLine($"{day.Date} {day.Solar.GhiKWhPerM2:F2} kWh/m²  {day.Wind.MeanSpeed:F1} m/s");
```

`SyntheticWeather.DefaultSolar` and `.DefaultWind` give you just one resource each, if you don't
need both. Everything below this point covers fitting from your own DWD station record instead of
the bundled one.

## Hourly values

Every year from `GenerateYear` also carries its hours, 24 per day. They are computed together
with the days and read back by time range:

```csharp
var year = SyntheticWeather.Default.GenerateYear(2025, seed: 4242);

// The hours *starting* in [06:00, 12:00): six hours, 06:00 to 11:00, covering exactly that span.
foreach (var hour in year.HoursBetween(new DateTime(2025, 1, 1, 6, 0, 0), new DateTime(2025, 1, 1, 12, 0, 0)))
    Console.WriteLine($"{hour.Start:HH:mm} {hour.Solar.GhiWhPerM2:F0} Wh/m²  {hour.Wind.Speed:F1} m/s");

IReadOnlyList<CoupledWeatherHour> all = year.Hours; // 8,760 (8,784 in a leap year)
```

A `DateTime` without a kind is read in the site's time zone, which is UTC for the default site, not
in the server's local zone. The range is clamped to the year, so a span across New Year needs both
years. `SyntheticSolarYear` and `SyntheticWindYear` have the same `Hours` and `HoursBetween`.

Each day's hours start at its local midnight. With a solar site in a daylight-saving zone, the two
switch days are an hour off against their neighbours: in spring two hours share a `Start`, and in
autumn one hour of the timeline has no entry. Don't key a dictionary on `Start` in such a zone. The
default site is in UTC, so every hour is contiguous and unique.

**The daily values do not change.** The hours come from a random stream of their own, so a year
with hours has exactly the days it had before.

- **Solar:** each hour is the day's clear-sky index times that hour's clear-sky ceiling. The shape
  over the day and the daily total are right: the 24 hours add up exactly to the day. There are no
  passing clouds, because every hour of a day has the same cloudiness.
- **Wind:** the hours average exactly to the day's `MeanSpeed`. Their spread comes from the same
  within-day distribution the turbine yield uses, so the mean of the cubed hourly speeds carries the
  day's energy. The hours are ordered by a diurnal cycle fitted per month (windier afternoons,
  strongest in summer) and an hour-to-hour persistence, both fitted from the Essen hourly record.
  Each day is pinned to its own mean, so there can be a step at midnight.

A year's hours are stored as one number per hour and resource, about 140 KB per year. The hour
records are built only when you read them.

## Usage

`SyntheticSolarProvider` is the entry point. Fit once from the station record, then ask it for
as many years as you need:

```csharp
var provider = SyntheticSolarProvider.FromDwdRecord("data/dwd_bochum_solar.csv", DwdSolarStations.Bochum);

// A year at the fitting station, reproducible from (year, seed).
SyntheticSolarYear year = provider.GenerateYear(2026, seed: 42);

foreach (var day in year.Days)
    Console.WriteLine($"{day.Date:yyyy-MM-dd} {day.GhiKWhPerM2:F3} kWh/m²");

Console.WriteLine($"{year.GhiKWhPerM2:F0} kWh/m² over {year.Days.Count} days");

// Somewhere else: the distributions transfer, the geometry does not, so pass the target site.
var köln = new SolarSite(51.02095, 6.89422, altitudeMeters: 50.0);
var elsewhere = provider.GenerateYear(2026, seed: 42, köln);
```

Fitting is the expensive step it reads the whole record so keep the provider around and call
`GenerateYear` per request. The provider is immutable and thread-safe; the generators it hands
out are not. `provider.Generate(start, end, seed)` covers spans that are not calendar years.

Each year is one realisation, never a forecast: the same seed reproduces it exactly, a different
seed is an equally plausible year for that site.

The repo has 3 folders:
1. `tests`: tests (AI-generated by Claude Opus 5)
2. `src`: the logic itself (mostly handwritten)
3. `samples`: things to try out (AI-generated by Claude Opus 5). Run with:
   `dotnet run --project samples/WeatherSynth.Sample -c Release -- <command>`
   - `summary`: coverage, gaps, monthly daily-GHI totals, clear-day counts
   - `kt`: builds the clearness-index dataset and runs the acceptance checks
   - `calibrate`: fits Linke turbidity against measured cloudless days
   - `fit`: fits the monthly Beta distributions and scores them (KS, persistence)
   - `year`: prints one synthetic year at daily resolution `year [year] [seed]`
   - `viz`: writes `viz/index.html`, a self-contained page with a solar/wind/combined switch.
     `viz <years> [seed] [coupled|independent]` projects future years instead of the record's span
   - `couple`: fits the solar-wind coupling and scores it (needs both records)
   - `zenith`: solar position vs. the DWD ZENIT column (151k reference angles)
   - `decompose`: splits the zenith residual into declination vs. hour-angle error
   - `impact`: what the zenith residual costs on daily clear-sky GHI
   - `fitcoords`: recovers station coordinates from ZENIT by residual minimisation
   - `sanity`: the original clear-sky harness (equinox/solstice totals)
   - `windsummary`: wind coverage, gaps, monthly mean speeds, the cube-law correction
   - `windfit`: fits the twelve monthly Weibull distributions and scores them (KS, persistence)
   - `windyear`: prints one synthetic wind year at daily resolution `windyear [year] [seed]`
   - `windpower`: turbine yield, checked against the record's own hourly energy

It is built for .NET 10.0.

It supports Global Radiation, which can be used for PV generation calculations, and daily mean
wind speed. `SyntheticWindProvider` is the wind entry point and works like the solar one:

```csharp
var wind = SyntheticWindProvider.FromDwdRecord("data/dwd_essen_wind.csv", DwdWindStations.EssenBredeney);
SyntheticWindYear year = wind.GenerateYear(2026, seed: 42);

Console.WriteLine($"{year.MeanSpeed:F2} m/s mean, windiest day {year.MaxSpeed:F2} m/s");

// Somewhere higher up. Read the warning below before trusting the result.
var hub = new WindSite(HeightMeters: 100.0, RoughnessLengthMeters: 0.1);
var lifted = wind.GenerateYear(2026, seed: 42, hub);
```

**Two warnings about wind output.** Wind power goes as the cube of speed, so a daily mean speed
is *not* enough for an energy estimate it is low by about 25% at this station. Use
`MeanCubedSpeed`, which is carried for exactly that reason. And the height transfer is a big
source of error: the log law and the power law disagree by 26% over a
15 m → 100 m extrapolation. Generating at the station's own 15 m applies no transfer at all.

For an actual turbine, `MeanCubedSpeed` is still not enough that's why there is a fake power
curve in `TurbinePowerCurve`, which `TurbineYield` integrates over each day to return a daily
energy. It takes 3 as the cut-in speed, 12.5 as the rated speed, and 25 as the cut-out speed. With
2 MW rated power, it returns the daily energy in kWh. It is a very rough estimate, but it is better
than nothing. Keep in mind that the values are just examples fit them to your own needs.

## Coupling the two

Normally a day can't be both sunny and windy, so the two resources are not independent. The
`CoupledWeatherProvider` takes this into account and generates days that pair up the way real ones
do. It needs both records, because it has to measure the correlation between them. That makes it a
bit more realistic. It is an opt-in feature.

```csharp
var both = CoupledWeatherProvider.FromDwdRecords(
    "data/dwd_bochum_solar.csv", DwdSolarStations.Bochum,
    "data/dwd_essen_wind.csv", DwdWindStations.EssenBredeney);

CoupledWeatherYear year = both.GenerateYear(2026, seed: 42);
Console.WriteLine($"{year.Solar.GhiKWhPerM2:F0} kWh/m², {year.Wind.MeanSpeed:F2} m/s");
```

## Two stations

Solar is fitted at **Bochum (DWD 7365)** and wind at **Essen-Bredeney (DWD 01303)**, about 29 km
apart. That split is forced rather than chosen: Bochum carries no wind record at all. Both sit at
roughly 150 m in the same regional weather, which is what makes the pairing defensible but any
coupling measured between the two resources is attenuated by the separation, and so is a lower
bound on the co-located value.

The wind record runs 2009-01-01 to 2025-12-31 at hourly resolution, matching the solar record's
span. Its anemometer is at **15 m** above ground, not the 10 m almost everyone assumes.

## Data attribution

The weather [data](https://opendata.dwd.de/climate_environment/CDC/observations_germany/climate/hourly/solar/) in `data/dwd_bochum_solar.csv` comes from the Deutscher Wetterdienst [DWD](https://opendata.dwd.de/climate_environment/CDC/observations_germany/climate/hourly/solar/DESCRIPTION_obsgermany_climate_hourly_solar_en.pdf) Climate
Data Center and is used under the [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) license.

The wind [data](https://opendata.dwd.de/climate_environment/CDC/observations_germany/climate/hourly/wind/) in `data/dwd_essen_wind.csv` comes from the same source under the same
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) license. It is DWD's hourly wind
product for station 01303, spliced from the `historical/` and `recent/` archives with the overlap
de-duplicated in favour of `historical/`, and restricted to 2009-2025.

© Deutscher Wetterdienst, 2026

## License

The code in this repository is licensed under the [GNU AFFERO GENERAL PUBLIC LICENSE](LICENSE). This does not cover
the DWD data, which is licensed separately as noted above.

`WeatherSynth.Core` depends on [SolarCalculator](https://www.nuget.org/packages/SolarCalculator)
(LGPL-3.0) for solar position primitives, pulled in as an ordinary NuGet package reference. Its
license is included alongside this one wherever WeatherSynth is distributed.

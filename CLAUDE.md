# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build
dotnet test                                              # xUnit + FluentAssertions

dotnet run --project samples/WeatherModel.Sample -c Release -- <command>
#   summary    coverage, gaps, monthly daily-GHI totals, clear-day counts
#   zenith     solar position vs the DWD ZENIT column (151k reference angles)
#   decompose  splits the zenith residual into declination vs hour-angle error
#   impact     what the zenith residual costs on daily clear-sky GHI
#   fitcoords  recovers station coordinates from ZENIT by residual minimisation
#   sanity     the original clear-sky harness (equinox/solstice totals)
```

Always use `-c Release` for the sample: several commands sweep the full 151,871-row record and
are several times slower in Debug. Never pipe them through `tail` while iterating — it buffers
until EOF and you will watch a blank screen for minutes.

Tests are data-aware: the ZENIT validation silently passes if `data/dwd_bochum.csv` is absent,
so it never blocks a build on a machine without the data.

## What this project is

A **stochastic weather generator**, not a forecaster. It produces synthetic daily values that are
statistically and physically plausible for a site — never a prediction of a specific day. Solar
first, wind later on the same skeleton.

`knowledge.md` is the design record: it holds the rationale, the rejected alternatives, the
validation targets, and an open-items checklist. **Read it before making modelling decisions, and
update its open-items list when you close one.** It is the source of truth for *why*; the code is
only the *what*.

## Architecture

The central idea is that irradiance is never modelled directly. It has a hard physical ceiling —
the clear-sky irradiance — so the model works on the bounded **clearness index**:

```
Kt = actual GHI / clear-sky GHI          synthetic GHI = sampled Kt × clear-sky GHI
```

**Two different indices, easily confused** (`Climate/DailyClearness.cs`):

| | denominator | cloudless | Bochum mean | support |
|---|---|---|---|---|
| `ClearSkyIndex` — **model this** | modelled clear-sky | ≈ 1.0 | 0.591 | **[0, ~1.25]** |
| `ClearnessIndex` — cross-check only | extraterrestrial | ≈ 0.75 | 0.410 | [0, 0.85] |

The familiar "Kt runs 0.05–0.75" belongs to the *second* row. A Beta fit on the first must be
scaled to [0, 1.25]: 5.7% of days legitimately exceed 1.0, because the ceiling carries a
monthly-mean turbidity and a cleaner-than-average day beats it. Don't clamp that away.

This splits the system into a deterministic half and a stochastic half:

**`src/WeatherModel.Core/`** — pure, I/O-free, the only place physics lives.
- `Solar/ClearSkyCalculator.cs` — `ClearSkyIneichen.Estimate(...)`, a port of
  `pvlib.clearsky.ineichen`. Instantaneous, W/m². Also hosts the air-mass / pressure /
  extraterrestrial-irradiance helpers.
- `Solar/SolarPositionCalculator.cs` — zenith angles. **Exists because SolarCalculator's
  `SolarDeclination` is broken** (see landmines); it interpolates declination and equation of time
  across the day and rebuilds the zenith. Memoises per-date terms, so it is not thread-safe —
  one instance per thread.
- `Solar/LinkeTurbidity.cs` — atmospheric haze input. Still a **placeholder** Central Europe
  climatology, month-interpolated. Phase 3 replaces it with a site fit.
- `Solar/DailyCSCalculator.cs` — integrates the instantaneous model into Wh/m² (midpoint rule,
  default 15-min step, partial final step weighted by true duration). `IntegrateGhiWhPerM2` covers
  arbitrary intervals, which is what matches the ceiling to observation intervals.

**`src/WeatherModel.Data/`** — everything that touches the outside world. `DwdSolarReader`,
`DwdSolarDay` aggregation, station metadata. References Core; Core never references it.

**Stochastic half (not built yet)** — historical GHI → per-day-of-year (or per-month) Kt
distributions → first-order Markov chain over Kt states (clear / partly / overcast) for day-to-day
persistence → Beta distribution sampled within the state. Beta because its bounded support fits
Kt; a normal distribution produces out-of-range values.

**Ordering constraint:** the clear-sky ceiling must be calibrated and frozen *before* any
distribution is fitted on top of it. Kt is a ratio — change the denominator afterwards and every
fitted distribution silently becomes invalid.

**Fit at Bochum, apply anywhere.** Kt normalises out geometry, so the statistics fitted at the
station transfer to Köln/Gangelt. But the *denominator during fitting* must use Bochum's own
coordinates, not the target site's.

## Landmines specific to this domain

These have already cost time here; `knowledge.md` §5 has the full list.

- **`SolarCalculator.SolarDeclination` is evaluated once per DATE, not per instant.** 2015-03-20 at
  00:00, 06:00, 12:00 and 18:00 UTC all return −0.3732°. Declination moves ~0.39°/day at the
  equinoxes, so this cost 0.178° of zenith RMSE and a 1.9% *seasonal* swing in daily clear-sky GHI.
  Fixed in `SolarPositionCalculator` — do not go back to reading `SolarElevation` directly.
  Diagnostic signature: an annual sinusoid that is zero at both solstices and extreme at the
  equinoxes is a declination problem; one that flips sign between morning and afternoon and
  cancels in the daily mean is an hour-angle problem.
- **`Angle.Degrees` from SolarCalculator is the whole-degree component only** — 47.6° reads as 47.
  Always read `.Radians` and convert manually.
- **One `SolarTimes` instance per timestep.** Sunrise/sunset are time-of-day independent but
  `SolarZenith` is not. Reusing one instance for a whole day yields a constant zenith.
- **Apparent vs. true zenith.** Kasten-Young air mass expects the refraction-corrected zenith. The
  correction is computed locally in `DailyCSCalculator.RefractionCorrectionDegrees` rather than via
  the package's `SolarElevationCorrected`, which is absent from some released versions. Do not
  confuse this (~0.57° at the horizon) with the library's 0.833° sunrise/sunset convention, which
  bundles in the sun's semi-diameter.
- **`np.fmax` is not `Math.Max`.** `np.fmax(NaN, 0)` is 0; `Math.Max(NaN, 0)` is NaN. The port uses
  explicit `double.IsNaN` checks. Related: pvlib's `tl / tl` is a NaN-propagation trick, not
  physics — deliberately not ported.
- **Resolve the UTC offset from noon, not midnight**, or DST-transition days pick the wrong offset.
- **DWD data has its own set** — `-999` means missing (7.4% of rows), radiation is in J/cm² not
  Wh/m², sunshine duration is in minutes not hours, and timestamps mark the interval *end* in
  *true solar time*. `knowledge.md` §9 documents all of it; go through `DwdSolarReader` rather
  than parsing the file again.

## Known deviations from pvlib parity

- `ClearSkyIneichen.Estimate` applies the **Perez enhancement unconditionally**
  (`ClearSkyCalculator.cs`), whereas pvlib's `perez_enhancement` defaults to `False`. This is an
  open decision, not an oversight — the effect grows with air mass, so it matters most for winter
  daily integrals at this latitude. `knowledge.md` calls for computing both variants over a full
  year before committing either way.
- Linke turbidity is placeholder data, so absolute irradiance values are not yet site-accurate.

## Validation

`Programm.cs` is the sanity harness — it prints instantaneous values plus the four
equinox/solstice daily totals. Expected magnitudes for ~51°N, low altitude:

| Check | Expected |
|---|---|
| Clear-sky daily GHI, June solstice | ~8 kWh/m² |
| Clear-sky daily GHI, December solstice | ~1 kWh/m² |
| Peak noon GHI, summer | ~900 W/m² |

Numbers far outside these almost always mean a longitude sign flip, a time-zone mismatch, or
degree/radian confusion — check those three before suspecting the physics.

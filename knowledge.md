# Stochastic Solar Radiation Model — Design Notes

Working notes for a C# library that produces realistic synthetic daily solar
radiation values (no forecasting — plausible weather, not predicted weather).

---

## 1. Project setup

**Project type:** Class Library, not console/web.

```
src/YourLib/          the library
tests/YourLib.Tests/  xUnit or NUnit
samples/              optional console demo
```

`<TargetFrameworks>`, net 9.0

**Packaging csproj properties:** `PackageId`, `Version` (SemVer), `Authors`,
`Description`, `PackageLicenseExpression`, `PackageReadmeFile`, and
`GenerateDocumentationFile` (so XML doc comments surface in IntelliSense).

### Libraries worth knowing

| Package | Role | Notes |
|---|---|---|
| **SolarCalculator** (`Innovative.SolarCalculator`) | Solar position | **Chosen.** NOAA/Meeus method, actively maintained, exposes `SolarZenith` directly. LGPL-3.0. |
| SolCalc | Solar position | Also NOAA-based, sub-arcminute accuracy. |
| SunCalcSharp | Solar position | Port of SunCalc JS. Unmaintained since 2020, low adoption. |
| NREL SPA (C# port) | Solar position | ~0.0003° accuracy, but not on NuGet — you'd vendor source. Overkill here. |
| CoordinateSharp | Geo + solar | Split-licensed: free for OSS/non-commercial, **paid otherwise**. Check before depending. |
| UnitsNet | Typed physical quantities | Avoids ambiguous `double` params for irradiance / temperature / pressure. |
| NodaTime | Date/time | Solar calcs are very sensitive to TZ and DST — the #1 source of bugs in this domain. |

**Dependency weight caution:** public dependencies (NodaTime, UnitsNet) are
inherited by your consumers. A common pattern is to use plain
`DateTimeOffset`/`double` at the public API boundary and richer types internally.

### Why NREL SPA is unnecessary here

The stochastic Kt sampling introduces uncertainty orders of magnitude larger
than the difference between ~1 arcminute and 0.0003° solar position accuracy.
Precision beyond NOAA buys nothing for this use case.

---

## 2. The modelling approach

### The problem with the naive approach

"10-year daily average + random noise" produces physically implausible output:
negative radiation, values exceeding the physical maximum, and unrealistic
day-to-day jumps.

### The fix: model the clearness index, not radiation

Solar radiation has a hard physical ceiling — the **clear-sky irradiance**,
determined purely by sun position and atmosphere. Real radiation is always some
fraction of it:

```
Kt = actual GHI / clear-sky GHI
```

Kt captures "how cloudy was it" largely independently of season or latitude.
**Build the historical distribution and the stochastic model around it, not raw
irradiance.**

> ⚠️ **Correction — two different indices were conflated above.** See §11. The formula
> `GHI / clear-sky GHI` is the **clear-sky index**, which reaches **1.0** on a cloudless day.
> The **0.05–0.75** range quoted here belongs to the *classical clearness index*,
> `GHI / extraterrestrial GHI`. Both are now computed; the clear-sky index is the one modelled.

### Pipeline

1. Compute **clear-sky irradiance** for the target day/location (deterministic).
2. From 10 years of history, build the **Kt distribution** for that day-of-year
   (or month, if per-day samples are too sparse).
3. **Sample** a Kt value.
4. **Multiply:** `synthetic GHI = sampled Kt × clear-sky GHI`.
5. Clamp to `[0, clear-sky]` as a final safety net.

### Day-to-day correlation matters

Cloud cover is autocorrelated — cloudy and clear stretches cluster. I.i.d.
sampling gives unrealistic day-to-day oscillation.

**Fix:** first-order **Markov chain** (the WGEN-style approach used in
agrometeorology). Bucket Kt into states (clear / partly cloudy / overcast),
estimate transition probabilities `P(tomorrow | today)` from history, then
sample the state conditioned on yesterday, and sample Kt within that state.
Large realism gain for little complexity.

### Distribution shape

Use a **Beta distribution** for Kt — its bounded support fits naturally.
A normal distribution will generate out-of-range values.

### Historical data sources

- **PVGIS** — free, Europe-focused, good for this location
- **NASA POWER** — global
- **Open-Meteo historical archive** — global, easy API

**Architectural note:** keep data fetching in a *separate* package from the pure
calculation library. One has no I/O, the other does — this makes testing far easier.

---

## 3. Clear-sky model: Ineichen–Perez (2002)

### Availability

There is **no mature C# NuGet package** implementing clear-sky irradiance models.
Solar *position* is well covered; clear-sky *irradiance* is not. pvlib-python is
the reference implementation for the whole field but is Python-only.

Options considered:
- **Port Ineichen** ← chosen. Closed-form, small, well-validated, best accuracy/cost tradeoff.
- **Haurwitz** — needs only zenith angle, no turbidity data, but less accurate.
- **Call pvlib via Python.NET** — only worth it for the full model catalog.

### pvlib equivalent

```python
pvlib.clearsky.ineichen(apparent_zenith, airmass_absolute, linke_turbidity,
                        altitude=0, dni_extra=1364.0, perez_enhancement=False)
```

Higher-level wrapper: `Location.get_clearsky(times, model='ineichen')`

Docs: https://pvlib-python.readthedocs.io/en/stable/reference/generated/pvlib.clearsky.ineichen.html

| pvlib param | C# port |
|---|---|
| `apparent_zenith` | `apparentZenithDegrees` |
| `airmass_absolute` | computed internally via `AbsoluteAirMass` |
| `linke_turbidity` | `linkeTurbidity` |
| `altitude` | `altitudeMeters` |
| `dni_extra` | `i0` — **units of this determine units of output** |
| `perez_enhancement` | `perezEnhancement` |

### Structure of the GHI equation

```
GHI = cg1 × I0 × cos(zenith) × [transmittance] × (optional Perez enhancement)
```

- `cg1`, `cg2` — empirically fitted coefficients (this is what makes it a
  validated model rather than a textbook exponential)
- `I0` — extraterrestrial irradiance
- `cos(zenith)` — projection onto a horizontal surface
- transmittance — the actual atmospheric physics

### The transmittance term explained

```
exp(-cg2 × airMass × (fh1 + fh2 × (TL - 1)))
```

This is **Beer–Lambert extinction**: `transmittance = exp(−optical depth)`,
where optical depth = airmass (path length) × atmospheric attenuation strength.

The bracket splits attenuation into two physically distinct parts:

- **`fh1 = exp(−altitude/8000)`** — permanent molecular atmosphere (Rayleigh
  scattering by air itself). The 8000 m scale height is the atmospheric scale
  height; shrinks with elevation because there's less air overhead.
- **`fh2 × (TL − 1)`**, `fh2 = exp(−altitude/1250)` — aerosols and water vapour.
  The much shorter 1250 m scale height reflects that haze and moisture
  concentrate in the boundary layer — hence very clear skies at altitude.

**Why `TL − 1`:** Linke turbidity is defined so **TL = 1 is a perfectly clean,
dry Rayleigh atmosphere**. Subtracting 1 isolates the *excess* attenuation above
that ideal baseline — the part actually caused by aerosols and water vapour.
At TL = 1 the second term vanishes and only molecular scattering remains.

### DNI and DHI

DNI takes the **minimum of two independent estimates**. DHI is then derived by
closure: `DHI = GHI − DNI × cos(zenith)`.

---

## 4. Linke turbidity

Describes how much the *cloudless* atmosphere attenuates direct sunlight
relative to a clean dry atmosphere. Typically 2–7; low in winter, high in summer
at mid latitudes.

**There is no formula — it's a lookup.** pvlib provides:

```python
pvlib.clearsky.lookup_linke_turbidity(time, latitude, longitude,
                                      filepath=None, interp_turbidity=True)
```

Docs: https://pvlib-python.readthedocs.io/en/stable/reference/generated/pvlib.clearsky.lookup_linke_turbidity.html

Backed by a global raster of monthly means from SoDa historical data
(Remund et al., "Worldwide Linke Turbidity Information", ISES Solar World
Congress 2003). Stored in the pvlib repo at `pvlib/data/LinkeTurbidities.h5` as a
**2160 × 4320 × 12** byte array — **divide stored values by 20** to recover
actual turbidity.

`interp_turbidity=True` smoothly interpolates between monthly values instead of
stepping at month boundaries — worth replicating.

### Options for C#

1. **Extract the 12 monthly values for your fixed coordinates and hardcode them.**
   Cheapest, and gives real site-specific numbers. ← recommended for a fixed site.
2. Ship the whole H5 as an embedded resource — only if the library must work anywhere.
3. **Fit it yourself** from clear-sky days in your own 10-year dataset — arguably
   most accurate for a single site, absorbs local aerosol conditions the global
   grid averages away.

> The hardcoded Central-Europe monthly table in `LinkeTurbidity.cs` is a
> **placeholder**, not real site data.

---

## 5. Gotchas (the expensive ones)

### `Angle.Degrees` is a trap
On SolarCalculator's `Angle` type, `.Degrees` is the **whole-number degree
component only** — a 47.6° zenith reads as `47`. **Always read `.Radians`** and
convert manually.

### One `SolarTimes` instance per timestep
Sunrise/sunset don't depend on the time-of-day input, but **`SolarZenith` does**.
Reusing a single instance for a whole day gives one constant zenith. The daily
integrator must construct a fresh instance per step.

### `SolarDeclination` is evaluated once per DATE, not per instant

**The most expensive one found so far.** SolarCalculator returns the same declination for every
time of day: 2015-03-20 at 00:00, 06:00, 12:00 and 18:00 UTC all give −0.3732°, which is the
value at midnight. True declination moves ~0.39°/day near the equinoxes, so every afternoon
inherits up to half a day of drift.

Measured against 151,871 reference zenith angles in the DWD Bochum file:

| | RMSE | mean bias | monthly bias | daily clear-sky GHI error |
|---|---|---|---|---|
| Library as-is | 0.178° | +0.002° | ±0.18°, annual sinusoid | 0.56% mean abs, −1.18%/+0.73% seasonal |
| Declination interpolated | 0.100° | −0.000° | ±0.007° | — |
| …and fitted coordinates | **0.033°** | 0.000° | ±0.001° | **0.029% mean abs** |

The signature to recognise: an annual sinusoid in the zenith residual that is **zero at both
solstices and extreme at the equinoxes** is a declination/date problem, because it tracks dδ/dt.
An error that flips sign between morning and afternoon and cancels in the daily mean is an hour
angle problem instead (longitude, equation of time, or timestamp convention).

Fix in `SolarPositionCalculator`: take the library's declination and equation of time at the
surrounding midnights, interpolate to the instant, and rebuild the zenith from
`cos z = sin φ sin δ + cos φ cos δ cos H`. Both quantities are smooth over a day, so linear
interpolation costs well under a thousandth of a degree.

This is worth fixing despite being ~1%, even though Kt sampling dominates the error budget: the
bias is *seasonal*, so it contaminates the exact axis Kt is binned on and would show up as a
real-looking seasonal signal in the fitted distributions.

### `SolarElevationCorrected` may not exist in your package version
It's documented on the project's `master` branch (`AtmosphericRefractionCorrection`,
`SolarElevationCorrected`, col AF) but **is not in all released NuGet versions**.
Solution: compute the NOAA refraction correction yourself from `SolarElevation` —
removes the version dependency entirely.

### Apparent vs. true zenith
The Kasten-Young air mass formula expects the **apparent (refraction-corrected)**
zenith. Refraction is negligible overhead but ~0.57° at the horizon — exactly
where air mass is largest and matters most.

> Don't confuse this with the library's `AtmosphericRefraction` default of
> **0.833°**: that's the sunrise/sunset convention, bundling refraction (~0.57°)
> with the sun's semi-diameter (~0.27°). For air mass you want refraction only.

### `perez_enhancement` defaults to FALSE in pvlib
The `exp(0.01 × airmass^1.8)` term is applied **conditionally**:

```python
ghi = np.exp(-cg2*airmass_absolute*(fh1 + fh2*(tl - 1)))
if perez_enhancement:
    ghi *= np.exp(0.01*airmass_absolute**1.8)
```

Applying it unconditionally silently makes your model `perez_enhancement=True`.
The effect grows with air mass — i.e. it is largest exactly where pvlib warns it
<br>may produce spurious results (sun near horizon, high air mass; see
pvlib-python issue #435). Significant for winter daily integrals at high latitude.

### `tl / tl` in pvlib is NOT physics
It equals 1. It's a **NaN-propagation trick**: airmass NaNs get mapped to 0 via
`np.fmax`, but NaNs from *other* inputs should propagate — multiplying and
dividing by `tl` reinserts turbidity NaNs after the clamp. Nothing to port;
C# `double` NaN semantics propagate naturally.

### `Math.Max` is NOT `np.fmax`
- `np.fmax(NaN, 0)` → **0**
- `Math.Max(double.NaN, 0)` → **NaN**

A literal translation of `fmax` to `Math.Max` lets NaN sail straight through.
Use an explicit `double.IsNaN(x)` check instead.

Note that `fmax` can *only* ever fire on NaN here anyway — `exp()` of any real
number is strictly positive, so it never clamps a genuine negative. Clamping the
final GHI after scaling is mathematically equivalent to pvlib clamping the
transmittance before scaling, since `cg1`, `dni_extra` and `cosZenith` are all
non-negative.

### Fail loudly on bad turbidity
Dropping the `tl / tl` trick means a NaN turbidity silently becomes 0 — a
plausible-looking zero instead of a NaN signalling broken input. Guard against
NaN explicitly at the method boundary.

---

## 6. Integration to daily totals

The Ineichen model is **instantaneous** (W/m²). A daily total (Wh/m²) requires
integrating across the day.

- **Timestep:** 10 minutes is a good default — 144 samples/day, within a fraction
  of a percent of the true integral.
- **Midpoint rule:** sample at the *middle* of each interval, not the boundary.
  Noticeably more accurate around sunrise/sunset where irradiance changes fastest.
- **UTC offset:** resolve it once from **noon**, not midnight — avoids picking up
  the wrong offset on DST transition days.

---

## 7. Validation targets

Sanity checks for ~51°N (Gangelt, NRW), low altitude:

| Check | Expected |
|---|---|
| Clear-sky daily GHI, June solstice | ~8 kWh/m² |
| Clear-sky daily GHI, December solstice | ~1 kWh/m² |
| Peak noon GHI, summer | ~900 W/m² |
| Refraction correction at −0.575° elevation | 0.575° |
| Refraction correction at 0° elevation | 0.482° |

> The "~0.57° at the horizon" figure needs care: it is the correction at **−0.575° geometric
> elevation**, the point where refraction has just lifted the sun onto the visible horizon, which
> makes it self-consistent. At 0° geometric elevation the sun is already above the apparent
> horizon and the correction is only 0.482°. Neither is the 0.833° sunrise/sunset convention.

Numbers wildly outside these almost always mean a **longitude sign flip**, a
**time zone mismatch**, or **degree/radian confusion**.

Beyond self-consistency, validate against **NOAA/NREL published reference tables**
rather than only testing internal consistency.

### Suggested test stack
xUnit (or NUnit) + FluentAssertions.

---

## 8. Open items

- [x] Replace placeholder Linke turbidity with real values — **done**, fitted to 577 measured
  cloudless days from the Bochum record. See §10
- [x] Decide `perez_enhancement` on/off — **done: ON**, though not for the expected reason.
  See §10
- [x] Acquire 10 years of historical GHI — **done**, and better than planned: DWD station 7365
  (Bochum), hourly, 2009-01-01 to 2026-06-30. Ground measurements rather than reanalysis, and
  they carry diffuse radiation and sunshine duration alongside global, which is what makes the
  turbidity fit above possible. See §9
- [x] Build the index dataset and verify it — **done**, 5,779 days. See §11, which corrects two
  errors in the §2 premise (clear-sky index vs clearness index; the distribution is not clearly
  bimodal)
- [ ] Bin the distribution per day-of-year window or per month, and fit
- [ ] Implement Markov-chain state transitions for day-to-day persistence
- [ ] Fit Beta distributions per state — **scaled to [0, 1.25]**, not [0, 1]; see §11
- [ ] Verify LGPL-3.0 of SolarCalculator is acceptable for intended distribution
- [ ] Consider hourly profiles later: shape the day's energy by solar elevation
  (roughly sinusoidal, peaking at solar noon), then add sub-daily cloud
  variability on top

---

## 9. The DWD Bochum dataset

`data/dwd_bochum.csv` — station 7365, hourly solar, 2009-01-01 to 2026-06-30, 151,871 rows.
Read it with `DwdSolarReader`; never re-derive the conventions below by hand.

### Timestamp semantics (the part that costs a day if you get it wrong)

DWD reports these intervals in **true solar time** (wahre Ortszeit, WOZ), not UTC or local clock
time. Interval boundaries are whole WOZ hours, so the accompanying UTC timestamps land on odd
minutes that drift through the year with the equation of time — `00:34`, not `00:00`. That is
correct data, not corruption.

- `MESS_DATUM` — UTC, **interval END**
- `MESS_DATUM_WOZ` — true solar time, interval END, always a whole hour
- **interval midpoint = `MESS_DATUM` − 30 min**, and that is the instant `ZENIT` refers to

Two independent confirmations: the Jan-1 offset (WOZ 01:00 ↔ UTC 00:34 = +28.8 min of longitude
minus 3.5 min of equation of time), and the dataset's minimum `ZENIT` of 28.60°. That minimum
looks like 52.04°N if you assume it lands at solar noon — it does not, because midpoints sit on
WOZ half-hours, so the closest sample to June-solstice noon is 30 minutes off it.

Because WOZ is *true* solar time, the hour angle at each midpoint is exact by definition:
`H = (WOZ hours − 12) × 15°`. That makes the file a reference for validating solar position with
no equation-of-time calculation involved — and lets the published zenith be inverted for the
declination it must have been computed from, separating declination errors from hour-angle ones.

### Columns and units

| Column | Meaning | Note |
|---|---|---|
| `FG_LBERG` | Global horizontal, hourly sum | **J/cm²** |
| `FD_LBERG` | Diffuse horizontal, hourly sum | J/cm² |
| `SD_LBERG` | Sunshine duration | **minutes, 0-60** — not hours |
| `ZENIT` | Zenith at interval midpoint | **geometric**, no refraction correction |
| `QN_592` | Quality level | **always 1** — no quality filtering is possible |
| `ATMO_LBERG` | Longwave counter-radiation | 62% missing, irrelevant to solar |

**`Wh/m² = J/cm² × 2.7778`** (10⁴ J/m² per J/cm², 3600 J per Wh). Confirmed by the monthly
maxima: 8.57 (Jun), 8.86 (Jul), 1.46 (Dec) kWh/m², and 1074 kWh/m²/yr month-weighted.

### `-999` means missing

7.4% of rows (11,217). Reading it as a number is the most damaging available mistake here: one
missing hour drags a daily total down by ~2,775 Wh/m² and can push it negative. `DwdSolarReader`
maps it to null.

### Coverage

- **5,781 of 6,328 days are 100% complete.** Outages are chunky, never scattered — *no* day sits
  between 95% and 99% complete — so a strict completeness filter discards only broken days.
- Worst years by lost daylight hours: 2016 (27.7%), 2018 (25.9%), 2019 (20.0%), 2010 (14.8%).
  Most other years are under 2%.
- **All of December 2023 and December 2024 are absent** (62 days). December is already the
  thinnest month at 401 complete days against May's 545. Anything estimating day-to-day
  transition probabilities must treat these as chain breaks rather than bridge them.
- **651-785 days are effectively cloudless** (≥95% sunshine, mean diffuse fraction 0.23-0.25),
  515 of them Mar-Oct. This is the clear-sky calibration set.

### Station coordinates are FITTED, not looked up

`DwdStations.Bochum` holds 51.4445°N 7.3852°E, obtained by minimising the residual against the
ZENIT column. A nominal "Bochum" position (51.4885°N 7.1927°E) triples the residual from 0.033°
to 0.100°. The longitude is corroborated independently of any zenith calculation, since
`longitude = (WOZ − UTC − equation of time) × 15` gives the same +0.19° shift.

The latitude rests on the zenith fit alone, so it may be absorbing a small residual algorithm
bias rather than reflecting geography. That is acceptable here: what matters is that the
clear-sky ceiling uses the same geometry DWD used for the measurements it will be divided into.

Altitude cannot be recovered from zenith angles and stays a Ruhr-area estimate (150 m). Errors
in it are largely absorbed by the fitted Linke turbidity, since both scale the clear-sky
magnitude.

---

## 10. Clear-sky calibration: turbidity and the Perez question

Fitted on 577 cloudless days (complete, sunshine ≥95% of possible, diffuse fraction <0.30),
minimising squared error against measured hourly irradiation, sun above 5°.

### Fitted turbidity (`LinkeTurbidityTable.BochumFitted`)

| | J | F | M | A | M | J | J | A | S | O | N | D |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Fitted (Perez on) | 2.68 | 2.72 | 2.91 | 3.12 | 3.14 | 3.38 | 3.65 | 3.92 | 3.42 | 3.09 | 2.72 | 2.31 |
| Old placeholder | 2.7 | 2.9 | 3.3 | 3.7 | 4.0 | 4.2 | 4.2 | 4.1 | 3.6 | 3.1 | 2.8 | 2.6 |

The placeholder was overstating summer haze by ~0.5-0.9. That depressed the summer clear-sky
ceiling and would have inflated summer Kt — a seasonal artefact in exactly the wrong place.

Clear-day counts are uneven: 38-86 per month for March-October, but only 18 in January and 10 in
December. Winter values are the noisiest and should be revisited if more winters arrive.

### How the Perez question actually resolved

The planned discriminator — residual flatness against air mass — turned out **not** to settle it,
and two traps had to be cleared first:

1. **Search-range clamping.** With the enhancement off, the December fit ran to the bottom of the
   search range. Setting the floor at a "physically plausible" 1.5 hid this; lowering it to 1.0
   revealed the fit actually wanted 1.44.
2. **Air mass is confounded with season.** Turbidity is fitted per month, and at 51°N the sun
   never exceeds ~15° in December, so winter is entirely high-air-mass. A pooled air-mass profile
   is therefore partly a seasonal profile. The tilt has to be measured **within** each month,
   where turbidity is a single constant — and winter months then cannot discriminate at all,
   having no low-air-mass samples to contrast against.

With that fixed, the within-month tilt (mean residual at air mass >3 minus at <2, over the eight
months that can discriminate):

| | mean \|tilt\| | direction |
|---|---|---|
| Perez on | 4.63% | over-predicts at high air mass |
| Perez off | 3.78% | under-predicts at high air mass |

**Neither variant is right — the truth lies between them.** Perez-on over-predicting at high air
mass is an empirical confirmation of pvlib's own warning (issue #435) about that term.

And on daily totals, the quantity Kt actually divides by, the two are **indistinguishable**:

| | bias | mean \|error\| |
|---|---|---|
| Perez on | +0.20% | 3.39% |
| Perez off | +0.04% | 3.45% |

That is expected: per-month turbidity fitting absorbs any constant offset, and the high-air-mass
hours where the variants differ carry little of a day's energy.

**Decision: Perez ON**, because the deciding evidence ended up being turbidity plausibility rather
than residual shape. Turned off, the fit demands TL of 1.44 in December, 1.95 in January and 2.02
in November — at or below the Rayleigh-atmosphere floor, and implausible for the Ruhr. Since
every known bias in the fit (hazy days slipping through the clear filter, altitude error) pushes
turbidity *up*, values that low indicate the model is compensating for something. The fitted
table is also a documented, inspectable artefact; one full of 1.4s would misdescribe the
atmosphere even if it happened to reproduce the totals.

**Revisit if intra-day profiles become the focus** (§8's last open item): there the within-day
shape is the whole point, Perez-off has the flatter air-mass residual, and the coupling means
turbidity must be refitted alongside any switch.

### The 3.4% floor

Mean absolute error on clear-day daily totals is ~3.4% for both variants. This is *irreducible*
with a monthly climatology — it is real day-to-day variation in atmospheric turbidity that twelve
numbers cannot represent. It sets a practical floor on how sharply Kt can be defined, and is
worth remembering before chasing smaller effects elsewhere.

---

## 11. The Kt dataset — and two corrections to the premise

Built over 5,779 days (5,781 complete, less 2 sensor outages). `ClearnessIndexBuilder`.

### Correction 1: clear-sky index ≠ clearness index

§2 gave the clear-sky-index *formula* with the clearness-index *range*. They differ by the
atmosphere's own transmittance, and both are now on `DailyClearness`:

| | denominator | cloudless day | Bochum annual mean | Bochum range |
|---|---|---|---|---|
| **Clear-sky index** (modelled) | modelled clear-sky | **≈ 1.0** | **0.591** | 0.06 – 1.19 |
| Classical **Kt** (cross-check) | extraterrestrial | ≈ 0.75 | 0.410 | – 0.83 |

The classical column lands exactly on the 0.05–0.75 / mean-0.4–0.5 figures §2 quoted, which is
what identified the mix-up.

**Model the clear-sky index.** It divides out solar geometry *and* the turbidity climatology,
where the classical index leaves a seasonal signal behind — clear-sky transmittance itself varies
with sun elevation, so classical Kt on cloudless days runs ~0.75 in summer but far lower in
winter. That residual structure is exactly what the normalisation is supposed to remove.

**Consequence for fitting:** support is **[0, ~1.25]**, not [0, 1]. A Beta distribution must be
scaled to that range. **5.7% of days legitimately exceed 1.0** (max 1.185), spread evenly through
the year at 1.02–1.09. This is not a broken ceiling: turbidity in the model is a monthly mean, so
a day cleaner than its month's average genuinely beats it — consistent with the ±3.4% clear-day
scatter from §10. Clamping to 1.0 would throw away real signal at the clear end.

### Correction 2: the distribution is NOT clearly bimodal

§2 assumed clear and overcast days form separate lobes, and used that to justify discrete Markov
states. Measured, the deepest valley between any two peaks is only **15% below the lower peak** —
the distribution is a broad plateau from about 0.2 to 0.9 with a rise at the clear end, not two
lobes.

Beware naive tests here: counting local maxima finds "peaks" one bin apart on a flat, noisy
distribution and reports bimodality that is not there. Require a valley with real depth.

Discrete states are still a reasonable way to capture day-to-day persistence — but the
justification is persistence, not natural lobes, and **the state boundaries are an arbitrary
modelling choice**. Worth checking whether a continuous autoregressive model on the index beats a
3-state chain before committing.

### Data-quality landmine: outages recorded as valid zeros

**2026-03-28 reads exactly 0.0 for every hour**, including midday at a zenith of 48.8°, with
sunshine duration 0. That is a sensor outage written as valid zeros rather than as `-999`, so
neither the missing-value handling nor the completeness check catches it — and `QN_592` is 1 for
every row in this record, so quality flags offer nothing. Real overcast still delivers a few
percent of clear-sky, never zero.

`DwdSolarDay.HasImplausibleZeros` flags it: any hour reading exactly zero with the sun above 10°.
Two days in the record. Filter on it in addition to `IsComplete` — they describe different
problems, one missing data and one wrong data.

### Verified genuine, despite looking wrong

**2022-07-24** reaches classical Kt 0.828 and clear-sky index 1.184 — above every textbook
ceiling. It is real: a textbook symmetric bell peaking at 361 J/cm² (1003 W/m², the dataset
maximum), 60/60 sunshine minutes every hour, and a noon diffuse fraction of 0.10 against a
typical clear-day 0.15–0.25. Exceptionally clean dry air during the July 2022 heatwave. Do not
filter it out — it is the upper tail the model should reproduce.

---

## References

- Ineichen, P. & Perez, R. (2002). "A new airmass independent formulation for
  the Linke turbidity coefficient." *Solar Energy* 73(3), 151–157.
- Reno, M., Hansen, C. & Stein, J. "Global Horizontal Irradiance Clear Sky
  Models: Implementation and Analysis." Sandia National Laboratories,
  SAND2012-2389.
- Kasten, F. & Young, A.T. (1989). Relative optical air mass formula.
- Remund et al. (2003). "Worldwide Linke Turbidity Information."
  ISES Solar World Congress.
- Meeus, J. *Astronomical Algorithms* — basis of SolarCalculator.
- NOAA Solar Calculations spreadsheet:
  http://www.esrl.noaa.gov/gmd/grad/solcalc/calcdetails.html
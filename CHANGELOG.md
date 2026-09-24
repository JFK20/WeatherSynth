# Changelog

## 0.3.0

**Hourly values.** Every generated year now also carries its hours:

- `CoupledWeatherYear`, `SyntheticSolarYear` and `SyntheticWindYear` gain `Hours` and
  `HoursBetween(start, endExclusive)` (`DateTimeOffset` and `DateTime` overloads).
- New records: `CoupledWeatherHour`, `SyntheticSolarHour`, `SyntheticWindHour`.
- Solar hours add up exactly to their day, and wind hours average exactly to their day.
- The wind hours follow a diurnal cycle and an hourly persistence fitted from the Essen record.
  These are new bundled coefficients.

Generated daily output is unchanged: the hours use their own random stream. The change is
additive, so nothing was removed or renamed.

## 0.2.0

**Upgrading from 0.1:** three public types were renamed. Generated output is unchanged.

| 0.1 | 0.2 |
|---|---|
| `DwdStation` / `DwdStations` | `DwdSolarStation` / `DwdSolarStations` |
| `DwdTurbines` | `ReferenceTurbines` |
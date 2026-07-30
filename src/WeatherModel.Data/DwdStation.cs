namespace WeatherModel.Data;

/// <summary>Location metadata for a DWD measurement station.</summary>
/// <param name="Id">DWD station identifier (STATIONS_ID).</param>
/// <param name="Name">Station name.</param>
/// <param name="LatitudeDegrees">Latitude, north positive.</param>
/// <param name="LongitudeDegrees">Longitude, east positive.</param>
/// <param name="AltitudeMeters">Station elevation above sea level.</param>
public sealed record DwdStation(
    int Id,
    string Name,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double AltitudeMeters);

/// <summary>Stations this project has data for.</summary>
public static class DwdStations
{
    /// <summary>
    /// Bochum, North Rhine-Westphalia. Hourly solar record from 2009-01-01.
    ///
    /// <para><b>These coordinates are fitted from the file itself, not taken from a gazetteer.</b>
    /// Minimising the residual against the 151,871 ZENIT values puts the station at
    /// 51.4445°N 7.3852°E (RMSE 0.033°), roughly 0.19° east of where a nominal "Bochum"
    /// position would put it — about 13 km. Using the nominal position instead triples the
    /// residual to 0.100°.</para>
    ///
    /// <para>The longitude is corroborated independently of any zenith calculation:
    /// MESS_DATUM_WOZ is true solar time, so <c>longitude = (WOZ − UTC − equation of time) × 15</c>,
    /// and that yields the same +0.19° shift. The latitude rests on the zenith fit alone, so it
    /// could in principle be absorbing a small residual bias in the solar-position algorithm
    /// rather than reflecting geography.</para>
    ///
    /// <para>What matters for this project is that the clear-sky ceiling is computed with the
    /// same geometry DWD used for the measurements it will be divided into, and fitted
    /// coordinates deliver that whatever the true postal address.</para>
    ///
    /// <para>Altitude cannot be recovered from zenith angles and remains a Ruhr-area estimate.
    /// Any error in it is largely absorbed by the fitted Linke turbidity, since both act on the
    /// clear-sky magnitude.</para>
    /// </summary>
    public static readonly DwdStation Bochum = new(
        Id: 7365,
        Name: "Bochum",
        LatitudeDegrees: 51.4445,
        LongitudeDegrees: 7.3852,
        AltitudeMeters: 150.0);
}

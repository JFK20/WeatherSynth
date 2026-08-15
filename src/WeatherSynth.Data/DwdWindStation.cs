using WeatherSynth.Wind;

namespace WeatherSynth.Data;

/// <summary>
/// Location metadata for a DWD wind measurement station.
///
/// <para>Separate from <see cref="DwdStation"/> rather than an extension of it, because of one
/// field: <see cref="AnemometerHeightMeters"/>. Wind speed is only meaningful together with the
/// height it was measured at, and a record with no field for that height invites the 10 m
/// default that almost everyone assumes and that is wrong here.</para>
/// </summary>
/// <param name="Id">DWD station identifier (STATIONS_ID).</param>
/// <param name="Name">Station name.</param>
/// <param name="LatitudeDegrees">Latitude, north positive.</param>
/// <param name="LongitudeDegrees">Longitude, east positive.</param>
/// <param name="AltitudeMeters">Station elevation above sea level.</param>
/// <param name="AnemometerHeightMeters">
/// Height of the anemometer above ground. Every speed in the record - and so every fitted A and
/// γ - belongs to this height and to no other.
/// </param>
/// <param name="RoughnessLengthMeters">
/// Aerodynamic roughness length of the terrain around the station, in metres. An estimate, not a
/// measurement - see <see cref="DwdWindStations.EssenBredeney"/> for what that is worth here.
/// </param>
public sealed record DwdWindStation(
    int Id,
    string Name,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double AltitudeMeters,
    double AnemometerHeightMeters,
    double RoughnessLengthMeters
)
{
    /// <summary>
    /// This station's anemometer as a wind site: the reference every transfer starts from.
    ///
    /// <para>Generating here rather than somewhere else gives a transfer factor of exactly 1.0, so
    /// the roughness estimate above costs nothing until a caller actually asks to move the speeds
    /// to another height.</para>
    /// </summary>
    public WindSite ToSite() => new(AnemometerHeightMeters, RoughnessLengthMeters);
}

/// <summary>Wind stations this project has data for.</summary>
public static class DwdWindStations
{
    /// <summary>
    /// Essen-Bredeney, North Rhine-Westphalia. Hourly wind record, 2009-01-01 to 2025-12-31.
    ///
    /// <para><b>A different station from the solar fit, and that is deliberate.</b> Bochum 7365
    /// carries no wind record at all, so the two halves of this project are fitted ~29 km apart:
    /// solar at Bochum, wind here. Both sit at about 150 m in the same regional weather, which is
    /// what makes the pairing defensible - but it does attenuate any measured coupling between
    /// the two resources, and a coupling fitted across this separation is a lower bound on the
    /// co-located one.</para>
    ///
    /// <para><b>The anemometer is at 15 m, not the ubiquitous 10 m.</b> It has also moved over
    /// the station's life: 18 m (1963-65), 15.4 m (1965-85), 16 m (1985-2000) and 15 m since
    /// 2000-07-26. It is constant across the 2009-2025 fitting span, which is one of the three
    /// reasons for that start date - the others being the MEZ-to-UTC timestamp change at
    /// 2003-09-01, and matching the solar record's span exactly.</para>
    ///
    /// <para>Coordinates are DWD's own station-list values, not fitted: unlike the solar record,
    /// nothing in a wind file constrains position, and nothing downstream of the fit depends on
    /// it. Position matters here only for judging how far the statistics can be carried.</para>
    ///
    /// <para><b>The roughness length is an estimate, and the weakest number in this record.</b>
    /// Bredeney is a leafy suburb and the station sits in parkland, which puts it somewhere in
    /// 0.3-0.5 m - between the suburban class (0.4) and something more open. 0.3 is the parkland
    /// end of that bracket. Nothing derived from the record itself constrains it, unlike the
    /// anemometer height, and the honest reading is that a transferred speed inherits an
    /// uncertainty of several percent from this one number alone. It affects only transferred
    /// results: generating at the station's own height leaves it unused.</para>
    /// </summary>
    public static readonly DwdWindStation EssenBredeney = new(
        Id: 1303,
        Name: "Essen-Bredeney",
        LatitudeDegrees: 51.4041,
        LongitudeDegrees: 6.9677,
        AltitudeMeters: 150.0,
        AnemometerHeightMeters: 15.0,
        RoughnessLengthMeters: 0.3
    );
}

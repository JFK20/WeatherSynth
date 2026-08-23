namespace WeatherSynth;

/// <summary>
/// Worked turbine examples, so the sample and the tests share one definition rather than three
/// copies of a magic 12.5.
/// </summary>
/// <remarks>
/// <para>These are <b>representative, not any particular manufacturer's product.</b> Real power
/// curves are published per model and per air density, and a yield figure quoted for a real project
/// should use the real table through <see cref="TurbinePowerCurve.FromTable"/>. What these are for
/// is having something concrete to integrate while the modelling is being checked.</para>
///
/// <para>Named after the station convention in <see cref="DwdWindStations"/> only in spirit - a
/// turbine has nothing to do with DWD. They live here because this is the assembly that already
/// carries reference data, and Core stays free of it.</para>
/// </remarks>
public static class DwdTurbines
{
    /// <summary>
    /// A 2 MW onshore machine on the idealised curve: cut-in 3, rated 12.5, cut-out 25 m/s.
    ///
    /// <para>The default worked example, and the one every measured figure in knowledge.md §16 was
    /// computed against. Roughly a mid-2000s German onshore installation - modern machines have
    /// larger rotors and reach rated at lower speeds, which matters a great deal at a site as
    /// sheltered as this one.</para>
    /// </summary>
    public static TurbinePowerCurve Generic2Mw { get; } =
        TurbinePowerCurve.Idealised(
            cutInMetersPerSecond: 3.0,
            ratedMetersPerSecond: 12.5,
            cutOutMetersPerSecond: 25.0,
            ratedPowerKilowatts: 2000.0
        );

    /// <summary>
    /// The same nameplate as <see cref="Generic2Mw"/> in tabulated form, with the rounded shoulder
    /// near rated that the idealisation cannot represent.
    ///
    /// <para>Here so the idealisation can be checked against something rather than trusted, the way
    /// <c>WindProfile.PowerLaw</c> exists to check the log law. The two disagree most in the 8-12
    /// m/s band, which at a low-wind site is where a large share of the energy sits.</para>
    /// </summary>
    public static TurbinePowerCurve Tabulated2Mw { get; } =
        TurbinePowerCurve.FromTable(
            new[]
            {
                (2.0, 0.0),
                (3.0, 0.0),
                (4.0, 66.0),
                (5.0, 152.0),
                (6.0, 280.0),
                (7.0, 457.0),
                (8.0, 690.0),
                (9.0, 978.0),
                (10.0, 1296.0),
                (11.0, 1598.0),
                (12.0, 1818.0),
                (13.0, 1935.0),
                (14.0, 1980.0),
                (15.0, 1995.0),
                (16.0, 2000.0),
                (20.0, 2000.0),
                (25.0, 2000.0),
                (25.5, 0.0),
            }
        );

    /// <summary>
    /// A hub the transferred figures in the sample are quoted at: 100 m over farmland-with-hedges
    /// roughness.
    ///
    /// <para>A yield at the 15 m anemometer is arithmetically correct and practically meaningless -
    /// no turbine stands there. This is the site the reports lead with, and the roughness is a
    /// deliberate step down from the station's own 0.3 m because a turbine is sited in the open,
    /// not in the parkland the anemometer sits in.</para>
    /// </summary>
    public static WindSite HundredMetreHub { get; } =
        new WindSite(HeightMeters: 100.0, RoughnessLengthMeters: 0.1);
}

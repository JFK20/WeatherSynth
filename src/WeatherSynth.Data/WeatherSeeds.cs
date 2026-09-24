namespace WeatherSynth;

/// <summary>
/// Seeds for driving more than one generator from a single caller-visible number.
///
/// <para>Only one operation, and it exists because the obvious thing is wrong in a way that
/// leaves no trace in any per-resource diagnostic.</para>
/// </summary>
public static class WeatherSeeds
{
    /// <summary>
    /// Two independent stream seeds derived from one.
    ///
    /// <para><b>Two independent streams need two seeds.</b> Handing the same seed to
    /// <see cref="SyntheticSolarProvider"/> and <see cref="SyntheticWindProvider"/> makes them
    /// draw the <i>same</i> standard normals in the same order, so the two latent chains move in
    /// lockstep and the resources come out strongly <b>positively</b> correlated - measured at
    /// +0.75 against a real-world -0.22. Each half still looks perfect on its own: the
    /// marginals, the persistence and the annual totals are all exactly right, and only the
    /// pairing is nonsense. Nothing a single-resource report prints can see it.</para>
    ///
    /// <para>Derived from the caller's seed rather than picked, so one number still reproduces
    /// the whole run.</para>
    ///
    /// <para>This is for <i>independent</i> generation. If the two resources should actually
    /// depend on each other - windy days coming out cloudy - that is
    /// <see cref="CoupledWeatherProvider"/>, which takes a single seed and must: two seeds
    /// there would be two independent streams again, which is the thing it exists to fix.</para>
    /// </summary>
    /// <param name="seed">The caller's seed. The same value always yields the same pair.</param>
    /// <returns>One seed for the solar stream and one for the wind stream.</returns>
    public static (int Solar, int Wind) Split(int seed)
    {
        var streams = new Random(seed);

        return (streams.Next(), streams.Next());
    }
}

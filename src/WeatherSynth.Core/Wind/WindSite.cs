using System;

namespace WeatherSynth.Wind
{
    /// <summary>
    /// A place to generate for: a height above ground and the roughness of the terrain around it.
    ///
    /// <para>The wind counterpart of <see cref="WeatherSynth.Solar.SolarSite"/>, and it plays the
    /// same role - the half of the model that is <i>not</i> transferable. The fitted speed
    /// distributions belong to one anemometer at one height; this is what carries them somewhere
    /// else.</para>
    ///
    /// <para><b>Position is deliberately absent.</b> A solar site needs latitude and longitude
    /// because the ceiling is geometry; a wind site does not, because nothing here is computed
    /// from coordinates. What makes a wind fit transferable to a place is that the place shares
    /// the fitting station's wind climate, and that is a judgement the caller makes - not
    /// something this type could check from a latitude.</para>
    /// </summary>
    /// <param name="HeightMeters">
    /// Height above ground, in metres. The anemometer height when describing a measurement, the
    /// hub height when describing a turbine.
    /// </param>
    /// <param name="RoughnessLengthMeters">
    /// Aerodynamic roughness length of the surrounding terrain, in metres. Standard classes:
    /// <b>0.0002</b> open water, <b>0.03</b> open farmland, <b>0.1</b> farmland with hedges,
    /// <b>0.4</b> suburban, <b>1.0</b> urban or forest.
    ///
    /// <para>An estimate rather than a measurement, always. It stands in for everything upwind of
    /// the site, and a class either side is an ordinary amount of uncertainty.</para>
    /// </param>
    public sealed record WindSite(double HeightMeters, double RoughnessLengthMeters)
    {
        /// <summary>Height above ground, in metres. Always greater than the roughness length.</summary>
        public double HeightMeters { get; init; } =
            Validated(HeightMeters, RoughnessLengthMeters);

        /// <summary>Aerodynamic roughness length of the terrain, in metres. Always positive.</summary>
        public double RoughnessLengthMeters { get; init; } =
            RoughnessLengthMeters > 0.0
                ? RoughnessLengthMeters
                : throw new ArgumentOutOfRangeException(
                    nameof(RoughnessLengthMeters),
                    RoughnessLengthMeters,
                    "Roughness length must be positive; a perfectly smooth surface has none."
                );

        /// <summary>
        /// The guard that matters, per knowledge.md §14.
        ///
        /// <para>At <c>z = z0</c> the logarithm is zero, so the transfer factor is either zero or a
        /// division by zero; below <c>z0</c> it goes negative and the model returns a negative wind
        /// speed with no other sign of trouble. The log law is a surface-layer relation and simply
        /// does not describe the air down among the roughness elements.</para>
        /// </summary>
        private static double Validated(double heightMeters, double roughnessLengthMeters)
        {
            if (!(heightMeters > 0.0))
                throw new ArgumentOutOfRangeException(
                    nameof(heightMeters),
                    heightMeters,
                    "Height above ground must be positive."
                );

            if (heightMeters <= roughnessLengthMeters)
                throw new ArgumentOutOfRangeException(
                    nameof(heightMeters),
                    heightMeters,
                    $"Height must exceed the roughness length ({roughnessLengthMeters:F3} m). "
                        + "Below it the logarithmic profile is undefined and the transfer would "
                        + "silently return nonsense."
                );

            return heightMeters;
        }

        /// <summary>
        /// The factor a speed measured at <paramref name="reference"/> must be multiplied by to
        /// apply here.
        ///
        /// <para>The single definition of the height transfer, the way
        /// <c>SolarSite.CreateCeiling()</c> is the single definition of the clear-sky ceiling.
        /// Returns exactly 1.0 when this site and the reference are the same place, so generating
        /// at the fitting station costs nothing and carries none of the profile's uncertainty.</para>
        ///
        /// <para><b>Read <see cref="WindProfile"/> before trusting a transferred number.</b> This
        /// multiplication is the largest source of error in the whole pipeline - far larger than
        /// anything in the fitted distributions - and it is at its worst exactly where it is most
        /// wanted, extrapolating from a low anemometer to a hub height.</para>
        /// </summary>
        /// <param name="reference">The site the speeds were measured at.</param>
        /// <param name="profile">The profile law; defaults to <see cref="WindProfile.LogLaw"/>.</param>
        public double TransferFactorFrom(WindSite reference, WindProfile? profile = null)
        {
            if (reference is null)
                throw new ArgumentNullException(nameof(reference));

            return (profile ?? WindProfile.LogLaw).Factor(reference, this);
        }
    }
}

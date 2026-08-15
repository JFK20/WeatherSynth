using System;

namespace WeatherSynth.Wind
{
    /// <summary>
    /// How wind speed changes with height above ground: the rule that carries a speed measured at
    /// one height to another.
    ///
    /// <para><b>This is where essentially all the error in a wind resource estimate lives</b>, and
    /// it dwarfs everything in the distribution fitting upstream of it. A single roughness length
    /// and a single profile, applied to daily means, ignores atmospheric stability - the profile is
    /// markedly steeper on a clear night than on a windy afternoon - the diurnal cycle in the
    /// exponent, and the terrain upwind of the target, which is what the roughness length is
    /// standing in for. The solar half of this library has an irreducible floor of about 3.4% from
    /// its monthly-mean turbidity; the wind equivalent is here and is far larger, especially
    /// extrapolating from a 15 m anemometer to a 100 m hub.</para>
    ///
    /// <para>Two forms, behind one type so they can be compared rather than argued about. The log
    /// law is the default and is what the European Wind Atlas and WAsP use; the power law is the
    /// engineering shorthand.</para>
    /// </summary>
    public abstract class WindProfile
    {
        /// <summary>
        /// The logarithmic profile: <c>factor = ln(z/z0_target) / ln(z_ref/z0_ref)</c>.
        ///
        /// <para>The default, and the one to use unless there is a reason not to. It follows from
        /// surface-layer similarity theory under neutral stability, and its roughness length is a
        /// real, tabulated property of the terrain rather than a fitted constant.</para>
        /// </summary>
        public static readonly WindProfile LogLaw = new LogarithmicProfile();

        /// <summary>
        /// The power-law profile: <c>factor = (z / z_ref)^alpha</c>.
        ///
        /// <para><b>Ignores roughness entirely</b> - a caller who carefully sets a roughness length
        /// and then selects this profile will find it changes nothing, which is worth knowing
        /// before it is discovered as a bug. The terrain enters through the exponent instead, and
        /// only loosely.</para>
        ///
        /// <para>Here mainly so the log law can be checked against something. Where the two
        /// disagree materially, that disagreement is a fair estimate of how much the height
        /// transfer is really worth.</para>
        /// </summary>
        /// <param name="exponent">
        /// The shear exponent alpha. The 1/7 default is the open-terrain rule of thumb; rougher
        /// sites run higher, 0.2-0.3 over suburbs and forest.
        /// </param>
        public static WindProfile PowerLaw(double exponent = 1.0 / 7.0) =>
            new PowerLawProfile(exponent);

        /// <summary>
        /// The factor a speed measured at <paramref name="reference"/> is multiplied by to reach
        /// <paramref name="target"/>. Exactly 1.0 when the two describe the same place.
        /// </summary>
        public abstract double Factor(WindSite reference, WindSite target);

        private sealed class LogarithmicProfile : WindProfile
        {
            public override double Factor(WindSite reference, WindSite target)
            {
                if (reference is null)
                    throw new ArgumentNullException(nameof(reference));
                if (target is null)
                    throw new ArgumentNullException(nameof(target));

                // Both sites validate height > roughness at construction, so both logarithms are
                // strictly positive here and the ratio cannot go negative or divide by zero.
                return Math.Log(target.HeightMeters / target.RoughnessLengthMeters)
                    / Math.Log(reference.HeightMeters / reference.RoughnessLengthMeters);
            }

            public override string ToString() => "log law";
        }

        private sealed class PowerLawProfile : WindProfile
        {
            private readonly double _exponent;

            public PowerLawProfile(double exponent)
            {
                if (double.IsNaN(exponent) || exponent < 0.0)
                    throw new ArgumentOutOfRangeException(
                        nameof(exponent),
                        exponent,
                        "Shear exponent must be zero or positive; wind does not slow with height."
                    );

                _exponent = exponent;
            }

            public override double Factor(WindSite reference, WindSite target)
            {
                if (reference is null)
                    throw new ArgumentNullException(nameof(reference));
                if (target is null)
                    throw new ArgumentNullException(nameof(target));

                return Math.Pow(target.HeightMeters / reference.HeightMeters, _exponent);
            }

            public override string ToString() =>
                $"power law (alpha {_exponent:F3})";
        }
    }
}

namespace FourTrac.Core.Inverse;

/// <summary>Parametric prior family for one sampled quantity.</summary>
public enum PriorDistribution
{
    /// <summary>Continuous uniform on [A, B].</summary>
    Uniform,

    /// <summary>Normal with mean A and standard deviation B.</summary>
    Normal,
}

/// <summary>
/// One sampled quantity's prior (PLAN.md §5 input panels).
/// Uniform: A = min, B = max. Normal: A = mean, B = sigma.
///
/// Normals are deliberately UNTRUNCATED: every prior here is centred on an
/// observation with a small sigma (measurement noise), so truncation would
/// bias the hypothesis cloud; physically-impossible draws (e.g. a negative BC
/// at absurd sigma) are handled by likelihood weighting (M3.2), not clipping.
/// </summary>
public readonly record struct ScalarPrior(PriorDistribution Kind, double A, double B)
{
    /// <summary>Uniform prior on [min, max].</summary>
    public static ScalarPrior Uniform(double min, double max) =>
        new(PriorDistribution.Uniform, min, max);

    /// <summary>Normal prior with the given mean and standard deviation.</summary>
    public static ScalarPrior Normal(double mean, double sigma) =>
        new(PriorDistribution.Normal, mean, sigma);

    /// <summary>Draw one sample from this prior.</summary>
    public double Sample(Xoshiro256 rng) => Kind switch
    {
        PriorDistribution.Uniform => rng.NextDouble() * (B - A) + A,
        PriorDistribution.Normal => A + B * rng.NextNormal(),
        _ => throw new InvalidOperationException($"Unknown prior distribution {Kind}."),
    };
}

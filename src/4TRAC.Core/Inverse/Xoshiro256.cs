namespace FourTrac.Core.Inverse;

/// <summary>
/// Deterministic PRNG: xoshiro256** seeded through SplitMix64.
///
/// Deliberately in-house (PLAN.md §5 "RNG seed"): a case report must reproduce
/// its exact hypothesis cloud from the recorded seed on any machine and any
/// .NET version. System.Random's algorithm is implementation-specific and may
/// differ between runtimes, which would silently break that guarantee.
/// </summary>
public sealed class Xoshiro256
{
    private ulong _s0, _s1, _s2, _s3;

    public Xoshiro256(ulong seed)
    {
        // Standard xoshiro seeding: expand the (possibly sparse) user seed
        // into the four state words with SplitMix64.
        ulong z = seed;
        _s0 = SplitMix(ref z);
        _s1 = SplitMix(ref z);
        _s2 = SplitMix(ref z);
        _s3 = SplitMix(ref z);
        if (_s0 == 0 && _s1 == 0 && _s2 == 0 && _s3 == 0)
            _s0 = 0x9E3779B97F4A7C15UL; // all-zero state is a fixed point; never allow it.
    }

    private static ulong SplitMix(ref ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        ulong x = z;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>Next 64-bit value (xoshiro256**).</summary>
    public ulong NextUInt64()
    {
        ulong result = Rotl(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Uniform double in [0, 1) (53-bit resolution).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Standard normal deviate via Box-Muller (two uniforms per draw, no cached pair - determinism over speed).</summary>
    public double NextNormal()
    {
        double u1 = NextDouble();
        double u2 = NextDouble();
        if (u1 < 1e-300) u1 = 1e-300; // guard log(0); unreachable in practice (2^-53 granularity).
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));
}

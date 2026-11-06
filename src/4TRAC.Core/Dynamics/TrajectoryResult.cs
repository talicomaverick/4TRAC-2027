using FourTrac.Core.Geo;

namespace FourTrac.Core.Dynamics;

/// <summary>Why the trajectory integration stopped.</summary>
public enum TrajectoryEndReason
{
    /// <summary>Descended through the target height (terrain or elevation plane).</summary>
    GroundHit,

    /// <summary>Exceeded the maximum flight time.</summary>
    TimeLimit,

    /// <summary>Speed fell below the minimum (bullet tumbling/falling out of the sky).</summary>
    MinSpeed,

    /// <summary>Started at or below the target height (nothing to integrate).</summary>
    StartedBelowTarget,
}

/// <summary>One recorded trajectory sample.</summary>
public readonly record struct TrajectorySample(
    double T,
    Cartesian3 Position,
    Cartesian3 Velocity,
    double Speed,
    double Mach,
    double Density,
    double HeightM);

/// <summary>Result of a forward trajectory integration.</summary>
public sealed record TrajectoryResult(
    IReadOnlyList<TrajectorySample> Samples,
    TrajectoryEndReason EndReason,
    double TimeOfFlight,
    Cartesian3 FinalPosition,
    Cartesian3 FinalVelocity,
    double FinalSpeed,
    double FinalMach,
    double FinalHeightM);

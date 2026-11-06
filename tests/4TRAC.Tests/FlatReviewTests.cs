using FourTrac.Core.Atm;
using FourTrac.Core.Drag;
using FourTrac.Core.Flat;
using FourTrac.Core.Geo;
using FourTrac.Core.Inverse;
using Xunit;

namespace FourTrac.Tests;

/// <summary>
/// Regression tests added after the 2026-11-05 code review of the flat path:
/// root-find convergence invariant, calibration-status note, truncation warning,
/// and the count of non-positive exterior-angle draws.
/// </summary>
public class FlatReviewTests
{
    private static readonly Atmosphere Std = Atmosphere.FromMeasurements(15.0, 101325.0, 0.5, 0.0);

    [Fact]
    public void Solve_WhenConverged_ImpliedMuzzleVelocityEqualsTheDraw()
    {
        // Invariant behind "every hypothesis is an exact prior draw": a hypothesis
        // reported as converged must reproduce the drawn muzzle velocity to
        // within the root-find tolerance (checked here at 10x the tolerance).
        var drag = new BcDragModel(DragTableCatalog.ByStandard("G1"), new ConstantBcProvider(0.40));
        var violations = new List<string>();

        foreach (double originHeight in new[] { 0.0, 30.0 })
        {
            var scene = new FlatScene(40.0, originHeight, Std, Coriolis: true);
            foreach (double mv in new[] { 250.0, 850.0 })
            foreach (double ext in new[] { 1.0, 5.0, 15.0, 30.0, 60.0 })
            {
                var o = FlatSolver.SolveAtMuzzleVelocity(
                    new FlatObservation(ext, 270.0), mv, drag, scene, 2.0, -1.0);
                if (o.Converged && Math.Abs(o.MuzzleVelocityMps - mv) > 1e-2)
                    violations.Add($"originH {originHeight} mv {mv} ext {ext}: implied {o.MuzzleVelocityMps:F4}");
            }
        }

        Assert.True(violations.Count == 0, string.Join("; ", violations));
    }

    private static FlatAnalysisRequest Request(int hypotheses, FlatSpin? spin) => new(
        ImpactLatDeg: 40.0, ImpactLonDeg: -75.0, ImpactElevationM: 100.0, OriginHeightM: 0.0,
        ExteriorAngleDeg: 30.0, ExteriorSigmaDeg: 0.5, AzimuthDeg: 270.0, AzimuthSigmaDeg: 0.5,
        MvMinMps: 2550 * UnitConversions.FpsToMps, MvMaxMps: 2700 * UnitConversions.FpsToMps,
        BcLbIn2: 0.462, BcSigmaFraction: 0.03, ReferenceStandard: "G1",
        TemperatureC: (59 - 32) * 5.0 / 9.0, PressurePa: 29.92 * 3386.39, RelativeHumidity: 0.5,
        WindSpeedMps: 0.0, WindFromDeg: 0.0, WindSigmaMps: 0.5,
        Coriolis: true, Spin: spin, Hypotheses: hypotheses, Seed: 20261029UL);

    [Fact]
    public void Analysis_OutsideCalibratedConditions_SaysSoInTheLeadNote()
    {
        var spin = new FlatSpin(0.308, 168.0, 10.0, 1.22, RightHandTwist: true);
        var r = FlatAnalysis.Run(Request(60, spin));

        Assert.Null(r.Error);
        Assert.StartsWith("CALIBRATION", r.Notes[0]);
        Assert.Contains("OUTSIDE THE CALIBRATED CONDITIONS", r.Notes[0]);
        Assert.Contains("spin drift on", r.Notes[0]);
        Assert.Contains("60 hypotheses", r.Notes[0]);
    }

    [Fact]
    public void Analysis_HeavyRejection_CarriesTruncationWarning()
    {
        // A 100 m envelope rejects essentially every hypothesis (origin ~km away).
        var r = FlatAnalysis.Run(Request(40, null), new FlatSolveOptions { EnvelopeM = 100.0 });

        Assert.NotNull(r.Error);
        Assert.Contains(r.Notes, n => n.Contains("TRUNCATED CLOUD"));
    }

    [Fact]
    public void Cloud_ShallowAngleWithWidePrior_CountsNonPositiveAngleDraws()
    {
        var reference = DragTableCatalog.ByStandard("G1");
        var scene = new FlatScene(40.0, 0.0, Std, Coriolis: false);
        var priors = new InversePriors(
            ScalarPrior.Uniform(800.0, 820.0),
            ScalarPrior.Normal(0.3, 1.0),
            ScalarPrior.Normal(270.0, 0.1),
            ScalarPrior.Normal(0.40, 0.01),
            ScalarPrior.Normal(0.0, 0.1),
            ScalarPrior.Normal(0.0, 0.1));

        var cloud = FlatCloud.Run(priors, reference, scene, null, 40, 777UL);

        Assert.InRange(cloud.NonPositiveAngleDraws, 1, 40);
    }

    private sealed class CollectingProgress : IProgress<int>
    {
        private readonly object _gate = new();
        public List<int> Values { get; } = new();
        public void Report(int value) { lock (_gate) Values.Add(value); }
    }

    private static (InversePriors priors, DragTable reference, FlatScene scene) ShortScene()
    {
        var reference = DragTableCatalog.ByStandard("G1");
        var scene = new FlatScene(40.0, -5.0, Std, Coriolis: true);
        var priors = new InversePriors(
            ScalarPrior.Uniform(840.0, 860.0),
            ScalarPrior.Normal(25.0, 0.2),
            ScalarPrior.Normal(60.0, 0.2),
            ScalarPrior.Normal(0.40, 0.01),
            ScalarPrior.Normal(0.0, 0.1),
            ScalarPrior.Normal(0.0, 0.1));
        return (priors, reference, scene);
    }

    [Fact]
    public void Cloud_ReportsProgressUpToTheRequestedCount()
    {
        var (priors, reference, scene) = ShortScene();
        var progress = new CollectingProgress();

        var cloud = FlatCloud.Run(priors, reference, scene, null, 12, 5UL, progress: progress);

        Assert.Equal(12, cloud.Requested);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(12, progress.Values.Max());
        Assert.All(progress.Values, v => Assert.InRange(v, 1, 12));
    }

    [Fact]
    public void Cloud_WhenCancelled_ThrowsInsteadOfReturningAPartialCloud()
    {
        var (priors, reference, scene) = ShortScene();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            FlatCloud.Run(priors, reference, scene, null, 50, 5UL, cancellationToken: cts.Token));
    }

    [Fact]
    public void Cloud_ProgressAndCancellationDoNotChangeTheResult()
    {
        var (priors, reference, scene) = ShortScene();

        var plain = FlatCloud.Run(priors, reference, scene, null, 20, 99UL);
        var watched = FlatCloud.Run(priors, reference, scene, null, 20, 99UL,
            cancellationToken: CancellationToken.None, progress: new CollectingProgress());

        Assert.Equal(plain.Points.Count, watched.Points.Count);
        for (int i = 0; i < plain.Points.Count; i++)
        {
            Assert.Equal(plain.Points[i].EastM, watched.Points[i].EastM);
            Assert.Equal(plain.Points[i].NorthM, watched.Points[i].NorthM);
        }
    }
}

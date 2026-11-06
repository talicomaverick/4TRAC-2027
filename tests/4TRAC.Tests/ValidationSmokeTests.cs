using FourTrac.Core.Geo;
using FourTrac.Core.Stats;
using FourTrac.Validation;

namespace FourTrac.Tests;

/// <summary>
/// Smoke gate for the V2 validation harness (PLAN.md §7): a tiny design
/// matrix must run end to end - truth solve, noisy observations, exact-prior
/// Monte-Carlo cloud, HDR regions, coverage scoring - in seconds, producing
/// well-formed metrics and bit-identical results from the same seed. The full
/// matrix (~10^4 cells x 200 trials) is a 4TRAC.Validation console run, not a
/// unit test.
/// </summary>
public class ValidationSmokeTests
{
    /// <summary>The smoke matrix runs and produces well-formed trial outcomes.</summary>
    [Fact]
    public void SmokeMatrix_RunsAndProducesWellFormedOutcomes()
    {
        const int trials = 2;
        const int hypotheses = 20;

        foreach (var cell in DesignMatrix.SmokeMatrix())
        {
            Assert.True(SceneFactory.TrySolveCombo(cell.Combo, out var truth, out string? reason),
                $"Smoke truth for {cell.Combo} failed: {reason}");
            Assert.True(truth.MuzzleVelocityMps > DesignMatrix.MinValidMuzzleVelocityMps);
            Assert.True(truth.TimeOfFlightS > 0 && truth.TimeOfFlightS < 60);

            var truthVec = truth.ImpactFrame.ToEnu(truth.OriginPositionEcef);
            var truthEnu = new Vec2(truthVec.X, truthVec.Y);

            for (int t = 0; t < trials; t++)
            {
                var outcome = TrialRunner.Run(truth, cell.Protocol, hypotheses,
                    TrialRunner.DeriveSeed(777UL, ((long)cell.Index << 32) | (uint)t), truthEnu);

                Assert.Equal(0, outcome.FailedHypotheses);
                Assert.True(outcome.Area50M2 > 0 && outcome.Area95M2 > outcome.Area50M2,
                    $"Cell {cell.Index} trial {t}: areas {outcome.Area50M2:F1}/{outcome.Area95M2:F1} m^2.");
                Assert.False(double.IsNaN(outcome.MedianErrorM));
                Assert.False(double.IsNaN(outcome.MapErrorM));
                Assert.True(outcome.MedianErrorM < 5000,
                    $"Cell {cell.Index} trial {t}: median error {outcome.MedianErrorM:F1} m is implausible.");
            }
        }
    }

    /// <summary>Same seed reproduces the trial bit-for-bit (case-report reproducibility).</summary>
    [Fact]
    public void SmokeTrial_SameSeed_IsBitIdentical()
    {
        var cell = DesignMatrix.SmokeMatrix()[0];
        Assert.True(SceneFactory.TrySolveCombo(cell.Combo, out var truth, out string? reason),
            $"Smoke truth failed: {reason}");
        var truthVec = truth.ImpactFrame.ToEnu(truth.OriginPositionEcef);
        var truthEnu = new Vec2(truthVec.X, truthVec.Y);

        const ulong seed = 987654321UL;
        var a = TrialRunner.Run(truth, cell.Protocol, 16, seed, truthEnu);
        var b = TrialRunner.Run(truth, cell.Protocol, 16, seed, truthEnu);

        // ElapsedMs is wall-clock, not science - equalise before the record compare.
        Assert.Equal(a with { ElapsedMs = 0 }, b with { ElapsedMs = 0 });
    }

    /// <summary>
    /// Coverage sanity in the small-noise limit: with priors tight around the
    /// truth the 95 % region must contain the true origin, and the median
    /// origin error must sit inside the V2 error budget of the scene's range.
    /// </summary>
    [Fact]
    public void NoiseFreeLimit_CoversTruthAndMeetsBudget()
    {
        var combo = new SceneCombo(100, 5, 0, 0);
        Assert.True(SceneFactory.TrySolveCombo(combo, out var truth, out string? reason),
            $"Truth solve failed: {reason}");

        var truthVec = truth.ImpactFrame.ToEnu(truth.OriginPositionEcef);
        var truthEnu = new Vec2(truthVec.X, truthVec.Y);
        var tight = new CellProtocol(
            AngleNoiseSigmaDeg: 0.05, MvPriorHalfWidthMps: 0.5,
            BcSigmaFraction: 0.001, WindSigmaMps: 0.1);

        int covered = 0;
        const int trials = 3;
        for (int t = 0; t < trials; t++)
        {
            var outcome = TrialRunner.Run(truth, tight, hypotheses: 24,
                TrialRunner.DeriveSeed(4242UL, t), truthEnu);
            Assert.Equal(0, outcome.FailedHypotheses);
            if (outcome.Covered95) covered++;
            Assert.True(outcome.MedianErrorM <= Math.Max(5.0, 0.015 * combo.RangeM),
                $"Tight-prior median error {outcome.MedianErrorM:F2} m exceeds the {0.015 * combo.RangeM:F2} m budget.");
        }
        Assert.True(covered >= trials - 1,
            $"Only {covered}/{trials} tight-prior trials covered the truth.");
    }

    /// <summary>
    /// The MV validity floor excludes the absurd corner by construction: a
    /// 50 m / 45 deg cell needs ~22 m/s - far below any G-standard's low end.
    /// </summary>
    [Fact]
    public void LowVelocityCorner_IsExcludedWithReason()
    {
        var combo = new SceneCombo(50, 45, 0, 0);
        Assert.False(SceneFactory.TrySolveCombo(combo, out _, out string? reason));
        Assert.Contains("validity floor", reason, StringComparison.OrdinalIgnoreCase);
    }
}

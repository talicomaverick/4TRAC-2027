using FourTrac.Core.Geo;
using FourTrac.Core.Stats;

namespace FourTrac.Validation;

/// <summary>One tornado ladder point: one input's uncertainty swept, the rest nominal.</summary>
public sealed record TornadoRow(
    string Input,
    double Sigma,
    string Unit,
    double MedianErrorM,
    double MeanArea95M2,
    double Coverage95,
    int Trials);

/// <summary>
/// Sensitivity (tornado) sweep (PLAN.md §7): one reference scene, one input's
/// uncertainty swept per ladder while the others stay nominal. The published
/// output is the LARGEST uncertainty per input that keeps the median origin
/// error inside the error budget - the required scene-measurement precision.
/// Ladder points share their per-trial seeds (common random numbers): the
/// noise realizations are identical across a ladder, so differences between
/// adjacent points isolate the swept input's effect.
/// </summary>
public static class Tornado
{
    public static readonly SceneCombo ReferenceCombo = new(500.0, 30.0, 0.0, 3.0);

    public static readonly double[] SigmaAngleDegLadder = { 0.1, 0.25, 0.5, 1.0, 2.0 };
    public static readonly double[] MvHalfWidthLadder = { 1.0, 3.0, 5.0, 10.0, 15.0 };
    public static readonly double[] WindSigmaLadder = { 0.25, 0.5, 1.0, 2.0, 3.0 };
    public static readonly double[] BcSigmaFractionLadder = { 0.002, 0.005, 0.01, 0.03, 0.06 };

    public static List<TornadoRow> Run(int trials, int hypotheses, ulong seed)
    {
        if (!SceneFactory.TrySolveCombo(ReferenceCombo, out var truth, out string? why))
            throw new InvalidOperationException($"Tornado reference scene failed: {why}");

        var truthEnuVec = truth.ImpactFrame.ToEnu(truth.OriginPositionEcef);
        var truthEnu = new Vec2(truthEnuVec.X, truthEnuVec.Y);
        var nominal = DesignMatrix.NominalProtocol;

        var rows = new List<TornadoRow>();
        Sweep("exterior angle sigma", "deg", SigmaAngleDegLadder,
            s => nominal with { AngleNoiseSigmaDeg = s });
        Sweep("MV prior half-width", "m/s", MvHalfWidthLadder,
            s => nominal with { MvPriorHalfWidthMps = s });
        Sweep("wind sigma (per axis)", "m/s", WindSigmaLadder,
            s => nominal with { WindSigmaMps = s });
        Sweep("BC sigma (fraction of BC)", "frac", BcSigmaFractionLadder,
            s => nominal with { BcSigmaFraction = s });
        return rows;

        void Sweep(string input, string unit, double[] ladder, Func<double, CellProtocol> withSigma)
        {
            foreach (double sigma in ladder)
            {
                var protocol = withSigma(sigma);
                var outcomes = new List<TrialOutcome>(trials);
                for (int t = 0; t < trials; t++)
                {
                    outcomes.Add(TrialRunner.Run(truth, protocol, hypotheses,
                        TrialRunner.DeriveSeed(seed, 0x70AD0L + t), truthEnu));
                }

                rows.Add(new TornadoRow(
                    input, sigma, unit,
                    Aggregation.Median(outcomes.Where(o => !double.IsNaN(o.MedianErrorM))
                                               .Select(o => o.MedianErrorM).ToList()),
                    outcomes.Where(o => !double.IsNaN(o.Area95M2)).Select(o => o.Area95M2).Average(),
                    outcomes.Count(o => o.Covered95) / (double)trials,
                    trials));
            }
        }
    }

    /// <summary>
    /// Largest swept sigma whose median origin error still meets the budget at
    /// the reference range - the required precision for that input.
    /// </summary>
    public static (double Sigma, string Unit)? RequiredPrecision(
        IReadOnlyList<TornadoRow> rows, string input, double budgetM)
    {
        var passing = rows.Where(r => r.Input == input && r.MedianErrorM <= budgetM).ToList();
        if (passing.Count == 0) return null;
        var best = passing.OrderByDescending(r => r.Sigma).First();
        return (best.Sigma, best.Unit);
    }
}

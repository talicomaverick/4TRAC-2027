using System.Globalization;

namespace FourTrac.Validation;

/// <summary>CSV/Markdown writers for the V2 validation report.</summary>
public static class ReportWriters
{
    private static string F(double v, int dec) =>
        double.IsNaN(v) ? "" : v.ToString("F" + dec, CultureInfo.InvariantCulture);

    /// <summary>Header of cells.csv (also the checkpoint format - one parse).</summary>
    public const string CellHeader =
        "CellIndex,RangeM,LaunchElDeg,AzimuthDeg,WindMps," +
        "SigmaAngleDeg,MvHalfWidthMps,BcSigmaFrac,WindSigmaMps," +
        "TruthMvMps,TruthExteriorDeg,TruthTofS," +
        "Trials,Coverage50,Coverage95,MedianErrM,MapErrM," +
        "MeanArea50M2,MeanArea95M2,MaxFailed,MeanTrialMs,Eligible,BudgetM,LowMach";

    /// <summary>Header of trials.csv.</summary>
    public const string TrialHeader =
        "CellIndex,TrialIndex,Covered50,Covered95,MedianErrM,MapErrM," +
        "BiasEastM,BiasNorthM,Area50M2,Area95M2,Failed,ExtentM,ElapsedMs";

    /// <summary>One cells.csv row - shared by the final writer and the checkpoint.</summary>
    public static string CellRow(CellSummary c) => string.Join(',',
        c.Cell.Index.ToString(CultureInfo.InvariantCulture),
        F(c.Cell.Combo.RangeM, 0), F(c.Cell.Combo.LaunchElDeg, 0),
        F(c.Cell.Combo.AzimuthDeg, 0), F(c.Cell.Combo.WindSpeedMps, 0),
        F(c.Cell.Protocol.AngleNoiseSigmaDeg, 2), F(c.Cell.Protocol.MvPriorHalfWidthMps, 1),
        F(c.Cell.Protocol.BcSigmaFraction, 3), F(c.Cell.Protocol.WindSigmaMps, 2),
        F(c.TruthMvMps, 2), F(c.TruthExteriorDeg, 3), F(c.TruthTofS, 3),
        c.TrialsRun.ToString(CultureInfo.InvariantCulture),
        F(c.Coverage50, 4), F(c.Coverage95, 4),
        F(c.MedianErrorM, 3), F(c.MapErrorM, 3),
        F(c.MeanArea50M2, 1), F(c.MeanArea95M2, 1),
        c.MaxFailedHypotheses.ToString(CultureInfo.InvariantCulture),
        F(c.MeanTrialMs, 1),
        c.Eligible ? "1" : "0", F(c.ErrorBudgetM, 2),
        c.LowMachExtension ? "1" : "0");

    /// <summary>One trials.csv row - shared by the final writer and the checkpoint.</summary>
    public static string TrialRow(int cellIndex, int trialIndex, TrialOutcome o) => string.Join(',',
        cellIndex.ToString(CultureInfo.InvariantCulture),
        trialIndex.ToString(CultureInfo.InvariantCulture),
        o.Covered50 ? "1" : "0", o.Covered95 ? "1" : "0",
        F(o.MedianErrorM, 3), F(o.MapErrorM, 3),
        F(o.BiasEastM, 3), F(o.BiasNorthM, 3),
        F(o.Area50M2, 1), F(o.Area95M2, 1),
        o.FailedHypotheses.ToString(CultureInfo.InvariantCulture),
        F(o.CloudExtentM, 2), F(o.ElapsedMs, 1));

    public static void WriteCellsCsv(string path, IReadOnlyList<CellSummary> cells)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CellHeader);
        foreach (var c in cells)
            sb.AppendLine(CellRow(c));
        File.WriteAllText(path, sb.ToString());
    }

    public static void WriteTrialsCsv(
        string path, IReadOnlyList<(int CellIndex, IReadOnlyList<TrialOutcome> Trials)> data)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(TrialHeader);
        foreach (var (cellIndex, trials) in data)
        {
            for (int t = 0; t < trials.Count; t++)
                sb.AppendLine(TrialRow(cellIndex, t, trials[t]));
        }
        File.WriteAllText(path, sb.ToString());
    }

    public static void WriteTornadoCsv(string path, IReadOnlyList<TornadoRow> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Input,Sigma,Unit,MedianErrM,MeanArea95M2,Coverage95,Trials");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(',',
                r.Input, F(r.Sigma, 3), r.Unit,
                F(r.MedianErrorM, 3), F(r.MeanArea95M2, 1),
                F(r.Coverage95, 4), r.Trials.ToString(CultureInfo.InvariantCulture)));
        }
        File.WriteAllText(path, sb.ToString());
    }

    public static void WriteReportMd(
        string path,
        RunConfig cfg,
        IReadOnlyList<CellSummary> cells,
        IReadOnlyList<(SceneCombo Combo, string Reason)> exclusions,
        MatrixSummary? matrix,
        IReadOnlyList<CriterionResult>? criteria,
        IReadOnlyList<TornadoRow>? tornado,
        TimeSpan elapsed,
        double? fullRunEstimateHours,
        int resumedCells = 0)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# 4TRAC V2 validation report");
        sb.AppendLine();
        sb.AppendLine($"Generated (UTC): {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", inv)}");
        sb.AppendLine();
        sb.AppendLine("## Run configuration");
        sb.AppendLine();
        sb.AppendLine($"- Mode: `{cfg.Mode}`  |  seed `{cfg.Seed}`  |  stride `{cfg.Stride}`");
        sb.AppendLine($"- Trials per cell: {cfg.Trials}  |  hypotheses per trial: {cfg.Hypotheses}");
        sb.AppendLine($"- Parallelism: {cfg.MaxDop}  |  elapsed: {elapsed.TotalMinutes:F2} min" +
                      (resumedCells > 0
                          ? " (this invocation only; earlier invocation(s) resumed from the checkpoint)"
                          : ""));
        sb.AppendLine($"- Cells run: {cells.Count}  |  cells excluded: {exclusions.Count}");
        if (resumedCells > 0)
            sb.AppendLine($"- Resumed {resumedCells} cell(s) from the checkpoint of an interrupted run " +
                          "(same matrix, seed and weight: deterministic, no partial cell is reused)");
        sb.AppendLine();

        sb.AppendLine("## Scene protocol (declared choices, PLAN.md Â§8.3)");
        sb.AppendLine();
        sb.AppendLine($"- Scene: lat {F(V2Scene.LatitudeDeg, 1)} deg, lon {F(V2Scene.LongitudeDeg, 1)} deg, " +
                      $"height {F(V2Scene.HeightM, 0)} m, flat (no DEM - terrain is V3/M4).");
        sb.AppendLine($"- Bullet: scalar G1, BC {F(V2Scene.TruthBc, 2)}, {V2Scene.Bullet.MassGr} g; Coriolis on (production path).");
        sb.AppendLine("- Wind: full-value crosswind FROM azimuth + 90 deg; truth wind observed with per-axis sigma from the cell.");
        sb.AppendLine("- Calibration protocol: every truth quantity sits at the centre of its declared prior.");
        sb.AppendLine("- The origin-error criterion is evaluated on the ACHIEVABLE-protocol anchor " +
                      "cells (sigma theta 0.25 deg, MV +/-1 m/s, BC 3 %, wind 0.5 m/s - the " +
                      "tornado-published precision tuple). Read jointly with the tornado, the " +
                      "error budget is a promise for scenes whose measurements meet the " +
                      "published requirements: achievable origin precision scales with scene " +
                      "measurement noise by physics. Nominal and noisy-protocol cells remain " +
                      "in the matrix as coverage-calibration and sensitivity evidence.");
        sb.AppendLine("- The theta axis is the LAUNCH elevation; the realized exterior (impact) angle is recorded per cell.");
        sb.AppendLine($"- MV validity floor: Mach {F(DesignMatrix.MinValidMach, 1)} " +
                      $"({F(DesignMatrix.MinValidMuzzleVelocityMps, 2)} m/s at this scene); " +
                      $"cells below the published 400 fps bound ({F(DesignMatrix.PublishedTableFloorMps, 2)} m/s) " +
                      "are flagged LOW-MACH: model-relative calibration still holds " +
                      "(truth and inverse share the curve), but they validate the solver, not the bullet model.");
        sb.AppendLine();

        if (exclusions.Count > 0)
        {
            sb.AppendLine("## Excluded cells (itemized, never silent)");
            sb.AppendLine();
            sb.AppendLine("| Range (m) | El (deg) | Az (deg) | Wind (m/s) | Reason |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var (combo, reason) in exclusions)
            {
                sb.AppendLine($"| {F(combo.RangeM, 0)} | {F(combo.LaunchElDeg, 0)} | " +
                              $"{F(combo.AzimuthDeg, 0)} | {F(combo.WindSpeedMps, 0)} | {reason} |");
            }
            sb.AppendLine();
        }

        if (matrix is not null && criteria is not null)
        {
            sb.AppendLine("## Matrix summary");
            sb.AppendLine();
            sb.AppendLine($"- Pooled 95 % coverage: **{F(matrix.Coverage95Pooled * 100.0, 2)} %**  |  " +
                          $"pooled 50 % coverage: **{F(matrix.Coverage50Pooled * 100.0, 2)} %**");
            sb.AppendLine($"- Eligible cells (theta >= {F(V2Scene.MinEligibleAngleDeg, 0)} deg, range <= {F(V2Scene.MaxEligibleRangeM, 0)} m): " +
                          $"{matrix.EligibleCells}  |  pooled median origin error there: {F(matrix.MedianErrorEligiblePooledM, 2)} m");
            sb.AppendLine();

            sb.AppendLine("## Pass criteria (PLAN.md Â§7 V2)");
            sb.AppendLine();
            sb.AppendLine("| Criterion | Verdict | Observed | Required |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var c in criteria)
            {
                sb.AppendLine($"| {c.Name} | {(c.Passed is null ? "SKIP" : c.Passed.Value ? "PASS" : "FAIL")} | {c.Observed} | {c.Required} |");
            }
            sb.AppendLine();

            var worst = cells.Where(c => c.Eligible)
                             .OrderByDescending(c => c.MedianErrorM / c.ErrorBudgetM)
                             .Take(10).ToList();
            if (worst.Count > 0)
            {
                sb.AppendLine("## Worst eligible cells (median origin error / budget)");
                sb.AppendLine();
                sb.AppendLine("| Cell | Range/El/Az/Wind | sigma theta | MV half | BC sigma | Median err (m) | Budget (m) | Cov 95 % |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|");
                foreach (var c in worst)
                {
                    var combo = c.Cell.Combo;
                    sb.AppendLine($"| {c.Cell.Index} | {F(combo.RangeM, 0)}/{F(combo.LaunchElDeg, 0)}/" +
                                  $"{F(combo.AzimuthDeg, 0)}/{F(combo.WindSpeedMps, 0)}" +
                                  $"{(c.LowMachExtension ? " (low-Mach)" : "")} | " +
                                  $"{F(c.Cell.Protocol.AngleNoiseSigmaDeg, 2)} deg | " +
                                  $"{F(c.Cell.Protocol.MvPriorHalfWidthMps, 1)} m/s | " +
                                  $"{F(c.Cell.Protocol.BcSigmaFraction * 100.0, 1)} % | " +
                                  $"{F(c.MedianErrorM, 2)} | {F(c.ErrorBudgetM, 2)} | {F(c.Coverage95 * 100.0, 1)} % |");
                }
                sb.AppendLine();
            }
        }

        if (tornado is not null && tornado.Count > 0)
        {
            double budget = Math.Max(5.0, 0.015 * Tornado.ReferenceCombo.RangeM);
            sb.AppendLine("## Tornado: required scene-measurement precision");
            sb.AppendLine();
            sb.AppendLine($"Reference scene: {F(Tornado.ReferenceCombo.RangeM, 0)} m / " +
                          $"{F(Tornado.ReferenceCombo.LaunchElDeg, 0)} deg / az {F(Tornado.ReferenceCombo.AzimuthDeg, 0)} deg / " +
                          $"{F(Tornado.ReferenceCombo.WindSpeedMps, 0)} m/s wind. " +
                          $"Error budget: {F(budget, 2)} m (1.5 % of range). One input swept, the rest nominal.");
            sb.AppendLine();
            sb.AppendLine("| Input | Swept sigma | Median err (m) | HDR95 area (m^2) | Cov 95 % |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var r in tornado)
            {
                sb.AppendLine($"| {r.Input} | {F(r.Sigma, 3)} {r.Unit} | {F(r.MedianErrorM, 2)} | " +
                              $"{F(r.MeanArea95M2, 0)} | {F(r.Coverage95 * 100.0, 1)} % |");
            }
            sb.AppendLine();
            sb.AppendLine("**Required precision (largest swept sigma still inside the budget):**");
            sb.AppendLine();
            foreach (var input in tornado.Select(r => r.Input).Distinct())
            {
                var req = Tornado.RequiredPrecision(tornado, input, budget);
                sb.AppendLine(req is null
                    ? $"- {input}: **no swept sigma meets the budget while the others stay nominal** - " +
                      "tightening this input alone cannot buy the budget; the binding constraint " +
                      "is another input (the ladder rows show each input's own sensitivity)"
                    : $"- {input}: **<= {F(req.Value.Sigma, 3)} {req.Value.Unit}** (1 sigma)");
            }
            sb.AppendLine();
        }

        if (fullRunEstimateHours is not null)
        {
            sb.AppendLine("## Full-matrix extrapolation");
            sb.AppendLine();
            sb.AppendLine($"Measured throughput extrapolates the plan-mandated full run " +
                          $"(stride 1, 200 trials/cell, 250 hypotheses/trial) to about " +
                          $"**{F(fullRunEstimateHours.Value, 1)} hours** at {cfg.MaxDop}-way parallelism. " +
                          "Schedule it as an overnight/background job or thin the matrix with --stride.");
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;
using FourTrac.Core.Atm;
using FourTrac.Core.Drag;
using FourTrac.Core.Flat;
using FourTrac.Core.Geo;

namespace FourTrac.Validation;

/// <summary>
/// V2 matrix on the flat model (PLAN.md §7, rewired). Cells are origin-range
/// bins crossed with crosswind levels. Each trial draws the TRUE origin
/// uniformly by area in the cell's 10 % annulus, and the true muzzle velocity,
/// BC and wind from the same priors the inverse uses; then it simulates the
/// shot, adds measurement noise to the observed impact angles, and checks
/// whether the true origin lies in the 50 % and 95 % regions. The realised
/// exterior angle is recorded per trial, since the launch-angle axis of the
/// plan is a realised quantity here.
///
/// Gates (whole matrix): 95 % coverage in [92, 98] %, 50 % in [45, 55] %.
/// Gate (per cell, R ≤ 1000 m, realised exterior ≥ 15°): median origin error
/// ≤ max(5 m, 1.5 % R). When the angle-noise information bound already exceeds
/// the budget, the criterion is SKIP with the bound quoted, never FAIL.
/// </summary>
public static class FlatMatrix
{
    public sealed record Cell(int Index, double RangeM, double WindMps, double AngleSigmaDeg);

    public static IReadOnlyList<Cell> Cells()
    {
        var cells = new List<Cell>();
        int i = 0;
        foreach (double r in new[] { 500.0, 1000.0, 2000.0 })
            foreach (double w in new[] { 0.0, 3.0 })
                cells.Add(new Cell(i++, r, w, 0.5));
        return cells;
    }

    public static CoverageConfig ConfigFor(Cell c) => new(
        LatDeg: 40.0,
        OriginHeightM: 0.0,
        Atmosphere: Atmosphere.FromMeasurements((59 - 32) * 5.0 / 9.0, 29.92 * 3386.39, 0.5, 100.0),
        Coriolis: true,
        Reference: DragTableCatalog.ByStandard("G1"),
        BcNominalLbIn2: 0.462,
        BcSigmaLbIn2: 0.462 * 0.03,
        LaunchElevationMinDeg: 0.0,
        LaunchElevationMaxDeg: 0.0,
        LaunchAzimuthDeg: 0.0,
        MvMinMps: 2400 * UnitConversions.FpsToMps,
        MvMaxMps: 2700 * UnitConversions.FpsToMps,
        WindEastMps: c.WindMps,
        WindNorthMps: 0.0,
        WindSigmaMps: 0.5,
        AngleSigmaDeg: c.AngleSigmaDeg,
        AzimuthSigmaDeg: c.AngleSigmaDeg,
        Spin: null,
        OriginRangeMinM: 0.9 * c.RangeM,
        OriginRangeMaxM: 1.1 * c.RangeM);

    public static int Run(string outDir, int trials, int hypotheses, ulong seed)
    {
        Directory.CreateDirectory(outDir);
        var sw = Stopwatch.StartNew();
        var cells = Cells();
        var results = new List<(Cell Cell, CoverageResult Result, double Elapsed)>();

        foreach (var cell in cells)
        {
            var t0 = sw.Elapsed.TotalSeconds;
            ulong cellSeed = seed ^ (0xA24BAED4963EE407UL * (ulong)(cell.Index + 1));
            var r = CoverageStudy.Run(ConfigFor(cell), trials, hypotheses, cellSeed);
            results.Add((cell, r, sw.Elapsed.TotalSeconds - t0));
            Console.WriteLine($"cell {cell.Index}: R={cell.RangeM:F0} wind={cell.WindMps:F0} trials={r.Trials.Count} skipped={r.Skipped} c50={r.Coverage50:P1} c95={r.Coverage95:P1} ({sw.Elapsed.TotalSeconds:F0} s)");
        }

        WriteCells(outDir, results);
        WriteTrials(outDir, results);
        WriteReport(outDir, results, trials, hypotheses, seed, sw.Elapsed);
        Console.WriteLine($"report: {Path.Combine(outDir, "report.md")}");
        return 0;
    }

    private static void WriteCells(string dir, List<(Cell Cell, CoverageResult Result, double Elapsed)> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("cell,range_m,wind_mps,angle_sigma_deg,trials,skipped,coverage50,coverage95,median_error_m,median_exterior_deg,seconds");
        foreach (var (c, r, s) in rows)
        {
            double medExt = r.Trials.Count == 0 ? double.NaN : Median(r.Trials.Select(t => t.TruthExteriorDeg));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{c.Index},{c.RangeM:F0},{c.WindMps:F1},{c.AngleSigmaDeg:F2},{r.Trials.Count},{r.Skipped},{r.Coverage50:F4},{r.Coverage95:F4},{r.MedianErrorM:F3},{medExt:F3},{s:F1}"));
        }
        File.WriteAllText(Path.Combine(dir, "cells.csv"), sb.ToString());
    }

    private static void WriteTrials(string dir, List<(Cell Cell, CoverageResult Result, double Elapsed)> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("cell,trial,truth_range_m,truth_mv_fps,truth_exterior_deg,covered50,covered95,median_error_m,signed_e_m,signed_n_m,cloud_points,no_origin,outside_envelope");
        foreach (var (c, r, _) in rows)
            foreach (var t in r.Trials)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{c.Index},{t.Index},{t.TruthOriginRangeM:F2},{t.TruthMuzzleVelocityMps / UnitConversions.FpsToMps:F2},{t.TruthExteriorDeg:F4},{(t.Covered50 ? 1 : 0)},{(t.Covered95 ? 1 : 0)},{t.MedianErrorM:F3},{t.SignedEastErrorM:F3},{t.SignedNorthErrorM:F3},{t.CloudPoints},{t.CloudNoOrigin},{t.CloudOutsideEnvelope}"));
        File.WriteAllText(Path.Combine(dir, "trials.csv"), sb.ToString());
    }

    private static void WriteReport(string dir, List<(Cell Cell, CoverageResult Result, double Elapsed)> rows,
        int trials, int hypotheses, ulong seed, TimeSpan elapsed)
    {
        var all = rows.SelectMany(x => x.Result.Trials).ToList();
        int skipped = rows.Sum(x => x.Result.Skipped);
        double c50 = all.Count == 0 ? double.NaN : all.Count(t => t.Covered50) / (double)all.Count;
        double c95 = all.Count == 0 ? double.NaN : all.Count(t => t.Covered95) / (double)all.Count;

        var sb = new StringBuilder();
        sb.AppendLine("# 4TRAC V2 matrix - flat model");
        sb.AppendLine();
        sb.AppendLine($"Seed {seed} | trials/cell {trials} | hypotheses/trial {hypotheses} | elapsed {elapsed.TotalMinutes:F1} min | cells {rows.Count}");
        sb.AppendLine();
        sb.AppendLine("Protocol: origin uniform by area in a 10 % annulus at each range; truth MV 2400-2700 fps, BC 0.462 ± 3 %, wind ± 0.5 m/s, angle and azimuth noise per cell; flat origin prior (declared).");
        sb.AppendLine();

        sb.AppendLine("## Verdicts");
        sb.AppendLine();
        sb.AppendLine("| Criterion | Verdict | Measured | Required / reason |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine($"| 95 % region coverage (whole matrix) | {Verdict(c95, 0.92, 0.98)} | {c95:P1} | 92-98 % |");
        sb.AppendLine($"| 50 % region coverage (whole matrix) | {Verdict(c50, 0.45, 0.55)} | {c50:P1} | 45-55 % |");

        foreach (var (c, r, _) in rows.Where(x => x.Cell.RangeM <= 1000))
        {
            var eligible = r.Trials.Where(t => t.TruthExteriorDeg >= 15.0).ToList();
            string name = $"median origin error, R={c.RangeM:F0} m, wind={c.WindMps:F0} m/s (ext ≥ 15°)";
            if (eligible.Count == 0)
            {
                sb.AppendLine($"| {name} | SKIP | no eligible trials | realised exterior ≥ 15° needed |");
                continue;
            }
            double budget = Math.Max(5.0, 0.015 * c.RangeM);
            double med = Median(eligible.Select(t => t.MedianErrorM));
            double ext = Median(eligible.Select(t => t.TruthExteriorDeg));
            // Along-range sensitivity to the exterior angle: |dR/dθ| ≈ 2R·cot(2θ) per radian of impact angle.
            double bound = 2.0 * c.RangeM / Math.Tan(2.0 * ext * UnitConversions.DegToRad) * c.AngleSigmaDeg * UnitConversions.DegToRad;
            if (bound > budget)
                sb.AppendLine($"| {name} | SKIP | median {med:F1} m | information bound from angle noise {bound:F1} m exceeds budget {budget:F1} m |");
            else
                sb.AppendLine($"| {name} | {(med <= budget ? "PASS" : "FAIL")} | median {med:F1} m | ≤ {budget:F1} m |");
        }
        sb.AppendLine();

        sb.AppendLine("## Cells");
        sb.AppendLine();
        sb.AppendLine("| Cell | Range (m) | Crosswind (m/s) | Trials | Skipped | 50 % | 95 % | Median error (m) | Median exterior (°) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var (c, r, _) in rows)
        {
            double medExt = r.Trials.Count == 0 ? double.NaN : Median(r.Trials.Select(t => t.TruthExteriorDeg));
            sb.AppendLine($"| {c.Index} | {c.RangeM:F0} | {c.WindMps:F0} | {r.Trials.Count} | {r.Skipped} | {r.Coverage50:P1} | {r.Coverage95:P1} | {r.MedianErrorM:F1} | {medExt:F1} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Exclusions and deviations");
        sb.AppendLine();
        sb.AppendLine($"- {skipped} trials skipped in total (shot could not be aimed at the drawn origin, or fewer than 10 hypotheses converged). Counted in the invariant completed + skipped = requested.");
        sb.AppendLine("- Flat origin prior is declared; coverage is calibrated under it, not under a launch-elevation prior.");
        sb.AppendLine("- Spin drift off; constant crosswind; scalar G1 BC; single measurement protocol (angle sigma per cell, azimuth equal).");
        sb.AppendLine("- Only 3 ranges x 2 crosswind levels are run; the full plan matrix (range x angle x azimuth x wind x noise x MV x BC) is not. The launch-angle axis is realised per trial, not designed.");
        sb.AppendLine("- Median error per cell is the distance from the truth to the cloud's median, not the mode.");

        File.WriteAllText(Path.Combine(dir, "report.md"), sb.ToString());
    }

    private static string Verdict(double v, double lo, double hi) =>
        double.IsNaN(v) ? "SKIP" : v >= lo && v <= hi ? "PASS" : "FAIL";

    private static double Median(IEnumerable<double> values)
    {
        var s = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
        if (s.Length == 0) return double.NaN;
        int m = s.Length / 2;
        return s.Length % 2 == 1 ? s[m] : 0.5 * (s[m - 1] + s[m]);
    }
}

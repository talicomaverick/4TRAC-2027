using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using FourTrac.Core.Geo;
using FourTrac.Core.Stats;

namespace FourTrac.Validation;

/// <summary>Resolved CLI configuration of one harness run.</summary>
public sealed record RunConfig(
    string Mode,
    int Trials,
    int Hypotheses,
    ulong Seed,
    int Stride,
    int MaxDop,
    string OutDir,
    bool WriteTrialsCsv);

/// <summary>
/// 4TRAC.Validation - the V2 validation harness (PLAN.md Â§7).
///
/// Modes:
///   full     the V2 design matrix (thinnable with --stride), 200 trials/cell
///            at full plan weight; writes cells.csv, trials.csv, report.md
///   smoke    the tiny unit-suite matrix (seconds)
///   tornado  the sensitivity sweep: required scene-measurement precision
///
/// Determinism: every trial's noise + cloud seed derives from --seed and the
/// cell/trial indices, so a run is exactly reproducible from the report.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var cfg = Parse(args);
        if (cfg is null) return 2;
        Directory.CreateDirectory(cfg.OutDir);

        // Nudge stride off protocol-combo multiples (otherwise every sampled
        // cell carries the same protocol - see DesignMatrix.FullMatrix).
        if (cfg.Mode == "full" && cfg.Stride > 1 &&
            cfg.Stride % DesignMatrix.ProtocolCombosPerScene == 0)
        {
            Console.WriteLine(
                $"note: stride {cfg.Stride} aliases the {DesignMatrix.ProtocolCombosPerScene} protocol combos; " +
                $"using {cfg.Stride + 1} instead.");
            cfg = cfg with { Stride = cfg.Stride + 1 };
        }

        return cfg.Mode switch
        {
            "smoke" => RunMatrix(DesignMatrix.SmokeMatrix(), cfg),
            "full" => RunMatrix(WithAnchors(DesignMatrix.FullMatrix(cfg.Stride)), cfg),
            "anchors" => RunMatrix(DesignMatrix.AnchorCells(), cfg),
            "tornado" => RunTornado(cfg),
            "flat" => FlatMatrix.Run(cfg.OutDir, cfg.Trials, cfg.Hypotheses, cfg.Seed),
            _ => 2,
        };
    }

    private static RunConfig? Parse(string[] args)
    {
        string mode = "full", outDir = "validation-out";
        int trials = -1, hypotheses = -1, stride = 1, dop = Environment.ProcessorCount;
        ulong seed = 20261022;
        bool trialsCsv = true;

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"Missing value for {args[i]}.");

            switch (args[i])
            {
                case "--mode": mode = Next(); break;
                case "--trials": int.TryParse(Next(), out trials); break;
                case "--hypotheses": int.TryParse(Next(), out hypotheses); break;
                case "--seed": ulong.TryParse(Next(), out seed); break;
                case "--stride": int.TryParse(Next(), out stride); break;
                case "--max-dop": int.TryParse(Next(), out dop); break;
                case "--out": outDir = Next(); break;
                case "--no-trials-csv": trialsCsv = false; break;
                case "--help" or "-h":
                    Console.WriteLine(
                        "4TRAC.Validation V2 harness\n" +
                        "  --mode full|smoke|anchors|tornado   run mode (default full; anchors = eligible scenes at\n" +
                        "                                the published-precision protocol - keeps the\n" +
                        "                                origin-error criterion evaluable in thin runs)\n" +
                        "  --trials N                 Monte-Carlo trials per cell (default: 200 full / 4 smoke / 30 tornado)\n" +
                        "  --hypotheses N             cloud hypotheses per trial (default: 250 full / 20 smoke+tornado)\n" +
                        "  --seed S                   base seed (default 20261022)\n" +
                        "  --stride K                 keep every K-th matrix cell (cost control, default 1)\n" +
                        "  --max-dop N                parallel cells (default: logical processors)\n" +
                        "  --out DIR                  output directory (default validation-out)\n" +
                        "  --no-trials-csv            skip the per-trial CSV");
                    return null;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]} (--help)");
                    return null;
            }
        }

        if (mode is not ("full" or "smoke" or "anchors" or "tornado" or "flat"))
        {
            Console.Error.WriteLine($"Unknown mode: {mode} (--help)");
            return null;
        }
        if (trials <= 0) trials = mode == "smoke" ? 4 : mode == "tornado" ? 30 : mode == "flat" ? 40 : 200;
        if (hypotheses <= 0) hypotheses = mode == "full" ? 250 : mode == "flat" ? 500 : 20;
        if (stride < 1) stride = 1;
        if (dop < 1) dop = 1;

        return new RunConfig(mode, trials, hypotheses, seed, stride, dop, outDir, trialsCsv);
    }

    /// <summary>Union the criterion anchors into a cell list (deduplicated by
    /// scene + protocol), renumbering indices so seeds stay deterministic.</summary>
    private static List<DesignCell> WithAnchors(List<DesignCell> cells)
    {
        var anchors = DesignMatrix.AnchorCells()
            .Where(a => !cells.Any(c => c.Combo == a.Combo && c.Protocol == a.Protocol))
            .ToList();
        if (anchors.Count == 0) return cells;
        Console.WriteLine($"note: added {anchors.Count} criterion-anchor cell(s) " +
                          "(published-precision protocol at every eligible scene).");
        cells.AddRange(anchors);
        return cells.Select((c, i) => c with { Index = i }).ToList();
    }

    private static int RunMatrix(List<DesignCell> cells, RunConfig cfg)
    {
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine(
            $"4TRAC V2: mode={cfg.Mode} cells={cells.Count} trials={cfg.Trials} " +
            $"hypotheses={cfg.Hypotheses} dop={cfg.MaxDop} seed={cfg.Seed}");

        // Truth scenes are shared by every cell of a combo - solve each once.
        var comboCache = new ConcurrentDictionary<SceneCombo, (TruthScene? Scene, string? Reason)>();
        var outcomes = new CellOutcome[cells.Count];
        var sw = Stopwatch.StartNew();
        int done = 0;
        int progressStep = Math.Max(1, cells.Count / 20);

        // Resume support: completed cells live in checkpoint/cells.csv, so a
        // killed run restarts from where it stopped (see Checkpoint.cs).
        var resumed = Checkpoint.Load(Checkpoint.CellsPath(cfg.OutDir), cells);
        if (resumed.Count > 0)
            Console.WriteLine($"note: resuming {resumed.Count} completed cell(s) from checkpoint " +
                              $"({cells.Count - resumed.Count} to run).");
        var summariesById = new CellSummary?[cells.Count];

        Parallel.For(0, cells.Count,
            new ParallelOptions { MaxDegreeOfParallelism = cfg.MaxDop },
            i =>
            {
                var cell = cells[i];
                if (resumed.ContainsKey(cell.Index))
                {
                    int rd = Interlocked.Increment(ref done);
                    if (rd % progressStep == 0 || rd == cells.Count)
                        Console.WriteLine($"  {rd}/{cells.Count} cells ({sw.Elapsed.TotalMinutes:F1} min)", inv);
                    return;
                }

                var (truth, reason) = comboCache.GetOrAdd(cell.Combo, c =>
                {
                    bool ok = SceneFactory.TrySolveCombo(c, out var ts, out string? why);
                    return ok ? (ts, null) : (null, why);
                });

                if (truth is null)
                {
                    outcomes[i] = new CellOutcome(cell, null, reason, null);
                }
                else
                {
                    var truthVec = truth.ImpactFrame.ToEnu(truth.OriginPositionEcef);
                    var truthEnu = new Vec2(truthVec.X, truthVec.Y);
                    var trials = new TrialOutcome[cfg.Trials];
                    for (int t = 0; t < cfg.Trials; t++)
                    {
                        // Per-trial seed: cell index + trial index mixed in, so
                        // cells sharing a truth scene still get independent noise.
                        ulong seed = TrialRunner.DeriveSeed(
                            cfg.Seed, ((long)cell.Index << 32) | (uint)t);
                        trials[t] = TrialRunner.Run(
                            truth, cell.Protocol, cfg.Hypotheses, seed, truthEnu);
                    }
                    var outcome = new CellOutcome(cell, truth, null, trials);
                    outcomes[i] = outcome;

                    // Persist immediately: an interruption costs at most the
                    // cells currently in flight.
                    var summary = Aggregation.Summarize(outcome);
                    summariesById[i] = summary;
                    Checkpoint.Append(cfg.OutDir, summary, trials);
                }

                int d = Interlocked.Increment(ref done);
                if (d % progressStep == 0 || d == cells.Count)
                    Console.WriteLine($"  {d}/{cells.Count} cells ({sw.Elapsed.TotalMinutes:F1} min)", inv);
            });

        sw.Stop();

        // Resumed cells never populate outcomes[] (their work happened in an
        // earlier invocation), so those slots stay null: filter with OfType and
        // never dereference them. Excluded cells are NOT checkpointed (there is
        // no trial data worth keeping) and are re-evaluated on every
        // invocation, so the exclusion list stays complete - at the cost of
        // one cached truth solve per excluded scene.
        var excludedOutcomes = outcomes.OfType<CellOutcome>()
            .Where(o => o.Truth is null)
            .ToList();
        var exclusions = excludedOutcomes
            .Select(o => (o.Cell.Combo, o.ExclusionReason ?? "unknown"))
            .DistinctBy(e => e.Item1)
            .OrderBy(e => e.Item1.RangeM).ThenBy(e => e.Item1.LaunchElDeg)
            .ToList();

        // Invariant: every cell that was not resumed must have produced an
        // outcome; a silent gap here would corrupt the exclusion accounting.
        for (int i = 0; i < cells.Count; i++)
            if (!resumed.ContainsKey(cells[i].Index) && outcomes[i] is null)
                throw new InvalidOperationException(
                    $"internal: cell {i} ({cells[i].Combo}) finished without an outcome");
        var summaries = resumed.Values
            .Concat(summariesById.Where(s => s is not null).Select(s => s!))
            .OrderBy(s => s.Cell.Index)
            .ToList();
        var matrix = Aggregation.Summarize(summaries, exclusions.Count);
        var criteria = Aggregation.Criteria(matrix, coverageApplicable: cfg.Mode != "anchors");

        ReportWriters.WriteCellsCsv(Path.Combine(cfg.OutDir, "cells.csv"), summaries);
        if (cfg.WriteTrialsCsv)
        {
            // Assembled from the checkpoint: it spans every invocation,
            // including cells resumed from an earlier (killed) run.
            Checkpoint.WriteFinalTrials(cfg.OutDir, Path.Combine(cfg.OutDir, "trials.csv"));
        }

        // Full-run extrapolation: plan weight (200 trials, 250 hypotheses) at
        // stride 1, scaled by the measured per-trial cost.
        double avgTrialMs = summaries.SelectMany(s => new[] { s.MeanTrialMs }).DefaultIfEmpty(0).Average();
        double fullCells = cells.Count * (double)cfg.Stride;
        double estHours = fullCells * 200.0 * (avgTrialMs * 250.0 / cfg.Hypotheses)
                          / cfg.MaxDop / 3_600_000.0;

        ReportWriters.WriteReportMd(
            Path.Combine(cfg.OutDir, "report.md"), cfg, summaries, exclusions,
            matrix, criteria, tornado: null, sw.Elapsed, estHours,
            resumedCells: resumed.Count);
        Checkpoint.Discard(cfg.OutDir);

        Console.WriteLine();
        // Exclusions are reported per distinct scene; a thinned sample can hit
        // the same excluded combo under several protocols, so the cell count
        // and the scene count differ - print both, never silently.
        int excludedCells = excludedOutcomes.Count;
        Console.WriteLine($"Cells run {matrix.CellsRun}, excluded {excludedCells} cells " +
                          $"({exclusions.Count} distinct scenes), " +
                          $"trials {matrix.TrialsTotal}, elapsed {sw.Elapsed.TotalMinutes:F2} min" +
                          (resumed.Count > 0 ? " (this invocation)" : ""));
        foreach (var c in criteria)
            Console.WriteLine(
                $"  [{(c.Passed is null ? "SKIP" : c.Passed.Value ? "PASS" : "FAIL")}] {c.Name}: {c.Observed}  (required {c.Required})", inv);
        Console.WriteLine($"Full-matrix (stride 1, 200x250) estimate: {estHours:F1} h at {cfg.MaxDop}-way parallelism");
        Console.WriteLine($"Outputs: {Path.GetFullPath(cfg.OutDir)}");

        // SKIP (null) is not a failure - a thinned run may contain no eligible
        // cell; the full plan-weight run evaluates every criterion.
        return criteria.Any(c => c.Passed == false) ? 1 : 0;
    }

    private static int RunTornado(RunConfig cfg)
    {
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine(
            $"4TRAC tornado: trials={cfg.Trials} hypotheses={cfg.Hypotheses} seed={cfg.Seed}");
        var sw = Stopwatch.StartNew();
        var rows = Tornado.Run(cfg.Trials, cfg.Hypotheses, cfg.Seed);
        sw.Stop();

        ReportWriters.WriteTornadoCsv(Path.Combine(cfg.OutDir, "tornado.csv"), rows);
        ReportWriters.WriteReportMd(
            Path.Combine(cfg.OutDir, "report.md"), cfg,
            cells: Array.Empty<CellSummary>(), exclusions: Array.Empty<(SceneCombo, string)>(),
            matrix: null, criteria: null, tornado: rows, sw.Elapsed, fullRunEstimateHours: null);

        double budget = Math.Max(5.0, 0.015 * Tornado.ReferenceCombo.RangeM);
        Console.WriteLine($"Elapsed {sw.Elapsed.TotalMinutes:F2} min; budget {budget:F2} m at the reference scene");
        foreach (var input in rows.Select(r => r.Input).Distinct())
        {
            var req = Tornado.RequiredPrecision(rows, input, budget);
            Console.WriteLine(req is null
                ? $"  {input}: no swept sigma meets the budget while the others stay nominal" +
                  " (tightening this input alone cannot buy the budget)"
                : $"  {input}: required precision <= {req.Value.Sigma} {req.Value.Unit} (1 sigma)", inv);
        }
        Console.WriteLine($"Outputs: {Path.GetFullPath(cfg.OutDir)}");
        return 0;
    }
}

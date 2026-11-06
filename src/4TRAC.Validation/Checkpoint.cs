using System.Globalization;

namespace FourTrac.Validation;

/// <summary>
/// Crash-safe incremental persistence for long matrix runs. Every completed
/// cell is appended (flushed) to checkpoint/cells.csv in the SAME column
/// format as the final cells.csv, and its raw trial rows to
/// checkpoint/trials.csv; a restarted run skips completed cells and the final
/// artifacts are assembled from the checkpoint, so an interruption costs at
/// most the cells that were in flight.
///
/// Integrity rules (never silent):
/// - a torn TAIL line (killed mid-append) is ignored - the only safe partial;
/// - corruption anywhere else aborts with the line number;
/// - every row is validated against the current matrix (cell indices are
///   position-dependent, so a checkpoint from a different stride/anchor set
///   is an ERROR, not a silent skip).
/// - excluded cells are deliberately NOT persisted: they carry no trial data,
///   so each invocation re-evaluates them and the exclusion list in the report
///   is always complete for the whole matrix.
/// </summary>
public static class Checkpoint
{
    public static string CellsPath(string outDir) =>
        Path.Combine(outDir, "checkpoint", "cells.csv");

    public static string TrialsPath(string outDir) =>
        Path.Combine(outDir, "checkpoint", "trials.csv");

    private static readonly object Gate = new();

    /// <summary>Load completed cells; validates every row against the current
    /// matrix. Returns empty when no checkpoint exists.</summary>
    public static Dictionary<int, CellSummary> Load(string path, IReadOnlyList<DesignCell> cells)
    {
        var result = new Dictionary<int, CellSummary>();
        if (!File.Exists(path)) return result;

        string[] lines = File.ReadAllLines(path);
        for (int li = 1; li < lines.Length; li++)   // 0 = header
        {
            string line = lines[li].Trim();
            if (line.Length == 0) continue;
            bool last = li == lines.Length - 1;

            CellSummary summary;
            try
            {
                summary = ParseCellRow(line);
            }
            catch (Exception) when (last)
            {
                break;   // torn tail from a killed append
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Checkpoint {path} is corrupt at line {li + 1}: {ex.Message}. " +
                    "Delete the checkpoint directory to restart from zero.", ex);
            }

            Validate(summary, cells, path, li + 1);
            result[summary.Cell.Index] = summary;
        }
        return result;
    }

    /// <summary>Append one completed cell (summary + raw trial rows) and flush.
    /// Thread-safe: cells finish on parallel workers.</summary>
    public static void Append(string outDir, CellSummary summary, IReadOnlyList<TrialOutcome> trials)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CellsPath(outDir))!);
            if (!File.Exists(CellsPath(outDir)))
                File.WriteAllText(CellsPath(outDir), ReportWriters.CellHeader + Environment.NewLine);
            if (!File.Exists(TrialsPath(outDir)))
                File.WriteAllText(TrialsPath(outDir), ReportWriters.TrialHeader + Environment.NewLine);

            File.AppendAllText(CellsPath(outDir), ReportWriters.CellRow(summary) + Environment.NewLine);

            var sb = new System.Text.StringBuilder();
            for (int t = 0; t < trials.Count; t++)
                sb.AppendLine(ReportWriters.TrialRow(summary.Cell.Index, t, trials[t]));
            File.AppendAllText(TrialsPath(outDir), sb.ToString());
        }
    }

    /// <summary>Assemble the final trials.csv from the checkpoint (it spans
    /// every invocation, including resumed cells). Writes the header alone
    /// when no trial rows exist.</summary>
    public static void WriteFinalTrials(string outDir, string finalPath)
    {
        string path = TrialsPath(outDir);
        if (File.Exists(path))
            File.Copy(path, finalPath, overwrite: true);
        else
            File.WriteAllText(finalPath, ReportWriters.TrialHeader + Environment.NewLine);
    }

    /// <summary>Remove the checkpoint after a completed run (the final
    /// artifacts are complete; a fresh invocation starts from zero).</summary>
    public static void Discard(string outDir)
    {
        string dir = Path.Combine(outDir, "checkpoint");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private static CellSummary ParseCellRow(string line)
    {
        string[] f = line.Split(',');
        if (f.Length != 24)
            throw new FormatException($"expected 24 fields, got {f.Length}");

        var inv = CultureInfo.InvariantCulture;
        // The writer emits "" for NaN (e.g. BudgetM on non-eligible cells);
        // reverse that convention EXACTLY - NaN stays NaN, never a silent 0.
        double D(int i) => f[i].Length == 0 ? double.NaN : double.Parse(f[i], inv);
        int I(int i) => int.Parse(f[i], inv);
        bool B(int i) => f[i] switch
        {
            "1" => true,
            "0" => false,
            _ => throw new FormatException($"field {i} is not a flag: '{f[i]}'"),
        };

        int index = I(0);
        var combo = new SceneCombo(D(1), D(2), D(3), D(4));
        var protocol = new CellProtocol(D(5), D(6), D(7), D(8));
        return new CellSummary(
            new DesignCell(index, combo, protocol),
            TruthMvMps: D(9),
            TruthExteriorDeg: D(10),
            TruthTofS: D(11),
            TrialsRun: I(12),
            Coverage50: D(13),
            Coverage95: D(14),
            MedianErrorM: D(15),
            MapErrorM: D(16),
            MeanArea50M2: D(17),
            MeanArea95M2: D(18),
            MaxFailedHypotheses: I(19),
            MeanTrialMs: D(20),
            Eligible: B(21),
            ErrorBudgetM: D(22),
            LowMachExtension: B(23));
    }

    private static void Validate(CellSummary s, IReadOnlyList<DesignCell> cells,
                                 string path, int line)
    {
        string Fail(string why) => throw new InvalidOperationException(
            $"Checkpoint {path} line {line}: {why}. " +
            "Delete the checkpoint directory to restart from zero.");

        int i = s.Cell.Index;
        if (i < 0 || i >= cells.Count)
            Fail($"cell index {i} is outside the current matrix ({cells.Count} cells)");

        var c = cells[i];
        static bool Eq(double a, double b) => Math.Abs(a - b) <= 1e-6;
        if (!Eq(c.Combo.RangeM, s.Cell.Combo.RangeM) ||
            !Eq(c.Combo.LaunchElDeg, s.Cell.Combo.LaunchElDeg) ||
            !Eq(c.Combo.AzimuthDeg, s.Cell.Combo.AzimuthDeg) ||
            !Eq(c.Combo.WindSpeedMps, s.Cell.Combo.WindSpeedMps) ||
            !Eq(c.Protocol.AngleNoiseSigmaDeg, s.Cell.Protocol.AngleNoiseSigmaDeg) ||
            !Eq(c.Protocol.MvPriorHalfWidthMps, s.Cell.Protocol.MvPriorHalfWidthMps) ||
            !Eq(c.Protocol.BcSigmaFraction, s.Cell.Protocol.BcSigmaFraction) ||
            !Eq(c.Protocol.WindSigmaMps, s.Cell.Protocol.WindSigmaMps))
            Fail($"cell {i} was produced by a different matrix/protocol combination");
    }
}

using FourTrac.Validation;

namespace FourTrac.Tests;

/// <summary>
/// Checkpoint integrity: a long V2 run must survive an interrupted process
/// without losing completed cells and without ever mixing rows from a
/// different matrix (indices are position-dependent).
/// </summary>
public class CheckpointTests
{
    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "4trac-ckpt-" + Guid.NewGuid().ToString("N"));

    private static CellSummary MakeSummary(DesignCell cell) => new(
        cell,
        TruthMvMps: 123.45, TruthExteriorDeg: 12.345, TruthTofS: 1.234,
        TrialsRun: 3, Coverage50: 0.5000, Coverage95: 0.9750, MedianErrorM: 2.500,
        MapErrorM: 1.750, MeanArea50M2: 100.5, MeanArea95M2: 400.25,
        MaxFailedHypotheses: 2, MeanTrialMs: 1234.5, Eligible: true,
        ErrorBudgetM: double.NaN, LowMachExtension: false);

    [Fact]
    public void Checkpoint_RoundTripsCellRowsThroughTheSameCsvFormat()
    {
        string dir = NewTempDir();
        try
        {
            var cells = DesignMatrix.SmokeMatrix();
            var s0 = MakeSummary(cells[0]);
            var s1 = MakeSummary(cells[1]);
            Checkpoint.Append(dir, s0, Array.Empty<TrialOutcome>());
            Checkpoint.Append(dir, s1, Array.Empty<TrialOutcome>());

            var loaded = Checkpoint.Load(Checkpoint.CellsPath(dir), cells);

            Assert.Equal(2, loaded.Count);
            var back = loaded[cells[0].Index];
            Assert.Equal(s0.Coverage50, back.Coverage50, 4);
            Assert.Equal(s0.Coverage95, back.Coverage95, 4);
            Assert.Equal(s0.MedianErrorM, back.MedianErrorM, 3);
            Assert.Equal(s0.MapErrorM, back.MapErrorM, 3);
            Assert.Equal(s0.MeanArea50M2, back.MeanArea50M2, 1);
            Assert.Equal(s0.MeanArea95M2, back.MeanArea95M2, 1);
            Assert.Equal(s0.MeanTrialMs, back.MeanTrialMs, 1);
            Assert.Equal(s0.MaxFailedHypotheses, back.MaxFailedHypotheses);
            Assert.Equal(s0.Eligible, back.Eligible);
            Assert.Equal(s0.LowMachExtension, back.LowMachExtension);
            Assert.True(double.IsNaN(back.ErrorBudgetM),
                "empty field must round-trip as NaN (non-eligible cell has no budget)");
            Assert.Equal(s0.Cell.Combo.RangeM, back.Cell.Combo.RangeM);
            Assert.Equal(s0.Cell.Protocol.AngleNoiseSigmaDeg, back.Cell.Protocol.AngleNoiseSigmaDeg);

            // The final trials.csv is assembled from the checkpoint; with no
            // trial rows it must still be a well-formed header-only file.
            string finalTrials = Path.Combine(dir, "trials.csv");
            Checkpoint.WriteFinalTrials(dir, finalTrials);
            Assert.Equal(ReportWriters.TrialHeader,
                File.ReadLines(finalTrials).First());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Checkpoint_RejectsRowsFromADifferentMatrix()
    {
        string dir = NewTempDir();
        try
        {
            var cells = DesignMatrix.SmokeMatrix();
            Checkpoint.Append(dir, MakeSummary(cells[1]), Array.Empty<TrialOutcome>());

            // Same index, different scene: a checkpoint from another stride or
            // anchor set must abort, never silently skip or mix.
            var other = new List<DesignCell>
            {
                cells[0],
                new(1, new SceneCombo(50, 45, 270, 8), DesignMatrix.AchievableProtocol),
            };
            var ex = Assert.Throws<InvalidOperationException>(
                () => Checkpoint.Load(Checkpoint.CellsPath(dir), other));
            Assert.Contains("different matrix", ex.Message);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Checkpoint_IgnoresATornTailLine()
    {
        string dir = NewTempDir();
        try
        {
            var cells = DesignMatrix.SmokeMatrix();
            Checkpoint.Append(dir, MakeSummary(cells[0]), Array.Empty<TrialOutcome>());

            // Simulate a kill mid-append: an incomplete row with no newline.
            File.AppendAllText(Checkpoint.CellsPath(dir), "1,2,3");

            var loaded = Checkpoint.Load(Checkpoint.CellsPath(dir), cells);
            Assert.Single(loaded);
            Assert.True(loaded.ContainsKey(cells[0].Index));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}

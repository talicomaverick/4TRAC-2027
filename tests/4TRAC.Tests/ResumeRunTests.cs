using FourTrac.Validation;

namespace FourTrac.Tests;

/// <summary>
/// End-to-end resume gate for the V2 harness: a run that resumes cells from a
/// checkpoint must finish exactly like an uninterrupted run.
///
/// Regression: the first implementation resumed cells correctly but left their
/// CellOutcome slots null, so the end-of-run exclusion accounting crashed with
/// a NullReferenceException AFTER every cell had finished - i.e. it would have
/// destroyed a 30+ hour unattended production run at the final step. Unit
/// tests of Checkpoint.Load/Append alone did not catch it; only a full
/// Program.Main resume does.
/// </summary>
public class ResumeRunTests
{
    private static string[] SmokeArgs(string dir) =>
        new[] { "--mode", "smoke", "--trials", "1", "--hypotheses", "4", "--out", dir };

    [Fact]
    public void PartiallyResumedRun_CompletesWithCompleteArtifacts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "4trac-resume-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string[] args = SmokeArgs(dir);
            int first = Program.Main(args);

            string cellsPath = Path.Combine(dir, "cells.csv");
            string trialsPath = Path.Combine(dir, "trials.csv");
            Assert.True(File.Exists(cellsPath));
            string[] firstCells = File.ReadAllLines(cellsPath);
            Assert.Equal(3, firstCells.Length);              // header + 2 smoke cells
            string[] firstTrials = File.ReadAllLines(trialsPath);

            // Plant a checkpoint holding only the FIRST cell + its trial rows,
            // exactly what an interruption after one completed cell leaves.
            string cp = Path.Combine(dir, "checkpoint");
            Directory.CreateDirectory(cp);
            File.WriteAllLines(Path.Combine(cp, "cells.csv"),
                new[] { firstCells[0], firstCells[1] });
            File.WriteAllLines(Path.Combine(cp, "trials.csv"),
                firstTrials.Where((l, i) => i == 0 || l.StartsWith("0,", StringComparison.Ordinal))
                           .ToArray());

            int second = Program.Main(args);                  // must NOT throw

            Assert.Equal(first, second);                      // same verdict, not a crash
            Assert.True(File.Exists(Path.Combine(dir, "report.md")));
            Assert.False(Directory.Exists(cp), "checkpoint must be discarded after success");

            string[] resumedCells = File.ReadAllLines(cellsPath);
            Assert.Equal(3, resumedCells.Length);
            Assert.Equal(firstCells.Select(l => l.Split(',')[0]),
                         resumedCells.Select(l => l.Split(',')[0]));

            string[] resumedTrials = File.ReadAllLines(trialsPath);
            Assert.Equal(3, resumedTrials.Length);            // header + 1 resumed + 1 new trial
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FullyResumedRun_IsByteIdenticalAndDoesNotCrash()
    {
        string dir = Path.Combine(Path.GetTempPath(), "4trac-resume-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string[] args = SmokeArgs(dir);
            int first = Program.Main(args);

            string cellsPath = Path.Combine(dir, "cells.csv");
            string trialsPath = Path.Combine(dir, "trials.csv");
            byte[] cellsBefore = File.ReadAllBytes(cellsPath);
            string[] firstCells = File.ReadAllLines(cellsPath);
            string[] firstTrials = File.ReadAllLines(trialsPath);

            // Every cell already done: the invocation has 0 cells to run.
            string cp = Path.Combine(dir, "checkpoint");
            Directory.CreateDirectory(cp);
            File.WriteAllLines(Path.Combine(cp, "cells.csv"), firstCells);
            File.WriteAllLines(Path.Combine(cp, "trials.csv"), firstTrials);

            int second = Program.Main(args);                  // must NOT throw

            Assert.Equal(first, second);
            Assert.True(File.Exists(Path.Combine(dir, "report.md")));
            Assert.False(Directory.Exists(cp));
            Assert.Equal(cellsBefore, File.ReadAllBytes(cellsPath));   // byte-identical cells.csv
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}

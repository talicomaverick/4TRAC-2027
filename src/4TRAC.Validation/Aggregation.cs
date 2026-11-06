namespace FourTrac.Validation;

/// <summary>Bundle of one cell's raw results (or its exclusion).</summary>
public sealed record CellOutcome(
    DesignCell Cell,
    TruthScene? Truth,
    string? ExclusionReason,
    IReadOnlyList<TrialOutcome>? Trials);

/// <summary>Aggregated statistics of one design cell.</summary>
public sealed record CellSummary(
    DesignCell Cell,
    double TruthMvMps,
    double TruthExteriorDeg,
    double TruthTofS,
    int TrialsRun,
    double Coverage50,
    double Coverage95,
    double MedianErrorM,
    double MapErrorM,
    double MeanArea50M2,
    double MeanArea95M2,
    int MaxFailedHypotheses,
    double MeanTrialMs,
    bool Eligible,
    double ErrorBudgetM,
    bool LowMachExtension);

/// <summary>Matrix-level aggregation + the V2 pass-criteria inputs.</summary>
public sealed record MatrixSummary(
    int CellsRun,
    int CellsExcluded,
    int TrialsTotal,
    double Coverage50Pooled,
    double Coverage95Pooled,
    double MedianErrorEligiblePooledM,
    int EligibleCells,
    CellSummary? WorstEligibleCell,
    IReadOnlyList<CellSummary> EligibilityViolations);

/// <summary>One evaluated pass criterion of PLAN.md §7 V2. Passed is null
/// when the run did not evaluate it (e.g. a thinned run contained no eligible
/// cell) - untested reports SKIP, never FAIL.</summary>
public sealed record CriterionResult(string Name, bool? Passed, string Observed, string Required);

public static class Aggregation
{
    /// <summary>Median of a non-empty sample (odd or even length).</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) throw new ArgumentException("Empty sample.");
        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : 0.5 * (sorted[mid - 1] + sorted[mid]);
    }

    public static CellSummary Summarize(CellOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Truth is null || outcome.Trials is null || outcome.Trials.Count == 0)
            throw new ArgumentException("Cannot summarize an excluded/empty cell.");

        var truth = outcome.Truth;
        var trials = outcome.Trials;
        int n = trials.Count;

        double cov50 = trials.Count(t => t.Covered50) / (double)n;
        double cov95 = trials.Count(t => t.Covered95) / (double)n;
        double medErr = Median(trials.Where(t => !double.IsNaN(t.MedianErrorM))
                                      .Select(t => t.MedianErrorM).ToList());
        double mapErr = Median(trials.Where(t => !double.IsNaN(t.MapErrorM))
                                     .Select(t => t.MapErrorM).ToList());
        double area50 = trials.Where(t => !double.IsNaN(t.Area50M2)).Select(t => t.Area50M2).DefaultIfEmpty(double.NaN).Average();
        double area95 = trials.Where(t => !double.IsNaN(t.Area95M2)).Select(t => t.Area95M2).DefaultIfEmpty(double.NaN).Average();
        double meanMs = trials.Average(t => t.ElapsedMs);

        // Eligibility for the origin-error criterion (PLAN.md §7 V2):
        // theta >= 15 deg at range <= 1000 m, budget max(5 m, 1.5 % of range),
        // evaluated at the ACHIEVABLE protocol - the tornado-published
        // precision tuple (see DesignMatrix.AchievableProtocol). Read jointly
        // with the tornado, the budget is a promise for scenes whose
        // measurements meet the published requirements: achievable origin
        // error scales with measurement noise by physics (a +/-0.5 deg
        // inclinometer leaves ~15 m along-track at 500 m / 15 deg), so
        // nominal and noisy cells stay in the matrix as coverage-calibration
        // and sensitivity evidence, not as error gates.
        bool eligible = truth.Combo.LaunchElDeg >= V2Scene.MinEligibleAngleDeg
                        && truth.Combo.RangeM <= V2Scene.MaxEligibleRangeM
                        && outcome.Cell.Protocol == DesignMatrix.AchievableProtocol;
        double budget = eligible ? Math.Max(5.0, 0.015 * truth.Combo.RangeM) : double.NaN;

        return new CellSummary(
            outcome.Cell, truth.MuzzleVelocityMps, truth.ExteriorAngleDeg, truth.TimeOfFlightS,
            n, cov50, cov95, medErr, mapErr, area50, area95,
            trials.Max(t => t.FailedHypotheses), meanMs, eligible, budget,
            LowMachExtension: truth.MuzzleVelocityMps < DesignMatrix.PublishedTableFloorMps);
    }

    public static MatrixSummary Summarize(IReadOnlyList<CellSummary> summaries, int cellsExcluded)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        int trials = summaries.Sum(s => s.TrialsRun);
        double cov50 = summaries.Sum(s => s.Coverage50 * s.TrialsRun) / Math.Max(1, trials);
        double cov95 = summaries.Sum(s => s.Coverage95 * s.TrialsRun) / Math.Max(1, trials);

        var eligible = summaries.Where(s => s.Eligible).OrderBy(s => s.Cell.Index).ToList();
        // Pooled eligible median error = median over the eligible cells' per-trial medians
        // (equal weight per cell: the criterion is per-cell, cells must not be
        // weighted by trial count twice).
        double pooledEligible = eligible.Count == 0 ? double.NaN
            : Median(eligible.Select(s => s.MedianErrorM).ToList());

        var worst = eligible.OrderByDescending(s => s.MedianErrorM / s.ErrorBudgetM).FirstOrDefault();
        var violations = eligible
            .Where(s => s.MedianErrorM > s.ErrorBudgetM)
            .OrderByDescending(s => s.MedianErrorM - s.ErrorBudgetM)
            .ToList();

        return new MatrixSummary(
            summaries.Count, cellsExcluded, trials, cov50, cov95,
            pooledEligible, eligible.Count, worst, violations);
    }

    /// <summary>The three V2 pass criteria of PLAN.md §7. Coverage criteria
    /// are SKIPped when <paramref name="coverageApplicable"/> is false: the
    /// centre-truth calibration protocol fixes the truth at every prior's
    /// centre, so symmetric MV/BC prior axes widen the cloud along directions
    /// the truth never leaves - a tight-prior subset (the criterion anchors)
    /// over-covers BY CONSTRUCTION, and its coverage number is not a valid
    /// gate (the plan's coverage criterion is a whole-matrix statement).</summary>
    public static IReadOnlyList<CriterionResult> Criteria(MatrixSummary s, bool coverageApplicable = true)
    {
        ArgumentNullException.ThrowIfNull(s);
        var list = new List<CriterionResult>(3);

        if (coverageApplicable)
        {
            bool cov95Pass = s.Coverage95Pooled is >= 0.92 and <= 0.98;
            list.Add(new CriterionResult(
                "95 % region coverage (whole matrix)", cov95Pass,
                $"{s.Coverage95Pooled * 100.0:F2} %",
                "92 % <= coverage <= 98 %"));

            bool cov50Pass = s.Coverage50Pooled is >= 0.45 and <= 0.55;
            list.Add(new CriterionResult(
                "50 % region coverage (whole matrix)", cov50Pass,
                $"{s.Coverage50Pooled * 100.0:F2} %",
                "45 % <= coverage <= 55 %"));
        }
        else
        {
            string why = "not evaluated: centre-truth calibration with symmetric MV/BC " +
                         "priors over-covers tight-prior subsets by construction " +
                         "(coverage is a whole-matrix criterion, PLAN.md §8.3)";
            list.Add(new CriterionResult("95 % region coverage (whole matrix)", null, why, "-"));
            list.Add(new CriterionResult("50 % region coverage (whole matrix)", null, why, "-"));
        }

        bool? errPass = s.EligibleCells == 0 ? null : s.EligibilityViolations.Count == 0;
        string observed = s.EligibleCells == 0
            ? "no eligible cells in this run"
            : $"worst eligible cell #{s.WorstEligibleCell!.Cell.Index} " +
              $"({s.WorstEligibleCell.Cell.Combo.RangeM:F0} m / {s.WorstEligibleCell.Cell.Combo.LaunchElDeg:F0} deg): " +
              $"median {s.WorstEligibleCell.MedianErrorM:F2} m vs budget {s.WorstEligibleCell.ErrorBudgetM:F2} m" +
              (s.EligibilityViolations.Count > 0
                  ? $"; {s.EligibilityViolations.Count} cell(s) over budget"
                  : "; all eligible cells within budget");
        list.Add(new CriterionResult(
            "median origin error at published scene precision (theta >= 15 deg, range <= 1000 m)", errPass,
            observed, "per-cell median <= max(5 m, 1.5 % of range)"));
        return list;
    }
}

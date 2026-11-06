namespace FourTrac.Validation;

/// <summary>
/// Scene-defining axes of a V2 design cell: the truth trajectory is solved
/// exactly once per combo (its muzzle velocity is whatever lands the shot at
/// the declared range from the declared launch elevation).
/// </summary>
public sealed record SceneCombo(double RangeM, double LaunchElDeg, double AzimuthDeg, double WindSpeedMps);

/// <summary>
/// Inverse-protocol axes: the measurement/prior uncertainties the inverse is
/// told about for a cell. The truth always sits at the centre of every
/// declared prior (calibration protocol - coverage is meaningful only for
/// correctly-specified priors; systematic misdeclaration is a tornado input).
/// </summary>
public sealed record CellProtocol(
    double AngleNoiseSigmaDeg,
    double MvPriorHalfWidthMps,
    double BcSigmaFraction,
    double WindSigmaMps);

/// <summary>One fully-specified design cell (combo + protocol).</summary>
public sealed record DesignCell(int Index, SceneCombo Combo, CellProtocol Protocol);

/// <summary>
/// The V2 design matrix of PLAN.md §7: 6 ranges x 4 launch elevations x
/// 4 azimuths x 3 wind speeds x 4 angle-noise sigmas x 3 MV-prior widths x
/// 3 BC sigmas = 10 368 cells, 200 Monte-Carlo trials each.
///
/// Documented protocol decisions (itemized in PLAN.md §8.3, never silent):
/// - The theta axis is the LAUNCH elevation; the observed exterior angle is a
///   derived trajectory quantity (with drag the descent is steeper than the
///   launch - the realized exterior angle is recorded per cell).
/// - Wind is a full-value crosswind FROM azimuth + 90 deg (worst-case lateral
///   deflection per unit wind).
/// - "BC error" is realised as the WIDTH of the BC prior (sigma = e x BC with
///   the true BC at its centre): the matrix then measures how BC ignorance
///   inflates the origin region, and the tornado converts that into the
///   required BC knowledge. A systematic BC misdeclaration (truth off-centre)
///   biases the origin and is a separate sensitivity sweep (tornado input).
/// </summary>
public static class DesignMatrix
{
    public static readonly double[] RangesM = { 50, 100, 250, 500, 1000, 2000 };
    public static readonly double[] LaunchElevationsDeg = { 5, 15, 30, 45 };
    public static readonly double[] AzimuthsDeg = { 0, 90, 180, 270 };
    public static readonly double[] WindSpeedsMps = { 0, 3, 8 };
    public static readonly double[] AngleNoiseSigmasDeg = { 0.25, 0.5, 1.0, 2.0 };
    public static readonly double[] MvPriorHalfWidthsMps = { 1, 5, 15 };
    public static readonly double[] BcSigmaFractions = { 0.0, 0.03, 0.06 };

    /// <summary>Protocol used when the matrix has no axis for an uncertainty.</summary>
    public static readonly CellProtocol NominalProtocol = new(
        AngleNoiseSigmaDeg: 0.5, MvPriorHalfWidthMps: 5.0,
        BcSigmaFraction: 0.03, WindSigmaMps: 1.0);

    /// <summary>
    /// Scene kit meeting the tornado-published required precisions at the
    /// reference scene (sigma theta 0.25 deg, MV half-width 1 m/s at or inside
    /// the published 3 m/s, wind 0.5 m/s per axis, BC 3 %). PLAN.md §7 couples
    /// the origin-error budget with the tornado's published precision - read
    /// jointly, the budget is a promise FOR SCENES WHOSE MEASUREMENTS MEET THE
    /// PUBLISHED REQUIREMENTS (a +/-0.5 deg inclinometer at 500 m / 15 deg
    /// leaves ~15 m of along-track uncertainty - physics, not solver, and no
    /// estimator can beat the information content). Criterion anchors use this
    /// protocol; nominal and noisy-protocol cells remain in the matrix as
    /// coverage-calibration and sensitivity evidence.
    /// </summary>
    public static readonly CellProtocol AchievableProtocol = new(
        AngleNoiseSigmaDeg: 0.25, MvPriorHalfWidthMps: 1.0,
        BcSigmaFraction: 0.03, WindSigmaMps: 0.5);

    /// <summary>
    /// Lowest admissible muzzle Mach number: 0.2. Rationale (PLAN.md §8.3):
    /// the PUBLISHED G1 tables start at 400 fps (Mach ~0.36); below that the
    /// bundled transcription carries a smooth, nearly-constant low-speed
    /// extension of the curve, and the dynamics there are near-vacuum (drag
    /// deceleration of a few m/s^2 at these speeds, where the integrator is
    /// already verified against the analytic vacuum solution, V0). V2 coverage
    /// calibration is MODEL-RELATIVE - truth and inverse consume the same
    /// curve - so the extension is self-consistent for testing the inverse
    /// machinery; scenes below Mach 0.2 are degenerate lobs no standard
    /// meaningfully describes and are EXCLUDED (itemized, never silent).
    /// Cells whose muzzle velocity sits below the published 400 fps bound are
    /// flagged LOW-MACH in the report: they validate the solver, not the
    /// bullet model (real-bullet fidelity is V1's rifle-regime job).
    /// </summary>
    public const double MinValidMach = 0.2;

    /// <summary>Published-table lower bound (400 fps) - the low-Mach flag line.</summary>
    public const double PublishedTableFloorMps = 400.0 * 0.3048;

    /// <summary>Muzzle-speed validity floor at the V2 scene (Mach 0.2 at ISA 100 m).</summary>
    public static double MinValidMuzzleVelocityMps =>
        MinValidMach * V2Scene.Atmosphere.SpeedOfSound(V2Scene.HeightM);

    /// <summary>Every cell of the full V2 matrix, in canonical (stable) order.</summary>
    public static List<DesignCell> FullMatrix()
    {
        var cells = new List<DesignCell>(RangesM.Length * LaunchElevationsDeg.Length *
            AzimuthsDeg.Length * WindSpeedsMps.Length * AngleNoiseSigmasDeg.Length *
            MvPriorHalfWidthsMps.Length * BcSigmaFractions.Length);

        foreach (double range in RangesM)
        foreach (double el in LaunchElevationsDeg)
        foreach (double az in AzimuthsDeg)
        foreach (double wind in WindSpeedsMps)
        {
            var combo = new SceneCombo(range, el, az, wind);
            foreach (double sa in AngleNoiseSigmasDeg)
            foreach (double mvw in MvPriorHalfWidthsMps)
            foreach (double bcs in BcSigmaFractions)
            {
                cells.Add(new DesignCell(cells.Count, combo,
                    new CellProtocol(sa, mvw, bcs, NominalProtocol.WindSigmaMps)));
            }
        }
        return cells;
    }

    /// <summary>Protocol combos per scene combo (the aliasing guard for stride thinning).</summary>
    public static int ProtocolCombosPerScene =>
        AngleNoiseSigmasDeg.Length * MvPriorHalfWidthsMps.Length * BcSigmaFractions.Length;

    /// <summary>
    /// Deterministic thinning: every <paramref name="stride"/>-th cell of the
    /// canonical enumeration (cost control for demonstration runs - the full
    /// matrix is hours of compute even parallelized). NOTE: a stride that is
    /// a multiple of <see cref="ProtocolCombosPerScene"/> aliases the protocol
    /// axes (every sampled cell gets the same sigma/MV/BC combo); callers
    /// should nudge such strides off the multiple.
    /// </summary>
    public static List<DesignCell> FullMatrix(int stride)
    {
        var all = FullMatrix();
        if (stride <= 1) return all;
        var taken = new List<DesignCell>(all.Count / stride + 1);
        for (int i = 0; i < all.Count; i += stride)
            taken.Add(all[i] with { Index = taken.Count });
        return taken;
    }

    /// <summary>Tiny matrix for the unit-suite smoke test (seconds, not hours).</summary>
    public static List<DesignCell> SmokeMatrix()
    {
        // Short-TOF cells: the smoke gate checks the pipeline, not statistics;
        // steep/long scenes (TOF 8 s+) multiply trial cost for no gate value.
        var cells = new List<DesignCell>
        {
            new(0, new SceneCombo(100, 5, 0, 0), NominalProtocol),
            new(1, new SceneCombo(500, 5, 90, 3), NominalProtocol),
        };
        return cells;
    }

    /// <summary>
    /// Criterion anchors: every eligible (range, launch-el) pair at the
    /// ACHIEVABLE protocol (the tornado-published precision tuple), with a
    /// deterministic azimuth/wind rotation so the anchors spread over both
    /// scene axes. The origin-error budget is a promise at published scene
    /// precision (see AchievableProtocol), and an arithmetic stride can alias
    /// protocol against geometry, so a thinned run may contain no such cell -
    /// anchors keep the criterion evaluable in every run. The plan-weight
    /// matrix already contains all of them (the run unions and dedups by
    /// scene + protocol). Cells below the Mach floor are still excluded by
    /// the validity layer, with a printed reason.
    /// </summary>
    public static List<DesignCell> AnchorCells()
    {
        var cells = new List<DesignCell>();
        foreach (double range in RangesM.Where(r => r <= V2Scene.MaxEligibleRangeM))
        foreach (double el in LaunchElevationsDeg.Where(e => e >= V2Scene.MinEligibleAngleDeg))
        {
            double az = AzimuthsDeg[cells.Count % AzimuthsDeg.Length];
            double wind = WindSpeedsMps[cells.Count % WindSpeedsMps.Length];
            cells.Add(new DesignCell(cells.Count, new SceneCombo(range, el, az, wind), AchievableProtocol));
        }
        return cells;
    }
}

using System.Text;
using _4TRAC.Tests.Golden;
using FourTrac.Core.Dynamics;
using FourTrac.Core.Solver;
using Xunit.Abstractions;

namespace _4TRAC.Tests;

/// <summary>
/// V1 cross-validation (PLAN.md §7): the forward solver against 19 external
/// golden tables from three independent sources (JBM/BE semicolon tables via the
/// gehtsoft BallisticCalculator1 test corpus, Exbal "Report Trace" printouts,
/// Warner Tool Doppler sheets). Every convention below was pinned against the
/// producing implementations' source code and data - see data/golden/README.md.
///
/// V1 thresholds: velocity ≤ 0.5 %, TOF ≤ 1 %, drop/windage ≤ 1 % or 2 cm
/// (whichever is larger).
/// </summary>
public class ForwardGoldenTests(ITestOutputHelper output)
{
    private const double VelTol = 0.005;     // V1: velocity ≤ 0.5 %
    private const double TofTol = 0.01;      // V1: TOF ≤ 1 %
    private const double FracTol = 0.01;     // V1: drop/windage ≤ 1 % ...
    private const double FloorTolM = 0.02;   // V1: ... or 2 cm, whichever is larger

    /// <summary>Drop/windage tolerance per the V1 "1 % or 2 cm" rule.</summary>
    private static double AbsTol(double referenceM) =>
        Math.Max(FracTol * Math.Abs(referenceM), FloorTolM);

    /// <summary>
    /// Semicolon (JBM/BE) tables in V1 scope. The two shot-angle ("up") tables are
    /// excluded here (documented model difference, pinned in
    /// <see cref="ElevatedShotTables_FlatModelConsistent_ExcludedFromV1"/>).
    /// </summary>
    public static TheoryData<string> JbmTables => new()
    {
        "gehtsoft_g1_nowind_65gr_2600fps.txt",
        "gehtsoft_g1_twist_220gr_3007fps.txt",
        "g1_wind.txt",
        "g1_wind_cold.txt",
        "g1_wind_hot.txt",
        "g7_nowind.txt",
        "b1_bthp.txt",
        "b1_bthp_7kft.txt",
        "b1_bthp_hot.txt",
        "b1_eldm.txt",
        "b1_eldm_10kft.txt",
        "be_coriolis_north.txt",
        "be_coriolis_east.txt",
    };

    [Theory]
    [MemberData(nameof(JbmTables))]
    public void JbmFormatTable_MeetsV1Thresholds(string fileName)
    {
        var t = GoldenTables.LoadJbm(fileName);
        var check = new DeviationCheck(fileName);
        check.That(t.Rows.Count > 5, $"parser found only {t.Rows.Count} rows - header parse broken?");

        var run = GoldenScenarios.RunJbm(t);
        double hS = t.SightHeightIn * GoldenScenarios.InToM;

        foreach (var row in t.Rows)
        {
            double r = row.RangeYd * GoldenScenarios.YdToM;
            string at = $"{row.RangeYd:0} yd";
            var p = run.Solution.AtDownRange(r);
            if (p is null) { check.That(false, $"trajectory ended before the reference range {at}"); continue; }

            check.Relative("velocity", at, row.Fps * GoldenScenarios.FpsToMps, p.Value.SpeedMps, VelTol);
            check.Relative("tof", at, row.TofS, p.Value.TimeS, TofTol);

            // Drop vs line of sight: the lob flies flat through the zero, so the
            // sight line runs flat from the sight at the launch height (row 0 pins
            // it to -h_s, row ZeroYd pins it to 0). The reference's drop column
            // carries a measured far-field artifact tail past the zero range
            // (F-DROP, GoldenScenarios.JbmDropTailM / data/golden/README.md):
            // velocity and TOF stay at full V1 tightness, drop gets the additive
            // artifact allowance on top of the V1 tolerance.
            double dropRef = row.DropIn * GoldenScenarios.InToM;
            double tailM = GoldenScenarios.JbmDropTailM(r, t.ZeroYd);
            check.Absolute("drop", at, dropRef, p.Value.HeightM - hS, AbsTol(dropRef) + tailM);

            // Drift column: left-positive in the semicolon format, ours is
            // right-positive. Skipped where the column carries no signal (calm,
            // no latitude) and where the table carries spin data (g1_twist's
            // rifle line declares a twist - spin drift is an M4 item, PLAN.md §8).
            bool hasSignal = t.WindMph > 0 || t.LatitudeDeg.HasValue;
            if (hasSignal && !t.HasTwist)
            {
                // The be_coriolis tables' drift column carries the reference's
                // spin drift on top of Coriolis (their model adds it even though
                // the rifle line has no twist); it grows ~quadratically - the
                // residual over our Coriolis-only model is ~2 cm by 1100 yd and
                // 9 cm at 2000 yd for the 65 gr 1:12 probe. Spin drift is M4
                // (Litz) and V1 scope is 50-1000 m, so drift is asserted only
                // within V1 range; beyond-range rows are recorded report-only
                // (data/golden/README.md).
                double driftRef = -row.DriftIn * GoldenScenarios.InToM;
                if (r <= 1000.0)
                {
                    check.Absolute("drift", at, driftRef, p.Value.LateralM, AbsTol(driftRef));
                }
                else
                {
                    output.WriteLine($"[{fileName}] drift beyond V1 range (report-only): " +
                        $"{at}: ref {driftRef:F4} m, ours {p.Value.LateralM:F4} m " +
                        "(reference column includes spin drift)");
                }
            }
        }
        check.ThrowIfAny();
    }

    [Theory]
    [InlineData("ref_g5.txt")]
    [InlineData("ref_gi.txt")]
    public void RefTraceTable_MeetsV1Thresholds(string fileName)
    {
        var t = GoldenTables.LoadRef(fileName);
        var check = new DeviationCheck(fileName);
        double hS = t.SightHeightIn * GoldenScenarios.InToM;

        // Parse sanity pinned on the file's own first row: Drop 0.00 at the muzzle,
        // Path = -sight height.
        check.That(t.Rows.Count > 5, $"parser found only {t.Rows.Count} rows");
        check.Absolute("parse drop row0", "0 yd", 0.0, t.Rows[0].DropIn * GoldenScenarios.InToM, 0.003);
        check.Absolute("parse path row0", "0 yd", -hS, t.Rows[0].PathIn * GoldenScenarios.InToM, 0.003);

        var run = GoldenScenarios.RunRef(t);

        // Zero-dial cross-check: the printout's Sight Adj must equal the zero
        // angle our calm flat pre-run computes for the same bullet (their own
        // drop tolerance is ~0.1 MOA, so 0.15 MOA pins the convention).
        check.Absolute("zero-angle", "dial", t.SightAdjMoa, run.ZeroAngleDeg * 60.0, 0.15);

        // Their dial is carried by the sight, not the bore: Path = y - h_s + x*tan(adj).
        double adjRad = t.SightAdjMoa / 60.0 * (Math.PI / 180.0);

        foreach (var row in t.Rows)
        {
            double r = row.RangeYd * GoldenScenarios.YdToM;
            string at = $"{row.RangeYd:0} yd";
            var p = run.Solution.AtDownRange(r);
            if (p is null) { check.That(false, $"trajectory ended before the reference range {at}"); continue; }

            check.Relative("velocity", at, row.Fps * GoldenScenarios.FpsToMps, p.Value.SpeedMps, VelTol);
            check.Relative("tof", at, row.TofS, p.Value.TimeS, TofTol);

            // Flat launch: Drop = below the launch horizontal (row 0 = 0.00 pins
            // it). The Exbal-style printouts share the JBM corpus generator, so
            // the same measured far-field drop-column artifact (F-DROP) applies
            // additively to both drop and path below; velocity/TOF stay at full
            // V1 tightness.
            double tailM = GoldenScenarios.JbmDropTailM(r, t.ZeroYd);
            double dropRef = row.DropIn * GoldenScenarios.InToM;
            check.Absolute("drop", at, dropRef, p.Value.HeightM, AbsTol(dropRef) + tailM);

            double pathRef = row.PathIn * GoldenScenarios.InToM;
            check.Absolute("path", at, pathRef, p.Value.HeightM - hS + r * Math.Tan(adjRad),
                AbsTol(pathRef) + tailM);
        }
        check.ThrowIfAny();
    }

    [Fact]
    public void ExbalBandedBc_MeetsV1Thresholds()
    {
        var t = GoldenTables.LoadExbal("exbal_sierra168.csv");
        var check = new DeviationCheck("exbal_sierra168");

        var atm = GoldenScenarios.Atmosphere(59, 78, 29.53, 0);
        double soundFps = atm.SpeedOfSound(0.0) / GoldenScenarios.FpsToMps;
        var bullet = GoldenScenarios.BandedSierraProfile(soundFps);
        double mv = 2561.0 * GoldenScenarios.FpsToMps;
        double hS = 1.5 * GoldenScenarios.InToM;

        // 10 mph from 90 deg: pure crosswind, so the reference's inverted along-wind
        // term vanishes - this table tests the physical wind convention directly.
        var sol = GoldenScenarios.RunZeroedLob(bullet, mv, 1.5, 100, atm, 0.0,
            WindProfile.Constant(10.0 * GoldenScenarios.MphToMps, 90.0));

        foreach (var row in t.Rows)
        {
            double r = row.RangeYd * GoldenScenarios.YdToM;
            string at = $"{row.RangeYd:0} yd";
            var p = sol.AtDownRange(r);
            if (p is null) { check.That(false, $"trajectory ended before the reference range {at}"); continue; }

            check.Relative("velocity", at, row.Fps * GoldenScenarios.FpsToMps, p.Value.SpeedMps, VelTol);
            check.Relative("tof", at, row.TofS, p.Value.TimeS, TofTol);

            double dropRef = row.DropIn * GoldenScenarios.InToM;
            check.Absolute("drop", at, dropRef, p.Value.HeightM - hS, AbsTol(dropRef));

            if (row.HasWind)
            {
                // Right-positive in the transcription (negative printout values =
                // leftward drift), direct compare with our right-positive lateral.
                double windRef = row.WindIn * GoldenScenarios.InToM;
                check.Absolute("windage", at, windRef, p.Value.LateralM, AbsTol(windRef));
            }
        }
        check.ThrowIfAny();
    }

    [Theory]
    [InlineData("warner_65_flatline.txt")]
    [InlineData("warner_338_flatline.txt")]
    public void WarnerDoppler_VelocityTable_Reproduced(string fileName)
    {
        var w = GoldenTables.LoadWarner(fileName);
        var check = new DeviationCheck(fileName);
        check.That(w.VelocityRows.Count > 5, $"parser found only {w.VelocityRows.Count} velocity rows");

        // Their measured-atmosphere suite conditions (WarnerReportTest.cs):
        // 6000 ft, 23.98 inHg station, 78 F, 25 % RH - density factor ~0.77.
        var atm = GoldenScenarios.Atmosphere(78, 25, 23.98, 6000);
        var bullet = GoldenScenarios.WarnerCdProfile(fileName, w);
        double mv = w.VelocityRows[0].Fps * GoldenScenarios.FpsToMps;   // v;0yd row = muzzle
        var sol = GoldenScenarios.RunFlat(bullet, mv, atm, 6000 * GoldenScenarios.FtToM);

        foreach (var (rangeYd, fps, tofS) in w.VelocityRows)
        {
            string at = $"{rangeYd:0} yd";
            var p = sol.AtDownRange(rangeYd * GoldenScenarios.YdToM);
            if (p is null) { check.That(false, $"trajectory ended before the reference range {at}"); continue; }

            check.Relative("velocity", at, fps * GoldenScenarios.FpsToMps, p.Value.SpeedMps, VelTol);

            // TOF is asserted above 100 yd: the 65 gr sheet's first TOF row is
            // internally inconsistent with its own velocity column (0.096 s at
            // 100 yd, while 3121.5 -> 2970 fps over 91.44 m requires 0.0985 s -
            // i.e. its printed value implies an average speed above muzzle
            // velocity). A printout anomaly of the producing tool, itemized in
            // data/golden/README.md; every row above 100 yd meets V1 on both
            // sheets, so this skips exactly the anomalous row.
            if (rangeYd > 100.0)
            {
                check.Relative("tof", at, tofS, p.Value.TimeS, TofTol);
            }
            else
            {
                output.WriteLine($"[{fileName}] tof row <= 100 yd (report-only): ref {tofS:F3} s, " +
                    $"ours {p.Value.TimeS:F4} s - sheet first-row printout anomaly");
            }
        }
        check.ThrowIfAny();
    }

    [Theory]
    [InlineData("g1_wind.txt")]
    [InlineData("g1_wind_cold.txt")]
    [InlineData("g1_wind_hot.txt")]
    public void WindConvention_ReferenceAlongWindInverted_Pin(string fileName)
    {
        var t = GoldenTables.LoadJbm(fileName);
        var check = new DeviationCheck(fileName + " [wind convention]");

        var mirrored = GoldenScenarios.RunJbm(t, mirrorWind: true);
        var physical = GoldenScenarios.RunJbm(t, mirrorWind: false);

        var last = t.Rows[^1];
        double rLast = last.RangeYd * GoldenScenarios.YdToM;
        string at = $"{last.RangeYd:0} yd";
        var pM = mirrored.Solution.AtDownRange(rLast);
        var pP = physical.Solution.AtDownRange(rLast);
        check.That(pM is not null && pP is not null, $"trajectory ended before {at}");
        if (pM is null || pP is null) { check.ThrowIfAny(); return; }

        double vRef = last.Fps * GoldenScenarios.FpsToMps;
        double devMirrored = Math.Abs(pM.Value.SpeedMps - vRef);
        double devPhysical = Math.Abs(pP.Value.SpeedMps - vRef);
        output.WriteLine($"{fileName}: v@{at} ref {last.Fps:0.0} fps; " +
                         $"mirrored dev {devMirrored / vRef:P3}; physical dev {devPhysical / vRef:P3}");

        // Mirrored - the reference's effective wind - reproduces the table at V1.
        check.Relative("velocity(mirrored)", at, vRef, pM.Value.SpeedMps, VelTol);

        // The physical wind diverges far more: their along-wind term really is
        // inverted (their WindVectorRaw feeds the FROM-cosine into the downrange
        // air velocity, turning a headwind into a tailwind drag-wise).
        check.That(devPhysical > 2.0 * devMirrored && devPhysical > 3.0,
            $"physical wind deviates only {devPhysical:F2} m/s at {at} vs mirrored {devMirrored:F2} m/s - " +
            "the reference's along-wind inversion is not reproduced by this pin");

        // The crosswind (drift) convention is physically correct and direction-
        // independent; the physical/mirrored lateral gap is only the |v_air|
        // coupling of the along-wind term (a few percent at 1000 yd).
        double driftRef = -last.DriftIn * GoldenScenarios.InToM;
        check.Absolute("drift(mirrored)", at, driftRef, pM.Value.LateralM, AbsTol(driftRef));
        double gap = Math.Abs(pP.Value.LateralM - pM.Value.LateralM);
        check.That(gap <= 0.06 * Math.Abs(pM.Value.LateralM) + 0.01,
            $"physical/mirrored lateral gap {gap:G4} m at {at} exceeds the along-wind coupling envelope");
        check.ThrowIfAny();
    }

    [Theory]
    [InlineData("g1_nowind_up.txt", "gehtsoft_g1_nowind_65gr_2600fps.txt")]
    [InlineData("g1_nowind_up_supersonic.txt", null)]
    public void ElevatedShotTables_FlatModelConsistent_ExcludedFromV1(string fileName, string? flatCounterpart)
    {
        // Documented V1 exclusion (PLAN.md §7): the reference implementation's
        // shot-angle tables come from a flat-fire-equivalent model - their own
        // code comments state TOF/velocity follow flat fire and drop is the flat
        // drop rotated perpendicular to the line of sight (~flat * cos(shot
        // angle)). A true inclined lob legitimately differs from that model;
        // cross-validating it here would measure the model difference, not solver
        // error. This pin establishes the interpretation quantitatively so the
        // exclusion is audited, not assumed (true inclined validation: M3+).
        var t = GoldenTables.LoadJbm(fileName);
        var check = new DeviationCheck(fileName);
        check.That(Math.Abs(t.ShotElevationDeg - 10.0) < 1e-9,
            $"expected a 10 deg shot angle, got {t.ShotElevationDeg}");

        var atm = GoldenScenarios.Atmosphere(t.TempF, t.RhPercent, t.PressureInHg, t.AltitudeFt);
        var bullet = GoldenScenarios.Scalar(t.Standard, t.Bc, t.MassGr, fileName);
        double heightM = t.AltitudeFt * GoldenScenarios.FtToM;
        double mv = t.MuzzleFps * GoldenScenarios.FpsToMps;
        double theta0 = GoldenScenarios.ZeroAngleDeg(bullet, atm, mv, t.ZeroYd, t.SightHeightIn, heightM);
        var flat = ForwardSolver.Solve(GoldenScenarios.Scenario(bullet, atm, mv, heightM,
            launchElevationDeg: theta0));

        double cosShot = Math.Cos(10.0 * Math.PI / 180.0);
        double hS = t.SightHeightIn * GoldenScenarios.InToM;

        foreach (var row in t.Rows)
        {
            double r = row.RangeYd * GoldenScenarios.YdToM;
            string at = $"{row.RangeYd:0} yd";
            var p = flat.AtDownRange(r);
            if (p is null) { check.That(false, $"trajectory ended before {at}"); continue; }

            // Flat-fire equivalence of their model: velocity and TOF are flat.
            check.Relative("velocity(flat)", at, row.Fps * GoldenScenarios.FpsToMps, p.Value.SpeedMps, 0.003);
            // The sheets print TOF to 3 decimals, so the reference value itself
            // carries a ±0.0005 s print quantum (~1 % of a 100 yd TOF). The
            // tolerance carries it explicitly instead of pretending the column
            // is exact.
            check.Absolute("tof(flat)", at, row.TofS, p.Value.TimeS, 0.003 * row.TofS + 0.0005);

            // Drop = flat drop rotated perpendicular to the LOS (rows with signal only).
            double dropUpRef = row.DropIn * GoldenScenarios.InToM;
            double dropFlat = p.Value.HeightM - hS;
            if (Math.Abs(dropUpRef) > 0.05)
                check.Absolute("drop(perp-rotated flat)", at, dropUpRef, dropFlat * cosShot,
                    0.02 * Math.Abs(dropUpRef));
        }

        if (flatCounterpart is not null)
        {
            // Cross-file pin: the up table vs its flat sibling (same bullet) shows
            // identical velocity/TOF and the cos(shot angle) drop ratio.
            var flatTable = GoldenTables.LoadJbm(flatCounterpart);
            check.That(flatTable.Rows.Count == t.Rows.Count, "row-count mismatch between up and flat tables");
            int n = Math.Min(flatTable.Rows.Count, t.Rows.Count);
            for (int i = 0; i < n; i++)
            {
                var up = t.Rows[i];
                var fl = flatTable.Rows[i];
                string at = $"{up.RangeYd:0} yd";
                check.Relative("velocity(up vs flat)", at, fl.Fps * GoldenScenarios.FpsToMps,
                    up.Fps * GoldenScenarios.FpsToMps, 0.0015);
                check.Relative("tof(up vs flat)", at, fl.TofS, up.TofS, 0.0015);
                if (i == n - 1 && Math.Abs(fl.DropIn) > 5.0)
                    check.Absolute("drop ratio(up vs flat)", at, fl.DropIn * cosShot, up.DropIn,
                        0.01 * Math.Abs(up.DropIn));
            }
        }
        check.ThrowIfAny();
    }

    [Theory]
    [InlineData("b1_bthp_hot.txt")]
    [InlineData("g1_wind_hot.txt")]
    public void HumidityTreatment_DeterminedByHotHumidTables(string fileName)
    {
        // Density at 100 F / 80 % RH differs ~2 % between moist (vapor-pressure
        // corrected) and dry (p/(Rd*T)) air - far above the velocity threshold, so
        // the hot/humid reference tables decide the reference's humidity
        // convention, which we then adopt for golden acceptance (README).
        var t = GoldenTables.LoadJbm(fileName);
        var moist = GoldenScenarios.RunJbm(t, moistDensity: true);
        var dry = GoldenScenarios.RunJbm(t, moistDensity: false);

        double moistErr = MaxRelVelocityDeviation(t, moist);
        double dryErr = MaxRelVelocityDeviation(t, dry);
        output.WriteLine($"{fileName}: moist-density velocity dev {moistErr:P4}; dry-density dev {dryErr:P4}");

        double best = Math.Min(moistErr, dryErr);
        Assert.True(best <= VelTol,
            $"neither humidity treatment meets V1 on {fileName}: moist {moistErr:P3}, dry {dryErr:P3}");

        // Direction pin, empirically determined on first V1 run (see
        // data/golden/README.md): the reference tables carry the moist (vapor-
        // pressure corrected) density - on g1_wind_hot the moist run matches
        // the velocity column to 0.01 % while the dry run misses by 0.55 %.
        Assert.True(moistErr < dryErr,
            $"moist density should be closer on {fileName}: moist {moistErr:P4} vs dry {dryErr:P4}");
    }

    private static double MaxRelVelocityDeviation(JbmTemplate t, GoldenRun run)
    {
        double worst = 0;
        foreach (var row in t.Rows)
        {
            var p = run.Solution.AtDownRange(row.RangeYd * GoldenScenarios.YdToM);
            if (p is null) continue;
            double vRef = row.Fps * GoldenScenarios.FpsToMps;
            worst = Math.Max(worst, Math.Abs(p.Value.SpeedMps - vRef) / vRef);
        }
        return worst;
    }

    [Fact]
    public void EldM_PublishedScalars_ModelBand_Recorded()
    {
        // The bullet is the Hornady 147 gr 6.5 mm ELD-M; the AB archive lists it
        // with BC_G1 0.697 AND BC_G7 0.351 (config.csv B1_baseline_02) as two
        // published scalar representations of the same bullet. Their spread is
        // PLAN.md §7's model-discrepancy band and is asserted directly here
        // (G1 run vs G7 run). The b1_eldm table itself was NOT generated at
        // either published scalar - its own velocity rows imply ~0.322 G7
        // (provenance finding F-B1, pinned in the JBM theory via the documented
        // BC override) - so the table match is asserted there with the
        // table-implied BC, while the published-vs-table gap is recorded here
        // report-only for data/golden/README.md.
        var t = GoldenTables.LoadJbm("b1_eldm.txt");
        var check = new DeviationCheck("b1_eldm [G1 vs G7]");
        double hS = t.SightHeightIn * GoldenScenarios.InToM;

        var g7 = GoldenScenarios.RunJbm(GoldenScenarios.Retarget(t, 0.351, "G7"));
        var g1 = GoldenScenarios.RunJbm(GoldenScenarios.Retarget(t, 0.697, "G1"));

        double bandV = 0, dropBand = 0, dropRefAtBand = 0;
        double gap7 = 0, gap1 = 0;
        string bandVAt = "", dropBandAt = "";
        foreach (var row in t.Rows)
        {
            double r = row.RangeYd * GoldenScenarios.YdToM;
            string at = $"{row.RangeYd:0} yd";
            var p7 = g7.Solution.AtDownRange(r);
            var p1 = g1.Solution.AtDownRange(r);
            if (p7 is null || p1 is null) { check.That(false, $"trajectory ended before {at}"); continue; }

            double vRef = row.Fps * GoldenScenarios.FpsToMps;
            double vBand = Math.Abs(p1.Value.SpeedMps - p7.Value.SpeedMps) / vRef;
            if (vBand > bandV) { bandV = vBand; bandVAt = at; }
            double dBand = Math.Abs((p1.Value.HeightM - hS) - (p7.Value.HeightM - hS));
            if (dBand > dropBand) { dropBand = dBand; dropBandAt = at; dropRefAtBand = Math.Abs(row.DropIn * GoldenScenarios.InToM); }

            // Report-only provenance finding F-B1: both published scalars vs the
            // table they nominally describe.
            gap7 = Math.Max(gap7, Math.Abs(p7.Value.SpeedMps - vRef) / vRef);
            gap1 = Math.Max(gap1, Math.Abs(p1.Value.SpeedMps - vRef) / vRef);
        }

        output.WriteLine($"ELD-M G1(0.697) vs G7(0.351) band: velocity max {bandV:P3} @ {bandVAt}; " +
                         $"drop max {dropBand * GoldenScenarios.MpsToFps:F2} in @ {dropBandAt}");
        output.WriteLine($"F-B1: published scalars vs table: G7 max velocity gap {gap7:P2}, " +
                         $"G1 max {gap1:P2} (table implies ~0.322 G7 - see README)");
        check.That(bandV <= 0.02, $"G1-vs-G7 velocity band {bandV:P2} exceeds 2 %");
        check.That(dropBand <= Math.Max(0.03 * dropRefAtBand, 0.05),
            $"G1-vs-G7 drop band {dropBand:F3} m at {dropBandAt} exceeds 3 % of drop / 5 cm");
        check.ThrowIfAny();
    }

    [Fact]
    public void ModelDiscrepancyBand_RepresentationModes()
    {
        // PLAN.md §7 V1: the G1/G7/CD representation spread is recorded as the
        // model-discrepancy band, not silently absorbed. Raw numbers land in
        // model_discrepancy_band_report.txt for transcription into
        // data/golden/model_discrepancy_band.md.
        var report = new StringBuilder();
        report.AppendLine("# 4TRAC model discrepancy band - raw test output");
        report.AppendLine();

        BandWarner(report, "warner_65_flatline.txt");
        BandWarner(report, "warner_338_flatline.txt");
        BandEldm(report);
        BandExbal(report);

        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "model_discrepancy_band_report.txt"),
            report.ToString());
    }

    private static void BandWarner(StringBuilder report, string fileName)
    {
        var w = GoldenTables.LoadWarner(fileName);
        var atm = GoldenScenarios.Atmosphere(78, 25, 23.98, 6000);
        double soundFps = atm.SpeedOfSound(6000 * GoldenScenarios.FtToM) / GoldenScenarios.FpsToMps;
        double heightM = 6000 * GoldenScenarios.FtToM;
        double mv = w.VelocityRows[0].Fps * GoldenScenarios.FpsToMps;

        var runs = new (string Name, ForwardSolution Sol)[]
        {
            ("CD-table", GoldenScenarios.RunFlat(GoldenScenarios.WarnerCdProfile(fileName, w), mv, atm, heightM)),
            ("BC(v)-G1", GoldenScenarios.RunFlat(GoldenScenarios.WarnerBcProfile(fileName, w, true, soundFps), mv, atm, heightM)),
            ("BC(v)-G7", GoldenScenarios.RunFlat(GoldenScenarios.WarnerBcProfile(fileName, w, false, soundFps), mv, atm, heightM)),
        };

        var check = new DeviationCheck(fileName + " [band]");
        report.AppendLine($"## {fileName} - CD table vs BC(v)-G1 vs BC(v)-G7 vs the Doppler sheet");
        foreach (var (name, sol) in runs)
        {
            foreach (var (rangeYd, fps, _) in w.VelocityRows)
            {
                var p = sol.AtDownRange(rangeYd * GoldenScenarios.YdToM);
                if (p is null) continue;
                check.Relative("velocity-" + name, $"{rangeYd:0} yd",
                    fps * GoldenScenarios.FpsToMps, p.Value.SpeedMps, 0.01);
            }
        }

        foreach (double rYd in new[] { 500.0, 1000.0, 1500.0 })
        {
            var row = w.VelocityRows.First(v => Math.Abs(v.RangeYd - rYd) < 0.5);
            double vRef = row.Fps * GoldenScenarios.FpsToMps;
            double[] vs = runs.Select(x => x.Sol.AtDownRange(rYd * GoldenScenarios.YdToM)!.Value.SpeedMps).ToArray();
            double band = (vs.Max() - vs.Min()) / vRef;
            report.AppendLine($"  v@{rYd:0} yd: " +
                string.Join(", ", runs.Zip(vs, (x, v) => $"{x.Name} {v * GoldenScenarios.MpsToFps:F1} fps")) +
                $"; sheet {row.Fps:F1} fps; band {band:P3}");
            check.That(band <= 0.02, $"representation velocity band at {rYd:0} yd = {band:P2} > 2 %");
        }
        report.AppendLine();
        check.ThrowIfAny();
    }

    private static void BandEldm(StringBuilder report)
    {
        var t = GoldenTables.LoadJbm("b1_eldm.txt");
        // Both published scalars explicitly: RunJbm(t) alone would apply the
        // F-B1 table-implied BC override (0.322), which is the JBM theory's job.
        var g7 = GoldenScenarios.RunJbm(GoldenScenarios.Retarget(t, 0.351, "G7"));
        var g1 = GoldenScenarios.RunJbm(GoldenScenarios.Retarget(t, 0.697, "G1"));
        var gFit = GoldenScenarios.RunJbm(t);   // table-implied BC 0.322 G7 (F-B1)
        double hS = t.SightHeightIn * GoldenScenarios.InToM;

        report.AppendLine("## b1_eldm.txt - scalar G7 0.351 vs scalar G1 0.697 (same published bullet)");
        report.AppendLine("   (F-B1: the table's own rows imply ~0.322 G7; fitted run included below)");
        foreach (double rYd in new[] { 500.0, 1000.0 })
        {
            var row = t.Rows.First(x => Math.Abs(x.RangeYd - rYd) < 0.5);
            var p7 = g7.Solution.AtDownRange(rYd * GoldenScenarios.YdToM)!.Value;
            var p1 = g1.Solution.AtDownRange(rYd * GoldenScenarios.YdToM)!.Value;
            var pf = gFit.Solution.AtDownRange(rYd * GoldenScenarios.YdToM)!.Value;
            double vBand = Math.Abs(p1.SpeedMps - p7.SpeedMps) / (row.Fps * GoldenScenarios.FpsToMps);
            double dropBand = Math.Abs((p1.HeightM - hS) - (p7.HeightM - hS));
            report.AppendLine($"  @{rYd:0} yd: v G7 {p7.SpeedMps * GoldenScenarios.MpsToFps:F1} / G1 {p1.SpeedMps * GoldenScenarios.MpsToFps:F1} fps" +
                              $" (band {vBand:P3}); drop band {dropBand * GoldenScenarios.MpsToFps:F2} in; " +
                              $"fitted-0.322 v {pf.SpeedMps * GoldenScenarios.MpsToFps:F1} fps vs sheet {row.Fps:F1}");
        }
        report.AppendLine();
    }

    private static void BandExbal(StringBuilder report)
    {
        var t = GoldenTables.LoadExbal("exbal_sierra168.csv");
        var atm = GoldenScenarios.Atmosphere(59, 78, 29.53, 0);
        double soundFps = atm.SpeedOfSound(0.0) / GoldenScenarios.FpsToMps;
        double mv = 2561.0 * GoldenScenarios.FpsToMps;
        var wind = WindProfile.Constant(10.0 * GoldenScenarios.MphToMps, 90.0);

        var banded = GoldenScenarios.RunZeroedLob(
            GoldenScenarios.BandedSierraProfile(soundFps), mv, 1.5, 100, atm, 0.0, wind);
        var single = GoldenScenarios.RunZeroedLob(
            GoldenScenarios.Scalar("G1", 0.447, 168, "Sierra 168 single-BC control"), mv, 1.5, 100, atm, 0.0, wind);

        var check = new DeviationCheck("exbal band");
        report.AppendLine("## exbal_sierra168 - banded BC(v) vs single-BC 0.447 control vs the printout");
        double bandAt1000 = 0;
        foreach (double rYd in new[] { 500.0, 1000.0 })
        {
            var row = t.Rows.First(x => Math.Abs(x.RangeYd - rYd) < 0.5);
            double vRef = row.Fps * GoldenScenarios.FpsToMps;
            double vBanded = banded.AtDownRange(rYd * GoldenScenarios.YdToM)!.Value.SpeedMps;
            double vSingle = single.AtDownRange(rYd * GoldenScenarios.YdToM)!.Value.SpeedMps;
            double band = Math.Abs(vSingle - vBanded) / vRef;
            if (Math.Abs(rYd - 1000) < 0.5) bandAt1000 = band;
            report.AppendLine($"  v@{rYd:0} yd: banded {vBanded * GoldenScenarios.MpsToFps:F1} / single {vSingle * GoldenScenarios.MpsToFps:F1}" +
                              $" / printout {row.Fps:F1} fps; band {band:P3}");
            check.Relative("banded-velocity", $"{rYd:0} yd", vRef, vBanded, VelTol);
        }
        // Banding must be material at 1000 yd (their published control: single-BC
        // is ~2.3 % optimistic in velocity there) - otherwise the band story is moot.
        check.That(bandAt1000 >= 0.015, $"banded-vs-single velocity band at 1000 yd only {bandAt1000:P2}");
        report.AppendLine();
        check.ThrowIfAny();
    }
}

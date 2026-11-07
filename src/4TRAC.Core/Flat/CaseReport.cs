using System.Globalization;
using System.Net;
using System.Text;
using FourTrac.Core.Geo;
using FourTrac.Core.Stats;

namespace FourTrac.Core.Flat;

/// <summary>
/// Self-contained HTML case report: every input, the declared model, the
/// seed, the verdicts, every exclusion and deviation, and the published
/// precision requirements (PLAN.md §5, §7). Numbers are printed as computed;
/// nothing is rounded into a verdict that the data does not support.
/// </summary>
public static class CaseReport
{
    public const string InputAccuracyNote =
        "Results depend directly on the accuracy of the input data. Values chosen from the calibre database are nominal catalogue figures, not measurements of the actual bullet or barrel.";
    public const string EstimatedBcNote =
        "The ballistic coefficient used comes from an UNVERIFIED ESTIMATE in the calibre database. Replace it with a verified value for the actual bullet.";

    public const string ModelVersion = "4TRAC flat-earth 1.0 (M4)";

    public static string Html(FlatAnalysisRequest q, FlatAnalysisResult r, DateTime generatedUtc, bool metric = false, bool estimatedBc = false)
    {
        double mvDiv = metric ? 1.0 : UnitConversions.FpsToMps;
        string mvUnit = metric ? "m/s" : "fps";
        string mvFmt = metric ? "F1" : "F0";
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;

        sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>");
        sb.AppendLine("<title>4TRAC case report</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;max-width:960px;margin:24px auto;color:#222}" +
                      "table{border-collapse:collapse;width:100%;margin:8px 0}td,th{border:1px solid #bbb;padding:4px 8px;text-align:left;vertical-align:top}" +
                      "th{background:#eee}.warn{background:#fff3cd;border:1px solid #d39e00;padding:8px;margin:8px 0}" +
                      "code{font-family:Consolas,monospace}" +
                      "@page{size:A4;margin:14mm}@media print{body{margin:0;max-width:none}tr,.warn{break-inside:avoid}h2{break-after:avoid}}</style></head><body>");

        sb.AppendLine("<h1>4TRAC case report</h1>");
        sb.AppendLine($"<p>Model: <code>{Enc(ModelVersion)}</code> &middot; generated {generatedUtc:yyyy-MM-dd HH:mm} UTC &middot; seed <code>{q.Seed}</code></p>");

        if (r.Notes.Count > 0 && r.Notes[0].StartsWith("CALIBRATION", StringComparison.Ordinal))
            sb.AppendLine($"<div class=\"warn\"><b>Calibration status:</b> {Enc(r.Notes[0])}</div>");
        sb.AppendLine($"<div class=\"warn\"><b>Input accuracy:</b> {Enc(InputAccuracyNote)}{(estimatedBc ? " <b>" + Enc(EstimatedBcNote) + "</b>" : "")}</div>");

        sb.AppendLine("<h2>1. Result</h2>");
        if (r.Error is not null)
        {
            sb.AppendLine($"<div class=\"warn\"><b>No region reported:</b> {Enc(r.Error)}</div>");
        }
        else
        {
            sb.AppendLine("<table><tr><th>Quantity</th><th>Value</th></tr>");
            sb.AppendLine($"<tr><td>Most likely origin (HDR mode, annotation only)</td><td>{r.ModeLatDeg.ToString("F6", c)}, {r.ModeLonDeg.ToString("F6", c)}</td></tr>");
            sb.AppendLine($"<tr><td>Range from impact</td><td>{r.ModeRangeM.ToString("F1", c)} m</td></tr>");
            sb.AppendLine($"<tr><td>Bearing from impact</td><td>{r.ModeBearingDeg.ToString("F1", c)} deg</td></tr>");
            foreach (var b in r.Bands)
                sb.AppendLine($"<tr><td>{(b.Fraction * 100).ToString("F0", c)} % region area</td><td>{b.AreaM2.ToString("F0", c)} m&sup2;</td></tr>");
            sb.AppendLine($"<tr><td>Implied muzzle velocity P5 / P50 / P95</td><td>{(r.MvP05Mps / mvDiv).ToString(mvFmt, c)} / {(r.MvP50Mps / mvDiv).ToString(mvFmt, c)} / {(r.MvP95Mps / mvDiv).ToString(mvFmt, c)} {mvUnit}</td></tr>");
            sb.AppendLine($"<tr><td>Origin range P5 / P50 / P95</td><td>{r.RangeP05M.ToString("F0", c)} / {r.RangeP50M.ToString("F0", c)} / {r.RangeP95M.ToString("F0", c)} m</td></tr>");
            sb.AppendLine($"<tr><td>KDE cell / bandwidth</td><td>{r.CellSizeM.ToString("F2", c)} m / {r.BandwidthM.ToString("F2", c)} m</td></tr>");
            sb.AppendLine("</table>");
        }

        sb.AppendLine("<h2>2. Inputs</h2><table><tr><th>Input</th><th>Value</th></tr>");
        Row(sb, "Impact latitude / longitude", $"{q.ImpactLatDeg.ToString("F6", c)}, {q.ImpactLonDeg.ToString("F6", c)} deg");
        Row(sb, "Impact elevation", $"{q.ImpactElevationM.ToString("F1", c)} m");
        Row(sb, "Origin plane relative to impact", $"{q.OriginHeightM.ToString("F2", c)} m");
        Row(sb, "Angle of incidence (below horizontal)", $"{q.ExteriorAngleDeg.ToString("F3", c)} +/- {q.ExteriorSigmaDeg.ToString("F3", c)} deg");
        Row(sb, "Travel azimuth at impact", $"{q.AzimuthDeg.ToString("F3", c)} +/- {q.AzimuthSigmaDeg.ToString("F3", c)} deg true");
        Row(sb, "Muzzle velocity prior (uniform)", $"{(q.MvMinMps / mvDiv).ToString(mvFmt, c)} to {(q.MvMaxMps / mvDiv).ToString(mvFmt, c)} {mvUnit}");
        Row(sb, "Ballistic coefficient", $"{q.BcLbIn2.ToString("F4", c)} +/- {(q.BcSigmaFraction * 100).ToString("F1", c)} % ({Enc(q.ReferenceStandard)})");
        Row(sb, "Atmosphere", metric ? $"{q.TemperatureC.ToString("F1", c)} C, {(q.PressurePa / 100.0).ToString("F1", c)} hPa, {(q.RelativeHumidity * 100).ToString("F0", c)} % RH" : $"{(q.TemperatureC * 9 / 5 + 32).ToString("F1", c)} F, {(q.PressurePa / 3386.39).ToString("F2", c)} inHg, {(q.RelativeHumidity * 100).ToString("F0", c)} % RH");
        Row(sb, "Wind (FROM, speed) and per-axis sigma", $"{(q.WindFromDeg).ToString("F1", c)} deg, {(metric ? q.WindSpeedMps : q.WindSpeedMps / UnitConversions.MphToMps).ToString("F1", c)} {(metric ? "m/s" : "mph")}; sigma {q.WindSigmaMps.ToString("F2", c)} m/s");
        if (q.SymmetricCrosswindMps > 0)
            Row(sb, "Wind mode", $"UNKNOWN WIND: crosswind {(metric ? q.SymmetricCrosswindMps : q.SymmetricCrosswindMps / UnitConversions.MphToMps).ToString("F1", c)} {(metric ? "m/s" : "mph")} applied to both sides of the path at random; no along-path wind");
        if (q.WindAloftHeightM > 0 && q.SymmetricCrosswindMps <= 0)
            Row(sb, "Wind aloft", $"{q.WindAloftFromDeg.ToString("F1", c)} deg, {(metric ? q.WindAloftSpeedMps : q.WindAloftSpeedMps / UnitConversions.MphToMps).ToString("F1", c)} {(metric ? "m/s" : "mph")} at and above {q.WindAloftHeightM.ToString("F0", c)} m over the impact plane (linear blend from the ground wind)");
        Row(sb, "Launch search", q.DescendingLaunch ? "Descending (shooter above the impact firing down)" : "Climbing (shooter at or below the plane, or lofted shot)");
        Row(sb, "Coriolis", q.Coriolis ? "on" : "off");
        Row(sb, "Spin drift", q.Spin is null ? "off" : metric ? $"on: {(q.Spin.DiameterIn * 25.4).ToString("F3", c)} mm, {q.Spin.MassGr.ToString("F1", c)} gr, twist {(q.Spin.TwistIn * 25.4).ToString("F1", c)} mm ({(q.Spin.RightHandTwist ? "right" : "left")}-hand), length {(q.Spin.LengthIn * 25.4).ToString("F2", c)} mm" : $"on: {q.Spin.DiameterIn.ToString("F4", c)} in, {q.Spin.MassGr.ToString("F1", c)} gr, twist {q.Spin.TwistIn.ToString("F1", c)} in ({(q.Spin.RightHandTwist ? "right" : "left")}-hand), length {q.Spin.LengthIn.ToString("F3", c)} in");
        Row(sb, "Hypotheses", q.Hypotheses.ToString(c));
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>3. Declared model and envelope</h2><ul>");
        sb.AppendLine("<li>Flat-earth point mass in a local ENU frame; gravity 9.80665 m/s&sup2;.</li>");
        sb.AppendLine($"<li>Validated horizontal envelope: {(r.Cloud?.EnvelopeM ?? 10000).ToString("F0", c)} m. Origins beyond it are rejected, never clamped.</li>");
        sb.AppendLine("<li>Drag: scalar G1/G7 ballistic coefficient; Mach-dependent reference function, linear interpolation between nodes.</li>");
        sb.AppendLine("<li>Origin prior: <b>flat</b> over the search area (declared assumption; see calibration).</li>");
        sb.AppendLine("<li>Muzzle velocity is a prior, solved for by bracketed root find; Monte-Carlo weights are identically 1.</li>");
        sb.AppendLine("</ul>");

        sb.AppendLine("<h2>4. Exclusions and deviations</h2><ul>");
        foreach (var n in r.Notes)
            sb.AppendLine($"<li>{Enc(n)}</li>");
        sb.AppendLine("</ul>");

        sb.AppendLine("<h2>5. Calibration record</h2>");
        sb.AppendLine("<p>Truth-drawn check with a flat origin prior (origin uniform by area in a 3&ndash;4.5 km annulus, launch found by shooting; BC, wind and Coriolis on; 0.5&deg; angle and azimuth noise):</p>");
        sb.AppendLine("<table><tr><th>Trials</th><th>Hypotheses</th><th>50 % band</th><th>95 % band</th><th>Gate</th></tr>");
        sb.AppendLine("<tr><td>150</td><td>500</td><td>49.3 %</td><td>98.0 %</td><td>45&ndash;55 % and 92&ndash;98 %: PASS (95 % at the edge)</td></tr>");
        sb.AppendLine("<tr><td>106</td><td>500</td><td>49.1 %</td><td>96.2 %</td><td>Rifle, spin ON, 500&ndash;2000 m: PASS</td></tr>");
        sb.AppendLine("<tr><td>320</td><td>500</td><td>49.7 %</td><td>94.7 %</td><td>Handgun, descending branch, flat plane 30/100 m up, 100&ndash;250 m: PASS</td></tr>");
        sb.AppendLine("</table>");
        sb.AppendLine("<p>Not covered by any calibration: impacts steeper than about 50&deg; (a near-vertical rifle suite covered 82 %), a real building or slope instead of a flat plane, unknown-wind and wind-aloft modes, terrain from a real DEM.</p>");
        sb.AppendLine("<p>This is a property of the method under the stated prior, recorded from a separate run (PLAN.md &sect;8.4). It is not recomputed for each case.</p>");

        sb.AppendLine("<h2>6. Published precision requirements</h2><p>The solver's origin error is bounded by the scene-measurement precision. The requirements are recorded in the validation harness (PLAN.md &sect;8.3), measured on the ECEF path, and are not yet re-measured on the flat model. Until they are, quote the region widths above as model output, not as a precision claim.</p>");

        sb.AppendLine("<h2>7. Limitations</h2><ul>");
        sb.AppendLine(q.DemPath is null || q.DemPath.Trim().Length == 0
            ? "<li>Flat origin plane at the input height; no DEM was used.</li>"
            : $"<li>Origin surface from DEM <code>{Enc(Path.GetFileName(q.DemPath))}</code> (ESRI ASCII, WGS84). Track outside its coverage is rejected.</li>");
        sb.AppendLine(q.WindAloftHeightM > 0 && q.SymmetricCrosswindMps <= 0
            ? "<li>Two-layer wind (ground and one layer aloft, linear blend, shared uncertainty); no fuller profile.</li>"
            : "<li>Constant wind; no layered profile.</li>");
        sb.AppendLine(q.DescendingLaunch
            ? "<li>Descending launch searched from ONE flat origin plane at the height entered; a real roof or slope has many heights.</li>"
            : "<li>Climbing launch searched only; a shooter above the impact firing down is not represented unless the Descending or Both search is used.</li>");
        sb.AppendLine("<li>Magnus effect is not modelled.</li>");
        sb.AppendLine("<li>Single shot only; no ricochet or multiple-hole reconstruction.</li>");
        sb.AppendLine("</ul>");

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string name, string value) =>
        sb.AppendLine($"<tr><td>{Enc(name)}</td><td>{value}</td></tr>");

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}

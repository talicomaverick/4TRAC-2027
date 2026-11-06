using System.Globalization;
using System.Text.RegularExpressions;

namespace _4TRAC.Tests.Golden;

/// <summary>Parsed semicolon-delimited trajectory table (JBM-style format).</summary>
internal sealed class JbmTemplate
{
    public required string FileName { get; init; }

    // ammo line
    public required double Bc { get; init; }
    public required string Standard { get; init; }   // G1 / G7
    public required double MassGr { get; init; }
    public required double MuzzleFps { get; init; }

    // rifle line
    public required double SightHeightIn { get; init; }
    public required double ZeroYd { get; init; }
    public bool HasTwist { get; init; }

    // wind line (absent = calm)
    public double WindMph { get; init; }
    public double WindFromDeg { get; init; }

    // atmosphere line (pressure = station pressure at the stated altitude,
    // confirmed from the reference implementation's TableLoader/Atmosphere ctor)
    public required double TempF { get; init; }
    public required double RhPercent { get; init; }
    public required double PressureInHg { get; init; }
    public required double AltitudeFt { get; init; }

    // shot line: elevation, windage, optional barrel azimuth + latitude (Coriolis)
    public required double ShotElevationDeg { get; init; }
    public double? BarrelAzimuthDeg { get; init; }
    public double? LatitudeDeg { get; init; }

    public required List<JbmRow> Rows { get; init; }
}

internal readonly record struct JbmRow(
    double RangeYd, double DropIn, double DriftIn, double Fps, double TofS);

/// <summary>Parsed "Report Trace" table (ref_g5/ref_gi files).</summary>
internal sealed class RefTemplate
{
    public required string FileName { get; init; }
    public required string Standard { get; init; }   // G5 / GI
    public required double Bc { get; init; }
    public required double MuzzleFps { get; init; }
    public required double SightAdjMoa { get; init; }
    public required double ZeroYd { get; init; }
    public required double MassGr { get; init; }
    public required double SightHeightIn { get; init; }
    public required double RangeSlopeDeg { get; init; }
    public required double TempF { get; init; }
    public required double RhPercent { get; init; }
    public required double PressureInHg { get; init; }
    public required double AltitudeFt { get; init; }
    public required double WindMph { get; init; }
    public required List<RefRow> Rows { get; init; }
}

internal readonly record struct RefRow(
    double RangeYd, double Fps, double PathIn, double DropIn, double TofS);

/// <summary>Exbal printout transcription (Sierra .308 168gr, banded G1 BC, 10 mph wind).</summary>
internal sealed class ExbalTemplate
{
    public required string FileName { get; init; }
    public required List<ExbalRow> Rows { get; init; }
}

/// <summary>
/// dist(yd), drop(in) vs line of sight, wind(in, right-positive - the printout
/// shows negative values for the leftward drift of its 3-o'clock wind), velocity
/// (fps), energy (ft-lb), time (s). Wind blank at 1000 yd = printout watermark.
/// </summary>
internal readonly record struct ExbalRow(
    double RangeYd, double DropIn, double WindIn, double Fps, double EnergyFtLb, double TofS)
{
    public bool HasWind => !double.IsNaN(WindIn);
}

/// <summary>Headerless Warner Doppler sheet: CD(Mach) table plus measured v(range)/TOF at ~0.77 density.</summary>
internal sealed class WarnerSheet
{
    public required double MassGr { get; init; }
    public required double DiameterIn { get; init; }
    public required List<(double Mach, double Cd, double BcG1, double BcG7)> CdRows { get; init; }
    public required List<(double RangeYd, double Fps, double TofS)> VelocityRows { get; init; }
}

/// <summary>
/// Robust number extraction from unit-suffixed fields ("59.0F", "0.365G1", "29.53inHg"):
/// takes the first numeric token, so the parse is immune to legacy code-page
/// mangling of the degree symbol in the reference files.
/// </summary>
internal static partial class RefParse
{
    [GeneratedRegex(@"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?")]
    private static partial Regex Number();

    public static double Num(string field)
    {
        var m = Number().Match(field);
        if (!m.Success)
            throw new FormatException($"No number in field '{field}'.");
        return double.Parse(m.Value, CultureInfo.InvariantCulture);
    }

    public static bool TryNum(string field, out double value)
    {
        var m = Number().Match(field);
        if (!m.Success) { value = 0; return false; }
        value = double.Parse(m.Value, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>First numeric token of the field plus the remainder after it ("0.365G1" -&gt; 0.365, "G1").</summary>
    public static (double Value, string Remainder) NumAndRest(string field)
    {
        var m = Number().Match(field);
        if (!m.Success)
            throw new FormatException($"No number in field '{field}'.");
        return (double.Parse(m.Value, CultureInfo.InvariantCulture), field[(m.Index + m.Length)..]);
    }

    public static string[] SplitSemi(string line) =>
        line.Split(';', StringSplitOptions.None);

    public static string[] SplitTabFiltered(string line) =>
        line.Split('\t', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
}

internal static class GoldenTables
{
    /// <summary>Parse a semicolon-delimited JBM-style table.</summary>
    public static JbmTemplate LoadJbm(string fileName)
    {
        var lines = File.ReadAllLines(GoldenData.Path(fileName))
            .Where(l => l.Trim().Length > 0)
            .ToArray();

        double bc = 0, mass = 0, mv = 0, hS = 0, zero = 0;
        string standard = "G1";
        bool hasTwist = false;
        double windMph = 0, windFrom = 0;
        double tempF = 59, rh = 0, inHg = 29.92, altFt = 0;
        double elev = 0, windage = 0;
        double? az = null, lat = null;
        var rows = new List<JbmRow>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var f = RefParse.SplitSemi(line);
            switch (f[0].Trim().ToLowerInvariant())
            {
                case "ammo":
                {
                    // ammo;{bc}{STD};{mass}gr;{mv}ft/s[;extras]
                    var (bcValue, bcRest) = RefParse.NumAndRest(f[1].Trim());
                    bc = bcValue;
                    standard = bcRest.Trim().ToUpperInvariant();
                    if (standard.Length == 0) standard = "G1";
                    mass = RefParse.Num(f[2]);
                    mv = RefParse.Num(f[3]);
                    break;
                }
                case "rifle":
                    hS = RefParse.Num(f[1]);
                    zero = RefParse.Num(f[2]);
                    hasTwist = f.Length > 3 && f[3].Trim().Length > 0;
                    break;
                case "wind":
                    windMph = RefParse.Num(f[1]);
                    windFrom = RefParse.Num(f[2]);
                    break;
                case "atmosphere":
                    tempF = RefParse.Num(f[1]);
                    rh = RefParse.Num(f[2]);
                    inHg = RefParse.Num(f[3]);
                    altFt = RefParse.Num(f[4]);
                    break;
                case "shot":
                    elev = RefParse.Num(f[1]);
                    windage = RefParse.Num(f[2]);
                    if (f.Length > 3 && f[3].Trim().Length > 0) az = RefParse.Num(f[3]);
                    if (f.Length > 4 && f[4].Trim().Length > 0) lat = RefParse.Num(f[4]);
                    break;
                default:
                {
                    if (line.Contains(';') && RefParse.TryNum(f[0], out double range))
                    {
                        // columns: range;drop;drop-moa;drift;drift-moa;fps;mach;energy;tof[;spin;spin-moa]
                        rows.Add(new JbmRow(
                            range,
                            RefParse.Num(f[1]),
                            RefParse.Num(f[3]),
                            RefParse.Num(f[5]),
                            RefParse.Num(f[8])));
                    }
                    break;
                }
            }
        }

        return new JbmTemplate
        {
            FileName = fileName,
            Bc = bc, Standard = standard, MassGr = mass, MuzzleFps = mv,
            SightHeightIn = hS, ZeroYd = zero, HasTwist = hasTwist,
            WindMph = windMph, WindFromDeg = windFrom,
            TempF = tempF, RhPercent = rh, PressureInHg = inHg, AltitudeFt = altFt,
            ShotElevationDeg = elev, BarrelAzimuthDeg = az, LatitudeDeg = lat,
            Rows = rows,
        };
    }

    /// <summary>Parse a "Report Trace" table (tab-separated with a label/value header block).</summary>
    public static RefTemplate LoadRef(string fileName)
    {
        var lines = File.ReadAllLines(GoldenData.Path(fileName));
        var header = new Dictionary<string, string>();
        var rows = new List<RefRow>();
        bool inBody = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (!inBody && line.Contains(':'))
            {
                // Label cells end with ':'; the next cell is the value, a unit cell
                // (if any) follows and carries no ':'. The groups are NOT uniform
                // triples: "Name:" rows have no unit cell, so walk cell by cell.
                var f = RefParse.SplitTabFiltered(line);
                for (int i = 0; i < f.Length; i++)
                {
                    if (f[i].EndsWith(':') && i + 1 < f.Length)
                    {
                        header[f[i].TrimEnd(':').Trim()] = f[i + 1];
                        i++;
                    }
                }
                continue;
            }
            if (line.StartsWith("Report Trace") || line.StartsWith("Range\t")) { inBody = true; continue; }

            // Columns: Range Velocity Energy Path Drop Drift TOF CDFC Mntum Zero Adj.
            var c = RefParse.SplitTabFiltered(line);
            if (c.Length >= 7 && RefParse.TryNum(c[0], out double range) && RefParse.TryNum(c[1], out double fps))
            {
                rows.Add(new RefRow(
                    range,
                    fps,
                    RefParse.Num(c[3]),   // Path (vs sight line)
                    RefParse.Num(c[4]),   // Drop (below line of departure)
                    RefParse.Num(c[6]))); // TOF
            }
        }

        // BC label key is the drag standard + " BC" ("G5 BC", "GI BC").
        var bcKey = header.Keys.First(k => k.EndsWith(" BC", StringComparison.Ordinal));
        string standard = bcKey[..^3].Trim().ToUpperInvariant();

        return new RefTemplate
        {
            FileName = fileName,
            Standard = standard,
            Bc = RefParse.Num(header[bcKey]),
            MuzzleFps = RefParse.Num(header["Muzzle Vel"]),
            SightAdjMoa = RefParse.Num(header["Sight Adj"]),
            ZeroYd = RefParse.Num(header["Zeroed Rng"]),
            MassGr = RefParse.Num(header["Bullet Wgt"]),
            SightHeightIn = RefParse.Num(header["Sight Height"]),
            RangeSlopeDeg = RefParse.Num(header["Range Slope"]),
            TempF = RefParse.Num(header["Temperature"]),
            RhPercent = RefParse.Num(header["Humidity"]),
            PressureInHg = RefParse.Num(header["Pressure"]),
            AltitudeFt = RefParse.Num(header["Altitude"]),
            WindMph = RefParse.Num(header["Wind Speed"]),
            Rows = rows,
        };
    }

    /// <summary>Parse a Warner Doppler sheet (comment lines start with #).</summary>
    public static WarnerSheet LoadWarner(string fileName)
    {
        double mass = 0, diam = 0;
        var cd = new List<(double, double, double, double)>();
        var vel = new List<(double, double, double)>();

        foreach (var raw in File.ReadAllLines(GoldenData.Path(fileName)))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = RefParse.SplitSemi(line);
            switch (f[0].Trim().ToLowerInvariant())
            {
                case "bullet":
                    mass = RefParse.Num(f[2]);
                    diam = RefParse.Num(f[3]);
                    break;
                case "base":
                    break; // drag standard the BC columns refer to (G1)
                case "cd":
                    cd.Add((RefParse.Num(f[1]), RefParse.Num(f[2]), RefParse.Num(f[3]), RefParse.Num(f[4])));
                    break;
                case "v":
                    vel.Add((RefParse.Num(f[1]), RefParse.Num(f[2]), RefParse.Num(f[3])));
                    break;
            }
        }

        return new WarnerSheet { MassGr = mass, DiameterIn = diam, CdRows = cd, VelocityRows = vel };
    }

    /// <summary>Parse the Exbal printout CSV transcription.</summary>
    public static ExbalTemplate LoadExbal(string fileName)
    {
        var rows = new List<ExbalRow>();
        foreach (var raw in File.ReadAllLines(GoldenData.Path(fileName)))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split(',', StringSplitOptions.TrimEntries);
            rows.Add(new ExbalRow(
                RefParse.Num(f[0]),
                RefParse.Num(f[1]),
                f[2].Length == 0 ? double.NaN : RefParse.Num(f[2]),
                RefParse.Num(f[3]),
                RefParse.Num(f[4]),
                RefParse.Num(f[5])));
        }
        return new ExbalTemplate { FileName = fileName, Rows = rows };
    }
}

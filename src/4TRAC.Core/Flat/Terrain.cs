using System.Globalization;
using FourTrac.Core.Geo;

namespace FourTrac.Core.Flat;

/// <summary>
/// Height of the origin surface relative to the impact plane, as a function of
/// east and north metres from the impact. NaN means "no data here": the
/// integrator then rejects the hypothesis instead of extrapolating.
/// </summary>
public interface IFlatSurface
{
    double Height(double eastM, double northM);
}

/// <summary>A horizontal plane at a fixed height (the flat-ground case).</summary>
public sealed class PlaneSurface : IFlatSurface
{
    private readonly double _height;

    public PlaneSurface(double heightM) => _height = heightM;

    public double Height(double eastM, double northM) => _height;
}

/// <summary>
/// Regular geographic (WGS84, degrees) elevation grid in ESRI ASCII format.
/// Export a DEM to this format from any GIS tool. Bilinear sampling; outside
/// the grid, or on NODATA, the sample is NaN.
/// </summary>
public sealed class DemGrid : IDemSource
{
    private readonly double _west;
    private readonly double _south;
    private readonly double _cell;
    private readonly int _cols;
    private readonly int _rows;
    private readonly double _noData;
    private readonly double[] _values; // row-major, row 0 = northernmost

    private DemGrid(int cols, int rows, double west, double south, double cell, double noData, double[] values)
    {
        _cols = cols;
        _rows = rows;
        _west = west;
        _south = south;
        _cell = cell;
        _noData = noData;
        _values = values;
    }

    public static DemGrid Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        for (; i < lines.Length; i++)
        {
            var parts = lines[i].Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            // Header lines are "key value" with a non-numeric key; the first data row starts with a number.
            if (parts.Length == 2 && !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                header[parts[0]] = parts[1];
            else
                break;
        }

        int cols = int.Parse(Required(header, "ncols"), CultureInfo.InvariantCulture);
        int rows = int.Parse(Required(header, "nrows"), CultureInfo.InvariantCulture);
        double cell = double.Parse(Required(header, "cellsize"), CultureInfo.InvariantCulture);
        double noData = double.Parse(Required(header, "NODATA_value"), CultureInfo.InvariantCulture);
        double west, south;
        // Corner or centre registration. Cell centres are assumed when the centre key is present.
        if (header.TryGetValue("xllcenter", out var xc) && header.TryGetValue("yllcenter", out var yc))
        {
            west = double.Parse(xc, CultureInfo.InvariantCulture) - 0.5 * cell;
            south = double.Parse(yc, CultureInfo.InvariantCulture) - 0.5 * cell;
        }
        else
        {
            west = double.Parse(Required(header, "xllcorner"), CultureInfo.InvariantCulture);
            south = double.Parse(Required(header, "yllcorner"), CultureInfo.InvariantCulture);
        }

        var values = new double[cols * rows];
        int n = 0;
        for (; i < lines.Length && n < values.Length; i++)
        {
            foreach (var tok in lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (n >= values.Length) throw new InvalidDataException($"DEM '{path}' has more values than {cols}x{rows}.");
                values[n++] = double.Parse(tok, CultureInfo.InvariantCulture);
            }
        }
        if (n != values.Length) throw new InvalidDataException($"DEM '{path}' has {n} values, expected {cols * rows}.");

        return new DemGrid(cols, rows, west, south, cell, noData, values);
    }

    private static string Required(Dictionary<string, string> h, string key) =>
        h.TryGetValue(key, out var v) ? v : throw new InvalidDataException($"DEM header is missing '{key}'.");

    /// <summary>Bilinear elevation at a geographic position (metres). NaN outside coverage or on NODATA.</summary>
    public double Sample(double latDeg, double lonDeg)
    {
        double fx = (lonDeg - _west) / _cell - 0.5;
        double fy = (latDeg - _south) / _cell - 0.5;
        int x0 = (int)Math.Floor(fx);
        int y0 = (int)Math.Floor(fy);
        if (x0 < 0 || y0 < 0 || x0 + 1 >= _cols || y0 + 1 >= _rows)
            return double.NaN;

        double tx = fx - x0, ty = fy - y0;
        double v00 = Cell(x0, _rows - 1 - y0), v10 = Cell(x0 + 1, _rows - 1 - y0);
        double v01 = Cell(x0, _rows - 2 - y0), v11 = Cell(x0 + 1, _rows - 2 - y0);
        if (double.IsNaN(v00) || double.IsNaN(v10) || double.IsNaN(v01) || double.IsNaN(v11))
            return double.NaN;
        return (1 - tx) * (1 - ty) * v00 + tx * (1 - ty) * v10 + (1 - tx) * ty * v01 + tx * ty * v11;
    }

    private double Cell(int col, int row)
    {
        double v = _values[row * _cols + col];
        return v == _noData ? double.NaN : v;
    }
}

/// <summary>
/// The DEM as an origin surface in the impact-anchored flat frame. Each grid
/// point is mapped through the exact ENU frame at the impact, so the height is
/// the true local-up coordinate. The flat model's gravity and atmosphere are
/// unchanged; only the origin surface moves.
/// </summary>
public sealed class DemSurface : IFlatSurface
{
    private readonly IDemSource _grid;
    private readonly EnuFrame _anchor;

    public DemSurface(IDemSource grid, double impactLatDeg, double impactLonDeg)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        double h0 = grid.Sample(impactLatDeg, impactLonDeg);
        if (double.IsNaN(h0))
            throw new ArgumentException("The impact point is outside the DEM coverage or on NODATA.");
        _anchor = EnuFrame.AtOrigin(impactLatDeg, impactLonDeg, h0);
    }

    /// <summary>Ellipsoidal elevation of the impact as the terrain source gives it (m).</summary>
    public double AnchorHeightM => _anchor.OriginHeightM;

    public double Height(double eastM, double northM)
    {
        var ecef = _anchor.ToEcef(new Cartesian3(eastM, northM, 0.0));
        Geodetic.EcefToLla(ecef, out double lat, out double lon, out _);
        double h = _grid.Sample(lat, lon);
        if (double.IsNaN(h)) return double.NaN;
        return _anchor.ToEnu(Geodetic.LlaToEcef(lat, lon, h)).Z;
    }
}

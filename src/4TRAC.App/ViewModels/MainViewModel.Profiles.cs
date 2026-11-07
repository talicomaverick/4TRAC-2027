using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace FourTrac.App.ViewModels;

/// <summary>One bullet (weight and type) for a calibre, with its nominal BC and drag function.</summary>
public sealed class BulletOption
{
    public string Calibre { get; init; } = "";
    public double WeightGr { get; init; }
    public string BulletType { get; init; } = "";
    public string Drag { get; init; } = "G1";
    public double? Bc { get; init; }
    public double LengthIn { get; init; }
    public double MvMinFps { get; init; }
    public double MvMaxFps { get; init; }
    public string Source { get; init; } = "";
    public string Status { get; init; } = "";

    /// <summary>Text shown in the editable box when this bullet is picked.</summary>
    public string WeightText => WeightGr.ToString("0.#", CultureInfo.InvariantCulture);

    public string Display =>
        $"{WeightText} gr  {BulletType}  " +
        (Bc is null ? "(no BC data)" : $"({Drag} {Bc.Value.ToString("0.000", CultureInfo.InvariantCulture)})") +
        (Status == "Estimate" ? "  [estimate]" : "");

    public override string ToString() => Display;
}

/// <summary>A calibre with its bullet diameter, typical factory twist and bullets.</summary>
public sealed class CalibreInfo
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public int Rank { get; init; }
    public double DiameterIn { get; init; }
    public double TwistIn { get; init; }
    public List<BulletOption> Bullets { get; } = new();
    public override string ToString() => Name;
}

public partial class MainViewModel
{
    public const string AccuracyWarning =
        "For the most accurate results the input data must be accurate. Calibre values are nominal catalogue figures " +
        "(BC, bullet length and twist vary by bullet and barrel); replace them with data for the actual bullet.";

    private List<CalibreInfo> _calibres = new();
    private CalibreInfo? _selectedCalibre;
    private IReadOnlyList<BulletOption> _bulletOptions = Array.Empty<BulletOption>();
    private string _bulletSourceNote = "";

    public ICollectionView CalibreView { get; private set; } = CollectionViewSource.GetDefaultView(new List<CalibreInfo>());

    public CalibreInfo? SelectedCalibre
    {
        get => _selectedCalibre;
        set
        {
            if (SetProperty(ref _selectedCalibre, value) && value is not null)
                ApplyCalibre(value);
        }
    }

    public IReadOnlyList<BulletOption> BulletOptions
    {
        get => _bulletOptions;
        private set => SetProperty(ref _bulletOptions, value);
    }

    public string BulletSourceNote
    {
        get => _bulletSourceNote;
        private set => SetProperty(ref _bulletSourceNote, value);
    }

    private void InitializeProfiles()
    {
        try
        {
            using var stream = typeof(MainViewModel).Assembly.GetManifestResourceStream("calibre_database.csv");
            if (stream is null) return;
            using var reader = new StreamReader(stream);
            _calibres = ParseCalibres(reader.ReadToEnd());
        }
        catch
        {
            _calibres = new();   // the database is a convenience; the form still works without it
        }

        var view = new ListCollectionView(_calibres);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CalibreInfo.Category)));
        CalibreView = view;
    }

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    private static List<CalibreInfo> ParseCalibres(string csv)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        var header = SplitCsv(lines[0]);
        int Col(string n) => header.IndexOf(n);
        int cCat = Col("Category"), cRank = Col("PopularityRank"), cName = Col("Calibre"), cDia = Col("BulletDiameterIn"),
            cTwist = Col("TypicalFactoryTwistIn"), cW = Col("BulletWeightGr"), cType = Col("BulletType"),
            cDrag = Col("DragFunctionUsed"), cBc = Col("BC_Used"), cLen = Col("EstimatedBulletLengthIn"),
            cSrc = Col("Source"), cStatus = Col("Status"), cMvLo = Col("TypicalMvMinFps"), cMvHi = Col("TypicalMvMaxFps");

        var byKey = new Dictionary<string, CalibreInfo>();
        var order = new List<CalibreInfo>();
        foreach (var line in lines.Skip(1))
        {
            var c = SplitCsv(line);
            string key = c[cCat] + "|" + c[cName];
            if (!byKey.TryGetValue(key, out var cal))
            {
                cal = new CalibreInfo
                {
                    Name = c[cName], Category = c[cCat], Rank = int.Parse(c[cRank], inv),
                    DiameterIn = double.Parse(c[cDia], inv), TwistIn = double.Parse(c[cTwist], inv),
                };
                byKey[key] = cal;
                order.Add(cal);
            }
            cal.Bullets.Add(new BulletOption
            {
                Calibre = c[cName],
                WeightGr = double.Parse(c[cW], inv),
                BulletType = c[cType],
                Drag = c[cDrag],
                Bc = double.TryParse(c[cBc], NumberStyles.Float, inv, out double bc) ? bc : null,
                LengthIn = double.Parse(c[cLen], inv),
                MvMinFps = double.Parse(c[cMvLo], inv),
                MvMaxFps = double.Parse(c[cMvHi], inv),
                Source = c[cSrc],
                Status = c[cStatus],
            });
        }
        return order;
    }

    private string Fmt(double v, int decimals) =>
        Math.Round(v, decimals).ToString("0.###############", CultureInfo.InvariantCulture);

    /// <summary>Picking a calibre fills diameter and twist, offers its bullet weights, and loads a mid-range default bullet.</summary>
    private void ApplyCalibre(CalibreInfo cal)
    {
        DiameterIn = IsMetric ? Fmt(cal.DiameterIn * MmPerIn, 3) : Fmt(cal.DiameterIn, 3);
        TwistIn = IsMetric ? Fmt(cal.TwistIn * MmPerIn, 1) : Fmt(cal.TwistIn, 2);
        BulletOptions = cal.Bullets;
        if (cal.Bullets.Count > 0) ApplyBullet(cal.Bullets[cal.Bullets.Count / 2]);
    }

    /// <summary>Picking a mass fills BC, drag function and length. Every value stays editable.</summary>
    public void ApplyBullet(BulletOption b)
    {
        MassGr = b.WeightText;
        ReferenceStandard = b.Drag;
        BallisticCoefficient = b.Bc is null ? "" : b.Bc.Value.ToString("0.000", CultureInfo.InvariantCulture);
        LengthIn = IsMetric ? Fmt(b.LengthIn * MmPerIn, 2) : Fmt(b.LengthIn, 3);
        _estimatedBcText = b.Status == "Estimate" ? BallisticCoefficient : null;
        MvMinFps = IsMetric ? Fmt(b.MvMinFps * 0.3048, 1) : Fmt(b.MvMinFps, 0);
        MvMaxFps = IsMetric ? Fmt(b.MvMaxFps * 0.3048, 1) : Fmt(b.MvMaxFps, 0);
        if (b.Bc is not null)
            BcSigmaPercent = b.Status == "Estimate" ? "10" : "3";

        BulletSourceNote = b.Status switch
        {
            "Sourced" => $"BC source: {b.Source}. Nominal value; bullet length is estimated from mass. Muzzle velocity range is a typical factory span (estimate): set it from your load or chronograph data.",
            "Estimate" => "BC is an UNVERIFIED ESTIMATE (BC sigma set to 10 %). Bullet length and the muzzle velocity range (typical factory span) are also estimates.",
            _ => "No BC data available for this bullet: enter the ballistic coefficient yourself. The muzzle velocity range is a typical factory span (estimate).",
        };
    }

    private void ClearCalibreSelection()
    {
        _selectedCalibre = null;
        OnPropertyChanged(nameof(SelectedCalibre));
        BulletOptions = Array.Empty<BulletOption>();
        BulletSourceNote = "";
    }
}

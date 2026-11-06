using System.Text.Json;
using System.Text.Json.Serialization;
using FourTrac.Core.Drag;
using FourTrac.Core.Geo;

namespace FourTrac.Core.Solver;

/// <summary>How the bullet's drag is represented in a <see cref="BulletProfile"/>.</summary>
public enum DragMode
{
    /// <summary>Scalar published G-style BC (lb/in^2) against a standard reference table (G1/G5/G6/G7/GI).</summary>
    ScalarBc,

    /// <summary>Velocity-dependent BC (BC(v), Pejsa/Litz style) against a standard reference table.</summary>
    BcVelocity,

    /// <summary>The bullet's own measured CD(Mach) table plus physical caliber and mass (no BC involved).</summary>
    CdTable,
}

/// <summary>
/// A reusable bullet/ammunition profile (PLAN.md D8: JSON bullet library).
/// Published-data units are kept at the boundary (grains, lb/in^2, inches, fps);
/// conversion to SI happens when the drag model is created.
/// </summary>
public sealed class BulletProfile
{
    /// <summary>Profile name, e.g. "Hornady 147gr ELD-M 6.5mm".</summary>
    public string Name { get; set; } = "";

    /// <summary>Display caliber label, e.g. "6.5 mm" or ".308".</summary>
    public string? Caliber { get; set; }

    /// <summary>Bullet mass in grains (used for energy reporting and CD-table mode).</summary>
    public double MassGr { get; set; }

    /// <summary>Physical bullet diameter in inches (required in <see cref="DragMode.CdTable"/> mode).</summary>
    public double? DiameterIn { get; set; }

    /// <summary>Drag representation mode.</summary>
    public DragMode DragMode { get; set; } = DragMode.ScalarBc;

    /// <summary>Standard reference table name for scalar/BC(v) modes: "G1", "G5", "G6", "G7" or "GI".</summary>
    public string ReferenceStandard { get; set; } = "G1";

    /// <summary>Scalar ballistic coefficient (lb/in^2) - <see cref="DragMode.ScalarBc"/> only.</summary>
    public double? BcLbIn2 { get; set; }

    /// <summary>BC(v) table as [fps, lb/in^2] pairs - <see cref="DragMode.BcVelocity"/> only.</summary>
    public List<double[]>? BcPoints { get; set; }

    /// <summary>CD(Mach) table as [mach, cd] pairs - <see cref="DragMode.CdTable"/> only.</summary>
    public List<double[]>? CdPoints { get; set; }

    /// <summary>Provenance: where these numbers came from (printed in every case report).</summary>
    public string Provenance { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Load a profile from a JSON file.</summary>
    public static BulletProfile Load(string path)
    {
        var profile = JsonSerializer.Deserialize<BulletProfile>(
            File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Bullet profile '{path}' deserialized to null.");
        profile.Validate();
        return profile;
    }

    /// <summary>Save a profile to a JSON file.</summary>
    public void Save(string path)
    {
        Validate();
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Validate that the profile is self-consistent for its drag mode.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("Bullet profile needs a name.");
        if (MassGr <= 0)
            throw new InvalidOperationException($"Bullet profile '{Name}': mass must be positive.");

        switch (DragMode)
        {
            case DragMode.ScalarBc:
                if (BcLbIn2 is not > 0)
                    throw new InvalidOperationException($"Bullet profile '{Name}': scalar BC mode needs a positive BcLbIn2.");
                CheckStandard();
                break;

            case DragMode.BcVelocity:
                if (BcPoints is null || BcPoints.Count < 2)
                    throw new InvalidOperationException($"Bullet profile '{Name}': BC(v) mode needs at least two [fps, bc] points.");
                CheckStandard();
                break;

            case DragMode.CdTable:
                if (DiameterIn is not > 0)
                    throw new InvalidOperationException($"Bullet profile '{Name}': CD-table mode needs a positive DiameterIn.");
                if (CdPoints is null || CdPoints.Count < 2)
                    throw new InvalidOperationException($"Bullet profile '{Name}': CD-table mode needs at least two [mach, cd] points.");
                break;

            default:
                throw new InvalidOperationException($"Unknown drag mode {DragMode}.");
        }
    }

    private void CheckStandard()
    {
        switch (ReferenceStandard.Trim().ToUpperInvariant())
        {
            case "G1": case "G5": case "G6": case "G7": case "GI": break;
            default:
                throw new InvalidOperationException(
                    $"Bullet profile '{Name}': unknown reference standard '{ReferenceStandard}' (G1/G5/G6/G7/GI).");
        }
    }

    /// <summary>Create the core drag model described by this profile.</summary>
    public IDragModel CreateDragModel()
    {
        Validate();
        return DragMode switch
        {
            DragMode.ScalarBc => new BcDragModel(
                DragTableCatalog.ByStandard(ReferenceStandard),
                new ConstantBcProvider(BcLbIn2!.Value)),

            DragMode.BcVelocity => new BcDragModel(
                DragTableCatalog.ByStandard(ReferenceStandard),
                new TabulatedBcProvider(BcPoints!
                    .Select(p => (p[0] * UnitConversions.FpsToMps, p[1]))
                    .ToArray())),

            DragMode.CdTable => new CdTableDragModel(
                DragTable.FromPoints(
                    CdPoints!.Select(p => (p[0], p[1])).ToArray(),
                    $"{Name} CD(Mach)"),
                DiameterIn!.Value * UnitConversions.InchToM,
                MassGr * UnitConversions.GrToKg),

            _ => throw new InvalidOperationException($"Unknown drag mode {DragMode}."),
        };
    }
}

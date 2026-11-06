using System.Reflection;
using System.Text.Json;

namespace FourTrac.Core.Drag;

/// <summary>Metadata shipped with a bundled drag table.</summary>
public sealed record DragTableProvenance(
    string Id,
    string Description,
    string Transcription,
    string Url,
    string OriginalData,
    string RetrievedUtc);

/// <summary>
/// Access to the bundled standard-projectile drag tables (G1, G7), loaded from
/// embedded JSON resources with provenance metadata recorded in every case report.
/// </summary>
public static class DragTableCatalog
{
    private static readonly Lazy<DragTable> G1Lazy = new(() => Load("g1.json"));
    private static readonly Lazy<DragTable> G5Lazy = new(() => Load("g5.json"));
    private static readonly Lazy<DragTable> G6Lazy = new(() => Load("g6.json"));
    private static readonly Lazy<DragTable> G7Lazy = new(() => Load("g7.json"));
    private static readonly Lazy<DragTable> GILazy = new(() => Load("gi.json"));

    public static DragTable G1 => G1Lazy.Value;
    public static DragTable G5 => G5Lazy.Value;
    public static DragTable G6 => G6Lazy.Value;
    public static DragTable G7 => G7Lazy.Value;
    public static DragTable GI => GILazy.Value;

    /// <summary>Resolve a standard-projectile table by name ("G1", "G5", "G6", "G7", "GI").</summary>
    public static DragTable ByStandard(string standard) => standard.Trim().ToUpperInvariant() switch
    {
        "G1" => G1,
        "G5" => G5,
        "G6" => G6,
        "G7" => G7,
        "GI" => GI,
        _ => throw new ArgumentException(
            $"Unknown drag standard '{standard}'. Supported: G1, G5, G6, G7, GI.", nameof(standard)),
    };

    private static DragTable Load(string fileName)
    {
        var assembly = typeof(DragTableCatalog).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded drag table '{fileName}' not found.");

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var doc = JsonDocument.Parse(stream);

        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString() ?? fileName;
        var points = root.GetProperty("points").EnumerateArray()
            .Select(p =>
            {
                var arr = p.EnumerateArray().ToArray();
                return (arr[0].GetDouble(), arr[1].GetDouble());
            })
            .ToArray();

        // Reference-standard tables: piecewise-linear between nodes, matching the
        // convention of the whole reference ecosystem (JBM, gehtsoft, Exbal) that
        // generated the golden tables. User CD tables (BulletProfile) keep PCHIP.
        return DragTable.FromPoints(points, id, InterpolationKind.Linear);
    }
}

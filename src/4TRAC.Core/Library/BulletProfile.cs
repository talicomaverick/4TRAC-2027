using System.Text.Json;
using FourTrac.Core.Drag;

namespace FourTrac.Core.Library;

/// <summary>
/// A recallable bullet/ammunition profile (D8: bullet library). All fields are
/// in the user-facing imperial units at the boundary; the Core converts to SI
/// internally. Unknown fields are rejected, not silently ignored.
/// </summary>
public sealed record BulletProfile(
    string Name,
    double CaliberIn,
    double MassGr,
    double LengthIn,
    double DiameterIn,
    double TwistIn,
    bool RightHandTwist,
    double BcLbIn2,
    double BcSigmaFraction,
    string ReferenceStandard,
    string? Notes = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Profile name is required.");
        if (!(CaliberIn > 0)) throw new ArgumentOutOfRangeException(nameof(CaliberIn));
        if (!(MassGr > 0)) throw new ArgumentOutOfRangeException(nameof(MassGr));
        if (!(LengthIn > 0)) throw new ArgumentOutOfRangeException(nameof(LengthIn));
        if (!(DiameterIn > 0)) throw new ArgumentOutOfRangeException(nameof(DiameterIn));
        if (!(TwistIn > 0)) throw new ArgumentOutOfRangeException(nameof(TwistIn));
        if (!(BcLbIn2 > 0)) throw new ArgumentOutOfRangeException(nameof(BcLbIn2));
        if (!(BcSigmaFraction >= 0)) throw new ArgumentOutOfRangeException(nameof(BcSigmaFraction));
        DragTableCatalog.ByStandard(ReferenceStandard); // throws if unknown standard
    }

    /// <summary>Serialize to JSON with the 4trac-bullet-profile/1.0 schema marker.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(new
        {
            schema = "4trac-bullet-profile/1.0",
            Name,
            CaliberIn,
            MassGr,
            LengthIn,
            DiameterIn,
            TwistIn,
            RightHandTwist,
            BcLbIn2,
            BcSigmaFraction,
            ReferenceStandard,
            Notes
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Deserialize from JSON, rejecting unknown fields.</summary>
    public static BulletProfile FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? schema = root.TryGetProperty("schema", out var s) ? s.GetString() : null;
        if (schema != "4trac-bullet-profile/1.0")
            throw new InvalidDataException($"Unsupported profile schema: {schema ?? "(missing)"}");

        string name = RequiredString(root, "name");
        double caliberIn = RequiredNumber(root, "caliberIn");
        double massGr = RequiredNumber(root, "massGr");
        double lengthIn = RequiredNumber(root, "lengthIn");
        double diameterIn = RequiredNumber(root, "diameterIn");
        double twistIn = RequiredNumber(root, "twistIn");
        bool rightHandTwist = RequiredBool(root, "rightHandTwist");
        double bcLbIn2 = RequiredNumber(root, "bcLbIn2");
        double bcSigmaFraction = root.TryGetProperty("bcSigmaFraction", out var bsf)
            ? bsf.GetDouble() : 0.03;
        string referenceStandard = root.TryGetProperty("referenceStandard", out var rs)
            ? rs.GetString() ?? "G1" : "G1";
        string? notes = root.TryGetProperty("notes", out var n) ? n.GetString() : null;

        // Reject unknown fields.
        var known = new HashSet<string>
        {
            "schema", "name", "caliberIn", "massGr", "lengthIn", "diameterIn",
            "twistIn", "rightHandTwist", "bcLbIn2", "bcSigmaFraction",
            "referenceStandard", "notes"
        };
        foreach (var prop in root.EnumerateObject())
        {
            if (!known.Contains(prop.Name))
                throw new InvalidDataException($"Unknown profile field '{prop.Name}'.");
        }

        var profile = new BulletProfile(name, caliberIn, massGr, lengthIn, diameterIn,
            twistIn, rightHandTwist, bcLbIn2, bcSigmaFraction, referenceStandard, notes);
        profile.Validate();
        return profile;
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Profile field '{name}' must be a string.");
        return el.GetString()!;
    }

    private static double RequiredNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException($"Profile field '{name}' must be a number.");
        return el.GetDouble();
    }

    private static bool RequiredBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False)
            throw new InvalidDataException($"Profile field '{name}' must be a boolean.");
        return el.ValueKind == JsonValueKind.True;
    }
}

/// <summary>
/// Bullet profile library: loads profiles from a directory of JSON files and
/// provides one-click recall. Profiles are read-only at runtime; the directory
/// is scanned on load.
/// </summary>
public static class BulletProfileLibrary
{
    private static readonly List<BulletProfile> _profiles = new();

    /// <summary>All loaded profiles, in directory order.</summary>
    public static IReadOnlyList<BulletProfile> Profiles => _profiles;

    /// <summary>Load every *.json profile from <paramref name="directory"/>.</summary>
    public static void LoadDirectory(string directory)
    {
        _profiles.Clear();
        if (!Directory.Exists(directory)) return;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                _profiles.Add(BulletProfile.FromJson(json));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Failed to load bullet profile '{file}': {ex.Message}", ex);
            }
        }
    }

    /// <summary>Find a profile by exact name match.</summary>
    public static BulletProfile? FindByName(string name) =>
        _profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

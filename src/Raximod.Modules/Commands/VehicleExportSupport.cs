using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Raximod.Generation.Assets;

namespace Raximod.Modules;

internal static class VehicleExportSupport
{
    public static HashSet<string> RecordNames(string root)
    {
        string[] libraries =
        [
            "uber.ubr", "patch1/patch1.ubr", "patch2/patch2.ubr", "patch3/patch3.ubr",
            "patch4/patch4.ubr", "patch5/patch5.ubr", "expansion1/expansion1.ubr",
        ];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in libraries)
        {
            string path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue;
            names.UnionWith(GlbExportTool.ListRecords(path));
        }
        return names;
    }

    public static GlbInspection InspectGlb(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x46546C67) throw new InvalidOperationException($"Invalid GLB: {path}");
        reader.ReadUInt32();
        reader.ReadUInt32();
        int jsonLength = reader.ReadInt32();
        if (reader.ReadUInt32() != 0x4E4F534A) throw new InvalidOperationException($"GLB has no JSON chunk: {path}");
        using JsonDocument document = JsonDocument.Parse(reader.ReadBytes(jsonLength));
        JsonElement root = document.RootElement;
        string[] nodes = root.TryGetProperty("nodes", out JsonElement nodeArray)
            ? nodeArray.EnumerateArray().Select(node => node.TryGetProperty("name", out JsonElement name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToArray()
            : [];
        string[] animations = root.TryGetProperty("animations", out JsonElement animationArray)
            ? animationArray.EnumerateArray().Select(animation => animation.TryGetProperty("name", out JsonElement name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToArray()
            : [];
        JsonElement[] accessors = root.TryGetProperty("accessors", out JsonElement accessorArray)
            ? accessorArray.EnumerateArray().ToArray() : [];
        int triangles = 0;
        if (root.TryGetProperty("meshes", out JsonElement meshArray))
        {
            foreach (JsonElement mesh in meshArray.EnumerateArray())
            foreach (JsonElement primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                int mode = primitive.TryGetProperty("mode", out JsonElement modeElement) ? modeElement.GetInt32() : 4;
                if (mode != 4) continue;
                int accessorIndex = primitive.TryGetProperty("indices", out JsonElement indices)
                    ? indices.GetInt32()
                    : primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
                triangles += accessors[accessorIndex].GetProperty("count").GetInt32() / 3;
            }
        }
        int bones = root.TryGetProperty("skins", out JsonElement skins)
            ? skins.EnumerateArray().SelectMany(skin => skin.GetProperty("joints").EnumerateArray())
                .Select(joint => joint.GetInt32()).Distinct().Count()
            : 0;
        return new GlbInspection(nodes, animations, triangles, bones);
    }

    public static bool IsHardpoint(string name) =>
        name.StartsWith("hp_", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_mount", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("mount", StringComparison.OrdinalIgnoreCase);

    public static bool IsArticulationNode(string name)
    {
        string value = name.ToLowerInvariant();
        string[] markers =
        [
            "turret", "gun", "cannon", "barrel", "tire", "wheel", "tread", "track", "sus_",
            "steering", "rotor", "prop", "engine", "door", "hatch", "canopy", "gear", "skid", "bay",
        ];
        return markers.Any(value.Contains);
    }
}

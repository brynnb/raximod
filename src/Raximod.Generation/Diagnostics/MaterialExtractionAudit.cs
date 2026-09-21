using System.Text;
using System.Text.Json;

namespace Raximod.Generation.Diagnostics
{
    internal static class MaterialExtractionAudit
    {
        public static void Run(string assetDirectory, string effectDirectory, ExtractionReport report)
        {
            foreach (string sidecar in Directory.EnumerateFiles(assetDirectory, "*.materials.json"))
                AuditSidecar(sidecar, sidecar[..^".materials.json".Length] + ".glb", report);
            string effects = Path.Combine(effectDirectory, "effects.materials.json");
            if (File.Exists(effects)) AuditSidecar(effects, null, report);
        }

        private static void AuditSidecar(string sidecar, string? glb, ExtractionReport report)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(sidecar));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("materials", out JsonElement materials)) return;
            report.Add("nativeMaterials.preserved", materials.GetArrayLength());
            Dictionary<string, HashSet<string>>? semantics = glb != null && File.Exists(glb)
                ? ReadPrimitiveSemantics(glb) : null;
            var missingStageTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement material in materials.EnumerateArray())
            {
                string name = material.GetProperty("name").GetString() ?? "unknown";
                if (material.TryGetProperty("stages", out JsonElement stages))
                foreach (JsonElement stage in stages.EnumerateArray())
                {
                    report.Add("textureStages.preserved");
                    string item = $"{name}:stage{stage.GetProperty("slot").GetInt32()}";
                    string? texture = stage.TryGetProperty("texture", out JsonElement textureValue)
                        ? textureValue.GetString() : null;
                    bool hasUri = stage.TryGetProperty("textureUri", out JsonElement uri)
                                  && uri.ValueKind == JsonValueKind.String;
                    if (!string.IsNullOrWhiteSpace(texture)
                        && !hasUri)
                    {
                        missingStageTextures.Add(texture);
                        if (IsIntentionalNonVisualTexture(texture))
                            report.Info("texture-stage.nonvisual", Path.GetFileName(sidecar), texture,
                                "Native stage is an intentional no-draw/physics-only texture record.");
                        else if (HasSourceAbsenceEvidence(stage, texture))
                            report.Info("texture-stage.source-absent", Path.GetFileName(sidecar), item,
                                $"Static DDS '{texture}' is absent from the recorded installed-source index. " +
                                "The native failed binding is NULL; this does not recover missing artwork or establish dynamic texture supply.");
                        else
                            report.Warn("texture-stage.texture", Path.GetFileName(sidecar), item,
                                $"Native texture stage metadata is preserved, but texture '{texture}' could not be exported.");
                    }
                    if (stage.TryGetProperty("textureRepair", out JsonElement textureRepair)
                        && textureRepair.ValueKind == JsonValueKind.Object)
                    {
                        string resolved = textureRepair.TryGetProperty("resolvedTexture", out JsonElement resolvedValue)
                            ? resolvedValue.GetString() ?? "unknown" : "unknown";
                        report.Info("texture-stage.texture-repair", Path.GetFileName(sidecar), item,
                            $"Verified retail texture reference '{texture}' was resolved as '{resolved}'.");
                    }
                    string? program = stage.TryGetProperty("program", out JsonElement programValue)
                        ? programValue.GetString() : null;
                    if (stage.TryGetProperty("programRepair", out JsonElement repair)
                        && repair.ValueKind == JsonValueKind.Object)
                    {
                        string resolved = repair.TryGetProperty("resolvedProgram", out JsonElement resolvedValue)
                            ? resolvedValue.GetString() ?? "unknown" : "unknown";
                        report.Info("texture-stage.program-repair", Path.GetFileName(sidecar), item,
                            $"Verified retail stage reference '{program}' was repaired with '{resolved}'.");
                    }
                    if (!string.IsNullOrWhiteSpace(program) && !program.Equals("disable", StringComparison.OrdinalIgnoreCase)
                        && stage.TryGetProperty("commands", out JsonElement commands) && commands.GetArrayLength() == 0)
                    {
                        if (program.Equals("cs1_ef_test_beam", StringComparison.OrdinalIgnoreCase))
                            report.Info("texture-stage.test-program", Path.GetFileName(sidecar), item,
                                "The only unresolved stage belongs to the unused ef_test_beam authoring sample.");
                        else
                            report.Warn("texture-stage.program", Path.GetFileName(sidecar), item,
                                $"Stage program '{program}' has no decoded command record.");
                    }
                }
                if (semantics == null || !semantics.TryGetValue(Normalise(name), out HashSet<string>? attributes)) continue;
                if (Boolean(material, "hasUv0", "HasUv0"))
                {
                    report.Add("uvChannels.expected");
                    if (!attributes.Contains("TEXCOORD_0")) report.Warn("uv-channel.discarded", Path.GetFileName(glb!),
                        name, "Source material requires UV0 but its exported primitive lacks TEXCOORD_0.");
                }
                if (Boolean(material, "hasUv1", "HasUv1"))
                {
                    report.Add("uvChannels.expected");
                    if (!attributes.Contains("TEXCOORD_1")) report.Warn("uv-channel.discarded", Path.GetFileName(glb!),
                        name, "Source material requires UV1 but its exported primitive lacks TEXCOORD_1.");
                }
            }
            if (root.TryGetProperty("missingTextures", out JsonElement missing)
                && missing.ValueKind == JsonValueKind.Array)
            foreach (JsonElement value in missing.EnumerateArray())
            {
                string texture = value.ToString();
                if (missingStageTextures.Contains(texture)) continue;
                if (IsIntentionalNonVisualTexture(texture))
                    report.Info("material.nonvisual-texture", Path.GetFileName(sidecar), texture,
                        "Native material references an intentional no-draw/physics-only texture record.");
                else if (IsKnownUnshippedOptionalLightmap(texture))
                    report.Info("material.optional-lightmap-unshipped", Path.GetFileName(sidecar), texture,
                        "The retail material names this optional lightmap, but no matching texture exists in " +
                        "any installed client archive; runtime intentionally uses neutral lighting rather than " +
                        "substituting an unrelated placement-specific bake.");
                else
                    report.Warn("material.texture", Path.GetFileName(sidecar), texture,
                        "Native material references an unresolved source texture.");
            }
        }

        private static bool IsIntentionalNonVisualTexture(string texture) =>
            texture.Equals("nodraw_nocollide", StringComparison.OrdinalIgnoreCase)
            || texture.Equals("force_dome_phy_tex", StringComparison.OrdinalIgnoreCase);

        private static bool HasSourceAbsenceEvidence(JsonElement stage, string texture) =>
            stage.TryGetProperty("sourceAvailability", out var source) && source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty("status", out var status) && status.GetString() == "absent"
            && source.TryGetProperty("lookup", out var lookup) && lookup.GetString() == texture + ".dds"
            && source.TryGetProperty("scope", out var scope) && scope.GetString() == "installed-flat-pack-and-loose-dds"
            && source.TryGetProperty("archiveCount", out var archives) && archives.TryGetInt32(out int count) && count > 0
            && source.TryGetProperty("indexSha256", out var hash) && hash.GetString() is { Length: 64 } value
            && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        private static bool IsKnownUnshippedOptionalLightmap(string texture) =>
            texture.Equals("earth_shrub2_lmap", StringComparison.OrdinalIgnoreCase)
            || texture.Equals("sandbags3_lm", StringComparison.OrdinalIgnoreCase)
            || texture.Equals("cf_gate_stairs_03_lm", StringComparison.OrdinalIgnoreCase);

        private static Dictionary<string, HashSet<string>> ReadPrimitiveSemantics(string glb)
        {
            using FileStream stream = File.OpenRead(glb);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != 0x46546C67) return new(StringComparer.OrdinalIgnoreCase);
            reader.ReadUInt32();
            reader.ReadUInt32();
            int length = reader.ReadInt32();
            if (reader.ReadUInt32() != 0x4E4F534A) return new(StringComparer.OrdinalIgnoreCase);
            using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(reader.ReadBytes(length)).TrimEnd('\0', ' '));
            JsonElement root = document.RootElement;
            string[] names = root.TryGetProperty("materials", out JsonElement materials)
                ? materials.EnumerateArray().Select(material => Normalise(material.GetProperty("name").GetString() ?? "")).ToArray()
                : Array.Empty<string>();
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty("meshes", out JsonElement meshes)) return result;
            foreach (JsonElement mesh in meshes.EnumerateArray())
            foreach (JsonElement primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                if (!primitive.TryGetProperty("material", out JsonElement materialIndex)) continue;
                int index = materialIndex.GetInt32();
                if ((uint)index >= names.Length) continue;
                if (!result.TryGetValue(names[index], out HashSet<string>? attributes))
                    result[names[index]] = attributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty attribute in primitive.GetProperty("attributes").EnumerateObject())
                    attributes.Add(attribute.Name);
            }
            return result;
        }

        private static string Normalise(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"\.\d+$", "");

        private static bool Boolean(JsonElement value, string camel, string legacy) =>
            (value.TryGetProperty(camel, out JsonElement property)
             || value.TryGetProperty(legacy, out property))
            && property.ValueKind == JsonValueKind.True;
    }
}

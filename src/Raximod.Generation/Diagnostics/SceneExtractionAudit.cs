using System.Text.Json;

namespace Raximod.Generation.Diagnostics
{
    internal static class SceneExtractionAudit
    {
        public static void Run(string continentDirectory, ExtractionReport report)
        {
            string planetSideOutput = Directory.GetParent(continentDirectory)?.FullName ?? continentDirectory;
            foreach (string filename in Directory.EnumerateFiles(continentDirectory, "*.json"))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(filename));
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("format", out JsonElement format)
                    || format.GetString() != "raxicore-continent-scene") continue;
                string source = Path.GetFileName(filename);
                AuditPortalChildren(root, source, planetSideOutput, report);
                AuditComposites(root, source, planetSideOutput, report);
                AuditTerrainCutouts(root, source, planetSideOutput, report);
            }
            string groundcover = Path.Combine(continentDirectory, "groundcover.json");
            if (File.Exists(groundcover)) AuditGroundcover(groundcover, report);
            AuditAssetAvailability(Path.Combine(planetSideOutput, "assets", "manifest.json"), report);
        }

        private static void AuditTerrainCutouts(JsonElement root, string source, string output, ExtractionReport report)
        {
            if (!root.TryGetProperty("nativeTerrainChunks", out var chunks)
                || chunks.ValueKind != JsonValueKind.Array || chunks.GetArrayLength() == 0) return;
            foreach (var chunk in chunks.EnumerateArray())
            {
                string uri = chunk.GetProperty("uri").GetString()!;
                if (!File.Exists(Path.Combine(output, uri)))
                    report.Warn("terrain-chunk.missing", source, uri, "Native terrain GLB is missing.");
                else report.Add("terrainChunks.present");
            }
            string? cutouts = root.TryGetProperty("terrainFoundationCutoutsUri", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(cutouts) || !File.Exists(Path.Combine(output, cutouts)))
            {
                report.Warn("terrain-cutouts.missing", source, cutouts ?? "provenance",
                    "Native terrain lacks its foundation-cutout provenance report.");
                return;
            }
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, cutouts)));
            var provenance = document.RootElement;
            if (!provenance.TryGetProperty("Method", out var method)
                || method.GetString() != "exact-placed-hq-foundation-triangle-subtraction-v2")
                report.Warn("terrain-cutouts.outdated", source, cutouts, "Regenerate terrain with complete foundation coverage.");
            if (provenance.TryGetProperty("RecordsWithoutGeometry", out var missing))
                foreach (var record in missing.EnumerateArray())
                    report.Warn("terrain-cutouts.source", source, record.GetString()!, "Foundation source geometry was not resolved.");
            report.Add("terrainCutouts.present");
        }

        private static void AuditPortalChildren(JsonElement root, string source, string output, ExtractionReport report)
        {
            if (!root.TryGetProperty("portalChildren", out JsonElement children)
                || children.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement child in children.EnumerateArray())
            {
                report.Add("portalChildren.preserved");
                string item = child.TryGetProperty("instance", out JsonElement instance)
                    ? instance.GetString() ?? "unknown" : "unknown";
                bool complete = ArrayLength(child, "sourceMatrix") == 16
                                && ArrayLength(child, "position") == 3
                                && ArrayLength(child, "rotation") == 4
                                && ArrayLength(child, "scale") == 3
                                && child.TryGetProperty("flags", out _);
                if (!complete) report.Warn("portal-child.discarded-field", source, item,
                    "Portal child is missing its complete source matrix, decomposed transform, or flags.");
                AuditAsset(child, source, item, output, "portal-child.asset", report);
            }
        }

        private static void AuditComposites(JsonElement root, string source, string output, ExtractionReport report)
        {
            if (!root.TryGetProperty("composites", out JsonElement composites)
                || composites.ValueKind != JsonValueKind.Object) return;
            if (composites.TryGetProperty("definitions", out JsonElement definitions))
            foreach (JsonProperty definition in definitions.EnumerateObject())
            foreach (JsonElement child in definition.Value.EnumerateArray())
            {
                report.Add("compositeMembers.preserved");
                string item = child.GetProperty("record").GetString() ?? "unknown";
                if (ArrayLength(child, "localMatrix") != 16) report.Warn("composite-member.discarded-transform",
                    source, item, $"Composite definition {definition.Name} lacks its composed local matrix.");
                string path = Path.Combine(output, "assets", item + ".glb");
                if (!File.Exists(path)) report.Warn("composite-member.asset", source, item,
                    $"Composite definition {definition.Name} references an unresolved GLB.");
            }
            if (!composites.TryGetProperty("diagnostics", out JsonElement diagnostics)) return;
            WarnArray(diagnostics, "cycles", "composite-member.cycle", source,
                "Recursive composite cycle prevented member expansion.", report);
            WarnArray(diagnostics, "depthExceeded", "composite-member.depth", source,
                "Composite expansion exceeded its safety depth.", report);
            WarnArray(diagnostics, "ambiguousDefinitions", "composite-member.ambiguous", source,
                "Multiple non-equivalent definitions share this composite name.", report);
            // nonCompositeDefinitions are PSForever entity lists, not discarded visual compositions.
        }

        private static void AuditGroundcover(string filename, ExtractionReport report)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(filename));
            JsonElement root = document.RootElement;
            report.Add("groundcoverRecipes.preserved", root.GetProperty("recipes").GetArrayLength());
            report.Add("groundcoverDistributions.preserved", root.GetProperty("surfaces").GetArrayLength());
            JsonElement diagnostics = root.GetProperty("diagnostics");
            WarnArray(diagnostics, "activeMissingTextures", "groundcover.texture.active", Path.GetFileName(filename),
                "Groundcover recipe texture is unresolved in the reference archives.", report);
            InfoArray(diagnostics, "unusedMissingTextures", "groundcover.texture.unused", Path.GetFileName(filename),
                "Unused malformed groundcover recipe references an unresolved texture.", report);
            // Version-1 catalogs used one undifferentiated field.
            WarnArray(diagnostics, "missingTextures", "groundcover.texture", Path.GetFileName(filename),
                "Groundcover recipe texture is unresolved in the reference archives.", report);
            WarnArray(diagnostics, "missingRecipes", "groundcover.recipe-link", Path.GetFileName(filename),
                "distribution.lst references a missing groundcover recipe.", report);
            if (diagnostics.TryGetProperty("aliasCorrections", out JsonElement aliases)
                && aliases.ValueKind == JsonValueKind.Array)
                report.Add("groundcoverAliases.corrected", aliases.GetArrayLength());
        }

        private static void AuditAssetAvailability(string filename, ExtractionReport report)
        {
            if (!File.Exists(filename)) return;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(filename));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("nonVisual", out JsonElement records)
                || records.ValueKind != JsonValueKind.Array) return;
            report.Add("assetRecords.intentionalNonVisual", records.GetArrayLength());
            foreach (JsonElement record in records.EnumerateArray())
                report.Info("asset.nonvisual", Path.GetFileName(filename), record.ToString(),
                    "Native record is intentionally logical/empty and does not require a rendered GLB.");
        }

        private static void AuditAsset(JsonElement value, string source, string item, string output,
            string category, ExtractionReport report)
        {
            if (!value.TryGetProperty("uri", out JsonElement uri) || uri.ValueKind != JsonValueKind.String) return;
            string path = Path.Combine(output, uri.GetString()!.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) report.Warn(category, source, item, "Placement references an unresolved GLB.");
        }

        private static int ArrayLength(JsonElement value, string property) =>
            value.TryGetProperty(property, out JsonElement array) && array.ValueKind == JsonValueKind.Array
                ? array.GetArrayLength() : -1;

        internal static void WarnArray(JsonElement parent, string property, string category, string source,
            string message, ExtractionReport report)
        {
            if (!parent.TryGetProperty(property, out JsonElement values) || values.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement value in values.EnumerateArray())
                report.Warn(category, source, value.ToString(), message);
        }

        internal static void InfoArray(JsonElement parent, string property, string category, string source,
            string message, ExtractionReport report)
        {
            if (!parent.TryGetProperty(property, out JsonElement values) || values.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement value in values.EnumerateArray())
                report.Info(category, source, value.ToString(), message);
        }
    }
}

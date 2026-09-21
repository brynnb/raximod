using System.Text.Json;

namespace Raximod.Generation.Diagnostics
{
    internal static class EffectExtractionAudit
    {
        public static void Run(string effectDirectory, ExtractionReport report)
        {
            string filename = Path.Combine(effectDirectory, "effects.json");
            if (!File.Exists(filename)) return;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(filename));
            JsonElement root = document.RootElement;
            JsonElement effects = root.GetProperty("effects");
            report.Add("effectGraphs.preserved", effects.GetArrayLength());
            foreach (JsonElement effect in effects.EnumerateArray())
            {
                report.Add("effectLayers.preserved", effect.GetProperty("layers").GetArrayLength());
                report.Add("effectLinks.preserved", effect.GetProperty("links").GetArrayLength());
                if (effect.TryGetProperty("actions", out JsonElement actions))
                    report.Add("effectActions.categorized", actions.GetArrayLength());
            }
            if (root.TryGetProperty("decals", out JsonElement decals) && decals.ValueKind == JsonValueKind.Array)
            {
                report.Add("effectDecals.preserved", decals.GetArrayLength());
                foreach (JsonElement decal in decals.EnumerateArray())
                    report.Add("effectDecalVariants.preserved", decal.GetProperty("variants").GetArrayLength());
            }
            JsonElement diagnostics = root.GetProperty("diagnostics");
            if (diagnostics.TryGetProperty("commandCoverage", out JsonElement coverage)
                && coverage.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in coverage.EnumerateArray())
                {
                    string command = entry.GetProperty("command").GetString() ?? "unknown";
                    string category = entry.GetProperty("category").GetString() ?? "unknown";
                    string support = entry.GetProperty("runtimeSupport").GetString() ?? "preserved";
                    int occurrences = entry.GetProperty("occurrences").GetInt32();
                    report.Add($"effectCommands.{support}", occurrences);
                    report.Add($"effectCommandCategories.{category}", occurrences);
                    if (support is "preserved" or "partial" or "event-only" or "callback")
                    {
                        report.Info("effect-command.runtime-coverage", "effects.json", command,
                            $"{occurrences} occurrence(s) are preserved as {category}; runtime support is {support}.");
                    }
                }
            }
            SceneExtractionAudit.WarnArray(diagnostics, "unclassifiedCommands", "effect-command.unclassified",
                "effects.json", "Effect command is losslessly preserved but has no semantic category.", report);
            if (diagnostics.TryGetProperty("repairedLinks", out JsonElement repairedLinks)
                && repairedLinks.ValueKind == JsonValueKind.Array)
                report.Add("effectLinks.repaired", repairedLinks.GetArrayLength());
            if (diagnostics.TryGetProperty("recoveredGraphs", out JsonElement recoveredGraphs)
                && recoveredGraphs.ValueKind == JsonValueKind.Array)
                report.Add("effectGraphs.recovered", recoveredGraphs.GetArrayLength());
            if (diagnostics.TryGetProperty("repairedMeshes", out JsonElement repairedMeshes)
                && repairedMeshes.ValueKind == JsonValueKind.Array)
            {
                report.Add("effectMeshes.repaired", repairedMeshes.GetArrayLength());
                foreach (JsonElement repair in repairedMeshes.EnumerateArray())
                {
                    string source = repair.GetProperty("authoredMesh").GetString() ?? "unknown";
                    string target = repair.GetProperty("resolvedMesh").GetString() ?? "unknown";
                    string kind = repair.GetProperty("kind").GetString() ?? "repair";
                    report.Info("effect-mesh.repair", "effects.json", source,
                        $"Verified {kind} resolves the authored mesh as '{target}'.");
                }
            }
            SceneExtractionAudit.WarnArray(diagnostics, "missingLinks", "effect-link.unresolved", "effects.json",
                "effects.adb references a target graph that does not exist in the source database.", report);
            SceneExtractionAudit.InfoArray(diagnostics, "externalLinks", "effect-link.external-handle", "effects.json",
                "Reference is an authored attachment/script handle rather than a child effect graph.", report);
            SceneExtractionAudit.WarnArray(diagnostics, "missingMeshes", "effect-mesh.unresolved", "effects.json",
                "Effect mesh could not be exported from a shared model library.", report);
            SceneExtractionAudit.WarnArray(diagnostics, "cycles", "effect-link.cycle", "effects.json",
                "Effect graph contains a recursive cycle; runtime recursion guards will stop it.", report);
        }
    }
}

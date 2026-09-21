using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Raximod.Generation;
using Raximod.Generation.Assets;

namespace Raximod.Modules;

public static class ExportManifestAssetsCommand
{
    public static int Run(string[] args)
    {
        if (args.Length is < 3 or > 5 || args.Skip(3).Any(arg => arg is not ("--overwrite" or "--liquids-only")))
        {
            Console.Error.WriteLine("usage: <PlanetSideDir> <continent-manifest-directory> <asset-output-directory> [--overwrite] [--liquids-only]");
            return 1;
        }

        string planetside = Path.GetFullPath(args[0]);
        string manifests = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[2]);
        bool overwrite = args.Contains("--overwrite");
        bool liquidsOnly = args.Contains("--liquids-only");
        if (!Directory.Exists(manifests))
        {
            Console.Error.WriteLine($"manifest directory not found: {manifests}");
            return 1;
        }

        var records = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var liquidRecords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groundcoverRecords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string filename in Directory.EnumerateFiles(manifests, "*.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(filename));
            AddGroundcoverRecords(document.RootElement, records, groundcoverRecords);
            AddRecords(document.RootElement, "objects", records);
            AddRecords(document.RootElement, "warpgateBarriers", records);
            AddRecords(document.RootElement, "liquids", records);
            AddRecords(document.RootElement, "liquids", liquidRecords);
            AddRecords(document.RootElement, "portalChildren", records);
            AddCompositeRecords(document.RootElement, records);
        }

        static void AddGroundcoverRecords(
            JsonElement root, HashSet<string> records, HashSet<string> groundcoverRecords)
        {
            if (!root.TryGetProperty("format", out JsonElement format)
                || format.GetString() != "raxicore-groundcover-catalog"
                || !root.TryGetProperty("recipes", out JsonElement recipes)
                || recipes.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement recipe in recipes.EnumerateArray())
            {
                if (!recipe.TryGetProperty("meshes", out JsonElement meshes)
                    || meshes.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement mesh in meshes.EnumerateArray())
                {
                    if (mesh.TryGetProperty("name", out JsonElement name)
                        && name.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(name.GetString()))
                    {
                        records.Add(name.GetString()!);
                        groundcoverRecords.Add(name.GetString()!);
                    }
                }
            }
        }

        static void AddCompositeRecords(JsonElement root, HashSet<string> records)
        {
            if (!root.TryGetProperty("composites", out JsonElement composites) ||
                composites.ValueKind != JsonValueKind.Object ||
                !composites.TryGetProperty("definitions", out JsonElement definitions) ||
                definitions.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            foreach (JsonProperty definition in definitions.EnumerateObject())
            {
                if (definition.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement entry in definition.Value.EnumerateArray())
                {
                    if (entry.TryGetProperty("record", out JsonElement record) &&
                        record.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(record.GetString()))
                    {
                        records.Add(record.GetString()!);
                    }
                }
            }
        }

        // A query-only family refresh must not reinterpret previously published vehicle
        // or forcefield movement contracts. Require a completed world export first.
        JsonElement? retained = liquidsOnly
            ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(output, "manifest.json"))) : null;
        if (retained is { } previous && (previous.GetProperty("format").GetString() != "raxicore-asset-availability"
            || previous.GetProperty("failed").GetArrayLength() != 0))
            throw new InvalidDataException("A liquid refresh requires a valid existing world asset publication");
        var progress = new SynchronousProgress<string>(Console.WriteLine);
        GlbBatchExportTool.Result result = GlbBatchExportTool.Run(
            new GlbBatchExportTool.Options(
                planetside,
                output,
                liquidsOnly ? liquidRecords : records,
                overwrite,
                AutoDetectNativeAnimations: true,
                SearchAllInstalledLibraries: true),
            progress);
        var logicalNonVisual = LoadLogicalNonVisualRecords(planetside);
        var nonVisual = result.Failed
            .Where(failure => groundcoverRecords.Contains(failure.Record)
                              && (failure.NativeMeshCount == 0 || failure.NativeVertexCount == 0))
            .Select(failure => failure.Record)
            .Concat(result.Missing.Where(logicalNonVisual.Records.Contains))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (retained is { } old)
            nonVisual.UnionWith(old.GetProperty("nonVisual").EnumerateArray().Select(record => record.GetString()!));
        string[] missingRecords = records.Where(record => !nonVisual.Contains(record)
            && !File.Exists(Path.Combine(output, record + ".glb"))).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var unavailable = result.Missing.Where(record => !nonVisual.Contains(record))
            .Concat(result.Failed.Where(failure => !nonVisual.Contains(failure.Record))
                .Select(failure => failure.Record))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        unavailable.UnionWith(missingRecords);

        // Native spatial auditing is deliberately mandatory between render export and collision export.
        // It backfills existing GLBs too, so --overwrite is not required to gain newly understood native
        // metadata. A successful geometry decode is not considered complete unless every record-owned byte
        // range was consumed and AAB data was retained for debug-only inspection.
        string[] spatialRecords = records
            .Where(record => File.Exists(Path.Combine(output, record + ".glb")))
            .ToArray();
        NativeSpatialAuditTool.Result spatialResult = NativeSpatialAuditTool.Run(
            new NativeSpatialAuditTool.Options(planetside, output, spatialRecords), progress);
        if (spatialResult.Failures.Count != 0)
        {
            foreach (NativeSpatialAuditTool.Failure failure in spatialResult.Failures)
                Console.Error.WriteLine($"native spatial audit failed {failure.Record}: {failure.Reason}");
            return 4;
        }

        // Collision is deliberately part of this command, after every direct, portal-child, composite and
        // groundcover GLB export has been attempted. Known unavailable visual records remain explicit in
        // manifest.json and make the command fail, but must not prevent collision from covering every GLB
        // that was successfully materialized (including late-discovered facility children).
        CollisionManifestTool.Result collisionResult = liquidsOnly ? RetainedCollision(output) : CollisionManifestTool.Run(
            new CollisionManifestTool.Options(planetside, output, AabFallbackRecords: spatialRecords), progress);
        if (!collisionResult.Complete)
        {
            Console.Error.WriteLine(
                $"collision export incomplete: {collisionResult.Failures.Count} native colliders were discarded");
            return 3;
        }
        // Final browser packaging follows discovery/collision and precedes publication.
        // Standalone exports retain their embedded source images; world GLBs use the
        // byte-identical shared companions already emitted by the native exporter.
        SharedGlbImageExport.Run(output);
        var assetManifest = new
        {
            format = "raxicore-asset-availability",
            version = 2,
            gameObjects = logicalNonVisual.Diagnostics,
            gameObjectProvenance = nonVisual
                .Where(logicalNonVisual.Provenance.ContainsKey)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(record => record, record => logicalNonVisual.Provenance[record],
                    StringComparer.OrdinalIgnoreCase),
            pipeline = new
            {
                format = "raxicore-manifest-asset-pipeline",
                version = 1,
                phases = new[] { "manifest-discovery", "render-assets", "native-spatial-audit", "native-collision", "shared-images", "publish" },
                sourceCoverageRecords = spatialResult.Complete,
                nativeAabRecords = spatialResult.AabRecords,
                collisionRecords = collisionResult.Written,
                collisionShapes = collisionResult.Shapes,
                installedUbrs = result.InstalledLibraryCount,
                searchedUbrs = result.SearchedLibraryCount,
            },
            available = records.Where(record => !unavailable.Contains(record)
                                                  && File.Exists(Path.Combine(output, record + ".glb")))
                .Order(StringComparer.OrdinalIgnoreCase),
            nonVisual = nonVisual.Order(StringComparer.OrdinalIgnoreCase),
            missing = missingRecords,
            failed = result.Failed.Where(failure => !nonVisual.Contains(failure.Record))
                .OrderBy(failure => failure.Record, StringComparer.OrdinalIgnoreCase),
        };
        File.WriteAllText(
            Path.Combine(output, "manifest.json"),
            JsonSerializer.Serialize(assetManifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            $"requested={result.Requested} existing={result.Existing} exported={result.Exported} " +
            $"missing={missingRecords.Length} " +
            $"failed={result.Failed.Count - nonVisual.Count(record => result.Failed.Any(failure => failure.Record.Equals(record, StringComparison.OrdinalIgnoreCase)))} " +
            $"nonVisual={nonVisual.Count} " +
            $"installedUbrs={result.InstalledLibraryCount} searchedUbrs={result.SearchedLibraryCount} " +
            $"collisionRecords={collisionResult.Written} collisionShapes={collisionResult.Shapes}");
        return unavailable.Count == 0 ? 0 : 2;

        static CollisionManifestTool.Result RetainedCollision(string directory)
        {
            using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "collision-manifest.json")));
            if (index.RootElement.GetProperty("format").GetString() != "raxicore-collision-index")
                throw new InvalidDataException("Missing valid world collision publication for liquid refresh");
            int count = 0, shapes = 0;
            foreach (var record in index.RootElement.GetProperty("records").EnumerateArray())
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, record.GetString() + ".collision.json")));
                shapes += document.RootElement.GetProperty("shapes").GetArrayLength();
                count++;
            }
            return new(count, count, shapes, 0, []);
        }

        static void AddRecords(JsonElement root, string property, HashSet<string> records)
        {
            if (!root.TryGetProperty(property, out JsonElement entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("record", out JsonElement record) &&
                    record.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(record.GetString()))
                {
                    records.Add(record.GetString()!);
                }
            }
        }

        static (
            HashSet<string> Records,
            GameObjectDb.DecoderDiagnostics? Diagnostics,
            Dictionary<string, GameObjectDb.GameObjectProvenance> Provenance)
            LoadLogicalNonVisualRecords(string planetside)
        {
            string path = Path.Combine(planetside, "startup.pak-out", "game_objects.adb");
            if (!File.Exists(path)) return (
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
                new Dictionary<string, GameObjectDb.GameObjectProvenance>(StringComparer.OrdinalIgnoreCase));
            GameObjectDb database = GameObjectDb.Parse(File.ReadAllBytes(path));
            GameObjectDb.GameObject[] objects = database.ResolvedObjects
                .Where(gameObject => GameObjectPropertyReader.List(gameObject, "meshsequence") is not { Count: > 0 } values
                                     || values.All(value => value.Equals("none", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            return (
                objects.Select(gameObject => gameObject.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                database.Diagnostics,
                objects.ToDictionary(
                    gameObject => gameObject.Name,
                    gameObject => gameObject.Provenance,
                    StringComparer.OrdinalIgnoreCase));
        }
    }
}

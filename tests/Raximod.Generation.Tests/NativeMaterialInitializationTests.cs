using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeMaterialInitializationTests
{
    [Fact]
    public void DefaultsDoNotReplaceAuthoredStagesOrClaimOtherConstructorBranches()
    {
        Assert.Equal("arbitrary_texture", NativeMaterialInitialization.UnconfiguredStages(
            "arbitrary_texture", [])[0].Arguments[0]);
        foreach (string command in new[] { "mat_stage1", "mat_texture1", "mat_anim1", "mat_stage2" })
            Assert.Empty(NativeMaterialInitialization.UnconfiguredStages("plain", [new(command, ["value"])]));
        foreach (string name in new[] { "wall+lightmap", "ui_icon", "map120000", "wall_gf3" })
            Assert.Empty(NativeMaterialInitialization.UnconfiguredStages(name, []));
    }

    [InstalledClientFact("startup.pak-out/materials.adb")]
    public void ExportResolvesOriginalImplicitTexturesWithoutInventingAnAdbRecord()
    {
        string root = Environment.GetEnvironmentVariable("PLANETSIDE_DIR") ?? "/home/brynn/Downloads/PlanetSide";
        var textures = new TextureProvider(root);
        string output = Path.Combine("/var/tmp", "native-material-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            string[] names = ["ef_whitecircle", "smoketrail1", "ef_decon_a", "ef_elec_arc", "ef_sparks"];
            string path = Path.Combine(output, "defaults.glb");
            NativeMaterialManifestTool.Write(path, "defaults", names.Select(n =>
                new NativeMaterialManifestTool.Usage(n, 1, true, false)).ToArray(), textures);
            using var document = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".materials.json")));
            var materials = document.RootElement.GetProperty("materials").EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!);
            foreach (string name in names.Take(3))
            {
                var material = materials[name];
                var initial = material.GetProperty("stageInitialization");
                Assert.Equal(name != "ef_decon_a", initial.GetProperty("materialRecordPresent").GetBoolean());
                Assert.Equal(name == "ef_decon_a" ? 0 : 2, material.GetProperty("sectionCommands").GetArrayLength());
                var stages = material.GetProperty("stages");
                Assert.Equal(4, stages.GetArrayLength());
                Assert.Equal(name, stages[0].GetProperty("texture").GetString());
                Assert.Equal("default0", stages[0].GetProperty("program").GetString());
                Assert.True(File.Exists(Path.Combine(output, stages[0].GetProperty("textureUri").GetString()!)));
                Assert.Contains(stages[0].GetProperty("commands").EnumerateArray(), c =>
                    c.GetProperty("name").GetString() == "sc_colorop" && c.GetProperty("arguments")[0].GetString() == "modulate");
                Assert.Equal("disable2", stages[1].GetProperty("program").GetString());
            }
            // The original texture lookup can also fail. Keep its exact missing
            // name observable; a default stage is not proof of a shipped texture.
            Assert.Equal(JsonValueKind.Null, materials["ef_elec_arc"].GetProperty("stages")[0].GetProperty("textureUri").ValueKind);
            Assert.Contains("ef_elec_arc", document.RootElement.GetProperty("missingTextures").EnumerateArray().Select(v => v.GetString()));
            var absent = materials["ef_elec_arc"].GetProperty("stages")[0].GetProperty("sourceAvailability");
            Assert.Equal("absent", absent.GetProperty("status").GetString());
            Assert.Equal("ef_elec_arc.dds", absent.GetProperty("lookup").GetString());
            Assert.True(absent.GetProperty("archiveCount").GetInt32() > 0);
            Assert.Equal(64, absent.GetProperty("indexSha256").GetString()!.Length);
            Assert.Equal(JsonValueKind.Null, materials["ef_sparks"].GetProperty("stageInitialization").ValueKind);
        }
        finally { Directory.Delete(output, true); }
    }

    [Fact]
    public void MaterialAuditDistinguishesAbsentStaticSourceFromMissingExportsIncludingNamedNull()
    {
        string root = Path.Combine("/var/tmp", "texture-absence-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var proof = new { status = "absent", lookup = "missing.dds", scope = "installed-flat-pack-and-loose-dds",
                archiveCount = 1, indexSha256 = new string('a', 64) };
            var materials = new[] {
                new { name = "proven", stages = new[] { new { slot = 1, texture = "missing", textureUri = (string?)null, sourceAvailability = proof } } },
                new { name = "unexported", stages = new[] { new { slot = 1, texture = "missing", textureUri = (string?)null, sourceAvailability = proof with { indexSha256 = "invalid" } } } },
                new { name = "named-null", stages = new[] { new { slot = 1, texture = "null", textureUri = (string?)null, sourceAvailability = proof } } },
            };
            File.WriteAllText(Path.Combine(root, "effects.materials.json"), JsonSerializer.Serialize(new { materials }));
            var report = new Raximod.Generation.Diagnostics.ExtractionReport();
            // One directory pass; the effects path does not exist in this fixture.
            Raximod.Generation.Diagnostics.MaterialExtractionAudit.Run(root, Path.Combine(root, "unused"), report);
            Assert.Single(report.Issues, i => i.Category == "texture-stage.source-absent");
            Assert.Equal(new[] { "named-null:stage1", "unexported:stage1" },
                report.Issues.Where(i => i.Severity == "warning").Select(i => i.Item).Order().ToArray());
        }
        finally { Directory.Delete(root, true); }
    }
}

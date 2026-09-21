using System.Numerics;
using System.Text.Json;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Continents;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class GroundcoverPortalChildrenTests
{
    [InstalledClientFact("maps/map_resources.pak", "uber.ubr", "map15.ubr")]
    public void VehicleTrainingGroundcoverRetainsNativeChildrenAndVisibilityIdentity()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR") ?? "/home/brynn/Downloads/PlanetSide";
        var log = new Progress<string>();
        var mpo = ContinentExportTool.LoadMpo(Path.Combine(client, "maps/map_resources.pak"), "map15", log)!;
        var ground = ContinentExportTool.LoadGroundcover(client, "map15", log);
        var source = UberModel.Load(File.ReadAllBytes(Path.Combine(client, "uber.ubr")));
        var parents = PortalChildPlacements.Parents(mpo, ground);
        Assert.Equal(parents.Length, parents.Select(p => p.SourceIndex).Distinct().Count());
        var pavilions = parents.Where(p => p.Parent.Name == "vt_spawn" && p.SourceIndex >= mpo.Objects.Count).ToArray();
        Assert.NotEmpty(pavilions);
        foreach (var parent in pavilions)
        {
            var children = PortalChildPlacements.Build(parent.Parent, parent.SourceIndex, source);
            Assert.Contains(children, child => child.Record == "vt_holo");
            Assert.Contains(children, child => child.Record == "spawn_zone");
            Assert.All(children, child => Assert.Equal(parent.SourceIndex, child.ParentIndex));
            var holo = children.Single(child => child.Record == "vt_holo");
            var native = source.FetchMeshSystem("vt_spawn")!.PortalMeshItems.Single(c => c.AssetName == "vt_holo");
            var world = native.Transform * Matrix4x4.CreateScale(parent.Parent.Scale)
                * Matrix4x4.CreateRotationZ(parent.Parent.Yaw) * Matrix4x4.CreateTranslation(parent.Parent.Position);
            Assert.InRange(Vector3.Distance(new(holo.Position[0], holo.Position[1], holo.Position[2]),
                new(world.M41, world.M43, -world.M42)), 0, .002f);
        }
        string output = Path.Combine("/var/tmp", "groundcover-portals-" + Guid.NewGuid());
        Directory.CreateDirectory(output);
        try
        {
            File.WriteAllText(Path.Combine(output, "map15.json"), "{\"terrainSentinel\":123,\"portalChildren\":[]}");
            PortalChildPlacements.Refresh(client, output, [Path.Combine(client, "map15.ubr")], "map15", source, log);
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "map15.json")));
            Assert.Equal(123, result.RootElement.GetProperty("terrainSentinel").GetInt32());
            Assert.Equal(parents.Sum(p => PortalChildPlacements.Build(p.Parent, p.SourceIndex, source).Count),
                result.RootElement.GetProperty("portalChildren").GetArrayLength());
        }
        finally { Directory.Delete(output, true); }
    }
}

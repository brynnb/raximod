using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;

namespace Raximod.Generation.Continents;

public static class HartExportTool
{
    public static void Run(string planetside, string output)
    {
        var database = GameObjectDb.Parse(File.ReadAllBytes(System.IO.Path.Combine(planetside, "startup.pak-out/game_objects.adb")));
        var objects = database.ResolvedObjects.ToDictionary(o => o.Name, StringComparer.Ordinal);
        var physics = new PhysicsListDatabase();
        physics.ParseFile(System.IO.Path.Combine(planetside, "startup.pak-out/physics_barrier.lst"));
        var shuttle = HartBindings.ResolveShuttle(objects["orbital_shuttle"]);
        var mechanism = objects["obbasemesh"];
        var buildings = new[] { "nc", "tr", "vs" }.Select(faction =>
            HartBindings.ResolveBuilding(objects["orbital_building_" + faction], physics)).ToArray();
        if (buildings.Any(b => b.Shuttle != shuttle.Record || b.Entrances.Length != shuttle.Entrances.Length))
            throw new InvalidDataException("HART buildings do not bind the shuttle entrances");
        var locations = DropPodLocationExport.Export(planetside, output);
        DropPodLocationExport.WriteJson(System.IO.Path.Combine(output, "manifest.json"), new {
            format = "raxicore-hart", version = 1, coordinateSystem = "right-handed-z-up",
            source = "startup.pak/game_objects.adb and physics_barrier.lst; maps/map_resources.pak",
            decoder = database.Diagnostics, shuttle, buildings,
            mechanism = new { record = mechanism.Name, sounds = HartBindings.ResolveSounds(mechanism),
                source = mechanism.PropertySources.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value) },
            landing = new {
                format = "native-droppod-le", version = 1,
                retailEvidence = new {
                    executableSha256 = "7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a",
                    loader = "0x8c1600", lookup = "0x8c1040", roundingInitialization = "0x4026bf",
                },
                header = "int32 extent, int32 cellSize; then row-major float32 XY pairs",
                lookup = "clamp native XY to [0, extent-1], truncate each coordinate, integer-divide by cellSize; index y*side+x",
                semantics = "static landing remap; not launch authorization or current facility/continent eligibility",
                maps = locations,
            },
        });
    }
}

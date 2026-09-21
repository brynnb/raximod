using Raximod.EngineAssets.Databases;
using Raximod.Generation.Awards;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class AwardCatalogCompilerTests
{
    [Fact]
    public void InstalledCatalogCompilesToAuthoritativeCommendationIdsAndReferences()
    {
        string? planetSide = FindPlanetSideDirectory();
        if (planetSide is null) return;

        string source = Path.Combine(planetSide, "startup.pak-out");
        NativeAward[] raw = NativeAwardCatalog.Parse(File.ReadAllBytes(Path.Combine(source, "awards.adb"))).ToArray();
        NativeStringTable english = NativeStringTable.Parse(File.ReadAllBytes(Path.Combine(source, "english.str")));
        GameObjectDb gameObjects = GameObjectDb.Parse(File.ReadAllBytes(Path.Combine(source, "game_objects.adb")));

        CompiledAwardCatalog catalog = AwardCatalogCompiler.Compile(raw, english, gameObjects);

        Assert.Equal(429, catalog.Definitions.Count);
        Assert.Equal(1024, catalog.Palette.Count);
        Assert.True(catalog.Audit.CommendationIdsContiguous);
        Assert.True(catalog.Audit.PaletteIndicesContiguous);
        Assert.Equal((0, "2005_fan_faire_commander"), (catalog.Definitions[0].Id, catalog.Definitions[0].Name));
        Assert.Equal((50, "avenger7"), (catalog.Definitions[50].Id, catalog.Definitions[50].Name));
        Assert.Equal((51, "bending_movie_actor"), (catalog.Definitions[51].Id, catalog.Definitions[51].Name));
        Assert.Equal((107, "combat_medic"), (catalog.Definitions[107].Id, catalog.Definitions[107].Name));
        Assert.Equal((194, "explorer1"), (catalog.Definitions[194].Id, catalog.Definitions[194].Name));
        Assert.Equal((428, "xmas_spirit"), (catalog.Definitions[428].Id, catalog.Definitions[428].Name));

        CompiledAwardDefinition combatMedic = catalog.Definitions.Single(definition => definition.Name == "combat_medic");
        Assert.Equal("Combat Medic, (Qualification)", combatMedic.Localization.DisplayName?.English);
        CompiledAwardRequirement medicRequirement = Assert.Single(combatMedic.Requirements);
        Assert.Equal("healassist", medicRequirement.Type);
        Assert.Equal(35, medicRequirement.QualificationCount);

        CompiledAwardRequirement explorer = Assert.Single(
            catalog.Definitions.Single(definition => definition.Name == "explorer1").Requirements);
        Assert.Equal("firsttimeevent", explorer.Type);
        Assert.Equal("monolith", explorer.ObjectGroup);
        Assert.Equal(9, explorer.QualificationCount);
        Assert.NotEmpty(catalog.ObjectGroups.Single(group => group.Name == "monolith").Members);

        Assert.Equal("female", catalog.Definitions.Single(definition => definition.Name == "valentine_female").Sex);
        Assert.Equal("male", catalog.Definitions.Single(definition => definition.Name == "valentine_male").Sex);
        Assert.Equal(2, catalog.Definitions.Count(definition => definition.Sex is not null));

        Assert.Single(catalog.Diagnostics, diagnostic =>
            diagnostic.Code == "unsupported-award-property" &&
            diagnostic.AwardName == "contest_firstbr40" &&
            diagnostic.PropertyName == "Only");
    }

    private static string? FindPlanetSideDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("PLANETSIDE_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;
        const string local = "/home/brynn/Downloads/PlanetSide";
        return Directory.Exists(local) ? local : null;
    }
}

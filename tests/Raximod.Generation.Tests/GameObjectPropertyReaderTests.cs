using Raximod.EngineAssets.Databases;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class GameObjectPropertyReaderTests
{
    private static readonly KeyValuePair<string, List<string>>[] Properties =
    [
        new("scalar", ["one"]),
        new("vector", ["1", "2", "3"]),
        new("meshes", ["left", "right"]),
    ];

    [Fact]
    public void ScalarRejectsAuthoredListsInsteadOfDiscardingTheirTail()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            GameObjectPropertyReader.Scalar(Properties, "trhev_dualcycler", "meshes"));

        Assert.Contains("contains 2 values", error.Message);
        Assert.Contains("Tuple(...) or List(...)", error.Message);
    }

    [Fact]
    public void ScalarIsCaseInsensitiveAndAllowsMissingProperties()
    {
        Assert.Equal("one", GameObjectPropertyReader.Scalar(Properties, "record", "SCALAR"));
        Assert.Null(GameObjectPropertyReader.Scalar(Properties, "record", "missing"));
    }

    [Fact]
    public void TupleRequiresAnExplicitAllowedArity()
    {
        Assert.Equal(["1", "2", "3"],
            GameObjectPropertyReader.Tuple(Properties, "projectile", "vector", 3));
        Assert.Throws<InvalidDataException>(() =>
            GameObjectPropertyReader.Tuple(Properties, "projectile", "vector", 2));
    }

    [Fact]
    public void ListPreservesEveryAuthoredValueInOrder()
    {
        Assert.Equal(["left", "right"], GameObjectPropertyReader.List(Properties, "meshes"));
    }
}

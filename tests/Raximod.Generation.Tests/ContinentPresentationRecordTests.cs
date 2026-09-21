using Raximod.Generation.Continents;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class ContinentPresentationRecordTests
{
    [Theory]
    [InlineData("force_dome_amp_physics")]
    [InlineData("force_dome_comm_physics")]
    [InlineData("FORCE_DOME_TECH_PHYSICS")]
    public void FacilityForceDomePhysicsIsNotPresentation(string record)
    {
        Assert.False(ContinentExportTool.IsRenderableSceneRecord(record));
    }

    [Theory]
    [InlineData("force_dome_amp")]
    [InlineData("amp_station")]
    [InlineData("tree_canopy")]
    public void OrdinaryAndVisualRecordsRemainRenderable(string record)
    {
        Assert.True(ContinentExportTool.IsRenderableSceneRecord(record));
    }
}

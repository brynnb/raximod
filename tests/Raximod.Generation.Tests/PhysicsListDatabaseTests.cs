using Raximod.EngineAssets.Databases;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class PhysicsListDatabaseTests
{
    [Fact]
    public void ParseFilePreservesCompoundPrimitivesAndCompleteWheelConstraint()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                phys_model prowler
                phys_default_constraint_orient 0 0 0
                phys_box root 75.3 6.5 4.2 2.1 0.1 0.0 1.2
                phys_orientation 4096 -682 803
                phys_cookie root
                phys_com_offset -5 0 1
                phys_model_collides_with_objects true
                phys_model_collides_with_terrain false
                phys_material prowler
                phys_aggregate 1
                phys_end_info
                phys_sphere cdsph0 1.0 0.9 2.82 2.09 0.9
                phys_cookie lefttread
                phys_model_collides_with_objects false
                phys_model_collides_with_terrain true
                phys_offset_highlimit 0 0 0.1
                phys_offset_lowlimit 0 0 -0.4
                phys_end_info
                phys_carwheel ptreadfl agg1 cdsph0
                phys_carwheel_suspension_chassisheight 0.9
                phys_carwheel_suspension_travel 0.5
                phys_carwheel_suspension_damping 25
                phys_carwheel_suspension_ztos 0.8
                phys_carwheel_suspension_softness 0.25
                phys_carwheel_steers false
                phys_carwheel_drives true
                phys_carwheel_brakes true
                phys_end_info
                phys_rpro root_rpro agg1 root
                phys_rpro_linearstrength 1 1 1
                phys_rpro_angularstrength 1 1 1
                phys_end_info
                """);

            var database = new PhysicsListDatabase();
            database.ParseFile(path);
            PhysicsListDatabase.Model model = Assert.IsType<PhysicsListDatabase.Model>(
                database.FindModel("PROWLER"));

            PhysicsListDatabase.Shape box = Assert.Single(model.Shapes,
                value => value.Kind == PhysicsListDatabase.ShapeKind.Box);
            Assert.Equal(new System.Numerics.Vector3(6.5f, 4.2f, 2.1f), box.Size);
            Assert.Equal(MathF.PI / 2, box.Orientation.X, 5);
            Assert.Equal(-14.9853515625f, box.Orientation.Y * 180 / MathF.PI, 4);
            Assert.Equal(17.64404296875f, box.Orientation.Z * 180 / MathF.PI, 4);
            Assert.Equal("prowler", box.Material);
            Assert.Equal(new System.Numerics.Vector3(-5f, 0f, 1f), box.CenterOfMassOffset);
            Assert.Equal(new System.Numerics.Vector3(-5f, 0f, 1f), model.CenterOfMassOffset);
            Assert.True(box.CollidesWithObjects);
            Assert.False(box.CollidesWithTerrain);

            PhysicsListDatabase.Shape sphere = Assert.Single(model.Shapes,
                value => value.Kind == PhysicsListDatabase.ShapeKind.Sphere);
            Assert.Equal(new System.Numerics.Vector3(0f, 0f, 0.1f), sphere.OffsetHighLimit);
            Assert.Equal(new System.Numerics.Vector3(0f, 0f, -0.4f), sphere.OffsetLowLimit);
            Assert.False(sphere.CollidesWithObjects);
            Assert.True(sphere.CollidesWithTerrain);

            PhysicsListDatabase.Shape wheel = Assert.Single(model.Shapes,
                value => value.Kind == PhysicsListDatabase.ShapeKind.CarWheel);
            Assert.Equal("cdsph0", wheel.Primitive);
            Assert.False(wheel.Steers);
            Assert.True(wheel.Drives);
            Assert.True(wheel.Brakes);
            Assert.Equal(0.9f, wheel.SuspensionChassisHeight);
            Assert.Equal(0.5f, wheel.SuspensionTravel);
            Assert.Equal(25f, wheel.SuspensionDamping);
            Assert.Equal(0.8f, wheel.SuspensionZToS);
            Assert.Equal(0.25f, wheel.SuspensionSoftness);

            Assert.Equal(4, database.RetainedUnsupportedCommands.Count);
            Assert.Equal(
                ["phys_default_constraint_orient", "phys_rpro", "phys_rpro_linearstrength", "phys_rpro_angularstrength"],
                database.RetainedUnsupportedCommands.Select(command => command.Name));
            Assert.All(database.RetainedUnsupportedCommands, command => Assert.Equal("prowler", command.Model));
            Assert.Equal(1, database.CommandCounts["phys_com_offset"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KnownCommandContractMatchesInstalledPhysicsListCorpus()
    {
        string[] expected =
        [
            "phys_aggregate", "phys_bonewrite", "phys_box", "phys_carwheel",
            "phys_carwheel_brakes", "phys_carwheel_drives", "phys_carwheel_steers",
            "phys_carwheel_suspension_chassisheight", "phys_carwheel_suspension_damping",
            "phys_carwheel_suspension_softness", "phys_carwheel_suspension_travel",
            "phys_carwheel_suspension_ztos", "phys_collisionboneoffset", "phys_com_offset",
            "phys_cone", "phys_cone_axis", "phys_cone_damping", "phys_cone_half_angle",
            "phys_cone_stiffness", "phys_cookie", "phys_cylinder",
            "phys_default_constraint_orient", "phys_end_info", "phys_hinge", "phys_hinge_axis",
            "phys_hinge_limits", "phys_hinge_pos_offset", "phys_hinge_stiffness", "phys_material",
            "phys_material_adhesion", "phys_material_dimensions", "phys_material_friction",
            "phys_material_interaction", "phys_material_primaryslip", "phys_material_restitution",
            "phys_material_secondaryslip", "phys_material_softness",
            "phys_material_tensilestrength", "phys_model", "phys_model_collides_with_objects",
            "phys_model_collides_with_terrain", "phys_offset_highlimit", "phys_offset_lowlimit",
            "phys_orientation", "phys_rpro", "phys_rpro_angularstrength",
            "phys_rpro_linearstrength", "phys_shares_geometry", "phys_sphere", "phys_tri_end",
            "phys_tri_info", "phys_tri_num_tris", "phys_tri_vert_info",
            "phys_use_skeleton_xfrm",
        ];

        Assert.Equal(expected.Order(), PhysicsListDatabase.KnownCommandNames.Order());
        Assert.Equal(24, PhysicsListDatabase.RetainedUnsupportedCommandNames.Count);
    }

    [Fact]
    public void ParseFileRetainsKnownUnsupportedMaterialCommandsWithoutAFalseSemanticClaim()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                phys_material_interaction dropship
                phys_material_dimensions 2
                phys_material_friction 4000
                phys_material_softness 0.001
                phys_material_adhesion 0
                phys_material_restitution 0.001
                phys_material_primaryslip 0.00001
                phys_material_secondaryslip 0.0001
                """);

            var database = new PhysicsListDatabase();
            database.ParseFile(path);

            Assert.Equal(8, database.RetainedUnsupportedCommands.Count);
            Assert.Equal("dropship", database.RetainedUnsupportedCommands[0].Arguments.Single());
            Assert.Null(database.RetainedUnsupportedCommands[0].Model);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseFileRetainsForceDomeNullPrimitiveCommands()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                phys_model force_dome_amp
                phys_orientation 0 0 0
                phys_cookie null
                phys_material null
                phys_com_offset 0 0 0
                phys_end_info
                """);

            var database = new PhysicsListDatabase();
            database.ParseFile(path);
            PhysicsListDatabase.Model model = Assert.IsType<PhysicsListDatabase.Model>(
                database.FindModel("force_dome_amp"));

            Assert.Empty(model.Shapes);
            Assert.Equal(System.Numerics.Vector3.Zero, model.CenterOfMassOffset);
            Assert.Equal(
                ["phys_orientation", "phys_cookie", "phys_material", "phys_com_offset"],
                model.UnsupportedCommands.Select(command => command.Name));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseFileFailsClosedOnAnUnknownCommand()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "phys_future_constraint 1 2 3\n");
            var database = new PhysicsListDatabase();

            InvalidDataException error = Assert.Throws<InvalidDataException>(() => database.ParseFile(path));

            Assert.Contains(":1: unknown physics-list command 'phys_future_constraint'", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseDirectoryHasDeterministicCrossFileCommandOrder()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"physics-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "physics_z.lst"), "phys_material_interaction z\n");
            File.WriteAllText(Path.Combine(directory, "physics_a.lst"), "phys_material_interaction a\n");

            PhysicsListDatabase database = PhysicsListDatabase.ParseDirectory(directory);

            Assert.Equal(
                ["physics_a.lst", "physics_z.lst"],
                database.RetainedUnsupportedCommands.Select(command =>
                    Path.GetFileName(command.SourcePath)));
            Assert.Equal([1L, 2L], database.RetainedUnsupportedCommands.Select(command => command.Order));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

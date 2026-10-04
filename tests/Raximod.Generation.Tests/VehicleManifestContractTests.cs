using System.Numerics;
using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class VehicleManifestContractTests
{
    [Fact]
    public void CoordinateContractDistinguishesConvertedModelsFromRetainedNativeVectors()
    {
        Assert.Equal(15, VehicleManifestContract.SchemaVersion);
        Assert.Equal("right-handed-y-up", VehicleManifestContract.ModelAssetCoordinateSystem);
        Assert.Equal("right-handed-z-up", VehicleManifestContract.NativeDataCoordinateSystem);
        Assert.NotEqual(
            VehicleManifestContract.ModelAssetCoordinateSystem,
            VehicleManifestContract.NativeDataCoordinateSystem);
        Assert.Equal(
            "runtime-consumed-approximation",
            VehicleManifestContract.CenterOfMassRuntimeFidelity);
    }

    [Fact]
    public void TrackedVehiclePresentationCorpusPreservesBothAuthoredMaterialSides()
    {
        VehicleManifestContract.RequireExpectedTrackedVehiclePresentations(
            VehicleManifestContract.TrackedVehiclePresentations);
        Assert.All(VehicleManifestContract.TrackedVehiclePresentations, pair =>
        {
            Assert.Equal(2, pair.Value.Length);
            Assert.False(pair.Value[0].RightTread);
            Assert.True(pair.Value[1].RightTread);
            Assert.All(pair.Value, tread => Assert.True(tread.RollLength > 0));
        });

        var missingSide = VehicleManifestContract.TrackedVehiclePresentations.ToDictionary(
            pair => pair.Key,
            pair => pair.Key == "prowler" ? pair.Value[..1] : pair.Value,
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains("prowler", Assert.Throws<InvalidDataException>(() =>
            VehicleManifestContract.RequireExpectedTrackedVehiclePresentations(missingSide)).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnimationAttachmentResolvesAgainstTheEmittedAliasModelAndFailsClosed()
    {
        Assert.Equal("quad_body", VehicleManifestContract.ResolveAnimationAttachBone(
            "fury", "fury_body", "quad_body", ["quad_body", "hp_gun"]));
        Assert.Equal("thresher_body", VehicleManifestContract.ResolveAnimationAttachBone(
            "twomanhoverbuggy", "thresher_body", "thresher_body", ["thresher_body"]));
        Assert.Equal("deliverer_body", VehicleManifestContract.ResolveAnimationAttachBone(
            "battlewagon", "battlewagontr_body", "battlewagontr_body",
            ["deliverer_body", "deliverer_turret_front"]));
        Assert.Equal("ams", VehicleManifestContract.ResolveAnimationAttachBone(
            "ams", "ams_body", "ams_body", ["ams", "spawn_terminal"]));
        Assert.Null(VehicleManifestContract.ResolveAnimationAttachBone(
            "droppod", null, null, ["root"]));
        Assert.Contains("do not exist", Assert.Throws<InvalidDataException>(() =>
            VehicleManifestContract.ResolveAnimationAttachBone(
                "broken", "missing", null, ["body"])).Message);
    }

    [Fact]
    public void FlightControlClassificationSeparatesPilotDropPodAndServerScriptedCorpus()
    {
        var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string definition, string expected) in
            VehicleManifestContract.NonBfrFlightControlClassifications)
        {
            string sourceRecord = definition switch
            {
                "wasp" => "mosquito",
                "vulture" => "liberator",
                _ => definition,
            };
            bool? serverControlled = definition == "orbital_shuttle" ? true : null;
            string classification = Assert.IsType<string>(
                VehicleManifestContract.ClassifyFlightControl(
                    definition, sourceRecord, canFly: true, serverControlled));
            Assert.Equal(expected, classification);
            actual[definition] = classification;
        }

        Assert.Null(VehicleManifestContract.ClassifyFlightControl(
            "lightning", "lightning", canFly: false, flightIsAlwaysServerControlled: null));
        VehicleManifestContract.RequireExpectedFlightControls(
            actual, VehicleManifestContract.NonBfrFlightControlClassifications);

        actual["lightning"] = VehicleManifestContract.PilotFlightControl;
        Assert.Contains("unexpected: lightning", Assert.Throws<InvalidDataException>(() =>
            VehicleManifestContract.RequireExpectedFlightControls(
                actual, VehicleManifestContract.NonBfrFlightControlClassifications)).Message);
    }

    [Fact]
    public void VehiclePhysicsRequiresAnObjectCollidingBodyAndDoesNotCountWheelContacts()
    {
        PhysicsListDatabase database = Parse("""
            phys_model dropship
            phys_box root 10 5 5 5 0 0 0
            phys_end_info
            phys_sphere contact 1 1 0 0 0
            phys_end_info
            phys_carwheel wheel agg1 contact
            phys_end_info
            """);
        PhysicsListDatabase.Model model = Assert.IsType<PhysicsListDatabase.Model>(
            database.FindModel("dropship"));

        VehicleManifestContract.PhysicsCoverage coverage =
            VehicleManifestContract.RequireVehiclePhysics(database, "galaxy_gunship", "dropship");

        Assert.Equal(new("galaxy_gunship", "dropship", 1), coverage);
        Assert.Equal("body", VehicleManifestContract.PrimitiveRole(model, model.Shapes[0]));
        Assert.Equal("wheel-contact", VehicleManifestContract.PrimitiveRole(model, model.Shapes[1]));
    }

    [Fact]
    public void VehiclePhysicsFailsClosedForMissingNamesModelsAndBodyFootprints()
    {
        PhysicsListDatabase database = Parse("""
            phys_model wheel_only
            phys_sphere contact 1 1 0 0 0
            phys_end_info
            phys_carwheel wheel agg1 contact
            phys_end_info
            """);

        Assert.Contains("no authored physics name", Assert.Throws<InvalidDataException>(() =>
            VehicleManifestContract.RequireVehiclePhysics(database, "mosquito", null)).Message);
        Assert.Contains("missing physics model 'missing'", Assert.Throws<InvalidDataException>(() =>
            VehicleManifestContract.RequireVehiclePhysics(database, "mosquito", "missing")).Message);
        Assert.Contains("zero role=body primitives", Assert.Throws<InvalidDataException>(() =>
            VehicleManifestContract.RequireVehiclePhysics(database, "mosquito", "wheel_only")).Message);
    }

    [Fact]
    public void StaticTurretPathAllowsMissingModelsButAuditsResolvedOnes()
    {
        PhysicsListDatabase database = Parse("""
            phys_model spitfire_turret
            phys_box root 10 5 5 5 0 0 0
            phys_end_info
            """);

        Assert.Null(VehicleManifestContract.AuditTurretPhysics(
            database, "manned_turret", "manned_turret"));
        Assert.Equal(
            new("spitfire_turret", "spitfire_turret", 1),
            VehicleManifestContract.AuditTurretPhysics(
                database, "spitfire_turret", "spitfire_turret"));
    }

    [Fact]
    public void ExpectedAircraftBodyCountsAreStrictCorpusInvariants()
    {
        Assert.Equal(
            new Dictionary<string, int>
            {
                ["mosquito"] = 22,
                ["lightgunship"] = 22,
                ["wasp"] = 22,
                ["liberator"] = 36,
                ["vulture"] = 36,
                ["dropship"] = 49,
                ["galaxy_gunship"] = 49,
                ["lodestar"] = 30,
                ["phantasm"] = 28,
            }.OrderBy(pair => pair.Key),
            VehicleManifestContract.OrdinaryAircraftBodyPrimitiveCounts.OrderBy(pair => pair.Key));

        var actual = new Dictionary<string, VehicleManifestContract.PhysicsCoverage>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["mosquito"] = new("mosquito", "mosquito", 22),
        };

        VehicleManifestContract.RequireExpectedBodyCounts(actual,
            new Dictionary<string, int> { ["mosquito"] = 22 });
        Assert.Contains("expected 'mosquito' to have 21", Assert.Throws<InvalidDataException>(() =>
            VehicleManifestContract.RequireExpectedBodyCounts(actual,
                new Dictionary<string, int> { ["mosquito"] = 21 })).Message);
    }

    [Fact]
    public void ActiveCollisionContractExcludesHoverContactsButDestroyedGeometryRetainsThem()
    {
        PhysicsListDatabase database = Parse("""
            phys_model hover
            phys_box root 10 5 4 3 1 2 3
            phys_model_collides_with_objects true
            phys_end_info
            phys_sphere contact 1 1 4 5 6
            phys_model_collides_with_objects false
            phys_end_info
            phys_carwheel wheel agg1 contact
            phys_end_info
            """);
        PhysicsListDatabase.Model model = Assert.IsType<PhysicsListDatabase.Model>(
            database.FindModel("hover"));

        VehicleManifestContract.ActiveCollisionShape active = Assert.Single(
            VehicleManifestContract.ActiveObjectCollisionShapes(model));
        Assert.Equal("physics:hover:root", active.Id);
        Assert.Equal(new Vector3(1, 2, 3), active.SourceCenter);
        Assert.Equal(
            ["root"],
            VehicleManifestContract.CollisionPrimitives(model, objectCollidingOnly: true)
                .Select(shape => shape.Name).ToArray());
        Assert.Equal(
            ["root", "contact"],
            VehicleManifestContract.CollisionPrimitives(model, objectCollidingOnly: false)
                .Select(shape => shape.Name).ToArray());
    }

    [Fact]
    public void CollisionSidecarContractChecksIdsAndSourceCentersAfterBasisConversion()
    {
        VehicleManifestContract.ActiveCollisionShape[] expected =
        [
            new("physics:mediumtransport:root", new Vector3(1, 2, 3)),
        ];
        VehicleManifestContract.RequireMatchingActiveCollisionSidecar(
            "battlewagontr",
            expected,
            [new("physics:mediumtransport:root", [1, 3, -2])]);
        VehicleManifestContract.RequireEquivalentActiveCollisionContracts(
            "shared aliases",
            expected,
            [new("physics:mediumtransport:root", new Vector3(1, 2, 3))]);

        Assert.Contains("missing: physics:mediumtransport:root", Assert.Throws<InvalidDataException>(() =>
            VehicleManifestContract.RequireMatchingActiveCollisionSidecar(
                "battlewagontr", expected, [])).Message);
        Assert.Contains("source center differs", Assert.Throws<InvalidDataException>(() =>
            VehicleManifestContract.RequireMatchingActiveCollisionSidecar(
                "battlewagontr",
                expected,
                [new("physics:mediumtransport:root", [1, 3, -4])])).Message);
        Assert.Contains("source center differs", Assert.Throws<InvalidDataException>(() =>
            VehicleManifestContract.RequireEquivalentActiveCollisionContracts(
                "shared aliases",
                expected,
                [new("physics:mediumtransport:root", new Vector3(1, 2, 4))])).Message);
    }

    [Fact]
    public void SerializedVehicleFamilyAuditsEveryAliasAgainstItsCanonicalSidecar()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("raximod-vehicle-collision-");
        try
        {
            string sidecarPath = Path.Combine(directory.FullName, "battlewagontr.collision.json");
            File.WriteAllText(sidecarPath, """
                {
                  "record": "battlewagontr",
                  "shapes": [
                    {
                      "id": "physics:mediumtransport:root",
                      "source": "physics_lst",
                      "group": "active",
                      "enabled": true,
                      "center": [1, 3, -2]
                    },
                    {
                      "id": "physics:mediumtransport_destroyed:root",
                      "source": "physics_lst",
                      "group": "destroyed",
                      "enabled": false,
                      "center": [0, 0, 0]
                    }
                  ]
                }
                """);
            string manifest = """
                {
                  "vehicles": [
                    {
                      "definition": "battlewagon",
                      "deployedPhysics": null,
                      "model": "models/battlewagontr.glb",
                      "physics": {
                        "model": "mediumtransport",
                        "primitives": [
                          {
                            "name": "root",
                            "role": "body",
                            "position": [1, 2, 3],
                            "collidesWithObjects": true
                          },
                          {
                            "name": "hovercontact",
                            "role": "wheel-contact",
                            "position": [4, 5, 6],
                            "collidesWithObjects": false
                          }
                        ]
                      },
                      "destruction": {
                        "destroyedPhysics": "mediumtransport_destroyed"
                      }
                    },
                    {
                      "definition": "battlewagon_alias",
                      "deployedPhysics": null,
                      "model": "models/battlewagontr.glb",
                      "physics": {
                        "model": "mediumtransport",
                        "primitives": [
                          {
                            "name": "root",
                            "role": "body",
                            "position": [1, 2, 3],
                            "collidesWithObjects": true
                          }
                        ]
                      },
                      "destruction": {
                        "destroyedPhysics": "mediumtransport_destroyed"
                      }
                    }
                  ]
                }
                """;

            Assert.Equal(2, VehicleManifestContract.RequireGeneratedCollisionCoverage(
                manifest, directory.FullName, expectedDefinitionCount: 2));
            Assert.Contains("audited 2 of 3", Assert.Throws<InvalidDataException>(() =>
                VehicleManifestContract.RequireGeneratedCollisionCoverage(
                    manifest, directory.FullName, expectedDefinitionCount: 3)).Message);

            File.WriteAllText(sidecarPath, File.ReadAllText(sidecarPath).Replace(
                "\"physics:mediumtransport:root\"",
                "\"physics:mediumtransport:wrong\"",
                StringComparison.Ordinal));
            Assert.Contains("missing: physics:mediumtransport:root", Assert.Throws<InvalidDataException>(() =>
                VehicleManifestContract.RequireGeneratedCollisionCoverage(
                    manifest, directory.FullName, expectedDefinitionCount: 2)).Message);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static PhysicsListDatabase Parse(string contents)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, contents);
            var database = new PhysicsListDatabase();
            database.ParseFile(path);
            return database;
        }
        finally
        {
            File.Delete(path);
        }
    }
}

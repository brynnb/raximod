using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class ClientWeaponMetadataResolverTests
{
    [Fact]
    public void TurningAccuracyRetainsWeaponLevelUnitsAndRejectsIncompleteOrAmbiguousData()
    {
        var weapon = new GameObjectDb.GameObject { Name = "bolt_driver", Properties = new() };
        Assert.Null(ClientWeaponMetadataResolver.TurningAccuracy(weapon));
        weapon.Properties["turncofpenalty"] = ["0.2"];
        Assert.Throws<InvalidDataException>(() => ClientWeaponMetadataResolver.TurningAccuracy(weapon));
        weapon.Properties["turncofpenalty_max"] = ["3.0"];
        Assert.Equal(new(0.2f, 3f), ClientWeaponMetadataResolver.TurningAccuracy(weapon));
        foreach (var invalid in new string[][] { ["NaN"], ["Infinity"], ["-1"], ["0.2", "0.15"] })
        {
            weapon.Properties["turncofpenalty"] = invalid.ToList();
            Assert.Throws<InvalidDataException>(() => ClientWeaponMetadataResolver.TurningAccuracy(weapon));
        }
    }

    [Fact]
    public void WeaponAimPreservesNativeArcsAndOrientationWithoutInventingChassisJoints()
    {
        var vehicle = new GameObjectDb.GameObject { Properties = new(StringComparer.OrdinalIgnoreCase)
        {
            ["weapon1"] = ["galaxy_gunship_cannon"], ["weaponpoint1_AttachBones"] = ["hp_gund"],
            ["weaponpoint1_BoneOrientation"] = ["0", "0", "90"], ["weaponpoint1_UpDownUsesY"] = ["true"],
            ["weaponpoint1_LeftAOF"] = ["5"], ["weaponpoint1_RightAOF"] = ["80"],
            ["weaponpoint1_UpAOF"] = ["80"], ["weaponpoint1_DownAOF"] = ["80"],
        }};
        var point = ClientWeaponMetadataResolver.IndexedWeaponPoint(1, vehicle)!;
        Assert.Empty(point.YawBones); Assert.Empty(point.PitchBones);
        Assert.Equal([0f, 0f, 90f], point.Aim.OrientationDegrees!);
        Assert.Equal([5f], point.Aim.LeftDegrees); Assert.True(point.Aim.UpDownUsesY);
        // Flail/Switchblade list mobile and deployed limits. Retain both.
        vehicle.Properties["weaponpoint1_LeftAOF"] = ["0", "360"];
        Assert.Equal([0f, 360f], ClientWeaponMetadataResolver.IndexedWeaponPoint(1, vehicle)!.Aim.LeftDegrees);
        vehicle.Properties["weaponpoint1_InvertUpDown"] = ["false", "true"];
        vehicle.Properties["weaponpoint1_InvertYWhenYawBoneIsPitchBone"] = ["false"];
        point = ClientWeaponMetadataResolver.IndexedWeaponPoint(1, vehicle)!;
        Assert.Equal([false, true], point.Aim.InvertUpDown);
        Assert.False(point.Aim.InvertYWhenYawBoneIsPitchBone);
        vehicle.Properties["weaponpoint1_BoneOrientation"] = ["90"];
        Assert.Throws<InvalidDataException>(() => ClientWeaponMetadataResolver.IndexedWeaponPoint(1, vehicle));
    }
    [Fact]
    public void EquippedLoopsPreserveTheWholeNativeTupleAndRejectMalformedData()
    {
        var weapon = new GameObjectDb.GameObject { Name = "chainblade", Properties = new() };
        Assert.Null(ClientWeaponMetadataResolver.LoopingSound(weapon));
        weapon.Properties["soundkey_looping"] = ["knife_tr_secondary_loop.wav", "0.75", "100"];
        Assert.Equal(new("knife_tr_secondary_loop.wav", 0.75f, 100f), ClientWeaponMetadataResolver.LoopingSound(weapon));
        foreach (var invalid in new string[][] {
            ["loop.wav"], ["loop.wav", "0.75", "100", "other.wav"],
            ["loop.wav", "NaN", "100"], ["loop.wav", "1", "0"], ["loop.wav", "-1", "100"],
        }) {
            weapon.Properties["soundkey_looping"] = invalid.ToList();
            Assert.Throws<InvalidDataException>(() => ClientWeaponMetadataResolver.LoopingSound(weapon));
        }
    }
    [Fact]
    public void NativeBurstCountsAdditionalRoundsAndRejectsMalformedScalars()
    {
        var weapon = new GameObjectDb.GameObject { Properties = new()
        {
            ["firemode1_autofirecount"] = ["2"],
            ["firemode1_autofiretime"] = ["150"],
            ["firemode1_shotsperround"] = ["8"],
        }};
        Assert.Null(ClientWeaponMetadataResolver.FireBurst(0, weapon));
        Assert.Equal(new(2, 150), ClientWeaponMetadataResolver.FireBurst(1, weapon));
        weapon.Properties["firemode1_autofirecount"] = ["2", "5"];
        Assert.Throws<InvalidDataException>(() => ClientWeaponMetadataResolver.FireBurst(1, weapon));
        weapon.Properties["firemode1_autofirecount"] = ["5"];
        weapon.Properties.Remove("firemode1_autofiretime");
        Assert.Throws<InvalidDataException>(() => ClientWeaponMetadataResolver.FireBurst(1, weapon));
    }
    [Fact]
    public void MissingChildAttachmentUsesTheProvenNativeJointZeroRuleWithoutRenamingSource()
    {
        var exact = ClientWeaponMetadataResolver.AttachmentBone("HP_GUN", ["hp_gun"], "root");
        Assert.Equal("exact", exact.Reason);
        Assert.Equal("hp_gun", exact.Resolved);
        var native = ClientWeaponMetadataResolver.AttachmentBone("barrels_bone", ["barrels_aa_bone"], "barrels_aa_bone");
        Assert.Equal("barrels_bone", native.Requested);
        Assert.Equal("barrels_aa_bone", native.Resolved);
        Assert.Equal("native-joint-zero", native.Reason);
        Assert.Throws<InvalidDataException>(() => ClientWeaponMetadataResolver.AttachmentBone("missing", [], null));
    }
    [Fact]
    public void MuzzleZeroAliasesReplaceRatherThanAppendThroughInheritance()
    {
        var weapon = new GameObjectDb.GameObject
        {
            Name = "child", InheritanceChain = ["parent", "child"],
            Properties = new(StringComparer.OrdinalIgnoreCase)
            {
                ["clientfiremode0_muzzle"] = ["inherited"],
                ["clientfiremode0_muzzle0"] = ["left"],
                ["clientfiremode0_muzzle1"] = ["right"],
            },
            PropertySources = new Dictionary<string, GameObjectDb.GameObjectPropertySource>
            {
                ["clientfiremode0_muzzle"] = new("parent", 20, 400),
                ["clientfiremode0_muzzle0"] = new("child", 2, 40),
                ["clientfiremode0_muzzle1"] = new("child", 3, 60),
            },
        };
        Assert.Equal(["left", "right"], ClientWeaponMetadataResolver.Muzzles(0, weapon));
        var specialized = new GameObjectDb.GameObject
        {
            Properties = new() { ["clientfiremode0_muzzle"] = ["replaced_zero"] },
        };
        Assert.Equal(["replaced_zero", "right"], ClientWeaponMetadataResolver.Muzzles(0, weapon, specialized));
        specialized.Properties["clientfiremode0_muzzle"] = ["ambiguous", "other"];
        Assert.Throws<InvalidDataException>(() => ClientWeaponMetadataResolver.Muzzles(0, specialized));
        specialized.Properties.Clear();
        specialized.Properties["clientfiremode0_muzzle1"] = ["gap"];
        Assert.Throws<InvalidDataException>(() => ClientWeaponMetadataResolver.Muzzles(0, specialized));
    }

    [Fact]
    public void FirstPersonStateDoesNotOverwriteWorldEffectSelection()
    {
        var weapon = new GameObjectDb.GameObject
        {
            Properties = new(StringComparer.OrdinalIgnoreCase)
            {
                ["clientfiremode1_fp_fire_anim_state"] = ["0"],
                ["clientfiremode2_fire_effect_offset"] = ["1"],
                ["clientfiremode2_weaponalternatesfire"] = ["true"],
            },
        };
        var quasarAlternate = ClientWeaponMetadataResolver.FirePresentation(1, weapon);
        Assert.Equal("fire1", quasarAlternate.FirstPersonEvent);
        Assert.Equal(1, quasarAlternate.EffectOffset);
        Assert.Equal(["fire1", "fire2", "fire3"], quasarAlternate.EffectKeys);
        var maxSpecial = ClientWeaponMetadataResolver.FirePresentation(2, weapon);
        Assert.Equal("fire3", maxSpecial.FirstPersonEvent);
        Assert.Equal(1, maxSpecial.EffectOffset);
        Assert.True(maxSpecial.AlternatesFire);
        Assert.Null(maxSpecial.CommonMesh);
        Assert.Equal("fire1", ClientWeaponMetadataResolver.FirePresentation(4, weapon).FirstPersonEvent);
        weapon.Properties["fire_effect_keys"] = ["custom_left", "custom_right"];
        Assert.Equal(["custom_left", "custom_right"], ClientWeaponMetadataResolver.FirePresentation(0, weapon).EffectKeys);
        weapon.Properties["clientfiremode1_fp_fire_anim_state"] = ["0", "1"];
        Assert.Throws<InvalidDataException>(() => ClientWeaponMetadataResolver.FirePresentation(1, weapon));
    }

    [Fact]
    public void OwningWeaponDeclaresModesIndependentlyOfItsReusedMesh()
    {
        var mesh = new GameObjectDb.GameObject { Name = "liberator_bomb_bay", Properties = new()
        {
            ["firemode0_ammo_types"] = ["liberator_bomb"],
            ["firemode1_use_common_ammo"] = ["true"],
        } };
        var weapon = new GameObjectDb.GameObject { Name = "vulture_bomb_bay", Properties = new()
        {
            ["meshsequence"] = [mesh.Name],
            ["firemode0_ammo_types"] = ["liberator_bomb"],
            ["clientfiremode1_crosshair"] = ["not_a_gameplay_mode"],
        } };
        Assert.Equal([0, 1], ClientWeaponMetadataResolver.FireModeIndices(mesh));
        Assert.Equal([0], ClientWeaponMetadataResolver.FireModeIndices(weapon));
        weapon.Properties["FireMode1_use_common_ammo"] = ["true"];
        Assert.Equal([0, 1], ClientWeaponMetadataResolver.FireModeIndices(weapon));
    }

    [Fact]
    public void NativeWeaponUsesStableClassIdentityWithoutRenamingWireDefinition()
    {
        var native = new GameObjectDb.GameObject { Name = "20mm_cannon_dropship", ClassId = 14, Type = "weapon" };
        var objects = new Dictionary<string, GameObjectDb.GameObject> { [native.Name] = native };
        var classes = ClientWeaponMetadataResolver.ServerObjectClasses("""
            object ObjectClass {
              // final val misleading = 123
              final val cannon_dropship_20mm = 14 // Galaxy gun
              final val bullet_20mm = 16
            }
            """);
        Assert.Equal(2, classes.Count);
        var resolved = ClientWeaponMetadataResolver.NativeWeapon("cannon_dropship_20mm", objects, classes);
        Assert.Equal("20mm_cannon_dropship", resolved?.Name);
        Assert.Equal(14, resolved?.ClassId);
        Assert.Equal(14, classes["cannon_dropship_20mm"]);
        Assert.Null(ClientWeaponMetadataResolver.NativeWeapon("unregistered", objects, classes));
    }

    [Fact]
    public void ClassResolutionRejectsConflictsAndNonWeaponMatches()
    {
        var classes = new Dictionary<string, int> { ["server_weapon"] = 14 };
        var objects = new Dictionary<string, GameObjectDb.GameObject>
        {
            ["server_weapon"] = new() { Name = "server_weapon", ClassId = 15, Type = "weapon" },
        };
        Assert.Throws<InvalidDataException>(() =>
            ClientWeaponMetadataResolver.NativeWeapon("server_weapon", objects, classes));
        objects.Clear();
        Assert.Throws<InvalidDataException>(() =>
            ClientWeaponMetadataResolver.NativeWeapon("server_weapon", objects, classes));
        objects["wrong_type"] = new() { Name = "wrong_type", ClassId = 14, Type = "ammo" };
        Assert.Throws<InvalidDataException>(() =>
            ClientWeaponMetadataResolver.NativeWeapon("server_weapon", objects, classes));
        objects["native_weapon"] = new() { Name = "native_weapon", ClassId = 14, Type = "weapon" };
        Assert.Throws<InvalidDataException>(() =>
            ClientWeaponMetadataResolver.NativeWeapon("server_weapon", objects, classes));
        Assert.Throws<InvalidDataException>(() => ClientWeaponMetadataResolver.ServerObjectClasses("unknown"));
        Assert.Throws<InvalidDataException>(() => ClientWeaponMetadataResolver.ServerObjectClasses(
            "final val a = 1\nfinal val a = 2"));
    }

    [Fact]
    public void RenamedWeaponResolvesBothGalaxyMountOccurrences()
    {
        var vehicle = new GameObjectDb.GameObject
        {
            Properties = new(StringComparer.OrdinalIgnoreCase)
            {
                ["weapon1"] = ["20mm_cannon_dropship"],
                ["weapon2"] = ["20mm_cannon_dropship"],
                ["weaponpoint1_AttachBones"] = ["hp_left"],
                ["weaponpoint2_AttachBones"] = ["hp_right"],
            },
        };
        Assert.Equal(["hp_left"], ClientWeaponMetadataResolver.ExactWeaponPoint("20mm_cannon_dropship", 0, vehicle)!.AttachBones);
        Assert.Equal(["hp_right"], ClientWeaponMetadataResolver.ExactWeaponPoint("20mm_cannon_dropship", 1, vehicle)!.AttachBones);
        Assert.Null(ClientWeaponMetadataResolver.ExactWeaponPoint("20mm_cannon_dropship", 2, vehicle));
    }

    [Fact]
    public void ComponentsPreserveMeshAndWeaponAttachmentOrder()
    {
        var weapon = new GameObjectDb.GameObject
        {
            Name = "lightning_weapon_system",
            Properties = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["meshsequence"] = ["75mm_lightning", "chaingun_lightning"],
                ["weaponattachbonenames"] = ["hp_75mm", "hp_chaingun"],
            },
        };

        Assert.Equal(
            [
                new ClientWeaponMetadataResolver.WeaponComponent(0, "75mm_lightning", "hp_75mm"),
                new ClientWeaponMetadataResolver.WeaponComponent(1, "chaingun_lightning", "hp_chaingun"),
            ],
            ClientWeaponMetadataResolver.Components(weapon));
    }

    [Fact]
    public void ComponentsKeepMissingParallelAttachmentDataObservable()
    {
        var weapon = new GameObjectDb.GameObject
        {
            Name = "partial_weapon_system",
            Properties = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["meshsequence"] = ["left_gun", "right_gun"],
                ["weaponattachbonenames"] = ["hp_left"],
            },
        };

        ClientWeaponMetadataResolver.WeaponComponent[] components =
            ClientWeaponMetadataResolver.Components(weapon);

        Assert.Equal("hp_left", components[0].WeaponAttachBone);
        Assert.Null(components[1].WeaponAttachBone);
    }
}

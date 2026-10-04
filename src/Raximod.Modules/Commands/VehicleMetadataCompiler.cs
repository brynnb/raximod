using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;

namespace Raximod.Modules;

/// <summary>
/// Compiles source-backed vehicle handling, presentation, seat, physics, and destruction metadata.
/// Export orchestration and file publication remain in <see cref="ExportVehiclesCommand"/>.
/// </summary>
internal sealed class VehicleMetadataCompiler
{
    private readonly Dictionary<string, GameObjectDb.GameObject> gameObjects;
    private readonly Dictionary<string, VehicleEntryBindings.Entry[]> nativeEntries;
    private readonly IReadOnlyList<VehicleMountBinding> mountBindings;
    private readonly IReadOnlyList<(string Vehicle, int Seat, int Slot)> controlledWeapons;
    private readonly IReadOnlyDictionary<string, ExportedAsset> modelResults;
    private readonly IReadOnlyDictionary<string, string> vehicleRenderRecords;
    private readonly string[] interactionAnimationNames;
    private readonly PhysicsListDatabase physics;
    private readonly IReadOnlySet<string> effectNames;
    private readonly IReadOnlyDictionary<string, string> vehicleExplosionAliases;
    private readonly IReadOnlyDictionary<string, ExportedAsset?> wreckResults;

    public VehicleMetadataCompiler(
        Dictionary<string, GameObjectDb.GameObject> gameObjects,
        Dictionary<string, VehicleEntryBindings.Entry[]> nativeEntries,
        IReadOnlyList<VehicleMountBinding> mountBindings,
        IReadOnlyList<(string Vehicle, int Seat, int Slot)> controlledWeapons,
        IReadOnlyDictionary<string, ExportedAsset> modelResults,
        IReadOnlyDictionary<string, string> vehicleRenderRecords,
        string[] interactionAnimationNames,
        PhysicsListDatabase physics,
        IReadOnlySet<string> effectNames,
        IReadOnlyDictionary<string, string> vehicleExplosionAliases,
        IReadOnlyDictionary<string, ExportedAsset?> wreckResults)
    {
        this.gameObjects = gameObjects;
        this.nativeEntries = nativeEntries;
        this.mountBindings = mountBindings;
        this.controlledWeapons = controlledWeapons;
        this.modelResults = modelResults;
        this.vehicleRenderRecords = vehicleRenderRecords;
        this.interactionAnimationNames = interactionAnimationNames;
        this.physics = physics;
        this.effectNames = effectNames;
        this.vehicleExplosionAliases = vehicleExplosionAliases;
        this.wreckResults = wreckResults;
    }

public VehicleExportData Compile(string definition, string sourceRecord)
    {
        if (!gameObjects.TryGetValue(definition, out GameObjectDb.GameObject? gameObject))
            throw new InvalidOperationException($"No game_objects.adb vehicle definition for {definition}");
        gameObjects.TryGetValue(sourceRecord, out GameObjectDb.GameObject? sourceObject);
        var properties = ((sourceObject?.Properties ?? []).Concat(gameObject.Properties))
            .GroupBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();

        string? Value(string key) => GameObjectPropertyReader.Scalar(
            properties, $"{definition} (merged with {sourceRecord})", key);
        string? SoundValue(string key) => GameObjectPropertyReader.Tuple(
            properties, $"{definition} (merged with {sourceRecord})", key, 1, 2, 3)?[0] switch
        {
            "quad_driver_mount.wav" => "quadassault_driver_mount.wav",
            "quad_driver_dismount.wav" => "quadassault_driver_dismount.wav",
            string value => value,
            _ => null,
        };
        float? Number(string key) => float.TryParse(Value(key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : null;
        bool? OptionalFlag(string key) => Value(key) switch
        {
            null => null,
            string value when value.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            string value when value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            string value => throw new InvalidDataException(
                $"Vehicle '{definition}' has non-boolean {key} value '{value}'"),
        };
        bool Flag(string key) => OptionalFlag(key) == true;
        bool? OptionalBinaryFlag(string key) => Value(key) switch
        {
            null => null,
            "1" => true,
            "0" => false,
            string value when value.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            string value when value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            string value => throw new InvalidDataException(
                $"Vehicle '{definition}' has non-binary {key} value '{value}'"),
        };
        string? FlightControl() => VehicleManifestContract.ClassifyFlightControl(
            definition,
            sourceRecord,
            Flag("canfly"),
            OptionalFlag("flightisalwaysservercontrolled"));
        float[] DriverLookLimits(int mountPoint, string suffix)
        {
            string key = $"mountpoint{mountPoint}_{suffix}";
            IReadOnlyList<string>? values = GameObjectPropertyReader.Tuple(
                properties, $"{definition} (merged with {sourceRecord})", key, 1, 2);
            if (values is null) return [];
            return values.Select(value => float.Parse(value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        }
        float? DriverLookLimit(int mountPoint, string suffix)
        {
            float[] values = DriverLookLimits(mountPoint, suffix);
            // Flail and Switchblade author a two-value, mode-specific tuple. The first value is the
            // normal driving view used by this camera block; the lossless catalog retains both.
            return values.Cast<float?>().FirstOrDefault();
        }
        float[] Numbers(string prefix, string suffix) => properties
            .Where(property => property.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && property.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .SelectMany(property => property.Value)
            .Select(value => float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float parsed) ? (float?)parsed : null)
            .Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        float[] PropertyNumbers(params string[] keys) => properties
            .Where(property => keys.Any(key => property.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(property => property.Value)
            .Select(value => float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float parsed) ? (float?)parsed : null)
            .Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        float[]? Vector(string key)
        {
            IReadOnlyList<string>? values = GameObjectPropertyReader.Tuple(
                properties, $"{definition} (merged with {sourceRecord})", key, 3);
            if (values is null) return null;
            return values.Select(value => float.Parse(value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        }
        float[][]? VectorList(string key, params int[] allowedLengths)
        {
            IReadOnlyList<string>? values = GameObjectPropertyReader.Tuple(
                properties, $"{definition} (merged with {sourceRecord})", key, allowedLengths);
            if (values is null) return null;
            return values.Chunk(3).Select(chunk => chunk.Select(value => float.Parse(value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToArray();
        }
        void AuditPilotControlledFlightContract()
        {
            if (FlightControl() != VehicleManifestContract.PilotFlightControl) return;

            string[] supportedFlightFields =
            [
                "flight_cargo_dimensions", "flight_cargo_offset", "flight_skid_positions",
                "flight_water_effect_spray_pct", "flight_water_effect_tail_bone",
                "flight_water_effect_tail_root_dist", "flightafterburnerbuildup",
                "flightafterburnercapacitorrechargerate", "flightafterburnercapacitorrechargeratedelay",
                "flightafterburnerforce", "flightafterburnermaxcapacitorduration",
                "flightafterburnerturbulencefrequency", "flightafterburnerturbulencestandard",
                "flightafterburnerturbulencestart", "flightafterburnerturbulencestartduration",
                "flightcanelevate", "flightcontrailthrottlevalue", "flightdescentspeed",
                "flightdustcreationheight", "flightdustcreationoffset", "flightdusteffectname",
                "flightelevatespeed", "flightelevationspeedupfactor", "flightengineuseroll",
                "flightengineuseyorient", "flightgravityactsatlowspeed", "flightgravitycutoffspeed",
                "flighthasafterburners", "flighthashelicopterfeel", "flightheightforsoftlanding",
                "flightlandingenginemaxrotation", "flightlandingenginespeed", "flightlandingheight",
                "flightlandingmaxpitch", "flightlandingpitchincrement", "flightlandingspeed",
                "flightleftenginebonename", "flightleftwingbonename", "flightmaxacceleration",
                "flightmaxheight", "flightmaxpitchatmaxspeed", "flightmaxpitchatminspeed",
                "flightmaxroll", "flightmaxspeed", "flightmaxyawmaxpitchpenaltypercentage",
                "flightminacceleration", "flightminheight", "flightminspeed", "flightmomentumfactors",
                "flightmomentumturncutoff", "flightrateofascent", "flightrateofdescent",
                "flightrightenginebonename", "flightrightwingbonename", "flightsecondstomaxdescent",
                "flightsecondstomaxelevate", "flightsecondstomaxpitch", "flightsecondstomaxyaw",
                "flightskidabonename", "flightskidbbonename", "flightskidcbonename",
                "flightskiddbonename", "flightskidebonename", "flightskidleftrotbonename",
                "flightskidpositionmax", "flightskidrightrotbonename", "flightskidrotationmax",
                "flighttailbonename", "flightthrottledown", "flightturbulence", "flightturbulencefreq",
                "flightusestfdb", "flightverticalfractionatmaxspeed", "flightwingextendmax",
                "flightwingextendspeed", "flightzerothrottleforlanding",
            ];
            string[] unexplained = properties
                .Select(property => property.Key.ToLowerInvariant())
                .Where(key => key.StartsWith("flight", StringComparison.Ordinal)
                    && !supportedFlightFields.Contains(key, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal).Order().ToArray();
            if (unexplained.Length > 0)
                throw new InvalidDataException(
                    $"Pilot-controlled aircraft '{definition}' has unexplained flight field(s): "
                    + string.Join(", ", unexplained));

            // These fields form the shared native control contract for every ordinary aircraft in the
            // installed client. Fail extraction if a future source/export change silently drops one;
            // optional character fields such as low-speed gravity remain nullable by design.
            string[] requiredScalars =
            [
                "flightcanelevate", "flightmaxspeed", "flightminspeed",
                "flightmaxacceleration", "flightminacceleration",
                "flightelevatespeed", "flightdescentspeed", "flightelevationspeedupfactor",
                "flightrateofascent", "flightrateofdescent",
                "flightsecondstomaxelevate", "flightsecondstomaxdescent",
                "flightmaxheight", "flightmaxpitchatminspeed", "flightmaxpitchatmaxspeed",
                "flightmaxroll", "flightmaxyawmaxpitchpenaltypercentage",
                "flightsecondstomaxpitch", "flightsecondstomaxyaw",
                "minyawspeed", "maxyawspeed", "flightmomentumturncutoff",
                "flightverticalfractionatmaxspeed", "strafespeed", "strafefractionatmaxspeed",
                "secondstofullthrottle", "flightusestfdb", "flightturbulence", "flightturbulencefreq",
                "flightlandingheight", "flightheightforsoftlanding", "flightlandingspeed",
                "flightlandingmaxpitch", "flightlandingpitchincrement",
            ];
            string[] missing = requiredScalars.Where(key => Value(key) is null).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException(
                    $"Pilot-controlled aircraft '{definition}' is missing required flight field(s): "
                    + string.Join(", ", missing));
            _ = GameObjectPropertyReader.Tuple(
                properties, $"{definition} (merged with {sourceRecord})", "flightmomentumfactors", 3)
                ?? throw new InvalidDataException(
                    $"Pilot-controlled aircraft '{definition}' is missing flightmomentumfactors");

            if (!Flag("flighthasafterburners")) return;
            string[] afterburnerFields =
            [
                "flightafterburnerforce", "flightafterburnerbuildup",
                "flightafterburnermaxcapacitorduration", "flightafterburnercapacitorrechargerate",
                "flightafterburnercapacitorrechargeratedelay",
            ];
            missing = afterburnerFields.Where(key => Value(key) is null).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException(
                    $"Afterburner aircraft '{definition}' is missing capacitor field(s): "
                    + string.Join(", ", missing));
        }
        AuditPilotControlledFlightContract();
        int? driverMount = properties
            .Select(property => (Property: property,
                Match: Regex.Match(property.Key, "^mountpoint(\\d+)_IsDriver$", RegexOptions.IgnoreCase)))
            .Where(value => value.Match.Success
                && value.Property.Value.Any(item => string.Equals(item, "true", StringComparison.OrdinalIgnoreCase)))
            .Select(value => (int?)int.Parse(value.Match.Groups[1].Value))
            .FirstOrDefault();
        var wheelRadii = Numbers("wheel", "_radius");
        var trackingSpeeds = Numbers("weaponpoint", "_TrackingSpeed");
        var steeringAngles = PropertyNumbers("maxsteeringwheel", "maxsteeringwheel_left", "maxsteeringwheel_right");
        string[] flightSkidBones = Enumerable.Range('a', 5)
            .Select(value => Value($"flightskid{(char)value}bonename"))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
        string? physicsName = Value("physics");
        PhysicsListDatabase.Model? physicsModel = physics.FindModel(physicsName);
        if (physicsModel is not null && physicsModel.CenterOfMassOffset is null)
            throw new InvalidDataException(
                $"Vehicle '{definition}' physics model '{physicsModel.Name}' has no authored phys_com_offset");
        PhysicsListDatabase.Shape[] wheelConstraints = physicsModel?.Shapes
            .Where(shape => shape.Kind == PhysicsListDatabase.ShapeKind.CarWheel).ToArray() ?? [];
        object? physicsProfile = physicsModel is null ? null : ExportPhysicsProfile(physicsModel);
        string? deployedName = Value("physics_deployed");
        PhysicsListDatabase.Model? deployedModel = null;
        if (deployedName is not null && !deployedName.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            _ = VehicleManifestContract.RequireVehiclePhysics(physics, definition + ":deployed", deployedName);
            deployedModel = physics.FindModel(deployedName)
                ?? throw new InvalidDataException(
                    $"Vehicle '{definition}' physics_deployed '{deployedName}' is missing");
        }
        object? deployedPhysicsProfile = deployedModel is null ? null : ExportPhysicsProfile(deployedModel);
        var wheels = gameObject.Properties
            .Select(property => Regex.Match(property.Key, "^wheel(\\d+)_constraint$", RegexOptions.IgnoreCase))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value))
            .Distinct().OrderBy(index => index)
            .Select(index =>
            {
                string? constraintName = Value($"wheel{index}_constraint");
                string? primitiveName = Value($"wheel{index}_model");
                PhysicsListDatabase.Shape? constraint = wheelConstraints.FirstOrDefault(shape =>
                    shape.Name.Equals(constraintName, StringComparison.OrdinalIgnoreCase));
                PhysicsListDatabase.Shape? sphere = physicsModel?.Shapes.FirstOrDefault(shape =>
                    shape.Kind == PhysicsListDatabase.ShapeKind.Sphere
                    && shape.Name.Equals(primitiveName ?? constraint?.Primitive,
                        StringComparison.OrdinalIgnoreCase));
                string? rollMaterial = Value($"wheel{index}_rollmatname");
                TreadPresentationExport? tread = rollMaterial == null ? null : new(
                    Number($"wheel{index}_direction") ?? 1,
                    OptionalBinaryFlag($"wheel{index}_righttread") ?? true,
                    Number($"wheel{index}_rollmatlen") ?? 1,
                    rollMaterial,
                    Value($"wheel{index}_rollmatname_low"),
                    Number($"wheel{index}_tread"),
                    "u");
                if (tread is { RollLength: <= 0 })
                    throw new InvalidDataException(
                        $"Vehicle '{definition}' wheel {index} has non-positive rollmatlen {tread.RollLength}");
                return new WheelExport(
                    index,
                    Value($"wheel{index}_bonename"),
                    constraintName,
                    primitiveName ?? constraint?.Primitive,
                    Number($"wheel{index}_radius") ?? sphere?.Radius,
                    sphere == null ? null : [sphere.Position.X, sphere.Position.Y, sphere.Position.Z],
                    constraint?.Steers ?? false,
                    constraint?.Drives ?? false,
                    constraint?.Brakes ?? false,
                    // All track contacts need control metadata, including contacts
                    // without a render bone or a scrolling tread material.
                    OptionalBinaryFlag($"wheel{index}_righttread"),
                    Number($"wheel{index}_tread"),
                    tread,
                    constraint == null ? null : new SuspensionExport(
                        constraint.SuspensionChassisHeight,
                        constraint.SuspensionTravel,
                        constraint.SuspensionDamping,
                        constraint.SuspensionZToS,
                        constraint.SuspensionSoftness,
                        sphere?.OffsetLowLimit is System.Numerics.Vector3 low
                            ? [low.X, low.Y, low.Z] : null,
                        sphere?.OffsetHighLimit is System.Numerics.Vector3 high
                            ? [high.X, high.Y, high.Z] : null));
            }).ToArray();
        if (Number("treadturntorque") is > 0)
        {
            foreach (var wheel in wheels)
            {
                if (wheel.RightTread == null || !(wheel.ConstraintTread > 0) || !(wheel.Radius > 0))
                    throw new InvalidDataException(
                        $"Tracked vehicle '{definition}' wheel {wheel.Index} requires righttread, positive tread and radius");
            }
        }
        float[] forwardPositions = wheels.Where(wheel => wheel.Position != null)
            .Select(wheel => wheel.Position![0]).Distinct().ToArray();
        float[] lateralPositions = wheels.Where(wheel => wheel.Position != null)
            .Select(wheel => wheel.Position![1]).Distinct().ToArray();
        float? wheelbase = forwardPositions.Length > 1
            ? forwardPositions.Max() - forwardPositions.Min() : null;
        float? wheelTrack = lateralPositions.Length > 1
            ? lateralPositions.Max() - lateralPositions.Min() : null;
        var handling = new
        {
            source = "game_objects.adb",
            coordinateSystem = VehicleManifestContract.NativeDataCoordinateSystem,
            category = gameObject.Type,
            canFly = Flag("canfly"),
            flightControl = FlightControl(),
            flightIsAlwaysServerControlled = OptionalFlag("flightisalwaysservercontrolled"),
            mass = Number("mass"),
            water = VehicleWaterBindings.Resolve(gameObject),
            maxForwardSpeed = Number("maxforward") ?? Number("flightmaxspeed") ?? Number("MaxSpeed"),
            maxReverseSpeed = Number("maxreverse"),
            secondsToFullThrottle = Number("secondstofullthrottle"),
            secondsToFullReverseThrottle = Number("secondstofullreversethrottle"),
            maxBrake = Number("maxbrake"),
            brakeTorque = Number("braketorque"),
            throttleTorque = Number("throttletorque"),
            idleTorque = Number("idletorque"),
            secondsToMaxSteering = Number("secondstomaxwheel"),
            steeringLockStoppedDegrees = Number("maxwheelwhenstopped"),
            steeringLockFullSpeedDegrees = Number("maxwheelatfullspeed"),
            maxSteeringDegrees = steeringAngles.Length > 0 ? steeringAngles.Max(Math.Abs) : (float?)null,
            wheelbase,
            wheelTrack,
            usesWheelSteering = wheels.Any(wheel => wheel.Steers),
            isTreaded = Flag("istreaded"),
            treadTurnTorque = Number("treadturntorque"),
            strafeSpeed = Number("strafespeed"),
            strafeFractionAtMaxSpeed = Number("strafefractionatmaxspeed"),
            secondsToMaxStrafe = Number("secondstomaxstrafe"),
            secondsToZeroStrafe = Number("secondstozerostrafe"),
            flightCanElevate = OptionalFlag("flightcanelevate"),
            flightMinSpeed = Number("flightminspeed") ?? Number("minspeed"),
            flightMaxAcceleration = Number("flightmaxacceleration"),
            flightMinAcceleration = Number("flightminacceleration"),
            flightElevateSpeed = Number("flightelevatespeed"),
            flightDescentSpeed = Number("flightdescentspeed"),
            flightElevationSpeedupFactor = Number("flightelevationspeedupfactor"),
            flightRateOfAscent = Number("flightrateofascent"),
            flightRateOfDescent = Number("flightrateofdescent"),
            flightSecondsToMaxElevate = Number("flightsecondstomaxelevate"),
            flightSecondsToMaxDescent = Number("flightsecondstomaxdescent"),
            flightMinHeight = Number("flightminheight"),
            flightMaxHeight = Number("flightmaxheight"),
            flightLandingHeight = Number("flightlandingheight"),
            flightHeightForSoftLanding = Number("flightheightforsoftlanding"),
            flightLandingSpeed = Number("flightlandingspeed"),
            flightLandingMaxPitchDegrees = Number("flightlandingmaxpitch"),
            flightLandingPitchIncrementDegrees = Number("flightlandingpitchincrement"),
            flightLandingEngineMaxRotationDegrees = Number("flightlandingenginemaxrotation"),
            flightLandingEngineSpeedDegrees = Number("flightlandingenginespeed"),
            flightSkids = flightSkidBones.Length > 0 ? new
            {
                bones = flightSkidBones,
                extension = Number("flightskidpositionmax"),
                leftRotationBone = Value("flightskidleftrotbonename"),
                rightRotationBone = Value("flightskidrightrotbonename"),
                rotationDegrees = Number("flightskidrotationmax"),
            } : null,
            // Keep the old field while consumers migrate to the explicit speed endpoints. Drop pods
            // also use it today, and silently changing its meaning would conflate two runtime systems.
            flightMaxPitchDegrees = Number("flightmaxpitchatmaxspeed"),
            flightMaxPitchAtMinSpeedDegrees = Number("flightmaxpitchatminspeed"),
            flightMaxPitchAtMaxSpeedDegrees = Number("flightmaxpitchatmaxspeed"),
            flightMaxRollDegrees = Number("flightmaxroll"),
            secondsToMaxPitch = Number("flightsecondstomaxpitch"),
            secondsToMaxYaw = Number("flightsecondstomaxyaw"),
            minYawSpeedDegrees = Number("minyawspeed"),
            maxYawSpeedDegrees = Number("maxyawspeed"),
            flightMaxYawAtMaxPitchPenaltyPercentage = Number("flightmaxyawmaxpitchpenaltypercentage"),
            flightMomentumFactors = Vector("flightmomentumfactors"),
            flightMomentumTurnCutoff = Number("flightmomentumturncutoff"),
            flightVerticalFractionAtMaxSpeed = Number("flightverticalfractionatmaxspeed"),
            flightGravityActsAtLowSpeed = OptionalFlag("flightgravityactsatlowspeed"),
            flightGravityCutoffSpeed = Number("flightgravitycutoffspeed"),
            flightHasHelicopterFeel = OptionalFlag("flighthashelicopterfeel"),
            flightThrottleDown = Number("flightthrottledown"),
            flightZeroThrottleForLanding = OptionalFlag("flightzerothrottleforlanding"),
            flightUsesTfdb = OptionalFlag("flightusestfdb"),
            flightTurbulence = Number("flightturbulence"),
            flightTurbulenceFrequency = Number("flightturbulencefreq"),
            dropPod = gameObject.Name.Equals("droppod", StringComparison.OrdinalIgnoreCase) ? new
            {
                locationRadius = Number("droppodlocationradius"),
                orbitalOffset = Vector("droppodorbitaloffset"),
                autoDismountSeconds = Number("flightdroppodautodismounttime"),
                freefallRotationSpeedDegrees = Number("flightfreefallrotationspeed"),
                freefallDropHeight = Number("flightfreefalldropheight"),
                freefallDropAngleDegrees = Number("flightfreefalldropangle"),
                freefallCameraRadius = Number("flightfreefallcameraradius"),
                freefallCameraFinalPitchDegrees = Number("flightfreefallcamerafinalpitchangle"),
                landingHeightAutomatic = Number("flightlandingheight_auto1"),
                landingHeightManual = Number("flightlandingheight_manual"),
                landingSpeedAutomatic = Number("flightlandingspeed_auto1"),
                landingSpeedManual = Number("flightlandingspeed_manual"),
                landingMaxPitchDegrees = Number("flightlandingmaxpitch"),
                landingPitchIncrementDegrees = Number("flightlandingpitchincrement"),
                maxPitchAtMinSpeedDegrees = Number("flightmaxpitchatminspeed"),
                momentumFactors = Vector("flightmomentumfactors"),
                momentumTurnCutoff = Number("flightmomentumturncutoff"),
                countdownToFadeSeconds = Number("flightcountdowntofadeout"),
                countdownToDestructionSeconds = Number("flightcountdowntodestruction"),
            } : null,
            hasAfterburners = Flag("flighthasafterburners"),
            afterburnerForce = Number("flightafterburnerforce"),
            // The native values are retained without normalizing their units. Runtime conversion is a
            // separate semantic decision and must remain visible instead of being baked into extraction.
            afterburnerBuildup = Number("flightafterburnerbuildup"),
            afterburnerMaxCapacitorDuration = Number("flightafterburnermaxcapacitorduration"),
            afterburnerCapacitorRechargeRate = Number("flightafterburnercapacitorrechargerate"),
            afterburnerCapacitorRechargeDelay = Number("flightafterburnercapacitorrechargeratedelay"),
            afterburnerTurbulenceFrequency = Number("flightafterburnerturbulencefrequency"),
            afterburnerTurbulenceStandard = Number("flightafterburnerturbulencestandard"),
            afterburnerTurbulenceStart = Number("flightafterburnerturbulencestart"),
            afterburnerTurbulenceStartDuration = Number("flightafterburnerturbulencestartduration"),
            wheelRadius = wheelRadii.Length > 0 ? wheelRadii.Average() : (double?)null,
            weaponTrackingSpeedsDegrees = trackingSpeeds,
        };
        var flightPresentation = Flag("canfly") ? new
        {
            source = "game_objects.adb",
            coordinateSystem = VehicleManifestContract.NativeDataCoordinateSystem,
            engineUsesRoll = OptionalFlag("flightengineuseroll"),
            engineUsesYawOrientation = OptionalFlag("flightengineuseyorient"),
            leftEngineBone = Value("flightleftenginebonename"),
            rightEngineBone = Value("flightrightenginebonename"),
            tailBone = Value("flighttailbonename"),
            leftWingBone = Value("flightleftwingbonename"),
            rightWingBone = Value("flightrightwingbonename"),
            wingExtensionDegrees = Number("flightwingextendmax"),
            wingExtensionSpeedDegrees = Number("flightwingextendspeed"),
            contrailThrottle = Number("flightcontrailthrottlevalue"),
            dust = Value("flightdusteffectname") is string dustEffect ? new
            {
                effect = dustEffect,
                creationHeight = Number("flightdustcreationheight"),
                creationOffset = Number("flightdustcreationoffset"),
            } : null,
            water = Value("flight_water_effect_tail_bone") is string waterTailBone ? new
            {
                tailBone = waterTailBone,
                sprayPercentage = Number("flight_water_effect_spray_pct"),
                tailRootDistance = Number("flight_water_effect_tail_root_dist"),
            } : null,
            cargo = Vector("flight_cargo_dimensions") is float[] cargoDimensions ? new
            {
                dimensions = cargoDimensions,
                offset = Vector("flight_cargo_offset"),
            } : null,
            skidContactPositions = VectorList("flight_skid_positions", 6, 9),
        } : null;
        var cameraMountPoints = mountBindings.Where(binding => binding.Vehicle == definition)
            .Select(binding => binding with { MountPoint = NativePoint(binding.MountPoint) })
            .Where(binding => Vector($"mountpoint{binding.MountPoint}_viewpoint") != null)
            .Select(binding => new
            {
                seat = binding.Seat,
                weaponSlots = controlledWeapons.Where(w => w.Vehicle == definition && w.Seat == binding.Seat)
                    .Select(w => w.Slot).Order().ToArray(),
                mountPoint = binding.MountPoint,
                isDriver = Flag($"mountpoint{binding.MountPoint}_IsDriver"),
                isGunner = Flag($"mountpoint{binding.MountPoint}_IsGunner"),
                viewpoint = Vector($"mountpoint{binding.MountPoint}_viewpoint"),
                viewpointBone = Value($"mountpoint{binding.MountPoint}_viewpointbone"),
                viewpointType = Value($"mountpoint{binding.MountPoint}_viewpointtype") ?? "viewpoint_normal",
                viewpointOrientation = Vector($"mountpoint{binding.MountPoint}_viewpointorientation"),
                lookLeftDegrees = DriverLookLimit(binding.MountPoint, "lookleftlimit"),
                lookRightDegrees = DriverLookLimit(binding.MountPoint, "lookrightlimit"),
                lookUpDegrees = DriverLookLimit(binding.MountPoint, "lookuplimit"),
                lookDownDegrees = DriverLookLimit(binding.MountPoint, "lookdownlimit"),
            })
            .GroupBy(value => value.seat)
            .Select(group => group.OrderBy(value => value.mountPoint).First())
            .OrderBy(value => value.seat)
            .ToArray();
        var camera = new
        {
            source = "game_objects.adb",
            coordinateSystem = VehicleManifestContract.NativeDataCoordinateSystem,
            minimumDistance = Number("mincamdist"),
            maximumDistance = Number("maxcamdist"),
            cockpitView = OptionalFlag("cockpitview"),
            chaseCameraBone = Value("chasecamerabonename"),
            chaseCameraViewpoint = Vector("chasecameraviewpoint"),
            freeCameraBone = Value("freecamerabonename"),
            freeCameraViewpoint = Vector("freecameraviewpoint"),
            freeCameraInitialRotation = Vector("freecamerainitialrotation"),
            mountPoints = cameraMountPoints,
            driver = driverMount.HasValue ? new
            {
                mountPoint = driverMount.Value,
                isDriver = true,
                isGunner = Flag($"mountpoint{driverMount.Value}_IsGunner"),
                viewpoint = Vector($"mountpoint{driverMount.Value}_viewpoint"),
                viewpointBone = Value($"mountpoint{driverMount.Value}_viewpointbone"),
                viewpointType = Value($"mountpoint{driverMount.Value}_viewpointtype"),
                viewpointOrientation = Vector($"mountpoint{driverMount.Value}_viewpointorientation"),
                lookLeftDegrees = DriverLookLimit(driverMount.Value, "lookleftlimit"),
                lookRightDegrees = DriverLookLimit(driverMount.Value, "lookrightlimit"),
                lookUpDegrees = DriverLookLimit(driverMount.Value, "lookuplimit"),
                lookDownDegrees = DriverLookLimit(driverMount.Value, "lookdownlimit"),
            } : null,
        };
        var mountPoints = properties
            .Select(property => Regex.Match(property.Key, "^mountpoint(\\d+)_sound_(?:mount|dismount)$", RegexOptions.IgnoreCase))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value))
            .Distinct().OrderBy(index => index)
            .Select(index => new
            {
                index,
                mount = SoundValue($"mountpoint{index}_sound_mount"),
                dismount = SoundValue($"mountpoint{index}_sound_dismount"),
            }).ToArray();
        ExportedAsset? bodyAsset = modelResults.GetValueOrDefault(vehicleRenderRecords.GetValueOrDefault(definition) ?? sourceRecord);
        if (bodyAsset is not null && nativeEntries.TryGetValue(definition, out var authoredEntries))
        {
            nativeEntries[definition] = VehicleEntryBindings.BindBody(definition, authoredEntries, bodyAsset.Nodes, bodyAsset.Animations);
            int unbound = nativeEntries[definition].SelectMany(e => e.Variants)
                .SelectMany(v => new[] { v.Mount, v.Dismount }).DistinctBy(c => c.Name)
                .Count(c => c.UnboundTracks?.Length > 0);
            if (unbound > 0) Console.WriteLine($"vehicle entry {definition}: {unbound} clips retain unmatched source tracks in manifest variants[].unboundTracks");
        }
        string? animationAttachBone = bodyAsset is null ? null
            : VehicleManifestContract.ResolveAnimationAttachBone(
                definition,
                Value("animattachbonename"),
                sourceObject is null ? null : GameObjectPropertyReader.Scalar(
                    sourceObject.Properties, $"{sourceRecord} source model", "animattachbonename"),
                bodyAsset.Nodes);
        SeatMountPointExport[] seatMountPoints = mountBindings.Where(binding => binding.Vehicle == definition)
            .Select(binding => new SeatMountPointExport(
                binding.Seat,
                binding.MountPoint,
                Value($"mountpoint{NativePoint(binding.MountPoint)}_name"),
                Number($"mountpoint{NativePoint(binding.MountPoint)}_mountdismountradius"),
                Number($"mountpoint{NativePoint(binding.MountPoint)}_mountdismountsector"),
                OptionalFlag($"mountpoint{NativePoint(binding.MountPoint)}_hideavatar"),
                OptionalFlag($"mountpoint{NativePoint(binding.MountPoint)}_renderifcurrentchildin1stperson"),
                NativePoint(binding.MountPoint),
                nativeEntries.GetValueOrDefault(definition)?.FirstOrDefault(e => e.EntryPoint == binding.MountPoint)?.Location,
                nativeEntries.GetValueOrDefault(definition)?.FirstOrDefault(e => e.EntryPoint == binding.MountPoint)?.MirrorYPositionForDismount ?? false))
            .OrderBy(value => value.MountPoint)
            .ToArray();
        SeatAnimationExport[] seatAnimations = mountBindings.Where(binding => binding.Vehicle == definition)
            .Select(binding =>
            {
                var entry = nativeEntries.GetValueOrDefault(definition)?.FirstOrDefault(e => e.EntryPoint == binding.MountPoint);
                if (entry is null || entry.Variants.Length == 0) return null;
                var variant = entry.Variants.FirstOrDefault(v => v.Occupant == "infantry") ?? entry.Variants[0];
                return new SeatAnimationExport(
                    binding.Seat,
                    binding.MountPoint,
                    entry.Name,
                    variant.Mount.Name,
                    variant.Dismount.Name,
                    SoundValue($"mountpoint{entry.NativeMountPoint}_sound_mount"),
                    SoundValue($"mountpoint{entry.NativeMountPoint}_sound_dismount"),
                    entry.Variants,
                    OptionalFlag($"mountpoint{entry.NativeMountPoint}_hasdelayeddismount"));
            })
            .Where(value => value != null && (value.MountClip != null || value.DismountClip != null))
            .Cast<SeatAnimationExport>().OrderBy(value => value.Seat).ToArray();
        int NativePoint(int entryPoint) => nativeEntries.GetValueOrDefault(definition)
            ?.FirstOrDefault(e => e.EntryPoint == entryPoint)?.NativeMountPoint ?? entryPoint;
        if (seatAnimations.Length == 0 && (sourceRecord == "manned_turret"
            || sourceRecord == "portable_manned_turret"))
        {
            string prefix = sourceRecord;
            string mount = $"{prefix}_gunnera_mount";
            string dismount = $"{prefix}_gunnera_dismount";
            seatAnimations = [new SeatAnimationExport(
                0, 1, "gunnera",
                interactionAnimationNames.FirstOrDefault(name => name.Equals(mount, StringComparison.OrdinalIgnoreCase)),
                interactionAnimationNames.FirstOrDefault(name => name.Equals(dismount, StringComparison.OrdinalIgnoreCase)),
                SoundValue("mountpoint1_sound_mount"),
                SoundValue("mountpoint1_sound_dismount"))];
        }
        var audio = new
        {
            source = "game_objects.adb",
            ambient = new
            {
                file = Value("ambient_sound"),
                minimumDistance = Number("ambient_sound_minrange"),
                maximumDistance = Number("ambient_sound_maxrange"),
                volume = Number("ambient_sound_volume"),
            },
            engine = new
            {
                idle = Value("engineidlename"),
                low = Value("enginelorpmname"),
                high = Value("enginehirpmname"),
                minimumDistance = Number("enginemindistance"),
                maximumDistance = Number("enginemaxdistance"),
                idlePitch = new { minimum = Number("engineidlepitchatmin"), crossover = Number("engineidlepitchatcross"), maximum = Number("engineidlepitchatmax") },
                lowPitch = new { minimum = Number("enginelorpmpitchatmin"), crossover = Number("enginelorpmpitchatcross"), maximum = Number("enginelorpmpitchatmax") },
                highPitch = new { minimum = Number("enginehirpmpitchatmin"), crossover = Number("enginehirpmpitchatcross"), maximum = Number("enginehirpmpitchatmax") },
                idleVolume = new { minimum = Number("engineidlevolumeatmin"), crossover = Number("engineidlevolumeatcross"), maximum = Number("engineidlevolumeatmax") },
                lowVolume = new { minimum = Number("enginelorpmvolumeatmin"), crossover = Number("enginelorpmvolumeatcross"), maximum = Number("enginelorpmvolumeatmax") },
                highVolume = new { minimum = Number("enginehirpmvolumeatmin"), crossover = Number("enginehirpmvolumeatcross"), maximum = Number("enginehirpmvolumeatmax") },
                layers = new[]
                {
                    new
                    {
                        name = "idle", file = Value("engineidlename"),
                        pitch = new { minimum = Number("engineidlepitchatmin"), atCrossover = Number("engineidlepitchatcross"), maximum = Number("engineidlepitchatmax"), crossover = Number("engineidlepitchcrossover") },
                        volume = new { minimum = Number("engineidlevolumeatmin"), atCrossover = Number("engineidlevolumeatcross"), maximum = Number("engineidlevolumeatmax"), crossover = Number("engineidlevolumecrossover") },
                    },
                    new
                    {
                        name = "low", file = Value("enginelorpmname"),
                        pitch = new { minimum = Number("enginelorpmpitchatmin"), atCrossover = Number("enginelorpmpitchatcross"), maximum = Number("enginelorpmpitchatmax"), crossover = Number("enginelorpmpitchcrossover") },
                        volume = new { minimum = Number("enginelorpmvolumeatmin"), atCrossover = Number("enginelorpmvolumeatcross"), maximum = Number("enginelorpmvolumeatmax"), crossover = Number("enginelorpmvolumecrossover") },
                    },
                    new
                    {
                        name = "high", file = Value("enginehirpmname"),
                        pitch = new { minimum = Number("enginehirpmpitchatmin"), atCrossover = Number("enginehirpmpitchatcross"), maximum = Number("enginehirpmpitchatmax"), crossover = Number("enginehirpmpitchcrossover") },
                        volume = new { minimum = Number("enginehirpmvolumeatmin"), atCrossover = Number("enginehirpmvolumeatcross"), maximum = Number("enginehirpmvolumeatmax"), crossover = Number("enginehirpmvolumecrossover") },
                    },
                },
            },
            mountPoints,
            collision = new
            {
                headOnWavePackage = Value("headonsoundname"),
                headOnVariants = Number("numheadonsounds"),
                headOnMinimumVolume = Number("headonsoundminvolume"),
                headOnMaximumVolume = Number("headonsoundmaxvolume"),
                landingWavePackage = Value("bouncesoundname"),
                landingVariants = Number("numbouncesounds"),
                landingMinimumVolume = Number("bouncesoundminvolume"),
                landingMaximumVolume = Number("bouncesoundmaxvolume"),
            },
        };
        VehicleCargoBindings.Manifest cargo = VehicleCargoBindings.Resolve(
            properties, $"Vehicle '{definition}' (merged with '{sourceRecord}')", SoundValue);
        string? explosionEffect = new[]
            {
                $"{definition}_explosion",
                $"{sourceRecord}_explosion",
                vehicleExplosionAliases.GetValueOrDefault(definition),
                "explosion",
            }
            .FirstOrDefault(candidate => candidate != null && effectNames.Contains(candidate));
        var destruction = new
        {
            source = "game_objects.adb + effects.adb + physics.lst",
            destroyedPhysics = Value("destroyedphysics"),
            model = wreckResults.GetValueOrDefault(Value("destroyedphysics") ?? "")?.Uri,
            explosionEffect,
            damageRadius = Number("damageradius") ?? Number("damage_radius"),
            damageAmount = Number("damageamount") ?? Number("damage_amount"),
            criticalStages = Enumerable.Range(1, 3).Select(index => new
            {
                index,
                health = Number($"criticalhealthvalue{index}"),
                effect = effectNames.Contains("vehicle_damage_sparks") ? "vehicle_damage_sparks" : null,
                randomEffectPeriod = Number($"criticalhealthvalue_randomperiod{index}"),
                randomEffectVariance = Number($"criticalhealthvalue_randomvariance{index}"),
            }).Where(stage => stage.health.HasValue).ToArray(),
        };
        return new VehicleExportData(
            handling, flightPresentation!, camera, audio, cargo, destruction,
            animationAttachBone, seatMountPoints, seatAnimations, physicsProfile, deployedPhysicsProfile, wheels);
    }
    // Both state profiles retain the same native primitive contract; state selection
    // belongs to the consumer, never a separate set of hand-authored hull sizes.
    private static object ExportPhysicsProfile(PhysicsListDatabase.Model physicsModel)
    {
        return new
        {
            source = physicsModel.SourceFile,
            model = physicsModel.Name,
            coordinateSystem = VehicleManifestContract.NativeDataCoordinateSystem,
            centerOfMassOffset = physicsModel.CenterOfMassOffset is System.Numerics.Vector3 centerOfMass
                ? new
                {
                    vector = new[] { centerOfMass.X, centerOfMass.Y, centerOfMass.Z },
                    coordinateSystem = VehicleManifestContract.NativeDataCoordinateSystem,
                    sourceCommand = "phys_com_offset",
                    runtimeFidelity = VehicleManifestContract.CenterOfMassRuntimeFidelity,
                }
                : null,
            retainedUnsupportedCommands = physicsModel.UnsupportedCommands
                .GroupBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new { command = group.Key, occurrences = group.Count() })
                .ToArray(),
            primitives = VehicleManifestContract.CollisionPrimitives(
                    physicsModel, objectCollidingOnly: false)
                .Select(shape => new
                {
                    name = shape.Name,
                    kind = shape.Kind.ToString().ToLowerInvariant(),
                    role = VehicleManifestContract.PrimitiveRole(physicsModel, shape),
                    mass = shape.Mass,
                    size = shape.Kind == PhysicsListDatabase.ShapeKind.Box
                        ? new[] { shape.Size.X, shape.Size.Y, shape.Size.Z } : null,
                    radius = shape.Kind is PhysicsListDatabase.ShapeKind.Sphere
                        or PhysicsListDatabase.ShapeKind.Cylinder ? shape.Radius : (float?)null,
                    length = shape.Kind == PhysicsListDatabase.ShapeKind.Cylinder
                        ? shape.Length : (float?)null,
                    position = new[] { shape.Position.X, shape.Position.Y, shape.Position.Z },
                    orientationRadians = new[] {
                        shape.Orientation.X, shape.Orientation.Y, shape.Orientation.Z,
                    },
                    cookie = string.IsNullOrWhiteSpace(shape.Cookie) ? null : shape.Cookie,
                    material = string.IsNullOrWhiteSpace(shape.Material) ? null : shape.Material,
                    aggregate = string.IsNullOrWhiteSpace(shape.Aggregate) ? null : shape.Aggregate,
                    collidesWithObjects = shape.CollidesWithObjects,
                    collidesWithTerrain = shape.CollidesWithTerrain,
                    usesSkeletonTransform = shape.UsesSkeletonTransform,
                    collisionBoneOffset = new[] {
                        shape.CollisionBoneOffset.X,
                        shape.CollisionBoneOffset.Y,
                        shape.CollisionBoneOffset.Z,
                    },
                    centerOfMassOffset = shape.CenterOfMassOffset is System.Numerics.Vector3 primitiveCenterOfMass
                        ? new[] { primitiveCenterOfMass.X, primitiveCenterOfMass.Y, primitiveCenterOfMass.Z }
                        : null,
                    offsetHighLimit = shape.OffsetHighLimit is System.Numerics.Vector3 high
                        ? new[] { high.X, high.Y, high.Z } : null,
                    offsetLowLimit = shape.OffsetLowLimit is System.Numerics.Vector3 low
                        ? new[] { low.X, low.Y, low.Z } : null,
                }).ToArray(),
        };
    }
}

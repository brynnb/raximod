using Raximod.Generation.Assets;

namespace Raximod.Modules;

internal sealed record ExportedAsset(
    string Uri,
    int Triangles,
    int Bones,
    string[] Animations,
    string[] Nodes,
    string[] ArticulationNodes,
    string[] Hardpoints);

internal sealed record WeaponBinding(string Vehicle, int Slot, string Record);

internal sealed record VehicleMountBinding(string Vehicle, int MountPoint, int Seat);

internal sealed record SeatMountPointExport(
    int Seat,
    int MountPoint,
    string? Name,
    float? Radius,
    float? Sector,
    bool? HideAvatar,
    bool? RenderIfCurrentChildInFirstPerson,
    int NativeMountPoint,
    float[]? EntryLocation,
    bool MirrorYPositionForDismount);

internal sealed record SeatAnimationExport(
    int Seat,
    int MountPoint,
    string Name,
    string? MountClip,
    string? DismountClip,
    string? MountSound,
    string? DismountSound,
    VehicleEntryBindings.Variant[]? Variants = null,
    bool? HasDelayedDismount = null);

internal sealed record TurretWeaponBinding(
    string Turret,
    int Slot,
    string Upgrade,
    string Weapon,
    ClientWeaponMetadataResolver.WeaponComponent[] Components);

internal sealed record VehicleExportData(
    object Handling,
    object FlightPresentation,
    object Camera,
    object Audio,
    object Cargo,
    object Destruction,
    string? AnimationAttachBone,
    SeatMountPointExport[] SeatMountPoints,
    SeatAnimationExport[] SeatAnimations,
    object? Physics,
    object? DeployedPhysics,
    WheelExport[] Wheels);

internal sealed record WheelExport(
    int Index,
    string? Node,
    string? Constraint,
    string? Primitive,
    float? Radius,
    float[]? Position,
    bool Steers,
    bool Drives,
    bool Brakes,
    bool? RightTread,
    float? ConstraintTread,
    TreadPresentationExport? Tread,
    SuspensionExport? Suspension);

internal sealed record TreadPresentationExport(
    float Direction,
    bool RightTread,
    float RollLength,
    string Material,
    string? LowDetailMaterial,
    float? ConstraintTread,
    string UvAxis);

internal sealed record SuspensionExport(
    float? ChassisHeight,
    float? Travel,
    float? Damping,
    float? ZToS,
    float? Softness,
    float[]? OffsetLowLimit,
    float[]? OffsetHighLimit);

internal sealed record GlbInspection(string[] Nodes, string[] Animations, int Triangles, int Bones);

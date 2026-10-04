using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Raximod.EngineAssets.Databases
{
    /// <summary>
    /// Reads the text physics databases extracted from startup.pak. A phys_model is a compound
    /// collider whose primitive declarations are followed by attributes up to phys_end_info.
    /// Force domes use the separate phys_tri_info triangle-soup form.
    /// </summary>
    public sealed class PhysicsListDatabase
    {
        public enum ShapeKind { Box, Sphere, Cylinder, CarWheel }

        public sealed class Shape
        {
            public ShapeKind Kind { get; init; }
            public string Name { get; init; } = "";
            public float Mass { get; init; }
            public Vector3 Size { get; init; }
            public Vector3 Position { get; init; }
            public Vector3 Orientation { get; set; }
            public float Radius { get; init; }
            public float Length { get; init; }
            public string Cookie { get; set; } = "";
            public string Material { get; set; } = "";
            public string Aggregate { get; set; } = "";
            public string Primitive { get; init; } = "";
            public Vector3 CollisionBoneOffset { get; set; }
            public Vector3? CenterOfMassOffset { get; set; }
            public Vector3? OffsetHighLimit { get; set; }
            public Vector3? OffsetLowLimit { get; set; }
            public bool CollidesWithObjects { get; set; } = true;
            public bool CollidesWithTerrain { get; set; } = true;
            public bool UsesSkeletonTransform { get; set; }
            public bool Steers { get; set; }
            public bool Drives { get; set; }
            public bool Brakes { get; set; }
            public float? SuspensionChassisHeight { get; set; }
            public float? SuspensionTravel { get; set; }
            public float? SuspensionDamping { get; set; }
            public float? SuspensionZToS { get; set; }
            public float? SuspensionSoftness { get; set; }
            public List<string> BoneWrites { get; } = new();
        }

        public sealed class Model
        {
            public string Name { get; init; } = "";
            public string SourceFile { get; init; } = "";
            public List<Shape> Shapes { get; } = new();
            /// <summary>
            /// The authored <c>phys_com_offset</c> on the model's root primitive. The source
            /// command is primitive-scoped, so every shape also retains its own value.
            /// </summary>
            public Vector3? CenterOfMassOffset { get; set; }
            public List<RetainedUnsupportedCommand> UnsupportedCommands { get; } = new();
        }

        /// <summary>
        /// A command present in the installed physics-list corpus whose transport meaning is
        /// retained but whose runtime semantics are not implemented by this reader.
        /// </summary>
        public sealed record RetainedUnsupportedCommand(
            long Order,
            string SourcePath,
            int LineNumber,
            string Name,
            string[] Arguments,
            string? Model,
            string? Shape,
            string? TriangleMesh);

        public sealed class TriangleMesh
        {
            public string Name { get; init; } = "";
            public int DeclaredTriangles { get; set; }
            public List<Vector3> Vertices { get; } = new();
        }

        private readonly Dictionary<string, Model> _models = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TriangleMesh> _triangleMeshes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _commandCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<RetainedUnsupportedCommand> _unsupportedCommands = new();
        private long _commandOrder;

        // This is the complete command vocabulary in the installed physics*.lst corpus. Commands
        // in ParsedCommandNames have a typed representation above. The rest are deliberately
        // retained with source/order/context rather than disappearing through a permissive switch.
        private static readonly HashSet<string> ParsedCommandNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "phys_aggregate", "phys_bonewrite", "phys_box", "phys_carwheel",
            "phys_carwheel_brakes", "phys_carwheel_drives", "phys_carwheel_steers",
            "phys_carwheel_suspension_chassisheight", "phys_carwheel_suspension_damping",
            "phys_carwheel_suspension_softness", "phys_carwheel_suspension_travel",
            "phys_carwheel_suspension_ztos", "phys_collisionboneoffset", "phys_com_offset",
            "phys_cookie", "phys_cylinder", "phys_end_info", "phys_material", "phys_model",
            "phys_model_collides_with_objects", "phys_model_collides_with_terrain",
            "phys_offset_highlimit", "phys_offset_lowlimit", "phys_orientation", "phys_sphere",
            "phys_tri_end", "phys_tri_info", "phys_tri_num_tris", "phys_tri_vert_info",
            "phys_use_skeleton_xfrm",
        };

        private static readonly HashSet<string> UnsupportedCommandNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "phys_cone", "phys_cone_axis", "phys_cone_damping", "phys_cone_half_angle",
            "phys_cone_stiffness", "phys_default_constraint_orient", "phys_hinge",
            "phys_hinge_axis", "phys_hinge_limits", "phys_hinge_pos_offset",
            "phys_hinge_stiffness", "phys_material_adhesion", "phys_material_dimensions",
            "phys_material_friction", "phys_material_interaction", "phys_material_primaryslip",
            "phys_material_restitution", "phys_material_secondaryslip", "phys_material_softness",
            "phys_material_tensilestrength", "phys_rpro", "phys_rpro_angularstrength",
            "phys_rpro_linearstrength", "phys_shares_geometry",
        };

        private static readonly HashSet<string> AllKnownCommandNames =
            new(ParsedCommandNames, StringComparer.OrdinalIgnoreCase);

        static PhysicsListDatabase()
        {
            AllKnownCommandNames.UnionWith(UnsupportedCommandNames);
        }

        public IReadOnlyDictionary<string, Model> Models => _models;
        public IReadOnlyDictionary<string, TriangleMesh> TriangleMeshes => _triangleMeshes;
        public IReadOnlyDictionary<string, int> CommandCounts => _commandCounts;
        public IReadOnlyList<RetainedUnsupportedCommand> RetainedUnsupportedCommands => _unsupportedCommands;
        public static IReadOnlySet<string> KnownCommandNames => AllKnownCommandNames;
        public static IReadOnlySet<string> RetainedUnsupportedCommandNames => UnsupportedCommandNames;
        public Model? FindModel(string? name) => name != null && _models.TryGetValue(name, out Model? value) ? value : null;
        public TriangleMesh? FindTriangleMesh(string? name) =>
            name != null && _triangleMeshes.TryGetValue(name, out TriangleMesh? value) ? value : null;

        public static PhysicsListDatabase ParseDirectory(string directory)
        {
            var result = new PhysicsListDatabase();
            if (!Directory.Exists(directory)) return result;
            foreach (string path in Directory.EnumerateFiles(directory, "physics*.lst")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                result.ParseFile(path);
            return result;
        }

        public void ParseFile(string path)
        {
            Model? model = null;
            Shape? shape = null;
            TriangleMesh? triangleMesh = null;
            int lineNumber = 0;
            foreach (string raw in File.ReadLines(path))
            {
                lineNumber++;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                string command = fields[0].ToLowerInvariant();
                if (!AllKnownCommandNames.Contains(command))
                    throw new InvalidDataException(
                        $"{path}:{lineNumber}: unknown physics-list command '{fields[0]}'");
                _commandCounts[command] = _commandCounts.TryGetValue(command, out int count)
                    ? count + 1 : 1;
                _commandOrder++;
                switch (command)
                {
                    case "phys_model" when fields.Length >= 2:
                        model = new Model { Name = fields[1], SourceFile = Path.GetFileName(path) };
                        _models[model.Name] = model;
                        shape = null;
                        triangleMesh = null;
                        break;
                    case "phys_box" when model != null && fields.Length >= 9:
                        shape = new Shape {
                            Kind = ShapeKind.Box, Name = fields[1], Mass = F(fields[2]),
                            Size = V(fields, 3), Position = V(fields, 6),
                        };
                        model.Shapes.Add(shape);
                        break;
                    case "phys_sphere" when model != null && fields.Length >= 7:
                        shape = new Shape {
                            Kind = ShapeKind.Sphere, Name = fields[1], Mass = F(fields[2]),
                            Radius = F(fields[3]), Position = V(fields, 4),
                        };
                        model.Shapes.Add(shape);
                        break;
                    case "phys_cylinder" when model != null && fields.Length >= 8:
                        shape = new Shape {
                            Kind = ShapeKind.Cylinder, Name = fields[1], Mass = F(fields[2]),
                            Radius = F(fields[3]), Length = F(fields[4]), Position = V(fields, 5),
                        };
                        model.Shapes.Add(shape);
                        break;
                    case "phys_carwheel" when model != null && fields.Length >= 4:
                        shape = new Shape {
                            Kind = ShapeKind.CarWheel, Name = fields[1], Aggregate = fields[2], Primitive = fields[3],
                        };
                        model.Shapes.Add(shape);
                        break;
                    case "phys_orientation" when shape != null && fields.Length >= 4:
                        // Retail physics dispatcher 0x61ebd0, opcode 4: angle * 2 * pi / 16384.
                        // These are fixed-turn units, not milliradians; wrong units close cargo guide walls.
                        shape.Orientation = V(fields, 1) * (2f * MathF.PI / 16384f);
                        break;
                    case "phys_cookie" when shape != null && fields.Length >= 2:
                        shape.Cookie = fields[1];
                        break;
                    case "phys_material" when shape != null && fields.Length >= 2:
                        shape.Material = fields[1];
                        break;
                    case "phys_aggregate" when shape != null && fields.Length >= 2:
                        shape.Aggregate = fields[1];
                        break;
                    case "phys_collisionboneoffset" when shape != null && fields.Length >= 4:
                        shape.CollisionBoneOffset = V(fields, 1);
                        break;
                    case "phys_com_offset" when model != null && shape != null && fields.Length >= 4:
                        shape.CenterOfMassOffset = V(fields, 1);
                        if (shape.Name.Equals("root", StringComparison.OrdinalIgnoreCase))
                            model.CenterOfMassOffset = shape.CenterOfMassOffset;
                        break;
                    case "phys_offset_highlimit" when shape != null && fields.Length >= 4:
                        shape.OffsetHighLimit = V(fields, 1);
                        break;
                    case "phys_offset_lowlimit" when shape != null && fields.Length >= 4:
                        shape.OffsetLowLimit = V(fields, 1);
                        break;
                    case "phys_use_skeleton_xfrm" when shape != null && fields.Length >= 2:
                        shape.UsesSkeletonTransform = Flag(fields[1]);
                        break;
                    case "phys_bonewrite" when shape != null && fields.Length >= 2:
                        shape.BoneWrites.Add(fields[1]);
                        break;
                    case "phys_carwheel_steers" when shape?.Kind == ShapeKind.CarWheel && fields.Length >= 2:
                        shape.Steers = Flag(fields[1]);
                        break;
                    case "phys_carwheel_drives" when shape?.Kind == ShapeKind.CarWheel && fields.Length >= 2:
                        shape.Drives = Flag(fields[1]);
                        break;
                    case "phys_carwheel_brakes" when shape?.Kind == ShapeKind.CarWheel && fields.Length >= 2:
                        shape.Brakes = Flag(fields[1]);
                        break;
                    case "phys_carwheel_suspension_chassisheight" when shape?.Kind == ShapeKind.CarWheel && fields.Length >= 2:
                        shape.SuspensionChassisHeight = F(fields[1]);
                        break;
                    case "phys_carwheel_suspension_travel" when shape?.Kind == ShapeKind.CarWheel && fields.Length >= 2:
                        shape.SuspensionTravel = F(fields[1]);
                        break;
                    case "phys_carwheel_suspension_damping" when shape?.Kind == ShapeKind.CarWheel && fields.Length >= 2:
                        shape.SuspensionDamping = F(fields[1]);
                        break;
                    case "phys_carwheel_suspension_ztos" when shape?.Kind == ShapeKind.CarWheel && fields.Length >= 2:
                        shape.SuspensionZToS = F(fields[1]);
                        break;
                    case "phys_carwheel_suspension_softness" when shape?.Kind == ShapeKind.CarWheel && fields.Length >= 2:
                        shape.SuspensionSoftness = F(fields[1]);
                        break;
                    // Despite the historical `phys_model_` prefix, every occurrence in the
                    // retail physics.lst is inside a primitive block and is consumed as a
                    // property of that current shape. Promoting it to Model disables an entire
                    // hover vehicle merely because its wheel-contact spheres do not collide
                    // with objects.
                    case "phys_model_collides_with_objects" when fields.Length >= 2:
                        if (shape == null)
                            throw new InvalidDataException(
                                $"{path}:{lineNumber}: phys_model_collides_with_objects is outside a primitive block");
                        shape.CollidesWithObjects = Flag(fields[1]);
                        break;
                    case "phys_model_collides_with_terrain" when fields.Length >= 2:
                        if (shape == null)
                            throw new InvalidDataException(
                                $"{path}:{lineNumber}: phys_model_collides_with_terrain is outside a primitive block");
                        shape.CollidesWithTerrain = Flag(fields[1]);
                        break;
                    case "phys_end_info":
                        shape = null;
                        break;
                    case "phys_tri_info" when fields.Length >= 2:
                        triangleMesh = new TriangleMesh { Name = fields[1] };
                        _triangleMeshes[triangleMesh.Name] = triangleMesh;
                        model = null;
                        shape = null;
                        break;
                    case "phys_tri_num_tris" when triangleMesh != null && fields.Length >= 2:
                        triangleMesh.DeclaredTriangles = I(fields[1]);
                        break;
                    case "phys_tri_vert_info" when triangleMesh != null && fields.Length >= 10:
                        triangleMesh.Vertices.Add(V(fields, 1));
                        triangleMesh.Vertices.Add(V(fields, 4));
                        triangleMesh.Vertices.Add(V(fields, 7));
                        break;
                    case "phys_tri_end":
                        triangleMesh = null;
                        break;
                    // The five force-dome placeholder models in physics.lst deliberately omit a
                    // primitive declaration but still author these four primitive-like commands.
                    // Preserve that anomalous source form without pretending it is a typed shape.
                    case "phys_orientation" when model != null && shape == null && fields.Length >= 4:
                    case "phys_cookie" when model != null && shape == null && fields.Length >= 2:
                    case "phys_material" when model != null && shape == null && fields.Length >= 2:
                        RetainUnsupported(path, lineNumber, command, fields, model, shape, triangleMesh);
                        break;
                    case "phys_com_offset" when model != null && shape == null && fields.Length >= 4:
                        model.CenterOfMassOffset = V(fields, 1);
                        RetainUnsupported(path, lineNumber, command, fields, model, shape, triangleMesh);
                        break;
                    case string value when UnsupportedCommandNames.Contains(value):
                        RetainUnsupported(path, lineNumber, command, fields, model, shape, triangleMesh);
                        break;
                    default:
                        throw new InvalidDataException(
                            $"{path}:{lineNumber}: physics-list command '{fields[0]}' has invalid "
                            + $"arguments or appears outside its required context: {line}");
                }
            }
        }

        private void RetainUnsupported(
            string path,
            int lineNumber,
            string command,
            string[] fields,
            Model? model,
            Shape? shape,
            TriangleMesh? triangleMesh)
        {
            var retained = new RetainedUnsupportedCommand(
                _commandOrder,
                Path.GetFullPath(path),
                lineNumber,
                command,
                fields.Skip(1).ToArray(),
                model?.Name,
                shape?.Name,
                triangleMesh?.Name);
            _unsupportedCommands.Add(retained);
            model?.UnsupportedCommands.Add(retained);
        }

        private static float F(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        private static int I(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        private static Vector3 V(string[] fields, int offset) =>
            new(F(fields[offset]), F(fields[offset + 1]), F(fields[offset + 2]));
        private static bool Flag(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }
}

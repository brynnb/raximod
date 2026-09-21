using Raximod.EngineAssets.Meshes;
using System.Numerics;

namespace Raximod.Generation.Records
{
    /// <summary>
    /// Dumps per-mesh vertex/material stats for one named record, searched across every mesh library
    /// (<c>uber</c>, each patch, <c>expansion1</c>) -- a quick way to see a model's real bounding box and
    /// material list without opening it in the viewport.
    ///
    /// Shared implementation for Raximod's record-inspection command.
    /// </summary>
    public static class ListRecordsTool
    {
        /// <param name="PlanetSideDir">The read-only reference client folder.</param>
        /// <param name="RecordName">The mesh system's record name to look up, e.g. <c>warpgate</c>.</param>
        public sealed record Options(string PlanetSideDir, string RecordName);

        private static readonly string[] Libraries =
            { "uber", "patch1", "patch2", "patch3", "patch4", "patch5", "expansion1" };

        /// <summary>Reports one line per mesh system found, then one line per section within it.</summary>
        public static int Run(Options options, IProgress<string> log, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(options.PlanetSideDir))
            {
                throw new ArgumentException("a PlanetSide reference client folder is required", nameof(options));
            }
            if (string.IsNullOrWhiteSpace(options.RecordName))
            {
                throw new ArgumentException("a record name is required", nameof(options));
            }

            string ps = options.PlanetSideDir;
            string target = options.RecordName;
            int found = 0;

            foreach (string libName in Libraries)
            {
                ct.ThrowIfCancellationRequested();

                string path = PathFor(ps, libName);
                if (!File.Exists(path))
                {
                    continue;
                }

                UberModel m;
                try
                {
                    m = UberModel.Load(File.ReadAllBytes(path));
                }
                catch (Exception e)
                {
                    log.Report($"skip {libName}: {e.Message}");
                    continue;
                }

                UberModel.MeshSystem? sys = m.FetchMeshSystem(target);
                if (sys == null)
                {
                    continue;
                }

                found++;
                log.Report($"=== {libName}/{target} : meshes={sys.Meshes.Count}");
                foreach (var skeleton in sys.Skeletons)
                {
                    log.Report($"  skeleton {skeleton.Name} bones={skeleton.Bones.Count}: "
                        + string.Join(", ", skeleton.Bones.Select(bone => bone.Name)));
                }
                if (sys.PortalMeshItems.Count > 0)
                {
                    log.Report($"  portal mesh items={sys.PortalMeshItems.Count}");
                    foreach (UberModel.PortalMeshItem item in sys.PortalMeshItems)
                    {
                        Vector3 t = item.Transform.Translation;
                        log.Report($"    {item.InstanceName} asset={item.AssetName} " +
                            $"position=<{t.X:F4}, {t.Y:F4}, {t.Z:F4}> flags=0x{item.Flags:X8}");
                    }
                }
                if (sys.Collisions != null)
                {
                    log.Report($"  collision '{sys.Collisions.Name}' parts={sys.Collisions.Parts.Count}");
                    foreach (UberModel.CollisionPart part in sys.Collisions.Parts)
                    {
                        log.Report($"    {part.Name} type={(uint)part.Type} bbox={part.BbMin}..{part.BbMax} " +
                            $"vertices={part.Vertices.Length} indices={part.Indices.Length}");
                    }
                }
                int mi = 0;
                foreach (UberModel.Mesh mesh in sys.Meshes)
                {
                    log.Report($"  mesh{mi} name={mesh.Name} model={mesh.ModelName} lod={mesh.Lod} " +
                        $"sections={mesh.Sections.Count}");
                    foreach (UberModel.MeshSection s in mesh.Sections)
                    {
                        float mnX = float.MaxValue, mxX = float.MinValue;
                        float mnY = float.MaxValue, mxY = float.MinValue;
                        float mnZ = float.MaxValue, mxZ = float.MinValue;
                        foreach (UberModel.UberVert v in s.Verts)
                        {
                            mnX = MathF.Min(mnX, v.Position.X);
                            mxX = MathF.Max(mxX, v.Position.X);
                            mnY = MathF.Min(mnY, v.Position.Y);
                            mxY = MathF.Max(mxY, v.Position.Y);
                            mnZ = MathF.Min(mnZ, v.Position.Z);
                            mxZ = MathF.Max(mxZ, v.Position.Z);
                        }
                        log.Report(
                            $"    section lod={s.Lod,-4} mat={s.MaterialName,-28} verts={s.Verts.Length,5} " +
                            $"x[{mnX,7:F1},{mxX,7:F1}] y[{mnY,7:F1},{mxY,7:F1}] z[{mnZ,6:F1},{mxZ,6:F1}]");
                    }
                    mi++;
                }
            }

            if (found == 0)
            {
                log.Report($"'{target}' was not found in any library.");
            }
            return found;
        }

        private static string PathFor(string planetside, string libName) =>
            libName == "uber" ? Path.Combine(planetside, "uber.ubr")
            : libName == "expansion1" ? Path.Combine(planetside, "expansion1", "expansion1.ubr")
            : Path.Combine(planetside, libName, libName + ".ubr");
    }
}

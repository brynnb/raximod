using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace Raximod.EngineAssets.Meshes
{
    /// <summary>
    /// Faithful port of the engine-derived reference implementation's <c>uber_model.{h,cpp}</c> — the binary-confirmed
    /// per-CMeshSection geometry assembly for the .ubr container.
    ///
    /// Where <see cref="UberMesh"/> handles the 0x2C header + 0x50 record "directory" layer, this
    /// decodes the 6 body sections (Vec3Data / Vec2Data / LookupData / MeshData / U32Data / ModelData)
    /// and walks the ModelData/pool command stream one CMeshSystem at a time. The crucial difference
    /// from the naive corner-triple builder: a section's vertices take their position from
    /// <c>Vec3Data[LookupData[i]]</c> and its triangles from a dedicated u16 index buffer sliced out
    /// of MeshData — NOT from raw consecutive corner indices.
    ///
    /// Provenance (per the C++ header): outer format + pool model + vertex-assembly dispatch are all
    /// binary-confirmed (A4.verify.body.assembly, 2026-05-28). The gated subsystems (Portal / Skeleton
    /// / Collision / AAB) are ported verbatim so their stream bytes are consumed in the exact binary
    /// read order, keeping the ModelData cursor aligned for the following mesh sections.
    /// </summary>
    public sealed class UberModel
    {
        // ----- vertex types (confirmed against the dispatch switch @ 0x009d9440) -------------------
        public enum VertexType : uint
        {
            None = 0,
            Lit = 1,
            Unlit = 2,
            Thin = 3,
            Raw = 4,
            FatDeform = 5,
            Fat = 6,
            Deform = 14,
        }

        // ----- CMeshSystem flag bits (gating bits confirmed against FUN_009d8ad0) ------------------
        private const uint FlagPortalSystem = 0x00001;
        private const uint FlagVec3Array = 0x00004;
        private const uint FlagSkeletons = 0x00008;
        private const uint FlagUserData = 0x00020;
        private const uint FlagCollision = 0x00080;
        private const uint FlagNoAab = 0x02000;
        private const uint FlagHasEf = 0x08000;

        // SAAB struct sizes (Pack=1).
        private const int SizeAabFace = 10;   // SAABFace
        private const int SizeAabNode = 0x28; // SAABBNode

        // ===========================================================================================
        // Stream reader — LE cursor over a byte slice. Mirrors UberStreamReader.
        // ===========================================================================================
        public sealed class StreamReader
        {
            private readonly byte[] _buf;
            private readonly int _start;
            private readonly int _size;
            private int _off;

            public StreamReader(byte[] buf, int start, int size)
            {
                _buf = buf;
                _start = start;
                _size = size;
                _off = 0;
            }

            public int Remaining => _off < _size ? _size - _off : 0;
            public int Consumed => _off;
            public int Size => _size;
            public bool ReadU8(out byte v)
            {
                v = 0;
                if (Remaining < 1) return false;
                v = _buf[_start + _off];
                _off += 1;
                return true;
            }

            public bool ReadU16(out ushort v)
            {
                v = 0;
                if (Remaining < 2) return false;
                v = (ushort)(_buf[_start + _off] | (_buf[_start + _off + 1] << 8));
                _off += 2;
                return true;
            }

            public bool ReadU32(out uint v)
            {
                v = 0;
                if (Remaining < 4) return false;
                int p = _start + _off;
                v = (uint)(_buf[p] | (_buf[p + 1] << 8) | (_buf[p + 2] << 16) | (_buf[p + 3] << 24));
                _off += 4;
                return true;
            }

            public bool ReadF32(out float v)
            {
                v = 0;
                if (!ReadU32(out uint bits)) return false;
                v = BitConverter.UInt32BitsToSingle(bits);
                return true;
            }

            public bool ReadBytes(int n, out byte[] outBytes)
            {
                outBytes = Array.Empty<byte>();
                if (n < 0 || Remaining < n) return false;
                outBytes = new byte[n];
                Array.Copy(_buf, _start + _off, outBytes, 0, n);
                _off += n;
                return true;
            }

            /// <summary>Pascal string: [u8 len][len bytes][1 pad byte]. Empty == failure (binary semantics).</summary>
            public string ReadPascalStr()
            {
                if (!ReadU8(out byte len)) return string.Empty;
                if (Remaining < len + 1) return string.Empty;
                string s = System.Text.Encoding.Latin1.GetString(_buf, _start + _off, len);
                _off += len;
                _off += 1; // trailing pad byte
                return s;
            }

            public bool ReadVec3(out Vector3 v)
            {
                v = default;
                if (!ReadF32(out float x) || !ReadF32(out float y) || !ReadF32(out float z)) return false;
                v = new Vector3(x, y, z);
                return true;
            }

            public bool ReadVec2(out Vector2 v)
            {
                v = default;
                if (!ReadF32(out float x) || !ReadF32(out float y)) return false;
                v = new Vector2(x, y);
                return true;
            }

            public bool SkipF32(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    if (!ReadF32(out _)) return false;
                }
                return true;
            }
        }

        // ===========================================================================================
        // Built vertex + section/mesh/system records.
        // ===========================================================================================
        public struct UberVert
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector2 Uv0;
            public Vector2 Uv1;
            public uint Diffuse;
            // Skinning (Deform / FatDeform only): 2-bone blend. boneA/boneB index the CMeshSystem
            // skeleton; weight is the explicit blend factor for the stored byte (the other bone's
            // weight is implied). Irrelevant to bind-pose rendering — positions are already posed.
            public byte BoneA;
            public byte BoneB;
            public float Weight;
        }

        public sealed class MeshSection
        {
            public string MaterialName = "";
            public uint Flags;
            public uint Id;
            public uint MeshId;
            public uint Lod;
            public VertexType Type = VertexType.None;
            public Vector3 BbMin, BbMax;
            public uint IndexCount;
            public ushort[] Indices = Array.Empty<ushort>();
            public uint VertexCount;
            public UberVert[] Verts = Array.Empty<UberVert>();
            public bool HasNormal, HasUv0, HasUv1, HasColor, HasSkin;

            // Topology bit — binary-derived from the shipped .ubr files (selftest flag/IndexCount
            // correlation): section flags are exactly 0x1 (triangle LIST) or 0x2 (triangle STRIP),
            // mutually exclusive. Every section whose IndexCount is NOT a multiple of 3 (provably a
            // strip) has flags==0x2; the flat list quads (water/terrain sheets) have flags==0x1.
            // (The uber_model.h C++ note guessed bit 0x4 for tristrip — that is unverified and does
            // NOT match the data; the real strip bit is 0x2.)
            public bool IsTriStrip => (Flags & 0x2) != 0;

            public bool Load(StreamReader r, UberModel data)
            {
                MaterialName = r.ReadPascalStr();
                if (MaterialName.Length == 0) return false;

                if (!r.ReadU32(out Flags)) return false;
                if (!r.ReadU32(out Id)) return false;
                if (!r.ReadU32(out MeshId)) return false;
                if (!r.ReadU32(out Lod)) return false;

                if (!r.ReadU32(out uint vtRaw)) return false;
                Type = (VertexType)vtRaw;

                if (!r.ReadVec3(out BbMin)) return false;
                if (!r.ReadVec3(out BbMax)) return false;
                if (!r.ReadU32(out IndexCount)) return false;

                // Index buffer lives in MeshData (not ModelData). ReadMeshData advances a dedicated
                // MeshData cursor; the ModelData stream reader is unaffected.
                if (!data.ReadMeshData((int)IndexCount * sizeof(ushort), out byte[] idxBytes))
                {
                    return false;
                }
                Indices = new ushort[IndexCount];
                for (int i = 0; i < IndexCount; i++)
                {
                    Indices[i] = (ushort)(idxBytes[i * 2] | (idxBytes[i * 2 + 1] << 8));
                }

                // Only known-supported types reserve cursors + build verts. Unsupported types still
                // read vertex_count (stream field) but refuse the load (cursor reservation would
                // corrupt the following section).
                switch (Type)
                {
                    case VertexType.Lit:
                    case VertexType.Unlit:
                    case VertexType.Thin:
                    case VertexType.Raw:
                    case VertexType.Fat:
                    case VertexType.Deform:
                    case VertexType.FatDeform:
                        if (!r.ReadU32(out VertexCount)) return false;
                        break;
                    case VertexType.None:
                    default:
                        data.RefuseReason = "vtype:unknown(" + vtRaw + ")";
                        return false;
                }

                uint lookupOff = data.GetLookupCursor(VertexCount);
                uint npv = U32PerVert(Type);
                uint u32Off = npv != 0 ? data.GetU32Cursor(VertexCount * npv) : 0;

                Verts = new UberVert[VertexCount];
                switch (Type)
                {
                    case VertexType.Lit:
                    {
                        HasColor = HasUv0 = true;
                        uint o = u32Off;
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                            Verts[i].Diffuse = data.U32At(o++);
                            Verts[i].Uv0 = data.Vec2(data.U32At(o++));
                        }
                        break;
                    }
                    case VertexType.Unlit:
                    {
                        HasNormal = HasUv0 = true;
                        uint o = u32Off;
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                            Verts[i].Normal = DecodeNormal(data.U32At(o++));
                            Verts[i].Uv0 = data.Vec2(data.U32At(o++));
                        }
                        break;
                    }
                    case VertexType.Thin:
                    {
                        HasUv0 = true;
                        uint o = u32Off;
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                            Verts[i].Uv0 = data.Vec2(data.U32At(o++));
                        }
                        break;
                    }
                    case VertexType.Raw:
                    {
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                        }
                        break;
                    }
                    case VertexType.Fat:
                    {
                        // 3 u32: packed normal + uv0(index) + uv1(index). (asm: assembler 0x009dab80)
                        HasNormal = HasUv0 = HasUv1 = true;
                        uint o = u32Off;
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                            Verts[i].Normal = DecodeNormal(data.U32At(o++));
                            Verts[i].Uv0 = data.Vec2(data.U32At(o++));
                            Verts[i].Uv1 = data.Vec2(data.U32At(o++));
                        }
                        break;
                    }
                    case VertexType.Deform:
                    {
                        // 3 u32: packed normal + uv0(index) + skin. (asm: assembler 0x009dd320)
                        HasNormal = HasUv0 = HasSkin = true;
                        uint o = u32Off;
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                            Verts[i].Normal = DecodeNormal(data.U32At(o++));
                            Verts[i].Uv0 = data.Vec2(data.U32At(o++));
                            DecodeSkin(data.U32At(o++), ref Verts[i]);
                        }
                        break;
                    }
                    case VertexType.FatDeform:
                    {
                        // 4 u32: packed normal + uv0(index) + uv1(index) + skin. (asm: 0x009dc990)
                        HasNormal = HasUv0 = HasUv1 = HasSkin = true;
                        uint o = u32Off;
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                            Verts[i].Normal = DecodeNormal(data.U32At(o++));
                            Verts[i].Uv0 = data.Vec2(data.U32At(o++));
                            Verts[i].Uv1 = data.Vec2(data.U32At(o++));
                            DecodeSkin(data.U32At(o++), ref Verts[i]);
                        }
                        break;
                    }
                }

                return true;
            }
        }

        public sealed class Mesh
        {
            public string Name = "";
            public string ModelName = "";
            public uint Lod;
            public Vector3 BbMin, BbMax;
            public uint MeshSectionCount2;
            public uint Id;
            public uint MeshSectionCount;
            public readonly List<MeshSection> Sections = new();

            public bool LoadHeader(StreamReader r)
            {
                Name = r.ReadPascalStr();
                if (Name.Length == 0) return false;
                ModelName = r.ReadPascalStr();
                if (ModelName.Length == 0) return false;
                if (!r.ReadU32(out Lod)) return false;
                if (!r.ReadVec3(out BbMin)) return false;
                if (!r.ReadVec3(out BbMax)) return false;
                if (!r.ReadU32(out MeshSectionCount2)) return false;
                if (!r.ReadU32(out Id)) return false;
                if (!r.ReadU32(out MeshSectionCount)) return false;
                return true;
            }

            public bool LoadMeshSections(StreamReader r, UberModel data)
            {
                Sections.Clear();
                for (uint i = 0; i < MeshSectionCount; i++)
                {
                    var s = new MeshSection();
                    if (!s.Load(r, data)) return false;
                    Sections.Add(s);
                }
                return true;
            }
        }

        // ----- skeleton (retained for skinning/animation; gated by MESHSYS_FLAG_SKELETONS) ---------
        public sealed class Bone
        {
            public string Name = "";
            public int Parent = -1;
            public Vector3 Position;                          // bind local position (Z-up, parent-relative)
            public Quaternion Rotation = Quaternion.Identity; // bind local rotation
        }

        public sealed class Skeleton
        {
            public string Name = "";
            public readonly List<Bone> Bones = new();
            public readonly List<SkeletonExternalInstance> ExternalInstances = new();
        }

        public sealed record SkeletonExternalInstance(string Name, uint BoneIndex);

        /// <summary>
        /// One native SAABFace. The packed key is demonstrably section-id in the high word and
        /// mesh-id in the low word; the three remaining words index that section's native vertices.
        /// </summary>
        public sealed record NativeAabFace(
            uint PackedSectionKey,
            ushort VertexA,
            ushort VertexB,
            ushort VertexC)
        {
            public ushort MeshId => (ushort)(PackedSectionKey & 0xffff);
            public ushort SectionId => (ushort)(PackedSectionKey >> 16);
        }

        /// <summary>
        /// Lossless 0x28-byte SAABBNode decode. The two leading vectors and four trailing words
        /// remain deliberately unnamed until their traversal semantics are confirmed from
        /// original-client call sites.
        /// </summary>
        public sealed record NativeAabNode(
            Vector3 Vector0,
            Vector3 Vector1,
            uint Word0,
            uint Word1,
            uint Word2,
            uint Word3);

        public sealed class NativeAabData
        {
            public readonly List<NativeAabFace> Faces = new();
            public NativeAabNode? Root;
            public readonly List<NativeAabNode> Nodes = new();
            public uint[] Map = Array.Empty<uint>();
        }

        public sealed record MeshSystemRecordCoverage(
            int ModelDataConsumed,
            int ModelDataTotal,
            int MeshDataConsumed,
            int MeshDataTotal,
            int LookupEntriesConsumed,
            int LookupEntriesTotal,
            int ConnectionEntriesConsumed,
            int ConnectionEntriesTotal)
        {
            public bool Complete => ModelDataConsumed == ModelDataTotal
                && MeshDataConsumed == MeshDataTotal
                && LookupEntriesConsumed == LookupEntriesTotal
                && ConnectionEntriesConsumed == ConnectionEntriesTotal;
        }

        public enum CollisionPartType : uint
        {
            Sphere = 0,
            Box = 1,
            Cylinder = 2,
            Mesh = 3,
            None = 4,
            OrientedBox = 6,
        }

        public sealed class CollisionPart
        {
            public string Name = "";
            public CollisionPartType Type;
            public Vector3 BbMin, BbMax;
            public Vector3 Center, Min, Max;
            public float Radius, Length;
            public Matrix4x4 Transform = Matrix4x4.Identity;
            public Vector3[] Vertices = Array.Empty<Vector3>();
            public Vector3[] Normals = Array.Empty<Vector3>();
            public ushort[] Indices = Array.Empty<ushort>();
        }

        public sealed class CollisionSet
        {
            public string Name = "";
            public readonly List<CollisionPart> Parts = new();
        }

        /// <summary>
        /// A render-model instance embedded in an indoor portal system. Large facilities use these
        /// records for separately-authored pieces (stairs, terminals, props, and similar children),
        /// so discarding them produces visibly incomplete buildings even when the parent mesh loads.
        /// </summary>
        public sealed class PortalMeshItem
        {
            public uint A, Index, Id, Flags, RegionA, RegionB;
            public string InstanceName = "";
            public string AssetName = "";
            public uint[] MeshIndices = Array.Empty<uint>();
            public Matrix4x4 Transform = Matrix4x4.Identity;
        }

        public sealed class MeshSystem
        {
            public string Name = "";
            public uint Flags;
            public Vector3 BbMin, BbMax;
            public uint A, B;   // raw bits; A/B are IEEE-754 floats = the system's (X,Y) world placement
            public readonly List<Mesh> Meshes = new();
            public readonly List<Skeleton> Skeletons = new();
            public readonly List<Vector3[]> AuxiliaryVectorArrays = new();
            public readonly List<PortalMeshItem> PortalMeshItems = new();
            public byte[] UserData = Array.Empty<byte>();
            public PortalVisibilityData? PortalVisibility;
            public NativeAabData? NativeAab;
            public CollisionSet? Collisions;
            public MeshSystemRecordCoverage? Coverage;
            /// <summary>True when the native mesh-system header declares an embedded collision block.</summary>
            public bool DeclaresCollision => (Flags & FlagCollision) != 0;
            /// <summary>Why a declared collision block could not be decoded, without rejecting render geometry.</summary>
            public string? CollisionDecodeFailure;

            /// <summary>World placement offset (X,Y) of this system's local geometry. Map tiles use this
            /// to grid out across the continent (multiples of 256); objects have it = 0.</summary>
            public Vector3 WorldOffset =>
                new Vector3(BitConverter.UInt32BitsToSingle(A), BitConverter.UInt32BitsToSingle(B), 0f);

            public bool Load(StreamReader r, UberModel data)
            {
                if (!r.ReadU32(out Flags)) return false;
                if (!r.ReadVec3(out BbMin)) return false;
                if (!r.ReadVec3(out BbMax)) return false;
                if (!r.ReadU32(out A)) return false;
                if (!r.ReadU32(out B)) return false;

                // Mesh array — phase 1: read every mesh header.
                if (!r.ReadU32(out uint meshCount)) return false;
                Meshes.Clear();
                for (uint i = 0; i < meshCount; i++)
                {
                    var m = new Mesh();
                    if (!m.LoadHeader(r)) return false;
                    Meshes.Add(m);
                }

                // Optional sub-systems — exact binary read order. These consume stream bytes that
                // sit BETWEEN the mesh headers and mesh sections; skipping them desynchronizes every
                // later field. Retain their decoded or opaque contents even when no exporter uses them.
                if ((Flags & FlagPortalSystem) != 0)
                {
                    PortalVisibility = PortalSystemReader.Read(r, out string? portalFailure);
                    if (PortalVisibility == null)
                    {
                        data.RefuseReason = "portal:" + (portalFailure ?? "unknown");
                        return false;
                    }
                    PortalMeshItems.AddRange(PortalVisibility.MeshItems);
                }
                if ((Flags & FlagVec3Array) != 0)
                {
                    if (!r.ReadU32(out uint n)) return false;
                    for (uint i = 0; i < n; i++)
                    {
                        if (!r.ReadU32(out uint m)) return false;
                        var values = new Vector3[checked((int)m)];
                        for (int j = 0; j < values.Length; j++)
                        {
                            if (!r.ReadVec3(out values[j])) return false;
                        }
                        AuxiliaryVectorArrays.Add(values);
                    }
                }
                if ((Flags & FlagUserData) != 0)
                {
                    if (!r.ReadU32(out uint n)) return false;
                    if (!r.ReadBytes((int)n, out UserData)) return false;
                }
                if ((Flags & FlagSkeletons) != 0)
                {
                    if (!r.ReadU32(out uint n)) return false;
                    for (uint i = 0; i < n; i++)
                    {
                        Skeleton? sk = SkeletonReader.Read(r);
                        if (sk == null) return false;
                        Skeletons.Add(sk);
                    }
                }

                // Phase 2: each mesh reads its sections (after ALL headers + optional subs).
                for (int i = 0; i < Meshes.Count; i++)
                {
                    if (!Meshes[i].LoadMeshSections(r, data)) { data.RefuseReason ??= "sections"; return false; }
                }

                // AAB: always read unless NO_AAB. Collision: gated by COLLISION.
                if ((Flags & FlagNoAab) == 0)
                {
                    NativeAab = Aab.Read(r, data);
                    if (NativeAab == null) { data.RefuseReason ??= "aab"; return false; }
                }
                if ((Flags & FlagCollision) != 0)
                {
                    // Collision is the last step in this system's ModelData slice. Retain every native
                    // primitive and fully consume COLL_Mesh triangle soup for exporters and previews.
                    Collisions = Collision.Read(r, data);
                    if (Collisions == null)
                    {
                        CollisionDecodeFailure = data.RefuseReason ?? "collision:decode-failed";
                        data.RefuseReason = null; // not a refusal; the system's geometry is intact
                    }
                }
                return true;
            }
        }

        // ===========================================================================================
        // Gated subsystems — ported in exact binary read order and retained losslessly. Keeping these
        // records prevents a successful cursor advance from being mistaken for semantic extraction.
        // ===========================================================================================
        private static class Aab
        {
            public static NativeAabData? Read(StreamReader r, UberModel data)
            {
                var result = new NativeAabData();
                if (!r.ReadU32(out uint faceCount)) return null;
                if (faceCount != 0)
                {
                    if (!data.ReadMeshData(checked((int)faceCount * SizeAabFace), out byte[] faces)) return null;
                    for (int offset = 0; offset < faces.Length; offset += SizeAabFace)
                    {
                        result.Faces.Add(new NativeAabFace(
                            BinaryPrimitives.ReadUInt32LittleEndian(faces.AsSpan(offset, 4)),
                            BinaryPrimitives.ReadUInt16LittleEndian(faces.AsSpan(offset + 4, 2)),
                            BinaryPrimitives.ReadUInt16LittleEndian(faces.AsSpan(offset + 6, 2)),
                            BinaryPrimitives.ReadUInt16LittleEndian(faces.AsSpan(offset + 8, 2))));
                    }
                }
                if (!r.ReadU32(out uint nodeCount)) return null;
                if (!data.ReadMeshData(SizeAabNode, out byte[] root)) return null; // root node, always read
                result.Root = ReadNode(root);
                if (nodeCount != 0)
                {
                    if (!data.ReadMeshData(checked((int)nodeCount * SizeAabNode), out byte[] nodes)) return null;
                    for (int offset = 0; offset < nodes.Length; offset += SizeAabNode)
                        result.Nodes.Add(ReadNode(nodes.AsSpan(offset, SizeAabNode)));
                }
                if (!r.ReadU32(out uint mapCount)) return null;
                if (mapCount != 0)
                {
                    if (!data.ReadMeshData(checked((int)mapCount * 4), out byte[] map)) return null;
                    result.Map = new uint[mapCount];
                    for (int i = 0; i < result.Map.Length; i++)
                        result.Map[i] = BinaryPrimitives.ReadUInt32LittleEndian(map.AsSpan(i * 4, 4));
                }
                return result;
            }

            private static NativeAabNode ReadNode(ReadOnlySpan<byte> bytes) => new(
                new Vector3(F32(bytes, 0), F32(bytes, 4), F32(bytes, 8)),
                new Vector3(F32(bytes, 12), F32(bytes, 16), F32(bytes, 20)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(24, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(28, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(32, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(36, 4)));

            private static float F32(ReadOnlySpan<byte> bytes, int offset) =>
                BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4)));
        }

        private static class SkeletonReader
        {
            public static Skeleton? Read(StreamReader r)
            {
                string name = r.ReadPascalStr();
                if (name.Length == 0) return null;
                var sk = new Skeleton { Name = name };
                if (!r.ReadU32(out uint nbones)) return null;
                for (uint i = 0; i < nbones; i++)
                {
                    Bone? b = ReadBone(r);
                    if (b == null) return null;
                    sk.Bones.Add(b);
                }
                if (!r.ReadU32(out uint n)) return null;
                if (n != 0)
                {
                    if (!r.ReadU32(out uint nx)) return null;
                    for (uint i = 0; i < nx; i++)
                    {
                        string instanceName = r.ReadPascalStr();
                        if (instanceName.Length == 0) return null;
                        if (!r.ReadU32(out uint boneIndex)) return null;
                        sk.ExternalInstances.Add(new SkeletonExternalInstance(instanceName, boneIndex));
                    }
                }
                return sk;
            }

            private static Bone? ReadBone(StreamReader r)
            {
                string name = r.ReadPascalStr();
                if (name.Length == 0) return null;
                if (!r.ReadU32(out uint parent)) return null;
                // CBoneTransform: flags(u32) + position(vec3) + quat(W,X,Y,Z) + rotation matrix(9 f32).
                if (!r.ReadU32(out _)) return null;                          // flags
                if (!r.ReadVec3(out Vector3 pos)) return null;              // bind position
                // Bind quaternion is stored X,Y,Z,W (verified geometrically: each bone's composed
                // bind-world position lands on the centroid of the vertices skinned to it only with
                // this order — for both soldiers and rigid props. The earlier "W,X,Y,Z" reading was
                // wrong; it left bind rotations subtly off, which exploded vertex-skinned models on
                // animation while leaving small-rotation rigid props looking acceptable).
                if (!r.ReadF32(out float qx)) return null;                 // quat X
                if (!r.ReadF32(out float qy)) return null;                 // quat Y
                if (!r.ReadF32(out float qz)) return null;                 // quat Z
                if (!r.ReadF32(out float qw)) return null;                 // quat W
                if (!r.SkipF32(9)) return null;                            // 3x3 rotation matrix (derived; unused)
                return new Bone
                {
                    Name = name,
                    Parent = unchecked((int)parent),
                    Position = pos,
                    Rotation = new Quaternion(qx, qy, qz, qw), // System.Numerics is (X,Y,Z,W)
                };
            }
        }

        private static class Collision
        {
            public static CollisionSet? Read(StreamReader r, UberModel data)
            {
                if (!r.ReadU32(out uint n)) { data.RefuseReason = "collision:count"; return null; }
                if (n == 0) return new CollisionSet();
                string name = r.ReadPascalStr();
                if (name.Length == 0) { data.RefuseReason = "collision:name"; return null; }
                if (!r.ReadU32(out uint nparts)) { data.RefuseReason = "collision:nparts"; return null; }
                var result = new CollisionSet { Name = name };
                for (uint i = 0; i < nparts; i++)
                {
                    CollisionPart? part = ReadPart(r, out uint t);
                    if (part == null)
                    {
                        data.RefuseReason = $"collision:part{i}/{nparts}:type={t}";
                        return null;
                    }
                    result.Parts.Add(part);
                }
                return result;
            }

            private static CollisionPart? ReadPart(StreamReader r, out uint t)
            {
                t = 0xFFFFFFFF;
                string name = r.ReadPascalStr();
                if (name.Length == 0) return null;
                if (!r.ReadU32(out t)) return null;
                if (!r.ReadVec3(out Vector3 bbMin) || !r.ReadVec3(out Vector3 bbMax)) return null;
                var part = new CollisionPart { Name = name, Type = (CollisionPartType)t, BbMin = bbMin, BbMax = bbMax };
                switch ((int)t)
                {
                    case 0:
                        if (!r.ReadVec3(out part.Center) || !r.ReadF32(out part.Radius)) return null;
                        return part;
                    case 1:
                        if (!r.ReadVec3(out part.Min) || !r.ReadVec3(out part.Max)) return null;
                        return part;
                    case 2:
                        if (!r.ReadVec3(out part.Center) || !r.ReadF32(out part.Length) || !r.ReadF32(out part.Radius)) return null;
                        return part;
                    case 6:
                        float[] matrix = new float[16];
                        for (int i = 0; i < matrix.Length; i++) if (!r.ReadF32(out matrix[i])) return null;
                        part.Transform = new Matrix4x4(
                            matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5], matrix[6], matrix[7],
                            matrix[8], matrix[9], matrix[10], matrix[11], matrix[12], matrix[13], matrix[14], matrix[15]);
                        if (!r.ReadVec3(out part.Min) || !r.ReadVec3(out part.Max)
                            || !r.ReadF32(out part.Length) || !r.ReadF32(out part.Radius)) return null;
                        return part;
                    case 3:
                        if (!r.ReadU32(out uint triangleCount) || triangleCount > 1_000_000) return null;
                        // COLL_Mesh repeats its own bounds, then stores unindexed triangle-soup
                        // vertices followed by one face normal for every triangle.
                        if (!r.ReadVec3(out _) || !r.ReadVec3(out _)) return null;
                        part.Vertices = new Vector3[checked(triangleCount * 3)];
                        for (int i = 0; i < part.Vertices.Length; i++)
                        {
                            if (!r.ReadVec3(out part.Vertices[i])) return null;
                        }
                        part.Normals = new Vector3[triangleCount];
                        for (int i = 0; i < part.Normals.Length; i++)
                        {
                            if (!r.ReadVec3(out part.Normals[i])) return null;
                        }
                        if (part.Vertices.Length > ushort.MaxValue) return null;
                        part.Indices = new ushort[part.Vertices.Length];
                        for (int i = 0; i < part.Indices.Length; i++) part.Indices[i] = (ushort)i;
                        return part;
                    case 4:
                        return part;
                    default:
                        return null;
                }
            }
        }

        internal static class PortalSystemReader
        {
            private const uint MaximumCollectionCount = 1_000_000;
            private const int MaximumAabvDepth = 4096;

            // Mirrors CPortalSystem::Load, but retains every decoded field instead of consuming it.
            // Names beginning with Raw intentionally avoid assigning semantics not confirmed by the
            // binary reader. A failed field returns no partial graph, preserving the old fail-closed
            // decoder behavior; generated visibility manifests fail open at render time.
            public static PortalVisibilityData? Read(StreamReader r, out string? failure)
            {
                failure = "presence-marker";
                if (!r.ReadU32(out uint presence)) return null;
                var result = new PortalVisibilityData { PresenceMarker = presence };
                if (presence == 0)
                {
                    failure = null;
                    return result;
                }

                failure = "bounds";
                if (!r.ReadVec3(out result.BoundsMinimum) || !r.ReadVec3(out result.BoundsMaximum)) return null;
                for (int i = 0; i < 5; i++)
                {
                    failure = $"mesh-descriptor-{i}";
                    PortalVisibilityMeshDescriptor? descriptor = ReadMeshDescriptor(r);
                    if (descriptor == null) return null;
                    result.MeshDescriptors.Add(descriptor);
                }

                failure = "exterior-portal-count";
                if (!ReadCount(r, out uint count)) return null;
                for (uint i = 0; i < count; i++)
                {
                    failure = $"exterior-portal-{i}";
                    PortalVisibilityPortal? portal = ReadPortal(r);
                    if (portal == null) return null;
                    result.ExteriorPortals.Add(portal);
                }

                failure = "region-count";
                if (!ReadCount(r, out uint regionCount)) return null;
                for (uint i = 0; i < regionCount; i++)
                {
                    failure = $"region-{i}";
                    PortalVisibilityRegion? region = ReadRegion(r);
                    if (region == null) return null;
                    result.Regions.Add(region);
                }

                failure = "name-count";
                if (!ReadCount(r, out count)) return null;
                for (uint i = 0; i < count; i++)
                {
                    failure = $"name-{i}";
                    string value = r.ReadPascalStr();
                    if (value.Length == 0) return null;
                    result.Names.Add(value);
                }

                failure = "mesh-item-count";
                if (!ReadCount(r, out count)) return null;
                if (count != 0)
                {
                    for (uint i = 0; i < count; i++)
                    {
                        failure = $"mesh-item-{i}";
                        PortalMeshItem? item = ReadMeshItem(r);
                        if (item == null) return null;
                        result.MeshItems.Add(item);
                    }

                    failure = "mesh-item-aabv-marker";
                    if (!r.ReadU32(out result.MeshItemAabvMarker)) return null;
                    if (result.MeshItemAabvMarker != 0)
                    {
                        failure = "mesh-item-aabv";
                        result.MeshItemAabv = ReadAabv(r);
                        if (result.MeshItemAabv == null) return null;
                    }
                    for (uint i = 0; i < regionCount; i++)
                    {
                        failure = $"region-spatial-binding-{i}";
                        if (!r.ReadU32(out uint portalId) || !r.ReadU32(out uint hasAabv)) return null;
                        var binding = new PortalVisibilityRegionSpatialBinding
                        {
                            PortalId = portalId,
                            HasAabvMarker = hasAabv,
                        };
                        if (hasAabv != 0)
                        {
                            binding.Aabv = ReadAabv(r);
                            if (binding.Aabv == null) return null;
                        }
                        result.RegionSpatialBindings.Add(binding);
                    }
                }

                failure = "raw-id-count";
                if (!ReadCount(r, out count) || !TryArrayLength(count, out int rawIdCount)) return null;
                result.RawIds = new uint[rawIdCount];
                for (int i = 0; i < result.RawIds.Length; i++)
                {
                    failure = $"raw-id-{i}";
                    if (!r.ReadU32(out result.RawIds[i])) return null;
                }
                if (count != 0)
                {
                    failure = "raw-id-aabv-marker";
                    if (!r.ReadU32(out result.RawIdAabvMarker)) return null;
                    if (result.RawIdAabvMarker != 0)
                    {
                        failure = "raw-id-aabv";
                        result.RawIdAabv = ReadAabv(r);
                        if (result.RawIdAabv == null) return null;
                    }
                }

                failure = "trailing-substructure-count";
                if (!ReadCount(r, out count)) return null;
                for (uint i = 0; i < count; i++)
                {
                    failure = $"trailing-substructure-{i}";
                    PortalVisibilityRegionSubstructure? value = ReadRegionSubstructure(r);
                    if (value == null) return null;
                    result.TrailingSubstructures.Add(value);
                }
                failure = null;
                return result;
            }

            private static PortalVisibilityMeshDescriptor? ReadMeshDescriptor(StreamReader r)
            {
                string name = r.ReadPascalStr();
                if (name.Length == 0 || !r.ReadF32(out float rawA) || !r.ReadF32(out float rawB)) return null;
                return new PortalVisibilityMeshDescriptor { Name = name, RawA = rawA, RawB = rawB };
            }

            private static PortalVisibilityPlane? ReadPlane(StreamReader r)
            {
                if (!r.ReadVec3(out Vector3 normal) || !r.ReadF32(out float distance) ||
                    !r.ReadF32(out float epsilon)) return null;
                return new PortalVisibilityPlane { Normal = normal, Distance = distance, Epsilon = epsilon };
            }

            private static PortalVisibilityPortal? ReadPortal(StreamReader r)
            {
                if (!r.ReadU32(out uint rawFlags) || !r.ReadU32(out uint regionA) ||
                    !r.ReadU32(out uint regionB)) return null;
                Vector3[]? points = ReadVectors(r, 4);
                if (points == null) return null;
                PortalVisibilityPlane? plane = ReadPlane(r);
                if (plane == null || !r.ReadU32(out uint meshItemId)) return null;
                return new PortalVisibilityPortal
                {
                    RawFlags = rawFlags,
                    RegionA = regionA,
                    RegionB = regionB,
                    Points = points,
                    Plane = plane,
                    MeshItemId = meshItemId,
                };
            }

            private static PortalVisibilityConvexHull? ReadConvexHull(StreamReader r)
            {
                if (!r.ReadU32(out uint rawA) || !r.ReadVec3(out Vector3 minimum) ||
                    !r.ReadVec3(out Vector3 maximum) || !ReadCount(r, out uint planeCount)) return null;
                var hull = new PortalVisibilityConvexHull
                {
                    RawA = rawA,
                    BoundsMinimum = minimum,
                    BoundsMaximum = maximum,
                };
                for (uint i = 0; i < planeCount; i++)
                {
                    PortalVisibilityPlane? plane = ReadPlane(r);
                    if (plane == null) return null;
                    hull.Planes.Add(plane);
                }
                if (!ReadCount(r, out uint vertexCount)) return null;
                Vector3[]? vertices = ReadVectors(r, vertexCount);
                if (vertices == null) return null;
                hull.Vertices = vertices;
                return hull;
            }

            private static PortalVisibilityRegionSubstructure? ReadRegionSubstructure(StreamReader r)
            {
                if (!r.ReadF32(out float rawA)) return null;
                Vector3[]? points = ReadVectors(r, 4);
                return points == null ? null : new PortalVisibilityRegionSubstructure { RawA = rawA, Points = points };
            }

            private static PortalVisibilityRegion? ReadRegion(StreamReader r)
            {
                string name = r.ReadPascalStr();
                if (name.Length == 0 || !r.ReadU32(out uint id) || !r.ReadVec3(out Vector3 minimum) ||
                    !r.ReadVec3(out Vector3 maximum) || !ReadCount(r, out uint portalCount)) return null;
                var region = new PortalVisibilityRegion
                {
                    Name = name,
                    Id = id,
                    BoundsMinimum = minimum,
                    BoundsMaximum = maximum,
                };
                for (uint i = 0; i < portalCount; i++)
                {
                    PortalVisibilityPortal? portal = ReadPortal(r);
                    if (portal == null) return null;
                    region.Portals.Add(portal);
                }
                if (!ReadCount(r, out uint hullCount)) return null;
                for (uint i = 0; i < hullCount; i++)
                {
                    PortalVisibilityConvexHull? hull = ReadConvexHull(r);
                    if (hull == null) return null;
                    region.ConvexHulls.Add(hull);
                }
                if (!ReadCount(r, out uint rawLinkCount) || !TryArrayLength(rawLinkCount, out int linkLength))
                    return null;
                region.RawLinks = new uint[linkLength];
                for (int i = 0; i < region.RawLinks.Length; i++)
                {
                    if (!r.ReadU32(out region.RawLinks[i])) return null;
                }
                if (!ReadCount(r, out uint substructureCount)) return null;
                for (uint i = 0; i < substructureCount; i++)
                {
                    PortalVisibilityRegionSubstructure? value = ReadRegionSubstructure(r);
                    if (value == null) return null;
                    region.Substructures.Add(value);
                }
                return region;
            }

            private static PortalMeshItem? ReadMeshItem(StreamReader r)
            {
                uint[] fields = new uint[6];
                for (int i = 0; i < fields.Length; i++)
                {
                    if (!r.ReadU32(out fields[i])) return null; // a, index, id, flags, region_a, region_b
                }
                string instanceName = r.ReadPascalStr();
                if (instanceName.Length == 0) return null;
                string assetName = r.ReadPascalStr();
                if (assetName.Length == 0) return null;
                if (!r.ReadU32(out uint nm)) return null;
                uint[] meshIndices = new uint[nm];
                for (uint i = 0; i < nm; i++)
                {
                    if (!r.ReadU32(out meshIndices[i])) return null;
                }
                float[] matrix = new float[16];
                for (int i = 0; i < matrix.Length; i++)
                {
                    if (!r.ReadF32(out matrix[i])) return null;
                }
                return new PortalMeshItem
                {
                    A = fields[0],
                    Index = fields[1],
                    Id = fields[2],
                    Flags = fields[3],
                    RegionA = fields[4],
                    RegionB = fields[5],
                    InstanceName = instanceName,
                    AssetName = assetName,
                    MeshIndices = meshIndices,
                    Transform = new Matrix4x4(
                        matrix[0], matrix[1], matrix[2], matrix[3],
                        matrix[4], matrix[5], matrix[6], matrix[7],
                        matrix[8], matrix[9], matrix[10], matrix[11],
                        matrix[12], matrix[13], matrix[14], matrix[15]),
                };
            }

            private static PortalVisibilityAabv? ReadAabv(StreamReader r)
            {
                if (!ReadCount(r, out uint declared)) return null;
                var result = new PortalVisibilityAabv { DeclaredNodeCount = declared };
                if (declared == 0) return result;
                uint decoded = 0;
                result.Root = ReadAabvNode(r, ref decoded, 0);
                if (result.Root == null) return null;
                result.DecodedNodeCount = decoded;
                return result;
            }

            private static PortalVisibilityAabvNode? ReadAabvNode(StreamReader r, ref uint decoded, int depth)
            {
                if (depth > MaximumAabvDepth || decoded >= MaximumCollectionCount ||
                    !r.ReadU8(out byte flags) || !r.ReadVec3(out Vector3 minimum) ||
                    !r.ReadVec3(out Vector3 maximum)) return null;
                decoded++;
                var node = new PortalVisibilityAabvNode
                {
                    Flags = flags,
                    BoundsMinimum = minimum,
                    BoundsMaximum = maximum,
                };
                if ((flags & 0x01) != 0)
                {
                    if (!r.ReadU16(out ushort leafCount)) return null;
                    node.LeafIndices = new ushort[leafCount];
                    for (int i = 0; i < node.LeafIndices.Length; i++)
                    {
                        if (!r.ReadU16(out node.LeafIndices[i])) return null;
                    }
                }
                if ((flags & 0x10) != 0)
                {
                    node.Child10 = ReadAabvNode(r, ref decoded, depth + 1);
                    if (node.Child10 == null) return null;
                }
                if ((flags & 0x20) != 0)
                {
                    node.Child20 = ReadAabvNode(r, ref decoded, depth + 1);
                    if (node.Child20 == null) return null;
                }
                return node;
            }

            private static bool ReadCount(StreamReader r, out uint count)
            {
                count = 0;
                return r.ReadU32(out count) && count <= MaximumCollectionCount;
            }

            private static Vector3[]? ReadVectors(StreamReader r, uint count)
            {
                if (!TryArrayLength(count, out int length)) return null;
                var values = new Vector3[length];
                for (int i = 0; i < values.Length; i++)
                {
                    if (!r.ReadVec3(out values[i])) return null;
                }
                return values;
            }

            private static bool TryArrayLength(uint count, out int length)
            {
                length = 0;
                if (count > MaximumCollectionCount || count > int.MaxValue) return false;
                length = (int)count;
                return true;
            }
        }

        // ===========================================================================================
        // UberModel container.
        // ===========================================================================================
        private bool _opened;
        private int _sourceByteLength;
        private uint _declaredByteLength;
        private UberHeader _header = new();
        private readonly List<UberRecord> _records = new();

        private Vector3[] _vec3 = Array.Empty<Vector3>();
        private Vector2[] _vec2 = Array.Empty<Vector2>();
        private uint[] _lookup = Array.Empty<uint>();
        private uint[] _u32 = Array.Empty<uint>();
        private byte[] _meshData = Array.Empty<byte>();
        private byte[] _modelData = Array.Empty<byte>();

        // Per-FetchMeshSystem cursors.
        private int _meshDataCur;
        private int _meshDataEnd;
        private uint _lookupOffset;
        private uint _u32Offset;

        public bool Opened => _opened;
        public int SourceByteLength => _sourceByteLength;
        public uint DeclaredByteLength => _declaredByteLength;
        public bool SourceCompletelyPartitioned => _opened && _declaredByteLength == _sourceByteLength;
        public UberHeader Header => _header;
        public IReadOnlyList<UberRecord> Records => _records;

        /// <summary>Diagnostic: reason the most recent <see cref="FetchMeshSystemAt"/> refused (or null).</summary>
        public string? RefuseReason { get; internal set; }

        public static UberModel Load(byte[] data)
        {
            var m = new UberModel();
            if (!m.Open(data))
            {
                throw new InvalidOperationException("UberModel.Open failed (bad header / size invariant)");
            }
            return m;
        }

        public bool Open(byte[] data)
        {
            _opened = false;
            _sourceByteLength = data.Length;
            _declaredByteLength = 0;
            _records.Clear();
            _meshDataCur = _meshDataEnd = 0;
            _lookupOffset = _u32Offset = 0;

            // Header + size invariant (reuses the UberMesh parse via a throwaway instance).
            UberMesh um;
            try
            {
                um = UberMesh.Load(data);
            }
            catch
            {
                return false;
            }

            _header = new UberHeader
            {
                Version1 = um.Header.Version1,
                Version2 = um.Header.Version2,
                RecordCount = um.Header.RecordCount,
                VertexCount = um.Header.VertexCount,
                UvCount = um.Header.UvCount,
                IndexCount = um.Header.IndexCount,
                ConnCount = um.Header.ConnCount,
                BlobBytes = um.Header.BlobBytes,
                PoolBytes = um.Header.PoolBytes,
                SectionOffset = um.Header.SectionOffset,
            };

            if (_header.Version1 != 1 || _header.Version2 != 1) return false;

            uint computedSection =
                  (uint)UberMesh.HeaderSize
                + _header.RecordCount * (uint)UberMesh.RecordSize
                + _header.VertexCount * 0x0C
                + _header.UvCount * 0x08
                + _header.IndexCount * 0x04
                + _header.BlobBytes;
            if (_header.SectionOffset != computedSection) return false;

            uint computedSize = _header.SectionOffset + _header.ConnCount * 0x04 + _header.PoolBytes;
            if (computedSize != (uint)data.Length) return false;
            _declaredByteLength = computedSize;

            foreach (UberRecord rec in um.Records)
            {
                _records.Add(rec);
            }

            // Body sections — content-formats §2.2 layout:
            //   verts[vertex_count]  × 0x0C   -> Vec3Data
            //   uvs[uv_count]        × 0x08   -> Vec2Data
            //   indices[index_count] × 0x04   -> LookupData (binary's Lookup is a 32-bit corner stream)
            //   blob[blob_bytes]              -> MeshData
            //   conn[conn_count]     × 0x04   -> U32Data
            //   pool[pool_bytes]              -> ModelData (per-mesh-system command stream)
            int p = UberMesh.HeaderSize + (int)_header.RecordCount * UberMesh.RecordSize;

            _vec3 = new Vector3[_header.VertexCount];
            for (int i = 0; i < _header.VertexCount; i++)
            {
                _vec3[i] = new Vector3(ReadF(data, p), ReadF(data, p + 4), ReadF(data, p + 8));
                p += 0x0C;
            }
            _vec2 = new Vector2[_header.UvCount];
            for (int i = 0; i < _header.UvCount; i++)
            {
                _vec2[i] = new Vector2(ReadF(data, p), ReadF(data, p + 4));
                p += 0x08;
            }
            _lookup = new uint[_header.IndexCount];
            for (int i = 0; i < _header.IndexCount; i++)
            {
                _lookup[i] = ReadU(data, p);
                p += 4;
            }
            _meshData = new byte[_header.BlobBytes];
            if (_header.BlobBytes > 0)
            {
                Array.Copy(data, p, _meshData, 0, (int)_header.BlobBytes);
                p += (int)_header.BlobBytes;
            }
            _u32 = new uint[_header.ConnCount];
            for (int i = 0; i < _header.ConnCount; i++)
            {
                _u32[i] = ReadU(data, p);
                p += 4;
            }
            _modelData = new byte[_header.PoolBytes];
            if (_header.PoolBytes > 0)
            {
                Array.Copy(data, p, _modelData, 0, (int)_header.PoolBytes);
            }

            _opened = true;
            return true;
        }

        /// <summary>Locate a CMeshSystem by record name and decode it. Returns null on miss/refuse.</summary>
        public MeshSystem? FetchMeshSystem(string name)
        {
            if (!_opened || name == null) return null;

            // Record names are matched case-insensitively: placements sometimes differ in case from the
            // record they name (e.g. the map_object "VT_building_vs" vs the record "Vt_building_vs"), and
            // the engine resolves them regardless of case.
            int found = -1;
            for (int i = 0; i < _records.Count; i++)
            {
                if (string.Equals(_records[i].Name, name, StringComparison.OrdinalIgnoreCase)) { found = i; break; }
            }
            if (found < 0) return null;
            return FetchMeshSystemAt(found);
        }

        /// <summary>Decode the CMeshSystem at record index. Returns null on refuse/overrun.</summary>
        public MeshSystem? FetchMeshSystemAt(int index)
        {
            if (!_opened || index < 0 || index >= _records.Count) return null;
            RefuseReason = null;

            UberRecord e0 = _records[index];
            uint nextModel;
            if (index + 1 < _records.Count)
            {
                nextModel = _records[index + 1].FirstVertex; // record +0x4C = ModelData/pool offset
            }
            else
            {
                nextModel = (uint)_modelData.Length;
            }

            uint modelOff = e0.FirstVertex;
            uint modelEnd = nextModel;
            if (modelOff > _modelData.Length || modelEnd > _modelData.Length || modelOff > modelEnd)
            {
                return null;
            }

            // Per-section cursors for this fetch.
            _meshDataCur = (int)e0.FirstBlobByte;
            _meshDataEnd = index + 1 < _records.Count ? (int)_records[index + 1].FirstBlobByte : (int)_header.BlobBytes;
            _lookupOffset = e0.FirstIndex;
            _u32Offset = e0.FirstConn;

            var r = new StreamReader(_modelData, (int)modelOff, (int)(modelEnd - modelOff));
            var sys = new MeshSystem { Name = e0.Name };
            if (sys.Load(r, this))
            {
                uint nextLookup = index + 1 < _records.Count
                    ? _records[index + 1].FirstIndex : (uint)_lookup.Length;
                uint nextConnection = index + 1 < _records.Count
                    ? _records[index + 1].FirstConn : (uint)_u32.Length;
                sys.Coverage = new MeshSystemRecordCoverage(
                    r.Consumed,
                    r.Size,
                    _meshDataCur - (int)e0.FirstBlobByte,
                    _meshDataEnd - (int)e0.FirstBlobByte,
                    checked((int)(_lookupOffset - e0.FirstIndex)),
                    checked((int)(nextLookup - e0.FirstIndex)),
                    checked((int)(_u32Offset - e0.FirstConn)),
                    checked((int)(nextConnection - e0.FirstConn)));
                return sys;
            }
            RefuseReason ??= "decode-failed(flags=0x" + sys.Flags.ToString("X") + ")";
            return null;
        }

        // ----- cursor + reservation helpers (binary: data.ReadMeshData / GetLookup / GetU32 / GetVec3) -----
        internal bool ReadMeshData(int nbytes, out byte[] outBytes)
        {
            outBytes = Array.Empty<byte>();
            if (nbytes < 0) return false;
            if (_meshDataCur + nbytes > _meshDataEnd) return false;
            if (_meshDataCur + nbytes > _meshData.Length) return false;
            outBytes = new byte[nbytes];
            Array.Copy(_meshData, _meshDataCur, outBytes, 0, nbytes);
            _meshDataCur += nbytes;
            return true;
        }

        internal uint GetLookupCursor(uint vertexCount)
        {
            uint ret = _lookupOffset;
            _lookupOffset += vertexCount;
            return ret;
        }

        internal uint GetU32Cursor(uint nu32)
        {
            if (nu32 == 0) return 0;
            uint ret = _u32Offset;
            _u32Offset += nu32;
            return ret;
        }

        internal uint U32At(uint i) => i < _u32.Length ? _u32[i] : 0u;

        internal Vector2 Vec2(uint i) => i < _vec2.Length ? _vec2[i] : default;

        /// <summary>position = Vec3Data[ LookupData[lookupIndex] ]; clamps to zero on overflow.</summary>
        internal Vector3 GetVec3Via(uint lookupIndex)
        {
            if (lookupIndex >= _lookup.Length) return default;
            uint vi = _lookup[lookupIndex];
            if (vi >= _vec3.Length) return default;
            return _vec3[vi];
        }

        // ----- static helpers ----------------------------------------------------------------------
        private static uint U32PerVert(VertexType t) => t switch
        {
            VertexType.Thin => 1,
            VertexType.Lit => 2,
            VertexType.Unlit => 2,
            VertexType.Fat => 3,
            VertexType.Deform => 3,
            VertexType.FatDeform => 4,
            _ => 0, // Raw + unsupported
        };

        // Skin u32 (asm: skin decoder 0x009dcba0): boneA = low byte, boneB = next byte, weight =
        // signed int16 in the high half scaled by 1/16384 (2^-14). 2-bone blend. Bind-pose rendering
        // ignores this; stored for completeness / future skinning.
        private static void DecodeSkin(uint packed, ref UberVert v)
        {
            // The stored u16 weight is the blend factor of BYTE1's bone, not byte0's — resolved
            // geometrically from the shipped soldier meshes: treating byte1 as the weighted
            // (dominant) bone halves each vertex's distance to that bone (avg 0.42 -> 0.19 over
            // trmmed's 2555 skinned verts, 73% closer), whereas byte0-as-weighted put ~65% of the
            // body on the pelvis and exploded the mesh on animation. So BoneA is byte1 (receives
            // Weight) and BoneB is byte0 (receives 1-Weight). content-formats.md flagged this
            // A-vs-B convention as inferred; this is the resolution.
            v.BoneA = (byte)((packed >> 8) & 0xFF);
            v.BoneB = (byte)(packed & 0xFF);
            v.Weight = (short)(packed >> 16) / 16384.0f;
        }

        private static Vector3 DecodeNormal(uint packed)
        {
            // x = bits 20..29 (signed arithmetic shift, no mask — binary fidelity), y = 10..19,
            // z = 0..9; each biased by -0x200 and scaled by 1/512.
            int ix = ((int)packed >> 20) - 0x200;
            int iy = (int)((packed >> 10) & 0x3FF) - 0x200;
            int iz = (int)(packed & 0x3FF) - 0x200;
            return new Vector3(ix / 512.0f, iy / 512.0f, iz / 512.0f);
        }

        private static float ReadF(byte[] d, int p) =>
            BitConverter.UInt32BitsToSingle(ReadU(d, p));

        private static uint ReadU(byte[] d, int p) =>
            (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
    }
}

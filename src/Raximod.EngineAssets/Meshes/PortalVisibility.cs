using System;
using System.Collections.Generic;
using System.Numerics;

namespace Raximod.EngineAssets.Meshes
{
    /// <summary>A native plane retained from a PlanetSide portal or convex hull.</summary>
    public sealed class PortalVisibilityPlane
    {
        public Vector3 Normal;
        public float Distance;
        public float Epsilon;
    }

    /// <summary>
    /// One authored portal quadrilateral. The first u32 is retained as <see cref="RawFlags"/> rather
    /// than interpreted: engine-derived notes associate it with portal flags, but individual bits have
    /// not yet been verified. Region values and mesh-item id are preserved exactly as stored.
    /// </summary>
    public sealed class PortalVisibilityPortal
    {
        public uint RawFlags;
        public uint RegionA;
        public uint RegionB;
        public Vector3[] Points = Array.Empty<Vector3>();
        public PortalVisibilityPlane Plane = new();
        public uint MeshItemId;
    }

    /// <summary>A named native portal-mesh descriptor; both trailing floats remain uninterpreted.</summary>
    public sealed class PortalVisibilityMeshDescriptor
    {
        public string Name = "";
        public float RawA;
        public float RawB;
    }

    /// <summary>A convex region volume with every authored plane and vertex retained.</summary>
    public sealed class PortalVisibilityConvexHull
    {
        public uint RawA;
        public Vector3 BoundsMinimum;
        public Vector3 BoundsMaximum;
        public readonly List<PortalVisibilityPlane> Planes = new();
        public Vector3[] Vertices = Array.Empty<Vector3>();
    }

    /// <summary>An opaque region substructure retained without assigning unverified semantics.</summary>
    public sealed class PortalVisibilityRegionSubstructure
    {
        public float RawA;
        public Vector3[] Points = Array.Empty<Vector3>();
    }

    /// <summary>One authored indoor region, including its portals, hulls, raw links, and opaque data.</summary>
    public sealed class PortalVisibilityRegion
    {
        public string Name = "";
        public uint Id;
        public Vector3 BoundsMinimum;
        public Vector3 BoundsMaximum;
        public readonly List<PortalVisibilityPortal> Portals = new();
        public readonly List<PortalVisibilityConvexHull> ConvexHulls = new();
        public uint[] RawLinks = Array.Empty<uint>();
        public readonly List<PortalVisibilityRegionSubstructure> Substructures = new();
    }

    /// <summary>One retained node of the native AABV spatial tree.</summary>
    public sealed class PortalVisibilityAabvNode
    {
        public byte Flags;
        public Vector3 BoundsMinimum;
        public Vector3 BoundsMaximum;
        public ushort[] LeafIndices = Array.Empty<ushort>();
        public PortalVisibilityAabvNode? Child10;
        public PortalVisibilityAabvNode? Child20;
    }

    /// <summary>A complete AABV tree plus the authored and decoded node counts.</summary>
    public sealed class PortalVisibilityAabv
    {
        public uint DeclaredNodeCount;
        public uint DecodedNodeCount;
        public PortalVisibilityAabvNode? Root;
        public bool NodeCountMatches => DeclaredNodeCount == DecodedNodeCount;
    }

    /// <summary>Native per-region portal id and its optional spatial tree.</summary>
    public sealed class PortalVisibilityRegionSpatialBinding
    {
        public uint PortalId;
        public uint HasAabvMarker;
        public PortalVisibilityAabv? Aabv;
    }

    /// <summary>
    /// Lossless decoded CPortalSystem payload. Fields whose meanings are not binary-confirmed retain
    /// raw names. Consumers must honor the generated sidecar's fail-open diagnostics rather than infer
    /// visibility solely from region ids.
    /// </summary>
    public sealed class PortalVisibilityData
    {
        public uint PresenceMarker;
        public Vector3 BoundsMinimum;
        public Vector3 BoundsMaximum;
        public readonly List<PortalVisibilityMeshDescriptor> MeshDescriptors = new();
        public readonly List<PortalVisibilityPortal> ExteriorPortals = new();
        public readonly List<PortalVisibilityRegion> Regions = new();
        public readonly List<string> Names = new();
        public readonly List<UberModel.PortalMeshItem> MeshItems = new();
        public uint MeshItemAabvMarker;
        public PortalVisibilityAabv? MeshItemAabv;
        public readonly List<PortalVisibilityRegionSpatialBinding> RegionSpatialBindings = new();
        public uint[] RawIds = Array.Empty<uint>();
        public uint RawIdAabvMarker;
        public PortalVisibilityAabv? RawIdAabv;
        public readonly List<PortalVisibilityRegionSubstructure> TrailingSubstructures = new();
        public bool Present => PresenceMarker != 0;
    }
}

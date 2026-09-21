using System.Numerics;
using System.Text.Json.Serialization;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Maps;

namespace Raximod.Generation.Continents
{
    /// <summary>
    /// Builds map-specific named composition definitions from pse_link and pse_relativeobject lists.
    /// Definitions are flattened recursively in the original local frame; cycles and unresolved link
    /// targets remain explicit diagnostics rather than disappearing from the scene.
    /// </summary>
    internal static class CompositeObjectCatalog
    {
        private const int MaxDepth = 32;

        internal sealed record Child(
            [property: JsonPropertyName("record")] string Record,
            [property: JsonPropertyName("position")] float[] Position,
            [property: JsonPropertyName("rotation")] float[] Rotation,
            [property: JsonPropertyName("scale")] float[] Scale,
            [property: JsonPropertyName("localMatrix")] float[] LocalMatrix,
            [property: JsonPropertyName("sourceDefinitions")] string[] SourceDefinitions,
            [property: JsonPropertyName("sourcePath")] int[] SourcePath,
            [property: JsonIgnore] Matrix4x4 NativeMatrix);

        internal sealed record Link(
            [property: JsonPropertyName("objectName")] string ObjectName,
            [property: JsonPropertyName("definition")] string Definition,
            [property: JsonIgnore] int SourceIndex);

        internal sealed record Diagnostics(
            [property: JsonPropertyName("cycles")] string[] Cycles,
            [property: JsonPropertyName("depthExceeded")] string[] DepthExceeded,
            [property: JsonPropertyName("nonCompositeDefinitions")] string[] NonCompositeDefinitions,
            [property: JsonPropertyName("ambiguousDefinitions")] string[] AmbiguousDefinitions);

        internal sealed record Catalog(
            [property: JsonPropertyName("definitions")] IReadOnlyDictionary<string, Child[]> Definitions,
            [property: JsonPropertyName("links")] Link[] Links,
            [property: JsonPropertyName("diagnostics")] Diagnostics Diagnostics);

        public static Catalog Build(
            IReadOnlyList<PakArchive> definitionPaks,
            PakArchive linksPak,
            string baseName,
            IReadOnlySet<string>? excludedRecords = null)
        {
            var raw = new Dictionary<string, IReadOnlyList<RelativeObject>>(StringComparer.OrdinalIgnoreCase);
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PakArchive pak in definitionPaks)
            foreach (PakEntry entry in pak.Entries)
            {
                if (!entry.Name.EndsWith(".lst", StringComparison.OrdinalIgnoreCase) ||
                    entry.Name.StartsWith("groundcover_", StringComparison.OrdinalIgnoreCase) ||
                    entry.UncompressedSize > 1_000_000)
                {
                    continue;
                }
                IReadOnlyList<RelativeObject> children;
                try { children = RelativeObjectList.Parse(pak.Extract(entry.Name)).Objects; }
                catch { continue; }
                if (children.Count == 0) continue;
                string definition = Path.GetFileNameWithoutExtension(entry.Name);
                if (!raw.TryAdd(definition, children) &&
                    !Equivalent(raw[definition], children)) ambiguous.Add(definition);
            }

            string linksName = $"objects_{baseName}.lst";
            PakEntry? linksEntry = linksPak.Entries.FirstOrDefault(entry =>
                entry.Name.Equals(linksName, StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<ObjectLink> rawLinks = linksEntry == null
                ? Array.Empty<ObjectLink>()
                : ObjectLinkList.Parse(linksPak.Extract(linksEntry.Name));
            var cycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var depthExceeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nonCompositeDefinitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var flattened = new Dictionary<string, Child[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string definition in rawLinks.Select(link => link.Definition)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string? resolvedDefinition = ResolveDefinition(definition, raw);
                if (resolvedDefinition == null)
                {
                    // pse_link is also used for pe_edit server-object lists (doors, blockers,
                    // billboards, and other stateful amenities). Those are reconciled through
                    // PSForever and are not visual composite definitions.
                    nonCompositeDefinitions.Add(definition);
                    continue;
                }
                var output = new List<Child>();
                Expand(
                    resolvedDefinition,
                    Matrix4x4.Identity,
                    new List<string>(),
                    new List<int>(),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    raw,
                    output,
                    cycles,
                    depthExceeded,
                    excludedRecords,
                    0);
                flattened[definition] = output.ToArray();
            }
            // The original pse_link sequence indexes the leading MPO objects. Preserve that
            // ordinal before filtering non-composites; it is not a nearest-position association.
            Link[] links = rawLinks.Select((link, index) => (Link: link, Index: index))
                .Where(item => flattened.ContainsKey(item.Link.Definition))
                .Select(item => new Link(item.Link.ObjectName, item.Link.Definition, item.Index))
                .ToArray();

            return new Catalog(
                flattened,
                links,
                new Diagnostics(
                    cycles.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    depthExceeded.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    nonCompositeDefinitions.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    ambiguous.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
        }

        private static string? ResolveDefinition(
            string requested,
            IReadOnlyDictionary<string, IReadOnlyList<RelativeObject>> definitions)
        {
            if (definitions.ContainsKey(requested)) return requested;
            int underscore = requested.LastIndexOf('_');
            if (underscore > 0 && int.TryParse(requested.AsSpan(underscore + 1), out _))
            {
                string unnumbered = requested.Substring(0, underscore);
                if (definitions.ContainsKey(unnumbered)) return unnumbered;
            }
            return null;
        }

        private static bool Equivalent(
            IReadOnlyList<RelativeObject> left,
            IReadOnlyList<RelativeObject> right) =>
            left.Count == right.Count && left.SequenceEqual(right);

        private static void Expand(
            string definition,
            Matrix4x4 parentMatrix,
            List<string> definitionPath,
            List<int> indexPath,
            HashSet<string> active,
            IReadOnlyDictionary<string, IReadOnlyList<RelativeObject>> definitions,
            List<Child> output,
            HashSet<string> cycles,
            HashSet<string> depthExceeded,
            IReadOnlySet<string>? excludedRecords,
            int depth)
        {
            if (depth >= MaxDepth)
            {
                depthExceeded.Add(string.Join(" -> ", definitionPath.Append(definition)));
                return;
            }
            if (!active.Add(definition))
            {
                cycles.Add(string.Join(" -> ", definitionPath.Append(definition)));
                return;
            }

            definitionPath.Add(definition);
            IReadOnlyList<RelativeObject> members = definitions[definition];
            for (int index = 0; index < members.Count; index++)
            {
                RelativeObject member = members[index];
                string record = Normalise(member.Name);
                Matrix4x4 memberMatrix = NativeMatrix(member) * parentMatrix;
                indexPath.Add(index);
                if (definitions.ContainsKey(record))
                {
                    Expand(record, memberMatrix, definitionPath, indexPath, active, definitions,
                        output, cycles, depthExceeded, excludedRecords, depth + 1);
                }
                else if (excludedRecords == null || !excludedRecords.Contains(record))
                {
                    output.Add(ToChild(record, memberMatrix, definitionPath, indexPath));
                }
                indexPath.RemoveAt(indexPath.Count - 1);
            }
            definitionPath.RemoveAt(definitionPath.Count - 1);
            active.Remove(definition);
        }

        private static Matrix4x4 NativeMatrix(RelativeObject value) =>
            Matrix4x4.CreateScale(value.Scale) *
            Matrix4x4.CreateRotationX(value.Pitch) *
            Matrix4x4.CreateRotationY(value.Roll) *
            Matrix4x4.CreateRotationZ(value.Yaw) *
            Matrix4x4.CreateTranslation(value.Position);

        private static Child ToChild(
            string record,
            Matrix4x4 native,
            IReadOnlyList<string> definitionPath,
            IReadOnlyList<int> indexPath)
        {
            Matrix4x4 basis = Matrix4x4.CreateRotationX(-MathF.PI / 2f);
            Matrix4x4.Invert(basis, out Matrix4x4 inverse);
            Matrix4x4 browser = inverse * native * basis;
            if (!Matrix4x4.Decompose(browser, out Vector3 scale, out Quaternion rotation, out Vector3 position))
            {
                throw new InvalidDataException(
                    $"Composite member {string.Join("/", definitionPath)}/{record} has a non-decomposable transform.");
            }
            return new Child(
                record,
                new[] { position.X, position.Y, position.Z },
                new[] { rotation.X, rotation.Y, rotation.Z, rotation.W },
                new[] { scale.X, scale.Y, scale.Z },
                Matrix(browser),
                definitionPath.ToArray(),
                indexPath.ToArray(),
                native);
        }

        private static string Normalise(string name) =>
            name.Length > 0 && (name[0] == '@' || name[0] == '!') ? name.Substring(1) : name;

        private static float[] Matrix(Matrix4x4 value) => new[]
        {
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44
        };
    }
}

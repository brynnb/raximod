using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Raximod.Generation.Assets;

/// <summary>Shared GLB image-storage rewrite. Retained mesh/skin/animation views are
/// copied verbatim; only image payloads, view indices and storage offsets may change.</summary>
public sealed class GlbImagePackaging(JsonObject root, byte[] binary, string file)
{
    public JsonObject Root => root;
    // URI-only packaging must preserve the entire BIN chunk, including padding
    // and unreferenced bytes, rather than relocating any geometry or animation.
    public byte[] Serialize()
    {
        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
        int size = (json.Length + 3) & ~3;
        int tail = binary.Length == 0 ? 0 : 8 + binary.Length;
        byte[] output = new byte[20 + size + tail];
        Write(output, 0, 0x46546c67); Write(output, 4, 2); Write(output, 8, output.Length);
        Write(output, 12, size); Write(output, 16, 0x4e4f534a);
        output.AsSpan(20, size).Fill(32); json.CopyTo(output, 20);
        if (tail > 0) { Write(output, 20 + size, binary.Length); Write(output, 24 + size, 0x004e4942); binary.CopyTo(output, 28 + size); }
        return output;
    }
    public ReadOnlySpan<byte> ImageBytes(int index)
    {
        var views = root["bufferViews"]?.AsArray();
        if (views == null || index < 0 || index >= views.Count)
            throw new InvalidDataException($"{file}: invalid image view.");
        return ViewBytes(views[index]!, binary, file);
    }

    public static GlbImagePackaging Read(byte[] glb, string file)
    {
        if (glb.Length < 20 || Read(glb, 0) != 0x46546c67 || Read(glb, 4) != 2 || Read(glb, 8) != glb.Length)
            throw new InvalidDataException($"{file}: invalid GLB header.");
        JsonObject? root = null;
        byte[] binary = [];
        for (int offset = 12; offset < glb.Length;)
        {
            if (offset > glb.Length - 8) throw new InvalidDataException($"{file}: truncated chunk header.");
            int length = Read(glb, offset), type = Read(glb, offset + 4);
            if (length < 0 || length % 4 != 0 || length > glb.Length - offset - 8)
                throw new InvalidDataException($"{file}: invalid chunk range.");
            if (type == 0x4e4f534a && offset == 12)
                root = JsonNode.Parse(glb.AsSpan(offset + 8, length))!.AsObject();
            else if (type == 0x004e4942 && binary.Length == 0)
                binary = glb.AsSpan(offset + 8, length).ToArray();
            else throw new InvalidDataException($"{file}: unsupported/duplicate GLB chunk.");
            offset += 8 + length;
        }
        if (root == null) throw new InvalidDataException($"{file}: missing JSON chunk.");
        return new GlbImagePackaging(root, binary, file);
    }

    public byte[] Repack(HashSet<int> removed, IReadOnlyDictionary<int, byte[]>? replacements = null)
    {
        var views = root["bufferViews"]?.AsArray() ?? new JsonArray();
        if (root["buffers"] is not JsonArray buffers || buffers.Count != 1 || buffers[0]?["uri"] != null)
            throw new InvalidDataException($"{file}: expected one embedded buffer.");
        // Repack all retained views, including skin, sparse-accessor and animation
        // data. Preserve view metadata and remap references without rebuilding meshes.
        var retained = new JsonArray();
        var indices = new Dictionary<int, int>();
        using var packed = new MemoryStream();
        for (int i = 0; i < views.Count; i++)
        {
            if (removed.Contains(i)) continue;
            JsonNode view = views[i]!.DeepClone();
            var bytes = ViewBytes(view, binary, file);
            while (packed.Length % 4 != 0) packed.WriteByte(0);
            view["byteOffset"] = packed.Length;
            if (replacements?.TryGetValue(i, out byte[]? replacement) == true)
            { packed.Write(replacement); view["byteLength"] = replacement.Length; }
            else packed.Write(bytes);
            indices[i] = retained.Count;
            retained.Add(view);
        }
        root["bufferViews"] = retained;
        if (retained.Count == 0) throw new InvalidDataException($"{file}: image-only GLBs require a separate packaging policy.");
        Remap(root);
        buffers[0]!["byteLength"] = packed.Length;
        while (packed.Length % 4 != 0) packed.WriteByte(0);
        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
        int jsonSize = (json.Length + 3) & ~3;
        byte[] output = new byte[checked(28 + jsonSize + (int)packed.Length)];
        Write(output, 0, 0x46546c67); Write(output, 4, 2); Write(output, 8, output.Length);
        Write(output, 12, jsonSize); Write(output, 16, 0x4e4f534a);
        output.AsSpan(20, jsonSize).Fill(32); json.CopyTo(output, 20);
        Write(output, 20 + jsonSize, (int)packed.Length); Write(output, 24 + jsonSize, 0x004e4942);
        packed.ToArray().CopyTo(output, 28 + jsonSize);
        return output;

        void Remap(JsonNode? node, bool imageReference = false)
        {
            if (node is JsonArray array) foreach (var child in array) Remap(child, imageReference);
            if (node is not JsonObject obj) return;
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                // Extras are application metadata, not glTF buffer-view references.
                if (key == "extras") continue;
                if (key != "bufferView") { Remap(obj[key], key == "images"); continue; }
                int old = obj[key]!.GetValue<int>();
                if (!imageReference && replacements?.ContainsKey(old) == true)
                    throw new InvalidDataException($"{file}: non-image data references replaced image view {old}.");
                if (!indices.TryGetValue(old, out int next))
                    throw new InvalidDataException($"{file}: non-image data references removed image view {old}.");
                obj[key] = next;
            }
        }
    }

    private static ReadOnlySpan<byte> ViewBytes(JsonNode view, byte[] binary, string file)
    {
        int start = view["byteOffset"]?.GetValue<int>() ?? 0;
        int size = view["byteLength"]!.GetValue<int>();
        if ((view["buffer"]?.GetValue<int>() ?? 0) != 0 || start < 0 || size <= 0 || start > binary.Length - size)
            throw new InvalidDataException($"{file}: invalid buffer view range.");
        if (view["extensions"] != null) throw new InvalidDataException($"{file}: buffer view extensions require explicit relocation support.");
        return binary.AsSpan(start, size);
    }
    private static int Read(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Write(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}

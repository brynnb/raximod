using System.Text.Json.Nodes;

namespace Raximod.Generation.Assets;

/// <summary>Lossless browser packaging of existing native exports. Only image storage,
/// buffer-view indices and byte offsets change; mesh/rig/animation payloads are copied verbatim.</summary>
public static class SharedGlbImageExport
{
    public sealed record Result(int Files, long BeforeBytes, long AfterBytes);

    public static Result Run(string directory, bool check = false, string? textureDirectory = null, bool recursive = false)
    {
        string[] files = Directory.GetFiles(directory, "*.glb", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
        // Validate the whole family before replacing any published model. Independent
        // files share no mutable state or texture writes, so keep concurrency bounded.
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        long before = 0, after = 0;
        Parallel.ForEach(files, options, file => {
            byte[] original = File.ReadAllBytes(file);
            byte[] output = Convert(original, file, textureDirectory);
            if (check && !original.AsSpan().SequenceEqual(output))
                throw new InvalidDataException($"{file}: embedded images still require externalization.");
            Interlocked.Add(ref before, original.Length);
            Interlocked.Add(ref after, output.Length);
        });
        if (!check) Parallel.ForEach(files, options, file => {
            byte[] original = File.ReadAllBytes(file);
            byte[] output = Convert(original, file, textureDirectory);
            if (original.AsSpan().SequenceEqual(output)) return;
            string temporary = file + $".raximod-{Guid.NewGuid():N}.tmp";
            try { File.WriteAllBytes(temporary, output); File.Move(temporary, file, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
        return new Result(files.Length, before, after);
    }

    public static byte[] Convert(byte[] glb, string file, string? textureDirectory = null)
    {
        var document = GlbImagePackaging.Read(glb, file);
        var root = document.Root;
        var removed = new HashSet<int>();
        var views = root["bufferViews"]?.AsArray() ?? new JsonArray();
        foreach (JsonNode? entry in root["images"]?.AsArray() ?? [])
        {
            var image = entry!.AsObject();
            string name = image["name"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.Contains('\\'))
                throw new InvalidDataException($"{file}: unsafe or missing image name.");
            string modelDirectory = Path.GetDirectoryName(Path.GetFullPath(file))!;
            string sharedPath = Path.Combine(textureDirectory ?? Path.Combine(modelDirectory, "material-textures"), name);
            // Real glTF-relative URIs preserve the family texture pool at every
            // nesting depth. Consumers resolve the URI; names are not a redirect map.
            string uri = Path.GetRelativePath(modelDirectory, Path.GetFullPath(sharedPath)).Replace('\\', '/');
            if (image["bufferView"] == null && image["uri"]?.GetValue<string>() is string publishedUri && publishedUri != uri)
            {
                // Final cross-family packaging replaces the family companion with
                // an actual content-addressed URI. Reuse must validate those bytes
                // without reconstructing or restoring a duplicate from image.name.
                if (publishedUri.Contains(':') || publishedUri.Contains('\\') || publishedUri.StartsWith('/'))
                    throw new InvalidDataException($"{file}: invalid published texture URI.");
                string publishedPath = Path.GetFullPath(Path.Combine(modelDirectory, publishedUri));
                if (Path.GetFileName(Path.GetDirectoryName(publishedPath)) != "textures")
                    throw new InvalidDataException($"{file}: unexpected external image URI for {name}.");
                SharedTexturePool.ValidateImage(publishedPath);
                // A partial re-export may have recreated the named companion.
                // Reject stale pooled models if those newly produced bytes differ.
                if (File.Exists(sharedPath) && !File.ReadAllBytes(sharedPath).AsSpan().SequenceEqual(File.ReadAllBytes(publishedPath)))
                    throw new InvalidDataException($"{file}: newly exported image {name} differs from the reused pool image; regenerate the model.");
                continue;
            }
            byte[] shared = File.ReadAllBytes(sharedPath);
            if (image["bufferView"] == null)
            {
                if (image["uri"]?.GetValue<string>() != uri)
                    throw new InvalidDataException($"{file}: unexpected external image URI for {name}.");
                continue;
            }
            int index = image["bufferView"]!.GetValue<int>();
            if (index < 0 || index >= views.Count) throw new InvalidDataException($"{file}: invalid image view.");
            var bytes = document.ImageBytes(index);
            if (!bytes.SequenceEqual(shared)) throw new InvalidDataException($"{file}: shared image {name} differs from embedded bytes.");
            removed.Add(index);
            image.Remove("bufferView");
            image.Remove("mimeType");
            image["uri"] = uri;
        }
        if (removed.Count == 0) return glb;
        return document.Repack(removed);
    }
}

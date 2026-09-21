using System.Security.Cryptography;
using System.Text.Json;
using Raximod.Generation.Assets;

namespace Raximod.Generation.Packaging
{
    /// <summary>
    /// Deterministic, machine-readable provenance written beside a portable model export.
    /// Runtime-only native material programs and gameplay metadata deliberately remain outside GLB.
    /// </summary>
    public sealed record ExportReceipt(
        string Format,
        int Version,
        string Profile,
        string Record,
        string SourceLibrary,
        string Asset,
        string Sha256,
        ExportReceiptGeometry Geometry,
        ExportReceiptContent Content,
        IReadOnlyList<string> FidelityLimits)
    {
        public const string FormatName = "raximod-model-export";
        public const int CurrentVersion = 1;

        public static ExportReceipt From(
            ExportProfile profile,
            GlbExportTool.Result result,
            string sourceRoot,
            string receiptPath)
        {
            string receiptDirectory = Path.GetDirectoryName(Path.GetFullPath(receiptPath))!;
            string assetPath = Path.GetFullPath(result.OutputPath);
            string sourceLibrary = Path.GetFullPath(result.LibraryPath);
            return new ExportReceipt(
                FormatName,
                CurrentVersion,
                profile.ToString().ToLowerInvariant(),
                result.RecordName,
                NormalizePath(Path.GetRelativePath(Path.GetFullPath(sourceRoot), sourceLibrary)),
                NormalizePath(Path.GetRelativePath(receiptDirectory, assetPath)),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assetPath))).ToLowerInvariant(),
                new ExportReceiptGeometry(
                    result.Meshes,
                    result.Sections,
                    result.Triangles,
                    result.Bones),
                new ExportReceiptContent(
                    result.Materials,
                    profile == ExportProfile.Standalone ? result.Textures : 0,
                    profile == ExportProfile.Shared ? result.Textures : 0,
                    result.Animations),
                new[]
                {
                    "GLB uses a portable standard-material representation of the native material data.",
                    "Native material programs, particle effects, sounds, and gameplay behavior are outside this model export.",
                });
        }

        public void Write(string path)
        {
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string temporaryPath = fullPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporaryPath, fullPath, true);
        }

        private static string NormalizePath(string path) => path.Replace('\\', '/');

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
    }

    public sealed record ExportReceiptGeometry(int Meshes, int Sections, int Triangles, int Bones);

    public sealed record ExportReceiptContent(
        int Materials,
        int EmbeddedTextures,
        int SharedTextures,
        int Animations);
}

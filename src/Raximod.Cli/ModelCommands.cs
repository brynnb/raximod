using Raximod.Generation;
using Raximod.Generation.Assets;
using Raximod.Generation.Packaging;
using Raximod.Generation.Records;
using Raximod.EngineAssets.Textures;

namespace Raximod
{
    internal static class ModelCommands
    {
        public static int Export(string[] arguments)
        {
            var args = new CommandArguments(arguments);
            string source = args.Required("--source");
            string record = args.Required("--record");
            string output = args.Required("--out");
            ExportProfile profile = ExportProfiles.Parse(args.Value("--profile"));
            string? sharedTextures = args.Value("--shared-textures");

            bool materialsOnly = args.Has("--materials-only");
            bool renderStatesOnly = args.Has("--render-states-only");
            if (materialsOnly && renderStatesOnly)
                throw new ArgumentException("Choose either --materials-only or --render-states-only, not both.");

            if (renderStatesOnly)
            {
                NativeMaterialMetadataRefresh.RenderStates(
                    Path.ChangeExtension(output, ".materials.json"),
                    new TextureProvider(source));
                Console.WriteLine($"refreshed native render states for {record}");
                return 0;
            }

            if (materialsOnly)
            {
                NativeMaterialFactionBindings.Refresh(
                    output,
                    record,
                    new TextureProvider(source),
                    sharedTextures);
                Console.WriteLine($"refreshed native materials for {record}: {Path.GetFullPath(output)}");
                return 0;
            }

            if (profile == ExportProfile.Shared && string.IsNullOrWhiteSpace(sharedTextures))
            {
                throw new ArgumentException(
                    "The shared profile requires --shared-textures <directory>; use the standalone profile for an all-in-one GLB.");
            }

            bool includeTextures = !args.Has("--no-textures");
            bool includeAnimations = !args.Has("--no-animations");
            int maxAnimations = args.Integer(
                "--max-animations",
                profile == ExportProfile.Standalone ? int.MaxValue : 250);
            if (!includeAnimations) maxAnimations = 0;

            var options = new GlbExportTool.Options(
                source,
                record,
                output,
                args.Value("--library"),
                IncludeTextures: includeTextures,
                IncludeAnimations: includeAnimations,
                MaxAnimations: maxAnimations,
                BakeWorldOffset: args.Has("--bake-world-offset"),
                AnimationPrefixes: args.Values("--animation-prefix"),
                SharedTextureDirectory: profile == ExportProfile.Shared ? sharedTextures : null,
                MeshMaterials: args.Values("--mesh-material"),
                WriteTextureCompanions: profile == ExportProfile.Shared,
                WriteNativeSidecars: profile == ExportProfile.Shared);

            var progress = new SynchronousProgress<string>(Console.WriteLine);
            GlbExportTool.Result result = GlbExportTool.Run(options, progress);
            if (profile == ExportProfile.Shared)
            {
                byte[] original = File.ReadAllBytes(result.OutputPath);
                byte[] externalized = SharedGlbImageExport.Convert(original, result.OutputPath, sharedTextures);
                if (!original.AsSpan().SequenceEqual(externalized))
                {
                    string temporaryPath = result.OutputPath + ".raximod.tmp";
                    File.WriteAllBytes(temporaryPath, externalized);
                    File.Move(temporaryPath, result.OutputPath, true);
                }
            }
            string receiptPath = args.Value("--receipt") ?? Path.ChangeExtension(output, ".export.json");
            ExportReceipt receipt = ExportReceipt.From(profile, result, source, receiptPath);
            receipt.Write(receiptPath);

            string texturePackaging = profile == ExportProfile.Standalone
                ? $"{result.Textures} embedded textures"
                : $"{result.Textures} shared textures";
            Console.WriteLine(
                $"exported {result.RecordName}: {result.Triangles} triangles, {result.Materials} materials, " +
                $"{texturePackaging}, {result.Bones} bones, {result.Animations} animations");
            Console.WriteLine($"asset: {Path.GetFullPath(result.OutputPath)}");
            Console.WriteLine($"receipt: {Path.GetFullPath(receiptPath)}");
            return 0;
        }

        public static int Inspect(string[] arguments)
        {
            var args = new CommandArguments(arguments);
            var progress = new SynchronousProgress<string>(Console.WriteLine);
            int found = ListRecordsTool.Run(
                new ListRecordsTool.Options(args.Required("--source"), args.Required("--record")),
                progress);
            return found > 0 ? 0 : 3;
        }
    }
}

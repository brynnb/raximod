namespace Raximod.Generation.Diagnostics
{
    public static class ExtractionReportTool
    {
        public sealed record Options(string ContinentDirectory, string AssetDirectory,
            string EffectDirectory, string OutputPath);

        public static ExtractionReport Run(Options options)
        {
            var report = new ExtractionReport();
            SceneExtractionAudit.Run(Path.GetFullPath(options.ContinentDirectory), report);
            MaterialExtractionAudit.Run(Path.GetFullPath(options.AssetDirectory),
                Path.GetFullPath(options.EffectDirectory), report);
            EffectExtractionAudit.Run(Path.GetFullPath(options.EffectDirectory), report);
            report.Write(Path.GetFullPath(options.OutputPath));
            return report;
        }
    }
}

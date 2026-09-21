using Raximod.Generation;
using Raximod.Generation.Assets;
using Raximod.Generation.Diagnostics;

namespace Raximod;

internal static class AuditCommands
{
    public static int MeshSelection(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        HighestQualityMeshCatalogAuditDocument audit = HighestQualityMeshCatalogAudit.Run(
            new HighestQualityMeshCatalogAudit.Options(args.Required("--source"), args.Required("--out")),
            new SynchronousProgress<string>(Console.WriteLine));
        Console.WriteLine(
            $"mesh-selection audit: libraries={audit.Metrics.InstalledLibraries} records={audit.Metrics.Records} " +
            $"decoded={audit.Metrics.DecodedRecords} failures={audit.Metrics.DecodeFailures} " +
            $"errors={audit.Metrics.Errors} warnings={audit.Metrics.Warnings}");
        return args.Has("--fail-on-ambiguity") && audit.Metrics.Errors + audit.Metrics.Warnings > 0 ? 3 : 0;
    }

    public static int Spatial(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        string[] records = args.Values("--record");
        NativeSpatialAuditTool.Result result = NativeSpatialAuditTool.Run(
            new NativeSpatialAuditTool.Options(
                args.Required("--source"),
                args.Required("--models"),
                records.Length > 0 ? records : null),
            new SynchronousProgress<string>(Console.WriteLine));
        foreach (NativeSpatialAuditTool.Failure failure in result.Failures)
            Console.Error.WriteLine($"{failure.Record}: {failure.Reason}");
        Console.WriteLine(
            $"spatial audit: examined={result.Examined} complete={result.Complete} " +
            $"aab={result.AabRecords} failures={result.Failures.Count}");
        return result.Failures.Count == 0 ? 0 : 3;
    }

    public static int Extraction(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        ExtractionReport report = ExtractionReportTool.Run(new ExtractionReportTool.Options(
            args.Required("--continents"),
            args.Required("--assets"),
            args.Required("--effects"),
            args.Required("--out")));
        int warnings = report.Issues.Count(issue => issue.Severity == "warning");
        Console.WriteLine(
            $"extraction audit: metrics={report.Metrics.Count} warnings={warnings} " +
            $"informational={report.Issues.Count - warnings}");
        return args.Has("--fail-on-warning") && warnings > 0 ? 3 : 0;
    }
}

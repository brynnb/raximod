using System.Text.Json;

namespace Raximod.Generation.Diagnostics
{
    public sealed record ExtractionIssue(string Severity, string Category, string Source, string Item, string Message);

    public sealed class ExtractionReport
    {
        private readonly Dictionary<string, long> _metrics = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ExtractionIssue> _issues = new();
        public IReadOnlyDictionary<string, long> Metrics => _metrics;
        public IReadOnlyList<ExtractionIssue> Issues => _issues;
        public void Add(string metric, long count = 1) => _metrics[metric] = _metrics.GetValueOrDefault(metric) + count;
        public void Warn(string category, string source, string item, string message) =>
            _issues.Add(new ExtractionIssue("warning", category, source, item, message));
        public void Info(string category, string source, string item, string message) =>
            _issues.Add(new ExtractionIssue("info", category, source, item, message));

        public void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var warningGroups = _issues
                .GroupBy(issue => new { issue.Severity, issue.Category, issue.Item, issue.Message })
                .Select(group => new
                {
                    group.Key.Severity,
                    group.Key.Category,
                    group.Key.Item,
                    group.Key.Message,
                    occurrences = group.Count(),
                    sources = group.Select(issue => issue.Source).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                })
                .OrderBy(group => group.Severity).ThenBy(group => group.Category).ThenBy(group => group.Item)
                .ToArray();
            var document = new
            {
                format = "raxicore-extraction-report",
                version = 2,
                generatedAt = DateTimeOffset.UtcNow,
                strict = true,
                invariants = new
                {
                    portalChildTransformFailureAbortsExport = true,
                    compositeCyclesAndDepthLimitsAreReported = true,
                    nativeMaterialCommandsRemainLossless = true,
                    effectCommandsAndLinksRemainLossless = true,
                    fuzzySpatialMeshDeletionIsForbidden = true
                },
                knownRisks = new
                {
                    highestQualityMeshFamilies = "conservative native LOD and exact-name policy; " +
                        "catalog audit must remain clean and per-mesh skeleton ownership must remain unambiguous"
                },
                metrics = _metrics.OrderBy(pair => pair.Key).ToDictionary(),
                warningCount = _issues.Count(issue => issue.Severity == "warning"),
                informationalCount = _issues.Count(issue => issue.Severity == "info"),
                uniqueIssueCount = warningGroups.Length,
                issueGroups = warningGroups,
                issues = _issues.OrderBy(issue => issue.Category).ThenBy(issue => issue.Source)
            };
            File.WriteAllText(path, JsonSerializer.Serialize(document,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
        }
    }
}

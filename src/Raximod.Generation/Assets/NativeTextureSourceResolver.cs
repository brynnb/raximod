using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Raximod.EngineAssets.Textures;

namespace Raximod.Generation.Assets
{
    /// <summary>
    /// Resolves the few native texture references that do not live in the FLAT texture archives.
    /// Repairs are exact and documented; this deliberately does not perform fuzzy filename matching.
    /// </summary>
    public static class NativeTextureSourceResolver
    {
        public sealed record Resolution(DdsImage? Image, string AuthoredKey, string ResolvedKey,
            string? RepairReason, string? SourceArchive)
        {
            public bool Repaired => RepairReason != null;
            internal NativeTextureResourceIndex.Search? MissingSource { get; init; }
        }

        private static readonly IReadOnlyDictionary<string, (string Key, string Reason)> Aliases =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["em_star"] = (
                    "em_star_001",
                    "Retail materials omit the numbered frame suffix; em_star_001 is the shipped first " +
                    "frame used by the rotating empire-star material."),
                ["drivermount001"] = (
                    "drivemount",
                    "The animation database uses drivermount while the shipped atlas drops the letter r."),
                ["passengermount001"] = (
                    "passenmount",
                    "The animation database uses passengermount while the shipped atlas uses passenmount.")
            };

        private static readonly ConditionalWeakTable<TextureProvider, Lazy<NativeTextureResourceIndex>> SourceIndexes = new();
        private static readonly ConditionalWeakTable<TextureProvider, ConcurrentDictionary<string, Lazy<Resolution>>> Resolutions = new();

        public static Resolution Resolve(TextureProvider textures, string authoredKey) =>
            Resolutions.GetValue(textures, _ => new(StringComparer.OrdinalIgnoreCase))
                .GetOrAdd(authoredKey, key => new(() => ResolveSource(textures, key))).Value;

        private static Resolution ResolveSource(TextureProvider textures, string authoredKey)
        {
            DdsImage? direct = textures.Get(authoredKey);
            if (direct != null) return new Resolution(direct, authoredKey, authoredKey, null, null);
            if (textures.Contains(authoredKey))
                throw new InvalidDataException($"Indexed native DDS could not be decoded: {authoredKey}");

            if (Aliases.TryGetValue(authoredKey, out var alias))
            {
                DdsImage? aliased = textures.Get(alias.Key);
                if (aliased != null)
                    return new Resolution(aliased, authoredKey, alias.Key, alias.Reason, null);
                if (textures.Contains(alias.Key))
                    throw new InvalidDataException($"Indexed native DDS alias could not be decoded: {authoredKey} -> {alias.Key}");
            }

            // An absent provider cannot prove a source absence. With an installed
            // source, inspect exact PACK and loose DDS names as well as FLAT.
            if (textures.AssetDirectory == null) return new(null, authoredKey, authoredKey, null, null);
            var index = SourceIndexes.GetValue(textures, t => new(() => new(t))).Value;
            var source = index.Read(authoredKey);
            if (source.Data != null)
                return new(DdsImage.Decode(source.Data), authoredKey, authoredKey,
                    "Exact static DDS resource outside the FLAT texture archives.", source.Source);
            return new(null, authoredKey, authoredKey, null, null) {
                MissingSource = textures.SourceArchives.Count > 0 ? index.Evidence : null,
            };
        }

        public static string ResolveKnownAlias(string key) =>
            Aliases.TryGetValue(key, out var alias) ? alias.Key : key;
    }
}

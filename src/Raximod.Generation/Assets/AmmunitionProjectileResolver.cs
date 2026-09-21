using System.Text.RegularExpressions;
using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets
{
    /// <summary>
    /// Resolves the projectile variant selected by a weapon fire mode. PlanetSide ammunition
    /// records inconsistently use either <c>projectile</c> or <c>projectile0</c> for their base
    /// projectile, so exporters must apply the same deterministic rules.
    /// </summary>
    public static partial class AmmunitionProjectileResolver
    {
        public sealed record Result(
            string? BaseProjectileName,
            string? ProjectileName,
            GameObjectDb.GameObject? BaseProjectile,
            GameObjectDb.GameObject? Projectile,
            bool RequestedVariantMissing,
            bool UsedSoleVariantFallback,
            string? Warning);

        public static Result Resolve(
            IReadOnlyDictionary<string, GameObjectDb.GameObject> objects,
            GameObjectDb.GameObject? ammunition,
            int requestedIndex)
        {
            static string? Property(GameObjectDb.GameObject value, string key)
            {
                string? result = GameObjectPropertyReader.Scalar(value, key);
                return string.IsNullOrWhiteSpace(result) ? null : result;
            }

            string? baseName = ammunition is null
                ? null
                : Property(ammunition, "projectile") ?? Property(ammunition, "projectile0");
            string? requestedName = requestedIndex > 0 && ammunition is not null
                ? Property(ammunition, $"projectile{requestedIndex}")
                : baseName;
            bool requestedMissing = requestedIndex > 0 && string.IsNullOrWhiteSpace(requestedName);
            bool usedSoleFallback = false;

            if (requestedMissing && ammunition is not null)
            {
                string[] variants = ammunition.Properties
                    .Where(property => ProjectileProperty().IsMatch(property.Key))
                    .Select(property => GameObjectPropertyReader.Scalar(
                        new[] { property }, ammunition.Name, property.Key))
                    .OfType<string>()
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (variants.Length == 1)
                {
                    requestedName = variants[0];
                    usedSoleFallback = true;
                }
                else
                {
                    requestedName = baseName;
                }
            }

            objects.TryGetValue(baseName ?? "", out GameObjectDb.GameObject? baseProjectile);
            objects.TryGetValue(requestedName ?? "", out GameObjectDb.GameObject? projectile);
            string? warning = requestedMissing
                ? $"ammunition '{ammunition?.Name ?? "<missing>"}' has no projectile{requestedIndex}; "
                    + (usedSoleFallback
                        ? $"using its sole projectile variant '{requestedName}'"
                        : requestedName is not null
                            ? $"using base projectile '{requestedName}'"
                            : "no deterministic fallback is available")
                : null;
            return new Result(
                baseName,
                requestedName,
                baseProjectile,
                projectile,
                requestedMissing,
                usedSoleFallback,
                warning);
        }

        [GeneratedRegex("^projectile\\d*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ProjectileProperty();
    }
}

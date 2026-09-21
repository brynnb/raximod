using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Raximod.EngineAssets.Databases
{
    /// <summary>
    /// Cardinality-aware access to resolved <c>game_objects.adb</c> properties.
    /// ADB properties are ordered argument lists: callers must deliberately choose
    /// scalar, fixed-width tuple, or list semantics instead of silently taking the
    /// first argument and discarding the rest.
    /// </summary>
    public static class GameObjectPropertyReader
    {
        public static string? Scalar(GameObjectDb.GameObject? gameObject, string property) =>
            gameObject is null ? null : Scalar(gameObject.Properties, gameObject.Name, property);

        public static string? Scalar(
            IEnumerable<KeyValuePair<string, List<string>>> properties,
            string source,
            string property)
        {
            IReadOnlyList<string> values = List(properties, property);
            return values.Count switch
            {
                0 => null,
                1 => values[0],
                _ => throw Cardinality(source, property, "a scalar", values.Count),
            };
        }

        public static IReadOnlyList<string> List(GameObjectDb.GameObject? gameObject, string property) =>
            gameObject is null ? Array.Empty<string>() : List(gameObject.Properties, property);

        public static IReadOnlyList<string> List(
            IEnumerable<KeyValuePair<string, List<string>>> properties,
            string property) =>
            properties.FirstOrDefault(item => item.Key.Equals(property, StringComparison.OrdinalIgnoreCase)).Value
            ?? (IReadOnlyList<string>)Array.Empty<string>();

        public static IReadOnlyList<string>? Tuple(
            GameObjectDb.GameObject? gameObject,
            string property,
            params int[] allowedArities) => gameObject is null
                ? null
                : Tuple(gameObject.Properties, gameObject.Name, property, allowedArities);

        public static IReadOnlyList<string>? Tuple(
            IEnumerable<KeyValuePair<string, List<string>>> properties,
            string source,
            string property,
            params int[] allowedArities)
        {
            if (allowedArities.Length == 0)
                throw new ArgumentException("At least one tuple arity is required", nameof(allowedArities));
            IReadOnlyList<string> values = List(properties, property);
            if (values.Count == 0) return null;
            if (!allowedArities.Contains(values.Count))
                throw Cardinality(source, property,
                    $"a tuple of {string.Join(" or ", allowedArities.Order())} values", values.Count);
            return values;
        }

        private static InvalidDataException Cardinality(
            string source,
            string property,
            string expected,
            int actual) => new(
                $"game_objects.adb record '{source}' property '{property}' contains {actual} values; "
                + $"the exporter requested {expected}. Use Tuple(...) or List(...) explicitly.");
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Raximod.EngineAssets.Archives;

namespace Raximod.EngineAssets.Databases
{
    /// <summary>
    /// PlanetSide's authored zone environment. Zone-to-sky and zone-to-time-cycle bindings come from
    /// <c>game_objects.adb</c>; lighting keyframes come from <c>timeofday.adb</c> and <c>light3d.adb</c>.
    /// All three databases are stored in <c>startup.pak</c>.
    /// </summary>
    public sealed class SkyDatabase
    {
        public readonly record struct SkyLight(
            Vector3 Horizon,
            Vector3 Sky,
            Vector3 Sun,
            Vector3 SunDir,
            string Name,
            string Texture,
            float Yon = 0,
            float FogStart = 0,
            float FogEnd = 1);

        public readonly record struct SkyKeyframe(float Hour, float Blend, SkyLight Light);
        public readonly record struct SkyLayer(int Index, string Kind, string Name, string? Argument);

        public sealed record SkyEnvironment(
            string Zone,
            string Cycle,
            string SkyDome,
            string Texture,
            float Radius,
            Vector3 SphereOffset,
            Vector2? DustCloudAngle,
            IReadOnlyList<SkyLayer> Layers,
            IReadOnlyList<SkyKeyframe> Keyframes,
            GameObjectDb.GameObjectProvenance? ZoneProvenance,
            GameObjectDb.GameObjectProvenance? SkyDomeProvenance);

        private readonly record struct CycleKeyframe(float Hour, float Blend, string LightName);

        private readonly Dictionary<string, SkyLight> _lights = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<CycleKeyframe>> _cycles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GameObjectDb.GameObject> _objects = new(StringComparer.OrdinalIgnoreCase);
        private GameObjectDb.DecoderDiagnostics? _gameObjectDiagnostics;

        public int LightCount => _lights.Count;
        public int CycleCount => _cycles.Count;
        public IReadOnlyCollection<string> Cycles => _cycles.Keys;
        public GameObjectDb.DecoderDiagnostics? GameObjectDiagnostics => _gameObjectDiagnostics;

        public static SkyDatabase? TryLoad(string? assetDir)
        {
            if (string.IsNullOrEmpty(assetDir)) return null;
            string pakPath = Path.Combine(assetDir, "startup.pak");
            if (!File.Exists(pakPath)) return null;
            PakArchive pak = PakArchive.Load(File.ReadAllBytes(pakPath));
            byte[]? l3d = Extract(pak, "light3d.adb");
            byte[]? tod = Extract(pak, "timeofday.adb");
            byte[]? objects = Extract(pak, "game_objects.adb");
            if (l3d == null || tod == null || objects == null) return null;

            var db = new SkyDatabase();
            db.ParseLights(l3d);
            db.ParseCycles(tod);
            GameObjectDb gameObjects = GameObjectDb.Parse(objects);
            db._gameObjectDiagnostics = gameObjects.Diagnostics;
            foreach (GameObjectDb.GameObject item in gameObjects.ResolvedObjects)
            {
                db._objects[item.Name] = item;
            }
            return db.LightCount > 0 ? db : null;
        }

        private static byte[]? Extract(PakArchive pak, string name)
        {
            foreach (PakEntry entry in pak.Entries)
            {
                if (entry.Name.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    return pak.Extract(entry.Name);
                }
            }
            return null;
        }

        /// <summary>Resolves the complete authored environment for a zone stem such as map10.</summary>
        public SkyEnvironment? EnvironmentForZone(string stem)
        {
            if (string.IsNullOrWhiteSpace(stem)) return null;
            stem = stem.ToLowerInvariant();
            _objects.TryGetValue(stem, out GameObjectDb.GameObject? zone);

            string cycle = Property(zone, "timeofday") ?? ResolveCycle(stem) ?? "";
            // The shipped map06 record references "artic_cycle", but that record is absent from the
            // shipped timeofday database. map02 is the authored arctic morning/day/night sequence.
            if (!_cycles.ContainsKey(cycle))
            {
                cycle = cycle.Equals("artic_cycle", StringComparison.OrdinalIgnoreCase)
                    && _cycles.ContainsKey("map02")
                        ? "map02"
                        : ResolveCycle(stem) ?? cycle;
            }
            string domeName = Property(zone, "skydome") ?? "";
            _objects.TryGetValue(domeName, out GameObjectDb.GameObject? dome);
            List<SkyLayer> layers = ParseLayers(dome);
            string texture = TextureForDome(domeName, layers, cycle);
            List<SkyKeyframe> keyframes = ResolveKeyframes(cycle, texture);
            if (keyframes.Count == 0 && dome == null) return null;

            return new SkyEnvironment(
                stem,
                cycle,
                domeName,
                texture,
                FloatProperty(dome, "radius"),
                VectorProperty(dome, "sphere_offset"),
                Vector2Property(dome, "dust_cloud_angle"),
                layers,
                keyframes,
                zone?.Provenance,
                dome?.Provenance);
        }

        private string? ResolveCycle(string stem)
        {
            if (_cycles.ContainsKey(stem)) return stem;
            if (_cycles.ContainsKey(stem + "cycle")) return stem + "cycle";
            if (stem.StartsWith("ugd", StringComparison.OrdinalIgnoreCase))
                return _cycles.ContainsKey("underground") ? "underground" : null;
            if (stem.StartsWith("map", StringComparison.OrdinalIgnoreCase))
                return _cycles.ContainsKey("map01") ? "map01" : null;
            return null;
        }

        private List<SkyKeyframe> ResolveKeyframes(string cycle, string texture)
        {
            var result = new List<SkyKeyframe>();
            if (!_cycles.TryGetValue(cycle, out List<CycleKeyframe>? frames)) return result;
            foreach (CycleKeyframe frame in frames)
            {
                if (_lights.TryGetValue(frame.LightName, out SkyLight light))
                {
                    result.Add(new SkyKeyframe(frame.Hour, frame.Blend, light with { Texture = texture }));
                }
            }
            return result;
        }

        private static string? Property(GameObjectDb.GameObject? item, string name) =>
            GameObjectPropertyReader.Scalar(item, name);

        private static float FloatProperty(GameObjectDb.GameObject? item, string name) =>
            GameObjectPropertyReader.Scalar(item, name) is string value
                ? Float(value)
                : 0;

        private static Vector3 VectorProperty(GameObjectDb.GameObject? item, string name)
        {
            IReadOnlyList<string>? values = GameObjectPropertyReader.Tuple(item, name, 3);
            if (values is null) return default;
            return new Vector3(Float(values[0]), Float(values[1]), Float(values[2]));
        }

        private static Vector2? Vector2Property(GameObjectDb.GameObject? item, string name)
        {
            IReadOnlyList<string>? values = GameObjectPropertyReader.Tuple(item, name, 2);
            if (values is null) return null;
            return new Vector2(Float(values[0]), Float(values[1]));
        }

        public static List<SkyLayer> ParseLayers(GameObjectDb.GameObject? dome)
        {
            if (dome == null) return new List<SkyLayer>();
            var layers = new List<SkyLayer>();
            foreach (string key in dome.Properties.Keys.Where(key =>
                key.StartsWith("layer", StringComparison.OrdinalIgnoreCase)))
            {
                if (!int.TryParse(key.AsSpan(5), out int index) || index <= 0
                    || layers.Any(layer => layer.Index == index))
                    throw new InvalidDataException($"Sky '{dome.Name}' has invalid or duplicate layer index '{key}'");
                // A fourth argument or a short tuple is not an ignorable tail/missing layer.
                // Retain the lossless ADB, but fail this semantic export for explicit review.
                IReadOnlyList<string> values = GameObjectPropertyReader.Tuple(dome, key, 2, 3)
                    ?? throw new InvalidDataException($"Sky '{dome.Name}' has empty '{key}'");
                layers.Add(new SkyLayer(index, values[0], values[1], values.Count == 3 ? values[2] : null));
            }
            return layers.OrderBy(layer => layer.Index).ToList();
        }

        private static string TextureForDome(string dome, List<SkyLayer> layers, string cycle)
        {
            string? zoneMaterial = layers
                .Where(layer => layer.Kind.Equals("mesh", StringComparison.OrdinalIgnoreCase)
                    && layer.Name.Equals("skydome1", StringComparison.OrdinalIgnoreCase))
                .Select(layer => layer.Argument)
                .FirstOrDefault(argument => !string.IsNullOrEmpty(argument));
            if (zoneMaterial != null && zoneMaterial.StartsWith("map", StringComparison.OrdinalIgnoreCase))
            {
                string candidate = "skydome" + zoneMaterial.Substring(3);
                if (SkyTextures.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
            }
            if (SkyTextures.Contains(dome, StringComparer.OrdinalIgnoreCase)) return dome;
            return TextureForCycle(cycle, dome);
        }

        private static string TextureForCycle(string cycle, string dome = "")
        {
            bool Has(string value) => cycle.Contains(value, StringComparison.OrdinalIgnoreCase)
                || dome.Contains(value, StringComparison.OrdinalIgnoreCase);
            if (Has("artic") || Has("arctic") || dome.Equals("skydome2", StringComparison.OrdinalIgnoreCase))
                return "skyartic";
            if (Has("desert")
                || dome.Equals("skydome1", StringComparison.OrdinalIgnoreCase)
                || dome.Equals("skydome4", StringComparison.OrdinalIgnoreCase)
                || dome.Equals("skydome8", StringComparison.OrdinalIgnoreCase))
                return "skydeserta";
            if (dome.Equals("skydome3", StringComparison.OrdinalIgnoreCase)) return "skyplains_layera";
            return "skydome20";
        }

        public static readonly string[] SkyTextures =
        {
            "skydome20", "skyartic", "skydeserta", "skydome5", "skydome10", "skydome11",
            "skydome12", "skydome13", "skydome16", "skyplains_layera", "layer2_sky1"
        };

        private void ParseLights(byte[] data)
        {
            foreach (NativeLightDefinition light in NativeLightCatalog.Parse(data))
            {
                NativeVector3? nativeDirection = light.Direction;
                Vector3 direction = nativeDirection == null ? default : new Vector3(
                    checked((float)nativeDirection.X),
                    checked((float)nativeDirection.Y),
                    checked((float)nativeDirection.Z));
                _lights[light.Name] = new SkyLight(
                    Rgb(light.FogColor),
                    Rgb(light.Ambient),
                    Rgb(light.Diffuse),
                    direction,
                    light.Name,
                    "",
                    checked((float)(light.Yon ?? 0)),
                    checked((float)(light.FogStart ?? 0)),
                    checked((float)(light.FogEnd ?? 1)));
            }
        }

        private void ParseCycles(byte[] data)
        {
            foreach (NativeTimeOfDayCycle cycle in NativeTimeOfDayCatalog.Parse(data))
            {
                string? missing = cycle.Keyframes.Select(frame => frame.Light)
                    .FirstOrDefault(light => !_lights.ContainsKey(light));
                if (missing != null)
                    throw new InvalidDataException(
                        $"time-of-day cycle '{cycle.Name}' references missing light '{missing}'");
                List<CycleKeyframe> frames = cycle.Keyframes.Select(frame => new CycleKeyframe(
                        checked((float)frame.Hour),
                        checked((float)frame.Blend),
                        frame.Light))
                    .OrderBy(frame => frame.Hour)
                    .ToList();
                if (frames.Count > 0) _cycles[cycle.Name] = frames;
            }
        }

        private static Vector3 Rgb(string? hex)
        {
            if (string.IsNullOrEmpty(hex)) return default;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                throw new InvalidDataException($"invalid authored light color '{hex}'");
            value &= 0xFFFFFF;
            return new Vector3(
                ((value >> 16) & 0xFF) / 255f,
                ((value >> 8) & 0xFF) / 255f,
                (value & 0xFF) / 255f);
        }

        private static float Float(string value) =>
            float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}

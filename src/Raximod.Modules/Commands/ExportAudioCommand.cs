using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Databases;

namespace Raximod.Modules;

public static class ExportAudioCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: ExportAudio <PlanetSide-directory> <output-directory>");
            return 1;
        }

        string planetside = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        string effectsOutput = Path.Combine(output, "sfx");
        string musicOutput = Path.Combine(output, "music");
        Directory.CreateDirectory(effectsOutput);
        Directory.CreateDirectory(musicOutput);
        var effects = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var music = new List<object>();
        var ambientSources = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var soiRadii = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var gameObjectProvenance = new Dictionary<string, GameObjectDb.GameObjectProvenance>(
            StringComparer.OrdinalIgnoreCase);
        GameObjectDb.DecoderDiagnostics? gameObjectDiagnostics = null;

        string[] banks =
        [
            "3dsounds",
            "patch1/patch1",
            "patch2/patch2",
            "patch3/patch3",
            "patch4/patch4",
            "patch5/patch5",
            "expansion1/expansion1",
        ];
        foreach (string bank in banks)
        {
            string indexPath = Path.Combine(planetside, bank + ".idx");
            string wavePath = Path.Combine(planetside, bank + ".wav");
            if (!File.Exists(indexPath) || !File.Exists(wavePath)) continue;
            byte[] source = File.ReadAllBytes(wavePath);
            int dataOffset = FindWaveDataOffset(source);
            foreach (string line in File.ReadLines(indexPath))
            {
                string[] fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 5 || !long.TryParse(fields[^4], out long offset) ||
                    !int.TryParse(fields[^3], out int length) || !int.TryParse(fields[^2], out int sampleRate) ||
                    !short.TryParse(fields[^1], out short bits)) continue;
                string name = string.Join(' ', fields[..^4]);
                if (offset < 0 || length <= 0 || dataOffset + offset + length > source.LongLength)
                    throw new InvalidDataException($"Audio entry '{name}' exceeds {bank}.wav");
                string safeName = Path.GetFileName(name).ToLowerInvariant();
                string target = Path.Combine(effectsOutput, safeName);
                File.WriteAllBytes(target, MakePcmWave(source.AsSpan(dataOffset + (int)offset, length), sampleRate, bits));
                effects[safeName] = new { uri = $"sfx/{safeName}", sourceBank = bank, offset, length, sampleRate, bits };
            }
            Console.WriteLine($"decoded {bank}");
        }

        IEnumerable<string> audioPaks = Directory.EnumerateFiles(planetside, "audio2d*.pak", SearchOption.AllDirectories);
        string nativeUiSounds = Path.Combine(planetside, "pack", "waves.pak");
        if (File.Exists(nativeUiSounds)) audioPaks = audioPaks.Append(nativeUiSounds);
        foreach (string pakPath in audioPaks)
        {
            PakArchive archive = PakArchive.Load(File.ReadAllBytes(pakPath));
            foreach (PakEntry entry in archive.Entries.Where(entry => entry.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
            {
                string safeName = Path.GetFileName(entry.Name).ToLowerInvariant();
                // waves.pak is the base UI/effect library. Patch archives and decoded audio banks
                // take precedence when they provide a newer file with the same name.
                if (pakPath == nativeUiSounds && effects.ContainsKey(safeName)) continue;
                File.WriteAllBytes(Path.Combine(effectsOutput, safeName), archive.Extract(entry.Name));
                effects[safeName] = new { uri = $"sfx/{safeName}", sourceBank = Path.GetRelativePath(planetside, pakPath) };
            }
            Console.WriteLine($"extracted {Path.GetRelativePath(planetside, pakPath)}");
        }

        string uiOutput = Path.Combine(planetside, "ui.pak-out");
        if (Directory.Exists(uiOutput))
        foreach (string source in Directory.EnumerateFiles(uiOutput, "*.wav", SearchOption.AllDirectories))
        {
            string safeName = Path.GetFileName(source).ToLowerInvariant();
            File.Copy(source, Path.Combine(effectsOutput, safeName), overwrite: true);
            effects[safeName] = new { uri = $"sfx/{safeName}", sourceBank = Path.GetRelativePath(planetside, source) };
        }

        foreach (string sourceDirectory in new[] { Path.Combine(planetside, "music"), Path.Combine(planetside, "expansion1") })
        {
            if (!Directory.Exists(sourceDirectory)) continue;
            foreach (string source in Directory.EnumerateFiles(sourceDirectory, "*.mp3", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(source);
                File.Copy(source, Path.Combine(musicOutput, name), overwrite: true);
                music.Add(new { name = Path.GetFileNameWithoutExtension(name), uri = $"music/{name}", source = Path.GetRelativePath(planetside, source) });
            }
        }

        string gameObjectsPath = Path.Combine(planetside, "startup.pak-out", "game_objects.adb");
        if (File.Exists(gameObjectsPath))
        {
            GameObjectDb gameObjectDatabase = GameObjectDb.Parse(File.ReadAllBytes(gameObjectsPath));
            gameObjectDiagnostics = gameObjectDatabase.Diagnostics;
            foreach (GameObjectDb.GameObject gameObject in gameObjectDatabase.ResolvedObjects)
            {
                string? Value(string key) => GameObjectPropertyReader.Scalar(gameObject, key);
                float? Number(string key) => float.TryParse(Value(key), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : null;
                float? soiRadius = Number("soi_radius") ?? Number("SOIradius");
                if (soiRadius is > 0)
                {
                    soiRadii[gameObject.Name] = soiRadius.Value;
                    gameObjectProvenance[gameObject.Name] = gameObject.Provenance;
                }
                string? file = Value("ambient_sound") ?? Value("ambient_noise");
                if (file is null || !file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) continue;
                ambientSources[gameObject.Name] = new
                {
                    file = Path.GetFileName(file).ToLowerInvariant(),
                    minimumRange = Number("ambient_sound_minrange") ?? Number("ambient_noise_rad") ?? 1f,
                    maximumRange = Number("ambient_sound_maxrange") ?? Number("ambient_noise_rad") ?? 20f,
                    volume = Number("ambient_sound_volume") ?? 1f,
                    indoorTest = string.Equals(Value("ambient_sound_indoor_test"), "true", StringComparison.OrdinalIgnoreCase),
                };
                gameObjectProvenance[gameObject.Name] = gameObject.Provenance;
            }
        }

        var manifest = new
        {
            schemaVersion = 2,
            gameObjects = gameObjectDiagnostics,
            gameObjectProvenance = gameObjectProvenance.OrderBy(item => item.Key)
                .ToDictionary(item => item.Key, item => item.Value),
            effects = effects.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value),
            ambientSources = ambientSources.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value),
            soiRadii = soiRadii.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value),
            music = music.ToArray(),
        };
        string manifestPath = Path.Combine(output, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"wrote {effects.Count} sound effects and {music.Count} music tracks -> {manifestPath}");
        return 0;

        static int FindWaveDataOffset(byte[] bytes)
        {
            if (bytes.Length < 12 || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF")
                throw new InvalidDataException("Audio bank is not a RIFF/WAVE file");
            int offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
                int length = BitConverter.ToInt32(bytes, offset + 4);
                if (id == "data") return offset + 8;
                offset += 8 + length + (length & 1);
            }
            throw new InvalidDataException("Audio bank has no data chunk");
        }

        static byte[] MakePcmWave(ReadOnlySpan<byte> samples, int sampleRate, short bits)
        {
            const short channels = 1;
            short blockAlign = (short)(channels * bits / 8);
            int byteRate = sampleRate * blockAlign;
            using var stream = new MemoryStream(44 + samples.Length);
            using var writer = new BinaryWriter(stream);
            writer.Write("RIFF"u8);
            writer.Write(36 + samples.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bits);
            writer.Write("data"u8);
            writer.Write(samples.Length);
            writer.Write(samples);
            return stream.ToArray();
        }
    }
}

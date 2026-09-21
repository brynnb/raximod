using System;
using System.IO;
using System.Text;

namespace Raximod.EngineAssets.Databases;

/// <summary>Dedicated lightning path, read only after the executable SHA check.</summary>
public static class NativeWeatherLightning
{
    public sealed record Definition(string[] Textures, double MinimumStrength,
        double[] CheckIntervalMs, double[] EventThreshold, double[] CloseThreshold,
        int EventModulus, int CloseModulus, int CloseCategory, int DistantCategory,
        double DurationSeconds, double BoltLifetimeScale, double Size, double HalfSizeScale,
        double DiagonalScale, double[] HorizontalRange, double[] VerticalRange, double[] DistanceRange,
        uint RandomMultiplier, uint RandomIncrement, int RandomShift, int RandomMask,
        uint DirectionMultiplier, uint DirectionIncrement, double DirectionScale,
        double FlashDepth);

    internal static Definition Read(Func<int, int, byte[]> read)
    {
        double Single(int address) => BitConverter.ToSingle(read(address, 4));
        double Double(int address) => BitConverter.ToDouble(read(address, 8));
        uint Immediate(int address, byte[] prefix)
        {
            if (!read(address, prefix.Length).AsSpan().SequenceEqual(prefix))
                throw new InvalidDataException($"Unexpected lightning instruction at 0x{address:x}");
            return BitConverter.ToUInt32(read(address + prefix.Length, 4));
        }
        int BytePush(int address)
        {
            if (read(address, 1)[0] != 0x6a) throw new InvalidDataException("Expected lightning byte push");
            return read(address + 1, 1)[0];
        }
        double FloatPush(int address) => BitConverter.UInt32BitsToSingle(Immediate(address, [0x68]));
        string Texture(int address)
        {
            int pointer = checked((int)Immediate(address, [0x68]));
            byte[] bytes = read(pointer, 32); int end = Array.IndexOf(bytes, (byte)0);
            if (end < 1) throw new InvalidDataException("Unterminated lightning texture");
            string name = Encoding.ASCII.GetString(bytes, 0, end);
            if (!name.EndsWith(".dds", StringComparison.Ordinal)) throw new InvalidDataException("Invalid lightning texture");
            return name[..^4];
        }
        // Pairs are intercept/slope in (1-strength), not endpoint guesses.
        // Keep the native 15-bit CRT result before modulus (its bias matters).
        return new([Texture(0x8747a7), Texture(0x874892)], Double(0xb95590),
            [Single(0xb7dbcc), Single(0xb7dbcc)], [Single(0xb7f440), Single(0xb99cc4)],
            [Single(0xb99cbc), Single(0xb99cc0)],
            checked((int)Immediate(0x863232, [0xb9])), checked((int)Immediate(0x863249, [0xb9])),
            BytePush(0x8634d5), BytePush(0x8634dd), FloatPush(0x8634a5), Single(0xb82794),
            FloatPush(0x863496), Single(0xb7dbe0), Single(0xb99fa0),
            [-Single(0xb7d948), Math.Abs(Double(0xb7dc00))],
            [Single(0xb7d960), (double)(float)Math.Abs(Double(0xb99cb0))],
            [Single(0xb7d948), Math.Abs(Double(0xb7dc00))],
            Immediate(0xa6c824, [0x69, 0xc9]), Immediate(0xa6c82a, [0x81, 0xc1]),
            read(0xa6c837, 1)[0], checked((int)Immediate(0xa6c838, [0x25])),
            Immediate(0x863290, [0x68]), Immediate(0x8632a2, [0x05]), Single(0xb99cb8),
            // XYZRHW flash vertices all use the same stored .9 depth.
            BitConverter.UInt32BitsToSingle(Immediate(0x879e2b, [0xc7, 0x44, 0x24, 0x18])));
    }
}

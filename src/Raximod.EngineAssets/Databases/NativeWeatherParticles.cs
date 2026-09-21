using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Raximod.EngineAssets.Databases;

/// <summary>Dedicated weather particle constructors in the verified retail build.
/// Coordinates remain native Z-up; these are not env_* effect graph aliases.</summary>
public static class NativeWeatherParticles
{
    public sealed record Definition(string Kind, string Texture, int Capacity, bool CameraRelative,
        double[][] Position, double[][] Velocity, double[] Size, double[] Lifetime,
        double QuadHalfSizeScale, double StreakLength, int SourceBlend, int DestinationBlend,
        double[] Tint, string Constructor, string Allocation, string Draw, string RenderState);

    // Caller MUST verify NativeWeatherData.ExecutableSha256 before providing reads.
    internal static IReadOnlyList<Definition> Read(Func<int, int, byte[]> read)
    {
        double Single(int address) => BitConverter.ToSingle(read(address, 4));
        int Push(int address)
        {
            byte[] bytes = read(address, 5);
            return bytes[0] switch
            {
                0x68 => BitConverter.ToInt32(bytes, 1),
                0x6a => (sbyte)bytes[1],
                _ => throw new InvalidDataException($"Expected native weather push at 0x{address:x}")
            };
        }
        string Texture(int address)
        {
            byte[] bytes = read(Push(address), 64);
            int end = Array.IndexOf(bytes, (byte)0);
            if (end <= 4) throw new InvalidDataException("Invalid native weather texture");
            string name = Encoding.ASCII.GetString(bytes, 0, end);
            if (!name.EndsWith(".dds", StringComparison.Ordinal))
                throw new InvalidDataException($"Unsupported native weather texture {name}");
            return name[..^4];
        }
        Definition Build(string kind, int start, int end, int allocation, int relative,
            int texture, int blend, int destination, string draw, string renderState)
        {
            var fields = Constructor(read(start, end - start), start);
            double Field(int offset) => fields.TryGetValue(offset, out int bits)
                ? BitConverter.Int32BitsToSingle(bits)
                : throw new InvalidDataException($"Missing {kind} particle field +0x{offset:x}");
            double[] Pair(int offset) => [Field(offset), Field(offset + 4)];
            return new(kind, Texture(texture), Push(allocation), Push(relative) != 0,
                [Pair(0x48), Pair(0x50), Pair(0x58)], [Pair(0x60), Pair(0x68), Pair(0x70)],
                Pair(0x78), Pair(0x80), Single(kind == "rain" ? 0xb7dbe0 : 0xb99fa0),
                kind == "rain" ? 2 * Single(0xb7dbe0) : 0, Push(blend), Push(destination),
                kind == "sand" ? [BitConverter.Int32BitsToSingle(Push(0x87cca5)),
                    BitConverter.Int32BitsToSingle(Push(0x87cc9c)),
                    BitConverter.Int32BitsToSingle(Push(0x87cc97)),
                    BitConverter.Int32BitsToSingle(Push(0x87cc92))] : [1, 1, 1, 1],
                $"0x{start:x}-0x{end:x}", $"0x{allocation:x}", draw, renderState);
        }
        return [
            Build("rain", 0x87cd10, 0x87cd8f, 0x876c30, 0x876c2a, 0x876b86,
                0x87da66, 0x87da73, "0x87af90", "0x87d910"),
            Build("snow", 0x87b440, 0x87b4b6, 0x876df0, 0x876dea, 0x876d46,
                0x87bfb6, 0x87bfc3, "0x87aa80", "0x87be60"),
            Build("sand", 0x87c060, 0x87c0d0, 0x876240, 0x87623a, 0x876196,
                0x87cbc6, 0x87cbd3, "0x87cc60 -> 0x87aa80", "0x87ca70")
        ];
    }

    private static Dictionary<int, int> Constructor(byte[] code, int start)
    {
        // These three straight-line constructors share one grammar. Account for
        // every instruction; reject unknown writes instead of scanning constants.
        var fields = new Dictionary<int, int>();
        int?[] registers = new int?[8];
        int offset = 0;
        int Byte() => code[offset++];
        int Int() { int value = BitConverter.ToInt32(code, offset); offset += 4; return value; }
        while (offset < code.Length)
        {
            int at = start + offset, op = Byte();
            if (op is 0x56 or 0x5e) continue; // preserve ESI
            if (op == 0xe8)
            {
                int target = at + 5 + Int();
                if (target != 0x87db00) throw new InvalidDataException("Unexpected weather base constructor");
                registers[0] = registers[1] = registers[2] = null;
                continue;
            }
            if (op is >= 0xb8 and <= 0xbf) { registers[op - 0xb8] = Int(); continue; }
            if (op is 0x89 or 0x8b or 0xc7)
            {
                int modrm = Byte(), mode = modrm >> 6, reg = (modrm >> 3) & 7, rm = modrm & 7;
                if (mode == 3 && op is 0x89 or 0x8b)
                {
                    if (op == 0x89) registers[rm] = registers[reg]; else registers[reg] = registers[rm];
                    continue;
                }
                if (rm != 6 || mode == 3 || op == 0x8b || (op == 0xc7 && reg != 0))
                    throw new InvalidDataException($"Unexpected weather constructor operand at 0x{at:x}");
                int field = mode == 0 ? 0 : mode == 1 ? (sbyte)Byte() : Int();
                int value = op == 0xc7 ? Int() : registers[reg]
                    ?? throw new InvalidDataException($"Unknown weather register at 0x{at:x}");
                fields.Add(field, value);
                continue;
            }
            if (op == 0xc3 && offset == code.Length && fields.Count == 17) return fields;
            throw new InvalidDataException($"Unsupported weather constructor instruction at 0x{at:x}");
        }
        throw new InvalidDataException("Weather particle constructor has no verified return");
    }
}

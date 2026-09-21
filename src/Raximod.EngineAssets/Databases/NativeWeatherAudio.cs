using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Raximod.EngineAssets.Databases;

/// <summary>Weather sound registrations and band/fade constants in the verified retail build.</summary>
public static class NativeWeatherAudio
{
    public sealed record Cue(int Category, int Band, string File, string Instruction);
    public sealed record Definition(IReadOnlyList<Cue> Cues, double[] Thresholds,
        double CrossfadeSeconds, int RainCategory, int SnowCategory, int SandCategory,
        double ShelteredGain, double ShelterFadeSeconds, double ExteriorFadeSeconds);

    internal static Definition Read(Func<int, int, byte[]> read)
    {
        double Single(int address) => BitConverter.ToSingle(read(address, 4));
        int Push(int address)
        {
            byte[] bytes = read(address, 5);
            return bytes[0] switch { 0x68 => BitConverter.ToInt32(bytes, 1), 0x6a => (sbyte)bytes[1],
                _ => throw new InvalidDataException($"Expected weather audio push at 0x{address:x}") };
        }
        // EDI is zeroed at 0x8625bc; EBP is assigned 3 at 0x8626af.
        // All later category/band assignments are interpreted, including repeated
        // sand/thunder registrations. Never deduplicate them by category or band.
        if (!read(0x8625bc, 2).AsSpan().SequenceEqual(new byte[] { 0x33, 0xff })
            || read(0x8626af, 5)[0] != 0xbd)
            throw new InvalidDataException("Unsupported weather audio initializer prelude");
        var cues = Registrations(read, BitConverter.ToInt32(read(0x8626b0, 4)));
        return new(cues, [Single(0xb7d9a8), Single(0xb81a20), Single(0xb82794)],
            Single(0xb99c9c), Push(0x8644f4), Push(0x8644b8), Push(0x86452d),
            BitConverter.Int32BitsToSingle(Push(0x8631ec)), Push(0x8631e5) / 1000d,
            Push(0x8631f7) / 1000d);
    }

    private static IReadOnlyList<Cue> Registrations(Func<int, int, byte[]> read, int initialEbp)
    {
        const int start = 0x8627cd, end = 0x862c81, stack = 0x100000;
        byte[] code = read(start, end - start);
        int offset = 0;
        int?[] regs = [null, null, null, null, stack, initialEbp, null, 0];
        var memory = new Dictionary<int, int?>();
        var strings = new Dictionary<int, string>();
        var cues = new List<Cue>();
        int Byte() => code[offset++];
        int Int() { int value = BitConverter.ToInt32(code, offset); offset += 4; return value; }
        int Required(int? value) => value ?? throw new InvalidDataException($"Unresolved weather audio operand at 0x{start + offset:x}");
        int? Load(int? address) => address.HasValue ? memory.GetValueOrDefault(address.Value) : null;
        void Push(int? value) { regs[4] -= 4; memory[Required(regs[4])] = value; }
        (int Mode, int Reg, int Rm, int? Address) Operand()
        {
            int bits = Byte(), mode = bits >> 6, reg = (bits >> 3) & 7, rm = bits & 7;
            int? address = regs[rm];
            if (mode != 3)
            {
                if (rm == 4 && Byte() != 0x24)
                    throw new InvalidDataException("Unexpected weather audio SIB operand");
                if (mode == 0 && rm == 5) address = Int();
                else if (mode == 1) address += (sbyte)Byte();
                else if (mode == 2) address += Int();
            }
            return (mode, reg, rm, address);
        }
        while (offset < code.Length)
        {
            int at = start + offset, op = Byte();
            if (op is >= 0xb8 and <= 0xbf) { regs[op - 0xb8] = Int(); continue; }
            if (op is >= 0x50 and <= 0x57) { Push(regs[op - 0x50]); continue; }
            if (op == 0x68) { Push(Int()); continue; }
            if (op == 0xe8)
            {
                int target = at + 5 + Int();
                int? result = null;
                if (target == 0x4048c0) // String construction: cdecl, caller pops arguments.
                {
                    int dest = Required(Load(regs[4])), text = Required(Load(regs[4] + 4));
                    byte[] bytes = read(text, 64); int length = Array.IndexOf(bytes, (byte)0);
                    if (length <= 4) throw new InvalidDataException("Invalid weather audio string");
                    strings[dest] = Encoding.ASCII.GetString(bytes, 0, length);
                    result = dest;
                }
                else if (target == 0x8647c0) // Lookup(category*, band*, string*), callee pops 12.
                {
                    int category = Required(Load(Load(regs[4]))), band = Required(Load(Load(regs[4] + 4)));
                    string file = strings[Required(Load(regs[4] + 8))];
                    if (!file.EndsWith(".wav", StringComparison.Ordinal))
                        throw new InvalidDataException($"Invalid weather audio file {file}");
                    cues.Add(new(category, band, file, $"0x{at:x}"));
                    regs[4] += 12;
                }
                else if (target is not (0x8646a0 or 0x4fb1d0))
                    throw new InvalidDataException($"Unexpected weather audio call at 0x{at:x}");
                regs[0] = result; regs[1] = regs[2] = null;
                continue;
            }
            if (op is 0x89 or 0x8b or 0x8d or 0xc7 or 0x83)
            {
                var (mode, reg, rm, address) = Operand();
                if (op == 0x83 && mode == 3 && reg == 0) regs[rm] += (sbyte)Byte();
                else if (op == 0x8b) regs[reg] = mode == 3 ? regs[rm] : Load(address);
                else if (op == 0x8d && mode != 3) regs[reg] = address;
                else if (op == 0x89 || (op == 0xc7 && reg == 0))
                {
                    int? value = op == 0xc7 ? Int() : regs[reg];
                    if (mode == 3) regs[rm] = value;
                    else memory[Required(address)] = value;
                }
                else throw new InvalidDataException($"Unexpected weather audio operand at 0x{at:x}");
                continue;
            }
            throw new InvalidDataException($"Unsupported weather audio instruction at 0x{at:x}");
        }
        if (regs[4] != stack || cues.Count != 18)
            throw new InvalidDataException("Incomplete native weather audio registrations");
        return cues;
    }
}

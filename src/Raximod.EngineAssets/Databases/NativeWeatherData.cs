using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Raximod.EngineAssets.Databases;

/// <summary>Weather parameters and continent assignments recovered from the original executable.
/// This reads the identified build's straight-line table initializer, not guessed map-name rules.</summary>
public sealed class NativeWeatherData
{
    public const string ExecutableSha256 = NativeMaterialProfiles.ExecutableSha256;
    public const int InitializerStart = 0x883430, InitializerEnd = 0x884fc9;
    public sealed record Continent(int Id, string Zone, string Map, string Name, int Climate,
        string Precipitation, int ClimateInstruction, int ZoneType, int NativeMapIndex,
        bool AcceptsGlobalStorms);
    public IReadOnlyList<Continent> Continents { get; }
    public double CoordinateExtent { get; }
    public double ByteScale { get; }
    public double RadiusScale { get; }
    public double InnerRadiusFraction { get; }
    public double StrengthThreshold { get; }
    public double StrengthScale { get; }
    public double StrengthOffset { get; }
    public double LogarithmBase { get; }
    public double TransitionSeconds { get; }
    public double RefreshSeconds { get; }
    public double TeleportDistanceSquared { get; }
    public int ClearTransmission { get; }
    public IReadOnlyList<NativeWeatherParticles.Definition> Particles { get; }
    public NativeWeatherAudio.Definition Audio { get; }
    public NativeWeatherClouds.Definition Clouds { get; }
    public NativeWeatherLightning.Definition Lightning { get; }
    public sealed record UnderwaterProfile(string Kind, int Tint, double FarPlane, double TransitionDepth);
    public IReadOnlyList<UnderwaterProfile> Underwater { get; }
    public double UnderwaterFogEnd { get; }
    public sealed record LiquidQuery(double StartOffset, double EndOffset, uint Mask, uint CavernMask, string CavernPrefix);
    public LiquidQuery LocalLiquidQuery { get; }

    private NativeWeatherData(Func<int, int, byte[]> read)
    {
        double Single(int address) => BitConverter.ToSingle(read(address, 4));
        CoordinateExtent = Single(0xb7ecdc);
        ByteScale = Single(0xb7e0c8);
        RadiusScale = Single(0xb99c90);
        InnerRadiusFraction = StrengthThreshold = Single(0xb7d9a8);
        StrengthScale = Single(0xb86ed8);
        StrengthOffset = Single(0xb80760);
        LogarithmBase = BitConverter.ToDouble(read(0xb99cc8, 8));
        TransitionSeconds = Single(0xb99c9c);
        // 0x862f57-0x862faa: resample every 2.5 seconds, or immediately after
        // a >=1000-unit observer displacement. Storm keys select linear mode
        // (0x875530/0x8760f0); 0x875902 evaluates from current to target.
        RefreshSeconds = Single(0xb99c98);
        TeleportDistanceSquared = Single(0xb99ca0);
        ClearTransmission = checked((int)Single(0xb85a6c));
        Particles = NativeWeatherParticles.Read(read);
        Audio = NativeWeatherAudio.Read(read);
        Clouds = NativeWeatherClouds.Read(read);
        Lightning = NativeWeatherLightning.Read(read);
        uint Immediate(int address, byte[] prefix)
        {
            if (!read(address, prefix.Length).AsSpan().SequenceEqual(prefix))
                throw new InvalidDataException($"Unexpected underwater instruction at 0x{address:x}");
            return BitConverter.ToUInt32(read(address + prefix.Length, 4));
        }
        double LocalFloat(int address) => BitConverter.UInt32BitsToSingle(Immediate(address, [0xc7, 0x44, 0x24, 0x18]));
        double Depth(int address) => BitConverter.UInt32BitsToSingle(Immediate(address, [0xc7, 0x44, 0x24, 0x14]));
        // 0x8605dc -> 0x863910 passes color, yon, normalized fog start/end.
        // Deep water is yon=100, fogEnd=.1 (10 metres), not 100-metre fog.
        Underwater = [
            new("water", checked((int)Immediate(0x8639e5, [0xbf])), LocalFloat(0x8639ea), Depth(0x8639f2)),
            new("lava", checked((int)Immediate(0x8639fc, [0xbf])), LocalFloat(0x863a01), Depth(0x863a09))
        ];
        UnderwaterFogEnd = Single(0xb7d960);
        // 0x86393c-0x863995: upward segment from observer Z-1 to Z+100.
        // 0x86185d sets mode 0x1000000 for the original cavern prefix and clears
        // it on leaving. This is NOT a debug flag: 0x86397b adds terrain/objects.
        int prefixAddress = checked((int)Immediate(0x861861, [0x68]));
        byte[] prefixBytes = read(prefixAddress, 4);
        if (prefixBytes[3] != 0 || Immediate(0x86187e, [0x68]) != Immediate(0x863945, [0x68]))
            throw new InvalidDataException("Unexpected native cavern liquid query mode");
        LocalLiquidQuery = new(-Single(0xb7d948), Single(0xb7f680), Immediate(0x863969, [0xbf]),
            Immediate(0x86397b, [0xbf]), Encoding.ASCII.GetString(prefixBytes, 0, 3));

        var stores = ReadInitializer(read(InitializerStart, InitializerEnd - InitializerStart));
        var continents = new List<Continent>();
        int Required(int address) => stores.TryGetValue(address, out var store) && store.Value is int value
            ? value : throw new InvalidDataException($"Unresolved native continent field 0x{address:x}");
        string Text(int address)
        {
            byte[] text = read(Required(address), 128);
            int length = Array.IndexOf(text, (byte)0);
            if (length <= 0 || text.Take(length).Any(value => value < 32 || value > 126))
                throw new InvalidDataException($"Invalid native continent string at 0x{address:x}");
            return Encoding.ASCII.GetString(text, 0, length);
        }
        // 0x885270 traverses exactly 33 entries, stride 0x3c, reading +0x20.
        // 0x86436b selects SnowStorm for 3, SandStorm for 0, RainStorm otherwise.
        for (int index = 0; index < 33; index++)
        {
            int address = 0xd326e0 + index * 0x3c;
            int id = Required(address + 4), climate = Required(address + 0x20);
            int zoneType = Required(address + 0x1c), mapIndex = Required(address + 0x14);
            if (id != index + 1 || climate is < 0 or > 3)
                throw new InvalidDataException($"Unsupported continent ID/climate at 0x{address:x}");
            continents.Add(new(id, Text(address), Text(address + 8), Text(address + 0x10), climate,
                climate == 3 ? "snow" : climate == 0 ? "sand" : "rain", stores[address + 0x20].Instruction,
                zoneType, mapIndex, zoneType is not (2 or 5) && mapIndex != 0));
        }
        Continents = continents;
    }

    public static NativeWeatherData Read(string executable)
    {
        byte[] bytes = File.ReadAllBytes(executable);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != ExecutableSha256)
            throw new InvalidDataException($"Unsupported weather executable SHA-256: {hash}");
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        int imageBase = checked((int)(pe.PEHeaders.PEHeader?.ImageBase
            ?? throw new InvalidDataException("Weather input has no PE image header")));
        return new NativeWeatherData((address, count) =>
            pe.GetSectionData(checked(address - imageBase)).GetContent(0, count).ToArray());
    }

    private sealed record Store(int? Value, int Instruction);

    // Deliberately limited to the checksum-verified initializer, not a general
    // disassembler. Decode every instruction boundary; never search arbitrary
    // bytes for something resembling a store. Calls only affect volatile registers
    // here. Their rectangle/string-object results are NOT reconstructed or claimed.
    private static Dictionary<int, Store> ReadInitializer(byte[] code)
    {
        int?[] registers = new int?[8]; // eax ecx edx ebx esp ebp esi edi
        var stores = new Dictionary<int, Store>();
        int offset = 0;
        int Byte() => code[offset++];
        int Int32() { int value = BitConverter.ToInt32(code, offset); offset += 4; return value; }
        (int Mode, int Register, int Rm, int? Address) Operand()
        {
            int modrm = Byte(), mode = modrm >> 6, reg = (modrm >> 3) & 7, rm = modrm & 7;
            int? address = null;
            if (mode != 3)
            {
                if (rm == 4)
                {
                    int sib = Byte();
                    if (mode == 0 && (sib & 7) == 5) Int32();
                }
                else if (mode == 0 && rm == 5) address = Int32();
                if (mode == 1) Byte();
                else if (mode == 2) Int32();
            }
            return (mode, reg, rm, address);
        }
        void Write(int mode, int rm, int? address, int? value, int instruction, bool lowByte = false)
        {
            if (mode == 3)
            {
                if (lowByte) throw new InvalidDataException("Unexpected byte-register initializer write");
                registers[rm] = value;
            }
            else if (address is int target)
                stores[target] = new(lowByte && value.HasValue ? value.Value & 255 : value, instruction);
        }
        while (offset < code.Length)
        {
            int instruction = InitializerStart + offset, opcode = Byte();
            if (opcode is >= 0x50 and <= 0x57) continue; // push register
            if (opcode is >= 0x58 and <= 0x5f) { registers[opcode - 0x58] = null; continue; }
            if (opcode is >= 0xb8 and <= 0xbf) { registers[opcode - 0xb8] = Int32(); continue; }
            if (opcode == 0x68) { Int32(); continue; }
            if (opcode == 0xe8) { Int32(); registers[0] = registers[1] = registers[2] = null; continue; }
            if (opcode == 0xa3) { stores[Int32()] = new(registers[0], instruction); continue; }
            if (opcode == 0xc3 && offset == code.Length) return stores;
            if (opcode is 0x33 or 0x83 or 0x88 or 0x89 or 0x8b or 0x8d or 0xc6 or 0xc7)
            {
                var (mode, reg, rm, address) = Operand();
                switch (opcode)
                {
                    case 0x33:
                        registers[reg] = mode == 3 && reg == rm ? 0 : null;
                        break;
                    case 0x83:
                        Byte();
                        if (mode == 3) registers[rm] = null;
                        break;
                    case 0x88:
                    case 0x89:
                        if (opcode == 0x88 && reg > 3)
                            throw new InvalidDataException("Unexpected high-byte initializer store");
                        Write(mode, rm, address, registers[reg], instruction, opcode == 0x88);
                        break;
                    case 0x8b:
                        registers[reg] = mode == 3 ? registers[rm] : null;
                        break;
                    case 0x8d:
                        registers[reg] = null;
                        break;
                    case 0xc6:
                    case 0xc7:
                        if (reg != 0) throw new InvalidDataException("Unexpected initializer immediate operation");
                        Write(mode, rm, address, opcode == 0xc7 ? Int32() : Byte(), instruction, opcode == 0xc6);
                        break;
                }
                continue;
            }
            throw new InvalidDataException($"Unsupported weather initializer instruction 0x{opcode:x} at 0x{instruction:x}");
        }
        throw new InvalidDataException("Weather initializer did not end at the verified return");
    }
}

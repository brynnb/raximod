using System;
using System.IO;
using System.Buffers.Binary;

namespace Raximod.EngineAssets.Maps;

/// <summary>Original .trn top-surface grid, including roofs. Every packed bit is retained.</summary>
public sealed class TerrainMap
{
    public const int Size = 2048, CellSize = 4, ByteLength = Size * Size * 2;
    public ushort[] Cells { get; }
    private TerrainMap(ushort[] cells) => Cells = cells;

    public static TerrainMap Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength) throw new InvalidDataException($"Invalid TRN length: {bytes.Length}, expected {ByteLength}");
        var cells = new ushort[Size * Size];
        for (int i = 0; i < cells.Length; i++) cells[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]);
        return new(cells);
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[ByteLength];
        for (int i = 0; i < Cells.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), Cells[i]);
        return bytes;
    }

    public double HeightAt(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("Non-finite TRN coordinates");
        // 0x8c2b20 truncates before signed division, including negative fractions.
        double column = Math.Truncate(Math.Truncate(x) / CellSize), row = Math.Truncate(Math.Truncate(y) / CellSize);
        if (column < 0 || row < 0 || column >= Size || row >= Size) return 0;
        return Cells[(int)row * Size + (int)column] & 0x3ff; // 0x8c2a90: no vertical scale.
    }
}

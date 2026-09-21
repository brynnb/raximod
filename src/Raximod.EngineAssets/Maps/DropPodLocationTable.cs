using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Raximod.EngineAssets.Maps;

/// <summary>Retail .droppod terrain-following lookup. Each cell stores a landing XY,
/// not height or an allowed/forbidden bit. The original byte layout is retained.</summary>
public sealed class DropPodLocationTable
{
    private readonly byte[] bytes;
    public int Extent { get; }
    public int CellSize { get; }
    public int Side { get; }
    public int CellCount => Side * Side;
    public int AccountedBytes => bytes.Length;

    private DropPodLocationTable(byte[] bytes, int extent, int cellSize)
    {
        this.bytes = bytes;
        Extent = extent;
        CellSize = cellSize;
        Side = extent / cellSize;
    }

    public static DropPodLocationTable Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < 8) throw new InvalidDataException("Drop location table has a truncated header");
        int extent = BinaryPrimitives.ReadInt32LittleEndian(source);
        int step = BinaryPrimitives.ReadInt32LittleEndian(source[4..]);
        if (extent <= 0 || step <= 0 || extent % step != 0)
            throw new InvalidDataException($"Invalid drop location extent/cell size: {extent}/{step}");
        long side = extent / step;
        // Compare before allocating or multiplying by the eight-byte entry width.
        if (side * side != (source.Length - 8L) / 8 || (source.Length - 8) % 8 != 0)
            throw new InvalidDataException("Drop location cell count does not account for the complete file");
        for (int offset = 8; offset < source.Length; offset += 4)
        {
            float coordinate = BinaryPrimitives.ReadSingleLittleEndian(source[offset..]);
            if (!float.IsFinite(coordinate) || coordinate < 0 || coordinate >= extent)
                throw new InvalidDataException($"Invalid drop location coordinate at byte {offset}: {coordinate}");
        }
        return new DropPodLocationTable(source.ToArray(), extent, step);
    }

    public Vector2 Cell(int x, int y)
    {
        if ((uint)x >= Side || (uint)y >= Side) throw new ArgumentOutOfRangeException(nameof(x));
        int offset = 8 + (y * Side + x) * 8;
        return new Vector2(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset)),
            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 4)));
    }

    public Vector2 Lookup(Vector2 nativePosition)
    {
        if (!float.IsFinite(nativePosition.X) || !float.IsFinite(nativePosition.Y))
            throw new ArgumentOutOfRangeException(nameof(nativePosition));
        // planetside.exe 0x8c1040: clamp, float-to-integer, integer division,
        // then y * side + x. Startup sets x87 _RC_CHOP (0x4026bf), so using
        // nearest rounding at a cell boundary selects a different landing point.
        // This static lookup is not authorization to launch outside map bounds.
        int x = (int)Math.Clamp(nativePosition.X, 0, Extent - 1) / CellSize;
        int y = (int)Math.Clamp(nativePosition.Y, 0, Extent - 1) / CellSize;
        return Cell(x, y);
    }

    public byte[] Encode() => (byte[])bytes.Clone();
}

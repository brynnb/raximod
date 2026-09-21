using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Raximod.EngineAssets.Databases;

/// <summary>Dedicated cloud-web data, kept separate from static sky and effect meshes.</summary>
public static class NativeWeatherClouds
{
    public const string GeometrySha256 = "df57ab5dc51bd7c9b5899d71375cced324817b1c717ad882ac16c9b3b404e3a5";
    public sealed record Mesh(string Name, double[][] Positions, double[][] Uvs, double[] Alpha, int[] Indices);
    public sealed record Definition(string FullTexture, string PartlyTexture, double RadiusBase,
        double RadiusByteScale, double Falloff, double VertexPositionScale, double[] Scale,
        double VerticalOffset, double DriftRate, double[] DefaultWind, double DefaultWindScale,
        double WindTransitionSeconds, double WindMotionScale, double[] ObserverMotionScale,
        string[] AlphaOperations);

    // Invoked only after NativeWeatherData's executable checksum validation.
    internal static Definition Read(Func<int, int, byte[]> read)
    {
        double Single(int address) => BitConverter.ToSingle(read(address, 4));
        double PushFloat(int address)
        {
            if (read(address, 1)[0] != 0x68) throw new InvalidDataException("Expected cloud transform push");
            return Single(address + 1);
        }
        string Texture(int address)
        {
            byte[] bytes = read(address, 32); int end = Array.IndexOf(bytes, (byte)0);
            string value = Encoding.ASCII.GetString(bytes, 0, end);
            if (!value.EndsWith(".dds", StringComparison.Ordinal)) throw new InvalidDataException("Invalid native cloud texture");
            return value[..^4];
        }
        string AlphaOperation(int address)
        {
            byte[] instruction = read(address, 8);
            // IDirect3DDevice8::SetTextureStageState(0, D3DTSS_ALPHAOP, value).
            // D3DTOP 5 is MODULATE2X, not SUBTRACT (10). Retain the native
            // operation through export and use the shared runtime stage compiler.
            if (instruction[0] != 0x6a || !instruction.Skip(2).Take(6).SequenceEqual(new byte[] { 0x6a, 4, 0x6a, 0, 0x53, 0xff }))
                throw new InvalidDataException("Expected native cloud alpha state");
            return instruction[1] switch { 4 => "modulate", 5 => "modulate2x",
                _ => throw new InvalidDataException("Unsupported native cloud alpha operation") };
        }
        return new(Texture(0xc9b3e8), Texture(0xc9b3f4), Single(0xb9a064), Single(0xb9a070),
            Single(0xb9a068), Single(0xb9a084),
            [PushFloat(0x8752fe), PushFloat(0x8752f9), PushFloat(0x8752f4)],
            // Constructor defaults differ from the active overcast wind setter.
            // 0x86442e passes ten seconds; 0x875266 passes .3 to web+0x88.
            PushFloat(0x87530d), Single(0x87e784), [0, Single(0x87e71e)], Single(0x87e78e),
            PushFloat(0x86442e), PushFloat(0x875266), [Single(0xb7d94c), Single(0xb9a080)],
            [AlphaOperation(0x87f244), AlphaOperation(0x87f3fe)]);
    }

    /// <summary>Exact immutable result of the native builders, reproduced by
    /// tools/RecoverWeatherGeometry. This is derived source data, not guessed
    /// sphere tessellation. Native Z-up coordinates and UVs remain untouched.</summary>
    public static IReadOnlyList<Mesh> Geometry()
    {
        using var source = typeof(NativeWeatherClouds).Assembly.GetManifestResourceStream(
            "Raximod.EngineAssets.Resources.NativeWeatherGeometry.bin")
            ?? throw new InvalidDataException("Missing recovered native weather geometry");
        using var copy = new MemoryStream(); source.CopyTo(copy); byte[] bytes = copy.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != GeometrySha256)
            throw new InvalidDataException("Recovered native weather geometry checksum mismatch");
        using var reader = new BinaryReader(new MemoryStream(bytes));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "PSWC" || reader.ReadInt32() != 1 || reader.ReadInt32() != 2)
            throw new InvalidDataException("Unsupported recovered weather geometry");
        var meshes = new List<Mesh>();
        foreach (string name in new[] { "dome", "horizon" })
        {
            int count = reader.ReadInt32(), indexCount = reader.ReadInt32();
            var positions = new double[count][]; var uvs = new double[count][]; var alpha = new double[count];
            for (int i = 0; i < count; i++)
            {
                positions[i] = [reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()];
                alpha[i] = (reader.ReadUInt32() >> 24) / 255d;
                uvs[i] = [reader.ReadSingle(), reader.ReadSingle()];
                if (positions[i].Concat(uvs[i]).Any(v => !double.IsFinite(v)))
                    throw new InvalidDataException("Nonfinite native weather geometry");
            }
            int[] indices = Enumerable.Range(0, indexCount).Select(_ => (int)reader.ReadUInt16()).ToArray();
            if (indices.Any(i => i >= count) || indexCount % 3 != 0)
                throw new InvalidDataException("Invalid native weather topology");
            meshes.Add(new(name, positions, uvs, alpha, indices));
        }
        if (reader.BaseStream.Position != bytes.Length) throw new InvalidDataException("Unaccounted weather geometry bytes");
        return meshes;
    }
}

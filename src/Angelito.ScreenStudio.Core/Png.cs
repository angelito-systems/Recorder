using System.IO.Compression;

namespace Angelito.ScreenStudio.Core;

/// <summary>Minimal RGBA PNG writer (for cursor images handed to ffmpeg); straight alpha, no filtering.</summary>
public static class Png
{
    public static void WriteBgra(string path, int w, int h, ReadOnlySpan<byte> bgra)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + w * 4];
            for (var y = 0; y < h; y++)
            {
                row[0] = 0; // filter: none
                for (var x = 0; x < w; x++)
                {
                    var s = (y * w + x) * 4;
                    (row[1 + x * 4], row[2 + x * 4], row[3 + x * 4], row[4 + x * 4]) = (bgra[s + 2], bgra[s + 1], bgra[s], bgra[s + 3]);
                }
                z.Write(row);
            }
        }
        using var f = File.Create(path);
        f.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(f, "IHDR", [.. Be(w), .. Be(h), 8, 6, 0, 0, 0]); // 8-bit RGBA
        Chunk(f, "IDAT", raw.ToArray());
        Chunk(f, "IEND", []);
    }

    private static byte[] Be(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(Be(data.Length));
        s.Write(t);
        s.Write(data);
        s.Write(Be((int)Crc([.. t, .. data])));
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] b)
    {
        var c = 0xFFFFFFFF;
        foreach (var x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}

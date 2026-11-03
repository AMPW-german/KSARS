using System.IO.Compression;

// Minimal PNG writer for solid-color 8x8 RGBA textures.
public static class Png
{
    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteChunk(BinaryWriter bw, string type, byte[] data)
    {
        bw.Write(ToBE((uint)data.Length));
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        bw.Write(typeBytes);
        bw.Write(data);
        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        bw.Write(ToBE(Crc32(crcInput)));
    }

    private static byte[] ToBE(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    private static byte[] ToBE(ushort v) => new[] { (byte)(v >> 8), (byte)v };

    public static void WriteSolid(string path, byte r, byte g, byte b, byte a, int size = 8)
    {
        // raw image: each scanline = filter byte 0 + RGBA pixels
        var raw = new byte[size * (1 + size * 4)];
        for (int y = 0; y < size; y++)
        {
            int row = y * (1 + size * 4);
            raw[row] = 0;
            for (int x = 0; x < size; x++)
            {
                int p = row + 1 + x * 4;
                raw[p] = r; raw[p + 1] = g; raw[p + 2] = b; raw[p + 3] = a;
            }
        }

        // zlib stream (78 9C + deflate + adler32)
        byte[] idat;
        using (var ms = new MemoryStream())
        {
            ms.WriteByte(0x78); ms.WriteByte(0x9C);
            using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                ds.Write(raw, 0, raw.Length);
            uint adler = Adler32(raw);
            ms.Write(ToBE(adler), 0, 4);
            idat = ms.ToArray();
        }

        var ihdr = new List<byte>();
        ihdr.AddRange(ToBE((uint)size)); ihdr.AddRange(ToBE((uint)size));
        ihdr.Add(8);  // bit depth
        ihdr.Add(6);  // color type RGBA
        ihdr.Add(0);  // compression
        ihdr.Add(0);  // filter
        ihdr.Add(0);  // interlace

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(bw, "IHDR", ihdr.ToArray());
        WriteChunk(bw, "IDAT", idat);
        WriteChunk(bw, "IEND", Array.Empty<byte>());
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }
}

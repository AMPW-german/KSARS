using System.Text;
using System.Text.Json;

// Extracts raw vertex positions from a GLB for analysis.
// Usage: invoked from Program.cs main when --verts is passed.
public static class Vertices
{
    public static void Dump(string path, string outCsv)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        var magic = br.ReadUInt32();
        var version = br.ReadUInt32();
        var length = br.ReadUInt32();
        var chunkLen = br.ReadUInt32();
        var chunkType = br.ReadUInt32();
        var jsonBytes = br.ReadBytes((int)chunkLen);
        // align to 4
        var json = JsonDocument.Parse(jsonBytes).RootElement;

        var chunkLen2 = br.ReadUInt32();
        var chunkType2 = br.ReadUInt32();
        var binStart = fs.Position;
        var bin = br.ReadBytes((int)chunkLen2);

        var bufferViews = json.GetProperty("bufferViews");
        var accessors = json.GetProperty("accessors");
        var meshes = json.GetProperty("meshes");
        var nodes = json.GetProperty("nodes");

        // build node transform (TRS) helper
        float4x4[] nodeWorld = new float4x4[nodes.GetArrayLength()];
        int[] nodeMesh = new int[nodes.GetArrayLength()];
        int[] nodeParent = new int[nodes.GetArrayLength()];
        for (int i = 0; i < nodeParent.Length; i++) { nodeParent[i] = -1; nodeMesh[i] = -1; nodeWorld[i] = float4x4.Identity; }

        int ni = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("mesh", out var m)) nodeMesh[ni] = m.GetInt32();
            nodeWorld[ni] = ReadNodeTRS(node);
            if (node.TryGetProperty("children", out var ch))
                foreach (var c in ch.EnumerateArray()) nodeParent[c.GetInt32()] = ni;
            ni++;
        }
        // compute world transforms by walking up parents
        for (int i = 0; i < nodeWorld.Length; i++)
        {
            var acc = nodeWorld[i];
            int p = nodeParent[i];
            while (p >= 0) { acc = Mul(nodeWorld[p], acc); p = nodeParent[p]; }
            nodeWorld[i] = acc;
        }

        var sb = new StringBuilder();
        sb.AppendLine("mesh,node,x,y,z");

        for (int i = 0; i < nodeMesh.Length; i++)
        {
            if (nodeMesh[i] < 0) continue;
            var mesh = meshes[nodeMesh[i]];
            var meshName = mesh.GetProperty("name").GetString();
            var nodeName = nodes[i].TryGetProperty("name", out var nn) ? nn.GetString() : i.ToString();
            var world = nodeWorld[i];
            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                int posAcc = prim.GetProperty("attributes").GetProperty("POSITION").GetInt32();
                var acc = accessors[posAcc];
                int bvIdx = acc.GetProperty("bufferView").GetInt32();
                int offset = acc.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
                int count = acc.GetProperty("count").GetInt32();
                var bv = bufferViews[bvIdx];
                int bvOffset = bv.TryGetProperty("byteOffset", out var bvo) ? bvo.GetInt32() : 0;
                int stride = bv.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 12;
                int start = bvOffset + offset;
                for (int v = 0; v < count; v++)
                {
                    int p0 = start + v * stride;
                    float x = BitConverter.ToSingle(bin, p0);
                    float y = BitConverter.ToSingle(bin, p0 + 4);
                    float z = BitConverter.ToSingle(bin, p0 + 8);
                    var wp = Transform(world, x, y, z);
                    sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2:F4},{3:F4},{4:F4}", meshName, nodeName, wp.x, wp.y, wp.z));
                }
            }
        }
        File.WriteAllText(outCsv, sb.ToString());
        Console.WriteLine($"wrote {outCsv}");
    }

    struct float4x4
    {
        public float m00, m01, m02, m03, m10, m11, m12, m13, m20, m21, m22, m23, m30, m31, m32, m33;
        public static float4x4 Identity => new float4x4 { m00 = 1, m11 = 1, m22 = 1, m33 = 1 };
    }

    static float4x4 ReadNodeTRS(JsonElement node)
    {
        var m = float4x4.Identity;
        if (node.TryGetProperty("matrix", out var mat))
        {
            var a = mat.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            // glTF matrix is column-major
            return new float4x4
            {
                m00 = a[0], m01 = a[4], m02 = a[8], m03 = a[12],
                m10 = a[1], m11 = a[5], m12 = a[9], m13 = a[13],
                m20 = a[2], m21 = a[6], m22 = a[10], m23 = a[14],
                m30 = a[3], m31 = a[7], m32 = a[11], m33 = a[15],
            };
        }
        float tx = 0, ty = 0, tz = 0, sx = 1, sy = 1, sz = 1, qx = 0, qy = 0, qz = 0, qw = 1;
        if (node.TryGetProperty("translation", out var t)) { var a = t.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray(); tx = a[0]; ty = a[1]; tz = a[2]; }
        if (node.TryGetProperty("scale", out var s)) { var a = s.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray(); sx = a[0]; sy = a[1]; sz = a[2]; }
        if (node.TryGetProperty("rotation", out var r)) { var a = r.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray(); qx = a[0]; qy = a[1]; qz = a[2]; qw = a[3]; }
        // rotation matrix from quat
        float xx = qx * qx, yy = qy * qy, zz = qz * qz, xy = qx * qy, xz = qx * qz, yz = qy * qz, wx = qw * qx, wy = qw * qy, wz = qw * qz;
        m.m00 = (1 - 2 * (yy + zz)) * sx; m.m01 = 2 * (xy - wz) * sy; m.m02 = 2 * (xz + wy) * sz; m.m03 = tx;
        m.m10 = 2 * (xy + wz) * sx; m.m11 = (1 - 2 * (xx + zz)) * sy; m.m12 = 2 * (yz - wx) * sz; m.m13 = ty;
        m.m20 = 2 * (xz - wy) * sx; m.m21 = 2 * (yz + wx) * sy; m.m22 = (1 - 2 * (xx + yy)) * sz; m.m23 = tz;
        return m;
    }

    static float4x4 Mul(float4x4 a, float4x4 b)
    {
        var r = new float4x4();
        r.m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20 + a.m03 * b.m30;
        r.m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21 + a.m03 * b.m31;
        r.m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22 + a.m03 * b.m32;
        r.m03 = a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03 * b.m33;
        r.m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20 + a.m13 * b.m30;
        r.m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21 + a.m13 * b.m31;
        r.m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22 + a.m13 * b.m32;
        r.m13 = a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13 * b.m33;
        r.m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20 + a.m23 * b.m30;
        r.m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21 + a.m23 * b.m31;
        r.m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22 + a.m23 * b.m32;
        r.m23 = a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23 * b.m33;
        r.m30 = a.m30 * b.m00 + a.m31 * b.m10 + a.m32 * b.m20 + a.m33 * b.m30;
        r.m31 = a.m30 * b.m01 + a.m31 * b.m11 + a.m32 * b.m21 + a.m33 * b.m31;
        r.m32 = a.m30 * b.m02 + a.m31 * b.m12 + a.m32 * b.m22 + a.m33 * b.m32;
        r.m33 = a.m30 * b.m03 + a.m31 * b.m13 + a.m32 * b.m23 + a.m33 * b.m33;
        return r;
    }

    static (float x, float y, float z) Transform(float4x4 m, float x, float y, float z)
    {
        return (
            m.m00 * x + m.m01 * y + m.m02 * z + m.m03,
            m.m10 * x + m.m11 * y + m.m12 * z + m.m13,
            m.m20 * x + m.m21 * y + m.m22 * z + m.m23
        );
    }
}

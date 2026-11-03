using System.Text.Json;
using System.Text.Json.Nodes;

// Minimal GLB reader/writer + math helpers for the converter.
public sealed class Glb
{
    public JsonNode Json;
    public byte[] Bin = Array.Empty<byte>();

    private Glb(JsonNode json) => Json = json;

    public static Glb Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (br.ReadUInt32() != 0x46546C67) throw new Exception("not a GLB");
        br.ReadUInt32(); br.ReadUInt32();
        var jsonLen = br.ReadUInt32();
        if (br.ReadUInt32() != 0x4E4F534A) throw new Exception("missing JSON chunk");
        var json = JsonNode.Parse(br.ReadBytes((int)jsonLen))!;
        var glb = new Glb(json);
        var binLen = br.ReadUInt32();
        if (br.ReadUInt32() != 0x004E4942) throw new Exception("missing BIN chunk");
        glb.Bin = br.ReadBytes((int)binLen);
        return glb;
    }

    public static void Write(string path, string json, byte[] bin)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        bw.Write(0x46546C67u); bw.Write(2u);
        bw.Write((uint)(12 + 8 + jsonBytes.Length + jsonPad + 8 + bin.Length));
        bw.Write((uint)(jsonBytes.Length + jsonPad)); bw.Write(0x4E4F534Au);
        bw.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) bw.Write((byte)0x20);
        int binPad = (4 - bin.Length % 4) % 4;
        bw.Write((uint)(bin.Length + binPad)); bw.Write(0x004E4942u);
        bw.Write(bin);
        for (int i = 0; i < binPad; i++) bw.Write((byte)0);
    }

    public int NodeCount => Json["nodes"]!.AsArray().Count;

    public int NodeMeshIndex(int n) => Json["nodes"]![n]!.AsObject().TryGetPropertyValue("mesh", out var m) ? m!.GetValue<int>() : -1;

    public IEnumerable<JsonObject> MeshPrimitives(int meshIdx)
    {
        foreach (var p in Json["meshes"]![meshIdx]!["primitives"]!.AsArray())
            yield return p!.AsObject();
    }

    public Mat4[] ComputeNodeWorldTransforms()
    {
        var nodes = Json["nodes"]!.AsArray();
        var world = new Mat4[nodes.Count];
        var parent = new int[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) { parent[i] = -1; world[i] = Mat4.Identity; }
        for (int i = 0; i < nodes.Count; i++)
        {
            world[i] = ReadNodeTRS(nodes[i]!);
            if (nodes[i]!.AsObject().TryGetPropertyValue("children", out var ch))
                foreach (var c in ch!.AsArray()) parent[c!.GetValue<int>()] = i;
        }
        for (int i = 0; i < world.Length; i++)
        {
            var acc = world[i];
            for (int p = parent[i]; p >= 0; p = parent[p]) acc = world[p] * acc;
            world[i] = acc;
        }
        return world;
    }

    private static Mat4 ReadNodeTRS(JsonNode node)
    {
        var o = node.AsObject();
        if (o.TryGetPropertyValue("matrix", out var mat))
        {
            var a = mat!.AsArray().Select(x => (float)x!.GetValue<double>()).ToArray();
            return new Mat4(
                a[0], a[4], a[8], a[12],
                a[1], a[5], a[9], a[13],
                a[2], a[6], a[10], a[14],
                a[3], a[7], a[11], a[15]);
        }
        float tx = 0, ty = 0, tz = 0, sx = 1, sy = 1, sz = 1, qx = 0, qy = 0, qz = 0, qw = 1;
        if (o.TryGetPropertyValue("translation", out var t)) { var a = t!.AsArray(); tx = (float)a[0]!.GetValue<double>(); ty = (float)a[1]!.GetValue<double>(); tz = (float)a[2]!.GetValue<double>(); }
        if (o.TryGetPropertyValue("scale", out var s)) { var a = s!.AsArray(); sx = (float)a[0]!.GetValue<double>(); sy = (float)a[1]!.GetValue<double>(); sz = (float)a[2]!.GetValue<double>(); }
        if (o.TryGetPropertyValue("rotation", out var r)) { var a = r!.AsArray(); qx = (float)a[0]!.GetValue<double>(); qy = (float)a[1]!.GetValue<double>(); qz = (float)a[2]!.GetValue<double>(); qw = (float)a[3]!.GetValue<double>(); }
        float xx = qx * qx, yy = qy * qy, zz = qz * qz, xy = qx * qy, xz = qx * qz, yz = qy * qz, wx = qw * qx, wy = qw * qy, wz = qw * qz;
        return new Mat4(
            (1 - 2 * (yy + zz)) * sx, 2 * (xy - wz) * sy, 2 * (xz + wy) * sz, tx,
            2 * (xy + wz) * sx, (1 - 2 * (xx + zz)) * sy, 2 * (yz - wx) * sz, ty,
            2 * (xz - wy) * sx, 2 * (yz + wx) * sy, (1 - 2 * (xx + yy)) * sz, tz,
            0, 0, 0, 1);
    }

    public PrimData DecodePrimitive(JsonObject prim)
    {
        var attrs = prim["attributes"]!.AsObject();
        var d = new PrimData { Material = prim.AsObject().TryGetPropertyValue("material", out var m) ? m!.GetValue<int>() : 0 };
        d.Positions = ReadVec3(attrs["POSITION"]!.GetValue<int>());
        d.Normals = attrs.TryGetPropertyValue("NORMAL", out var na) ? ReadVec3(na!.GetValue<int>()) : new float3[d.Positions.Length];
        d.Uvs = attrs.TryGetPropertyValue("TEXCOORD_0", out var ta) ? ReadVec2(ta!.GetValue<int>()) : new float2[d.Positions.Length];
        (d.Indices, d.IndexComponentType) = ReadIndices(prim["indices"]!.GetValue<int>());
        return d;
    }

    private (int bv, int offset, int stride) BufferViewOf(JsonNode accessor)
    {
        var bvs = Json["bufferViews"]!.AsArray();
        var acc = accessor.AsObject();
        var bv = bvs[acc["bufferView"]!.GetValue<int>()]!;
        int accOff = acc.TryGetPropertyValue("byteOffset", out var ao) ? ao!.GetValue<int>() : 0;
        int bvOff = bv.AsObject().TryGetPropertyValue("byteOffset", out var bo) ? bo!.GetValue<int>() : 0;
        int stride = bv.AsObject().TryGetPropertyValue("byteStride", out var bs) ? bs!.GetValue<int>() : 0;
        return (acc["bufferView"]!.GetValue<int>(), bvOff + accOff, stride);
    }

    private float3[] ReadVec3(int accessorIdx)
    {
        var acc = Json["accessors"]![accessorIdx]!;
        int count = acc["count"]!.GetValue<int>();
        var (bvIdx, offset, stride) = BufferViewOf(acc);
        if (stride == 0) stride = 12;
        var bvo = Json["bufferViews"]![bvIdx]!.AsObject().TryGetPropertyValue("byteOffset", out var b) ? b!.GetValue<int>() : 0;
        var result = new float3[count];
        int start = offset;
        for (int i = 0; i < count; i++)
        {
            int p = start + i * stride;
            result[i] = new float3(BitConverter.ToSingle(Bin, p), BitConverter.ToSingle(Bin, p + 4), BitConverter.ToSingle(Bin, p + 8));
        }
        return result;
    }

    private float2[] ReadVec2(int accessorIdx)
    {
        var acc = Json["accessors"]![accessorIdx]!;
        int count = acc["count"]!.GetValue<int>();
        var (bvIdx, offset, stride) = BufferViewOf(acc);
        if (stride == 0) stride = 8;
        var result = new float2[count];
        for (int i = 0; i < count; i++)
        {
            int p = offset + i * stride;
            result[i] = new float2(BitConverter.ToSingle(Bin, p), BitConverter.ToSingle(Bin, p + 4));
        }
        return result;
    }

    private (uint[] indices, int componentType) ReadIndices(int accessorIdx)
    {
        var acc = Json["accessors"]![accessorIdx]!;
        int count = acc["count"]!.GetValue<int>();
        int comp = acc["componentType"]!.GetValue<int>();
        var (_, offset, _) = BufferViewOf(acc);
        var result = new uint[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = comp switch
            {
                5121 => Bin[offset + i],
                5123 => BitConverter.ToUInt16(Bin, offset + i * 2),
                5125 => BitConverter.ToUInt32(Bin, offset + i * 4),
                _ => throw new Exception($"index componentType {comp}")
            };
        }
        return (result, comp);
    }
}

public struct float2 { public float X, Y; public float2(float x, float y) { X = x; Y = y; } }
public struct float3 { public float X, Y, Z; public float3(float x, float y, float z) { X = x; Y = y; Z = z; } }

public struct Mat4
{
    public float M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23, M30, M31, M32, M33;
    public Mat4(float m00, float m01, float m02, float m03, float m10, float m11, float m12, float m13,
                float m20, float m21, float m22, float m23, float m30, float m31, float m32, float m33)
    { M00 = m00; M01 = m01; M02 = m02; M03 = m03; M10 = m10; M11 = m11; M12 = m12; M13 = m13;
      M20 = m20; M21 = m21; M22 = m22; M23 = m23; M30 = m30; M31 = m31; M32 = m32; M33 = m33; }
    public static Mat4 Identity => new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

    public static Mat4 operator *(Mat4 a, Mat4 b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20 + a.M03 * b.M30,
        a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21 + a.M03 * b.M31,
        a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22 + a.M03 * b.M32,
        a.M00 * b.M03 + a.M01 * b.M13 + a.M02 * b.M23 + a.M03 * b.M33,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20 + a.M13 * b.M30,
        a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31,
        a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32,
        a.M10 * b.M03 + a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20 + a.M23 * b.M30,
        a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31,
        a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32,
        a.M20 * b.M03 + a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33,
        a.M30 * b.M00 + a.M31 * b.M10 + a.M32 * b.M20 + a.M33 * b.M30,
        a.M30 * b.M01 + a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31,
        a.M30 * b.M02 + a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32,
        a.M30 * b.M03 + a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33);

    public float3 TransformPoint(float3 v) => new(
        M00 * v.X + M01 * v.Y + M02 * v.Z + M03,
        M10 * v.X + M11 * v.Y + M12 * v.Z + M13,
        M20 * v.X + M21 * v.Y + M22 * v.Z + M23);

    public float3 TransformDir(float3 v) => new(
        M00 * v.X + M01 * v.Y + M02 * v.Z,
        M10 * v.X + M11 * v.Y + M12 * v.Z,
        M20 * v.X + M21 * v.Y + M22 * v.Z);

    // Inverse-transpose of the upper 3x3, for normals under non-uniform scale.
    public Mat4 InverseTranspose3x3()
    {
        float a = M00, b = M01, c = M02, d = M10, e = M11, f = M12, g = M20, h = M21, i = M22;
        float A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        float det = a * A + b * B + c * C;
        var inv = new Mat4(
            A / det, -(b * i - c * h) / det, (b * f - c * e) / det, 0,
            B / det, (a * i - c * g) / det, -(a * f - c * d) / det, 0,
            C / det, -(a * h - b * g) / det, (a * e - b * d) / det, 0,
            0, 0, 0, 1);
        // transpose the 3x3
        return new Mat4(inv.M00, inv.M10, inv.M20, 0, inv.M01, inv.M11, inv.M21, 0, inv.M02, inv.M12, inv.M22, 0, 0, 0, 0, 1);
    }
}

public sealed class PrimData
{
    public int Material;
    public float3[] Positions = Array.Empty<float3>();
    public float3[] Normals = Array.Empty<float3>();
    public float2[] Uvs = Array.Empty<float2>();
    public uint[] Indices = Array.Empty<uint>();
    public int IndexComponentType;
    public int VertexCount => Positions.Length;
    public int IndexCount => Indices.Length;

    public void Transform(Mat4 world, Mat4 normalMat)
    {
        for (int i = 0; i < Positions.Length; i++) Positions[i] = world.TransformPoint(Positions[i]);
        for (int i = 0; i < Normals.Length; i++)
        {
            var n = normalMat.TransformDir(Normals[i]);
            float len = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            Normals[i] = len > 0 ? new float3(n.X / len, n.Y / len, n.Z / len) : new float3(0, 1, 0);
        }
    }

    // world -> part space: px = wy, py = wz, pz = wx  (cyclic permutation, det +1)
    // KSA part-space convention: +X = nose/flight direction (habitat pod front),
    // -X = engine/aft, Y = lateral solar-panel booms (+/-65, right-left), Z = up.
    public void ApplyAxisRemap()
    {
        for (int i = 0; i < Positions.Length; i++)
            Positions[i] = new float3(Positions[i].Y, Positions[i].Z, Positions[i].X);
        for (int i = 0; i < Normals.Length; i++)
            Normals[i] = new float3(Normals[i].Y, Normals[i].Z, Normals[i].X);
    }

    // Concatenate primitives sharing a material into one primitive with a shared
    // vertex buffer (KSA renders only primitives[0] per mesh).
    public static PrimData Merge(List<PrimData> prims)
    {
        var merged = new PrimData { Material = prims[0].Material, IndexComponentType = 5125 };
        int vtotal = 0, itotal = 0;
        foreach (var p in prims) { vtotal += p.Positions.Length; itotal += p.Indices.Length; }
        merged.Positions = new float3[vtotal];
        merged.Normals = new float3[vtotal];
        merged.Uvs = new float2[vtotal];
        merged.Indices = new uint[itotal];
        int voff = 0, ioff = 0;
        foreach (var p in prims)
        {
            Array.Copy(p.Positions, 0, merged.Positions, voff, p.Positions.Length);
            Array.Copy(p.Normals, 0, merged.Normals, voff, p.Normals.Length);
            Array.Copy(p.Uvs, 0, merged.Uvs, voff, p.Uvs.Length);
            for (int i = 0; i < p.Indices.Length; i++)
                merged.Indices[ioff + i] = (uint)(p.Indices[i] + voff);
            voff += p.Positions.Length;
            ioff += p.Indices.Length;
        }
        return merged;
    }

    // The source model uses doubleSided materials; KSA renders single-sided, so emit
    // every triangle in both windings to keep all faces visible.
    public void MakeDoubleSided()
    {
        var doubled = new uint[Indices.Length * 2];
        Indices.CopyTo(doubled, 0);
        for (int i = Indices.Length; i < doubled.Length; i += 3)
        {
            doubled[i] = Indices[i - Indices.Length];
            doubled[i + 1] = Indices[i - Indices.Length + 2];
            doubled[i + 2] = Indices[i - Indices.Length + 1];
        }
        Indices = doubled;
        IndexComponentType = 5125; // uint
    }

    public byte[] PositionsToBytes(out double[] min, out double[] max)
    {
        min = new double[3] { double.MaxValue, double.MaxValue, double.MaxValue };
        max = new double[3] { double.MinValue, double.MinValue, double.MinValue };
        var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
        foreach (var p in Positions)
        {
            bw.Write(p.X); bw.Write(p.Y); bw.Write(p.Z);
            min[0] = Math.Min(min[0], p.X); min[1] = Math.Min(min[1], p.Y); min[2] = Math.Min(min[2], p.Z);
            max[0] = Math.Max(max[0], p.X); max[1] = Math.Max(max[1], p.Y); max[2] = Math.Max(max[2], p.Z);
        }
        return ms.ToArray();
    }

    public byte[] NormalsToBytes()
    {
        var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
        foreach (var n in Normals) { bw.Write(n.X); bw.Write(n.Y); bw.Write(n.Z); }
        return ms.ToArray();
    }

    public byte[] UvsToBytes()
    {
        var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
        foreach (var uv in Uvs) { bw.Write(uv.X); bw.Write(uv.Y); }
        return ms.ToArray();
    }

    public byte[] IndicesToBytes(out int componentType)
    {
        var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
        if (IndexComponentType == 5121)
        {
            componentType = 5121;
            foreach (var i in Indices) bw.Write((byte)i);
        }
        else if (IndexComponentType == 5123)
        {
            componentType = 5123;
            foreach (var i in Indices) bw.Write((ushort)i);
        }
        else
        {
            componentType = 5125;
            foreach (var i in Indices) bw.Write(i);
        }
        return ms.ToArray();
    }
}

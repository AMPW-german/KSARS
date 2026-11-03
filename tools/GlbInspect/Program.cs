using System.Text;
using System.Text.Json;

// Dumps GLB JSON chunk summary: node names, mesh names, materials, sizes.
var path = args[0];
if (args.Contains("--verts"))
{
    var outCsv = args.SkipWhile(a => a != "--verts").Skip(1).First();
    Vertices.Dump(path, outCsv);
    return;
}
using var fs = File.OpenRead(path);
using var br = new BinaryReader(fs);

var magic = br.ReadUInt32();
var version = br.ReadUInt32();
var length = br.ReadUInt32();
if (magic != 0x46546C67) { Console.WriteLine("Not a GLB"); return; }

var chunkLen = br.ReadUInt32();
var chunkType = br.ReadUInt32();
var jsonBytes = br.ReadBytes((int)chunkLen);
var json = JsonDocument.Parse(jsonBytes).RootElement;

if (json.TryGetProperty("asset", out var asset))
    Console.WriteLine($"asset: {asset}");

Console.WriteLine("\n=== MESHES ===");
var accessorList = json.GetProperty("accessors");
if (json.TryGetProperty("meshes", out var meshes))
{
    int i = 0;
    foreach (var mesh in meshes.EnumerateArray())
    {
        var name = mesh.TryGetProperty("name", out var n) ? n.GetString() : $"mesh_{i}";
        double minx = double.MaxValue, miny = double.MaxValue, minz = double.MaxValue;
        double maxx = double.MinValue, maxy = double.MinValue, maxz = double.MinValue;
        int prims = 0;
        if (mesh.TryGetProperty("primitives", out var primsArr))
        {
            prims = primsArr.GetArrayLength();
            foreach (var prim in primsArr.EnumerateArray())
            {
                if (prim.TryGetProperty("attributes", out var attrs) && attrs.TryGetProperty("POSITION", out var posAcc))
                {
                    var acc = accessorList[posAcc.GetInt32()];
                    if (acc.TryGetProperty("min", out var mn) && acc.TryGetProperty("max", out var mx))
                    {
                        var mna = mn.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                        var mxa = mx.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                        minx = Math.Min(minx, mna[0]); miny = Math.Min(miny, mna[1]); minz = Math.Min(minz, mna[2]);
                        maxx = Math.Max(maxx, mxa[0]); maxy = Math.Max(maxy, mxa[1]); maxz = Math.Max(maxz, mxa[2]);
                    }
                }
            }
        }
        Console.WriteLine($"mesh[{i}] '{name}' primitives={prims} bounds min=[{minx:F3},{miny:F3},{minz:F3}] max=[{maxx:F3},{maxy:F3},{maxz:F3}]");
        i++;
    }
}

Console.WriteLine("\n=== NODES ===");
if (json.TryGetProperty("nodes", out var nodes))
{
    int i = 0;
    foreach (var node in nodes.EnumerateArray())
    {
        var name = node.TryGetProperty("name", out var n) ? n.GetString() : $"node_{i}";
        var meshIdx = node.TryGetProperty("mesh", out var m) ? m.GetInt32().ToString() : "-";
        var t = node.TryGetProperty("translation", out var tr) ? string.Join(",", tr.EnumerateArray().Select(x => x.GetDouble().ToString("F2"))) : "-";
        var s = node.TryGetProperty("scale", out var sc) ? string.Join(",", sc.EnumerateArray().Select(x => x.GetDouble().ToString("F2"))) : "-";
        Console.WriteLine($"node[{i}] '{name}' mesh={meshIdx} T=[{t}] S=[{s}]");
        i++;
    }
}

Console.WriteLine("\n=== MATERIALS ===");
if (json.TryGetProperty("materials", out var materials))
{
    int i = 0;
    foreach (var mat in materials.EnumerateArray())
    {
        var name = mat.TryGetProperty("name", out var n) ? n.GetString() : $"mat_{i}";
        Console.WriteLine($"material[{i}] '{name}': {mat}");
        i++;
    }
}

Console.WriteLine("\n=== IMAGES/TEXTURES ===");
if (json.TryGetProperty("images", out var images))
{
    int i = 0;
    foreach (var img in images.EnumerateArray())
    {
        var name = img.TryGetProperty("name", out var n) ? n.GetString() : $"img_{i}";
        var mime = img.TryGetProperty("mimeType", out var mt) ? mt.GetString() : "?";
        Console.WriteLine($"image[{i}] '{name}' {mime}");
        i++;
    }
}

// Accessor bounds for overall scale
if (json.TryGetProperty("accessors", out var accessors))
{
    double minx = double.MaxValue, miny = double.MaxValue, minz = double.MaxValue;
    double maxx = double.MinValue, maxy = double.MinValue, maxz = double.MinValue;
    foreach (var acc in accessors.EnumerateArray())
    {
        if (acc.TryGetProperty("type", out var ty) && ty.GetString() == "VEC3" &&
            acc.TryGetProperty("min", out var mn) && acc.TryGetProperty("max", out var mx))
        {
            var mna = mn.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var mxa = mx.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            minx = Math.Min(minx, mna[0]); miny = Math.Min(miny, mna[1]); minz = Math.Min(minz, mna[2]);
            maxx = Math.Max(maxx, mxa[0]); maxy = Math.Max(maxy, mxa[1]); maxz = Math.Max(maxz, mxa[2]);
        }
    }
    Console.WriteLine($"\nBounds min=[{minx:F2},{miny:F2},{minz:F2}] max=[{maxx:F2},{maxy:F2},{maxz:F2}]");
    Console.WriteLine($"Size = [{maxx-minx:F2} x {maxy-miny:F2} x {maxz-minz:F2}] m");
}

Console.WriteLine($"\nscenes: {(json.TryGetProperty("scenes", out var scenes) ? scenes.GetArrayLength() : 0)}");
Console.WriteLine($"nodes total: {(json.TryGetProperty("nodes", out var nd) ? nd.GetArrayLength() : 0)}");
Console.WriteLine($"meshes total: {(json.TryGetProperty("meshes", out var me) ? me.GetArrayLength() : 0)}");

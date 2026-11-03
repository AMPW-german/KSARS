using System.Text.Json;
using System.Text.Json.Nodes;

// GlbConvert: converts a source GLB into a KSA-ready mesh-atlas GLB.
//
//  - Bakes node world transforms into vertex data (KSA's MeshAtlas ignores node transforms).
//  - Remaps axes so the ship's long axis lies on part-space X (KSA thrust axis, exhaust at -X)
//    and ship-up lies on +Y: part = R * world with R = [0,0,-1; 0,1,0; 1,0,0]  (world -Z -> part +X).
//  - Regroups primitives by glTF material index into one mesh per material
//    (KSA renders each mesh with a single <Material>), skipping INVISIBLE_MATERIAL_INDICES.
//  - Emits flat-color PNG textures + an XML fragment with PbrMaterial/PartModel declarations.
//
// Usage: GlbConvert <input.glb> <outputDir> [--skip-mat=i,j,...]

var input = args[0];
var outDir = args[1];
var skipMats = new HashSet<int> { 3 }; // mat_23: alpha=0 helper geometry
foreach (var a in args.Skip(2))
    if (a.StartsWith("--skip-mat="))
        foreach (var s in a["--skip-mat=".Length..].Split(',')) skipMats.Add(int.Parse(s));

var glb = Glb.Read(input);
var world = glb.ComputeNodeWorldTransforms();
var materials = glb.Json["materials"]?.AsArray();

// Collect output primitives grouped by material index.
var groups = new SortedDictionary<int, List<PrimData>>();
for (int n = 0; n < glb.NodeCount; n++)
{
    int meshIdx = glb.NodeMeshIndex(n);
    if (meshIdx < 0) continue;
    var w = world[n];
    var invT = w.InverseTranspose3x3();
    foreach (var prim in glb.MeshPrimitives(meshIdx))
    {
        int mat = prim.TryGetPropertyValue("material", out var mprop) ? mprop!.GetValue<int>() : 0;
        if (skipMats.Contains(mat)) continue;
        var d = glb.DecodePrimitive(prim);
        d.Transform(w, invT);
        d.ApplyAxisRemap();
        d.MakeDoubleSided();
        if (!groups.TryGetValue(mat, out var list)) groups[mat] = new List<PrimData>();
        groups[mat].Add(d);
    }
}

Directory.CreateDirectory(outDir);
var meshDir = Path.Combine(outDir, "Meshes");
var texDir = Path.Combine(outDir, "Textures");
Directory.CreateDirectory(meshDir);
Directory.CreateDirectory(texDir);

// ---- Build output GLB ----
var outJson = new JsonObject
{
    ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "GlbConvert (KSARS)" },
    ["scene"] = 0,
    ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(), ["name"] = "Scene" }),
    ["nodes"] = new JsonArray(),
    ["meshes"] = new JsonArray(),
    ["materials"] = materials?.DeepClone(),
    ["accessors"] = new JsonArray(),
    ["bufferViews"] = new JsonArray(),
    ["buffers"] = new JsonArray(),
};
var outNodes = (JsonArray)outJson["nodes"]!;
var outMeshes = (JsonArray)outJson["meshes"]!;
var outSceneNodes = (JsonArray)outJson["scenes"]![0]!["nodes"]!;
var outAccessors = (JsonArray)outJson["accessors"]!;
var outBufferViews = (JsonArray)outJson["bufferViews"]!;

var bin = new MemoryStream();
var binWriter = new BinaryWriter(bin);

int AddBufferView(byte[] data)
{
    while (bin.Position % 4 != 0) binWriter.Write((byte)0);
    var bv = new JsonObject { ["buffer"] = 0, ["byteOffset"] = (int)bin.Position, ["byteLength"] = data.Length };
    binWriter.Write(data);
    outBufferViews.Add(bv);
    return outBufferViews.Count - 1;
}

int AddAccessor(int bv, int compType, int count, string type, double[]? min, double[]? max)
{
    var acc = new JsonObject { ["bufferView"] = bv, ["componentType"] = compType, ["count"] = count, ["type"] = type };
    if (min != null) acc["min"] = new JsonArray(min.Select(v => JsonValue.Create(v)).ToArray<JsonNode?>());
    if (max != null) acc["max"] = new JsonArray(max.Select(v => JsonValue.Create(v)).ToArray<JsonNode?>());
    outAccessors.Add(acc);
    return outAccessors.Count - 1;
}

var meshNames = new List<string>();
double gMinX = 1e30, gMinY = 1e30, gMinZ = 1e30, gMaxX = -1e30, gMaxY = -1e30, gMaxZ = -1e30;
foreach (var (mat, prims) in groups)
{
    var d = PrimData.Merge(prims);
    var primsArr = new JsonArray();
    {
        var posBytes = d.PositionsToBytes(out var mn, out var mx);
        gMinX = Math.Min(gMinX, mn[0]); gMinY = Math.Min(gMinY, mn[1]); gMinZ = Math.Min(gMinZ, mn[2]);
        gMaxX = Math.Max(gMaxX, mx[0]); gMaxY = Math.Max(gMaxY, mx[1]); gMaxZ = Math.Max(gMaxZ, mx[2]);
        int posAcc = AddAccessor(AddBufferView(posBytes), 5126, d.VertexCount, "VEC3", mn, mx);
        int nrmAcc = AddAccessor(AddBufferView(d.NormalsToBytes()), 5126, d.VertexCount, "VEC3", null, null);
        int uvAcc = AddAccessor(AddBufferView(d.UvsToBytes()), 5126, d.VertexCount, "VEC2", null, null);
        int idxAcc = AddAccessor(AddBufferView(d.IndicesToBytes(out int idxComp)), idxComp, d.IndexCount, "SCALAR", null, null);
        primsArr.Add(new JsonObject
        {
            ["attributes"] = new JsonObject { ["POSITION"] = posAcc, ["NORMAL"] = nrmAcc, ["TEXCOORD_0"] = uvAcc },
            ["indices"] = idxAcc,
            ["material"] = mat,
        });
    }
    var name = $"KSARS_PHM_mat{mat:D2}";
    meshNames.Add(name);
    outMeshes.Add(new JsonObject { ["name"] = name, ["primitives"] = primsArr });
    outSceneNodes.Add(outNodes.Count);
    outNodes.Add(new JsonObject { ["name"] = name, ["mesh"] = outMeshes.Count - 1 });
}

outJson["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = (int)bin.Length });

var outGlb = Path.Combine(meshDir, "KSARS_PHM_MeshAtlas.glb");
Glb.Write(outGlb, JsonSerializer.Serialize(outJson), bin.ToArray());
Console.WriteLine($"wrote {outGlb} ({new FileInfo(outGlb).Length / 1e6:F1} MB, {groups.Count} material meshes)");
Console.WriteLine($"part-space bounds X[{gMinX:F1},{gMaxX:F1}] Y[{gMinY:F1},{gMaxY:F1}] Z[{gMinZ:F1},{gMaxZ:F1}]");
Console.WriteLine($"size {gMaxX - gMinX:F1} x {gMaxY - gMinY:F1} x {gMaxZ - gMinZ:F1} m");

// ---- PNG textures + XML fragment ----
Png.WriteSolid(Path.Combine(texDir, "phm_normal.png"), 128, 128, 255, 255);
var xml = new List<string>();
foreach (var (mat, _) in groups)
{
    var m = materials?[mat];
    var pbr = m?["pbrMetallicRoughness"];
    var c = pbr?["baseColorFactor"]?.AsArray();
    byte r = (byte)Math.Round(255 * (c?[0]?.GetValue<double>() ?? 1.0));
    byte g = (byte)Math.Round(255 * (c?[1]?.GetValue<double>() ?? 1.0));
    byte b = (byte)Math.Round(255 * (c?[2]?.GetValue<double>() ?? 1.0));
    byte metal = (byte)Math.Round(255 * (pbr?["metallicFactor"]?.GetValue<double>() ?? 0.0));
    byte rough = (byte)Math.Round(255 * (pbr?["roughnessFactor"]?.GetValue<double>() ?? 1.0));
    Png.WriteSolid(Path.Combine(texDir, $"phm_diff_{mat:D2}.png"), r, g, b, 255);
    Png.WriteSolid(Path.Combine(texDir, $"phm_pbr_{mat:D2}.png"), 255, rough, metal, 255); // R=AO G=rough B=metal
    var matName = m?["name"]?.GetValue<string>() ?? $"mat{mat}";
    xml.Add($"  <!-- {matName}: rgb({r},{g},{b}) metal={metal} rough={rough} -->\n" +
            $"  <PbrMaterial Id=\"KSARS_PHM_Material_{mat:D2}\">\n" +
            $"    <Diffuse Path=\"Assets/PHM/Textures/phm_diff_{mat:D2}.png\" Category=\"Vessel\" />\n" +
            $"    <Normal Path=\"Assets/PHM/Textures/phm_normal.png\" Category=\"Vessel\" />\n" +
            $"    <AoRoughMetal Path=\"Assets/PHM/Textures/phm_pbr_{mat:D2}.png\" Category=\"Vessel\" />\n" +
            $"  </PbrMaterial>\n" +
            $"  <SubPart Id=\"KSARS_PHM_Subpart_mat{mat:D2}\">\n" +
            $"    <PartModel Id=\"KSARS_PHM_Subpart_mat{mat:D2}_Model\">\n" +
            $"      <Mesh Id=\"{meshNames[groups.Keys.ToList().IndexOf(mat)]}\" />\n" +
            $"      <Material Id=\"KSARS_PHM_Material_{mat:D2}\" />\n" +
            $"    </PartModel>\n" +
            $"    <MeshView>\n" +
            $"      <Mesh Id=\"{meshNames[groups.Keys.ToList().IndexOf(mat)]}\" />\n" +
            $"    </MeshView>\n" +
            $"  </SubPart>");
}
File.WriteAllLines(Path.Combine(outDir, "generated_assets.xml"), xml);
Console.WriteLine($"wrote {groups.Count} material textures + generated_assets.xml");

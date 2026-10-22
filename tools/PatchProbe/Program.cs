using System.Reflection;
using System.Runtime.Loader;

// PatchProbe: loads the deployed KSARS.dll and runs the same Harmony
// CreateClassProcessor(...).Patch() call the mod's Init() performs,
// reporting which patch methods apply and where the chain aborts.
//
// Usage: PatchProbe <ksarsDll> <ksaDir> <starMapDir>

var ksarsDll = Path.GetFullPath(args[0]);
var ksaDir = Path.GetFullPath(args[1]);
var starMapDir = Path.GetFullPath(args[2]);

var probeDirs = new[] { Path.GetDirectoryName(ksarsDll)!, ksaDir, starMapDir };

Assembly? Resolve(AssemblyLoadContext ctx, AssemblyName name)
{
    foreach (var dir in probeDirs)
    {
        var candidate = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(candidate))
        {
            try { return ctx.LoadFromAssemblyPath(candidate); }
            catch { return null; }
        }
    }
    return null;
}

var alc = new AssemblyLoadContext("probe", isCollectible: false);
alc.Resolving += Resolve;

var harmonyAsm = alc.LoadFromAssemblyPath(Path.Combine(starMapDir, "0Harmony.dll"));
var ksarsAsm = alc.LoadFromAssemblyPath(ksarsDll);

var harmonyType = harmonyAsm.GetType("HarmonyLib.Harmony")!;
var harmony = Activator.CreateInstance(harmonyType, "KSARS-PROBE")!;
var patch = harmonyType.GetMethod("CreateClassProcessor")!;
var getPatched = harmonyType.GetMethod("GetPatchedMethods")!;

var targetType = ksarsAsm.GetType("KSARS.KSARS")!;
Console.WriteLine($"Loaded {ksarsAsm.FullName} from {ksarsAsm.Location}");
Console.WriteLine($"Harmony {harmonyAsm.GetName().Version} from {harmonyAsm.Location}");

object processor = patch.Invoke(harmony, new object[] { targetType })!;
var patchMethod = processor.GetType().GetMethod("Patch", Type.EmptyTypes)!;

try
{
    var originals = (System.Collections.IEnumerable)patchMethod.Invoke(processor, Array.Empty<object>())!;
    Console.WriteLine("Patch() returned successfully. Patched originals:");
    foreach (var o in originals)
        Console.WriteLine($"  {o}");
}
catch (Exception ex)
{
    var inner = ex is TargetInvocationException tie ? tie.InnerException! : ex;
    Console.WriteLine($"Patch() THREW: {inner.GetType().FullName}: {inner.Message}");
    Console.WriteLine(inner.StackTrace);
    Console.WriteLine();
    Console.WriteLine("Originals patched before the failure:");
    foreach (var o in (System.Collections.IEnumerable)getPatched.Invoke(harmony, Array.Empty<object>())!)
        Console.WriteLine($"  {o}");
}

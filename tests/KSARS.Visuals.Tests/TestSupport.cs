using System.Reflection;
using Xunit;

namespace KSARS.Visuals.Tests
{
    // Serializes tests that share process-global hook/capture state (Harmony
    // patches, RenderHooks status fields, StarCatalog store) with everything else.
    [CollectionDefinition("VisualHookState", DisableParallelization = true)]
    public class VisualHookStateCollection { }

    internal static class BaselineContext
    {
        private static readonly Lazy<bool> LazyEnabled = new(() => Metadata("VerifyRendererBaseline") == "true");
        private static readonly Lazy<string> LazyKsaAssemblyPath = new(() =>
        {
            var dir = Metadata("KSADir");
            var path = Path.Combine(dir ?? "", "KSA.dll");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"KSA.dll not found at '{path}'. Pass -p:KSADir=\"<KSA install dir>\" with -p:VerifyRendererBaseline=true.", path);
            }
            return path;
        });

        public static bool Enabled => LazyEnabled.Value;

        public static string KsaAssemblyPath => LazyKsaAssemblyPath.Value;

        private static string? Metadata(string key) =>
            typeof(BaselineContext).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)
                ?.Value;
    }

    public sealed class RendererBaselineFactAttribute : FactAttribute
    {
        public RendererBaselineFactAttribute()
        {
            if (!BaselineContext.Enabled)
            {
                Skip = "Installed renderer baseline not run; pass -p:VerifyRendererBaseline=true with explicit -p:KSADir.";
            }
        }
    }
}

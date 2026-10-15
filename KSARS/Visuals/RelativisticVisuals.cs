using System.Reflection;

namespace KSARS.Visuals
{
    internal static class RelativisticVisuals
    {
        private const string DisabledSuffix = "visuals disabled: object-atlas/foreground gate not attempted";

        private static RenderCompatibility.InspectionResult? s_baseline;

        internal static void InspectBaseline(Assembly gameAssembly)
        {
            if (s_baseline != null)
            {
                return;
            }

            s_baseline = RenderCompatibility.InspectAssembly(gameAssembly);
            if (!s_baseline.BaselineMatched)
            {
                Console.WriteLine($"KSARS: {s_baseline.Reason}; {DisabledSuffix}");
                return;
            }

            RenderHooks.TryInstall(s_baseline);
            Console.WriteLine($"KSARS: {RenderHooks.Status}");
        }

        internal static void Shutdown()
        {
            RenderHooks.Uninstall("mod unload");
        }

        internal static void DrawProbeGui()
        {
            RenderHooks.DrawProbeGui();
        }

        internal static string Status => s_baseline is not { } result
            ? "visuals disabled: baseline not evaluated"
            : result.BaselineMatched
                ? RenderHooks.Status
                : $"baseline incompatible ({result.Reason}); visuals disabled";
    }
}

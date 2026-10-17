using System.Linq.Expressions;
using System.Reflection;
using Brutal.Numerics;
using Brutal.VulkanApi;
using HarmonyLib;
using KSA;
using KSA.Rendering;

namespace KSARS.Visuals
{
    // Relativistic catalog-star replacement, injected at the native
    // InstancedStarTechnique.Render seam. The prefix records the owned Gaussian
    // PSF draw and suppresses the native star draw only for the call in which
    // every check and the recording itself succeeded; any failure returns the
    // native draw unchanged. Opt-in via KSARS_STAR_PROBE=1.
    internal static partial class RelativisticSpaceRenderer
    {
        private static MethodInfo? s_starRenderMethod;
        private static RelativisticStarPass? s_starPass;
        private static bool s_starProbeInstalled;
        private static bool s_starFailed;
        private static long s_starPresented;
        private static string s_starStatus = "star probe not installed";

        // members is the validated baseline map; the star probe resolves
        // GlobalShaderBindings::_currentFrame itself when the source probe did
        // not run PrepareProbe, so KSARS_STAR_PROBE=1 works standalone.
        internal static bool TryInstallStarProbe(Harmony harmony,
            IReadOnlyDictionary<string, MemberInfo>? members = null)
        {
            if (s_starProbeInstalled)
            {
                return true;
            }

            s_starStatus = "";
            try
            {
                if (s_getGlobalFrameIndex is null)
                {
                    if (members is null
                        || !members.TryGetValue("KSA.GlobalShaderBindings::_currentFrame", out MemberInfo? frameMember)
                        || frameMember is not FieldInfo currentFrame
                        || currentFrame.DeclaringType != typeof(GlobalShaderBindings)
                        || !currentFrame.IsStatic
                        || currentFrame.FieldType != typeof(int))
                    {
                        throw new InvalidOperationException(
                            "star probe unsupported: KSA.GlobalShaderBindings::_currentFrame member mapping missing; observer capture unavailable");
                    }

                    s_getGlobalFrameIndex = Expression.Lambda<Func<int>>(Expression.Field(null, currentFrame)).Compile();
                }

                MethodInfo? loader = typeof(Mod).GetMethod("LoadStarBinaries",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                ParameterInfo[]? parameters = loader?.GetParameters();
                if (loader is null || parameters is not { Length: 1 }
                    || parameters[0].ParameterType is not { } techniqueType
                    || techniqueType.Name != "InstancedStarTechnique")
                {
                    throw new InvalidOperationException(
                        "star probe unsupported: KSA.Mod::LoadStarBinaries signature changed");
                }

                MethodInfo? render = techniqueType.GetMethod("Render",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                ParameterInfo[]? renderParameters = render?.GetParameters();
                if (render is null || render.ReturnType != typeof(void)
                    || render.DeclaringType != techniqueType || !render.IsVirtual
                    || renderParameters is not { Length: 3 }
                    || renderParameters[0].ParameterType != typeof(CommandBuffer)
                    || renderParameters[1].ParameterType != typeof(IViewport)
                    || renderParameters[2].ParameterType != typeof(int))
                {
                    throw new InvalidOperationException(
                        "star probe unsupported: InstancedStarTechnique::Render(CommandBuffer,IViewport,int) signature changed");
                }

                MethodInfo? prefix = typeof(RelativisticSpaceRenderer).GetMethod(
                    nameof(StarRenderPrefix), BindingFlags.NonPublic | BindingFlags.Static);
                if (prefix is null || prefix.ReturnType != typeof(bool))
                {
                    throw new InvalidOperationException("star probe unsupported: prefix missing or mismatched");
                }

                harmony.Patch(render, prefix: new HarmonyMethod(prefix));
                s_starRenderMethod = render;
                s_starProbeInstalled = true;
                s_starFailed = false;
                s_starStatus = StarCatalogHooks.Installed
                    ? "star probe installed: native-profile sprite replacement armed (stock shape, modeled-energy conserving); native stars suppressed only per successfully recorded call"
                    : "star probe installed but star catalog capture unavailable; native stars retained";
                Console.WriteLine($"KSARS: {s_starStatus}");
                return true;
            }
            catch (Exception ex)
            {
                s_starStatus = $"star probe disabled: {ex.GetType().Name}: {ex.Message}; native stars retained";
                Console.WriteLine($"KSARS: {s_starStatus}");
                return false;
            }
        }

        // Returning false suppresses the native instanced draw for this call
        // only. Any failure path returns true so native stars render unchanged.
        private static bool StarRenderPrefix(object __instance, CommandBuffer __0,
            IViewport __1, int __2)
        {
            if (!s_starProbeInstalled || s_starFailed || s_recordingDisabled)
            {
                return true;
            }

            try
            {
                if (!ReferenceEquals(__1, Program.MainViewport))
                {
                    return true;
                }

                if (!TryCaptureEffectiveObserver(out ObserverSnapshot? snapshot, out PresentationVelocity observer))
                {
                    return true;
                }

                if (s_starPass is not { } pass)
                {
                    Core.Renderer? renderer = Program.GetRenderer();
                    RenderTarget? target = Program.OffscreenTarget;
                    ThermalSpectrumTable? table = StarCatalogHooks.Table;
                    if (renderer is null || target is null || table is null)
                    {
                        return true;
                    }

                    pass = new RelativisticStarPass(renderer, target, table);
                    s_starPass = pass;
                    Console.WriteLine("KSARS: star probe pass allocated against borrowed offscreen target");
                }

                if (StarCatalogHooks.Cache is not { } cache
                    || !pass.SyncCatalog(__instance, cache))
                {
                    return true;
                }

                pass.Record(__0, __1, in observer);
                s_starPresented++;
                s_starStatus = $"star probe presented: {pass.CatalogCount} cataloged star(s), {pass.FittedCount} fitted; Gaussian PSF sigma=1px, modeled-stock-energy conserving, Doppler+spectrum active; native star draw skipped this call only; GPU output unverified{(s_betaOverrideEnabled ? $"; OBSERVER OVERRIDE beta={s_betaOverride:G12}" : "")}";
                return false;
            }
            catch (Exception ex)
            {
                s_starFailed = true;
                s_starPass = null;
                s_starStatus = $"star probe disabled after record fault ({ex.GetType().Name}: {ex.Message}); native stars retained";
                Console.WriteLine($"KSARS: {s_starStatus}");
                return true;
            }
        }

        private static bool TryCaptureEffectiveObserver(out ObserverSnapshot? snapshot,
            out PresentationVelocity observer)
        {
            observer = default;
            snapshot = null;
            if (s_getGlobalFrameIndex is null
                || !ObserverSnapshot.TryCapture(s_getGlobalFrameIndex, out snapshot, out _)
                || snapshot is null)
            {
                return false;
            }

            observer = EffectiveObserver(snapshot);
            return true;
        }

        // Called from OnRendererRebuilt before verified-idle retirement so the
        // pass joins the same retire list as the other owned passes.
        private static void CollectStarPassResources(List<IDisposable> resources)
        {
            if (s_starPass is { } pass)
            {
                resources.Add(pass);
            }
        }

        internal static void ResetStarProbeState()
        {
            s_starRenderMethod = null;
            s_starPass = null;
            s_starProbeInstalled = false;
            s_starFailed = false;
            s_starPresented = 0;
            s_starStatus = "star probe not installed";
        }
    }
}

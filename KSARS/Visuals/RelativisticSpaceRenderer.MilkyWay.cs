using System.Linq.Expressions;
using System.Reflection;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using RenderCore;

namespace KSARS.Visuals
{
    // Relativistic Milky Way background, injected at the native
    // MilkyWayRenderer.RenderMilkyWay seam. The prefix records the owned
    // adapted-fragment draw and suppresses the native fullscreen draw only
    // for the call in which every check and the recording itself succeeded;
    // any failure returns the native draw unchanged. Opt-in via
    // KSARS_MILKYWAY_PROBE=1.
    internal static partial class RelativisticSpaceRenderer
    {
        private static MethodInfo? s_milkyWayRenderMethod;
        private static RelativisticMilkyWayPass? s_milkyWayPass;
        private static bool s_milkyWayProbeInstalled;
        private static bool s_milkyWayFailed;
        private static long s_milkyWayPresented;
        private static ThermalSpectrumTable? s_milkyWayTable;
        private static string s_milkyWayStatus = "milky way probe not installed";

        // members is the validated baseline map; the milky way probe resolves
        // GlobalShaderBindings::_currentFrame itself when the source probe did
        // not run PrepareProbe, so KSARS_MILKYWAY_PROBE=1 works standalone.
        internal static bool TryInstallMilkyWayProbe(Harmony harmony,
            IReadOnlyDictionary<string, MemberInfo>? members = null)
        {
            if (s_milkyWayProbeInstalled)
            {
                return true;
            }

            s_milkyWayStatus = "";
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
                            "milky way probe unsupported: KSA.GlobalShaderBindings::_currentFrame member mapping missing; observer capture unavailable");
                    }

                    s_getGlobalFrameIndex = Expression.Lambda<Func<int>>(Expression.Field(null, currentFrame)).Compile();
                }

                FieldInfo? rendererField = typeof(Program).GetField("_milkyWayRenderer",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (rendererField is null || rendererField.IsInitOnly)
                {
                    throw new InvalidOperationException(
                        "milky way probe unsupported: KSA.Program::_milkyWayRenderer field missing");
                }

                Type rendererType = rendererField.FieldType;
                MethodInfo? render = rendererType.GetMethod("RenderMilkyWay",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                ParameterInfo[]? renderParameters = render?.GetParameters();
                if (render is null || render.ReturnType != typeof(void)
                    || renderParameters is not { Length: 3 }
                    || renderParameters[0].ParameterType != typeof(CommandBuffer)
                    || renderParameters[1].ParameterType != typeof(IViewport)
                    || renderParameters[2].ParameterType != typeof(int))
                {
                    throw new InvalidOperationException(
                        "milky way probe unsupported: MilkyWayRenderer::RenderMilkyWay(CommandBuffer,IViewport,int) signature changed");
                }

                if (rendererType.GetField("_textureSet",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is not { } textureSetField
                    || textureSetField.FieldType != typeof(VkDescriptorSet)
                    || rendererType.GetField("_textureSetLayout",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is not { } textureSetLayoutField
                    || textureSetLayoutField.FieldType != typeof(DescriptorSetLayoutEx))
                {
                    throw new InvalidOperationException(
                        "milky way probe unsupported: MilkyWayRenderer texture set fields missing or mismatched");
                }

                MethodInfo? prefix = typeof(RelativisticSpaceRenderer).GetMethod(
                    nameof(MilkyWayRenderPrefix), BindingFlags.NonPublic | BindingFlags.Static);
                if (prefix is null || prefix.ReturnType != typeof(bool))
                {
                    throw new InvalidOperationException("milky way probe unsupported: prefix missing or mismatched");
                }

                harmony.Patch(render, prefix: new HarmonyMethod(prefix));
                s_milkyWayRenderMethod = render;
                s_milkyWayProbeInstalled = true;
                s_milkyWayFailed = false;
                s_milkyWayStatus = "milky way probe installed: adapted native fragment armed (inverse aberration + Doppler-shifted effective-temperature band); native draw suppressed only per successfully recorded call";
                Console.WriteLine($"KSARS: {s_milkyWayStatus}");
                return true;
            }
            catch (Exception ex)
            {
                s_milkyWayStatus = $"milky way probe disabled: {ex.GetType().Name}: {ex.Message}; native milky way retained";
                Console.WriteLine($"KSARS: {s_milkyWayStatus}");
                return false;
            }
        }

        // Returning false suppresses the native fullscreen draw for this call
        // only. Any failure path returns true so the native Milky Way renders
        // unchanged.
        private static bool MilkyWayRenderPrefix(object __instance, CommandBuffer __0,
            IViewport __1, int __2)
        {
            if (!s_milkyWayProbeInstalled || s_milkyWayFailed || s_recordingDisabled)
            {
                return true;
            }

            try
            {
                if (!ReferenceEquals(__1, Program.MainViewport))
                {
                    return true;
                }

                if (!TryCaptureEffectiveObserver(out _, out PresentationVelocity observer))
                {
                    return true;
                }

                if (s_milkyWayPass is not { } pass)
                {
                    Core.Renderer? renderer = Program.GetRenderer();
                    RenderTarget? target = Program.OffscreenTarget;
                    if (renderer is null || target is null
                        || !TryGetMilkyWayTextureSet(__instance, out VkDescriptorSet textureSet,
                            out VkDescriptorSetLayout textureSetLayout))
                    {
                        return true;
                    }

                    s_milkyWayTable ??= ThermalSpectrumTable.LoadEmbedded();
                    pass = new RelativisticMilkyWayPass(renderer, target, s_milkyWayTable,
                        textureSet, textureSetLayout);
                    s_milkyWayPass = pass;
                    Console.WriteLine("KSARS: milky way probe pass allocated against borrowed offscreen target");
                }

                pass.Record(__0, __1, in observer);
                s_milkyWayPresented++;
                s_milkyWayStatus = $"milky way probe presented: inverse aberration + Doppler-shifted effective-temperature band active; native draw skipped this call only; GPU output unverified{(s_betaOverrideEnabled ? $"; OBSERVER OVERRIDE beta={s_betaOverride:G12}" : "")}";
                return false;
            }
            catch (Exception ex)
            {
                s_milkyWayFailed = true;
                s_milkyWayPass = null;
                s_milkyWayStatus = $"milky way probe disabled after record fault ({ex.GetType().Name}: {ex.Message}); native milky way retained";
                Console.WriteLine($"KSARS: {s_milkyWayStatus}");
                return true;
            }
        }

        // The native texture set and its layout are instance fields on the
        // live MilkyWayRenderer; binding them keeps the owned pass sampling
        // the same cubemap and generated noise without duplicating texture
        // ownership.
        private static bool TryGetMilkyWayTextureSet(object instance,
            out VkDescriptorSet textureSet, out VkDescriptorSetLayout textureSetLayout)
        {
            textureSet = default;
            textureSetLayout = default;
            Type type = instance.GetType();
            if (type.GetField("_textureSet",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(instance)
                    is VkDescriptorSet set
                && type.GetField("_textureSetLayout",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(instance)
                    is DescriptorSetLayoutEx layout)
            {
                textureSet = set;
                textureSetLayout = (VkDescriptorSetLayout)layout;
                return !set.IsNull() && !layout.IsNull();
            }

            return false;
        }

        // Called from OnRendererRebuilt before verified-idle retirement so the
        // pass joins the same retire list as the other owned passes.
        private static void CollectMilkyWayPassResources(List<IDisposable> resources)
        {
            if (s_milkyWayPass is { } pass)
            {
                resources.Add(pass);
            }
        }

        internal static void ResetMilkyWayProbeState()
        {
            s_milkyWayRenderMethod = null;
            s_milkyWayPass = null;
            s_milkyWayProbeInstalled = false;
            s_milkyWayFailed = false;
            s_milkyWayPresented = 0;
            s_milkyWayTable = null;
            s_milkyWayStatus = "milky way probe not installed";
        }
    }
}

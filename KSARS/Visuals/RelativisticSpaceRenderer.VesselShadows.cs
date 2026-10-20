using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSA.Rendering.Lighting;
using RenderCore;

namespace KSARS.Visuals
{
    // Relativistic vessel self-shadows. The native cascaded shadow map is a
    // shared resource: CascadedShadowSystem::UpdateUniforms computes its
    // cascade view projections from the rest sun position and the same map is
    // sampled by terrain and vessels, so the vessel-lighting UBO swap cannot
    // re-aim the shadows part casts onto other parts. This probe owns a
    // second CascadedShadowSystem whose cascades are computed from the
    // aberrated (apparent) sun direction - the direction photons actually
    // propagate along in the observer's frame - and recorded with the native
    // part/spline CSM casters before the main color pass. Inside the four
    // vessel writers the single Program::GetCSMSystem call is rewritten to
    // GetCsmForVesselWriter, which returns the owned system while an owned map
    // was recorded for this render pass and the untouched native system
    // otherwise; non-main branches on the same local still resolve through
    // the owned system's own unshadowed set. Inside the two
    // DepthData::AddCommandsCSM casters the same call is rewritten to
    // GetCsmForDepthPass, which only substitutes while the owned pass is
    // recording so the native CSM pass keeps native bindings. Recording is
    // driven from a postfix on PlanetTransparenciesRenderer::UpdateRingMeshes,
    // which runs inside the main render command buffer after the native
    // cascade pass and before the offscreen color pass begins. Opt-in via
    // KSARS_SHADOW_PROBE=1 on top of KSARS_LIGHTING_PROBE=1. SuperMesh pool
    // casters stay native-excluded (the bucket mixes world meshes); planet
    // terrain is never drawn into the owned map.
    internal static partial class RelativisticSpaceRenderer
    {
        private static MethodInfo? s_nativeGetCsm;
        private static MethodInfo? s_csmForWriter;
        private static MethodInfo? s_csmForDepthPass;
        private static MethodInfo? s_shadowRingUpdate;
        private static FieldInfo? s_shadowPointSamplerField;
        private static FieldInfo? s_shadowSamplerField;
        private static bool s_shadowProbeInstalled;
        private static bool s_shadowFailed;
        private static bool s_shadowAnchorMissed;
        private static bool s_vesselShadowActive;
        private static bool s_recordingOwnedShadow;
        private static CascadedShadowSystem? s_ownedCsm;
        private static long s_shadowPresented;
        private static string s_shadowStatus = "vessel shadow probe not installed";

        internal static bool TryInstallVesselShadowProbe(Harmony harmony)
        {
            if (s_shadowProbeInstalled)
            {
                return true;
            }

            s_shadowStatus = "";
            try
            {
                if (!s_lightingProbeInstalled)
                {
                    throw new InvalidOperationException(
                        "vessel shadow probe requires the vessel lighting probe (KSARS_LIGHTING_PROBE=1)");
                }

                s_nativeGetCsm = typeof(Program).GetMethod(
                    nameof(Program.GetCSMSystem), BindingFlags.Public | BindingFlags.Static);
                if (s_nativeGetCsm is null || s_nativeGetCsm.ReturnType != typeof(CascadedShadowSystem)
                    || s_nativeGetCsm.GetParameters().Length != 0)
                {
                    throw new InvalidOperationException(
                        "vessel shadow probe unsupported: Program::GetCSMSystem() signature changed");
                }

                s_csmForWriter = RequiredShadowSelector(nameof(GetCsmForVesselWriter));
                s_csmForDepthPass = RequiredShadowSelector(nameof(GetCsmForDepthPass));

                s_shadowRingUpdate = typeof(PlanetTransparenciesRenderer).GetMethod(
                    nameof(PlanetTransparenciesRenderer.UpdateRingMeshes),
                    BindingFlags.Public | BindingFlags.Instance);
                if (s_shadowRingUpdate is null || s_shadowRingUpdate.ReturnType != typeof(void)
                    || s_shadowRingUpdate.GetParameters() is not { Length: 3 } ringParameters
                    || ringParameters[0].ParameterType != typeof(CommandBuffer)
                    || ringParameters[1].ParameterType != typeof(IViewport)
                    || ringParameters[2].ParameterType != typeof(int))
                {
                    throw new InvalidOperationException(
                        "vessel shadow probe unsupported: PlanetTransparenciesRenderer::UpdateRingMeshes(CommandBuffer,IViewport,int) signature changed");
                }

                MethodInfo? recordPostfix = typeof(RelativisticSpaceRenderer).GetMethod(
                    nameof(AfterRingMeshUpdate), BindingFlags.NonPublic | BindingFlags.Static);
                if (recordPostfix is null || recordPostfix.ReturnType != typeof(void)
                    || recordPostfix.GetParameters() is not { Length: 3 } postfixParameters
                    || postfixParameters[0].ParameterType != typeof(CommandBuffer)
                    || postfixParameters[1].ParameterType != typeof(IViewport)
                    || postfixParameters[2].ParameterType != typeof(int))
                {
                    throw new InvalidOperationException(
                        "vessel shadow probe unsupported: AfterRingMeshUpdate postfix missing or mismatched");
                }

                MethodInfo? writerTranspiler = typeof(RelativisticSpaceRenderer).GetMethod(
                    nameof(TranspileWriterCsmBinding), BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo? depthTranspiler = typeof(RelativisticSpaceRenderer).GetMethod(
                    nameof(TranspileShadowCasterCsmBinding), BindingFlags.NonPublic | BindingFlags.Static);
                if (!IsInstructionTranspiler(writerTranspiler) || !IsInstructionTranspiler(depthTranspiler))
                {
                    throw new InvalidOperationException(
                        "vessel shadow probe unsupported: CSM-binding transpilers missing or mismatched");
                }

                s_shadowPointSamplerField = RequiredProgramSampler("_shadowPointSamplerHandle");
                s_shadowSamplerField = RequiredProgramSampler("_shadowSamplerHandle");

                MethodInfo[] writers =
                {
                    RequiredVesselWriter(typeof(PartModelRenderer.ColorData), "WriteCommands"),
                    RequiredVesselWriter(typeof(PartModelGlass.Shared), "WriteCommandsColor"),
                    RequiredVesselWriter(typeof(SplineRenderer), "WriteCommands"),
                    RequiredVesselWriter(typeof(GenericMeshRenderer), "WriteCommands"),
                };
                MethodInfo[] casters =
                {
                    RequiredShadowCaster(typeof(PartModelRenderer.DepthData)),
                    RequiredShadowCaster(typeof(SplineRenderer.DepthData)),
                };

                s_shadowAnchorMissed = false;
                var patched = new List<(MethodInfo Target, MethodInfo Transpiler)>();
                try
                {
                    foreach (MethodInfo target in writers)
                    {
                        harmony.Patch(target, transpiler: new HarmonyMethod(writerTranspiler));
                        if (s_shadowAnchorMissed)
                        {
                            throw new InvalidOperationException(
                                $"vessel shadow probe unsupported: {target.DeclaringType?.FullName}::{target.Name} does not call Program::GetCSMSystem exactly once");
                        }

                        patched.Add((target, writerTranspiler));
                    }

                    foreach (MethodInfo target in casters)
                    {
                        harmony.Patch(target, transpiler: new HarmonyMethod(depthTranspiler));
                        if (s_shadowAnchorMissed)
                        {
                            throw new InvalidOperationException(
                                $"vessel shadow probe unsupported: {target.DeclaringType?.FullName}::{target.Name} does not call Program::GetCSMSystem exactly once");
                        }

                        patched.Add((target, depthTranspiler));
                    }

                    harmony.Patch(s_shadowRingUpdate, postfix: new HarmonyMethod(recordPostfix));
                }
                catch
                {
                    foreach ((MethodInfo target, MethodInfo transpiler) in patched)
                    {
                        try
                        {
                            harmony.Unpatch(target, transpiler);
                        }
                        catch
                        {
                        }
                    }

                    try
                    {
                        harmony.Unpatch(s_shadowRingUpdate, recordPostfix);
                    }
                    catch
                    {
                    }

                    throw;
                }

                s_shadowProbeInstalled = true;
                s_shadowFailed = false;
                s_vesselShadowActive = false;
                s_recordingOwnedShadow = false;
                s_shadowStatus = "vessel shadow probe installed: owned cascaded shadow map rendered with the aberrated sun direction for part+spline casters and bound into the four vessel writers; supermesh casters and terrain excluded; native CSM retained everywhere else";
                Console.WriteLine($"KSARS: {s_shadowStatus}");
                return true;
            }
            catch (Exception ex)
            {
                s_shadowStatus = $"vessel shadow probe disabled: {ex.GetType().Name}: {ex.Message}; native cascaded shadows retained";
                Console.WriteLine($"KSARS: {s_shadowStatus}");
                return false;
            }
        }

        private static bool IsInstructionTranspiler([NotNullWhen(true)] MethodInfo? method)
            => method is not null
                && method.ReturnType == typeof(IEnumerable<CodeInstruction>)
                && method.GetParameters() is { Length: 1 } parameters
                && parameters[0].ParameterType == typeof(IEnumerable<CodeInstruction>);

        private static MethodInfo RequiredShadowSelector(string name)
        {
            MethodInfo? method = typeof(RelativisticSpaceRenderer).GetMethod(
                name, BindingFlags.NonPublic | BindingFlags.Static);
            if (method is null || method.ReturnType != typeof(CascadedShadowSystem)
                || method.GetParameters().Length != 0)
            {
                throw new InvalidOperationException(
                    $"vessel shadow probe unsupported: {name}() missing or mismatched");
            }

            return method;
        }

        private static MethodInfo RequiredShadowCaster(Type declaringType)
        {
            MethodInfo? method = declaringType.GetMethod(
                "AddCommandsCSM", BindingFlags.Public | BindingFlags.Static);
            if (method is null || method.ReturnType != typeof(void)
                || method.GetParameters() is not { Length: 4 } parameters
                || parameters[0].ParameterType != typeof(CommandBuffer)
                || parameters[1].ParameterType != typeof(IViewport)
                || parameters[2].ParameterType != typeof(int)
                || parameters[3].ParameterType != typeof(int))
            {
                throw new InvalidOperationException(
                    $"vessel shadow probe unsupported: {declaringType.FullName}::AddCommandsCSM(CommandBuffer,IViewport,int,int) signature changed");
            }

            return method;
        }

        private static FieldInfo RequiredProgramSampler(string name)
        {
            FieldInfo? field = typeof(Program).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field is null || field.FieldType != typeof(int))
            {
                throw new InvalidOperationException(
                    $"vessel shadow probe unsupported: KSA.Program::{name} member mapping missing; shadow samplers unavailable");
            }

            return field;
        }

        internal static IEnumerable<CodeInstruction> TranspileWriterCsmBinding(
            IEnumerable<CodeInstruction> instructions)
        {
            return RewriteCsmCall(instructions, s_csmForWriter, "vessel writer");
        }

        internal static IEnumerable<CodeInstruction> TranspileShadowCasterCsmBinding(
            IEnumerable<CodeInstruction> instructions)
        {
            return RewriteCsmCall(instructions, s_csmForDepthPass, "shadow caster");
        }

        private static List<CodeInstruction> RewriteCsmCall(IEnumerable<CodeInstruction> instructions,
            MethodInfo? replacement, string site)
        {
            // Must stay total: Harmony re-runs remaining transpilers while
            // unpatching stacked patches on these methods, which can happen
            // after teardown cleared the install fields.
            List<CodeInstruction> list = instructions.ToList();
            if (s_nativeGetCsm is null || replacement is null)
            {
                return list;
            }

            int anchors = 0;
            int anchorIndex = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == System.Reflection.Emit.OpCodes.Call
                    && list[i].operand is MethodInfo method && method.Equals(s_nativeGetCsm))
                {
                    anchors++;
                    anchorIndex = i;
                }
            }

            if (anchors != 1)
            {
                s_shadowAnchorMissed = true;
                Console.WriteLine($"KSARS: vessel shadow probe anchor mismatch: expected 1 Program.GetCSMSystem call in {site}, found {anchors}");
                return list;
            }

            list[anchorIndex].operand = replacement;
            return list;
        }

        // Substitute for Program::GetCSMSystem inside the four patched vessel
        // writers. While an owned map was recorded for this render pass the
        // owned system is returned so DescriptorSet/DescriptorDynamicOffset
        // (and UnShadowed* on the non-main branch) all resolve to owned
        // resources; every other case returns the native system.
        private static CascadedShadowSystem GetCsmForVesselWriter()
            => s_vesselShadowActive && !s_shadowFailed && s_ownedCsm is { } owned
                ? owned
                : Program.GetCSMSystem();

        // Substitute inside PartModelRenderer/SplineRenderer DepthData
        // AddCommandsCSM. The native cascade pass and every other caller see
        // the flag cleared and keep the native system; only the owned pass
        // recording gets owned cascade matrices and the owned descriptor set.
        private static CascadedShadowSystem GetCsmForDepthPass()
            => s_recordingOwnedShadow && !s_shadowFailed && s_ownedCsm is { } owned
                ? owned
                : Program.GetCSMSystem();

        // Postfix on PlanetTransparenciesRenderer::UpdateRingMeshes: the last
        // trio-carrying call inside the main render record after the native
        // cascade pass and before the offscreen color pass begins, so the
        // owned shadow pass lands in the same command buffer ahead of every
        // vessel writer that samples it.
        private static void AfterRingMeshUpdate(CommandBuffer inCommandBuffer,
            IViewport inViewport, int frameIndex)
        {
            try
            {
                TryRecordVesselShadowPass(inCommandBuffer, inViewport, frameIndex);
            }
            catch (Exception ex)
            {
                s_recordingOwnedShadow = false;
                s_vesselShadowActive = false;
                s_shadowFailed = true;
                s_shadowStatus = $"vessel shadow probe disabled after record fault ({ex.GetType().Name}: {ex.Message}); native cascaded shadows retained";
                Console.WriteLine($"KSARS: {s_shadowStatus}");
            }
        }

        private static void TryRecordVesselShadowPass(CommandBuffer commandBuffer,
            IViewport viewport, int frameIndex)
        {
            s_vesselShadowActive = false;
            if (!s_shadowProbeInstalled || s_shadowFailed || s_recordingDisabled)
            {
                return;
            }

            if (Program.MainViewport is not { } main || viewport.ShaderSlot != main.ShaderSlot)
            {
                return;
            }

            if (!GameSettings.ShowVesselInCascadeShadows())
            {
                s_shadowStatus = "vessel shadow probe idle: vessel-in-cascade shadows disabled in game settings; native cascaded shadows retained";
                return;
            }

            if (!TryCaptureEffectiveObserver(out ObserverSnapshot? snapshot, out PresentationVelocity observer)
                || snapshot is null || !ReferenceEquals(snapshot.MainViewport, main))
            {
                s_shadowStatus = "vessel shadow probe waiting: observer capture unavailable; native cascaded shadows retained";
                return;
            }

            if (observer.Q == 1.0)
            {
                s_shadowStatus = "vessel shadow probe idle: observer at rest; owned map identical, native cascaded shadows retained";
                return;
            }

            // The light view direction must follow the apparent sun: photons
            // propagate along the aberrated direction in the observer frame.
            // The rest sun position comes from this frame's lighting snapshot
            // (ego coordinates) and is aberration-shifted about the followed
            // vehicle exactly like the direct-lighting path.
            float4 sunPosition = snapshot.LightingData.SunPositionRadius;
            double3 vesselEgo = snapshot.MainCamera.GetPositionEgo(snapshot.FollowedVehicle);
            var origin = new float3((float)vesselEgo.X, (float)vesselEgo.Y, (float)vesselEgo.Z);
            if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
            {
                s_shadowStatus = "vessel shadow probe idle: vessel ego origin not finite; native cascaded shadows retained";
                return;
            }

            var restSunOffset = new OpticalVector(
                sunPosition.X - origin.X, sunPosition.Y - origin.Y, sunPosition.Z - origin.Z);
            if (!TryAberrateEgoOffset(restSunOffset, in observer, out OpticalVector aberratedOffset))
            {
                s_shadowStatus = "vessel shadow probe idle: aberration transform failed this frame; native cascaded shadows retained";
                return;
            }

            var aberratedSunEgo = new double3(
                origin.X + aberratedOffset.X, origin.Y + aberratedOffset.Y, origin.Z + aberratedOffset.Z);
            if (!double.IsFinite(aberratedSunEgo.X) || !double.IsFinite(aberratedSunEgo.Y)
                || !double.IsFinite(aberratedSunEgo.Z))
            {
                s_shadowStatus = "vessel shadow probe idle: aberrated sun position not finite; native cascaded shadows retained";
                return;
            }

            if (s_ownedCsm is null && !TryCreateOwnedCsm())
            {
                return;
            }

            Camera camera = snapshot.MainCamera;
            s_ownedCsm!.UpdateUniforms(in camera, aberratedSunEgo, frameIndex);
            IReadOnlyList<int> cascades = s_ownedCsm.CascadesToRenderThisFrame;

            s_recordingOwnedShadow = true;
            try
            {
                for (int i = 0; i < cascades.Count; i++)
                {
                    int cascade = cascades[i];
                    s_ownedCsm.BeginCascade(commandBuffer, cascade);
                    try
                    {
                        PartModelRenderer.DepthData.AddCommandsCSM(commandBuffer, viewport, frameIndex, cascade);
                        SplineRenderer.DepthData.AddCommandsCSM(commandBuffer, viewport, frameIndex, cascade);
                    }
                    finally
                    {
                        s_ownedCsm.EndCascade(commandBuffer, cascade);
                    }
                }

                s_ownedCsm.DispatchPostFilterPass(commandBuffer);
            }
            finally
            {
                s_recordingOwnedShadow = false;
            }

            s_vesselShadowActive = true;
            s_shadowPresented++;
            double beta = observer.Speed / PhysicsConstants.SpeedOfLight;
            s_shadowStatus = $"vessel shadow probe presented: owned {s_ownedCsm.CascadeCount}-cascade map recorded with aberrated sun direction; beta={beta:G6} cascades={cascades.Count} frames={s_shadowPresented}; part+spline casters only, supermesh/terrain excluded{(s_betaOverrideEnabled ? $"; OBSERVER OVERRIDE beta={s_betaOverride:G12}" : "")}";
        }

        private static bool TryCreateOwnedCsm()
        {
            try
            {
                Program? instance = Program.Instance;
                GpuTextureSystem? textureSystem = instance?.TextureSystem;
                if (instance is null || textureSystem is null)
                {
                    s_shadowStatus = "vessel shadow probe waiting: program or texture system unavailable; native cascaded shadows retained";
                    return false;
                }

                if (s_shadowPointSamplerField?.GetValue(instance) is not int pointSampler
                    || s_shadowSamplerField?.GetValue(instance) is not int linearSampler)
                {
                    s_shadowStatus = "vessel shadow probe waiting: native shadow sampler handles unavailable; native cascaded shadows retained";
                    return false;
                }

                CascadedShadowSystem.Settings settings = Program.GetCSMSystem().CurrentSettings;
                s_ownedCsm = new CascadedShadowSystem(instance, textureSystem,
                    pointSampler, linearSampler, settings);
                Console.WriteLine($"KSARS: vessel shadow probe owned cascaded shadow system allocated ({settings.CascadeCount} cascades, native settings snapshot)");
                return true;
            }
            catch (Exception ex)
            {
                s_shadowFailed = true;
                s_shadowStatus = $"vessel shadow probe resource fault ({ex.GetType().Name}: {ex.Message}); native cascaded shadows retained";
                Console.WriteLine($"KSARS: {s_shadowStatus}");
                return false;
            }
        }

        // Called from OnRendererRebuilt before verified-idle retirement so the
        // owned shadow system joins the same retire list as the other owned
        // passes. The active marker is cleared because the retired descriptor
        // sets are no longer bindable.
        private static void CollectVesselShadowResources(List<IDisposable> resources)
        {
            if (s_ownedCsm is { } owned)
            {
                resources.Add(owned);
            }

            s_vesselShadowActive = false;
        }

        internal static void ResetVesselShadowProbeState()
        {
            s_nativeGetCsm = null;
            s_csmForWriter = null;
            s_csmForDepthPass = null;
            s_shadowRingUpdate = null;
            s_shadowPointSamplerField = null;
            s_shadowSamplerField = null;
            s_shadowProbeInstalled = false;
            s_shadowFailed = false;
            s_shadowAnchorMissed = false;
            s_vesselShadowActive = false;
            s_recordingOwnedShadow = false;
            s_ownedCsm = null;
            s_shadowPresented = 0;
            s_shadowStatus = "vessel shadow probe not installed";
        }
    }
}

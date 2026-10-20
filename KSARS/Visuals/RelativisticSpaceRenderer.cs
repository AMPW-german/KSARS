using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using Brutal;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSA.Rendering.Rings.Rendering;
using RenderCore;
using SyncObjects = Brutal.VulkanApi.Abstractions.SyncObjects;

namespace KSARS.Visuals
{
    internal static partial class RelativisticSpaceRenderer
    {
        private const int SourceTileSize = 512;
        private const int SourceGuardPixels = 8;
        private const int MaxCaptures = 2;
        private const int MaxFrozenCaptures = ProbeCaptureGeneration.MaxGenerations * (MaxCaptures + 1);
        private const double TinyBodyPixelCutoff = 6.0;

        private static MethodInfo? s_nativeClear;
        private static MethodInfo? s_wrapper;
        private static MethodInfo? s_nativeRun;
        private static MethodInfo? s_nativeSphere;
        private static MethodInfo? s_runWrapper;
        private static MethodInfo? s_sphereWrapper;
        private static MethodInfo? s_nativeSunDraw;
        private static MethodInfo? s_sunDrawWrapper;
        private static MethodInfo? s_nativeSunbloom;
        private static MethodInfo? s_sunbloomWrapper;
        private static Func<int>? s_getGlobalFrameIndex;
        private static Func<StaticCelestial, DistantSphereRenderer?>? s_getDistantRenderer;
        private static Func<DistantSphereRenderer, DistantSphereMaterialData>? s_getMaterial;
        private static Func<Core.Renderer, SyncObjects>? s_getSyncObjects;

        private static MainAtlasFrame?[]? s_mainFrames;
        private static SyncObjects[]? s_mainRing;
        private static MainAtlasFrame? s_preparedMainFrame;
        private static bool s_mainProbeEnabled;
        private static bool s_mainFailed;
        private static string s_mainStatus = "main probe not enabled";
        private static string s_mainSolarStatus = "main solar not prepared; native Sun path retained";
        private static FieldInfo? s_sunflareMemoryField;
        private static FieldInfo? s_sunflareBuffersField;
        private static FieldInfo? s_sunflareFrameCountField;
        private static SolarGlowShaderSources? s_solarGlareSources;
        private static bool s_solarGlareCtorFailed;
        private static string s_solarGlareStatus = "solar glare pass not recorded";
        private static bool s_mainSolarPrepared;
        private static bool s_mainSolarPresented;
        private static string s_mainLastRejection = "none";
        private static readonly MainAtlasOwnership s_mainOwnership = new();

        private static MethodInfo? s_tinyBegin;
        private static MethodInfo? s_tinyFilter;
        private static IViewport? s_tinyActiveViewport;
        private static int s_tinyActiveFrameIndex = -1;
        private static ObserverSnapshot? s_tinyMainSnapshot;
        private static PresentationVelocity s_tinyMainObserver;
        private static readonly Dictionary<object, double> s_tinyMainOmitted = new(ReferenceEqualityComparer.Instance);
        private static bool s_tinyFilterFailed;
        private static string s_tinyFailureStatus = "none";

        private static readonly List<ProbeCapture> s_captures = new();
        private static readonly List<ProbeCapture> s_captureArchive = new();
        private static readonly ProbeCaptureGeneration s_captureGeneration = new();
        private static AtlasCompositeProbe?[]? s_composites;
        private static AtlasCompositeProbe? s_previewComposite;
        private static SolarProbeCapture? s_solarCapture;
        private static readonly List<SolarProbeCapture> s_solarArchive = new();
        private static ObserverSnapshot? s_lastSnapshot;
        private static string s_lastCandidateReason = "no candidate evaluated";
        private static string s_status = "source probe not prepared";
        private static string s_compositeStatus = "composite probe not recorded";
        private static string s_solarStatus = "solar probe not recorded";
        private static string s_solarCoverageStatus = "solar coverage probe not recorded";
        private static string s_solarDepthInspectionStatus = "solar depth inspection not recorded";
        private static bool s_recordingDisabled;
        private static bool s_constraintLogged;
        private static bool s_compositeFailed;
        private static bool s_solarFailed;
        private static bool s_firstMainRecordLogged;
        private static MethodInfo? s_rebuildMethod;
        private static bool s_rebuildBoundaryVerified;
        private static int s_rendererEpoch;
        private static bool s_mainRetryAfterRebuild;
        private static bool s_compositeRetryAfterRebuild;
        private static int s_compositeDebugMode;
        private static int s_compositeDebugBody;
        private static bool s_betaOverrideEnabled;
        private static double s_betaOverride = 0.9;
        private static double s_effectiveBeta;
        private static bool s_overrideUsedCameraDirection;
        private static string s_axisDiagnostic = "axis diagnostic not computed";

        internal static void PrepareProbe(IReadOnlyDictionary<string, MemberInfo> members)
        {
            s_rebuildMethod = null;
            s_rebuildBoundaryVerified = false;
            s_tinyBegin = null;
            s_tinyFilter = null;
            ClearTinyDistanceState();
            VerifyCoreRendererIdentity();

            if (members is null
                || !members.TryGetValue("KSA.Program::RenderGame", out MemberInfo? member)
                || member is not MethodInfo renderGame
                || renderGame.DeclaringType != typeof(Program)
                || renderGame.IsStatic
                || renderGame.IsAbstract)
            {
                throw new InvalidOperationException("source probe unsupported: KSA.Program::RenderGame member mapping missing or mismatched");
            }

            ParameterInfo[] parameters = renderGame.GetParameters();
            if (parameters.Length != 2
                || parameters[0].ParameterType != typeof(AcquiredFrame)
                || parameters[1].ParameterType != typeof(double))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.Program::RenderGame signature changed");
            }

            if (!members.TryGetValue("KSA.Program::RebuildRenderer", out MemberInfo? rebuildMember)
                || rebuildMember is not MethodInfo rebuildMethod
                || rebuildMethod.DeclaringType != typeof(Program)
                || rebuildMethod.IsStatic
                || rebuildMethod.IsAbstract)
            {
                throw new InvalidOperationException("source probe unsupported: KSA.Program::RebuildRenderer member mapping missing or mismatched");
            }

            ParameterInfo[] rebuildParameters = rebuildMethod.GetParameters();
            if (rebuildMethod.ReturnType != typeof(void)
                || rebuildParameters.Length != 1
                || rebuildParameters[0].ParameterType != typeof(bool))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.Program::RebuildRenderer signature changed");
            }

            List<CodeInstruction> rebuildInstructions;
            try
            {
                rebuildInstructions = PatchProcessor.GetOriginalInstructions(rebuildMethod);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"source probe unsupported: KSA.Program::RebuildRenderer original instructions unreadable ({ex.GetType().Name}: {ex.Message})", ex);
            }

            var rebuildCalls = new List<(string DeclaringType, string Name, string ReturnType)>();
            var rebuildTail = new List<(string OpCode, string? Member)>();
            int rebuildReturnCount = 0;
            foreach (CodeInstruction instruction in rebuildInstructions)
            {
                if (instruction.opcode == OpCodes.Ret)
                {
                    rebuildReturnCount++;
                }

                if (instruction.opcode == OpCodes.Nop)
                {
                    continue;
                }

                rebuildTail.Add((instruction.opcode.ToString()!, MemberName(instruction.operand)));
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && instruction.operand is MethodInfo callMethod)
                {
                    rebuildCalls.Add((callMethod.DeclaringType?.FullName ?? string.Empty,
                        callMethod.Name, callMethod.ReturnType.FullName ?? string.Empty));
                }
            }

            if (!RenderCompatibility.TryValidateRebuildBoundary(rebuildCalls, rebuildReturnCount,
                    rebuildTail.TakeLast(8).ToList(), out string rebuildReason))
            {
                throw new InvalidOperationException($"source probe unsupported: KSA.Program::RebuildRenderer idle boundary {rebuildReason}");
            }

            if (!members.TryGetValue("KSA.GlobalShaderBindings::_currentFrame", out MemberInfo? frameMember)
                || frameMember is not FieldInfo currentFrame
                || currentFrame.DeclaringType != typeof(GlobalShaderBindings)
                || !currentFrame.IsStatic
                || currentFrame.FieldType != typeof(int))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.GlobalShaderBindings::_currentFrame field missing or mismatched");
            }

            FieldInfo? distantRendererField = typeof(StaticCelestial).GetField("_distantRenderer",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (distantRendererField is null || distantRendererField.FieldType != typeof(DistantSphereRenderer))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.StaticCelestial::_distantRenderer field missing or mismatched");
            }

            FieldInfo? materialField = typeof(DistantSphereRenderer).GetField("_material",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (materialField is null || materialField.FieldType != typeof(DistantSphereMaterialData))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.DistantSphereRenderer::_material field missing or mismatched");
            }

            MethodInfo? nativeClear = typeof(RenderImageEx).GetMethod(nameof(RenderImageEx.ClearDepthImages),
                BindingFlags.Public | BindingFlags.Static);
            if (nativeClear is null || nativeClear.ReturnType != typeof(void)
                || !ParametersAre(nativeClear, typeof(RenderTarget), typeof(CommandBuffer)))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.Rendering.RenderImageEx::ClearDepthImages(RenderTarget,CommandBuffer) missing or mismatched");
            }

            MethodInfo? wrapper = typeof(RelativisticSpaceRenderer).GetMethod(nameof(ClearDepthAndRecordSource),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (wrapper is null || !ParametersAre(wrapper, typeof(RenderTarget), typeof(CommandBuffer)))
            {
                throw new InvalidOperationException("source probe unsupported: ClearDepthAndRecordSource wrapper missing");
            }

            MethodInfo? nativeRun = typeof(StaticCelestialDistanceRendering).GetMethod(nameof(StaticCelestialDistanceRendering.Run),
                BindingFlags.Public | BindingFlags.Static);
            if (nativeRun is null || nativeRun.ReturnType != typeof(void)
                || !ParametersAre(nativeRun, typeof(CommandBuffer), typeof(IViewport), typeof(int)))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.StaticCelestialDistanceRendering::Run signature changed");
            }

            MethodInfo? nativeSphere = typeof(StaticCelestial).GetMethod(nameof(StaticCelestial.RenderSphere),
                BindingFlags.Public | BindingFlags.Instance);
            if (nativeSphere is null || nativeSphere.ReturnType != typeof(void)
                || !ParametersAre(nativeSphere, typeof(IViewport), typeof(CommandBuffer), typeof(int)))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.StaticCelestial::RenderSphere signature changed");
            }

            MethodInfo? runWrapper = typeof(RelativisticSpaceRenderer).GetMethod(nameof(RunNativeDistanceAndComposite),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (runWrapper is null || !ParametersAre(runWrapper, typeof(CommandBuffer), typeof(IViewport), typeof(int)))
            {
                throw new InvalidOperationException("source probe unsupported: RunNativeDistanceAndComposite wrapper missing");
            }

            MethodInfo? sphereWrapper = typeof(RelativisticSpaceRenderer).GetMethod(nameof(RenderNativeSphereOrAtlas),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (sphereWrapper is null || !ParametersAre(sphereWrapper,
                    typeof(StaticCelestial), typeof(IViewport), typeof(CommandBuffer), typeof(int)))
            {
                throw new InvalidOperationException("source probe unsupported: RenderNativeSphereOrAtlas wrapper missing");
            }

            MethodInfo? nativeSunDraw = typeof(KSA.Rendering.Sun.SunRenderer).GetMethod(
                nameof(KSA.Rendering.Sun.SunRenderer.Draw),
                BindingFlags.Public | BindingFlags.Instance);
            if (nativeSunDraw is null || nativeSunDraw.ReturnType != typeof(void)
                || !ParametersAre(nativeSunDraw, typeof(RenderCore.Systems.ViewHandle)))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.Rendering.Sun.SunRenderer::Draw signature changed");
            }

            MethodInfo? sunDrawWrapper = typeof(RelativisticSpaceRenderer).GetMethod(nameof(QueueNativeSunOrAtlas),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (sunDrawWrapper is null || !ParametersAre(sunDrawWrapper,
                    typeof(KSA.Rendering.Sun.SunRenderer), typeof(RenderCore.Systems.ViewHandle)))
            {
                throw new InvalidOperationException("source probe unsupported: QueueNativeSunOrAtlas wrapper missing");
            }

            MethodInfo? nativeSunbloom = typeof(SunbloomRenderer).GetMethod(
                nameof(SunbloomRenderer.Render), BindingFlags.Public | BindingFlags.Instance);
            if (nativeSunbloom is null || nativeSunbloom.ReturnType != typeof(void)
                || !ParametersAre(nativeSunbloom, typeof(CommandBuffer), typeof(IViewport), typeof(int)))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.SunbloomRenderer::Render signature changed");
            }

            MethodInfo? sunbloomWrapper = typeof(RelativisticSpaceRenderer).GetMethod(nameof(RenderSunbloomOrAtlas),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (sunbloomWrapper is null || !ParametersAre(sunbloomWrapper,
                    typeof(SunbloomRenderer), typeof(CommandBuffer), typeof(IViewport), typeof(int)))
            {
                throw new InvalidOperationException("source probe unsupported: RenderSunbloomOrAtlas wrapper missing");
            }

            if (!members.TryGetValue("KSA.SunbloomRenderer::_sunflareMemory", out MemberInfo? sunflareMemoryMember)
                || sunflareMemoryMember is not FieldInfo sunflareMemoryField
                || sunflareMemoryField.DeclaringType != typeof(SunbloomRenderer)
                || sunflareMemoryField.FieldType != typeof(Brutal.VulkanApi.Abstractions.MappedMemory))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.SunbloomRenderer::_sunflareMemory field missing or mismatched");
            }

            if (!members.TryGetValue("KSA.SunbloomRenderer::_sunflareBuffers", out MemberInfo? sunflareBuffersMember)
                || sunflareBuffersMember is not FieldInfo sunflareBuffersField
                || sunflareBuffersField.DeclaringType != typeof(SunbloomRenderer)
                || sunflareBuffersField.FieldType != typeof(Brutal.VulkanApi.Abstractions.BufferEx))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.SunbloomRenderer::_sunflareBuffers field missing or mismatched");
            }

            if (!members.TryGetValue("KSA.SunbloomRenderer::_frameCount", out MemberInfo? sunflareFrameCountMember)
                || sunflareFrameCountMember is not FieldInfo sunflareFrameCountField
                || sunflareFrameCountField.DeclaringType != typeof(SunbloomRenderer)
                || sunflareFrameCountField.FieldType != typeof(int))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.SunbloomRenderer::_frameCount field missing or mismatched");
            }

            MethodInfo? sunMeshDraw = typeof(KSA.Rendering.Sun.SunMeshRenderable).GetMethod(
                nameof(KSA.Rendering.Sun.SunMeshRenderable.Draw),
                BindingFlags.Public | BindingFlags.Instance);
            if (sunMeshDraw is null || sunMeshDraw.ReturnType != typeof(void))
            {
                throw new InvalidOperationException("source probe unsupported: KSA.Rendering.Sun.SunMeshRenderable::Draw signature changed");
            }

            List<CodeInstruction> sunDrawInstructions;
            List<CodeInstruction> sunMeshInstructions;
            try
            {
                sunDrawInstructions = PatchProcessor.GetOriginalInstructions(nativeSunDraw);
                sunMeshInstructions = PatchProcessor.GetOriginalInstructions(sunMeshDraw);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"source probe unsupported: native Sun queue instructions unreadable ({ex.GetType().Name}: {ex.Message})", ex);
            }

            if (!RenderCompatibility.TryValidateSolarQueueBoundary(
                    CallNamesOf(sunDrawInstructions), CallNamesOf(sunMeshInstructions),
                    out string solarQueueReason))
            {
                throw new InvalidOperationException($"source probe unsupported: native Sun queue boundary {solarQueueReason}");
            }

            s_mainProbeEnabled = Environment.GetEnvironmentVariable("KSARS_ATLAS_MAIN_PROBE") != "0";
            s_mainStatus = s_mainProbeEnabled
                ? "main probe armed; waiting for fenced frame slot; <=2 resolved bodies, resolved captures require native >6px; observed <=6px bodies omitted (nonphysical visibility simplification), D^3 + 5800K chroma shift + finite HDR clamp 65504; ordering + VVL verified, no radiance calibration claim"
                : "main probe disabled";

            if (s_mainProbeEnabled)
            {
                MethodInfo? nativeDistanceData = typeof(StaticCelestialDistanceRendering).GetMethod(
                    nameof(StaticCelestialDistanceRendering.UpdateRenderData), BindingFlags.Public | BindingFlags.Static);
                MethodInfo? tinyBegin = typeof(RelativisticSpaceRenderer).GetMethod(
                    nameof(BeginNativeDistanceData), BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo? tinyFilter = typeof(RelativisticSpaceRenderer).GetMethod(
                    nameof(FilterNativeDistantOrbiter), BindingFlags.NonPublic | BindingFlags.Static);
                if (nativeDistanceData is null || nativeDistanceData.ReturnType != typeof(void)
                    || !ParametersAre(nativeDistanceData, typeof(IViewport), typeof(int))
                    || tinyBegin is null || tinyBegin.ReturnType != typeof(void)
                    || !ParametersAre(tinyBegin, typeof(IViewport), typeof(int))
                    || tinyFilter is null || tinyFilter.ReturnType != typeof(IOrbiter)
                    || !ParametersAre(tinyFilter, typeof(object), typeof(IViewport), typeof(int)))
                {
                    throw new InvalidOperationException("source probe unsupported: KSA.StaticCelestialDistanceRendering::UpdateRenderData or tiny filter helpers missing or mismatched");
                }

                s_tinyBegin = tinyBegin;
                s_tinyFilter = tinyFilter;
            }

            s_getGlobalFrameIndex = Expression.Lambda<Func<int>>(Expression.Field(null, currentFrame)).Compile();

            ParameterExpression bodyParameter = Expression.Parameter(typeof(StaticCelestial), "body");
            s_getDistantRenderer = Expression.Lambda<Func<StaticCelestial, DistantSphereRenderer?>>(
                Expression.Field(bodyParameter, distantRendererField), bodyParameter).Compile();
            ParameterExpression rendererParameter = Expression.Parameter(typeof(DistantSphereRenderer), "renderer");
            s_getMaterial = Expression.Lambda<Func<DistantSphereRenderer, DistantSphereMaterialData>>(
                Expression.Field(rendererParameter, materialField), rendererParameter).Compile();

            s_nativeClear = nativeClear;
            s_wrapper = wrapper;
            s_nativeRun = nativeRun;
            s_nativeSphere = nativeSphere;
            s_runWrapper = runWrapper;
            s_sphereWrapper = sphereWrapper;
            s_nativeSunDraw = nativeSunDraw;
            s_sunDrawWrapper = sunDrawWrapper;
            s_nativeSunbloom = nativeSunbloom;
            s_sunbloomWrapper = sunbloomWrapper;
            s_sunflareMemoryField = sunflareMemoryField;
            s_sunflareBuffersField = sunflareBuffersField;
            s_sunflareFrameCountField = sunflareFrameCountField;
            s_rebuildMethod = rebuildMethod;
            s_rebuildBoundaryVerified = true;
            s_status = "source probe prepared; waiting for main render boundary";
        }

        internal static void OnRendererRebuilt()
        {
            s_previewComposite = null;
            s_preparedMainFrame = null;
            s_lastSnapshot = null;
            s_axisDiagnostic = "axis diagnostic unavailable pending renderer rebuild";
            s_mainOwnership.Reset();
            ClearTinyDistanceState();

            if (!s_rebuildBoundaryVerified || s_rebuildMethod is null)
            {
                s_recordingDisabled = true;
                s_mainFailed = true;
                s_compositeFailed = true;
                s_mainStatus = "main probe disabled: renderer rebuild observed without a verified idle boundary; no resources retired (idle unproven); retained arrays left for teardown; restart required";
                s_compositeStatus = "composite probe disabled: renderer rebuild observed without a verified idle boundary; no resources retired (idle unproven); retained arrays left for teardown; restart required";
                s_starFailed = true;
                s_starStatus = "star probe disabled: renderer rebuild observed without a verified idle boundary; retained pass left for teardown; native stars retained";
                s_milkyWayFailed = true;
                s_milkyWayStatus = "milky way probe disabled: renderer rebuild observed without a verified idle boundary; retained pass left for teardown; native milky way retained";
                s_lightingFailed = true;
                s_lightingStatus = "vessel lighting probe disabled: renderer rebuild observed without a verified idle boundary; retained resources left for teardown; native vessel lighting retained";
                s_shadowFailed = true;
                s_shadowStatus = "vessel shadow probe disabled: renderer rebuild observed without a verified idle boundary; retained resources left for teardown; native cascaded shadows retained";
                s_status = "source probe fatal: renderer rebuild observed without a verified idle boundary; no resources retired (idle unproven); restart required";
                Console.WriteLine($"KSARS: {s_status}");
                return;
            }

            var resources = new List<IDisposable>();
            if (s_composites is not null)
            {
                resources.AddRange(s_composites.Where(c => c is not null)!);
            }

            if (s_mainFrames is not null)
            {
                resources.AddRange(s_mainFrames.Where(f => f is not null)!);
            }

            CollectStarPassResources(resources);
            CollectMilkyWayPassResources(resources);
            CollectLightingResources(resources);
            CollectVesselShadowResources(resources);

            if (!FrameResourceRetirement.TryRetireAfterIdle(resources, out AggregateException? error))
            {
                s_recordingDisabled = true;
                s_mainFailed = true;
                s_compositeFailed = true;
                s_mainStatus = $"main probe disabled: rebuild retirement failed after native idle ({error!.InnerExceptions.Count} error(s)); retained arrays left for teardown; restart required";
                s_compositeStatus = $"composite probe disabled: rebuild retirement failed after native idle ({error.InnerExceptions.Count} error(s)); retained arrays left for teardown; restart required";
                s_status = $"source probe fatal: per-frame resource retirement incomplete at renderer epoch {s_rendererEpoch} rebuild boundary ({error.Message}); arrays retained for teardown; restart required";
                Console.WriteLine($"KSARS: {s_status}");
                return;
            }

            s_composites = null;
            s_mainFrames = null;
            s_mainRing = null;
            if (s_starPass is not null)
            {
                s_starPass = null;
                s_starStatus = "star probe pass retired at verified idle rebuild; recreates on the next eligible star draw";
            }
            if (s_milkyWayPass is not null)
            {
                s_milkyWayPass = null;
                s_milkyWayStatus = "milky way probe pass retired at verified idle rebuild; recreates on the next eligible milky way draw";
            }
            if (s_lightingResources is not null)
            {
                s_lightingResources = null;
                s_lightingStatus = "vessel lighting probe resources retired at verified idle rebuild; recreates on the next eligible vessel write";
            }
            if (s_ownedCsm is not null)
            {
                s_ownedCsm = null;
                s_shadowStatus = "vessel shadow probe system retired at verified idle rebuild; recreates on the next eligible shadow record";
            }
            s_rendererEpoch++;
            s_firstMainRecordLogged = false;
            s_mainSolarPrepared = false;
            s_mainSolarPresented = false;
            s_mainSolarStatus = "main solar waiting: per-slot resources retired at verified idle rebuild; native Sun path retained";
            s_solarGlareCtorFailed = false;
            s_solarGlareStatus = "solar glare pass retired at verified idle rebuild; waiting for current frame";
            if (!s_recordingDisabled)
            {
                if (s_mainRetryAfterRebuild)
                {
                    s_mainFailed = false;
                }

                if (s_compositeRetryAfterRebuild)
                {
                    s_compositeFailed = false;
                }
            }

            s_mainRetryAfterRebuild = false;
            s_compositeRetryAfterRebuild = false;
            bool mainResume = s_mainProbeEnabled && !s_recordingDisabled && !s_mainFailed;
            bool compositeResume = !s_recordingDisabled && !s_compositeFailed;
            if (mainResume)
            {
                s_mainStatus = $"main probe waiting: renderer epoch {s_rendererEpoch} rebuilt through verified native idle; per-slot frame resources eligible to recreate at the next acquired slot";
            }

            if (compositeResume)
            {
                s_compositeStatus = $"composite probe waiting: renderer epoch {s_rendererEpoch} rebuilt through verified native idle; per-slot composite resources eligible to recreate at the next acquired slot";
            }

            s_status = $"source probe renderer epoch {s_rendererEpoch}: rebuild returned through verified native idle; per-frame resources retired; frozen generations retained; mainResume={mainResume} compositeResume={compositeResume}";
            Console.WriteLine($"KSARS: {s_status}");
        }

        private static string? MemberName(object? operand) => operand switch
        {
            MethodInfo method => $"{method.DeclaringType?.FullName}::{method.Name}",
            FieldInfo field => $"{field.DeclaringType?.FullName}::{field.Name}",
            _ => null,
        };

        internal static IEnumerable<CodeInstruction> TranspileMainRenderGame(IEnumerable<CodeInstruction> instructions)
        {
            if (s_nativeClear is null || s_wrapper is null
                || s_nativeRun is null || s_nativeSphere is null
                || s_runWrapper is null || s_sphereWrapper is null
                || s_nativeSunDraw is null || s_sunDrawWrapper is null
                || s_nativeSunbloom is null || s_sunbloomWrapper is null)
            {
                throw new InvalidOperationException("source probe transpiler invoked before PrepareProbe");
            }

            List<CodeInstruction> list = instructions.ToList();
            var calls = new List<(string Type, string Name)>();
            int clearIndex = -1;
            int clearCount = 0;
            int beginIndex = -1;
            int beginCount = 0;
            int sunDrawIndex = -1;
            int sunDrawCount = 0;
            int runIndex = -1;
            int runCount = 0;
            int sphereIndex = -1;
            int sphereCount = 0;
            int sunbloomIndex = -1;
            int sunbloomCount = 0;
            int overallBloomIndex = -1;
            int overallBloomCount = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if ((list[i].opcode == OpCodes.Call || list[i].opcode == OpCodes.Callvirt)
                    && list[i].operand is MethodInfo method)
                {
                    calls.Add((method.DeclaringType?.FullName ?? string.Empty, method.Name));
                    if (list[i].opcode == OpCodes.Call && method.Equals(s_nativeClear))
                    {
                        clearCount++;
                        if (clearIndex < 0)
                        {
                            clearIndex = i;
                        }
                    }
                    else if (method.DeclaringType == typeof(RenderTarget)
                        && method.Name == nameof(RenderTarget.BeginRendering))
                    {
                        beginCount++;
                        if (beginIndex < 0)
                        {
                            beginIndex = i;
                        }
                    }
                    else if (method.Equals(s_nativeSunDraw))
                    {
                        sunDrawCount++;
                        if (sunDrawIndex < 0)
                        {
                            sunDrawIndex = i;
                        }
                    }
                    else if (list[i].opcode == OpCodes.Call && method.Equals(s_nativeRun))
                    {
                        runCount++;
                        if (runIndex < 0)
                        {
                            runIndex = i;
                        }
                    }
                    else if (method.Equals(s_nativeSphere))
                    {
                        sphereCount++;
                        if (sphereIndex < 0)
                        {
                            sphereIndex = i;
                        }
                    }
                    else if (method.Equals(s_nativeSunbloom))
                    {
                        sunbloomCount++;
                        if (sunbloomIndex < 0)
                        {
                            sunbloomIndex = i;
                        }
                    }
                    else if (method.DeclaringType == typeof(OverallBloomRenderer)
                        && method.Name == nameof(OverallBloomRenderer.Render))
                    {
                        overallBloomCount++;
                        if (overallBloomIndex < 0)
                        {
                            overallBloomIndex = i;
                        }
                    }
                }
            }

            if (clearCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: RenderImageEx.ClearDepthImages anchor count {clearCount}");
            }

            if (beginCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: RenderTarget.BeginRendering anchor count {beginCount}");
            }

            if (sunDrawCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: SunRenderer.Draw anchor count {sunDrawCount}");
            }

            if (runCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: StaticCelestialDistanceRendering.Run anchor count {runCount}");
            }

            if (sphereCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: StaticCelestial.RenderSphere anchor count {sphereCount}");
            }

            if (sunbloomCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: SunbloomRenderer.Render anchor count {sunbloomCount}");
            }

            if (overallBloomCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: OverallBloomRenderer.Render anchor count {overallBloomCount}");
            }

            if (!(clearIndex < beginIndex && beginIndex < sunDrawIndex && sunDrawIndex < runIndex
                && runIndex < sphereIndex && sphereIndex < sunbloomIndex && sunbloomIndex < overallBloomIndex))
            {
                throw new InvalidOperationException("source probe unsupported: render boundary anchors out of order");
            }

            int depthValue = int.MinValue;
            int colorValue = int.MinValue;
            int nonNop = 0;
            for (int i = beginIndex - 1; i >= 0 && nonNop < 2; i--)
            {
                if (list[i].opcode == OpCodes.Nop)
                {
                    continue;
                }

                int? value = IntegerConstant(list[i]);
                if (nonNop == 0)
                {
                    depthValue = value ?? int.MinValue;
                }
                else
                {
                    colorValue = value ?? int.MinValue;
                }
                nonNop++;
            }

            bool nativeDepthLoad = depthValue == 0 && colorValue == 1;
            if (!RenderCompatibility.TryValidateSourceBoundary(calls, nativeDepthLoad, out string reason))
            {
                throw new InvalidOperationException($"source probe unsupported: {reason}");
            }

            list[clearIndex].operand = s_wrapper;
            var sunDrawReplacement = new CodeInstruction(OpCodes.Call, s_sunDrawWrapper);
            sunDrawReplacement.labels.AddRange(list[sunDrawIndex].labels);
            sunDrawReplacement.blocks.AddRange(list[sunDrawIndex].blocks);
            list[sunDrawIndex] = sunDrawReplacement;
            list[runIndex].operand = s_runWrapper;
            var sphereReplacement = new CodeInstruction(OpCodes.Call, s_sphereWrapper);
            sphereReplacement.labels.AddRange(list[sphereIndex].labels);
            sphereReplacement.blocks.AddRange(list[sphereIndex].blocks);
            list[sphereIndex] = sphereReplacement;
            var sunbloomReplacement = new CodeInstruction(OpCodes.Call, s_sunbloomWrapper);
            sunbloomReplacement.labels.AddRange(list[sunbloomIndex].labels);
            sunbloomReplacement.blocks.AddRange(list[sunbloomIndex].blocks);
            list[sunbloomIndex] = sunbloomReplacement;
            return list;
        }

        internal static IEnumerable<CodeInstruction> TranspileNativeDistanceData(IEnumerable<CodeInstruction> instructions)
        {
            if (s_tinyBegin is null || s_tinyFilter is null)
            {
                throw new InvalidOperationException("tiny filter transpiler invoked before PrepareProbe");
            }

            List<CodeInstruction> list = instructions.ToList();
            int castIndex = -1;
            int castCount = 0;
            int appendStarsCount = 0;
            int diameterCount = 0;
            int blendCount = 0;
            int scaleCount = 0;
            int addCount = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Isinst && list[i].operand is Type castType
                    && castType == typeof(IOrbiter))
                {
                    castCount++;
                    if (castIndex < 0)
                    {
                        castIndex = i;
                    }
                }

                if ((list[i].opcode == OpCodes.Call || list[i].opcode == OpCodes.Callvirt)
                    && list[i].operand is MethodInfo method)
                {
                    if (method.DeclaringType == typeof(StaticCelestialDistanceRendering))
                    {
                        if (method.Name == "AppendStars")
                        {
                            appendStarsCount++;
                        }
                        else if (method.Name == "SpriteSphereBlend")
                        {
                            blendCount++;
                        }
                        else if (method.Name == "GetApparentSizeScale" && method.GetParameters().Length == 1)
                        {
                            scaleCount++;
                        }
                    }
                    else if (method.DeclaringType == typeof(Camera) && method.Name == "GetObjectDiameterPixels")
                    {
                        diameterCount++;
                    }
                    else if (method.Name == "Add" && method.DeclaringType is { Name: "DeviceVector" })
                    {
                        addCount++;
                    }
                }
            }

            if (castCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: UpdateRenderData IOrbiter cast anchor count {castCount}");
            }

            if (appendStarsCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: UpdateRenderData AppendStars anchor count {appendStarsCount}");
            }

            if (diameterCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: UpdateRenderData GetObjectDiameterPixels anchor count {diameterCount}");
            }

            if (blendCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: UpdateRenderData SpriteSphereBlend anchor count {blendCount}");
            }

            if (scaleCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: UpdateRenderData GetApparentSizeScale(IOrbiter) anchor count {scaleCount}");
            }

            if (addCount != 1)
            {
                throw new InvalidOperationException($"source probe unsupported: UpdateRenderData DeviceVector.Add anchor count {addCount}");
            }

            var castLoad = new CodeInstruction(OpCodes.Ldarg_0);
            castLoad.labels.AddRange(list[castIndex].labels);
            castLoad.blocks.AddRange(list[castIndex].blocks);
            list[castIndex].labels.Clear();
            list[castIndex].blocks.Clear();
            list[castIndex] = castLoad;
            list.Insert(castIndex + 1, new CodeInstruction(OpCodes.Ldarg_1));
            list.Insert(castIndex + 2, new CodeInstruction(OpCodes.Call, s_tinyFilter));

            var entryLoad = new CodeInstruction(OpCodes.Ldarg_0);
            entryLoad.labels.AddRange(list[0].labels);
            entryLoad.blocks.AddRange(list[0].blocks);
            list[0].labels.Clear();
            list[0].blocks.Clear();
            list.Insert(0, entryLoad);
            list.Insert(1, new CodeInstruction(OpCodes.Ldarg_1));
            list.Insert(2, new CodeInstruction(OpCodes.Call, s_tinyBegin));
            return list;
        }

        private static void BeginNativeDistanceData(IViewport viewport, int frameIndex)
        {
            s_tinyActiveViewport = null;
            s_tinyActiveFrameIndex = -1;

            if (!ReferenceEquals(viewport, Program.MainViewport))
            {
                return;
            }

            if (!s_mainProbeEnabled || s_mainFailed || s_recordingDisabled || s_tinyFilterFailed
                || s_getGlobalFrameIndex is null)
            {
                s_tinyMainSnapshot = null;
                s_tinyMainObserver = default;
                s_tinyMainOmitted.Clear();
                return;
            }

            try
            {
                if (!ObserverSnapshot.TryCapture(s_getGlobalFrameIndex, out ObserverSnapshot? snapshot, out _)
                    || snapshot is null
                    || snapshot.ResourceFrameIndex != frameIndex)
                {
                    s_tinyMainSnapshot = null;
                    s_tinyMainObserver = default;
                    s_tinyMainOmitted.Clear();
                    return;
                }

                s_tinyMainSnapshot = snapshot;
                s_tinyMainObserver = EffectiveObserver(snapshot);
                s_tinyMainOmitted.Clear();
                s_tinyActiveViewport = viewport;
                s_tinyActiveFrameIndex = frameIndex;
            }
            catch (Exception ex)
            {
                DisableTinyDistanceFilter(ex, "begin");
            }
        }

        private static IOrbiter? FilterNativeDistantOrbiter(object? source, IViewport viewport, int frameIndex)
        {
            if (source is not IOrbiter orbiter)
            {
                return null;
            }

            if (!s_mainProbeEnabled || s_mainFailed || s_recordingDisabled || s_tinyFilterFailed
                || s_tinyMainSnapshot is not { } snapshot
                || !ReferenceEquals(viewport, s_tinyActiveViewport)
                || frameIndex != s_tinyActiveFrameIndex
                || !ReferenceEquals(viewport, snapshot.MainViewport)
                || frameIndex != snapshot.ResourceFrameIndex
                || viewport.ShaderSlot is < 0 or > 7
                || GlobalShaderBindings.CameraData(viewport.ShaderSlot).Frame != snapshot.CameraData.Frame
                || s_getGlobalFrameIndex is null
                || s_getGlobalFrameIndex() != snapshot.GlobalFrameIndex)
            {
                return orbiter;
            }

            if (orbiter is not StaticCelestial body
                || ReferenceEquals(body, snapshot.MainCamera.GetEffectiveNearbyCelestial()))
            {
                return orbiter;
            }

            if (s_tinyMainOmitted.ContainsKey(body))
            {
                return null;
            }

            try
            {
                CelestialTextureStreamer? streamer = Program.GetPlanetRenderer()?.TextureStreamer;
                if (streamer is null
                    || !TryComputeMainViewCone(snapshot, out OpticalVector mainForward, out double viewCorner, out _)
                    || !TryBuildSourceCandidate(snapshot, s_tinyMainObserver, streamer, mainForward, viewCorner,
                            body, requireNativeSpherePixels: false,
                            out SourceCandidate candidate, out _, out _))
                {
                    return orbiter;
                }

                if (double.IsFinite(candidate.ObservedPixelDiameter)
                    && candidate.ObservedPixelDiameter <= TinyBodyPixelCutoff)
                {
                    s_tinyMainOmitted[body] = candidate.ObservedPixelDiameter;
                    return null;
                }

                return orbiter;
            }
            catch (Exception ex)
            {
                DisableTinyDistanceFilter(ex, "classify");
                return orbiter;
            }
        }

        private static void DisableTinyDistanceFilter(Exception ex, string phase)
        {
            s_tinyFilterFailed = true;
            s_tinyActiveViewport = null;
            s_tinyActiveFrameIndex = -1;
            if (s_tinyFailureStatus == "none")
            {
                s_tinyFailureStatus = $"main probe tiny omission disabled ({phase}: {ex.GetType().Name}: {ex.Message}); already-omitted bodies this frame retained for diagnostics; native fallback continues";
                Console.WriteLine($"KSARS: {s_tinyFailureStatus}");
            }
        }

        private static bool TinyClassificationsMatch(ObserverSnapshot snapshot, in PresentationVelocity observer)
        {
            if (s_tinyMainSnapshot is not { } tiny)
            {
                return false;
            }

            return ReferenceEquals(tiny.MainViewport, snapshot.MainViewport)
                && ReferenceEquals(tiny.MainCamera, snapshot.MainCamera)
                && ReferenceEquals(tiny.FollowedVehicle, snapshot.FollowedVehicle)
                && tiny.ResourceFrameIndex == snapshot.ResourceFrameIndex
                && tiny.GlobalFrameIndex == snapshot.GlobalFrameIndex
                && tiny.CameraData.Frame == snapshot.CameraData.Frame
                && tiny.CameraData.ScreenWidth == snapshot.CameraData.ScreenWidth
                && tiny.CameraData.ScreenHeight == snapshot.CameraData.ScreenHeight
                && tiny.CameraData.View.Equals(snapshot.CameraData.View)
                && tiny.CameraData.Projection.Equals(snapshot.CameraData.Projection)
                && tiny.PositionEcl.X == snapshot.PositionEcl.X
                && tiny.PositionEcl.Y == snapshot.PositionEcl.Y
                && tiny.PositionEcl.Z == snapshot.PositionEcl.Z
                && s_tinyMainObserver.Q == observer.Q
                && s_tinyMainObserver.Speed == observer.Speed
                && s_tinyMainObserver.Direction.X == observer.Direction.X
                && s_tinyMainObserver.Direction.Y == observer.Direction.Y
                && s_tinyMainObserver.Direction.Z == observer.Direction.Z;
        }

        private static void ClearTinyDistanceState()
        {
            s_tinyActiveViewport = null;
            s_tinyActiveFrameIndex = -1;
            s_tinyMainSnapshot = null;
            s_tinyMainObserver = default;
            s_tinyMainOmitted.Clear();
            s_tinyFilterFailed = false;
            s_tinyFailureStatus = "none";
        }

        private static string MainViewState => s_mainProbeEnabled
            ? (s_mainFailed
                ? "main view stock (main probe failed)"
                : "main view override armed: atlas main probe, <=2 resolved bodies, resolved captures require native >6px; observed <=6px bodies omitted (nonphysical visibility simplification), D^3 + 5800K chroma shift + finite HDR clamp 65504, GPU ordering + VVL verified, no radiance calibration claim")
            : "main view stock";

        internal static string GetStatus()
        {
            List<string> ownedBodyIds = s_preparedMainFrame is { Prepared: true } f && f.Bodies.Count > 0
                ? f.Bodies.Where(b => s_mainOwnership.Owns(f.Snapshot, b)).Select(b => b.Id).ToList()
                : new List<string>();
            string managedBodies = ownedBodyIds.Count > 0
                ? $"mainBodies=[{string.Join(",", ownedBodyIds)}]"
                : "mainBodies=none";
            List<string> culledBodyIds = s_mainOwnership.CulledBodies
                .OfType<StaticCelestial>()
                .Select(b => b.Id)
                .ToList();
            string culledBodies = culledBodyIds.Count > 0
                ? $"mainCulled=[{string.Join(",", culledBodyIds)}]"
                : "mainCulled=none";
            List<string> omittedBodyIds = s_mainOwnership.OmittedBodies
                .OfType<StaticCelestial>()
                .Select(b => s_tinyMainOmitted.TryGetValue(b, out double px) && double.IsFinite(px)
                    ? $"{b.Id}:{px:F2}px"
                    : $"{b.Id}:unrecorded")
                .ToList();
            string omittedBodies = omittedBodyIds.Count > 0
                ? $"mainOmitted=[{string.Join(",", omittedBodyIds)}] observed<=6px tiny distant-body omission: nonphysical visibility simplification"
                : "mainOmitted=none";
            bool tinyMatch = s_lastSnapshot is { } lastSnapshot
                && TinyClassificationsMatch(lastSnapshot, EffectiveObserver(lastSnapshot));
            List<string> nativeOmittedIds = tinyMatch
                ? s_tinyMainOmitted
                    .Where(kv => kv.Key is StaticCelestial && double.IsFinite(kv.Value))
                    .Select(kv => $"{((StaticCelestial)kv.Key).Id}:{kv.Value:F2}px")
                    .ToList()
                : new List<string>();
            string nativeOmitted = nativeOmittedIds.Count > 0
                ? $"nativeOmitted=[{string.Join(",", nativeOmittedIds)}] actual native sprite omissions this frame (observed<=6px; nonphysical visibility simplification)"
                : "nativeOmitted=none";
            string tinyFailure = $"tinyFilterFailure={s_tinyFailureStatus}";
            string frozen = $"frozen generation {s_captureGeneration.Generation + 1}/{ProbeCaptureGeneration.MaxGenerations} retained={s_captureArchive.Count + s_solarArchive.Count}";
            string epoch = $"rendererEpoch={s_rendererEpoch} boundaryVerified={s_rebuildBoundaryVerified}";
            string solarFlags = $"mainSolarPrepared={s_mainSolarPrepared} mainSolarPresented={s_mainSolarPresented}";
            return s_lastSnapshot is { } snapshot
                ? $"{s_status} | current observer={snapshot.FollowedVehicle.Id} nativeFrame={snapshot.CameraData.Frame} (frozen captures keep their own recorded epoch) | {s_mainStatus} | {s_mainSolarStatus} | {solarFlags} | {managedBodies} | {culledBodies} | {omittedBodies} | {nativeOmitted} | {tinyFailure} | mainFirstSubmissionRecorded={s_firstMainRecordLogged} | {epoch} | {s_solarStatus} | {s_solarCoverageStatus} | {s_solarDepthInspectionStatus} | {s_solarGlareStatus} | {s_starStatus} | {s_milkyWayStatus} | {s_lightingStatus} | {s_shadowStatus} | {MediaStatus} | {frozen} | {s_axisDiagnostic}"
                : $"{s_status} | {s_mainStatus} | {s_mainSolarStatus} | {solarFlags} | {managedBodies} | {culledBodies} | {omittedBodies} | {nativeOmitted} | {tinyFailure} | mainFirstSubmissionRecorded={s_firstMainRecordLogged} | {epoch} | {s_solarStatus} | {s_solarCoverageStatus} | {s_solarDepthInspectionStatus} | {s_solarGlareStatus} | {s_starStatus} | {s_milkyWayStatus} | {s_lightingStatus} | {s_shadowStatus} | {MediaStatus} | {frozen} | {s_axisDiagnostic}";
        }

        private static void DrawSpeedOverrideControls()
        {
            ImGui.Text($"current observer speed: beta={s_effectiveBeta:G9}");
            ImGui.Checkbox("Override observer speed", ref s_betaOverrideEnabled);
            if (!s_betaOverrideEnabled)
            {
                return;
            }

            double beta = s_betaOverride;
            double gamma = 1.0 / Math.Sqrt(1.0 - beta * beta);
            float gammaFloat = (float)Math.Min(gamma, 1e6);
            ImGui.SetNextItemWidth(220.0f);
            if (ImGui.SliderFloat("gamma (log)", ref gammaFloat, 1.0f, 1e6f,
                    flags: ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
            {
                double g = Math.Max(1.0, (double)gammaFloat);
                beta = Math.Sqrt(1.0 - 1.0 / (g * g));
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(130.0f);
            double betaInput = beta;
            // Full round-trip precision: extreme-beta test windows sit
            // below the default "%.6f" resolution.
            if (ImGui.InputDouble("##betaInput", ref betaInput, format: "%.17g"))
            {
                beta = betaInput;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(110.0f);
            double gammaInput = gamma;
            if (ImGui.InputDouble("gamma##gammaInput", ref gammaInput, format: "%.7g"))
            {
                double g = Math.Max(1.0, gammaInput);
                beta = Math.Sqrt(1.0 - 1.0 / (g * g));
            }

            s_betaOverride = Math.Clamp(beta, 0.0, 1.0 - 1e-15);
            beta = s_betaOverride;
            gamma = 1.0 / Math.Sqrt(1.0 - beta * beta);
            double q = Math.Sqrt((1.0 - beta) / (1.0 + beta));
#if DEBUG
            ImGui.Text($"beta={beta:G12} gamma={gamma:G6} q={q:G6} direction={(s_overrideUsedCameraDirection ? "camera forward" : "velocity")}");
#endif
        }

        internal static void DrawProbeGui()
        {
#if !DEBUG
            DrawSpeedOverrideControls();
            return;
#else
            ImGui.Separator();
            ImGui.Text("Frozen native source probe (raw HDR preview; no composite/calibration)");
            ImGui.Text(s_status);
            if (s_mainProbeEnabled)
            {
                ImGui.Separator();
                ImGui.Text("Main-pass atlas probe (diagnostic; <=2 resolved bodies + optional world sun; resolved captures require native >6px; observed <=6px bodies omitted (nonphysical visibility simplification); solar native 4R/2.75R scale preserved, opaque coverage models physical R only, corona premultiplied at simplified center-plane radial; D^3 + finite HDR clamp 65504; depth ordering GPU-unverified; not a production renderer)");
                ImGui.Text(s_mainStatus);
                ImGui.Text(s_mainSolarStatus);
            }

            ImGui.Text(s_axisDiagnostic);
            ImGui.Text(s_solarStatus);
            ImGui.Text(s_solarCoverageStatus);
            ImGui.Text(s_solarDepthInspectionStatus);
            if (s_starProbeInstalled)
            {
                ImGui.Text(s_starStatus);
            }
            if (s_milkyWayProbeInstalled)
            {
                ImGui.Text(s_milkyWayStatus);
            }
            if (s_lightingProbeInstalled)
            {
                ImGui.Text(s_lightingStatus);
            ImGui.Text(s_shadowStatus);
            }
            if (s_solarCapture is { } solarCapture)
            {
                double3 solarOrigin = solarCapture.Snapshot.PositionEcl;
                ImGui.Text($"star={solarCapture.SunId} origin=({solarOrigin.X:F0},{solarOrigin.Y:F0},{solarOrigin.Z:F0}) recorded nativeFrame={solarCapture.Snapshot.CameraData.Frame} resourceFrame={solarCapture.Snapshot.ResourceFrameIndex}; native mesh 4R / procedural 2.75R / physical R={solarCapture.SunRadius:F0}m; raw solar color/corona alpha only");
                if (solarCapture.Recorded && solarCapture.TextureRegistered)
                {
                    float2 solarSize = new(256.0f, 256.0f);
                    float2? uv0 = null;
                    float2? uv1 = null;
                    ImGui.Image(solarCapture.Texture, in solarSize, in uv0, in uv1);
                }

                if (solarCapture.Coverage is { TextureRegistered: true } coverage)
                {
                    ImGui.Text("Modeled solar coverage/depth (source-model geometry; not measured solar shader coverage, alpha, opacity or calibration):");
                    float2 coverageSize = new(256.0f, 256.0f);
                    float2? coverageUv0 = null;
                    float2? coverageUv1 = null;
                    ImGui.Image(coverage.Texture, in coverageSize, in coverageUv0, in coverageUv1);
                    ImGui.Text("legend: white=physical R geometric disc, cyan=nominal 2.75R comparison outside physical, blue=4R envelope outside nominal, black=outside envelope; depth channel modeled physical sphere only, 0 elsewhere");
                }

                if (solarCapture.DepthInspection is { TextureRegistered: true } inspection)
                {
                    ImGui.Text("Actual modeled solar depth attachment inspection (sampled D32, not native Sun opacity/calibration):");
                    float2 inspectionSize = new(768.0f, 256.0f);
                    float2? inspectionUv0 = null;
                    float2? inspectionUv1 = null;
                    ImGui.Image(inspection.Texture, in inspectionSize, in inspectionUv0, in inspectionUv1);
                    ImGui.Text("legend: left=actual coverage (white=depth>0); middle=depth-reconstructed physical frontness; right=reference error x1000 display only (green=low difference, magenta=expected coverage missing, red=unexpected coverage/large error, yellow=invalid sample); no GPU depth acceptance claim");
                }
            }

            ImGui.Separator();
            if (s_captureGeneration.BudgetExhausted)
            {
                ImGui.Text($"frozen generation {s_captureGeneration.Generation + 1}/{ProbeCaptureGeneration.MaxGenerations}; recapture budget exhausted (restart required)");
            }
            else
            {
                ImGui.Text($"frozen generation {s_captureGeneration.Generation + 1}/{ProbeCaptureGeneration.MaxGenerations}{(s_captureGeneration.Queued ? "; recapture queued" : "")}");
                if (ImGui.Button("Recapture sources at current observer"))
                {
                    s_captureGeneration.TryQueue();
                }
            }

            int frozenCount = s_captures.Count + s_captureArchive.Count
                + (s_solarCapture is null ? 0 : 1) + s_solarArchive.Count;
            ImGui.Text($"frozen capture budget {frozenCount}/{MaxFrozenCaptures} ({ProbeCaptureGeneration.MaxGenerations} generations x {MaxCaptures} body + 1 solar); solar source is raw color/corona alpha only - no opaque/depth/calibration semantics");
            DrawSpeedOverrideControls();

            if (s_captures.Count == 0)
            {
                return;
            }

            foreach (ProbeCapture capture in s_captures)
            {
                ImGui.Text($"body={capture.BodyId} nativeFrame={capture.Snapshot.CameraData.Frame} resourceFrame={capture.Snapshot.ResourceFrameIndex} globalFrame={capture.Snapshot.GlobalFrameIndex} heightFallback={capture.HeightFallback}");
                double3 captureOrigin = capture.Snapshot.PositionEcl;
                ImGui.Text($"snapshot origin=({captureOrigin.X:F0},{captureOrigin.Y:F0},{captureOrigin.Z:F0}); frozen capture - observer movement does not remap it");
                ImGui.Text($"observer={capture.Snapshot.FollowedVehicle.Id} q={capture.Snapshot.Observer.Q:G6} constrained={capture.Snapshot.Observer.WasConstrained}");
                if (capture.Recorded && capture.TextureRegistered)
                {
                    float2 size = new(256.0f, 256.0f);
                    float2? uv0 = null;
                    float2? uv1 = null;
                    ImGui.Image(capture.Texture, in size, in uv0, in uv1);
                }
                else
                {
                    ImGui.Text("(recording incomplete; frozen artifact retained, not a live view)");
                }
            }

            if (s_captures.Count > 0)
            {
                ImGui.Separator();
                ImGui.Text(s_compositeStatus);

                if (s_previewComposite is { TextureRegistered: true } previewComposite)
                {
                    ImGui.Text("debug mode: 0=composite 1=doppler 2=coverage 3=raw tile 4=body dir 5=velocity dir 6=depth coverage");
                    ImGui.SetNextItemWidth(90.0f);
                    ImGui.InputInt("mode", ref s_compositeDebugMode);
                    s_compositeDebugMode = Math.Clamp(s_compositeDebugMode, 0, 6);
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(90.0f);
                    ImGui.InputInt("body", ref s_compositeDebugBody);
                    s_compositeDebugBody = Math.Clamp(s_compositeDebugBody, 0, s_captures.Count - 1);

                    float2 size = new(640.0f, previewComposite.CompositeHeight);
                    float2? uv0 = null;
                    float2? uv1 = null;
                    ImGui.Image(previewComposite.Texture, in size, in uv0, in uv1);
                }
            }
#endif
        }

        internal static void Shutdown()
        {
            bool hasMainResources = s_mainFrames is not null && s_mainFrames.Any(f => f is not null);
            bool hasComposites = s_composites is not null && s_composites.Any(c => c is not null);
            if (s_captures.Count == 0 && s_captureArchive.Count == 0 && !hasComposites
                && s_solarCapture is null && s_solarArchive.Count == 0 && !hasMainResources
                && s_starPass is null && s_milkyWayPass is null && s_lightingResources is null
                && s_ownedCsm is null)
            {
                ResetProbeState("source probe shut down");
                return;
            }

            Core.Renderer? renderer = Program.GetRenderer();
            if (renderer is null)
            {
                throw TeardownBlocked("renderer unavailable; cannot prove GPU idle");
            }

            try
            {
                renderer.Device.WaitIdle();
            }
            catch (Exception ex)
            {
                throw TeardownBlocked($"device wait-idle failed ({ex.GetType().Name}: {ex.Message})", ex);
            }

            List<Exception>? errors = null;
            if (s_composites is not null)
            {
                foreach (AtlasCompositeProbe? composite in s_composites)
                {
                    if (composite is null)
                    {
                        continue;
                    }

                    try
                    {
                        composite.Dispose();
                    }
                    catch (Exception ex)
                    {
                        (errors ??= new List<Exception>()).Add(ex);
                    }
                }

                s_composites = null;
                s_previewComposite = null;
            }

            if (s_mainFrames is not null)
            {
                foreach (MainAtlasFrame? frame in s_mainFrames)
                {
                    if (frame is null)
                    {
                        continue;
                    }

                    try
                    {
                        frame.Dispose();
                    }
                    catch (Exception ex)
                    {
                        (errors ??= new List<Exception>()).Add(ex);
                    }
                }

                s_mainFrames = null;
            }

            if (s_starPass is { } starPass)
            {
                try
                {
                    starPass.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                s_starPass = null;
            }

            if (s_milkyWayPass is { } milkyWayPass)
            {
                try
                {
                    milkyWayPass.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                s_milkyWayPass = null;
            }

            if (s_lightingResources is { } lightingResources)
            {
                try
                {
                    lightingResources.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                s_lightingResources = null;
            }

            if (s_ownedCsm is { } ownedCsm)
            {
                try
                {
                    ownedCsm.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                s_ownedCsm = null;
            }

            foreach (ProbeCapture capture in s_captures.Concat(s_captureArchive))
            {
                try
                {
                    if (capture.TextureRegistered && capture.Backend is not null)
                    {
                        capture.Backend.RemoveTexture(capture.Texture);
                        capture.TextureRegistered = false;
                    }
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                try
                {
                    capture.Adapter.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                try
                {
                    capture.Context.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }
            }

            s_captures.Clear();
            s_captureArchive.Clear();

            foreach (SolarProbeCapture capture in
                     (s_solarCapture is null ? Enumerable.Empty<SolarProbeCapture>() : new[] { s_solarCapture })
                     .Concat(s_solarArchive))
            {
                try
                {
                    if (capture.TextureRegistered && capture.Backend is not null)
                    {
                        capture.Backend.RemoveTexture(capture.Texture);
                        capture.TextureRegistered = false;
                    }
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                try
                {
                    capture.DepthInspection?.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                try
                {
                    capture.Coverage?.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                try
                {
                    capture.Adapter.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                try
                {
                    capture.Context.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }
            }

            s_solarCapture = null;
            s_solarArchive.Clear();
            ResetProbeState(errors is null
                ? "source probe shut down"
                : $"source probe teardown incomplete: {errors.Count} per-entry failure(s) after verified idle");
            if (errors is not null)
            {
                throw new AggregateException("source probe teardown had per-entry failures", errors);
            }
        }

        private static InvalidOperationException TeardownBlocked(string detail, Exception? inner = null)
        {
            s_lastSnapshot = null;
            s_recordingDisabled = true;
            s_status = $"source probe teardown blocked: {detail}; captured resources retained pending GPU idle";
            return new InvalidOperationException($"source probe teardown unsupported: {detail}", inner);
        }

        private static void ResetProbeState(string status)
        {
            ResetStarProbeState();
            ResetMilkyWayProbeState();
            ResetLightingProbeState();
            ResetVesselShadowProbeState();
            s_captures.Clear();
            s_lastSnapshot = null;
            s_nativeClear = null;
            s_wrapper = null;
            s_nativeRun = null;
            s_nativeSphere = null;
            s_runWrapper = null;
            s_sphereWrapper = null;
            s_nativeSunDraw = null;
            s_sunDrawWrapper = null;
            s_nativeSunbloom = null;
            s_sunbloomWrapper = null;
            s_mainFrames = null;
            s_mainRing = null;
            s_preparedMainFrame = null;
            s_mainProbeEnabled = false;
            s_mainFailed = false;
            s_getSyncObjects = null;
            s_mainOwnership.Reset();
            s_tinyBegin = null;
            s_tinyFilter = null;
            ClearTinyDistanceState();
            s_mainStatus = "main probe not enabled";
            s_mainSolarPrepared = false;
            s_mainSolarPresented = false;
            s_mainSolarStatus = "main solar not prepared; native Sun path retained";
            s_mainLastRejection = "none";
            s_getGlobalFrameIndex = null;
            s_getDistantRenderer = null;
            s_getMaterial = null;
            s_recordingDisabled = false;
            s_constraintLogged = false;
            s_composites = null;
            s_previewComposite = null;
            s_compositeFailed = false;
            s_rebuildMethod = null;
            s_rebuildBoundaryVerified = false;
            s_rendererEpoch = 0;
            s_mainRetryAfterRebuild = false;
            s_compositeRetryAfterRebuild = false;
            s_captureArchive.Clear();
            s_captureGeneration.Reset();
            s_solarCapture = null;
            s_solarArchive.Clear();
            s_solarFailed = false;
            s_solarStatus = "solar probe not recorded";
            s_solarCoverageStatus = "solar coverage probe not recorded";
            s_solarDepthInspectionStatus = "solar depth inspection not recorded";
            s_firstMainRecordLogged = false;
            s_compositeDebugMode = 0;
            s_compositeDebugBody = 0;
            s_betaOverrideEnabled = false;
            s_overrideUsedCameraDirection = false;
            s_effectiveBeta = 0.0;
            s_axisDiagnostic = "axis diagnostic not computed";
            s_compositeStatus = "composite probe not recorded";
            s_lastCandidateReason = "no candidate evaluated";
            s_status = status;
        }

        private static void ClearDepthAndRecordSource(RenderTarget target, CommandBuffer commandBuffer)
        {
            RenderImageEx.ClearDepthImages(target, commandBuffer);
            s_mainOwnership.Reset();
            s_preparedMainFrame = null;
            s_mainSolarPrepared = false;
            s_mainSolarPresented = false;
            s_mainSolarStatus = "main solar waiting: no prepared replacement this frame; native Sun path retained";

            if (s_recordingDisabled
                || !ReferenceEquals(target, Program.OffscreenTarget)
                || !ReferenceEquals(Program.RenderedViewport, Program.MainViewport))
            {
                return;
            }

            try
            {
                TryRecordSources(commandBuffer);
                TryRecordComposite(commandBuffer);
            }
            catch (Exception ex)
            {
                s_recordingDisabled = true;
                s_lastSnapshot = null;
                if (s_captures.Any(c => !c.Adapter.RecordingRecovered) || MainRecordingUnbalanced()
                    || s_solarCapture is { Adapter.RecordingRecovered: false }
                    || s_solarCapture is { Coverage.RecordingRecovered: false }
                    || s_solarCapture is { DepthInspection.RecordingRecovered: false }
                    || s_solarArchive.Any(c => c.Coverage is { RecordingRecovered: false }
                        || c.DepthInspection is { RecordingRecovered: false })
                    || (s_composites?.Any(c => c is { RecordingRecovered: false }) ?? false))
                {
                    s_status = $"source probe recording failed ({ex.GetType().Name}: {ex.Message}); source pass left unbalanced; frame aborted; frozen captures retained for teardown";
                    Console.WriteLine($"KSARS: {s_status}");
                    throw;
                }

                s_status = $"source probe recording failed ({ex.GetType().Name}: {ex.Message}); frozen captures retained for teardown; stock rendering continues";
                Console.WriteLine($"KSARS: {s_status}");
            }

            try
            {
                PrepareMainFrameSources(commandBuffer);
            }
            catch (Exception ex)
            {
                s_mainFailed = true;
                s_mainRetryAfterRebuild = false;
                if (MainRecordingUnbalanced())
                {
                    s_mainStatus = $"main probe recording failed ({ex.GetType().Name}: {ex.Message}); source pass left unbalanced; frame aborted";
                    Console.WriteLine($"KSARS: {s_mainStatus}");
                    throw;
                }

                s_mainStatus = $"main probe recording failed ({ex.GetType().Name}: {ex.Message}); main probe disabled; stock rendering continues";
                Console.WriteLine($"KSARS: {s_mainStatus}");
            }
        }

        private static void TryRecordSources(CommandBuffer commandBuffer)
        {
            if (s_getGlobalFrameIndex is null)
            {
                return;
            }

            if (!ObserverSnapshot.TryCapture(s_getGlobalFrameIndex, out ObserverSnapshot? snapshot, out string reason)
                || snapshot is null)
            {
                s_lastSnapshot = null;
                s_axisDiagnostic = $"axis diagnostic unavailable: {reason}";
                s_status = s_captures.Count > 0
                    ? $"{s_captures.Count} frozen source(s) retained; source probe waiting: {reason}"
                    : $"source probe waiting: {reason}";
                return;
            }

            s_lastSnapshot = snapshot;
            if (snapshot.Observer.WasConstrained)
            {
                if (!s_constraintLogged)
                {
                    s_constraintLogged = true;
                    Console.WriteLine("KSARS: source probe observer velocity constrained to sub-luminal for capture");
                }
            }
            else
            {
                s_constraintLogged = false;
            }

            if (s_captureGeneration.TryConsume(hasValidSnapshot: true))
            {
                ArchiveFrozenSources();
            }

            TryRecordSolarSource(snapshot, commandBuffer);

            if (s_captures.Count >= MaxCaptures)
            {
                s_status = $"source probe complete: {s_captures.Count} frozen capture(s) recorded (generation {s_captureGeneration.Generation + 1}/{ProbeCaptureGeneration.MaxGenerations}); commands only, GPU output unverified; {MainViewState}; object-atlas/foreground gate pending";
                return;
            }

            if (TrySelectAndCapture(snapshot, commandBuffer))
            {
                s_status = $"source probe recorded {s_captures.Count} frozen capture(s); commands only, GPU output unverified; {MainViewState}; object-atlas/foreground gate pending";
            }
            else
            {
                s_status = s_captures.Count > 0
                    ? $"{s_captures.Count} frozen source(s) retained; source probe waiting: {s_lastCandidateReason}"
                    : $"source probe waiting: {s_lastCandidateReason}";
            }
        }

        private static bool TryComputeMainViewCone(ObserverSnapshot snapshot,
            out OpticalVector mainForward, out double viewCorner, out string reason)
        {
            mainForward = default;
            viewCorner = 0.0;
            float tanHalfVertical = 1.0f / MathF.Abs(snapshot.CameraData.Projection.M22);
            float tanHalfHorizontal = 1.0f / MathF.Abs(snapshot.CameraData.Projection.M11);
            if (!float.IsFinite(tanHalfVertical) || !float.IsFinite(tanHalfHorizontal))
            {
                reason = "nonfinite main projection tangents";
                return false;
            }

            viewCorner = Math.Atan(Math.Sqrt((double)tanHalfVertical * tanHalfVertical
                + (double)tanHalfHorizontal * tanHalfHorizontal));
            float4 forwardComponent = float4.Transform(new float4(0.0f, 0.0f, -1.0f, 0.0f), snapshot.CameraData.InverseView);
            if (!RelativisticOptics.TryNormalize(
                    new OpticalVector(forwardComponent.X, forwardComponent.Y, forwardComponent.Z),
                    out mainForward))
            {
                reason = "nonfinite main view axis";
                return false;
            }

            reason = "computed";
            return true;
        }

        private static bool TryBuildSourceCandidate(ObserverSnapshot snapshot, in PresentationVelocity observer,
            CelestialTextureStreamer streamer, OpticalVector mainForward, double viewCorner,
            StaticCelestial body, bool requireNativeSpherePixels,
            out SourceCandidate candidate, out string reason, out bool limitedByPixels)
        {
            candidate = default;
            reason = "no eligible source candidate";
            limitedByPixels = false;

            // Rest geometry only: the media probe may have rewritten
            // GetPositionEgo to apparent positions for native consumers, but
            // source candidates, transforms, and captures run on rest vectors.
            double3 relativePosition = body.GetPositionEcl() - snapshot.MainCamera.PositionEcl;
            if (!IsFinite(relativePosition))
            {
                return false;
            }

            DistantSphereRenderer? nativeRenderer = s_getDistantRenderer!(body);
            if (nativeRenderer is null)
            {
                return false;
            }

            DistantSphereMaterialData material = s_getMaterial!(nativeRenderer);
            material.DiffuseTextureId = streamer.GetDiffuseHandle(body);
            material.NormalTextureId = streamer.GetNormalHandle(body);
            PlanetaryRingsReference? rings = body.BodyTemplate.RingsReference;
            if (rings is not null)
            {
                double3 ringNormal = PlanetaryRingsRenderer.ComputeRingNormal(rings, body);
                material.RingNormal = new float3((float)ringNormal.X, (float)ringNormal.Y, (float)ringNormal.Z);
            }

            if (!float.IsFinite(material.MinHeight) || !float.IsFinite(material.MaxHeight))
            {
                reason = $"{body.Id}: nonfinite height bounds";
                return false;
            }

            int neutralHeightHandle = -1;
            if (material.HeightTextureId < 0 && material.MinHeight == 0.0f && material.MaxHeight == 0.0f)
            {
                TextureReference? neutralCube = TextureReference.EmptyCubemap;
                if (neutralCube?.Texture is not null
                    && neutralCube.TextureAsset?.Texture.IsCubemap == true
                    && neutralCube.BindlessHandle >= 0)
                {
                    neutralHeightHandle = neutralCube.BindlessHandle;
                }
            }

            bool heightFallback = false;
            if (!RenderCompatibility.TryResolveSourceHeightHandle(material.HeightTextureId,
                    material.MinHeight, material.MaxHeight, neutralHeightHandle, out int resolvedHeightHandle))
            {
                reason = $"{body.Id}: missing height source cannot use a neutral zero-displacement cube";
                return false;
            }

            if (resolvedHeightHandle != material.HeightTextureId)
            {
                material.HeightTextureId = resolvedHeightHandle;
                heightFallback = true;
            }

            if (material.DiffuseTextureId < 0 || material.NormalTextureId < 0
                || material.SamplerClampId < 0
                || material.ScatteringTextureId < 0 || material.SurgeTextureId < 0
                || (material.UseRingShadows != 0 && material.RingTextureId < 0))
            {
                reason = $"{body.Id}: unbound source texture handle (diffuse={material.DiffuseTextureId} normal={material.NormalTextureId} height={material.HeightTextureId} sampler={material.SamplerClampId} scattering={material.ScatteringTextureId} surge={material.SurgeTextureId} ringShadows={material.UseRingShadows} ring={material.RingTextureId})";
                return false;
            }

            if (!float.IsFinite(material.MeanDiffuseLuminosity) || material.MeanDiffuseLuminosity <= 0.0f)
            {
                reason = $"{body.Id}: nonpositive material mean diffuse luminosity";
                return false;
            }

            if (!double.IsFinite(body.MeanRadius) || body.MeanRadius <= 0.0)
            {
                reason = $"{body.Id}: nonpositive mean radius";
                return false;
            }

            double enclosingRadius = body.MeanRadius
                + Math.Max(0.0f, Math.Max(material.MinHeight, material.MaxHeight));
            if (!double.IsFinite(enclosingRadius) || enclosingRadius <= 0.0)
            {
                return false;
            }

            var restPosition = new OpticalVector(relativePosition.X, relativePosition.Y, relativePosition.Z);
            if (!AtlasProjection.TryCreate(restPosition, enclosingRadius, SourceTileSize, SourceGuardPixels,
                    out AtlasProjection projection))
            {
                return false;
            }

            if (requireNativeSpherePixels
                && !(snapshot.MainCamera.GetObjectDiameterPixels(2.0 * body.MeanRadius, projection.Distance) > 6.0))
            {
                reason = $"{body.Id}: below native sphere pixel threshold";
                limitedByPixels = true;
                return false;
            }

            if (!RelativisticOptics.TryTransformSphericalCap(restPosition, projection.RestAngularRadius,
                    observer, out ObservedSphericalCap cap))
            {
                return false;
            }

            double combinedRadius = cap.AngularRadius + viewCorner;
            bool observedVisible = combinedRadius >= Math.PI
                || cap.Axis.Dot(mainForward) >= Math.Cos(combinedRadius);

            doubleQuat ccf2Cce = body.GetCcf2Cce();
            floatQuat orientation = floatQuat.Pack(in ccf2Cce, v => (float)v);
            if (!TryCreateSourceCamera(snapshot, projection, out UboCameraData cameraData, out string cameraReason))
            {
                reason = $"{body.Id}: {cameraReason}";
                return false;
            }

            float4x4 world = float4x4.CreateScale((float)body.MeanRadius)
                * float4x4.CreateFromQuaternion(orientation)
                * float4x4.CreateTranslation(new float3(
                    (float)relativePosition.X, (float)relativePosition.Y, (float)relativePosition.Z));
            if (!IsFinite(world))
            {
                reason = $"{body.Id}: nonfinite source matrices";
                return false;
            }

            double observedPixelDiameter = double.NaN;
            float4x4 mainView = snapshot.CameraData.View;
            var capAxisView = new OpticalVector(
                cap.Axis.X * mainView.M11 + cap.Axis.Y * mainView.M21 + cap.Axis.Z * mainView.M31,
                cap.Axis.X * mainView.M12 + cap.Axis.Y * mainView.M22 + cap.Axis.Z * mainView.M32,
                -(cap.Axis.X * mainView.M13 + cap.Axis.Y * mainView.M23 + cap.Axis.Z * mainView.M33));
            double pixelScaleX = 0.5 * snapshot.CameraData.ScreenWidth
                * Math.Abs((double)snapshot.CameraData.Projection.M11);
            double pixelScaleY = 0.5 * snapshot.CameraData.ScreenHeight
                * Math.Abs((double)snapshot.CameraData.Projection.M22);
            if (AtlasProjection.TryGetObservedPixelDiameter(in cap, capAxisView,
                    pixelScaleX, pixelScaleY, out double boundedDiameter))
            {
                observedPixelDiameter = boundedDiameter;
            }

            var sourceData = new DistantSphereData { WorldMatrix = world, EmissiveStrength = 0.0f };
            candidate = new SourceCandidate(body, body.Id, projection, cameraData, material, sourceData,
                heightFallback, observedVisible, observedPixelDiameter);
            reason = "captured";
            return true;
        }

        private static bool TryCreateSourceCamera(ObserverSnapshot snapshot, AtlasProjection projection,
            out UboCameraData cameraData, out string reason)
        {
            cameraData = default;
            doubleQuat sourceRotation = Camera.LookAtRotation(
                new double3(projection.Forward.X, projection.Forward.Y, projection.Forward.Z),
                new double3(projection.Up.X, projection.Up.Y, projection.Up.Z)).Inverse();
            floatQuat sourceQuat = floatQuat.Pack(in sourceRotation, v => (float)v);
            float4x4 sourceView = float4x4.CreateFromQuaternion(sourceQuat);
            float4x4 sourceProjection = ReverseDepthBufferUtils.CreatePerspectiveFieldOfViewReverseZ(
                (float)(2.0 * Math.Atan(projection.TanHalfFov)), 1.0f,
                (float)projection.Near, (float)projection.Far);
            float4x4 sourceViewProjection = sourceView * sourceProjection;
            if (!float4x4.Invert(sourceView, out float4x4 inverseView)
                || !float4x4.Invert(sourceProjection, out float4x4 inverseProjection))
            {
                reason = "noninvertible source matrices";
                return false;
            }

            if (!IsFinite(sourceView) || !IsFinite(sourceProjection) || !IsFinite(sourceViewProjection)
                || !IsFinite(inverseView) || !IsFinite(inverseProjection))
            {
                reason = "nonfinite source matrices";
                return false;
            }

            cameraData = snapshot.CameraData;
            cameraData.View = sourceView;
            cameraData.Projection = sourceProjection;
            cameraData.ViewProjection = sourceViewProjection;
            cameraData.InverseView = inverseView;
            cameraData.InverseProjection = inverseProjection;
            cameraData.ScreenWidth = SourceTileSize;
            cameraData.ScreenHeight = SourceTileSize;
            cameraData.AspectRatio = 1.0f;
            cameraData.NearPlane = (float)projection.Near;
            cameraData.FarPlane = (float)projection.Far;
            reason = "computed";
            return true;
        }

        private static bool TrySelectAndCapture(ObserverSnapshot snapshot, CommandBuffer commandBuffer)
        {
            Celestial? nearby = snapshot.MainCamera.GetEffectiveNearbyCelestial();
            Core.Renderer? renderer = Program.GetRenderer();
            CelestialTextureStreamer? streamer = Program.GetPlanetRenderer()?.TextureStreamer;
            if (renderer is null || streamer is null)
            {
                s_lastCandidateReason = "renderer or texture streamer unavailable";
                return false;
            }

            if (!TryComputeMainViewCone(snapshot, out OpticalVector mainForward, out double viewCorner,
                    out string coneReason))
            {
                s_lastCandidateReason = coneReason;
                return false;
            }

            s_lastCandidateReason = "no eligible source candidate";
            PresentationVelocity observer = snapshot.Observer;
            foreach (StaticCelestial body in Universe.CurrentSystem!.All.OfType<StaticCelestial>())
            {
                if (s_captures.Count >= MaxCaptures)
                {
                    break;
                }

                if (ReferenceEquals(body, nearby) || s_captures.Any(c => ReferenceEquals(c.Body, body)))
                {
                    continue;
                }

                if (!TryBuildSourceCandidate(snapshot, in observer, streamer, mainForward, viewCorner, body,
                        requireNativeSpherePixels: false,
                        out SourceCandidate candidate, out string rejectReason, out _))
                {
                    s_lastCandidateReason = rejectReason;
                    continue;
                }

                if (!candidate.IsObservedVisible)
                {
                    s_lastCandidateReason = $"{body.Id}: supported source outside observed view";
                    continue;
                }

                ObjectCaptureContext? context = null;
                NativeCelestialCaptureAdapter? adapter = null;
                try
                {
                    context = new ObjectCaptureContext(renderer, SourceTileSize,
                        $"KSARS_SourceProbe_G{s_captureGeneration.Generation}_{s_captures.Count}",
                        candidate.Camera, snapshot.LightingData, snapshot.CelestialData, snapshot.VesselData,
                        snapshot.Constants, candidate.Material);
                    adapter = new NativeCelestialCaptureAdapter(renderer, context);
                }
                catch (Exception ex)
                {
                    adapter?.Dispose();
                    context?.Dispose();
                    throw new InvalidOperationException(
                        $"source probe unsupported: source resource creation failed for {body.Id}: {ex.Message}", ex);
                }

                var capture = new ProbeCapture
                {
                    Context = context,
                    Adapter = adapter,
                    Body = candidate.Body,
                    BodyId = candidate.BodyId,
                    Snapshot = snapshot,
                    Projection = candidate.Projection,
                    Recorded = false,
                    TextureRegistered = false,
                    HeightFallback = candidate.HeightFallback,
                };
                s_captures.Add(capture);

                adapter.Record(commandBuffer, context, candidate.DrawData);
                ImGuiBackendVulkanImpl backend = ImGuiBackend.Vulkan;
                RenderImage colorImage = context.Target.ColorImage
                    ?? throw new InvalidOperationException("source capture target has no color image");
                capture.Backend = backend;
                capture.Texture = backend.AddTexture(Program.LinearClampedSampler,
                    colorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
                capture.TextureRegistered = true;
                capture.Recorded = true;

                double3 origin = snapshot.PositionEcl;
                Console.WriteLine($"KSARS: source probe recorded capture {s_captures.Count}/{MaxCaptures} body={capture.BodyId} observer={snapshot.FollowedVehicle.Id} origin=({origin.X:F0},{origin.Y:F0},{origin.Z:F0}) resourceFrame={snapshot.ResourceFrameIndex} globalFrame={snapshot.GlobalFrameIndex} nativeCameraFrame={snapshot.CameraData.Frame} sourceFov={2.0 * Math.Atan(candidate.Projection.TanHalfFov):G6}rad heightFallback={capture.HeightFallback} formats=R32G32B32A32SFloat/D32SFloat viewportSlots={ViewportRegistry.Views.Length} commands-only; GPU output unverified");

                s_lastCandidateReason = "captured";
                return true;
            }

            return false;
        }

        private static void ArchiveFrozenSources()
        {
            s_captureArchive.AddRange(s_captures);
            s_captures.Clear();
            if (s_solarCapture is not null)
            {
                s_solarArchive.Add(s_solarCapture);
                s_solarCapture = null;
                s_solarStatus = "solar probe not recorded";
                s_solarCoverageStatus = "solar coverage probe not recorded";
                s_solarDepthInspectionStatus = "solar depth inspection not recorded";
            }

            s_lastCandidateReason = "no candidate evaluated";
            Console.WriteLine($"KSARS: source probe recapture requested; generation {s_captureGeneration.Generation + 1}/{ProbeCaptureGeneration.MaxGenerations} begins, {s_captureArchive.Count} body + {s_solarArchive.Count} solar frozen capture(s) archived until verified-idle teardown");
        }

        private static void TryRecordSolarSource(ObserverSnapshot snapshot, CommandBuffer commandBuffer)
        {
            if (s_solarFailed || s_solarCapture is not null)
            {
                return;
            }

            StellarBody? sun = Universe.WorldSun;
            if (sun is null || !double.IsFinite(sun.MeanRadius) || sun.MeanRadius <= 0.0)
            {
                s_solarStatus = "solar probe waiting: no live world sun";
                return;
            }

            // Rest geometry only: the media probe now transforms GetPositionEgo
            // for the sun too; solar candidates/captures run on rest vectors.
            double3 relativePosition = sun.GetPositionEcl() - snapshot.MainCamera.PositionEcl;
            if (!IsFinite(relativePosition))
            {
                s_solarStatus = $"solar probe waiting: {sun.Id} nonfinite relative position";
                return;
            }

            double enclosingRadius = 4.0 * sun.MeanRadius;
            if (!(relativePosition.Length() > enclosingRadius))
            {
                s_solarStatus = $"solar probe waiting: {sun.Id} observer inside 4R envelope";
                return;
            }

            if (!float.IsFinite(snapshot.LightingData.SunRadius) || snapshot.LightingData.SunRadius <= 0.0f
                || snapshot.LightingData.SunRadius != (float)sun.MeanRadius)
            {
                s_solarFailed = true;
                s_solarStatus = $"solar probe disabled: global SunRadius {snapshot.LightingData.SunRadius} does not match world sun {sun.Id} MeanRadius {sun.MeanRadius}; resources retained for teardown";
                Console.WriteLine($"KSARS: {s_solarStatus}");
                return;
            }

            var restPosition = new OpticalVector(relativePosition.X, relativePosition.Y, relativePosition.Z);
            if (!AtlasProjection.TryCreate(restPosition, enclosingRadius, SourceTileSize, SourceGuardPixels,
                    out AtlasProjection projection))
            {
                s_solarStatus = $"solar probe waiting: {sun.Id} projection unsupported";
                return;
            }

            if (!TryCreateSourceCamera(snapshot, projection, out UboCameraData sourceCamera, out string cameraReason))
            {
                s_solarStatus = $"solar probe waiting: {sun.Id} {cameraReason}";
                return;
            }

            float4x4 world = float4x4.CreateScale((float)enclosingRadius)
                * float4x4.CreateTranslation(new float3(
                    (float)relativePosition.X, (float)relativePosition.Y, (float)relativePosition.Z));
            if (!IsFinite(world))
            {
                s_solarStatus = $"solar probe waiting: {sun.Id} nonfinite world matrix";
                return;
            }

            var scene = new MeshRenderTechnique.Ubo
            {
                view = sourceCamera.View,
                proj = sourceCamera.Projection,
                time = sourceCamera.Time,
                exposure = sourceCamera.PbrExposure,
            };
            var instance = new InstanceData { data = float4.Zero, model = world };

            Core.Renderer? renderer = Program.GetRenderer();
            if (renderer is null)
            {
                s_solarStatus = "solar probe waiting: renderer unavailable";
                return;
            }

            ObjectCaptureContext? context = null;
            NativeSolarCaptureAdapter? adapter = null;
            try
            {
                context = new ObjectCaptureContext(renderer, SourceTileSize,
                    $"KSARS_SolarProbe_G{s_captureGeneration.Generation}",
                    sourceCamera, snapshot.LightingData, snapshot.CelestialData, snapshot.VesselData,
                    snapshot.Constants, default(DistantSphereMaterialData));
                adapter = new NativeSolarCaptureAdapter(renderer, context);
            }
            catch (Exception ex)
            {
                adapter?.Dispose();
                context?.Dispose();
                s_solarFailed = true;
                s_solarStatus = $"solar probe disabled: source resource creation failed for {sun.Id} ({ex.GetType().Name}: {ex.Message}); body probes continue";
                Console.WriteLine($"KSARS: {s_solarStatus}");
                return;
            }

            var capture = new SolarProbeCapture
            {
                Context = context,
                Adapter = adapter,
                Sun = sun,
                SunId = sun.Id,
                SunRadius = sun.MeanRadius,
                Snapshot = snapshot,
                Projection = projection,
                Recorded = false,
                TextureRegistered = false,
            };
            s_solarCapture = capture;

            try
            {
                adapter.Record(commandBuffer, context, in scene, in instance);
                ImGuiBackendVulkanImpl backend = ImGuiBackend.Vulkan;
                RenderImage colorImage = context.Target.ColorImage
                    ?? throw new InvalidOperationException("solar capture target has no color image");
                capture.Backend = backend;
                capture.Texture = backend.AddTexture(Program.LinearClampedSampler,
                    colorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
                capture.TextureRegistered = true;
                capture.Recorded = true;
            }
            catch (Exception ex)
            {
                s_solarFailed = true;
                s_solarStatus = $"solar probe disabled: record or registration failed for {sun.Id} ({ex.GetType().Name}: {ex.Message}); frozen resources retained for teardown";
                Console.WriteLine($"KSARS: {s_solarStatus}");
                if (!adapter.RecordingRecovered)
                {
                    throw;
                }

                return;
            }

            double3 origin = snapshot.PositionEcl;
            s_solarStatus = $"solar probe recorded star={capture.SunId} observer={snapshot.FollowedVehicle.Id} origin=({origin.X:F0},{origin.Y:F0},{origin.Z:F0}) nativeFrame={snapshot.CameraData.Frame} resourceFrame={snapshot.ResourceFrameIndex} mesh=4*MeanRadius procedural=2.75*R physical={sun.MeanRadius:F0}m; raw solar color/corona alpha only, no depth/opaque/calibration claim; commands only, GPU output unverified";
            Console.WriteLine($"KSARS: {s_solarStatus}");
            TryRecordSolarCoverage(capture, renderer, commandBuffer);
        }

        private static void TryRecordSolarCoverage(SolarProbeCapture capture,
            Core.Renderer renderer, CommandBuffer commandBuffer)
        {
            if (!SolarSourceGeometry.TryCreate(in capture.Projection, capture.SunRadius,
                    out SolarSourceGeometry model))
            {
                s_solarCoverageStatus = $"solar coverage probe unsupported for {capture.SunId}: modeled physical-R geometry rejected (distance {capture.Projection.Distance:F0}m radius {capture.SunRadius:F0}m); raw solar capture retained";
                Console.WriteLine($"KSARS: {s_solarCoverageStatus}");
                return;
            }

            try
            {
                capture.Coverage ??= new SolarCoverageProbe(renderer);
                capture.Coverage.Record(commandBuffer, in model);
                capture.Coverage.RegisterPreviewTexture();
                s_solarCoverageStatus = $"solar coverage probe recorded star={capture.SunId} nativeFrame={capture.Snapshot.CameraData.Frame} resourceFrame={capture.Snapshot.ResourceFrameIndex}: modeled physical R coverage/depth; nominal 2.75R/4R comparison; not native alpha/opaque/calibration acceptance; commands only, GPU output unverified";
                Console.WriteLine($"KSARS: {s_solarCoverageStatus}");
            }
            catch (Exception ex)
            {
                s_solarCoverageStatus = $"solar coverage probe record failed for {capture.SunId} ({ex.GetType().Name}: {ex.Message}); raw solar capture retained; body paths continue";
                Console.WriteLine($"KSARS: {s_solarCoverageStatus}");
                if (capture.Coverage is { RecordingRecovered: false })
                {
                    throw;
                }
            }

            if (capture.Coverage is { RecordingRecovered: true, TextureRegistered: true })
            {
                TryRecordSolarDepthInspection(capture, renderer, commandBuffer, in model);
            }
        }

        private static void TryRecordSolarDepthInspection(SolarProbeCapture capture,
            Core.Renderer renderer, CommandBuffer commandBuffer, in SolarSourceGeometry model)
        {
            try
            {
                capture.DepthInspection ??= new SolarDepthInspectionProbe(renderer,
                    capture.Coverage!.DepthImage);
                capture.DepthInspection.Record(commandBuffer, in model);
                capture.DepthInspection.RegisterPreviewTexture();
                s_solarDepthInspectionStatus = $"solar depth inspection recorded star={capture.SunId} nativeFrame={capture.Snapshot.CameraData.Frame} resourceFrame={capture.Snapshot.ResourceFrameIndex}: sampled owned D32 coverage attachment; three-pane actual/expected comparison, gain x1000 display only; commands recorded, no CPU readback; GPU depth acceptance pending";
                Console.WriteLine($"KSARS: {s_solarDepthInspectionStatus}");
            }
            catch (Exception ex)
            {
                s_solarDepthInspectionStatus = $"solar depth inspection failed for {capture.SunId} ({ex.GetType().Name}: {ex.Message}); raw/coverage captures retained; body paths continue";
                Console.WriteLine($"KSARS: {s_solarDepthInspectionStatus}");
                if (capture.DepthInspection is { RecordingRecovered: false })
                {
                    throw;
                }
            }
        }

        private static PresentationVelocity EffectiveObserver(ObserverSnapshot snapshot)
        {
            if (!s_betaOverrideEnabled)
            {
                s_overrideUsedCameraDirection = false;
                s_effectiveBeta = snapshot.Observer.Speed / PhysicsConstants.SpeedOfLight;
                return snapshot.Observer;
            }

            double3 velocity = snapshot.VelocityEcl;
            double speedSquared = velocity.X * velocity.X + velocity.Y * velocity.Y + velocity.Z * velocity.Z;
            OpticalVector direction;
            if (speedSquared > 1e-6 && snapshot.Observer.Speed > 0.0)
            {
                direction = snapshot.Observer.Direction;
                s_overrideUsedCameraDirection = false;
            }
            else
            {
                float4 forwardComponent = float4.Transform(
                    new float4(0.0f, 0.0f, -1.0f, 0.0f), snapshot.CameraData.InverseView);
                direction = new OpticalVector(forwardComponent.X, forwardComponent.Y, forwardComponent.Z);
                s_overrideUsedCameraDirection = true;
            }

            double beta = Math.Clamp(s_betaOverride, 0.0, 1.0 - 1e-15);
            if (RelativisticOptics.TryCreateVelocity(
                direction * (beta * PhysicsConstants.SpeedOfLight), out PresentationVelocity observer))
            {
                s_effectiveBeta = beta;
                return observer;
            }

            s_effectiveBeta = snapshot.Observer.Speed / PhysicsConstants.SpeedOfLight;
            return snapshot.Observer;
        }

        private static void UpdateAxisDiagnostic(ObserverSnapshot snapshot, in PresentationVelocity observer)
        {
            Vehicle? controlled = Program.ControlledVehicle;
            OpticalVector direction = observer.Direction;
            bool axisDefined = observer.Q != 1.0 && direction.LengthSquared() > 0.0;

            string angles = "effective axis undefined (observer at rest)";
            string axisUv = "n/a";
            if (axisDefined)
            {
                var dirW0 = new float4((float)direction.X, (float)direction.Y, (float)direction.Z, 0.0f);
                float4 viewDir = float4.Transform(dirW0, snapshot.CameraData.View);
                angles = ProbeAxisProjection.TryGetViewAngles(
                        new OpticalVector(viewDir.X, viewDir.Y, viewDir.Z), out double yaw, out double pitch)
                    ? $"viewYaw={yaw:F2}deg viewPitch={pitch:F2}deg"
                    : "view direction nonfinite";

                float4 clip = float4.Transform(dirW0, snapshot.CameraData.ViewProjection);
                axisUv = ProbeAxisProjection.TryGetUv(clip.X, clip.Y, clip.W, out double u, out double v)
                    ? $"axisUv=({u:F3},{v:F3})"
                    : "axisUv=behind";
            }

            string overrideSource = s_betaOverrideEnabled && s_overrideUsedCameraDirection
                ? "camera forward"
                : "velocity";

            string navball;
            if (controlled is null)
            {
                navball = "navball marker unavailable (no controlled vehicle)";
            }
            else
            {
                string frame = controlled.NavBallData.Frame.ToPlayerString();
                string parentId = controlled.Parent?.Id ?? "none";
                if (!controlled.NavBallMarkerData.VelocityValid)
                {
                    navball = $"navball marker invalid frame={frame} controlledParent={parentId}";
                }
                else if (controlled.Parent is null)
                {
                    navball = $"navball marker unavailable (controlled parent missing) frame={frame}";
                }
                else if (!axisDefined)
                {
                    navball = $"navball parent-relative marker frame={frame} controlledParent={parentId} markerVsObserver=axis-undefined";
                }
                else
                {
                    double3 markerEcl = controlled.NavBallMarkerData.VelocityDirCci
                        .Transform(controlled.Parent.GetCci2Cce());
                    navball = ProbeAxisProjection.TryAngleDegrees(direction,
                            new OpticalVector(markerEcl.X, markerEcl.Y, markerEcl.Z), out double angle)
                        ? $"navball parent-relative marker frame={frame} controlledParent={parentId} markerVsObserver={angle:F2}deg"
                        : $"navball parent-relative marker frame={frame} controlledParent={parentId} markerVsObserver=nonfinite";
                }

                if (!ReferenceEquals(controlled, snapshot.FollowedVehicle))
                {
                    navball += " (controlled != followed)";
                }
            }

            s_axisDiagnostic =
                $"axis diag | observer={snapshot.FollowedVehicle.Id} controlled={(controlled?.Id ?? "none")} " +
                $"nativeFrame={snapshot.CameraData.Frame} " +
                $"parent={(snapshot.FollowedVehicle.Parent?.Id ?? "none")} observerFrame=ECL/star-relative " +
                $"eclSpeed={snapshot.Observer.Speed:F2}m/s q={observer.Q:G6} overrideSource={overrideSource} " +
                $"observerDir=({direction.X:F4},{direction.Y:F4},{direction.Z:F4}) {angles} {axisUv} | {navball} | " +
                "note: navball/attitude may use parent-relative prograde despite star-oriented reference; axis cause unproven";
        }

        private static void TryRecordComposite(CommandBuffer commandBuffer)
        {
            if (s_captures.Count == 0 || s_compositeFailed || s_lastSnapshot is not { } snapshot)
            {
                return;
            }

            try
            {
                Core.Renderer? renderer = Program.GetRenderer();
                if (renderer is null)
                {
                    return;
                }

                if (!TryResolveMainSyncSlot(renderer, commandBuffer, out int slotIndex, out string slotReason,
                        out bool compositeInvalidated))
                {
                    s_compositeFailed = true;
                    s_compositeRetryAfterRebuild = compositeInvalidated;
                    s_compositeStatus = $"composite probe disabled: {slotReason}; no composite writes occurred; main probe unaffected";
                    Console.WriteLine($"KSARS: {s_compositeStatus}");
                    return;
                }

                int compositeHeight = snapshot.MainViewport.Width > 0
                    ? Math.Clamp((int)Math.Round(
                        AtlasCompositeProbe.CompositeWidth * (double)snapshot.MainViewport.Height
                        / snapshot.MainViewport.Width), 90, 1440)
                    : 360;
                s_composites ??= new AtlasCompositeProbe?[renderer.MaxFramesInFlight];
                AtlasCompositeProbe composite = s_composites[slotIndex] ??= new AtlasCompositeProbe(renderer, compositeHeight);
                s_previewComposite = composite;

                var sources = new List<AtlasTileSource>(s_captures.Count);
                foreach (ProbeCapture capture in s_captures)
                {
                    if (capture.Recorded
                        && capture.Context.Target.ColorImage is { } image
                        && capture.Context.Target.DepthImage is { } depthImage)
                    {
                        sources.Add(new AtlasTileSource(image, depthImage, capture.Projection, capture.BodyId));
                    }
                }

                composite.SyncBodies(sources);
                PresentationVelocity observer = EffectiveObserver(snapshot);
#if DEBUG
                UpdateAxisDiagnostic(snapshot, in observer);
#endif
                composite.Record(commandBuffer, snapshot, in observer,
                    s_compositeDebugMode, s_compositeDebugBody);
                composite.RegisterPreviewTexture();
                s_compositeStatus = s_betaOverrideEnabled
                    ? $"composite probe recorded ({composite.BoundBodyCount} tile(s); inverse aberration + D^3 scale + native source depth; owned target syncSlot={slotIndex} frozen {AtlasCompositeProbe.CompositeWidth}x{composite.CompositeHeight}; {MainViewState}; OBSERVER OVERRIDE beta={s_betaOverride:G12})"
                    : $"composite probe recorded ({composite.BoundBodyCount} tile(s); inverse aberration + D^3 scale + native source depth; owned target syncSlot={slotIndex} frozen {AtlasCompositeProbe.CompositeWidth}x{composite.CompositeHeight}; {MainViewState})";
            }
            catch (Exception ex)
            {
                if (s_composites?.Any(c => c is { RecordingRecovered: false }) == true)
                {
                    s_compositeStatus = $"composite probe record left an owned pass unbalanced ({ex.GetType().Name}: {ex.Message}); frame aborted";
                    Console.WriteLine($"KSARS: {s_compositeStatus}");
                    throw;
                }

                s_compositeFailed = true;
                s_compositeRetryAfterRebuild = false;
                s_compositeStatus = $"composite probe failed ({ex.GetType().Name}: {ex.Message}); frozen captures retained; {MainViewState}";
                Console.WriteLine($"KSARS: {s_compositeStatus}");
            }
        }

        private static Func<Core.Renderer, SyncObjects>? ResolveSyncObjectsAccessor()
        {
            PropertyInfo? property = typeof(Core.Renderer).GetProperty("SyncObjects",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property is null || !property.CanRead || property.PropertyType != typeof(SyncObjects))
            {
                return null;
            }

            ParameterExpression renderer = Expression.Parameter(typeof(Core.Renderer), "renderer");
            return Expression.Lambda<Func<Core.Renderer, SyncObjects>>(
                Expression.Property(renderer, property), renderer).Compile();
        }

        private static bool TryResolveMainSyncSlot(Core.Renderer renderer,
            CommandBuffer commandBuffer, out int slotIndex, out string reason, out bool resourcesInvalidated)
        {
            slotIndex = -1;
            reason = "validated";
            resourcesInvalidated = false;

            SyncObjects[] ring = renderer.AllSyncObjects;
            if (ring.Length != renderer.MaxFramesInFlight || ring.Length < 1)
            {
                reason = $"sync ring length {ring.Length} does not match MaxFramesInFlight {renderer.MaxFramesInFlight}";
                return false;
            }

            if (s_mainRing is not null && !ReferenceEquals(s_mainRing, ring))
            {
                reason = "sync ring replaced after preparation; owned resources bound to the previous ring, restart required";
                resourcesInvalidated = true;
                return false;
            }

            s_getSyncObjects ??= ResolveSyncObjectsAccessor();
            if (s_getSyncObjects is null)
            {
                reason = "Core.Renderer::SyncObjects slot accessor missing; cannot prove acquired slot";
                return false;
            }

            SyncObjects? acquired = s_getSyncObjects(renderer);
            if (acquired is null || acquired.CommandBuffer.VkHandle != commandBuffer.VkHandle)
            {
                reason = "acquired command buffer does not match the recording command buffer";
                return false;
            }

            Span<nint> fences = stackalloc nint[ring.Length];
            Span<nint> commands = stackalloc nint[ring.Length];
            for (int i = 0; i < ring.Length; i++)
            {
                fences[i] = ring[i]?.Fence.VkHandle ?? 0;
                commands[i] = ring[i]?.CommandBuffer.VkHandle ?? 0;
            }

            if (!MainAtlasSlotGuard.TryFindAcquiredSlot(fences, commands,
                    acquired.Fence.VkHandle, commandBuffer.VkHandle, out slotIndex, out reason))
            {
                return false;
            }

            s_mainRing ??= ring;
            return true;
        }

        private static void PrepareMainFrameSources(CommandBuffer commandBuffer)
        {
            if (!s_mainProbeEnabled || s_mainFailed)
            {
                return;
            }

            if (s_lastSnapshot is not { } snapshot)
            {
                s_mainStatus = "main probe waiting: no current snapshot";
                return;
            }

            Core.Renderer? renderer = Program.GetRenderer();
            if (renderer is null)
            {
                s_mainStatus = "main probe waiting: renderer unavailable";
                return;
            }

            int resourceIndex = snapshot.ResourceFrameIndex;
            if (!TryResolveMainSyncSlot(renderer, commandBuffer, out int slotIndex, out string ringReason,
                    out bool mainInvalidated))
            {
                s_mainFailed = true;
                s_mainRetryAfterRebuild = mainInvalidated;
                s_mainStatus = $"main probe disabled: {ringReason}; mapped sync-slot mismatch, no slot writes occurred";
                Console.WriteLine($"KSARS: {s_mainStatus}");
                return;
            }

            s_mainFrames ??= new MainAtlasFrame?[renderer.MaxFramesInFlight];
            if (slotIndex < 0 || slotIndex >= s_mainFrames.Length)
            {
                s_mainFailed = true;
                s_mainRetryAfterRebuild = false;
                s_mainStatus = $"main probe disabled: resolved sync slot {slotIndex} outside validated ring";
                Console.WriteLine($"KSARS: {s_mainStatus}");
                return;
            }

            MainAtlasFrame frame = s_mainFrames[slotIndex] ??= new MainAtlasFrame();
            frame.Reset();
            frame.SyncSlotIndex = slotIndex;
            frame.RendererEpoch = s_rendererEpoch;

            Celestial? nearby = snapshot.MainCamera.GetEffectiveNearbyCelestial();
            CelestialTextureStreamer? streamer = Program.GetPlanetRenderer()?.TextureStreamer;
            if (streamer is null)
            {
                s_mainStatus = "main probe waiting: texture streamer unavailable";
                return;
            }

            if (!TryComputeMainViewCone(snapshot, out OpticalVector mainForward, out double viewCorner,
                    out string coneReason))
            {
                s_mainStatus = $"main probe waiting: {coneReason}";
                return;
            }

            frame.Snapshot = snapshot;
            frame.Observer = EffectiveObserver(snapshot);
            if (s_captures.Count == 0)
            {
#if DEBUG
                UpdateAxisDiagnostic(snapshot, in frame.Observer);
#endif
            }
            s_mainOwnership.BeginFrame(snapshot);

            int limited = 0;
            int count = 0;
            s_mainLastRejection = "none";
            bool tinyMatch = s_tinyMainOmitted.Count > 0
                && TinyClassificationsMatch(snapshot, in frame.Observer);
            foreach (StaticCelestial body in Universe.CurrentSystem!.All.OfType<StaticCelestial>())
            {
                if (ReferenceEquals(body, nearby))
                {
                    continue;
                }

                if (tinyMatch && s_tinyMainOmitted.ContainsKey(body))
                {
                    frame.OmittedBodies.Add(body);
                    continue;
                }

                if (!TryBuildSourceCandidate(snapshot, frame.Observer, streamer, mainForward, viewCorner,
                        body, requireNativeSpherePixels: true,
                        out SourceCandidate candidate, out string rejectReason, out bool limitedByPixels))
                {
                    s_mainLastRejection = rejectReason;
                    if (limitedByPixels)
                    {
                        limited++;
                    }
                    continue;
                }

                if (!candidate.IsObservedVisible)
                {
                    frame.CulledBodies.Add(candidate.Body);
                    continue;
                }

                if (count >= AtlasMainPass.MaxBodies)
                {
                    continue;
                }

                MainAtlasFrame.Slot slot = frame.Slots[count];
                if (slot.Context is null)
                {
                    slot.Context = new ObjectCaptureContext(renderer, SourceTileSize,
                        $"KSARS_MainProbe_F{slotIndex}S{count}",
                        candidate.Camera, snapshot.LightingData, snapshot.CelestialData, snapshot.VesselData,
                        snapshot.Constants, candidate.Material);
                }
                else
                {
                    slot.Context.WriteSnapshot(candidate.Camera, snapshot.LightingData, snapshot.CelestialData,
                        snapshot.VesselData, snapshot.Constants, candidate.Material);
                }

                slot.Adapter ??= new NativeCelestialCaptureAdapter(renderer, slot.Context);
                slot.Adapter.Record(commandBuffer, slot.Context, candidate.DrawData);
                slot.Body = candidate.Body;
                slot.BodyId = candidate.BodyId;
                slot.Projection = candidate.Projection;
                slot.HeightFallback = candidate.HeightFallback;
                frame.Bodies.Add(candidate.Body);
                count++;
            }

            TryPrepareMainSolarSource(renderer, snapshot, frame, commandBuffer,
                mainForward, viewCorner);
            frame.BodyCount = count;
            frame.SourceCount = count + (frame.Solar.Prepared ? 1 : 0);
            // A culled solar frame owns native Sun+Sunbloom suppression even with zero
            // sources to composite, so it must still reach frame.Prepared below.
            if (frame.SourceCount == 0 && !frame.Solar.Culled)
            {
                s_mainOwnership.MarkCulled(snapshot, frame.CulledBodies.Cast<object>());
                s_mainOwnership.MarkOmitted(snapshot, frame.OmittedBodies.Cast<object>());
                s_mainStatus = frame.CulledBodies.Count > 0 || frame.OmittedBodies.Count > 0
                    ? $"main probe waiting: no resolved source in the observed view; {frame.CulledBodies.Count} supported source(s) outside observed view, {frame.OmittedBodies.Count} tiny source(s) omitted (observed <=6px; nonphysical visibility simplification), native sphere suppressed (last: {s_mainLastRejection})"
                    : limited > 0
                        ? $"main probe waiting: no candidate met the native sphere threshold ({limited} below native rest 6px; rest<=6px observed>6px remains unsupported stock LOD, last: {s_mainLastRejection}); stock sprites unchanged"
                        : $"main probe waiting: no eligible candidate (last: {s_mainLastRejection})";
                return;
            }

            if (frame.SourceCount > 0)
            {
                frame.Pass ??= new AtlasMainPass(renderer, Program.OffscreenTarget);
                var sources = new List<MainTileSource>(frame.SourceCount);
                for (int i = 0; i < count; i++)
                {
                    MainAtlasFrame.Slot slot = frame.Slots[i];
                    if (slot.Context!.Target.ColorImage is not { } color
                        || slot.Context.Target.DepthImage is not { } depth)
                    {
                        throw new InvalidOperationException("main probe source target missing color or depth image");
                    }

                    sources.Add(new MainTileSource { ColorImage = color, DepthImage = depth, Projection = slot.Projection });
                }

                if (frame.Solar.Prepared)
                {
                    if (frame.Solar.Context?.Target.ColorImage is not { } solarColor
                        || frame.Solar.Coverage is not { } solarCoverage)
                    {
                        throw new InvalidOperationException("main solar source target missing color or coverage depth image");
                    }

                    sources.Add(new MainTileSource
                    {
                        ColorImage = solarColor,
                        DepthImage = solarCoverage.DepthImage,
                        Projection = frame.Solar.Projection,
                        IsSolar = true,
                        Distance = frame.Solar.Model.Distance,
                        // Native SunRenderer::Draw only draws the mesh (and its corona) when
                        // dist < StartShowingDistOf = 16 * MeshRadiusOf = 16 * 5.5 * MeanRadius
                        // = 88R; beyond that the distant-sphere path draws the sun instead.
                        CoronaEnabled = 88.0 * frame.Solar.Model.PhysicalRadiusRatio > 1.0,
                    });
                }

                frame.Pass.SyncSources(sources);
            }
            s_mainOwnership.MarkCulled(snapshot, frame.CulledBodies.Cast<object>());
            s_mainOwnership.MarkOmitted(snapshot, frame.OmittedBodies.Cast<object>());
            frame.Prepared = true;
            s_preparedMainFrame = frame;
            string culledNote = frame.CulledBodies.Count > 0
                ? $"; {frame.CulledBodies.Count} supported source(s) outside observed view, native sphere suppressed"
                : "";
            string omittedNote = frame.OmittedBodies.Count > 0
                ? $"; {frame.OmittedBodies.Count} tiny source(s) omitted (observed <=6px; nonphysical visibility simplification), native sphere suppressed"
                : "";
            s_mainStatus = $"main probe prepared {count}/{AtlasMainPass.MaxBodies} resolved source(s){(frame.Solar.Prepared ? "+1 solar" : "")} on syncSlot={slotIndex} resourceFrame={resourceIndex} ({limited} below native rest >6px threshold unsupported; rest<=6px observed>6px stays stock{culledNote}{omittedNote}); diagnostic composite pending, finite HDR clamp 65504";
        }

        private static void RunNativeDistanceAndComposite(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
        {
            StaticCelestialDistanceRendering.Run(commandBuffer, viewport, frameIndex);
            TryRecordMainComposite(commandBuffer, viewport, frameIndex);
            FlushDeferredNativeSun();
        }

        private static void TryRecordMainComposite(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
        {
            if (!s_mainProbeEnabled || s_mainFailed
                || !ReferenceEquals(viewport, Program.MainViewport)
                || s_preparedMainFrame is not { } frame)
            {
                return;
            }

            if (!frame.Prepared || frame.Snapshot is not { } snapshot
                || frame.Pass is null || frame.SourceCount == 0
                || !ReferenceEquals(snapshot, s_lastSnapshot)
                || snapshot.ResourceFrameIndex != frameIndex)
            {
                return;
            }

            try
            {
                frame.Pass.ValidateState();
            }
            catch (Exception ex)
            {
                s_mainFailed = true;
                s_mainRetryAfterRebuild = ex is AtlasMainPass.RendererTargetChangedException;
                s_mainStatus = $"main probe disabled: {ex.Message}; stock rendering continues";
                Console.WriteLine($"KSARS: {s_mainStatus}");
                return;
            }

            bool solarGate = false;
            if (frame.Solar.Prepared)
            {
                SolarFrameToken current = CurrentSolarFrameToken();
                solarGate = frameIndex == current.ResourceFrame
                    && frame.Solar.Token.CanPresent(in current, prepared: true,
                        hasPending: frame.PendingSunRenderer is not null);
                if (!solarGate)
                {
                    s_mainStatus = $"main probe composite skipped: solar ownership seam unsatisfied this frame (tokenMatch={frame.Solar.Token.Matches(in current)} pendingSun={frame.PendingSunRenderer is not null}); no composite recorded, body sources unclaimed, native fallback retained";
                    s_mainSolarStatus = $"main solar skipped this frame: deferred native Sun seam or current ownership token did not match; native Sun path retained, no composite recorded";
                    return;
                }
            }

            PresentationVelocity observer = frame.Observer;
            try
            {
                frame.Pass.Record(commandBuffer, snapshot, in observer);
            }
            catch (Exception ex)
            {
                s_mainFailed = true;
                s_mainRetryAfterRebuild = false;
                s_mainStatus = $"main probe composite failed after record began ({ex.GetType().Name}: {ex.Message}); frame aborted to avoid double draw";
                Console.WriteLine($"KSARS: {s_mainStatus}");
                throw;
            }

            s_mainOwnership.MarkPresented(snapshot, frame.Bodies.Cast<object>());
            if (solarGate)
            {
                frame.Solar.Presented = true;
                s_mainSolarPresented = true;
                bool solarCorona = 88.0 * frame.Solar.Model.PhysicalRadiusRatio > 1.0;
                string coronaNote = solarCorona
                    ? "raw premultiplied capture"
                    : "omitted (beyond native 88R mesh-draw distance; distant-sphere parity)";
                s_mainSolarStatus = $"main solar presented star={frame.Solar.SunId} nativeFrame={snapshot.CameraData.Frame} resourceFrame={frameIndex} syncSlot={frame.SyncSlotIndex} epoch={s_rendererEpoch}: deferred native Sun mesh queue dropped and Sunbloom flare bypassed this frame only; native 4R mesh / 2.75R angular scale preserved; opaque coverage models physical R only; corona = {coronaNote} at simplified center-plane radial={frame.Solar.Model.Distance:F0}m (not measured solar depth, native alpha clamped 0..1); no GPU opacity/calibration claim";
            }

            if (!s_firstMainRecordLogged)
            {
                s_firstMainRecordLogged = true;
                Console.WriteLine($"KSARS: main probe first composite submission bodies=[{string.Join(",", frame.Bodies.Select(b => b.Id))}] nativeFrame={snapshot.CameraData.Frame} syncSlot={frame.SyncSlotIndex} resourceFrame={frameIndex} rendererEpoch={s_rendererEpoch} count={frame.BodyCount} viewportSlots={ViewportRegistry.Views.Length} commands-recorded; GPU correctness unverified");
            }

            s_mainStatus = s_betaOverrideEnabled
                ? $"main view override active: composite recorded ({frame.BodyCount}/{AtlasMainPass.MaxBodies} resolved bodies on syncSlot={frame.SyncSlotIndex} resourceFrame={frameIndex}; depth-aware radial composite; D^3 + 5800K chroma shift + finite HDR clamp 65504; ordering + VVL verified; no radiance calibration claim; OBSERVER OVERRIDE beta={s_betaOverride:G12})"
                : $"main view override active: composite recorded ({frame.BodyCount}/{AtlasMainPass.MaxBodies} resolved bodies on syncSlot={frame.SyncSlotIndex} resourceFrame={frameIndex}; depth-aware radial composite; D^3 + 5800K chroma shift + finite HDR clamp 65504; ordering + VVL verified; no radiance calibration claim)";
        }

        private static void RenderNativeSphereOrAtlas(StaticCelestial body, IViewport viewport,
            CommandBuffer commandBuffer, int frameIndex)
        {
            if (s_mainProbeEnabled && !s_mainFailed
                && s_lastSnapshot is { } snapshot
                && snapshot.ResourceFrameIndex == frameIndex
                && ReferenceEquals(viewport, Program.MainViewport)
                && ReferenceEquals(viewport, Program.RenderedViewport)
                && s_mainOwnership.Owns(snapshot, body))
            {
                int slot = viewport.ShaderSlot;
                if (slot >= 0 && slot < body.RenderSphereThisFrame.Length)
                {
                    body.RenderSphereThisFrame[slot] = false;
                }

                return;
            }

            body.RenderSphere(viewport, commandBuffer, frameIndex);
        }

        private static SolarFrameToken CurrentSolarFrameToken() =>
            new(s_lastSnapshot, Program.MainViewport, Program.MainViewport?.GetCamera(),
                Universe.WorldSun, Program.Instance?.ResourceFrameIndex ?? -1, s_rendererEpoch);

        private static void QueueNativeSunOrAtlas(KSA.Rendering.Sun.SunRenderer sunRenderer,
            RenderCore.Systems.ViewHandle view)
        {
            MainAtlasFrame? frame = s_preparedMainFrame;
            if (s_mainProbeEnabled && !s_mainFailed && !s_recordingDisabled
                && frame is { Prepared: true }
                && frame.Solar is { Presented: false }
                && (frame.Solar.Prepared || frame.Solar.Culled)
                && frame.PendingSunRenderer is null
                && ReferenceEquals(Program.RenderedViewport, Program.MainViewport))
            {
                SolarFrameToken current = CurrentSolarFrameToken();
                if (frame.Solar.Token.Matches(in current))
                {
                    frame.PendingSunRenderer = sunRenderer;
                    frame.PendingSunView = view;
                    return;
                }
            }

            sunRenderer.Draw(view);
        }

        private static void FlushDeferredNativeSun()
        {
            MainAtlasFrame? frame = s_preparedMainFrame;
            if (frame?.PendingSunRenderer is not { } pending)
            {
                return;
            }

            RenderCore.Systems.ViewHandle view = frame.PendingSunView;
            frame.PendingSunRenderer = null;
            SolarFrameToken current = CurrentSolarFrameToken();
            if (!(s_mainProbeEnabled && !s_mainFailed && !s_recordingDisabled
                    && ReferenceEquals(Program.RenderedViewport, Program.MainViewport)
                    && (frame.Solar.Token.Owns(in current, frame.Solar.Prepared, frame.Solar.Presented)
                        || (frame.Solar.Culled && frame.Solar.Token.Matches(in current)))))
            {
                pending.Draw(view);
            }
        }

        private static void RenderSunbloomOrAtlas(SunbloomRenderer sunbloom, CommandBuffer commandBuffer,
            IViewport viewport, int frameIndex)
        {
            if ((SolarReplacementPresented(viewport, frameIndex)
                    || SolarCulledOwned(viewport, frameIndex))
                && s_preparedMainFrame is { Prepared: true } frame)
            {
                TryRecordSolarGlow(sunbloom, commandBuffer, viewport, frameIndex, frame);
                return;
            }

            s_solarGlareStatus = "solar glare native this frame: no current presented or culled solar ownership; stock Sunbloom active";
            sunbloom.Render(commandBuffer, viewport, frameIndex);
        }

        // Culled ownership: the solar slot is valid and token-matched this frame but the
        // transformed sun fell outside the observed view, so the composite never recorded.
        // The native rest-position Sun and Sunbloom would still draw at the wrong place, so
        // they stay suppressed; the owned glare pass records only when the transformed
        // anchor is usable.
        private static bool SolarCulledOwned(IViewport viewport, int frameIndex)
        {
            if (!s_mainProbeEnabled || s_mainFailed || s_recordingDisabled
                || !ReferenceEquals(viewport, Program.MainViewport)
                || !ReferenceEquals(viewport, Program.RenderedViewport)
                || s_preparedMainFrame is not { Prepared: true } frame
                || !frame.Solar.Culled)
            {
                return false;
            }

            SolarFrameToken current = CurrentSolarFrameToken();
            return frameIndex == current.ResourceFrame
                && frame.Solar.Token.Matches(in current);
        }

        private static bool SolarReplacementPresented(IViewport viewport, int frameIndex)
        {
            if (!s_mainProbeEnabled || s_mainFailed || s_recordingDisabled
                || !ReferenceEquals(viewport, Program.MainViewport)
                || !ReferenceEquals(viewport, Program.RenderedViewport)
                || s_preparedMainFrame is not { Prepared: true } frame)
            {
                return false;
            }

            SolarFrameToken current = CurrentSolarFrameToken();
            return frameIndex == current.ResourceFrame
                && frame.Solar.Token.Owns(in current, frame.Solar.Prepared, frame.Solar.Presented);
        }

        private static SolarGlowShaderSources? SolarGlareSources()
        {
            if (s_solarGlareCtorFailed)
            {
                return null;
            }

            if (s_solarGlareSources is not null)
            {
                return s_solarGlareSources;
            }

            try
            {
                s_solarGlareSources = SolarGlowShaderSources.LoadInstalled();
                return s_solarGlareSources;
            }
            catch (Exception ex)
            {
                s_solarGlareCtorFailed = true;
                s_solarGlareStatus = $"solar glare disabled: installed shader adaptation unsupported ({ex.GetType().Name}: {ex.Message}); stock flare withheld only while solar replacement is presented";
                Console.WriteLine($"KSARS: {s_solarGlareStatus}");
                return null;
            }
        }

        private static void ComputeSolarGlareAnchor(ObserverSnapshot snapshot, in PresentationVelocity observer,
            StellarBody sun, in AtlasProjection projection, in SolarSourceGeometry model,
            MainAtlasFrame.SolarSlot solar)
        {
            solar.GlareProjection = default;
            solar.GlareAnchored = false;
            solar.GlareAnchorUv = default;
            solar.GlareAnchorDepth = 0.0f;

            float4 forwardComponent = float4.Transform(new float4(0.0f, 0.0f, -1.0f, 0.0f), snapshot.CameraData.InverseView);
            float4 rightComponent = float4.Transform(new float4(1.0f, 0.0f, 0.0f, 0.0f), snapshot.CameraData.InverseView);
            float4 upComponent = float4.Transform(new float4(0.0f, 1.0f, 0.0f, 0.0f), snapshot.CameraData.InverseView);
            if (!RelativisticOptics.TryNormalize(
                    new OpticalVector(forwardComponent.X, forwardComponent.Y, forwardComponent.Z), out OpticalVector forward)
                || !RelativisticOptics.TryNormalize(
                    new OpticalVector(rightComponent.X, rightComponent.Y, rightComponent.Z), out OpticalVector right)
                || !RelativisticOptics.TryNormalize(
                    new OpticalVector(upComponent.X, upComponent.Y, upComponent.Z), out OpticalVector up))
            {
                return;
            }

            float m22 = MathF.Abs(snapshot.CameraData.Projection.M22);
            if (!float.IsFinite(m22) || !(m22 > 0.0f))
            {
                return;
            }

            int width = snapshot.MainViewport.Width;
            int height = snapshot.MainViewport.Height;
            double verticalFov = 2.0 * Math.Atan(1.0 / m22);
            if (!SolarGlareProjection.TryCreate(in projection, sun.MeanRadius, in observer,
                    forward, right, up, verticalFov, width, height, out SolarGlareProjection glare)
                || !glare.AnchorSupported)
            {
                solar.GlareProjection = glare;
                return;
            }

            // Native anchor math (Program::UpdateShaderData): clip = Transform(world, ViewProjection);
            // divide by W; screen uv = xy*0.5+0.5; depth = clamp(z,0,1). Applied to the transformed
            // solar center at rest distance so the clip path stays bitwise-native.
            var observedWorld = new float3(
                (float)(glare.ObservedCenter.X * model.Distance),
                (float)(glare.ObservedCenter.Y * model.Distance),
                (float)(glare.ObservedCenter.Z * model.Distance));
            float4 clip = float4.Transform(new float4(observedWorld, 1.0f), snapshot.CameraData.ViewProjection);
            if (!float.IsFinite(clip.X) || !float.IsFinite(clip.Y)
                || !float.IsFinite(clip.Z) || !float.IsFinite(clip.W) || clip.W <= 0.0f)
            {
                solar.GlareProjection = glare;
                return;
            }

            float4 ndc = clip / clip.W;
            if (!float.IsFinite(ndc.X) || !float.IsFinite(ndc.Y) || !float.IsFinite(ndc.Z))
            {
                solar.GlareProjection = glare;
                return;
            }

            solar.GlareProjection = glare;
            solar.GlareAnchorUv = new float2(ndc.X, ndc.Y) * 0.5f + new float2(0.5f, 0.5f);
            solar.GlareAnchorDepth = Math.Clamp(ndc.Z, 0.0f, 1.0f);
            solar.GlareAnchored = true;
        }

        private static bool TryReadNativeFlare(SunbloomRenderer sunbloom, int shaderSlot, int frameIndex,
            out SunFlareData flare, out string reason)
        {
            flare = default;
            if (s_sunflareMemoryField is null || s_sunflareBuffersField is null
                || s_sunflareFrameCountField is null)
            {
                reason = "native flare UBO fields unresolved";
                return false;
            }

            object? memoryObj = s_sunflareMemoryField.GetValue(sunbloom);
            object? buffersObj = s_sunflareBuffersField.GetValue(sunbloom);
            object? countObj = s_sunflareFrameCountField.GetValue(sunbloom);
            if (memoryObj is not Brutal.VulkanApi.Abstractions.MappedMemory memory
                || buffersObj is not Brutal.VulkanApi.Abstractions.BufferEx buffers
                || countObj is not int frameCount || frameCount <= 0)
            {
                reason = "native flare UBO mapping unavailable";
                return false;
            }

            long stride = (long)ByteSize.Of<SunFlareData>();
            long offset = stride * ((long)shaderSlot * frameCount + frameIndex);
            if (shaderSlot < 0 || frameIndex < 0 || offset < 0
                || offset + stride > (long)buffers.BufferSize)
            {
                reason = $"native flare UBO slot offset out of range (offset={offset} size={buffers.BufferSize})";
                return false;
            }

            flare = memory.Offset((ByteSize)offset).As<SunFlareData>();
            reason = "read";
            return true;
        }

        private static void TryRecordSolarGlow(SunbloomRenderer sunbloom, CommandBuffer commandBuffer,
            IViewport viewport, int frameIndex, MainAtlasFrame frame)
        {
            MainAtlasFrame.SolarSlot solar = frame.Solar;
            if (s_solarGlareCtorFailed)
            {
                s_solarGlareStatus = "solar glare disabled: pass construction or shader adaptation failed earlier; no glow this frame (native stock-position flare withheld; atlas disc retained)";
                return;
            }

            if (frame.Pass is null || frame.Snapshot is not { } snapshot || solar.Sun is not { } sun)
            {
                s_solarGlareStatus = "solar glare skipped: solar frame state incomplete; no glow this frame";
                return;
            }

            if (!solar.GlareAnchored)
            {
                s_solarGlareStatus = "solar glare skipped: transformed anchor unsupported this frame (sun behind camera or cap >=90deg); no glow";
                return;
            }

            try
            {
                if (solar.GlarePass is { } existing && !ReferenceEquals(existing.Atlas, frame.Pass))
                {
                    existing.Dispose();
                    solar.GlarePass = null;
                }

                if (solar.GlarePass is null)
                {
                    if (SolarGlareSources() is not { } sources)
                    {
                        return;
                    }

                    if (Program.GetRenderer() is not { } coreRenderer)
                    {
                        s_solarGlareStatus = "solar glare skipped: renderer unavailable; no glow this frame";
                        return;
                    }

                    try
                    {
                        solar.GlarePass = new SolarGlowFlarePass(coreRenderer, Program.OffscreenTarget,
                            frame.Pass, sources);
                    }
                    catch (Exception ex)
                    {
                        s_solarGlareCtorFailed = true;
                        s_solarGlareStatus = $"solar glare disabled: pass construction failed ({ex.GetType().Name}: {ex.Message}); no glow for remaining frames this session";
                        Console.WriteLine($"KSARS: {s_solarGlareStatus}");
                        return;
                    }
                }

                if (!TryReadNativeFlare(sunbloom, viewport.ShaderSlot, frameIndex,
                        out SunFlareData flare, out string readReason))
                {
                    s_solarGlareStatus = $"solar glare skipped: {readReason}; no glow this frame";
                    return;
                }

                // Culled solar frames carry no atlas solar entry; info.x=0 also disables the
                // body-occlusion loop (irrelevant for an off-view sun) and info.y stays in
                // range without indexing a solar slot that was never written.
                // Lens bloom integrates the rendered buffer: energy ~ clamped disc
                // radiance * observed solid angle = min(D^3, 65504) * D^-2. This
                // equals D while the disc peak stays under the 65504 clamp (matching
                // the verified midrange look), peaks at ~40 near D=40, then decays as
                // aberration collapses the disc to a subpixel point — otherwise raw
                // flux (D) whites out the whole screen at extreme beta.
                double glareDoppler = solar.GlareProjection.SourceDoppler;
                float glareFluxScale = (float)(Math.Min(solar.GlareProjection.BrightnessScale, 65504.0)
                    / (glareDoppler * glareDoppler));
                var push = new SolarGlowFlarePass.GlarePushGpu
                {
                    ObserverUq = new float4((float)frame.Observer.Direction.X,
                        (float)frame.Observer.Direction.Y, (float)frame.Observer.Direction.Z,
                        (float)frame.Observer.Q),
                    SolarRest = new float4((float)solar.Projection.Forward.X,
                        (float)solar.Projection.Forward.Y, (float)solar.Projection.Forward.Z,
                        (float)(solar.SunRadius / solar.Model.Distance)),
                    Info = solar.Culled
                        ? new int4(0, 0, solar.GlareAnchored ? 1 : 0, 0)
                        : new int4(frame.SourceCount, frame.SourceCount - 1,
                            solar.GlareAnchored ? 1 : 0, 0),
                    GlareScale = new float4(0.0f, glareFluxScale, 0.0f, 0.0f),
                };
                flare.ScreenspaceSunPosition = solar.GlareAnchorUv;
                flare.ScreenspaceSunRadius = (float)solar.GlareProjection.SunRadiusPixels;
                flare.ScreenspaceGlowRadius = (float)solar.GlareProjection.GlowRadiusPixels;
                flare.SunDot = (float)solar.GlareProjection.SunDot;
                flare.SunDepth = solar.GlareAnchorDepth;
                solar.GlarePass.Prepare(viewport, Program.OffscreenTarget, in flare, in push);
                solar.GlarePass.Record(commandBuffer);
                s_solarGlareStatus = $"solar glare recorded: owned glow+blur+merge star={solar.SunId} anchor=({solar.GlareAnchorUv.X:F3},{solar.GlareAnchorUv.Y:F3}) sunRadius={solar.GlareProjection.SunRadiusPixels:F1}px glowRadius={solar.GlareProjection.GlowRadiusPixels:F1}px sunDot={solar.GlareProjection.SunDot:F3} doppler={solar.GlareProjection.SourceDoppler:G4} doppler3={solar.GlareProjection.BrightnessScale:G4} glareScale={glareFluxScale:G4} (glare=min(D^3,65504)*D^-2 clamped-radiance*solid-angle, disc radiance D^3){(solar.Culled ? "; culled off-view sun: flare-only, no disc composite" : "")}; live native flare config preserved via mapped read | lensflare ubo: showFlare={flare.ShowFlare} intensity={flare.FlareIntensity:G4} lensOrbFlags={flare.lensOrbFlags} ghost={flare.GhostOrbMultiplier:G4} bent={flare.BentOrbMultiplier:G4} allFlare={flare.AllFlareColorScalar:G4} orbSize={flare.OrbSizeModifier:G4} sunColor=({flare.SunColor.X:G3},{flare.SunColor.Y:G3},{flare.SunColor.Z:G3}) aspect={flare.AspectRatio:G3} screen={flare.ScreenWidth}x{flare.ScreenHeight}; commands only, GPU output unverified";
            }
            catch (SolarGlowFlarePass.RendererTargetChangedException ex)
            {
                try
                {
                    solar.GlarePass?.Dispose();
                }
                catch
                {
                }

                solar.GlarePass = null;
                s_solarGlareStatus = $"solar glare pass retired after target change ({ex.Message}); no glow this frame; retry next frame";
                Console.WriteLine($"KSARS: {s_solarGlareStatus}");
            }
            catch (Exception ex)
            {
                s_solarGlareStatus = $"solar glare record failed this frame ({ex.GetType().Name}: {ex.Message}); no glow this frame; native stock-position flare withheld";
                Console.WriteLine($"KSARS: {s_solarGlareStatus}");
            }
        }

        private static void TryPrepareMainSolarSource(Core.Renderer renderer, ObserverSnapshot snapshot,
            MainAtlasFrame frame, CommandBuffer commandBuffer,
            OpticalVector mainForward, double viewCorner)
        {
            MainAtlasFrame.SolarSlot solar = frame.Solar;
            StellarBody? sun = Universe.WorldSun;
            if (sun is null || !double.IsFinite(sun.MeanRadius) || sun.MeanRadius <= 0.0)
            {
                s_mainSolarStatus = "main solar waiting: no live world sun; native Sun path retained";
                return;
            }

            // Rest geometry only: the media probe now transforms GetPositionEgo
            // for the sun too; solar candidates/captures run on rest vectors.
            double3 relativePosition = sun.GetPositionEcl() - snapshot.MainCamera.PositionEcl;
            if (!IsFinite(relativePosition))
            {
                s_mainSolarStatus = $"main solar waiting: {sun.Id} nonfinite relative position; native Sun path retained";
                return;
            }

            double enclosingRadius = 4.0 * sun.MeanRadius;
            if (!(relativePosition.Length() > enclosingRadius))
            {
                s_mainSolarStatus = $"main solar waiting: {sun.Id} observer inside 4R envelope; native Sun path retained";
                return;
            }

            if (!float.IsFinite(snapshot.LightingData.SunRadius) || snapshot.LightingData.SunRadius <= 0.0f
                || snapshot.LightingData.SunRadius != (float)sun.MeanRadius)
            {
                s_mainSolarStatus = $"main solar unsupported this frame: global SunRadius {snapshot.LightingData.SunRadius} does not match world sun {sun.Id} MeanRadius {sun.MeanRadius}; native Sun path retained";
                return;
            }

            var restPosition = new OpticalVector(relativePosition.X, relativePosition.Y, relativePosition.Z);
            if (!AtlasProjection.TryCreate(restPosition, enclosingRadius, SourceTileSize, SourceGuardPixels,
                    out AtlasProjection projection))
            {
                s_mainSolarStatus = $"main solar waiting: {sun.Id} projection unsupported; native Sun path retained";
                return;
            }

            if (!SolarSourceGeometry.TryCreate(in projection, sun.MeanRadius, out SolarSourceGeometry model)
                || !SolarCoverageProbe.GeometryUploadSupported((float)model.TanHalfFov,
                    (float)model.PhysicalRadiusRatio, (float)model.NearRatio, (float)model.FarRatio))
            {
                s_mainSolarStatus = $"main solar waiting: {sun.Id} modeled physical-R geometry unsupported for float upload; native Sun path retained";
                return;
            }

            if (!RelativisticOptics.TryTransformSphericalCap(restPosition, projection.RestAngularRadius,
                    frame.Observer, out ObservedSphericalCap cap)
                || !cap.IntersectsViewCone(mainForward, viewCorner))
            {
                // The transformed sun is not inside the observed view (or the cap transform
                // degenerated), but the native rest-position Sun would still draw where the
                // unaberrated direction lands. Own the frame anyway so the native mesh and
                // Sunbloom are suppressed; the owned glare pass still records when the
                // transformed anchor is usable.
                solar.Sun = sun;
                solar.SunId = sun.Id;
                solar.SunRadius = sun.MeanRadius;
                solar.Projection = projection;
                solar.Model = model;
                solar.Token = new SolarFrameToken(snapshot, snapshot.MainViewport, snapshot.MainCamera,
                    sun, snapshot.ResourceFrameIndex, frame.RendererEpoch);
                solar.Culled = true;
                ComputeSolarGlareAnchor(snapshot, in frame.Observer, sun, in projection, in model, solar);
                s_mainSolarStatus = $"main solar culled star={sun.Id} nativeFrame={snapshot.CameraData.Frame} resourceFrame={snapshot.ResourceFrameIndex} syncSlot={frame.SyncSlotIndex}: transformed sun outside observed view; native Sun mesh + Sunbloom suppressed this frame; owned glare {(solar.GlareAnchored ? $"anchored uv=({solar.GlareAnchorUv.X:F3},{solar.GlareAnchorUv.Y:F3})" : "unanchored (behind camera or unsupported)")}";
                return;
            }

            if (!TryCreateSourceCamera(snapshot, projection, out UboCameraData sourceCamera, out string cameraReason))
            {
                s_mainSolarStatus = $"main solar waiting: {sun.Id} {cameraReason}; native Sun path retained";
                return;
            }

            float4x4 world = float4x4.CreateScale((float)enclosingRadius)
                * float4x4.CreateTranslation(new float3(
                    (float)relativePosition.X, (float)relativePosition.Y, (float)relativePosition.Z));
            if (!IsFinite(world))
            {
                s_mainSolarStatus = $"main solar waiting: {sun.Id} nonfinite world matrix; native Sun path retained";
                return;
            }

            var scene = new MeshRenderTechnique.Ubo
            {
                view = sourceCamera.View,
                proj = sourceCamera.Projection,
                time = sourceCamera.Time,
                exposure = sourceCamera.PbrExposure,
            };
            var instance = new InstanceData { data = float4.Zero, model = world };
            DistantSphereMaterialData material = default;

            try
            {
                if (solar.Context is null)
                {
                    solar.Context = new ObjectCaptureContext(renderer, SourceTileSize,
                        $"KSARS_MainSolar_F{frame.SyncSlotIndex}",
                        sourceCamera, snapshot.LightingData, snapshot.CelestialData, snapshot.VesselData,
                        snapshot.Constants, material);
                }
                else
                {
                    solar.Context.WriteSnapshot(sourceCamera, snapshot.LightingData, snapshot.CelestialData,
                        snapshot.VesselData, snapshot.Constants, material);
                }

                solar.Adapter ??= new NativeSolarCaptureAdapter(renderer, solar.Context);
                solar.Coverage ??= new SolarCoverageProbe(renderer);
                solar.Adapter.Record(commandBuffer, solar.Context, in scene, in instance);
                solar.Coverage.Record(commandBuffer, in model);
                CommandBufferEx.PipelineBarrier2(commandBuffer, solar.Coverage.DepthImage,
                    ImageBarrierInfo.Presets.DepthSampledReadVfc, 0, false, true);
            }
            catch (Exception ex)
            {
                s_mainSolarStatus = $"main solar record failed for {sun.Id} ({ex.GetType().Name}: {ex.Message}); native Sun path retained; body composite unaffected";
                Console.WriteLine($"KSARS: {s_mainSolarStatus}");
                if (solar.Adapter is { RecordingRecovered: false }
                    || solar.Coverage is { RecordingRecovered: false })
                {
                    throw;
                }

                return;
            }

            solar.Sun = sun;
            solar.SunId = sun.Id;
            solar.SunRadius = sun.MeanRadius;
            solar.Projection = projection;
            solar.Model = model;
            solar.Token = new SolarFrameToken(snapshot, snapshot.MainViewport, snapshot.MainCamera,
                sun, snapshot.ResourceFrameIndex, frame.RendererEpoch);
            solar.Prepared = true;
            ComputeSolarGlareAnchor(snapshot, in frame.Observer, sun, in projection, in model, solar);
            s_mainSolarPrepared = true;
            s_mainSolarStatus = $"main solar prepared star={sun.Id} nativeFrame={snapshot.CameraData.Frame} resourceFrame={snapshot.ResourceFrameIndex} syncSlot={frame.SyncSlotIndex}: native 4R mesh / procedural 2.75R scale preserved; opaque coverage models physical R only; corona = raw premultiplied capture at simplified center-plane radial={model.Distance:F0}m (not measured solar depth, alpha clamped 0..1); no GPU opacity/calibration claim; pending native Sun defer";
        }

        private static bool MainRecordingUnbalanced()
        {
            if (s_mainFrames is null)
            {
                return false;
            }

            foreach (MainAtlasFrame? frame in s_mainFrames)
            {
                if (frame is null)
                {
                    continue;
                }

                if (frame.Solar.Adapter is { RecordingRecovered: false }
                    || frame.Solar.Coverage is { RecordingRecovered: false }
                    || frame.Solar.GlarePass is { RecordingRecovered: false })
                {
                    return true;
                }

                foreach (MainAtlasFrame.Slot slot in frame.Slots)
                {
                    if (slot.Adapter is { RecordingRecovered: false })
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void VerifyCoreRendererIdentity()
        {
            string location = typeof(Core.Renderer).Assembly.Location;
            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                throw new InvalidOperationException("source probe unsupported: Core.Renderer assembly location unreadable");
            }

            string sha256;
            using (FileStream stream = File.OpenRead(location))
            {
                sha256 = Convert.ToHexString(SHA256.HashData(stream));
            }

            if (!string.Equals(sha256, RenderCompatibility.SupportedCoreRendererSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"source probe unsupported: Core.Renderer assembly hash mismatch ({sha256})");
            }
        }

        private static bool ParametersAre(MethodInfo method, params Type[] expected)
        {
            ParameterInfo[] actual = method.GetParameters();
            if (actual.Length != expected.Length)
            {
                return false;
            }

            for (int i = 0; i < expected.Length; i++)
            {
                if (actual[i].ParameterType != expected[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static List<(string Type, string Name)> CallNamesOf(IEnumerable<CodeInstruction> instructions) =>
            instructions
                .Where(i => (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt || i.opcode == OpCodes.Newobj)
                    && i.operand is MethodBase)
                .Select(i =>
                {
                    var method = (MethodBase)i.operand;
                    return (method.DeclaringType?.FullName ?? string.Empty, method.Name);
                })
                .ToList();

        private static int? IntegerConstant(CodeInstruction instruction)
        {
            if (instruction.opcode == OpCodes.Ldc_I4_M1) return -1;
            if (instruction.opcode == OpCodes.Ldc_I4_0) return 0;
            if (instruction.opcode == OpCodes.Ldc_I4_1) return 1;
            if (instruction.opcode == OpCodes.Ldc_I4_2) return 2;
            if (instruction.opcode == OpCodes.Ldc_I4_3) return 3;
            if (instruction.opcode == OpCodes.Ldc_I4_4) return 4;
            if (instruction.opcode == OpCodes.Ldc_I4_5) return 5;
            if (instruction.opcode == OpCodes.Ldc_I4_6) return 6;
            if (instruction.opcode == OpCodes.Ldc_I4_7) return 7;
            if (instruction.opcode == OpCodes.Ldc_I4_8) return 8;
            if (instruction.opcode == OpCodes.Ldc_I4_S) return (sbyte)instruction.operand!;
            if (instruction.opcode == OpCodes.Ldc_I4) return (int)instruction.operand!;
            return null;
        }

        private static bool IsFinite(double3 v) =>
            double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

        private static bool IsFinite(in float4x4 m) =>
            float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
            && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
            && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
            && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

        private readonly struct SourceCandidate
        {
            internal SourceCandidate(StaticCelestial body, string bodyId, AtlasProjection projection,
                UboCameraData camera, DistantSphereMaterialData material, DistantSphereData drawData,
                bool heightFallback, bool isObservedVisible, double observedPixelDiameter)
            {
                Body = body;
                BodyId = bodyId;
                Projection = projection;
                Camera = camera;
                Material = material;
                DrawData = drawData;
                HeightFallback = heightFallback;
                IsObservedVisible = isObservedVisible;
                ObservedPixelDiameter = observedPixelDiameter;
            }

            internal StaticCelestial Body { get; }
            internal string BodyId { get; }
            internal AtlasProjection Projection { get; }
            internal UboCameraData Camera { get; }
            internal DistantSphereMaterialData Material { get; }
            internal DistantSphereData DrawData { get; }
            internal bool HeightFallback { get; }
            internal bool IsObservedVisible { get; }
            internal double ObservedPixelDiameter { get; }
        }

        private sealed class MainAtlasFrame : IDisposable
        {
            internal sealed class Slot
            {
                internal ObjectCaptureContext? Context;
                internal NativeCelestialCaptureAdapter? Adapter;
                internal StaticCelestial? Body;
                internal string? BodyId;
                internal AtlasProjection Projection;
                internal bool HeightFallback;
            }

            internal sealed class SolarSlot
            {
                internal ObjectCaptureContext? Context;
                internal NativeSolarCaptureAdapter? Adapter;
                internal SolarCoverageProbe? Coverage;
                internal SolarGlowFlarePass? GlarePass;
                internal StellarBody? Sun;
                internal string? SunId;
                internal double SunRadius;
                internal AtlasProjection Projection;
                internal SolarSourceGeometry Model;
                internal SolarFrameToken Token;
                internal SolarGlareProjection GlareProjection;
                internal bool GlareAnchored;
                internal float2 GlareAnchorUv;
                internal float GlareAnchorDepth;
                internal bool Prepared;
                internal bool Presented;
                internal bool Culled;
            }

            internal readonly Slot[] Slots = { new(), new() };
            internal readonly SolarSlot Solar = new();
            internal readonly List<StaticCelestial> Bodies = new(AtlasMainPass.MaxBodies);
            internal readonly List<StaticCelestial> CulledBodies = new();
            internal readonly List<StaticCelestial> OmittedBodies = new();
            internal AtlasMainPass? Pass;
            internal ObserverSnapshot? Snapshot;
            internal PresentationVelocity Observer;
            internal int BodyCount;
            internal int SourceCount;
            internal int SyncSlotIndex = -1;
            internal int RendererEpoch = -1;
            internal bool Prepared;
            internal KSA.Rendering.Sun.SunRenderer? PendingSunRenderer;
            internal RenderCore.Systems.ViewHandle PendingSunView;

            internal void Reset()
            {
                Snapshot = null;
                Observer = default;
                Bodies.Clear();
                CulledBodies.Clear();
                OmittedBodies.Clear();
                BodyCount = 0;
                SourceCount = 0;
                SyncSlotIndex = -1;
                RendererEpoch = -1;
                Prepared = false;
                PendingSunRenderer = null;
                PendingSunView = default;
                Solar.Sun = null;
                Solar.SunId = null;
                Solar.SunRadius = 0.0;
                Solar.Projection = default;
                Solar.Model = default;
                Solar.Token = default;
                Solar.GlareProjection = default;
                Solar.GlareAnchored = false;
                Solar.GlareAnchorUv = default;
                Solar.GlareAnchorDepth = 0.0f;
                Solar.Prepared = false;
                Solar.Presented = false;
                Solar.Culled = false;
                foreach (Slot slot in Slots)
                {
                    slot.Body = null;
                    slot.BodyId = null;
                    slot.Projection = default;
                    slot.HeightFallback = false;
                }
            }

            public void Dispose()
            {
                List<Exception>? errors = null;
                try
                {
                    Pass?.Dispose();
                    Pass = null;
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                try
                {
                    Solar.GlarePass?.Dispose();
                    Solar.GlarePass = null;
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                try
                {
                    Solar.Coverage?.Dispose();
                    Solar.Coverage = null;
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                try
                {
                    Solar.Adapter?.Dispose();
                    Solar.Adapter = null;
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                try
                {
                    Solar.Context?.Dispose();
                    Solar.Context = null;
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }

                foreach (Slot slot in Slots)
                {
                    try
                    {
                        slot.Adapter?.Dispose();
                        slot.Adapter = null;
                    }
                    catch (Exception ex)
                    {
                        (errors ??= new List<Exception>()).Add(ex);
                    }

                    try
                    {
                        slot.Context?.Dispose();
                        slot.Context = null;
                    }
                    catch (Exception ex)
                    {
                        (errors ??= new List<Exception>()).Add(ex);
                    }
                }

                if (errors is not null)
                {
                    throw new AggregateException(
                        "main atlas frame teardown had per-resource failures", errors);
                }
            }
        }

        private sealed class ProbeCapture
        {
            internal required ObjectCaptureContext Context;
            internal required NativeCelestialCaptureAdapter Adapter;
            internal required StaticCelestial Body;
            internal required string BodyId;
            internal required ObserverSnapshot Snapshot;
            internal required AtlasProjection Projection;
            internal ImGuiBackendVulkanImpl? Backend;
            internal ImTextureRef Texture;
            internal bool TextureRegistered;
            internal bool Recorded;
            internal bool HeightFallback;
        }

        private sealed class SolarProbeCapture
        {
            internal required ObjectCaptureContext Context;
            internal required NativeSolarCaptureAdapter Adapter;
            internal required StellarBody Sun;
            internal required string SunId;
            internal required double SunRadius;
            internal required ObserverSnapshot Snapshot;
            internal required AtlasProjection Projection;
            internal ImGuiBackendVulkanImpl? Backend;
            internal ImTextureRef Texture;
            internal bool TextureRegistered;
            internal bool Recorded;
            internal SolarCoverageProbe? Coverage;
            internal SolarDepthInspectionProbe? DepthInspection;
        }
    }
}

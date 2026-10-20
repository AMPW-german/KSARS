using System.Reflection;
using HarmonyLib;
using KSA;

namespace KSARS.Visuals
{
    internal static class RenderHooks
    {
        internal const string OwnerId = "KSARS.Visuals";

        private static Harmony? s_harmony;

        private static FieldInfo? s_viewsField;
        private static FieldInfo? s_nextViewportIdField;
        private static FieldInfo? s_allocationSealedField;
        private static FieldInfo? s_shaderIndexPoolField;
        private static FieldInfo? s_disposalBufferField;
        private static FieldInfo? s_descriptorSetsField;
        private static FieldInfo? s_frameCountField;
        private static FieldInfo? s_cameraDataField;
        private static FieldInfo? s_lightingDataField;
        private static FieldInfo? s_celestialDataField;
        private static FieldInfo? s_vesselDataField;
        private static MethodInfo? s_poolCapacityGetter;
        private static MethodInfo? s_shaderSlotGetter;

        private static readonly List<int> s_stageOrder = new();
        private static readonly List<int> s_sealShaderSlots = new();

        private static Action? s_probeCleanup;
        private static Func<string>? s_probeStatus;
        private static Action? s_probeGui;
        private static bool s_probeGuiFailed;

        private static int s_descriptorsViewsLength = -1;
        private static int s_descriptorsNextViewportId = -1;
        private static bool s_descriptorsAllocationSealed = true;
        private static int s_descriptorsPoolCapacity = -1;
        private static int s_sealViewsLength = -1;
        private static int s_sealPoolCapacity = -1;
        private static int s_sealDisposalBufferLength = -1;
        private static int s_cameraDataLength = -1;
        private static int s_lightingDataLength = -1;
        private static int s_celestialDataLength = -1;
        private static int s_vesselDataLength = -1;
        private static int s_descriptorSetsLength = -1;
        private static int s_frameCountObserved;

        private static string s_status = "startup probe not installed; visuals disabled";

        internal static string Status
        {
            get
            {
                string catalog = string.Empty;
                try
                {
                    catalog = StarCatalogHooks.Status;
                }
                catch (Exception)
                {
                    catalog = string.Empty;
                }

                string suffix = catalog.Length == 0 ? string.Empty : $" | {catalog}";
                if (s_probeStatus is null)
                {
                    return s_status + suffix;
                }

                try
                {
                    return $"{s_status} | {s_probeStatus()}{suffix}";
                }
                catch (Exception)
                {
                    return $"{s_status} | source probe status unavailable{suffix}";
                }
            }
        }

        internal static void DrawProbeGui()
        {
            if (s_probeGui is null)
            {
                return;
            }

            try
            {
                s_probeGui();
            }
            catch (Exception ex)
            {
                if (!s_probeGuiFailed)
                {
                    s_probeGuiFailed = true;
                    Console.WriteLine($"KSARS: source probe gui failed ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }

        internal static bool TryInstall(RenderCompatibility.InspectionResult inspection)
        {
            if (!inspection.BaselineMatched)
            {
                return FailInstallRequirement("startup probe not installed: renderer baseline incompatible; visuals disabled");
            }

            IReadOnlyDictionary<string, MemberInfo> members = inspection.Members;
            if (!TryMember(members, "KSA.GlobalShaderBindings::CreateDescriptors", out MethodBase? createDescriptors)
                || !TryMember(members, "KSA.ViewportRegistry::SealAllocation", out MethodBase? sealAllocation)
                || !TryMember(members, "KSA.Program::.ctor", out MethodBase? programCtor)
                || !TryMember(members, "KSA.Program::Dispose", out MethodBase? programDispose)
                || !TryMember(members, "KSA.ViewportRegistry::_views", out FieldInfo? views)
                || !TryMember(members, "KSA.ViewportRegistry::_nextViewportId", out FieldInfo? nextViewportId)
                || !TryMember(members, "KSA.ViewportRegistry::_allocationSealed", out FieldInfo? allocationSealed)
                || !TryMember(members, "KSA.ViewportRegistry::ShaderIndexPool", out FieldInfo? shaderIndexPool)
                || !TryMember(members, "KSA.ViewportRegistry::DisposalBuffer", out FieldInfo? disposalBuffer)
                || !TryMember(members, "KSA.GlobalShaderBindings::_descriptorSets", out FieldInfo? descriptorSets)
                || !TryMember(members, "KSA.GlobalShaderBindings::_frameCount", out FieldInfo? frameCount)
                || !TryMember(members, "KSA.Program::_cameraData", out FieldInfo? cameraData)
                || !TryMember(members, "KSA.Program::_lightingData", out FieldInfo? lightingData)
                || !TryMember(members, "KSA.Program::_celestialData", out FieldInfo? celestialData)
                || !TryMember(members, "KSA.Program::_vesselData", out FieldInfo? vesselData)
                || !TryMember(members, "KSA.IViewport::get_ShaderSlot", out MethodInfo? shaderSlot))
            {
                return FailInstallRequirement("startup probe not installed: required member mapping incomplete; visuals disabled");
            }

            MethodInfo? poolCapacity = shaderIndexPool!.FieldType.GetMethod(
                "get_Capacity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (poolCapacity == null || poolCapacity.ReturnType != typeof(int) || poolCapacity.GetParameters().Length != 0)
            {
                return FailInstallRequirement("startup probe not installed: required member mapping incomplete; visuals disabled");
            }

            if (s_harmony != null)
            {
                return true;
            }

            s_viewsField = views;
            s_nextViewportIdField = nextViewportId;
            s_allocationSealedField = allocationSealed;
            s_shaderIndexPoolField = shaderIndexPool;
            s_disposalBufferField = disposalBuffer;
            s_descriptorSetsField = descriptorSets;
            s_frameCountField = frameCount;
            s_cameraDataField = cameraData;
            s_lightingDataField = lightingData;
            s_celestialDataField = celestialData;
            s_vesselDataField = vesselData;
            s_poolCapacityGetter = poolCapacity;
            s_shaderSlotGetter = shaderSlot;

            var harmony = new Harmony(OwnerId);
            try
            {
                harmony.Patch(createDescriptors, prefix: Hook(nameof(OnCreateDescriptors)));
                harmony.Patch(sealAllocation, prefix: Hook(nameof(OnSealAllocation)));
                harmony.Patch(programCtor, postfix: Hook(nameof(OnProgramConstructed)));
                harmony.Patch(programDispose, prefix: Hook(nameof(OnProgramDisposing)));
            }
            catch (HarmonyException ex)
            {
                return InstallFailed(harmony, ex);
            }
            catch (ArgumentException ex)
            {
                return InstallFailed(harmony, ex);
            }
            catch (InvalidOperationException ex)
            {
                return InstallFailed(harmony, ex);
            }

            s_harmony = harmony;
            s_status = "startup probe installed; observing allocation boundaries; visuals disabled";

            if (Environment.GetEnvironmentVariable("KSARS_ATLAS_PROBE") == "1")
            {
                MethodBase? renderGame = null;
                MethodBase? rebuildRenderer = null;
                MethodBase? distanceData = null;
                try
                {
                    MethodInfo? probeTranspiler = ResolveProbeCoordinator(members,
                        out renderGame, out rebuildRenderer, out MethodInfo? rebuildPostfix,
                        out distanceData, out MethodInfo? distanceDataTranspiler);
                    if (probeTranspiler is not null && renderGame is not null
                        && rebuildRenderer is not null && rebuildPostfix is not null)
                    {
                        harmony.Patch(renderGame, transpiler: new HarmonyMethod(probeTranspiler));
                        harmony.Patch(rebuildRenderer, postfix: new HarmonyMethod(rebuildPostfix));
                        if (distanceData is not null && distanceDataTranspiler is not null)
                        {
                            harmony.Patch(distanceData, transpiler: new HarmonyMethod(distanceDataTranspiler));
                            s_status += "; opt-in source probe installed with verified rebuild-idle retirement boundary and main-view tiny-body omission filter (commands only, GPU output unverified)";
                        }
                        else
                        {
                            s_status += "; opt-in source probe installed with verified rebuild-idle retirement boundary (commands only, GPU output unverified)";
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (renderGame is not null)
                    {
                        try
                        {
                            harmony.Unpatch(renderGame, HarmonyPatchType.Transpiler, OwnerId);
                        }
                        catch (Exception unpatchEx)
                        {
                            s_status += $"; source probe rollback failed: {unpatchEx.GetType().Name}: {unpatchEx.Message}";
                        }
                    }

                    if (rebuildRenderer is not null)
                    {
                        try
                        {
                            harmony.Unpatch(rebuildRenderer, HarmonyPatchType.Postfix, OwnerId);
                        }
                        catch (Exception unpatchEx)
                        {
                            s_status += $"; rebuild boundary rollback failed: {unpatchEx.GetType().Name}: {unpatchEx.Message}";
                        }
                    }

                    if (distanceData is not null)
                    {
                        try
                        {
                            harmony.Unpatch(distanceData, HarmonyPatchType.Transpiler, OwnerId);
                        }
                        catch (Exception unpatchEx)
                        {
                            s_status += $"; tiny-body data rollback failed: {unpatchEx.GetType().Name}: {unpatchEx.Message}";
                        }
                    }

                    try
                    {
                        s_probeCleanup?.Invoke();
                    }
                    catch (Exception cleanupEx)
                    {
                        s_status += $"; source probe cleanup failed: {cleanupEx.GetType().Name}: {cleanupEx.Message}";
                    }

                    s_probeCleanup = null;
                    s_probeStatus = null;
                    s_probeGui = null;
                    s_probeGuiFailed = false;
                    Exception inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                    s_status += $"; source probe disabled: {inner.GetType().Name}: {inner.Message}";
                    Console.WriteLine($"KSARS: {s_status}");
                }

                try
                {
                    StarCatalogHooks.TryInstall(harmony);
                }
                catch (Exception catalogEx)
                {
                    ResetCatalogStateIfLoaded();
                    s_status += $"; star catalog capture disabled: {catalogEx.GetType().Name}: {catalogEx.Message}";
                    Console.WriteLine($"KSARS: {s_status}");
                }
            }

            if (Environment.GetEnvironmentVariable("KSARS_STAR_PROBE") == "1")
            {
                try
                {
                    if (!StarCatalogHooks.Installed)
                    {
                        StarCatalogHooks.TryInstall(harmony);
                    }

                    RelativisticSpaceRenderer.TryInstallStarProbe(harmony, members);
                }
                catch (Exception starEx)
                {
                    ResetStarProbeStateIfLoaded();
                    s_status += $"; star probe disabled: {starEx.GetType().Name}: {starEx.Message}; native stars retained";
                    Console.WriteLine($"KSARS: {s_status}");
                }
            }

            if (Environment.GetEnvironmentVariable("KSARS_MILKYWAY_PROBE") == "1")
            {
                try
                {
                    RelativisticSpaceRenderer.TryInstallMilkyWayProbe(harmony, members);
                }
                catch (Exception milkyWayEx)
                {
                    ResetMilkyWayProbeStateIfLoaded();
                    s_status += $"; milky way probe disabled: {milkyWayEx.GetType().Name}: {milkyWayEx.Message}; native milky way retained";
                    Console.WriteLine($"KSARS: {s_status}");
                }
            }

            if (Environment.GetEnvironmentVariable("KSARS_LIGHTING_PROBE") == "1")
            {
                try
                {
                    RelativisticSpaceRenderer.TryInstallLightingProbe(harmony, members);
                }
                catch (Exception lightingEx)
                {
                    ResetLightingProbeStateIfLoaded();
                    s_status += $"; vessel lighting probe disabled: {lightingEx.GetType().Name}: {lightingEx.Message}; native vessel lighting retained";
                    Console.WriteLine($"KSARS: {s_status}");
                }
            }

            if (Environment.GetEnvironmentVariable("KSARS_SHADOW_PROBE") == "1")
            {
                try
                {
                    RelativisticSpaceRenderer.TryInstallVesselShadowProbe(harmony);
                }
                catch (Exception shadowEx)
                {
                    ResetVesselShadowProbeStateIfLoaded();
                    s_status += $"; vessel shadow probe disabled: {shadowEx.GetType().Name}: {shadowEx.Message}; native cascaded shadows retained";
                    Console.WriteLine($"KSARS: {s_status}");
                }
            }

            if (Environment.GetEnvironmentVariable("KSARS_MEDIA_PROBE") == "1")
            {
                try
                {
                    RelativisticSpaceRenderer.TryInstallMediaProbe(harmony, members);
                }
                catch (Exception mediaEx)
                {
                    ResetMediaProbeStateIfLoaded();
                    s_status += $"; media probe disabled: {mediaEx.GetType().Name}: {mediaEx.Message}; native body positions retained";
                    Console.WriteLine($"KSARS: {s_status}");
                }
            }

            return true;
        }

        internal static void Uninstall(string boundary)
        {
            if (s_harmony == null)
            {
                return;
            }

            try
            {
                s_probeCleanup?.Invoke();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"KSARS: source probe cleanup failed ({ex.GetType().Name}: {ex.Message})");
            }

            s_probeCleanup = null;
            s_probeStatus = null;
            s_probeGui = null;
            s_probeGuiFailed = false;

            s_harmony.UnpatchAll(OwnerId);
            s_harmony = null;
            ResetState();
            s_status = $"startup probe uninstalled at {boundary}; visuals disabled";
            Console.WriteLine($"KSARS: {s_status}");
        }

        private static bool FailInstallRequirement(string status)
        {
            if (s_harmony != null)
            {
                Uninstall("failed probe re-validation");
            }

            s_status = status;
            return false;
        }

        private static bool InstallFailed(Harmony harmony, Exception ex)
        {
            harmony.UnpatchAll(OwnerId);
            ResetState();
            s_status = $"startup probe disabled: patch installation failed ({ex.GetType().Name}: {ex.Message}); visuals disabled";
            Console.WriteLine($"KSARS: {s_status}");
            return false;
        }

        private static MethodInfo? ResolveProbeCoordinator(
            IReadOnlyDictionary<string, MemberInfo> members,
            out MethodBase? renderGame, out MethodBase? rebuildRenderer, out MethodInfo? rebuildPostfix,
            out MethodBase? nativeDistanceData, out MethodInfo? distanceDataTranspiler)
        {
            renderGame = null;
            rebuildRenderer = null;
            rebuildPostfix = null;
            nativeDistanceData = null;
            distanceDataTranspiler = null;
            s_probeCleanup = null;
            s_probeStatus = null;
            s_probeGui = null;

            if (!TryMember(members, "KSA.Program::RenderGame", out MethodBase? member)
                || member is not MethodInfo target || target.DeclaringType?.FullName != "KSA.Program")
            {
                s_status += "; source probe disabled: KSA.Program::RenderGame member mapping missing";
                return null;
            }

            if (!TryMember(members, "KSA.Program::RebuildRenderer", out MethodBase? rebuildMember)
                || rebuildMember is not MethodInfo rebuild || rebuild.DeclaringType?.FullName != "KSA.Program")
            {
                s_status += "; source probe disabled: KSA.Program::RebuildRenderer member mapping missing";
                return null;
            }

            renderGame = target;
            rebuildRenderer = rebuild;

            bool mainProbe = Environment.GetEnvironmentVariable("KSARS_ATLAS_MAIN_PROBE") == "1";
            Type? type = typeof(RenderHooks).Assembly.GetType("KSARS.Visuals.RelativisticSpaceRenderer", throwOnError: false);
            MethodInfo? prepare = type?.GetMethod("PrepareProbe", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo? transpiler = type?.GetMethod("TranspileMainRenderGame", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo? onRebuilt = type?.GetMethod("OnRendererRebuilt", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo? getStatus = type?.GetMethod("GetStatus", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo? shutdown = type?.GetMethod("Shutdown", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo? gui = type?.GetMethod("DrawProbeGui", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo? distanceTranspiler = type?.GetMethod("TranspileNativeDistanceData", BindingFlags.NonPublic | BindingFlags.Static);
            if (type is null || prepare is null || transpiler is null || onRebuilt is null || shutdown is null || gui is null
                || getStatus is null || getStatus.ReturnType != typeof(string)
                || getStatus.GetParameters().Length != 0
                || onRebuilt.ReturnType != typeof(void) || onRebuilt.GetParameters().Length != 0
                || shutdown.ReturnType != typeof(void) || shutdown.GetParameters().Length != 0
                || gui.ReturnType != typeof(void) || gui.GetParameters().Length != 0
                || prepare.ReturnType != typeof(void)
                || prepare.GetParameters().Length != 1
                || prepare.GetParameters()[0].ParameterType != typeof(IReadOnlyDictionary<string, MemberInfo>)
                || transpiler.ReturnType != typeof(IEnumerable<CodeInstruction>)
                || transpiler.GetParameters().Length != 1
                || transpiler.GetParameters()[0].ParameterType != typeof(IEnumerable<CodeInstruction>)
                || (mainProbe
                    && (distanceTranspiler is null
                        || distanceTranspiler.ReturnType != typeof(IEnumerable<CodeInstruction>)
                        || distanceTranspiler.GetParameters().Length != 1
                        || distanceTranspiler.GetParameters()[0].ParameterType != typeof(IEnumerable<CodeInstruction>))))
            {
                renderGame = null;
                rebuildRenderer = null;
                s_status += "; source probe disabled: RelativisticSpaceCoordinator members missing or mismatched";
                return null;
            }

            MethodInfo? distanceData = null;
            if (mainProbe)
            {
                distanceData = typeof(StaticCelestialDistanceRendering).GetMethod(
                    "UpdateRenderData", BindingFlags.Public | BindingFlags.Static);
                if (distanceData is null || distanceData.ReturnType != typeof(void)
                    || distanceData.GetParameters() is not { Length: 2 } distanceParameters
                    || distanceParameters[0].ParameterType != typeof(IViewport)
                    || distanceParameters[1].ParameterType != typeof(int))
                {
                    renderGame = null;
                    rebuildRenderer = null;
                    s_status += "; source probe disabled: KSA.StaticCelestialDistanceRendering::UpdateRenderData signature changed";
                    return null;
                }
            }

            prepare.Invoke(null, new object?[] { members });
            rebuildPostfix = onRebuilt;
            nativeDistanceData = distanceData;
            distanceDataTranspiler = mainProbe ? distanceTranspiler : null;

            s_probeCleanup = shutdown.CreateDelegate<Action>();
            s_probeStatus = getStatus.CreateDelegate<Func<string>>();
            s_probeGui = gui.CreateDelegate<Action>();
            return transpiler;
        }

        private static void ResetState()
        {
            s_stageOrder.Clear();
            s_sealShaderSlots.Clear();
            s_descriptorsViewsLength = -1;
            s_descriptorsNextViewportId = -1;
            s_descriptorsAllocationSealed = true;
            s_descriptorsPoolCapacity = -1;
            s_sealViewsLength = -1;
            s_sealPoolCapacity = -1;
            s_sealDisposalBufferLength = -1;
            s_cameraDataLength = -1;
            s_lightingDataLength = -1;
            s_celestialDataLength = -1;
            s_vesselDataLength = -1;
            s_descriptorSetsLength = -1;
            s_frameCountObserved = 0;
            s_viewsField = null;
            s_nextViewportIdField = null;
            s_allocationSealedField = null;
            s_shaderIndexPoolField = null;
            s_disposalBufferField = null;
            s_descriptorSetsField = null;
            s_frameCountField = null;
            s_cameraDataField = null;
            s_lightingDataField = null;
            s_celestialDataField = null;
            s_vesselDataField = null;
            s_poolCapacityGetter = null;
            s_shaderSlotGetter = null;
            ResetCatalogStateIfLoaded();
            ResetStarProbeStateIfLoaded();
            ResetMilkyWayProbeStateIfLoaded();
            ResetLightingProbeStateIfLoaded();
            ResetMediaProbeStateIfLoaded();
        }

        // StarCatalogHooks/RelativisticSpaceRenderer member signatures reference
        // KSA types; in hosts without the game assemblies those methods cannot be
        // compiled. Nothing can have been installed there, so skipping reset is a
        // no-op. Only dependency-resolution failures are ignored.
        private static void ResetCatalogStateIfLoaded()
        {
            try
            {
                StarCatalogHooks.ResetState();
            }
            catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or MissingMethodException or TypeInitializationException)
            {
            }
        }

        private static void ResetStarProbeStateIfLoaded()
        {
            try
            {
                RelativisticSpaceRenderer.ResetStarProbeState();
            }
            catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or MissingMethodException or TypeInitializationException)
            {
            }
        }

        private static void ResetMilkyWayProbeStateIfLoaded()
        {
            try
            {
                RelativisticSpaceRenderer.ResetMilkyWayProbeState();
            }
            catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or MissingMethodException or TypeInitializationException)
            {
            }
        }

        private static void ResetVesselShadowProbeStateIfLoaded()
        {
            try
            {
                RelativisticSpaceRenderer.ResetVesselShadowProbeState();
            }
            catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or MissingMethodException or TypeInitializationException)
            {
            }
        }

        private static void ResetLightingProbeStateIfLoaded()
        {
            try
            {
                RelativisticSpaceRenderer.ResetLightingProbeState();
            }
            catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or MissingMethodException or TypeInitializationException)
            {
            }
        }

        private static void ResetMediaProbeStateIfLoaded()
        {
            try
            {
                RelativisticSpaceRenderer.ResetMediaProbeState();
            }
            catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or MissingMethodException or TypeInitializationException)
            {
            }
        }

        private static void OnCreateDescriptors()
        {
            s_stageOrder.Add(1);
            s_descriptorsViewsLength = ArrayLength(s_viewsField);
            s_descriptorsNextViewportId = IntValue(s_nextViewportIdField);
            s_descriptorsAllocationSealed = BoolValue(s_allocationSealedField);
            s_descriptorsPoolCapacity = PoolCapacity();
            Console.WriteLine($"KSARS: startup probe observed descriptor creation boundary: views={s_descriptorsViewsLength} nextViewportId={s_descriptorsNextViewportId} sealed={s_descriptorsAllocationSealed} poolCapacity={s_descriptorsPoolCapacity}");
        }

        private static void OnSealAllocation()
        {
            s_stageOrder.Add(2);
            var views = s_viewsField?.GetValue(null) as Array;
            s_sealViewsLength = views?.Length ?? -1;
            s_sealShaderSlots.Clear();
            if (views != null && s_shaderSlotGetter != null)
            {
                foreach (object? view in views)
                {
                    if (view != null && s_shaderSlotGetter.Invoke(view, null) is int slot)
                    {
                        s_sealShaderSlots.Add(slot);
                    }
                }
            }

            s_sealPoolCapacity = PoolCapacity();
            s_sealDisposalBufferLength = ArrayLength(s_disposalBufferField);
            Console.WriteLine($"KSARS: startup probe observed viewport seal boundary: views={s_sealViewsLength} slots=[{string.Join(",", s_sealShaderSlots)}] poolCapacity={s_sealPoolCapacity} disposal={s_sealDisposalBufferLength}");
        }

        private static void OnProgramConstructed()
        {
            s_stageOrder.Add(3);
            s_cameraDataLength = ArrayLength(s_cameraDataField);
            s_lightingDataLength = ArrayLength(s_lightingDataField);
            s_celestialDataLength = ArrayLength(s_celestialDataField);
            s_vesselDataLength = ArrayLength(s_vesselDataField);
            s_descriptorSetsLength = ArrayLength(s_descriptorSetsField);
            s_frameCountObserved = IntValue(s_frameCountField);
            Console.WriteLine($"KSARS: startup probe observed constructor boundary: frames={s_frameCountObserved} descriptorSets={s_descriptorSetsLength} camera={s_cameraDataLength} lighting={s_lightingDataLength} celestial={s_celestialDataLength} vessel={s_vesselDataLength}");

            var observation = new StartupObservation
            {
                StageOrder = s_stageOrder.ToArray(),
                DescriptorsViewsLength = s_descriptorsViewsLength,
                DescriptorsNextViewportId = s_descriptorsNextViewportId,
                DescriptorsAllocationSealed = s_descriptorsAllocationSealed,
                DescriptorsPoolCapacity = s_descriptorsPoolCapacity,
                SealViewsLength = s_sealViewsLength,
                SealPoolCapacity = s_sealPoolCapacity,
                SealDisposalBufferLength = s_sealDisposalBufferLength,
                SealShaderSlots = s_sealShaderSlots.ToArray(),
                CameraDataLength = s_cameraDataLength,
                LightingDataLength = s_lightingDataLength,
                CelestialDataLength = s_celestialDataLength,
                VesselDataLength = s_vesselDataLength,
                DescriptorSetsLength = s_descriptorSetsLength,
                FrameCount = s_frameCountObserved,
            };
            var validation = ValidateStartup(observation);
            if (!validation.Passed)
            {
                Uninstall("failed startup validation");
            }
            s_status = validation.Status;
            Console.WriteLine($"KSARS: {s_status}");
        }

        private static void OnProgramDisposing()
        {
            int observed = s_stageOrder.Count;
            Uninstall("program teardown");
            if (observed < 3)
            {
                s_status = $"startup probe incomplete: {observed} of 3 allocation stages observed; visuals disabled";
            }
        }

        internal static StartupValidation ValidateStartup(in StartupObservation observation)
        {
            int[] order = observation.StageOrder ?? Array.Empty<int>();
            if (order.Length == 0)
            {
                return Fail("startup probe incomplete: no allocation boundary observed; visuals disabled");
            }

            if (order.Length > 3)
            {
                return Fail("startup probe failed: allocation stage observed more than once; visuals disabled");
            }

            for (int i = 0; i < order.Length; i++)
            {
                if (order[i] != i + 1)
                {
                    return Fail($"startup probe failed: allocation stages out of order ({string.Join("->", order)}); visuals disabled");
                }
            }

            if (order.Length < 3)
            {
                return Fail($"startup probe incomplete: {order.Length} of 3 allocation stages observed; visuals disabled");
            }

            if (observation.DescriptorsViewsLength != 0)
            {
                return Fail("startup probe failed: descriptor stage found existing viewport allocations; visuals disabled");
            }

            if (observation.DescriptorsNextViewportId != 1)
            {
                return Fail("startup probe failed: viewport id counter was not initial; visuals disabled");
            }

            if (observation.DescriptorsAllocationSealed)
            {
                return Fail("startup probe failed: viewport allocation sealed before descriptor stage; visuals disabled");
            }

            if (observation.DescriptorsPoolCapacity != 8)
            {
                return Fail("startup probe failed: shader index pool capacity was not baseline at descriptor stage; visuals disabled");
            }

            if (observation.SealViewsLength != 8)
            {
                return Fail("startup probe failed: viewport count was not baseline at seal stage; visuals disabled");
            }

            if (observation.SealPoolCapacity != 8)
            {
                return Fail("startup probe failed: shader index pool capacity was not baseline at seal stage; visuals disabled");
            }

            if (observation.SealDisposalBufferLength != 8)
            {
                return Fail("startup probe failed: disposal buffer length was not baseline; visuals disabled");
            }

            int[] slots = observation.SealShaderSlots ?? Array.Empty<int>();
            if (slots.Length != 8)
            {
                return Fail("startup probe failed: shader slot list was incomplete; visuals disabled");
            }

            if (slots.Distinct().Count() != 8)
            {
                return Fail("startup probe failed: shader slot ids were not unique; visuals disabled");
            }

            if (slots.Any(s => s < 0 || s > 7))
            {
                return Fail("startup probe failed: shader slot id was out of baseline range; visuals disabled");
            }

            if (observation.FrameCount <= 0)
            {
                return Fail("startup probe failed: renderer frame count was not initialized; visuals disabled");
            }

            if (observation.DescriptorSetsLength != (long)observation.FrameCount * 8)
            {
                return Fail("startup probe failed: descriptor set count did not match frames and slots; visuals disabled");
            }

            if (observation.CameraDataLength != 8
                || observation.LightingDataLength != 8
                || observation.CelestialDataLength != 8
                || observation.VesselDataLength != 8)
            {
                return Fail("startup probe failed: cpu uniform buffer length was not baseline; visuals disabled");
            }

            return new StartupValidation(true,
                $"startup observed before allocation; {observation.SealPoolCapacity} baseline slots; object-atlas/foreground gate not attempted");

            static StartupValidation Fail(string status) => new(false, status);
        }

        private static bool TryMember<T>(IReadOnlyDictionary<string, MemberInfo> members, string key, out T? member)
            where T : MemberInfo
        {
            member = members.TryGetValue(key, out MemberInfo? found) ? found as T : null;
            return member != null;
        }

        private static HarmonyMethod Hook(string name) =>
            new(typeof(RenderHooks).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!);

        private static int ArrayLength(FieldInfo? field) => (field?.GetValue(null) as Array)?.Length ?? -1;

        private static int IntValue(FieldInfo? field) => field?.GetValue(null) is int value ? value : -1;

        private static bool BoolValue(FieldInfo? field) => field?.GetValue(null) is true;

        private static int PoolCapacity()
        {
            object? pool = s_shaderIndexPoolField?.GetValue(null);
            return pool != null && s_poolCapacityGetter?.Invoke(pool, null) is int capacity ? capacity : -1;
        }
    }

    internal struct StartupObservation
    {
        public int[]? StageOrder;
        public int DescriptorsViewsLength;
        public int DescriptorsNextViewportId;
        public bool DescriptorsAllocationSealed;
        public int DescriptorsPoolCapacity;
        public int SealViewsLength;
        public int SealPoolCapacity;
        public int SealDisposalBufferLength;
        public int[]? SealShaderSlots;
        public int CameraDataLength;
        public int LightingDataLength;
        public int CelestialDataLength;
        public int VesselDataLength;
        public int DescriptorSetsLength;
        public int FrameCount;
    }

    internal readonly struct StartupValidation
    {
        public StartupValidation(bool passed, string status)
        {
            Passed = passed;
            Status = status;
        }

        public bool Passed { get; }
        public string Status { get; }
    }
}

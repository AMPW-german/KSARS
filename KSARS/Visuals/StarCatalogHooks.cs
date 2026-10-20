using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KSARS.Visuals
{
    // Read-only capture seam for KSA.Mod::LoadStarBinaries. The single
    // native InstancedStarTechnique::AddInstance call is routed through a static
    // wrapper that always invokes the native method exactly once first, then
    // records the raw record into StarCatalog. Native draws, file reads, and
    // exceptions are never altered, suppressed, or swallowed.
    internal static class StarCatalogHooks
    {
        private static MethodBase? s_loader;
        private static MethodInfo? s_addInstance;
        private static MethodInfo? s_wrapper;
        private static Action<object, IViewport, float3, byte, byte4, float>? s_nativeAddInstance;
        private static ThermalSpectrumTable? s_table;
        private static StarSpectralCache? s_cache;
        private static bool s_installed;
        private static bool s_faultLogged;
        private static string s_status = "";

        internal static string Status
        {
            get
            {
                string status = s_status;
                if (s_installed)
                {
                    status += (status.Length == 0 ? "" : " | ") + StarCatalog.Describe();
                }

                if (s_cache is { } cache)
                {
                    status += (status.Length == 0 ? "" : " | ") + $"spectral cache: {cache.Describe()}";
                }

                return status;
            }
        }

        internal static bool Installed => s_installed;

        // Read-only accessors for the relativistic star pass; both are null until
        // a successful install and stay null after a failed install rollback.
        internal static ThermalSpectrumTable? Table => s_table;
        internal static StarSpectralCache? Cache => s_cache;

        internal static bool TryInstall(Harmony harmony)
        {
            if (s_installed)
            {
                return true;
            }

            s_status = "";
            ThermalSpectrumTable table;
            try
            {
                table = ThermalSpectrumTable.LoadEmbedded();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                s_status = $"star catalog capture disabled: thermal table v2 unusable ({ex.GetType().Name}: {ex.Message})";
                Console.WriteLine($"KSARS: {s_status}");
                return false;
            }

            MethodBase? loader = null;
            try
            {
                (loader, MethodInfo addInstance) = ResolveTargets();
                PrepareCapture(addInstance);
                var cache = new StarSpectralCache(table);

                MethodInfo prefix = typeof(StarCatalogHooks).GetMethod(
                    nameof(LoadStarBinariesPrefix), BindingFlags.NonPublic | BindingFlags.Static)!;
                MethodInfo finalizer = typeof(StarCatalogHooks).GetMethod(
                    nameof(LoadStarBinariesFinalizer), BindingFlags.NonPublic | BindingFlags.Static)!;
                MethodInfo transpiler = typeof(StarCatalogHooks).GetMethod(
                    nameof(TranspileLoadStarBinaries), BindingFlags.NonPublic | BindingFlags.Static)!;

                harmony.Patch(loader,
                    prefix: new HarmonyMethod(prefix),
                    transpiler: new HarmonyMethod(transpiler),
                    finalizer: new HarmonyMethod(finalizer));

                s_loader = loader;
                s_table = table;
                s_cache = cache;
                s_installed = true;
                s_status = "star catalog capture installed: raw direction/scale/RGB records only; thermal table v2 ready; no spectral transform active";
                return true;
            }
            catch (Exception ex)
            {
                // A failed method-scoped rollback leaves the live wrapper callable:
                // keep the resolved delegate and capture state in that case.
                bool unpatchFailed = false;
                string rollbackNote = "";
                if (loader is not null)
                {
                    try
                    {
                        harmony.Unpatch(loader, HarmonyPatchType.All, RenderHooks.OwnerId);
                    }
                    catch (Exception unpatchEx)
                    {
                        unpatchFailed = true;
                        rollbackNote = $"; rollback failed: {unpatchEx.GetType().Name}: {unpatchEx.Message}; capture state preserved";
                    }
                }

                if (!unpatchFailed)
                {
                    s_loader = null;
                    s_addInstance = null;
                    s_wrapper = null;
                    s_nativeAddInstance = null;
                    s_table = null;
                    s_cache = null;
                    s_installed = false;
                    StarCatalog.Reset();
                }

                s_status = $"star catalog capture disabled: {ex.GetType().Name}: {ex.Message}{rollbackNote}";
                Console.WriteLine($"KSARS: {s_status}");
                return false;
            }
        }

        private static (MethodBase Loader, MethodInfo AddInstance) ResolveTargets()
        {
            MethodInfo? loader = typeof(Mod).GetMethod("LoadStarBinaries",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            ParameterInfo[]? parameters = loader?.GetParameters();
            if (loader is null || loader.ReturnType != typeof(void)
                || parameters is not { Length: 1 }
                || parameters[0].ParameterType is not { } techniqueType
                || techniqueType.IsValueType
                || techniqueType.Name != "InstancedStarTechnique")
            {
                throw new InvalidOperationException(
                    "star catalog unsupported: KSA.Mod::LoadStarBinaries(InstancedStarTechnique) signature changed");
            }

            MethodInfo? mainViewportGetter = typeof(Program)
                .GetProperty("MainViewport", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetGetMethod(nonPublic: true);
            if (mainViewportGetter is null
                || !typeof(IViewport).IsAssignableFrom(mainViewportGetter.ReturnType))
            {
                throw new InvalidOperationException(
                    "star catalog unsupported: KSA.Program::get_MainViewport missing or mismatched");
            }

            List<CodeInstruction> original;
            try
            {
                original = PatchProcessor.GetOriginalInstructions(loader);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"star catalog unsupported: LoadStarBinaries original instructions unreadable ({ex.GetType().Name}: {ex.Message})", ex);
            }

            int addCount = 0;
            int viewportCount = 0;
            MethodInfo? addInstance = null;
            foreach (CodeInstruction instruction in original)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && instruction.operand is MethodInfo method)
                {
                    if (method.Equals(mainViewportGetter))
                    {
                        viewportCount++;
                    }
                    else if (method.Name == "AddInstance" && method.DeclaringType is { } declaring
                        && (declaring.IsAssignableFrom(techniqueType) || techniqueType.IsAssignableFrom(declaring)))
                    {
                        addCount++;
                        addInstance = method;
                    }
                }
            }

            if (addCount != 1 || viewportCount < 1 || addInstance is null)
            {
                throw new InvalidOperationException(
                    $"star catalog unsupported: LoadStarBinaries anchors changed (AddInstance={addCount}, get_MainViewport={viewportCount})");
            }

            if (addInstance.ReturnType != typeof(void)
                || !ParametersAre(addInstance,
                    typeof(IViewport), typeof(float3), typeof(byte), typeof(byte4), typeof(float)))
            {
                throw new InvalidOperationException(
                    $"star catalog unsupported: {addInstance.DeclaringType?.FullName}::AddInstance signature {DescribeSignature(addInstance)} changed");
            }

            return (loader, addInstance);
        }

        private static string DescribeSignature(MethodInfo method) =>
            $"{method.ReturnType.Name} {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";

        private static void PrepareCapture(MethodInfo addInstance)
        {
            MethodInfo? wrapper = typeof(StarCatalogHooks).GetMethod(
                nameof(CaptureAddInstance), BindingFlags.NonPublic | BindingFlags.Static);
            if (wrapper is null || wrapper.ReturnType != typeof(void)
                || !ParametersAre(wrapper,
                    typeof(object), typeof(IViewport), typeof(float3), typeof(byte), typeof(byte4), typeof(float)))
            {
                throw new InvalidOperationException("star catalog unsupported: capture wrapper missing or mismatched");
            }

            var technique = Expression.Parameter(typeof(object), "technique");
            var viewport = Expression.Parameter(typeof(IViewport), "viewport");
            var direction = Expression.Parameter(typeof(float3), "direction");
            var scale = Expression.Parameter(typeof(byte), "scale");
            var color = Expression.Parameter(typeof(byte4), "color");
            var distance = Expression.Parameter(typeof(float), "distance");
            Expression nativeCall = Expression.Call(
                Expression.Convert(technique, addInstance.DeclaringType!),
                addInstance, viewport, direction, scale, color, distance);
            s_nativeAddInstance = Expression
                .Lambda<Action<object, IViewport, float3, byte, byte4, float>>(
                    nativeCall, technique, viewport, direction, scale, color, distance)
                .Compile();

            s_addInstance = addInstance;
            s_wrapper = wrapper;
        }

        private static bool ParametersAre(MethodInfo method, params Type[] expected) =>
            method.GetParameters().Select(p => p.ParameterType).SequenceEqual(expected);

        // Prefix: opens a transaction on this thread. Any capture-side failure is
        // contained here; the prefix never controls native execution.
        private static void LoadStarBinariesPrefix(object __instance, object __0,
            out StarCatalog.CaptureScope? __state)
        {
            __state = null;
            try
            {
                __state = StarCatalog.Begin(__instance, __0);
            }
            catch (Exception ex)
            {
                LogFault("prefix", ex);
            }
        }

        // Finalizer: commits the batch only when the native loader returned
        // normally, warms the spectral cache only for a published batch, then
        // logs one status line per committed batch; the original exception is
        // rethrown unchanged by Harmony.
        private static Exception? LoadStarBinariesFinalizer(Exception? __exception, StarCatalog.CaptureScope? __state)
        {
            try
            {
                if (StarCatalog.Complete(__state, __exception is null))
                {
                    if (__state is not null && s_cache is { } cache)
                    {
                        foreach (StarCatalog.Entry entry in __state.Entries)
                        {
                            cache.TryGet(entry.PackedRgb, out _);
                        }
                    }

                    Console.WriteLine($"KSARS: {Status}");
                }
            }
            catch (Exception ex)
            {
                LogFault("finalizer", ex);
            }

            return __exception;
        }

        // Replaces the single native callvirt; the original receiver and argument
        // stack are unchanged. The native submission always happens first and
        // exactly once; only its success is observed for capture.
        private static void CaptureAddInstance(object technique, IViewport viewport,
            float3 direction, byte scale, byte4 color, float distance)
        {
            s_nativeAddInstance!(technique, viewport, direction, scale, color, distance);

            StarCatalog.CaptureScope? scope = null;
            try
            {
                scope = StarCatalog.Current;
                if (scope is null
                    || !ReferenceEquals(scope.Technique, technique)
                    || !ReferenceEquals(viewport, Program.MainViewport))
                {
                    return;
                }

                uint packed = Unsafe.As<byte4, uint>(ref color) & 0xffffffu;
                scope.Add(new OpticalVector(direction.X, direction.Y, direction.Z), scale, packed);
            }
            catch (Exception ex)
            {
                if (scope is not null)
                {
                    scope.Failed = true;
                }

                LogFault("capture", ex);
            }
        }

        internal static IEnumerable<CodeInstruction> TranspileLoadStarBinaries(IEnumerable<CodeInstruction> instructions)
        {
            if (s_addInstance is null || s_wrapper is null)
            {
                throw new InvalidOperationException("star catalog transpiler invoked before capture preparation");
            }

            List<CodeInstruction> list = instructions.ToList();
            int index = -1;
            int count = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if ((list[i].opcode == OpCodes.Call || list[i].opcode == OpCodes.Callvirt)
                    && list[i].operand is MethodInfo method && method.Equals(s_addInstance))
                {
                    count++;
                    index = i;
                }
            }

            if (count != 1)
            {
                throw new InvalidOperationException(
                    $"star catalog unsupported: LoadStarBinaries AddInstance anchor count {count}");
            }

            var replacement = new CodeInstruction(OpCodes.Call, s_wrapper);
            replacement.labels.AddRange(list[index].labels);
            replacement.blocks.AddRange(list[index].blocks);
            list[index] = replacement;
            return list;
        }

        internal static void ResetState()
        {
            s_loader = null;
            s_addInstance = null;
            s_wrapper = null;
            s_nativeAddInstance = null;
            s_table = null;
            s_cache = null;
            s_installed = false;
            s_faultLogged = false;
            s_status = "";
            StarCatalog.Reset();
        }

        private static void LogFault(string phase, Exception ex)
        {
            if (s_faultLogged)
            {
                return;
            }

            s_faultLogged = true;
            s_status = $"star catalog capture degraded: {phase} fault ({ex.GetType().Name}: {ex.Message}); native loader unaffected";
            Console.WriteLine($"KSARS: {s_status}");
        }
    }
}

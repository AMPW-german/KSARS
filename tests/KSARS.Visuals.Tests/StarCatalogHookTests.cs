using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using KSARS.Visuals;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    [Collection("VisualHookState")]
    public class StarCatalogHookTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");

        private static AssemblyDefinition LoadModAssembly() => AssemblyDefinition.ReadAssembly(ModAssemblyPath);

        private static MethodDefinition SingleMethod(TypeDefinition type, string name) =>
            type.Methods.Single(m => m.Name == name);

        private static List<(int Index, MethodReference Reference)> CallsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Select((instruction, index) => (instruction, index))
                .Where(x => (x.instruction.OpCode == OpCodes.Call || x.instruction.OpCode == OpCodes.Callvirt)
                    && x.instruction.Operand is MethodReference)
                .Select(x => (x.index, (MethodReference)x.instruction.Operand))
                .ToList();

        private static TypeDefinition HooksType(ModuleDefinition module)
        {
            var type = module.GetType("KSARS.Visuals.StarCatalogHooks");
            Assert.NotNull(type);
            return type;
        }

        private static readonly string[] CapturePathMethods =
        {
            "CaptureAddStar", "LoadStarBinariesPrefix", "LoadStarBinariesFinalizer",
        };

        [Fact]
        public void Wrapper_InvokesNativeSubmissionExactlyOnceBeforeAnyCaptureWork()
        {
            using var assembly = LoadModAssembly();
            var wrapper = SingleMethod(HooksType(assembly.MainModule), "CaptureAddStar");

            Assert.Equal(
                new[]
                {
                    "System.Object", "Brutal.Numerics.float3", "System.Single",
                    "System.Byte", "Brutal.Numerics.byte4",
                },
                wrapper.Parameters.Select(p => p.ParameterType.FullName).ToArray());

            var instructions = wrapper.Body.Instructions;
            var invokeCalls = instructions
                .Select((instruction, index) => (instruction, index))
                .Where(x => x.instruction.OpCode == OpCodes.Callvirt
                    && x.instruction.Operand is MethodReference mr
                    && mr.Name == "Invoke"
                    && mr.DeclaringType.FullName.StartsWith("System.Action`5", StringComparison.Ordinal))
                .Select(x => x.index)
                .ToList();
            var invokeIndex = Assert.Single(invokeCalls);

            var calls = CallsOf(wrapper);
            Assert.Equal(invokeIndex, calls[0].Index);
            Assert.Contains(calls, c => c.Index > invokeIndex
                && c.Reference.DeclaringType.FullName == "KSARS.Visuals.StarCatalog"
                && c.Reference.Name == "get_Current");
            Assert.DoesNotContain(calls, c => c.Reference.Name == "AddStar");
        }

        [Fact]
        public void CapturePath_PerformsNoFileIOAndNoGpuWork()
        {
            using var assembly = LoadModAssembly();
            var hooks = HooksType(assembly.MainModule);
            foreach (string name in CapturePathMethods)
            {
                var method = SingleMethod(hooks, name);
                foreach (var call in CallsOf(method))
                {
                    string declaring = call.Reference.DeclaringType.FullName;
                    Assert.False(declaring.StartsWith("System.IO", StringComparison.Ordinal),
                        $"{method.Name} performs file I/O via {declaring}::{call.Reference.Name}");
                    Assert.False(declaring.StartsWith("Brutal.VulkanApi", StringComparison.Ordinal)
                        || declaring.StartsWith("RenderCore", StringComparison.Ordinal)
                        || declaring == "KSA.GlobalShaderBindings",
                        $"{method.Name} reaches GPU/UBO work via {declaring}::{call.Reference.Name}");
                }
            }
        }

        [Fact]
        public void PrefixAndFinalizer_ContainCaptureFailures()
        {
            using var assembly = LoadModAssembly();
            var hooks = HooksType(assembly.MainModule);
            var prefix = SingleMethod(hooks, "LoadStarBinariesPrefix");
            var finalizer = SingleMethod(hooks, "LoadStarBinariesFinalizer");

            Assert.True(prefix.Body.HasExceptionHandlers, "prefix must contain capture failures");
            Assert.True(finalizer.Body.HasExceptionHandlers, "finalizer must contain capture failures");

            Assert.Equal(3, prefix.Parameters.Count);
            Assert.Equal("__instance", prefix.Parameters[0].Name);
            Assert.Equal("__0", prefix.Parameters[1].Name);
            Assert.Equal("__state", prefix.Parameters[2].Name);
            Assert.True(prefix.Parameters[2].IsOut);

            Assert.Equal("System.Exception", finalizer.ReturnType.FullName);
            Assert.Equal("__exception", finalizer.Parameters[0].Name);
            Assert.Equal("__state", finalizer.Parameters[1].Name);
        }

        [Fact]
        public void Finalizer_CommitsThenWarmsThenLogs_AndReturnsOriginalException()
        {
            using var assembly = LoadModAssembly();
            var finalizer = SingleMethod(HooksType(assembly.MainModule), "LoadStarBinariesFinalizer");
            var calls = CallsOf(finalizer);

            int complete = calls.FindIndex(c =>
                c.Reference.DeclaringType.FullName == "KSARS.Visuals.StarCatalog"
                && c.Reference.Name == "Complete");
            int warm = calls.FindIndex(c =>
                c.Reference.DeclaringType.FullName == "KSARS.Visuals.StarSpectralCache"
                && c.Reference.Name == "TryGet");
            int log = calls.FindIndex(c =>
                c.Reference.DeclaringType.FullName == "System.Console"
                && c.Reference.Name == "WriteLine");

            Assert.True(complete >= 0, "finalizer never calls StarCatalog::Complete");
            Assert.True(warm > complete, "cache warm must follow a successful commit");
            Assert.True(log > warm, "status summary must be logged after cache warming");

            Assert.DoesNotContain(finalizer.Body.Instructions,
                i => i.OpCode == OpCodes.Throw || i.OpCode == OpCodes.Rethrow);
            var tail = finalizer.Body.Instructions.TakeLast(2).ToArray();
            Assert.Equal(OpCodes.Ret, tail[1].OpCode);
            // __exception may be spilled to a local across the EH region; the
            // returned slot's only store must load the original parameter.
            if (tail[0].Operand is VariableDefinition local)
            {
                var stores = finalizer.Body.Instructions
                    .Select((instruction, index) => (instruction, index))
                    .Where(x => x.instruction.Operand is VariableDefinition vd && vd == local
                        && (x.instruction.OpCode == OpCodes.Stloc
                            || x.instruction.OpCode == OpCodes.Stloc_S))
                    .ToList();
                var store = Assert.Single(stores);
                var feed = finalizer.Body.Instructions[store.index - 1];
                Assert.True(feed.OpCode == OpCodes.Ldarg_0
                        || (feed.Operand is ParameterDefinition p && p.Index == 0),
                    "returned local must be stored only from __exception");
            }
            else
            {
                Assert.True(tail[0].OpCode == OpCodes.Ldarg_0
                        || (tail[0].Operand is ParameterDefinition p && p.Index == 0),
                    "finalizer must return the original __exception unchanged");
            }
        }

        [Fact]
        public void CatalogInstall_IsOptInUnderAtlasProbeGate()
        {
            using var assembly = LoadModAssembly();
            var hooks = assembly.MainModule.GetType("KSARS.Visuals.RenderHooks");
            var tryInstall = SingleMethod(hooks, "TryInstall");
            var instructions = tryInstall.Body.Instructions;

            int catalogCall = instructions.ToList().FindIndex(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "KSARS.Visuals.StarCatalogHooks"
                && mr.Name == "TryInstall");
            Assert.True(catalogCall >= 0, "RenderHooks::TryInstall never invokes StarCatalogHooks::TryInstall");

            int envCall = instructions.ToList().FindIndex(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference mr
                && mr.Name == "GetEnvironmentVariable");
            Assert.True(envCall >= 0 && envCall < catalogCall,
                "catalog install must follow the KSARS_ATLAS_PROBE opt-in gate");
            Assert.Contains(instructions.Take(envCall),
                i => i.OpCode == OpCodes.Ldstr && (string?)i.Operand == "KSARS_ATLAS_PROBE");
        }

        [Fact]
        public void Rollback_IsScopedToLoaderMethodAndVisualsOwner()
        {
            using var assembly = LoadModAssembly();
            var hooks = HooksType(assembly.MainModule);
            var methods = hooks.Methods.Where(m => m.HasBody).ToList();

            var unpatchCalls = methods
                .SelectMany(m => m.Body.Instructions
                    .Select((instruction, index) => (method: m, index, instruction)))
                .Where(x => x.instruction.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "HarmonyLib.Harmony"
                    && mr.Name == "Unpatch")
                .ToList();
            Assert.NotEmpty(unpatchCalls);
            foreach (var call in unpatchCalls)
            {
                var mr = (MethodReference)call.instruction.Operand;
                Assert.Equal(3, mr.Parameters.Count);
                var previous = call.method.Body.Instructions[call.index - 1];
                Assert.True(previous.OpCode == OpCodes.Ldstr
                        && (string?)previous.Operand == RenderHooks.OwnerId,
                    $"Unpatch in {call.method.Name} is not scoped to the visuals owner");
            }

            Assert.DoesNotContain(methods.SelectMany(m => m.Body.Instructions),
                i => i.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "HarmonyLib.Harmony"
                    && mr.Name == "UnpatchAll");
        }

        [Fact]
        public void ResetPaths_ClearCatalogAndHookState()
        {
            using var assembly = LoadModAssembly();
            var hooks = assembly.MainModule.GetType("KSARS.Visuals.RenderHooks");

            // ResetState delegates to ResetCatalogStateIfLoaded, which guards the
            // StarCatalogHooks.ResetState call behind a dependency-resolution
            // catch so KSA-less hosts can run the reset path.
            var resetState = SingleMethod(hooks, "ResetState");
            Assert.Contains(CallsOf(resetState),
                c => c.Reference.DeclaringType.FullName == "KSARS.Visuals.RenderHooks"
                    && c.Reference.Name == "ResetCatalogStateIfLoaded");

            var resetCatalog = SingleMethod(hooks, "ResetCatalogStateIfLoaded");
            Assert.Contains(CallsOf(resetCatalog),
                c => c.Reference.DeclaringType.FullName == "KSARS.Visuals.StarCatalogHooks"
                    && c.Reference.Name == "ResetState");

            var uninstall = SingleMethod(hooks, "Uninstall");
            Assert.Contains(CallsOf(uninstall),
                c => c.Reference.DeclaringType.FullName == "KSARS.Visuals.RenderHooks"
                    && c.Reference.Name == "ResetState");

            var catalogHooks = HooksType(assembly.MainModule);
            var catalogReset = SingleMethod(catalogHooks, "ResetState");
            Assert.Contains(CallsOf(catalogReset),
                c => c.Reference.DeclaringType.FullName == "KSARS.Visuals.StarCatalog"
                    && c.Reference.Name == "Reset");
        }

        [Fact]
        public void Transpiler_PreservesLabelsAndExceptionBlocks()
        {
            using var assembly = LoadModAssembly();
            var transpiler = SingleMethod(HooksType(assembly.MainModule), "TranspileLoadStarBinaries");
            var instructions = transpiler.Body.Instructions;

            var fieldLoads = instructions
                .Where(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();
            Assert.Contains("labels", fieldLoads);
            Assert.Contains("blocks", fieldLoads);

            int addRangeCount = instructions.Count(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference mr && mr.Name == "AddRange");
            Assert.True(addRangeCount >= 2, "transpiler must copy labels and exception blocks");

            Assert.Contains(instructions, i => i.OpCode == OpCodes.Newobj
                && i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "System.InvalidOperationException");
        }

        [Fact]
        public void ModAssembly_EmbedsThermalTableAndNotices()
        {
            var module = typeof(RenderHooks).Assembly;
            var names = module.GetManifestResourceNames();
            Assert.Contains("KSARS.ThermalTable.v2.bin", names);
            Assert.Contains("KSARS.SpectralData.LICENSE.txt", names);
            Assert.Contains("KSARS.SpectralData.SOURCE.md", names);
            Assert.Contains("KSARS.SpectralData.TABLES.md", names);

            using Stream table = module.GetManifestResourceStream("KSARS.ThermalTable.v2.bin")!;
            Assert.Equal(32904, table.Length);
            Assert.Equal(ThermalSpectrumTable.ArtifactSha256, Convert.ToHexString(SHA256.HashData(table)));

            foreach (string notice in new[]
            {
                "KSARS.SpectralData.LICENSE.txt",
                "KSARS.SpectralData.SOURCE.md",
                "KSARS.SpectralData.TABLES.md",
            })
            {
                using Stream stream = module.GetManifestResourceStream(notice)!;
                Assert.True(stream.Length > 0, $"{notice} embedded resource is empty");
            }
        }

        [RendererBaselineFact]
        public void InstalledLoader_HasExpectedSignatureAndAnchors()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var mod = assembly.MainModule.GetType("KSA.Mod");
            Assert.NotNull(mod);

            var loader = mod.Methods.Single(m => m.Name == "LoadStarBinaries");
            Assert.False(loader.IsStatic);
            Assert.Equal("System.Void", loader.ReturnType.FullName);
            var techniqueParameter = Assert.Single(loader.Parameters);
            Assert.Equal("InstancedStarTechnique", techniqueParameter.ParameterType.Name);

            var technique = techniqueParameter.ParameterType.Resolve();
            Assert.NotNull(technique);
            var addStar = technique.Methods.Single(m => m.Name == "AddStar");
            Assert.False(addStar.IsStatic);
            Assert.Equal("System.Void", addStar.ReturnType.FullName);
            Assert.Equal(
                new[]
                {
                    "Brutal.Numerics.float3", "System.Single",
                    "System.Byte", "Brutal.Numerics.byte4",
                },
                addStar.Parameters.Select(p => p.ParameterType.FullName).ToArray());

            var calls = CallsOf(loader);
            Assert.Equal(1, calls.Count(c =>
                c.Reference.Name == "AddStar"
                && c.Reference.DeclaringType.FullName == techniqueParameter.ParameterType.FullName));
        }

        [RendererBaselineFact]
        public void InstalledByte4_IsFourSequentialByteFields()
        {
            string numericsPath = Path.Combine(
                Path.GetDirectoryName(BaselineContext.KsaAssemblyPath)!, "Brutal.Numerics.dll");
            using var assembly = AssemblyDefinition.ReadAssembly(numericsPath);
            var byte4 = assembly.MainModule.GetType("Brutal.Numerics.byte4");
            Assert.NotNull(byte4);
            Assert.True(byte4.IsValueType);

            var fields = byte4.Fields.Where(f => !f.IsStatic).ToList();
            Assert.Equal(4, fields.Count);
            Assert.All(fields, f => Assert.Equal("System.Byte", f.FieldType.FullName));
            bool sequential = byte4.IsSequentialLayout || byte4.IsExplicitLayout;
            Assert.True(sequential,
                $"byte4 layout {byte4.Attributes} fields {string.Join(',', fields.Select(f => f.Name))}");
            if (byte4.IsExplicitLayout)
            {
                Assert.Equal(new int[] { 0, 1, 2, 3 }, fields.Select(f => (int)f.Offset).OrderBy(o => o).ToArray());
            }
        }

        [RendererBaselineFact]
        public void TryInstall_OnInstalledBinary_PatchesLoaderUnderVisualsOwnerAndRollsBack()
        {
            var ksa = Assembly.LoadFrom(BaselineContext.KsaAssemblyPath);
            var loader = ksa.GetType("KSA.Mod")!
                .GetMethod("LoadStarBinaries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            var harmony = new Harmony(RenderHooks.OwnerId);
            try
            {
                Assert.True(StarCatalogHooks.TryInstall(harmony), StarCatalogHooks.Status);
                Assert.True(StarCatalogHooks.Installed);
                Assert.Contains("raw", StarCatalogHooks.Status);
                Assert.Contains("table v2", StarCatalogHooks.Status);
                Assert.Contains("spectral cache", StarCatalogHooks.Status);

                Patches info = Harmony.GetPatchInfo(loader)!;
                Assert.NotNull(info);
                Assert.Contains(RenderHooks.OwnerId, info.Owners);
                Assert.Single(info.Prefixes);
                Assert.Single(info.Finalizers);
                Assert.Single(info.Transpilers);

                Assert.True(StarCatalogHooks.TryInstall(harmony),
                    "second TryInstall must be idempotent while installed");
                Patches repeated = Harmony.GetPatchInfo(loader)!;
                Assert.Single(repeated.Prefixes);
                Assert.Single(repeated.Finalizers);
                Assert.Single(repeated.Transpilers);
            }
            finally
            {
                harmony.Unpatch(loader, HarmonyPatchType.All, RenderHooks.OwnerId);
                StarCatalogHooks.ResetState();
            }

            Assert.False(StarCatalogHooks.Installed);
            Assert.DoesNotContain(RenderHooks.OwnerId,
                Harmony.GetPatchInfo(loader)?.Owners ?? Enumerable.Empty<string>());
        }
    }
}

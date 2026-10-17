using System.Reflection;
using KSARS.Visuals;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    [Collection("VisualHookState")]
    public class RelativisticStarProbeTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");

        private static AssemblyDefinition LoadModAssembly() => AssemblyDefinition.ReadAssembly(ModAssemblyPath);

        private static TypeDefinition ModType(AssemblyDefinition assembly, string name)
        {
            var type = assembly.MainModule.GetType(name);
            Assert.NotNull(type);
            return type;
        }

        private static MethodDefinition SingleMethod(TypeDefinition type, string name) =>
            type.Methods.Single(m => m.Name == name);

        private static List<(int Index, MethodReference Reference)> CallsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Select((instruction, index) => (instruction, index))
                .Where(x => (x.instruction.OpCode == OpCodes.Call || x.instruction.OpCode == OpCodes.Callvirt)
                    && x.instruction.Operand is MethodReference)
                .Select(x => (x.index, (MethodReference)x.instruction.Operand))
                .ToList();

        private static StarSpectralCache NewCache() =>
            new(ThermalSpectrumTable.LoadEmbedded());

        private static StarCatalog.Entry Entry(double x, double y, double z, byte scale, uint rgb) =>
            new(new OpticalVector(x, y, z), scale, rgb);

        [Fact]
        public void Build_PacksDirectionColorAndModeledFlux()
        {
            // Catalog entries carry unit directions (normalized at capture);
            // the builder trusts that invariant and packs them verbatim.
            var entries = new[] { Entry(1.0, 0.0, 0.0, 128, 0x8080ffu), Entry(0.0, 1.0, 0.0, 64, 0xff8000u) };
            var records = StarGpuBuilder.Build(entries, NewCache(), out _);

            // Native byte4 memory order: R occupies the lowest byte of the packed
            // color, so 0x8080ff is R=255, G=128, B=128.
            Assert.Equal(2, records.Length);
            Assert.Equal(1.0f, records[0].DirectionX, 6);
            Assert.Equal(0.0f, records[0].DirectionY, 6);
            Assert.Equal(255.0f / 255.0f, records[0].ColorR, 6);
            Assert.Equal(128.0f / 255.0f, records[0].ColorG, 6);
            Assert.Equal(128.0f / 255.0f, records[0].ColorB, 6);
            Assert.Equal((float)StarFluxReference.Integrate(128), records[0].Flux);
            Assert.Equal(1.0f, records[1].DirectionY, 6);
            Assert.Equal((float)StarFluxReference.Integrate(64), records[1].Flux);
        }

        [Fact]
        public void Build_FittedColor_RecordsPositiveTemperatureAndModelThermalRest()
        {
            var cache = NewCache();
            var entries = new[] { Entry(0.0, 0.0, 1.0, 200, 0xfff4e8u) };
            var records = StarGpuBuilder.Build(entries, cache, out int fitted);

            Assert.Equal(1, fitted);
            Assert.True(cache.TryGet(0xfff4e8u, out StellarSpectrumModel? model));
            Assert.NotNull(model);
            Assert.Equal((float)model!.Fit.TemperatureKelvin, records[0].Temperature);
            Assert.True(records[0].Temperature > 0.0f);
            Assert.Equal((float)model.ThermalRest.X, records[0].ThermalX);
            Assert.Equal((float)model.ThermalRest.Y, records[0].ThermalY);
            Assert.Equal((float)model.ThermalRest.Z, records[0].ThermalZ);
            Assert.Equal((float)model.ThermalRest.LogLuminance, records[0].LogRestLuminance);
        }

        [Fact]
        public void Build_UnfittedColor_GetsNonPositiveTemperatureAndRawColor()
        {
            var records = StarGpuBuilder.Build(
                new[] { Entry(1.0, 0.0, 0.0, 255, 0x000000u) }, NewCache(), out int fitted);

            Assert.Equal(0, fitted);
            var record = Assert.Single(records);
            Assert.True(record.Temperature <= 0.0f);
            Assert.Equal(0.0f, record.ColorR);
            Assert.Equal(0.0f, record.ColorG);
            Assert.Equal(0.0f, record.ColorB);
        }

        [Fact]
        public void Build_ZeroScale_KeepsZeroFluxPrior()
        {
            var records = StarGpuBuilder.Build(
                new[] { Entry(1.0, 0.0, 0.0, 0, 0xffffffu) }, NewCache(), out _);
            Assert.Equal(0.0f, Assert.Single(records).Flux);
        }

        [Fact]
        public void CatalogRevision_BumpsOnlyOnCommittedPublication()
        {
            StarCatalog.Reset();
            int initial = StarCatalog.Revision;
            var technique = new object();
            var source = new object();

            var aborted = StarCatalog.Begin(source, technique)!;
            aborted.Add(new OpticalVector(1.0, 0.0, 0.0), 5, 0x111111u);
            Assert.False(StarCatalog.Complete(aborted, success: false));
            Assert.Equal(initial, StarCatalog.Revision);

            var invalid = StarCatalog.Begin(source, technique)!;
            invalid.Add(OpticalVector.Zero, 5, 0x111111u);
            Assert.False(StarCatalog.Complete(invalid, success: true));
            Assert.Equal(initial, StarCatalog.Revision);

            var scope = StarCatalog.Begin(source, technique)!;
            scope.Add(new OpticalVector(1.0, 0.0, 0.0), 5, 0x111111u);
            Assert.True(StarCatalog.Complete(scope, success: true));
            Assert.Equal(initial + 1, StarCatalog.Revision);

            var empty = StarCatalog.Begin(source, technique)!;
            Assert.True(StarCatalog.Complete(empty, success: true));
            Assert.Equal(initial + 2, StarCatalog.Revision);

            StarCatalog.Reset();
        }

        [Fact]
        public void Prefix_HasHarmonyCompatibleSignature()
        {
            using var assembly = LoadModAssembly();
            var renderer = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            var prefix = SingleMethod(renderer, "StarRenderPrefix");

            Assert.Equal("System.Boolean", prefix.ReturnType.FullName);
            Assert.Equal(
                new[] { "System.Object", "Brutal.VulkanApi.CommandBuffer", "KSA.IViewport", "System.Int32" },
                prefix.Parameters.Select(p => p.ParameterType.FullName).ToArray());
        }

        [Fact]
        public void Prefix_SuppressesNativeDrawOnlyAfterSuccessfulOwnedRecord()
        {
            using var assembly = LoadModAssembly();
            var renderer = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            var prefix = SingleMethod(renderer, "StarRenderPrefix");
            var calls = CallsOf(prefix);

            var recordIndex = Assert.Single(calls, c =>
                c.Reference.DeclaringType.FullName == "KSARS.Visuals.RelativisticStarPass"
                && c.Reference.Name == "Record").Index;

            // Every gate must precede the owned Record call: sync-slot-safe
            // observer capture, catalog sync, and the main-viewport identity
            // check all run before the only native-suppressing path.
            var captureIndex = Assert.Single(calls, c => c.Reference.Name == "TryCaptureEffectiveObserver").Index;
            var syncIndex = Assert.Single(calls, c => c.Reference.Name == "SyncCatalog").Index;
            var viewportIndex = Assert.Single(calls, c => c.Reference.DeclaringType.FullName == "KSA.Program"
                && c.Reference.Name == "get_MainViewport").Index;
            Assert.True(viewportIndex < captureIndex
                && captureIndex < syncIndex
                && syncIndex < recordIndex);
            Assert.DoesNotContain(calls, c =>
                c.Reference.DeclaringType.FullName == "KSA.InstancedStarTechnique");
            Assert.DoesNotContain(calls, c => c.Reference.Name == "AddInstance");
        }

        [Fact]
        public void Install_ValidatesNativeRenderSignatureBeforePatching()
        {
            using var assembly = LoadModAssembly();
            var renderer = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            var install = SingleMethod(renderer, "TryInstallStarProbe");
            var calls = CallsOf(install);

            Assert.Contains(calls, c => c.Reference.Name == "GetMethod");
            Assert.Contains(calls, c => c.Reference.DeclaringType.FullName.StartsWith("HarmonyLib.Harmony")
                && c.Reference.Name == "Patch");
            var ldstr = install.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldstr)
                .Select(i => (string?)i.Operand)
                .ToList();
            Assert.Contains(ldstr, s => s == "Render");
            Assert.Contains(ldstr, s => s == "LoadStarBinaries");
        }

        [Fact]
        public void InstallGate_RequiresStarProbeEnvironmentVariable()
        {
            using var assembly = LoadModAssembly();
            var hooks = ModType(assembly, "KSARS.Visuals.RenderHooks");
            var tryInstall = SingleMethod(hooks, "TryInstall");
            var instructions = tryInstall.Body.Instructions;

            int starGateIndex = instructions
                .Select((instruction, index) => (instruction, index))
                .Where(x => x.instruction.OpCode == OpCodes.Ldstr
                    && (string?)x.instruction.Operand == "KSARS_STAR_PROBE")
                .Select(x => x.index)
                .Single();

            var calls = CallsOf(tryInstall);
            Assert.Contains(calls, c => c.Index > starGateIndex
                && c.Reference.DeclaringType.FullName == "KSARS.Visuals.RelativisticSpaceRenderer"
                && c.Reference.Name == "TryInstallStarProbe");
        }

        [Fact]
        public void Pass_UsesNativeStarPipelinePresetsAndOwnedDescriptors()
        {
            using var assembly = LoadModAssembly();
            var pass = ModType(assembly, "KSARS.Visuals.RelativisticStarPass");

            var buildPipeline = SingleMethod(pass, "BuildPipeline");
            var fieldLoads = buildPipeline.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).FullName)
                .ToList();
            Assert.Contains(fieldLoads, f =>
                f.Contains("RenderingPresets/ReverseZDepthStencil") && f.Contains("DepthTestNoWrite"));
            Assert.Contains(fieldLoads, f =>
                f.Contains("RenderingPresets/BlendState") && f.Contains("BlendAdditive"));

            var ctor = pass.Methods.Single(m => m.IsConstructor && m.Parameters.Count == 3);
            var storageBindings = ctor.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldc_I4_7)
                .Count();
            Assert.True(storageBindings >= 3,
                "two SSBO bindings plus storage pool size expected (VkDescriptorType.StorageBuffer=7)");

            var record = SingleMethod(pass, "Record");
            var recordCalls = CallsOf(record);
            Assert.Contains(recordCalls, c => c.Reference.DeclaringType.FullName == "KSA.Program"
                && c.Reference.Name == "SetViewport");
            Assert.Contains(recordCalls, c =>
                c.Reference.DeclaringType.FullName == "KSA.GlobalShaderBindings"
                && c.Reference.Name == "DescriptorSet");
            var drawIndex = recordCalls.FindIndex(c => c.Reference.Name == "Draw"
                && c.Reference.DeclaringType.FullName.Contains("VkDeviceExtensions"));
            Assert.True(drawIndex >= 0);
            Assert.Contains(record.Body.Instructions.Take(recordCalls[drawIndex].Index),
                i => i.OpCode == OpCodes.Ldc_I4_6);
        }

        [Fact]
        public void Shaders_DeclareOwnedSsboContractAndNativeProfile()
        {
            Assert.Contains("set=1,binding=0", RelativisticStarShaders.Vertex);
            Assert.Contains("set=1,binding=1", RelativisticStarShaders.Vertex);
            Assert.Contains("readonly buffer ThermalTable", RelativisticStarShaders.Vertex);
            Assert.Contains("readonly buffer Catalog", RelativisticStarShaders.Vertex);
            Assert.Contains("gl_InstanceIndex", RelativisticStarShaders.Vertex);
            Assert.Contains("push_constant", RelativisticStarShaders.Vertex);
            Assert.Contains("1e8", RelativisticStarShaders.Vertex);
            // Stock-shaped sprite contract: scale byte drives quad size and
            // profile; real projected depth at the native 19.5-unit distance
            // plus the native +1.0 clip-z bias that overlays background depth.
            Assert.Contains("vec4 aux", RelativisticStarShaders.Vertex);
            Assert.Contains("19.5", RelativisticStarShaders.Vertex);
            Assert.Contains("/ 78.0", RelativisticStarShaders.Vertex);
            Assert.Contains("clip.z+1.0", RelativisticStarShaders.Vertex);
            Assert.Contains("+2.0*logD", RelativisticStarShaders.Vertex);
            Assert.Contains("smoothstep", RelativisticStarShaders.Fragment);
            Assert.Contains("65504", RelativisticStarShaders.Fragment);
            Assert.Contains("discard", RelativisticStarShaders.Fragment);
        }
    }
}

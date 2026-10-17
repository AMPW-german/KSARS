using System.Reflection;
using KSARS.Visuals;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    [Collection("VisualHookState")]
    public class RelativisticMilkyWayProbeTests
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

        [RendererBaselineFact]
        public void AdaptedSources_KeepNativeContractsAndInjectKsarsAnchors()
        {
            string shaderRoot = SolarGlowShaderSources.ResolveShaderRoot(
                BaselineContext.KsaAssemblyPath);
            MilkyWayShaderSources sources = MilkyWayShaderSources.Load(shaderRoot);

            // Injected anchors.
            Assert.Contains("layout(push_constant) uniform KsarsObserver { vec4 uq; }", sources.Fragment);
            Assert.Contains("set = 2, binding = 0", sources.Fragment);
            Assert.Contains("float ksarsDoppler;", sources.Fragment);
            Assert.Contains("sampleDir = ksarsRestDirection(sampleDir, ksarsDoppler);", sources.Fragment);
            Assert.Contains("outColor = vec4(ksarsRelativisticBackground(vec3(0.0), ksarsDoppler), 1.0);",
                sources.Fragment);
            Assert.Contains("result = ksarsRelativisticBackground(result, ksarsDoppler);", sources.Fragment);
            Assert.Contains("vec4 ksarsSpectrum(float temperature)", sources.Fragment);
            Assert.Contains("vec3 ksarsCmb(float doppler)", sources.Fragment);
            Assert.Contains("KSARS_CMB_REST * doppler", sources.Fragment);
            Assert.Contains("KSARS_MW_TEFF * doppler", sources.Fragment);
            Assert.Contains("exp(shiftS.w - restS.w)", sources.Fragment);
            Assert.Contains("u * (q * q * B - A) + nPerp * (2.0 * q)", sources.Fragment);
            Assert.Contains("(2.0 * q) / (A + q * q * B)", sources.Fragment);
            // Rest-state identity: q == 1 returns the stock radiance unchanged.
            Assert.Contains("if (doppler == 1.0) return rest;", sources.Fragment);
            Assert.Contains("if (q == 1.0) return nObs;", sources.Fragment);

            // Native contract preserved: world-direction reconstruction,
            // galactic transform, cubemap + noise sampling, cloud machinery,
            // luminance/latitude masks, brightness/contrast, spec constant.
            Assert.Contains("vec3 sampleDir = getWorldDirection(inUV,", sources.Fragment);
            Assert.Contains("global.camera.galacticPlane * vec4(sampleDir, 0.0)", sources.Fragment);
            Assert.Contains("vec3 texCol = textureLod(milkyWay, sampleDir, 0.0).rgb;", sources.Fragment);
            Assert.Contains("float cloudNoise(vec2 uv, float seed, float cloudScale)", sources.Fragment);
            Assert.Contains("constant_id = 0", sources.Fragment);
            Assert.Contains("smoothstep(LUM_THRESH_MIN, LUM_THRESH_MAX, texLum)", sources.Fragment);
            Assert.Contains("smoothstep(BAND_HEIGHT, 0.0, absBandY)", sources.Fragment);
            Assert.Contains("pow(result, vec3(CONTRAST))", sources.Fragment);
            Assert.Contains("set = 1, binding = 0", sources.Fragment);
            Assert.Contains("set = 1, binding = 1", sources.Fragment);
        }

        [RendererBaselineFact]
        public void AdaptedSources_RemainStrictWhenSeamsMissingOrDuplicated()
        {
            string shaderRoot = SolarGlowShaderSources.ResolveShaderRoot(
                BaselineContext.KsaAssemblyPath);
            string missing = Path.Combine(Path.GetTempPath(), $"ksars-mw-src-{Guid.NewGuid():N}");
            try
            {
                Assert.Throws<InvalidOperationException>(() => MilkyWayShaderSources.Load(missing));

                // The adapter expands includes, so the full tree must be present.
                foreach (string file in Directory.GetFiles(shaderRoot, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(shaderRoot, file);
                    string destination = Path.Combine(missing, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }

                string fragPath = Path.Combine(missing, "MilkyWay.frag");
                string frag = File.ReadAllText(fragPath).Replace("\r\n", "\n");
                frag = frag.Replace("    outColor = vec4(result, 1.0);",
                    "    outColor = vec4(result, 1.0);\n    outColor = vec4(result, 1.0);");
                File.WriteAllText(fragPath, frag);
                Assert.Throws<InvalidOperationException>(() => MilkyWayShaderSources.Load(missing));
            }
            finally
            {
                Directory.Delete(missing, recursive: true);
            }
        }

        [Fact]
        public void Install_PatchesRenderMilkyWay_AndValidatesSeamFields()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition install = SingleMethod(type, "TryInstallMilkyWayProbe");
            var calls = CallsOf(install);
            Assert.Contains(calls, c => c.Reference.Name == "Patch");
            Assert.Contains(calls, c => c.Reference.Name == "GetField" || c.Reference.Name == "GetMethod");
            // The reflection contract resolves the native renderer's texture
            // set fields so the owned pass binds the same cubemap + noise.
            Assert.Contains(install.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "_milkyWayRenderer");
            Assert.Contains(install.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "RenderMilkyWay");
            Assert.Contains(install.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "_textureSet");
            Assert.Contains(install.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "_textureSetLayout");
        }

        [Fact]
        public void Prefix_SuppressesNativeOnlyOnFullyOwnedRecord()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition prefix = SingleMethod(type, "MilkyWayRenderPrefix");
            Assert.True(prefix.ReturnType.Name == "Boolean");
            Assert.Equal(4, prefix.Parameters.Count);

            var calls = CallsOf(prefix);
            // Must consult the main-viewport identity and the observer capture.
            Assert.Contains(calls, c => c.Reference.Name == "get_MainViewport");
            Assert.Contains(calls, c => c.Reference.Name == "TryCaptureEffectiveObserver");
            Assert.Contains(calls, c => c.Reference.Name == "Record");
            Assert.Contains(calls, c => c.Reference.Name == "TryGetMilkyWayTextureSet");
            // Fail paths return true (native draw retained); only the fully
            // recorded call returns false.
            Assert.Contains(prefix.Body.Instructions,
                i => i.OpCode == OpCodes.Ldc_I4_0);
            Assert.True(prefix.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldc_I4_1) > 1);
        }

        [Fact]
        public void PassRecord_MatchesNativeFullscreenContract()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticMilkyWayPass");

            MethodDefinition record = SingleMethod(type, "Record");
            var calls = CallsOf(record);
            Assert.Contains(calls, c => c.Reference.DeclaringType.FullName == "KSA.Program"
                && c.Reference.Name == "SetViewport");
            Assert.Contains(calls, c => c.Reference.DeclaringType.FullName == "KSA.GlobalShaderBindings"
                && c.Reference.Name == "DescriptorSet");
            Assert.Contains(calls, c => c.Reference.Name == "BindDescriptorSets");
            // Draw(4,1,0,0): native fullscreen strip.
            int drawIndex = calls.FindIndex(c => c.Reference.Name == "Draw"
                && c.Reference.DeclaringType.FullName.Contains("VkDeviceExtensions"));
            Assert.True(drawIndex >= 0);
            Assert.Contains(record.Body.Instructions.Take(calls[drawIndex].Index),
                i => i.OpCode == OpCodes.Ldc_I4_4);
            // Push constants carry the observer for the FRAGMENT stage.
            int pushIndex = calls.FindIndex(c => c.Reference.Name == "PushConstants");
            Assert.True(pushIndex >= 0);
            Assert.Contains(record.Body.Instructions.Take(calls[pushIndex].Index),
                i => (i.OpCode == OpCodes.Ldc_I4 || i.OpCode == OpCodes.Ldc_I4_S)
                    && Convert.ToInt32(i.Operand) == 0x10); // FragmentBit
        }

        [Fact]
        public void PassCtor_MatchesNativePipelineContract()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticMilkyWayPass");
            MethodDefinition ctor = type.Methods.Single(m => m.Name == ".ctor");
            var calls = CallsOf(ctor);
            // Compiles the adapted installed fragment + shared fullscreen vertex.
            Assert.Contains(calls, c => c.Reference.Name == "LoadInstalled");
            Assert.Contains(calls, c => c.Reference.Name == "CreateBuffer");
            Assert.Contains(calls, c => c.Reference.Name == "CreatePipelineLayout");
            Assert.Contains(calls, c => c.Reference.Name == "CreateDescriptorSetLayout");
            Assert.Contains(calls, c => c.Reference.Name == "WriteBufferDescriptor");

            MethodDefinition buildPipeline = SingleMethod(type, "BuildPipeline");
            var buildCalls = CallsOf(buildPipeline);
            Assert.Contains(buildCalls, c => c.Reference.Name == "GetMilkyWayQuality");
            Assert.Contains(buildCalls, c => c.Reference.Name == "CreateGraphicsPipeline");
            Assert.Contains(buildCalls, c => c.Reference.Name == "SetupGraphicsPipeline");
            // TriangleStrip topology + additive blend + reverse-Z no-write.
            var fieldLoads = buildPipeline.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).FullName)
                .ToList();
            Assert.Contains(fieldLoads, f => f.Contains("InputAssembly") && f.Contains("TriangleStrip"));
            Assert.Contains(fieldLoads, f =>
                f.Contains("RenderingPresets/ReverseZDepthStencil") && f.Contains("DepthTestNoWrite"));
            Assert.Contains(fieldLoads, f =>
                f.Contains("RenderingPresets/BlendState") && f.Contains("BlendAdditive"));

            MethodDefinition write = SingleMethod(type, "WriteBufferDescriptor");
            Assert.Contains(CallsOf(write), c => c.Reference.Name == "UpdateDescriptorSets");
        }

        [Fact]
        public void RenderHooks_GatesInstallOnMilkyWayEnvVar_AndResetsChain()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition hooks = ModType(assembly, "KSARS.Visuals.RenderHooks");
            MethodDefinition install = SingleMethod(hooks, "TryInstall");
            Assert.Contains(install.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "KSARS_MILKYWAY_PROBE");
            var calls = CallsOf(install);
            Assert.Contains(calls, c => c.Reference.Name == "TryInstallMilkyWayProbe");

            MethodDefinition reset = hooks.Methods.Single(m => m.Name == "ResetState");
            Assert.Contains(CallsOf(reset), c => c.Reference.Name == "ResetMilkyWayProbeStateIfLoaded");
            MethodDefinition guarded = SingleMethod(hooks, "ResetMilkyWayProbeStateIfLoaded");
            Assert.Contains(CallsOf(guarded), c => c.Reference.Name == "ResetMilkyWayProbeState");
        }
    }
}

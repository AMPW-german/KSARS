using KSARS.Visuals;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class SolarGlowFlareTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");
        private const string PassType = "KSARS.Visuals.SolarGlowFlarePass";
        private const string RendererType = "KSARS.Visuals.RelativisticSpaceRenderer";
        private const string FrameType = "KSARS.Visuals.RelativisticSpaceRenderer/MainAtlasFrame";
        private const string SolarSlotType = "KSARS.Visuals.RelativisticSpaceRenderer/MainAtlasFrame/SolarSlot";

        private static List<(string DeclaringType, string Name)> CallsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
                    && i.Operand is MethodReference)
                .Select(i =>
                {
                    var mr = (MethodReference)i.Operand;
                    return (mr.DeclaringType.FullName, mr.Name);
                })
                .ToList();

        private static List<(string DeclaringType, string Name)> FieldRefsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Where(i => i.Operand is FieldReference)
                .Select(i =>
                {
                    var fr = (FieldReference)i.Operand;
                    return (fr.DeclaringType.FullName, fr.Name);
                })
                .ToList();

        private static TypeDefinition TypeOf(ModuleDefinition module, string name)
        {
            var type = module.GetType(name) ?? module.Types.SelectMany(AllNested).FirstOrDefault(t => t.FullName == name);
            Assert.NotNull(type);
            return type;
        }

        private static IEnumerable<TypeDefinition> AllNested(TypeDefinition type)
        {
            foreach (TypeDefinition nested in type.NestedTypes)
            {
                yield return nested;
                foreach (TypeDefinition deeper in AllNested(nested))
                {
                    yield return deeper;
                }
            }
        }

        private static MethodDefinition SingleMethod(ModuleDefinition module, string typeName, string methodName) =>
            TypeOf(module, typeName).Methods.Single(m => m.Name == methodName);

        private static int CountCalls(List<(string DeclaringType, string Name)> calls, string declaringType, string name) =>
            calls.Count(c => c.DeclaringType == declaringType && c.Name == name);

        private static int IndexOfCall(List<(string DeclaringType, string Name)> calls, string declaringType, string name) =>
            calls.FindIndex(c => c.DeclaringType == declaringType && c.Name == name);

        private static int? IntConstant(Instruction instruction)
        {
            if (instruction.OpCode == OpCodes.Ldc_I4_M1) return -1;
            if (instruction.OpCode == OpCodes.Ldc_I4_0) return 0;
            if (instruction.OpCode == OpCodes.Ldc_I4_1) return 1;
            if (instruction.OpCode == OpCodes.Ldc_I4_2) return 2;
            if (instruction.OpCode == OpCodes.Ldc_I4_3) return 3;
            if (instruction.OpCode == OpCodes.Ldc_I4_4) return 4;
            if (instruction.OpCode == OpCodes.Ldc_I4_5) return 5;
            if (instruction.OpCode == OpCodes.Ldc_I4_6) return 6;
            if (instruction.OpCode == OpCodes.Ldc_I4_7) return 7;
            if (instruction.OpCode == OpCodes.Ldc_I4_8) return 8;
            if (instruction.OpCode == OpCodes.Ldc_I4_S) return (sbyte)instruction.Operand;
            if (instruction.OpCode == OpCodes.Ldc_I4) return (int)instruction.Operand;
            return null;
        }

        private static TypeDefinition? TryTypeOf(ModuleDefinition module, string name) =>
            module.GetType(name) ?? module.Types.SelectMany(AllNested).FirstOrDefault(t => t.FullName == name);

        [Fact]
        public void SolarFrameToken_Matches_RequiresIdentitiesAndFrameEpoch()
        {
            object snapshot = new();
            object viewport = new();
            object camera = new();
            object sun = new();
            var token = new SolarFrameToken(snapshot, viewport, camera, sun, 3, 7);

            var same = new SolarFrameToken(snapshot, viewport, camera, sun, 3, 7);
            Assert.True(token.Matches(in same));
            Assert.True(token.Owns(in same, prepared: true, presented: true));
            Assert.True(token.CanPresent(in same, prepared: true, hasPending: true));

            var otherSnapshot = new SolarFrameToken(new object(), viewport, camera, sun, 3, 7);
            var otherViewport = new SolarFrameToken(snapshot, new object(), camera, sun, 3, 7);
            var otherCamera = new SolarFrameToken(snapshot, viewport, new object(), sun, 3, 7);
            var otherSun = new SolarFrameToken(snapshot, viewport, camera, new object(), 3, 7);
            var otherFrame = new SolarFrameToken(snapshot, viewport, camera, sun, 4, 7);
            var otherEpoch = new SolarFrameToken(snapshot, viewport, camera, sun, 3, 8);
            Assert.False(token.Matches(in otherSnapshot));
            Assert.False(token.Matches(in otherViewport));
            Assert.False(token.Matches(in otherCamera));
            Assert.False(token.Matches(in otherSun));
            Assert.False(token.Matches(in otherFrame));
            Assert.False(token.Matches(in otherEpoch));
        }

        [Fact]
        public void SolarFrameToken_Matches_RejectsNullsAndNegativeFrameEpoch()
        {
            object snapshot = new();
            object viewport = new();
            object camera = new();
            object sun = new();

            var token = new SolarFrameToken(snapshot, viewport, camera, sun, 3, 7);
            var same = new SolarFrameToken(snapshot, viewport, camera, sun, 3, 7);

            Assert.False(token.Owns(in same, prepared: false, presented: true));
            Assert.False(token.Owns(in same, prepared: true, presented: false));
            Assert.False(token.CanPresent(in same, prepared: false, hasPending: true));
            Assert.False(token.CanPresent(in same, prepared: true, hasPending: false));

            var negativeFrame = new SolarFrameToken(snapshot, viewport, camera, sun, -1, 7);
            var negativeEpoch = new SolarFrameToken(snapshot, viewport, camera, sun, 3, -1);
            Assert.False(negativeFrame.Matches(in same));
            Assert.False(negativeEpoch.Matches(in same));

            var nullIdentity = new SolarFrameToken(null, viewport, camera, sun, 3, 7);
            Assert.False(nullIdentity.Matches(in same));
            var nullSun = new SolarFrameToken(snapshot, viewport, camera, null, 3, 7);
            Assert.False(nullSun.Matches(in same));
        }

        [Fact]
        public void PassRecord_OrdersRenderingDispatchesAndBarriers()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = SingleMethod(assembly.MainModule, PassType, "Record");
            var calls = CallsOf(record);

            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "BeginRendering"));
            Assert.Equal(2, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "EndRendering"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "Draw"));
            Assert.Equal(2, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "Dispatch"));
            Assert.Equal(3, CountCalls(calls, "KSA.Rendering.BarrierBatch", "SubmitAndFlush"));
            Assert.Equal(3, CountCalls(calls,
                "Brutal.VulkanApi.Abstractions.CommandBufferExtensions", "PushConstants"));
            Assert.Equal(1, CountCalls(calls, "KSA.Program", "SetViewport"));

            int begin = IndexOfCall(calls, "Brutal.VulkanApi.VkDeviceExtensions", "BeginRendering");
            int draw = IndexOfCall(calls, "Brutal.VulkanApi.VkDeviceExtensions", "Draw");
            int end = IndexOfCall(calls, "Brutal.VulkanApi.VkDeviceExtensions", "EndRendering");
            int dispatch = IndexOfCall(calls, "Brutal.VulkanApi.VkDeviceExtensions", "Dispatch");
            Assert.True(begin >= 0 && draw > begin && end > draw && dispatch > end,
                "the glow draw must sit inside rendering, before the blur/merge dispatches");

            foreach (var instruction in record.Body.Instructions.Where(i =>
                         (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                         && i.Operand is MethodReference mr && mr.Name == "Dispatch"))
            {
                var mr = (MethodReference)instruction.Operand;
                Assert.Equal(4, mr.Parameters.Count);
                Assert.All(mr.Parameters.Skip(1),
                    p => Assert.Equal("System.Int32", p.ParameterType.FullName));
            }
        }

        [Fact]
        public void PassConstructor_BuildsOneGraphicsAndTwoComputePipelines()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var pass = TypeOf(assembly.MainModule, PassType);
            var calls = pass.Methods
                .Where(m => m.HasBody && m.Name is ".ctor" or "BuildGraphicsPipeline" or "CreateComputePipeline")
                .SelectMany(CallsOf)
                .ToList();

            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.Abstractions.DeviceExtensions", "CreateGraphicsPipeline"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "CreateComputePipelines"));
            Assert.Equal(4, CountCalls(calls, "KSARS.Visuals.ShaderCompileUtil", "CompileModule"));

            var ctorCalls = CallsOf(pass.Methods.Single(m => m.Name == ".ctor"));
            Assert.Equal(2, CountCalls(ctorCalls, PassType, "CreateComputePipeline"));
            Assert.Equal(1, CountCalls(ctorCalls, PassType, "BuildGraphicsPipeline"));
            Assert.Equal(1, CountCalls(ctorCalls, "KSARS.Visuals.AtlasMainPass", "get_AtlasSetLayout"));
            Assert.Equal(4, CountCalls(ctorCalls,
                "Brutal.VulkanApi.Abstractions.DescriptorPoolExExtensions", "AllocateDescriptorSet"));
        }

        [Fact]
        public void PassDispose_DestroysEveryOwnedVulkanHandle()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var pass = TypeOf(assembly.MainModule, PassType);
            var calls = pass.Methods
                .Where(m => m.HasBody && (m.Name == "Dispose" || m.Name.StartsWith("<Dispose>b__")))
                .SelectMany(CallsOf)
                .ToList();

            Assert.Equal(3, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "DestroyPipeline"));
            Assert.Equal(3, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "DestroyPipelineLayout"));
            Assert.Equal(4, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "DestroyShaderModule"));
            Assert.Equal(3, CountCalls(calls, "KSA.Rendering.RenderImage", "Dispose"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.Abstractions.MappedMemory", "Unmap"));
        }

        [Fact]
        public void PassPrepare_WritesDescriptorsAndRejectsForeignTargets()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule, PassType, "Prepare");
            var calls = CallsOf(prepare);
            var refs = FieldRefsOf(prepare);

            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.AtlasMainPass", "TryGetAtlasBinding"));
            Assert.Equal(9, CountCalls(calls, PassType, "WriteImageDescriptor"));
            Assert.Equal(1, CountCalls(calls, "KSA.Program", "get_MainViewport"));
            Assert.Equal(2, CountCalls(calls,
                "KSARS.Visuals.SolarGlowFlarePass/RendererTargetChangedException", ".ctor"));
            Assert.Contains(refs, f => f.Name == "_borrowedTarget");
        }

        [Fact]
        public void SunbloomSeam_RecordsOwnedGlowOnlyWhenReplacementPresentedOrCulled()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var wrapper = SingleMethod(assembly.MainModule, RendererType, "RenderSunbloomOrAtlas");
            var calls = CallsOf(wrapper);

            Assert.Equal(1, CountCalls(calls, "KSA.SunbloomRenderer", "Render"));
            Assert.Equal(1, CountCalls(calls, RendererType, "SolarReplacementPresented"));
            Assert.Equal(1, CountCalls(calls, RendererType, "SolarCulledOwned"));
            Assert.Equal(1, CountCalls(calls, RendererType, "TryRecordSolarGlow"));
            int presented = IndexOfCall(calls, RendererType, "SolarReplacementPresented");
            int culled = IndexOfCall(calls, RendererType, "SolarCulledOwned");
            int owned = IndexOfCall(calls, RendererType, "TryRecordSolarGlow");
            int native = IndexOfCall(calls, "KSA.SunbloomRenderer", "Render");
            Assert.True(presented >= 0 && culled > presented && owned > culled && native > owned,
                "the owned glow record must run only on the presented/culled paths and precede the native fallback");
        }

        [Fact]
        public void CulledSolar_SuppressesNativeSunQueueAndSunbloom()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);

            var prepare = SingleMethod(assembly.MainModule, RendererType, "TryPrepareMainSolarSource");
            Assert.Contains(prepare.Body.Instructions, i =>
                i.OpCode == OpCodes.Stfld && i.Operand is FieldReference fr
                && fr.DeclaringType.FullName == SolarSlotType && fr.Name == "Culled");

            var queue = SingleMethod(assembly.MainModule, RendererType, "QueueNativeSunOrAtlas");
            Assert.Contains(FieldRefsOf(queue),
                f => f.DeclaringType == SolarSlotType && f.Name == "Culled");

            var flush = SingleMethod(assembly.MainModule, RendererType, "FlushDeferredNativeSun");
            var flushCalls = CallsOf(flush);
            Assert.Contains(FieldRefsOf(flush),
                f => f.DeclaringType == SolarSlotType && f.Name == "Culled");
            Assert.Contains(flushCalls,
                c => c.DeclaringType.EndsWith("SolarFrameToken") && c.Name == "Matches");
            Assert.Contains(flushCalls,
                c => c.DeclaringType.EndsWith("SolarFrameToken") && c.Name == "Owns");

            var culled = SingleMethod(assembly.MainModule, RendererType, "SolarCulledOwned");
            Assert.Contains(CallsOf(culled),
                c => c.DeclaringType.EndsWith("SolarFrameToken") && c.Name == "Matches");
            Assert.DoesNotContain(CallsOf(culled),
                c => c.DeclaringType.EndsWith("SolarFrameToken") && c.Name == "Owns");
        }

        [Fact]
        public void CulledSolar_StillMarksFramePreparedWhenNoSourcesComposite()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);

            var record = SingleMethod(assembly.MainModule, RendererType, "PrepareMainFrameSources");
            var fields = FieldRefsOf(record);

            // The SourceCount==0 early return must consult Solar.Culled so culled frames
            // still reach frame.Prepared — otherwise the native Sun+Sunbloom leak back in.
            Assert.Contains(fields,
                f => f.DeclaringType == SolarSlotType && f.Name == "Culled");
            Assert.Contains(fields,
                f => f.DeclaringType == FrameType && f.Name == "SourceCount");
            Assert.Contains(fields,
                f => f.DeclaringType == FrameType && f.Name == "Prepared");
        }

        [Fact]
        public void TryRecordSolarGlow_ReadsNativeFlareUboAndOverwritesOnlyAnchors()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var glow = SingleMethod(assembly.MainModule, RendererType, "TryRecordSolarGlow");
            var calls = CallsOf(glow);
            var refs = FieldRefsOf(glow);

            Assert.Equal(1, CountCalls(calls, RendererType, "SolarGlareSources"));
            Assert.Equal(1, CountCalls(calls, RendererType, "TryReadNativeFlare"));
            Assert.Equal(1, CountCalls(calls, PassType, "Prepare"));
            Assert.Equal(1, CountCalls(calls, PassType, "Record"));
            int read = IndexOfCall(calls, RendererType, "TryReadNativeFlare");
            int prepare = IndexOfCall(calls, PassType, "Prepare");
            int record = IndexOfCall(calls, PassType, "Record");
            Assert.True(read >= 0 && prepare > read && record > prepare,
                "the native flare read-back must precede prepare, which must precede record");

            // Lens bloom integrates the clamped buffer: glareScale = min(D^3,65504)*D^-2 —
            // equals D unsaturated, decays as the disc collapses to a subpixel point. Raw D^3
            // or unbounded D both white out the screen at extreme beta. Both doppler reads
            // must precede Prepare since they feed the push constants.
            int srcDoppler = calls.FindIndex(c =>
                c.DeclaringType.EndsWith("SolarGlareProjection") && c.Name == "get_SourceDoppler");
            int d3 = calls.FindIndex(c =>
                c.DeclaringType.EndsWith("SolarGlareProjection") && c.Name == "get_BrightnessScale");
            Assert.True(srcDoppler >= 0 && srcDoppler < prepare,
                "glareScale must read SourceDoppler before the push is built");
            Assert.True(d3 >= 0 && d3 < prepare,
                "glareScale must read BrightnessScale (clamped D^3 budget) before the push is built");

            foreach (string field in new[] { "s_solarGlareCtorFailed", "s_solarGlareStatus" })
            {
                Assert.Contains(refs, f => f.DeclaringType == RendererType && f.Name == field);
            }

            var storedFields = glow.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference fr
                    && fr.DeclaringType.FullName == "KSA.SunFlareData")
                .Select(i => ((FieldReference)i.Operand).Name)
                .Distinct()
                .ToList();
            Assert.Equal(
                new[] { "ScreenspaceGlowRadius", "ScreenspaceSunPosition", "ScreenspaceSunRadius", "SunDepth", "SunDot" },
                storedFields.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void TryReadNativeFlare_UsesReflectedSunbloomFieldsAndDynamicOffset()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var read = SingleMethod(assembly.MainModule, RendererType, "TryReadNativeFlare");
            var calls = CallsOf(read);
            var refs = FieldRefsOf(read);

            foreach (string field in new[] { "s_sunflareMemoryField", "s_sunflareBuffersField", "s_sunflareFrameCountField" })
            {
                Assert.Contains(refs, f => f.DeclaringType == RendererType && f.Name == field);
            }

            Assert.True(CountCalls(calls, "System.Reflection.FieldInfo", "GetValue") >= 3);
            Assert.Equal(1, CountCalls(calls,
                "Brutal.VulkanApi.Abstractions.MappedMemory", "Offset"));
        }

        [Fact]
        public void PrepareProbe_StoresReflectedSunbloomFields()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule, RendererType, "PrepareProbe");
            var stores = prepare.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference fr
                    && fr.DeclaringType.FullName == RendererType)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();

            Assert.Contains("s_sunflareMemoryField", stores);
            Assert.Contains("s_sunflareBuffersField", stores);
            Assert.Contains("s_sunflareFrameCountField", stores);
        }

        [Fact]
        public void SolarSlot_CarriesOwnedGlowPassAndAnchorState()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var slot = TypeOf(assembly.MainModule, SolarSlotType);
            var names = slot.Fields.Select(f => f.Name).ToList();

            foreach (string field in new[] { "GlarePass", "GlareProjection", "GlareAnchored",
                     "GlareAnchorUv", "GlareAnchorDepth", "Culled" })
            {
                Assert.Contains(field, names);
            }
        }

        [Fact]
        public void MainRecordingUnbalanced_IncludesGlowPassRecovery()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var check = SingleMethod(assembly.MainModule, RendererType, "MainRecordingUnbalanced");
            var refs = FieldRefsOf(check);
            Assert.Contains(refs, f => f.DeclaringType == SolarSlotType && f.Name == "GlarePass");
        }

        [Fact]
        public void FrameDispose_RetiresGlowPassWithSolarRoots()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var dispose = SingleMethod(assembly.MainModule, FrameType, "Dispose");
            var refs = FieldRefsOf(dispose);
            Assert.Contains(refs, f => f.DeclaringType == SolarSlotType && f.Name == "GlarePass");
        }

        [Fact]
        public void PrepareSolarSource_ComputesGlareAnchorOnSuccess()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule, RendererType, "TryPrepareMainSolarSource");
            var calls = CallsOf(prepare);
            Assert.Equal(2, CountCalls(calls, RendererType, "ComputeSolarGlareAnchor"));

            var anchor = SingleMethod(assembly.MainModule, RendererType, "ComputeSolarGlareAnchor");
            var anchorCalls = CallsOf(anchor);
            Assert.Equal(1, CountCalls(anchorCalls, "KSARS.Visuals.SolarGlareProjection", "TryCreate"));
            Assert.True(CountCalls(anchorCalls, "Brutal.Numerics.float4", "Transform") >= 3,
                "the anchor must build the camera basis from InverseView and clip the transformed position via ViewProjection");
        }

        [Fact]
        public void VertexSource_IsSharedFullscreenTriangle()
        {
            Assert.Equal(AtlasCompositeProbe.VertexSource, SolarGlowShaderSources.VertexSource);
        }

        [Fact]
        public void GlarePushGpu_MatchesPushRangeByteCount()
        {
            Assert.Equal(64, SolarGlowShaderSources.GlarePushByteCount);

            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var push = TypeOf(assembly.MainModule, $"{PassType}/GlarePushGpu");
            Assert.True(push.IsSequentialLayout);
            Assert.Equal(
                new[] { "ObserverUq", "SolarRest", "Info", "GlareScale" },
                push.Fields.Select(f => f.Name).ToArray());
            Assert.All(push.Fields, f => Assert.StartsWith("Brutal.Numerics.", f.FieldType.FullName));
        }

        [RendererBaselineFact]
        public void AdaptedSources_KeepNativeContractsAndInjectKsarsAnchors()
        {
            string shaderRoot = SolarGlowShaderSources.ResolveShaderRoot(
                BaselineContext.KsaAssemblyPath);
            SolarGlowShaderSources sources = SolarGlowShaderSources.Load(shaderRoot);

            Assert.Contains("ksarsStockInsideSun", sources.FragmentSource);
            Assert.Contains("vec2 ndc = pixelUV * 2.;", sources.FragmentSource);
            Assert.DoesNotContain("pixelUV * 2. - 1.", sources.FragmentSource);
            Assert.Contains("ksarsParams.solarRest.w * 2.0", sources.FragmentSource);
            Assert.Contains("if (ksarsParams.info.z == 0)", sources.FragmentSource);
            Assert.Contains("ksarsVisibility(inUV, sunData.sunDepth)", sources.FragmentSource);
            Assert.Contains("(std140, set = 4, binding = 0) uniform KsarsAtlasBodies",
                sources.FragmentSource);
            Assert.Contains("set = 2, binding = 1", sources.FragmentSource);
            Assert.Contains("depthCull *= clamp(textureLod(mainColor, inUV, 0.0).a, 0.0, 1.0);",
                sources.FragmentSource);
            Assert.Contains("RaySphereIntersection", sources.FragmentSource);

            Assert.Contains("if (ksarsParams.info.z == 0) { return vec3(0.0); }", sources.BlurSource);
            Assert.Contains("UI_COVERAGE_MASK_SET 5", sources.BlurSource);
            Assert.DoesNotContain("set = 6", sources.BlurSource);
            Assert.DoesNotContain("AtmosphereDataUbo", sources.BlurSource);
            Assert.DoesNotContain("AtmosphereFunctions", sources.BlurSource);
            Assert.DoesNotContain("#define ATMOSPHERE_DATA_SET", sources.BlurSource);

            Assert.Contains("layout (push_constant) uniform KsarsGlareParams", sources.MergeSource);
            Assert.Contains("bloom.rgb * ksarsParams.glareScale.y", sources.MergeSource);
            Assert.Contains("65504.0", sources.MergeSource);
            Assert.DoesNotContain("* ksarsParams.glareScale", sources.BlurSource);
        }

        [RendererBaselineFact]
        public void AdaptedSources_RemainStrictWhenSeamsMissingOrDuplicated()
        {
            string shaderRoot = SolarGlowShaderSources.ResolveShaderRoot(
                BaselineContext.KsaAssemblyPath);
            string missing = Path.Combine(Path.GetTempPath(), $"ksars-glow-src-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(missing, "PostProcess"));
            try
            {
                Assert.Throws<InvalidOperationException>(() => SolarGlowShaderSources.Load(missing));

                foreach (string file in new[] { "sunbloom.frag", "sunbloom_blur.comp", "sunbloom_merge.comp" })
                {
                    File.Copy(Path.Combine(shaderRoot, "PostProcess", file),
                        Path.Combine(missing, "PostProcess", file));
                }

                string blurPath = Path.Combine(missing, "PostProcess", "sunbloom_blur.comp");
                string blur = File.ReadAllText(blurPath).Replace("\r\n", "\n");
                blur = blur.Replace(
                    "vec3 allBloomAndFlare(vec2 uv, vec2 sunPos, vec2 _) {",
                    "vec3 allBloomAndFlare(vec2 uv, vec2 sunPos, vec2 _) {\nvec3 allBloomAndFlare(vec2 uv, vec2 sunPos, vec2 _) {");
                File.WriteAllText(blurPath, blur);
                Assert.Throws<InvalidOperationException>(() => SolarGlowShaderSources.Load(missing));
            }
            finally
            {
                Directory.Delete(missing, recursive: true);
            }
        }

        [Fact]
        public void SolarGlareProjection_SunRadiusPixels_MatchesNativePixelScale()
        {
            // Native UpdateShaderData computes 0.5 * diameterPixels where
            // diameterPixels = (2R / d) * height / (2 * tan(fov/2)); the glare projection
            // must reproduce that pixel scale for the transformed cap radius.
            double distance = 147.5e9;
            double radius = 6.96342e8;
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, -distance),
                4.0 * radius, 512, 8, out AtlasProjection source));

            double fov = Math.PI / 3.0;
            int height = 1440;
            Assert.True(SolarGlareProjection.TryCreate(in source, radius, PresentationVelocity.Rest,
                new OpticalVector(0.0, 0.0, -1.0), new OpticalVector(1.0, 0.0, 0.0),
                new OpticalVector(0.0, 1.0, 0.0), fov, 3440, height,
                out SolarGlareProjection glare));

            double expectedRadius = 0.5 * height * (radius / distance) / Math.Tan(fov * 0.5);
            Assert.Equal(expectedRadius, glare.SunRadiusPixels, 6);
            Assert.Equal(Math.Max(50.0 * expectedRadius, 100.0), glare.GlowRadiusPixels, 6);
            Assert.True(glare.AnchorSupported);
            Assert.Equal(1.0, glare.SunDot, 6);
        }

        [Fact]
        public void PassPrepare_DeclaresImageLayoutsMatchingRecordedTransitions()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule, PassType, "Prepare");
            var instructions = prepare.Body.Instructions;

            var layouts = new List<int>();
            for (int i = 0; i + 1 < instructions.Count; i++)
            {
                if (instructions[i + 1].OpCode == OpCodes.Stfld
                    && instructions[i + 1].Operand is FieldReference fr
                    && fr.Name == "ImageLayout"
                    && fr.DeclaringType.Name == "VkDescriptorImageInfo")
                {
                    int? value = IntConstant(instructions[i]);
                    Assert.True(value.HasValue,
                        $"the ImageLayout store at IL_{instructions[i + 1].Offset:x4} must be fed by a constant");
                    layouts.Add(value.Value);
                }
            }

            // VkImageLayout ints pinned against the installed enum by
            // NativeImageBarrierPresets_SampledReadVfcTargetsShaderReadOnly.
            const int depthStencilReadOnly = 4;
            const int shaderReadOnly = 5;
            const int general = 1;
            Assert.Equal(
                new[]
                {
                    depthStencilReadOnly, // sceneDepth
                    shaderReadOnly,       // mainColor — Record transitions it via SampledReadVfc
                    general,              // blur storage image
                    shaderReadOnly,       // glow read
                    shaderReadOnly,       // occlusion read
                    general,              // scene storage image
                    shaderReadOnly,       // blur read
                },
                layouts.ToArray());
        }

        [Fact]
        public void PassRecord_TransitionsMainColorWithSampledReadInInitialBatch()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = SingleMethod(assembly.MainModule, PassType, "Record");
            var instructions = record.Body.Instructions;

            int firstFlush = instructions.ToList().FindIndex(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "KSA.Rendering.BarrierBatch" && mr.Name == "SubmitAndFlush");
            Assert.True(firstFlush >= 0, "Record must submit its initial barrier batch");

            // Pair every BarrierBatch.Add in the initial batch with the pass image
            // field and the ImageBarrierInfo preset loaded in its argument window.
            var pairs = new List<(string Image, string Preset)>();
            int windowStart = 0;
            for (int i = 0; i < firstFlush; i++)
            {
                bool isCall = instructions[i].OpCode == OpCodes.Call
                    || instructions[i].OpCode == OpCodes.Callvirt
                    || instructions[i].OpCode == OpCodes.Newobj;
                if (isCall && instructions[i].Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "KSA.Rendering.BarrierBatch" && mr.Name == "Add")
                {
                    var window = instructions.Skip(windowStart).Take(i - windowStart).ToList();
                    string image = window.LastOrDefault(w => w.OpCode == OpCodes.Ldfld
                            && w.Operand is FieldReference f && f.DeclaringType.FullName == PassType)
                        ?.Operand is FieldReference f1 ? f1.Name : "?";
                    string preset = window.LastOrDefault(w =>
                            (w.OpCode == OpCodes.Ldsfld || w.OpCode == OpCodes.Ldsflda)
                            && w.Operand is FieldReference f
                            && f.DeclaringType.FullName.Contains("ImageBarrierInfo"))
                        ?.Operand is FieldReference f2 ? f2.Name : "?";
                    pairs.Add((image, preset));
                }

                if (isCall)
                {
                    windowStart = i + 1;
                }
            }

            string detail = string.Join(", ", pairs.Select(p => $"{p.Image}->{p.Preset}"));
            Assert.Equal(1, pairs.Count(p => p.Image == "_mainColor"));
            Assert.True(pairs.Any(p => p.Image == "_mainColor" && p.Preset == "SampledReadVfc"),
                $"_mainColor must transition via SampledReadVfc in the initial batch; observed: {detail}");
        }

        [RendererBaselineFact]
        public void NativeImageBarrierPresets_SampledReadVfcTargetsShaderReadOnly()
        {
            string installDir = Path.GetDirectoryName(BaselineContext.KsaAssemblyPath)!;
            using var vulkan = AssemblyDefinition.ReadAssembly(
                Path.Combine(installDir, "Brutal.Vulkan.dll"));
            var layouts = vulkan.MainModule.GetType("Brutal.VulkanApi.VkImageLayout");
            Assert.NotNull(layouts);
            Assert.Equal(1, layouts.Fields.Single(f => f.Name == "General").Constant);
            Assert.Equal(4, layouts.Fields.Single(f => f.Name == "DepthStencilReadOnlyOptimal").Constant);
            Assert.Equal(5, layouts.Fields.Single(f => f.Name == "ShaderReadOnlyOptimal").Constant);

            TypeDefinition? presets = null;
            AssemblyDefinition? owner = null;
            foreach (string name in new[] { "Planet.Render.Core.dll", "KSA.dll", "Brutal.Vulkan.Abstractions.dll" })
            {
                var candidate = AssemblyDefinition.ReadAssembly(Path.Combine(installDir, name));
                TypeDefinition? found = TryTypeOf(candidate.MainModule,
                    "KSA.Rendering.ImageBarrierInfo/Presets");
                if (found is null)
                {
                    candidate.Dispose();
                    continue;
                }

                presets = found;
                owner = candidate;
                break;
            }

            Assert.NotNull(owner);
            using (owner)
            {
                var cctor = presets!.Methods.Single(m => m.Name == ".cctor");
                var instructions = cctor.Body.Instructions;
                int storeIndex = instructions.ToList().FindIndex(i =>
                    i.OpCode == OpCodes.Stsfld
                    && i.Operand is FieldReference fr && fr.Name == "SampledReadVfc");
                Assert.True(storeIndex >= 0,
                    "SampledReadVfc static store not found in the preset cctor");

                int sliceStart = 0;
                for (int i = storeIndex - 1; i >= 0; i--)
                {
                    if (instructions[i].OpCode == OpCodes.Stsfld)
                    {
                        sliceStart = i + 1;
                        break;
                    }
                }

                var slice = instructions.Skip(sliceStart).Take(storeIndex - sliceStart).ToList();
                int newobjIndex = slice.FindIndex(i =>
                    i.OpCode == OpCodes.Newobj
                    && i.Operand is MethodReference mr
                    && mr.DeclaringType.Name == "ImageBarrierInfo" && mr.Name == ".ctor");
                Assert.True(newobjIndex >= 0,
                    "SampledReadVfc cctor slice must construct ImageBarrierInfo; observed: " + Dump(slice));

                var ctor = (MethodReference)slice[newobjIndex].Operand;
                Assert.True(ctor.Parameters.Count >= 1
                        && ctor.Parameters[0].ParameterType.Name == "VkImageLayout",
                    "the first ImageBarrierInfo ctor argument must be the VkImageLayout");

                int? layoutArg = slice.Take(newobjIndex).Select(IntConstant).FirstOrDefault(c => c.HasValue);
                Assert.True(layoutArg == 5,
                    $"native SampledReadVfc must target ShaderReadOnlyOptimal (5); observed {layoutArg?.ToString() ?? "?"}; slice: " + Dump(slice));
            }
        }

        private static string Dump(IList<Instruction> instructions) =>
            string.Join("; ", instructions.Select(i =>
                $"{i.Offset:x4} {i.OpCode} {(i.Operand is FieldReference fr ? fr.DeclaringType.Name + "::" + fr.Name : i.Operand is MethodReference mr ? mr.DeclaringType.Name + "::" + mr.Name : i.Operand)}"));
    }
}

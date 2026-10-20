using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class AtlasCompositeOwnershipTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");
        private const string ProbeType = "KSARS.Visuals.AtlasCompositeProbe";

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

        private static List<(string DeclaringType, string Name)> AllCallsOf(TypeDefinition type) =>
            type.Methods.Where(m => m.HasBody).SelectMany(CallsOf).ToList();

        private static List<string> FieldStoresOf(MethodDefinition method) =>
            method.Body.Instructions
                .Where(i => (i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Stsfld)
                    && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).DeclaringType.FullName)
                .ToList();

        private static TypeDefinition TypeOf(ModuleDefinition module, string name)
        {
            var type = module.GetType(name);
            Assert.NotNull(type);
            return type;
        }

        private static int CountCalls(List<(string DeclaringType, string Name)> calls, string declaringType, string name) =>
            calls.Count(c => c.DeclaringType == declaringType && c.Name == name);

        [Fact]
        public void Composite_OwnsTargetBuffersPoolLayoutPipelineAndModules()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, ProbeType);
            var ctorCalls = CallsOf(type.Methods.Single(m => m.Name == ".ctor"));

            Assert.Equal(1, CountCalls(ctorCalls, "KSA.Rendering.RenderTarget", ".ctor"));
            Assert.Equal(1, CountCalls(ctorCalls, "Brutal.VulkanApi.Abstractions.BufferExExtensions", "CreateBuffer"));
            Assert.Equal(1, CountCalls(ctorCalls, "Brutal.VulkanApi.Abstractions.DescriptorPoolExExtensions", "CreateDescriptorPool"));
            Assert.Equal(1, CountCalls(ctorCalls, "Brutal.VulkanApi.Abstractions.DescriptorPoolExExtensions", "AllocateDescriptorSet"));
            Assert.Equal(1, CountCalls(ctorCalls, "Brutal.VulkanApi.Abstractions.DeviceExtensions", "CreatePipelineLayout"));

            var allCalls = AllCallsOf(type);
            Assert.Equal(2, CountCalls(ctorCalls, ProbeType, "CompileModule"));
            Assert.Equal(1, CountCalls(allCalls, "Brutal.VulkanApi.Abstractions.ShaderModuleExtensions", "FromSpirv"));
            Assert.Equal(1, CountCalls(allCalls, "Brutal.VulkanApi.Abstractions.DeviceExtensions", "CreateGraphicsPipeline"));
        }

        [Fact]
        public void Composite_BindsNativeGlobalSetReadOnly()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, ProbeType);
            var allCalls = AllCallsOf(type);

            Assert.True(CountCalls(allCalls, "KSA.GlobalShaderBindings", "DescriptorSet") >= 1);
            Assert.True(CountCalls(allCalls, "KSA.GlobalShaderBindings", "get_DescriptorSetLayout") >= 1);
            Assert.Equal(0, allCalls.Count(c => c.DeclaringType == "KSA.GlobalShaderBindings"
                && (c.Name.StartsWith("set_") || c.Name == "FrameOffset" || c.Name == "NextFrame")));

            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                Assert.DoesNotContain(FieldStoresOf(method), d => d.StartsWith("KSA."));
            }
        }

        [Fact]
        public void Composite_NeverInvokesNativeRenderEntryPoints()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var calls = AllCallsOf(TypeOf(assembly.MainModule, ProbeType));

            Assert.Equal(0, CountCalls(calls, "KSA.Program", "RenderGame"));
            Assert.Equal(0, CountCalls(calls, "KSA.Program", "RenderViewport"));
            Assert.Equal(0, CountCalls(calls, "KSA.DistantSphereRenderer", "Render"));
            Assert.Equal(0, CountCalls(calls, "KSA.StaticCelestial", "RenderSphere"));
            Assert.Equal(0, calls.Count(c => c.DeclaringType == "KSA.ViewportRegistry"));
            Assert.Equal(0, calls.Count(c => c.DeclaringType.StartsWith("KSA.Universe")));
            Assert.Equal(0, calls.Count(c => c.Name.Contains("WaitIdle")));
        }

        [Fact]
        public void CompositeRecord_BalancedPassAndViewportRestore()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = TypeOf(assembly.MainModule, ProbeType).Methods.Single(m => m.Name == "Record");
            var calls = CallsOf(record);

            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.RenderTarget", "BeginRendering"));
            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.RenderTarget", "EndRendering"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.Abstractions.CommandBufferExtensions", "PushConstants"));
            Assert.True(CountCalls(calls, "KSA.Program", "SetViewport") >= 1);
            Assert.True(record.Body.ExceptionHandlers.Count >= 1);
        }

        [Fact]
        public void CompositeRecord_MarksUnbalancedPassUnrecovered()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, ProbeType);
            Assert.NotNull(type.Properties.SingleOrDefault(p => p.Name == "RecordingRecovered"));

            var record = type.Methods.Single(m => m.Name == "Record");
            var instructions = record.Body.Instructions.ToList();
            Assert.Equal(2, instructions.Count(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference mr && mr.Name == "set_RecordingRecovered"));
            int endIndex = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.Name == "EndRendering");
            int recoveredStoreIndex = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.Name == "set_RecordingRecovered"
                && IsTrueConstant(instructions[instructions.IndexOf(i) - 1]));
            Assert.True(endIndex >= 0);
            Assert.True(recoveredStoreIndex > endIndex,
                "recovered flag must only be set after EndRendering succeeds");
        }

        private static bool IsTrueConstant(Instruction instruction) =>
            instruction.OpCode == OpCodes.Ldc_I4_1
            || (instruction.OpCode == OpCodes.Ldc_I4 && instruction.Operand is int v && v == 1);

        private static IEnumerable<MethodDefinition> MethodsWithLambdas(TypeDefinition type) =>
            type.Methods.Where(m => m.HasBody)
                .Concat(type.NestedTypes
                    .Where(n => n.FullName.Contains("<>c") || n.FullName.Contains("DisplayClass"))
                    .SelectMany(n => n.Methods.Where(m => m.HasBody)));

        [Fact]
        public void CoordinatorComposite_UnbalancedPassAbortsFrame()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var coordinator = TypeOf(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer");
            var record = coordinator.Methods.Single(m => m.Name == "TryRecordComposite");

            Assert.True(record.Body.Instructions.Any(i => i.OpCode == OpCodes.Rethrow),
                "unbalanced composite pass must rethrow to abort the frame");

            var lambdaCalls = MethodsWithLambdas(coordinator)
                .Where(m => m.Name.Contains("TryRecordComposite") || m.Name.Contains("ClearDepthAndRecordSource"))
                .SelectMany(CallsOf)
                .ToList();
            Assert.True(lambdaCalls.Count(c =>
                    c.DeclaringType == ProbeType && c.Name == "get_RecordingRecovered") >= 2,
                "composite failure and the record boundary must both check owned pass recovery flags");
        }

        [Fact]
        public void CompositeDispose_DestroysAllOwnedResources()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var dispose = TypeOf(assembly.MainModule, ProbeType).Methods.Single(m => m.Name == "Dispose");
            var calls = CallsOf(dispose);

            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.RenderTarget", "Dispose"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.Abstractions.DescriptorPoolEx", "Dispose"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.Abstractions.DescriptorSetLayoutEx", "Dispose"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.Abstractions.BufferEx", "Dispose"));
            Assert.Equal(2, calls.Count(c => c.Name == "DestroyShaderModule"));
            Assert.Equal(1, calls.Count(c => c.Name == "DestroyPipeline"));
            Assert.Equal(1, calls.Count(c => c.Name == "DestroyPipelineLayout"));
            Assert.Equal(0, calls.Count(c => c.Name.Contains("FreeDescriptorSet")));
        }

        [Fact]
        public void ProbeGui_ExposesBetaOverrideControls()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer");
            var overrideControls = type.Methods.Single(m => m.Name == "DrawSpeedOverrideControls");
            var calls = CallsOf(overrideControls);

            Assert.True(CountCalls(calls, "Brutal.ImGuiApi.ImGui", "SliderFloat") >= 1);
            Assert.True(CountCalls(calls, "Brutal.ImGuiApi.ImGui", "InputDouble") >= 1);
            Assert.True(CountCalls(calls, "Brutal.ImGuiApi.ImGui", "Checkbox") >= 1);

#if DEBUG
            var gui = type.Methods.Single(m => m.Name == "DrawProbeGui");
            Assert.True(CountCalls(CallsOf(gui), "Brutal.ImGuiApi.ImGui", "InputInt") >= 1);
#endif
        }

        [Fact]
        public void CompositeRecord_UsesCallerProvidedObserver()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = TypeOf(assembly.MainModule, ProbeType).Methods.Single(m => m.Name == "Record");

            Assert.Equal(5, record.Parameters.Count);
            Assert.Equal("KSARS.Visuals.PresentationVelocity",
                record.Parameters[2].ParameterType.FullName.TrimEnd('&'));
            Assert.DoesNotContain(CallsOf(record),
                c => c.DeclaringType == "KSARS.Visuals.ObserverSnapshot" && c.Name == "get_Observer");
        }

        [Fact]
        public void CompositeFragment_UnprojectsNearPlaneRayWithoutWDivide()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, ProbeType);
            var field = type.Fields.Single(f => f.Name == "FragmentSource");
            var source = Assert.IsType<string>(field.Constant);

            Assert.Contains("vec4(inUv * 2.0 - 1.0, 1.0, 1.0)", source);
            Assert.Contains("normalize(viewPos.xyz)", source);
            Assert.DoesNotContain("/ viewPos.w", source);
        }

        [Fact]
        public void CompositeFragment_DepthOcclusionCoverageAndModePins()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, ProbeType);
            var field = type.Fields.Single(f => f.Name == "FragmentSource");
            var source = Assert.IsType<string>(field.Constant);

            Assert.Contains("binding = 2) uniform sampler2D depthTiles[4];", source);
            Assert.Contains("depth > 0.0 && depth <= 1.0", source);
            Assert.Contains("texelFetch(depthTiles", source);
            Assert.Contains("misc.y / atlas.bodies[i].misc.z", source);
            Assert.Contains("b / (depth + a)", source);
            Assert.Contains("params.counts.y == 6", source);
            Assert.Contains("vec2 tangent = vec2(dot(nRest, atlas.bodies[bi].rightTan.xyz)", source);
            Assert.Contains("dot(tangent, tangent)", source);
            Assert.Contains("perp2 <= z0 * z0 * tanHalf * tanHalf", source);
            Assert.Contains("z0 > 0.0", source);
            Assert.DoesNotContain("1.0 - z0 * z0", source);
            Assert.DoesNotContain("clamp(uv0", source);
            Assert.DoesNotContain("upCos.w", source);
            Assert.DoesNotContain("clamp(uv,", source);
        }

        [Fact]
        public void CompositeFragment_RestStateBranchStillPinned()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, ProbeType);
            var source = Assert.IsType<string>(type.Fields.Single(f => f.Name == "FragmentSource").Constant);

            Assert.Contains("vec3 nRest = nObs;", source);
            Assert.Contains("float doppler = 1.0;", source);
            Assert.Contains("if (q != 1.0)", source);
        }

        [Fact]
        public void CompositeSyncBodies_AlwaysRewritesBufferAndDescriptors()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var sync = TypeOf(assembly.MainModule, ProbeType).Methods.Single(m => m.Name == "SyncBodies");
            var calls = CallsOf(sync);

            Assert.Equal(0, sync.Body.Instructions.Count(i =>
                i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference fr && fr.Name == "_boundBodyCount"));
            Assert.Equal(2, CountCalls(calls, ProbeType, "WriteImageDescriptorArray"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.Abstractions.MappableExtensions", "Map"));
        }

        [Fact]
        public void CoordinatorComposite_ResolvesAcquiredSlotBeforeCreationAndSync()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = TypeOf(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer")
                .Methods.Single(m => m.Name == "TryRecordComposite");
            var calls = CallsOf(record);

            int resolveIndex = calls.FindIndex(c => c.DeclaringType == "KSARS.Visuals.RelativisticSpaceRenderer"
                && c.Name == "TryResolveMainSyncSlot");
            int ctorIndex = calls.FindIndex(c => c.DeclaringType == ProbeType && c.Name == ".ctor");
            int syncIndex = calls.FindIndex(c => c.DeclaringType == ProbeType && c.Name == "SyncBodies");
            Assert.True(resolveIndex >= 0, "composite must resolve the acquired sync slot first");
            Assert.True(resolveIndex < ctorIndex && resolveIndex < syncIndex,
                "slot resolution must precede composite creation and SyncBodies");
            Assert.Equal(0, calls.Count(c => c.Name.Contains("WaitIdle")));
            Assert.Equal(0, calls.Count(c => c.DeclaringType == "KSA.ViewportRegistry"));
            Assert.Equal(0, calls.Count(c => c.DeclaringType.StartsWith("KSA.Universe")));
        }

        [Fact]
        public void CoordinatorCompositeFailure_IsContained()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = TypeOf(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer")
                .Methods.Single(m => m.Name == "TryRecordComposite");

            Assert.True(record.Body.ExceptionHandlers.Count >= 1);
            var calls = CallsOf(record);
            Assert.Equal(0, calls.Count(c => c.DeclaringType == "KSA.ViewportRegistry"));
            Assert.Equal(0, calls.Count(c => c.DeclaringType.StartsWith("KSA.Universe")));
        }
    }
}

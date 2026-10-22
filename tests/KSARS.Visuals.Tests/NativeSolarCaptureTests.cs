using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class NativeSolarCaptureTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");
        private const string AdapterType = "KSARS.Visuals.NativeSolarCaptureAdapter";

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

        private static List<(string DeclaringType, string Name)> FieldRefsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Where(i => i.Operand is FieldReference)
                .Select(i =>
                {
                    var fr = (FieldReference)i.Operand;
                    return (fr.DeclaringType.FullName, fr.Name);
                })
                .ToList();

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

        private static MethodDefinition SingleMethod(ModuleDefinition module, string typeName, string methodName) =>
            TypeOf(module, typeName).Methods.Single(m => m.Name == methodName);

        private static int CountCalls(List<(string DeclaringType, string Name)> calls, string declaringType, string name) =>
            calls.Count(c => c.DeclaringType == declaringType && c.Name == name);

        [Fact]
        public void SolarAdapter_BorrowsSunShadersMeshAndNoise()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, AdapterType);
            var ctorCalls = CallsOf(type.Methods.Single(m => m.Name == ".ctor"));

            Assert.True(ctorCalls.Count(c => c.DeclaringType == "KSA.ModLibrary" && c.Name == "Get") >= 3,
                "adapter must borrow SunVert/SunFrag ShaderReferences, the Sphere MeshReference, and the NoiseTex TextureReference");

            var allCalls = AllCallsOf(type);
            Assert.Equal(0, allCalls.Count(c => c.Name == "DestroyShaderModule"));
            var borrowed = new[]
            {
                "RenderCore.Mesh.SimpleVkMesh",
                "KSA.MeshReference",
                "KSA.ShaderReference",
                "KSA.TextureReference",
                "KSA.GpuTextureSystem",
            };
            Assert.Equal(0, allCalls.Count(c => borrowed.Contains(c.DeclaringType)
                && (c.Name == "Dispose" || c.Name == "Free" || c.Name == "FreeDescriptorSets")));
        }

        [Fact]
        public void SolarAdapter_OwnsBuffersLayoutsPoolAndPipeline()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var ctorCalls = CallsOf(SingleMethod(assembly.MainModule, AdapterType, ".ctor"));

            Assert.Equal(2, CountCalls(ctorCalls, "Brutal.VulkanApi.Abstractions.BufferExExtensions", "CreateBuffer"));
            Assert.Equal(2, CountCalls(ctorCalls, "Brutal.VulkanApi.Abstractions.DescriptorSetLayoutExExtensions", "CreateDescriptorSetLayout"));
            Assert.Equal(1, CountCalls(ctorCalls, "Brutal.VulkanApi.Abstractions.DescriptorPoolExExtensions", "CreateDescriptorPool"));
            Assert.Equal(1, CountCalls(ctorCalls, "Brutal.VulkanApi.Abstractions.DescriptorPoolExExtensions", "AllocateDescriptorSet"));
            Assert.Equal(1, CountCalls(ctorCalls, "Brutal.VulkanApi.Abstractions.DeviceExtensions", "CreatePipelineLayout"));

            var type = TypeOf(assembly.MainModule, AdapterType);
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_sceneBuffer"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_instanceBuffer"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_gapLayout"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_depthState"));
        }

        [Fact]
        public void SolarAdapter_BindsOwnedGlobalSetOnlyAtSet2()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = SingleMethod(assembly.MainModule, AdapterType, "Record");
            var calls = CallsOf(record);

            Assert.Equal(2, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "BindDescriptorSets"));
            Assert.True(CountCalls(calls, "KSARS.Visuals.ObjectCaptureContext", "get_GlobalSet") >= 1,
                "the owned snapshot global set must be bound for SET_GLOBAL 2");

            var allCalls = AllCallsOf(TypeOf(assembly.MainModule, AdapterType));
            Assert.Equal(0, CountCalls(allCalls, "KSA.GlobalShaderBindings", "DescriptorSet"));
            Assert.Equal(0, allCalls.Count(c => c.DeclaringType == "KSA.GlobalShaderBindings"
                && (c.Name.StartsWith("set_") || c.Name == "FrameOffset" || c.Name == "NextFrame")));
        }

        [Fact]
        public void SolarAdapter_NeverTouchesNativeSunRenderPath()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var calls = AllCallsOf(TypeOf(assembly.MainModule, AdapterType));

            Assert.Equal(0, calls.Count(c => c.DeclaringType.StartsWith("KSA.Rendering.Sun")));
            Assert.Equal(0, calls.Count(c => c.DeclaringType.Contains("MeshPassBucket")));
            Assert.Equal(0, CountCalls(calls, "KSA.Program", "RenderGame"));
            Assert.Equal(0, CountCalls(calls, "KSA.Program", "RenderViewport"));
            Assert.Equal(0, CountCalls(calls, "KSA.Program", "UpdateShaderData"));
            Assert.Equal(0, calls.Count(c => c.DeclaringType == "KSA.ViewportRegistry"));
            Assert.Equal(0, calls.Count(c => c.DeclaringType.StartsWith("KSA.Universe")));
            Assert.Equal(0, calls.Count(c => c.Name.Contains("WaitIdle")));

            foreach (var method in TypeOf(assembly.MainModule, AdapterType).Methods.Where(m => m.HasBody))
            {
                Assert.DoesNotContain(FieldStoresOf(method), d => d.StartsWith("KSA."));
            }
        }

        [Fact]
        public void SolarAdapterRecord_BalancedPassWithInstanceBindingAndViewportRestore()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = SingleMethod(assembly.MainModule, AdapterType, "Record");
            var calls = CallsOf(record);

            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.RenderTarget", "BeginRendering"));
            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.RenderTarget", "EndRendering"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "DrawIndexed"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.Abstractions.BindBufferExtensions", "BindVertexBuffer"));
            Assert.Equal(3, CountCalls(calls, "RenderCore.Mesh.SimpleVkMesh", "BindVertexStream"));
            Assert.Equal(2, CountCalls(calls, "Brutal.VulkanApi.Abstractions.MappableExtensions", "Map"));
            Assert.True(CountCalls(calls, "KSA.Program", "SetViewport") >= 1);
            Assert.True(record.Body.ExceptionHandlers.Count >= 1);
            Assert.True(calls.Count(c => c.DeclaringType == AdapterType && c.Name == "set_RecordingRecovered") >= 2);
        }

        [Fact]
        public void SolarAdapter_InstancedVertexInputUsesNativeInstanceData()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var make = SingleMethod(assembly.MainModule, AdapterType, "MakeVertexInput");

            Assert.True(make.Body.Instructions.Any(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is GenericInstanceMethod gim
                && gim.Name == "AddBindingWithAttributes"
                && gim.GenericArguments.Count == 1
                && gim.GenericArguments[0].FullName == "KSA.InstanceData"),
                "binding 3 must use the native KSA.InstanceData layout at instanced rate");
        }

        [Fact]
        public void SolarAdapter_OwnDepthStateGreaterNoWriteWithNativeAlphaBlend()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, AdapterType);
            var ctorCalls = CallsOf(type.Methods.Single(m => m.Name == ".ctor"));

            Assert.True(ctorCalls.Any(c => c.DeclaringType.StartsWith("Brutal.Memory.PtrOwner")
                && c.Name == ".ctor"),
                "adapter must allocate its own depth-state PtrOwner instead of mutating a native preset");

            var rebuild = type.Methods.Single(m => m.Name == "OnRebuildFrameResources");
            Assert.Contains(FieldRefsOf(rebuild),
                f => f.DeclaringType == AdapterType && f.Name == "_depthState");
            Assert.Contains(FieldRefsOf(rebuild),
                f => f.Name == "BlendColorAlpha");
        }

        [Fact]
        public void SolarAdapterDispose_DestroysOwnedOnly()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var dispose = SingleMethod(assembly.MainModule, AdapterType, "Dispose");
            var calls = CallsOf(dispose);

            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.Abstractions.DescriptorPoolEx", "Dispose"));
            Assert.Equal(2, CountCalls(calls, "Brutal.VulkanApi.Abstractions.DescriptorSetLayoutEx", "Dispose"));
            Assert.Equal(2, CountCalls(calls, "Brutal.VulkanApi.Abstractions.BufferEx", "Dispose"));
            Assert.Equal(1, calls.Count(c => c.DeclaringType.StartsWith("Brutal.Memory.PtrOwner")
                && c.Name == "Dispose"));
            Assert.Equal(1, CountCalls(calls, "RenderCore.RenderTechnique", "Dispose"));
            Assert.Equal(0, calls.Count(c => c.Name == "DestroyShaderModule"));
            Assert.Equal(0, calls.Count(c => c.Name.Contains("FreeDescriptorSet")));
        }

        [Fact]
        public void SolarAdapter_UsesPublishedNativeStructTypes()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, AdapterType);

            var genericArgs = type.Methods.Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions)
                .Select(i => i.Operand)
                .OfType<GenericInstanceMethod>()
                .Where(m => m.Name == "Of" && m.GenericArguments.Count == 1)
                .Select(m => m.GenericArguments[0].FullName)
                .ToList();
            Assert.Contains("KSA.MeshRenderTechnique/Ubo", genericArgs);
            Assert.Contains("KSA.InstanceData", genericArgs);

            var record = SingleMethod(assembly.MainModule, AdapterType, "Record");
            Assert.Equal("KSA.MeshRenderTechnique/Ubo", record.Parameters[2].ParameterType.FullName.TrimEnd('&'));
            Assert.Equal("KSA.InstanceData", record.Parameters[3].ParameterType.FullName.TrimEnd('&'));
        }

        [Fact]
        public void CoordinatorSolarRecord_ContainedCatchWithRecoveredConditionalRethrow()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var solar = SingleMethod(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer",
                "TryRecordSolarSource");
            var calls = CallsOf(solar);

            Assert.True(solar.Body.ExceptionHandlers.Count >= 1,
                "solar record and registration must be wrapped so failures disable solar only");
            Assert.Contains(calls, c => c.DeclaringType == AdapterType && c.Name == "Record");
            Assert.True(calls.Any(c => c.DeclaringType == AdapterType && c.Name == "get_RecordingRecovered"),
                "the contained catch must branch on adapter recovery state");
            Assert.True(solar.Body.Instructions.Any(i => i.OpCode == OpCodes.Rethrow),
                "an unrecovered solar pass must rethrow to abort the frame");
            int recordIndex = calls.FindIndex(c => c.DeclaringType == AdapterType && c.Name == "Record");
            Assert.True(recordIndex >= 0);
            Assert.Equal(0, calls.Skip(recordIndex + 1).Count(c => c.Name == "Dispose"));

            var boundary = SingleMethod(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer",
                "TryRecordSources");
            var boundaryCalls = CallsOf(boundary);
            int solarIndex = boundaryCalls.FindIndex(c => c.Name == "TryRecordSolarSource");
            int bodyIndex = boundaryCalls.FindIndex(c => c.Name == "TrySelectAndCapture");
            Assert.True(solarIndex >= 0 && bodyIndex > solarIndex,
                "body capture must continue after the solar attempt at the same boundary");
        }
    }
}

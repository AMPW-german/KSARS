using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class NativeCaptureOwnershipTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");

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

        private static MethodDefinition SingleMethod(ModuleDefinition module, string typeName, string methodName)
        {
            return TypeOf(module, typeName).Methods.Single(m => m.Name == methodName);
        }

        private static int CountCalls(List<(string DeclaringType, string Name)> calls, string declaringType, string name) =>
            calls.Count(c => c.DeclaringType == declaringType && c.Name == name);

        [Fact]
        public void ContextCtor_BorrowsNativeGlobalLayoutAndAllocatesOwnSets()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var ctor = SingleMethod(assembly.MainModule, "KSARS.Visuals.ObjectCaptureContext", ".ctor");
            var calls = CallsOf(ctor);

            Assert.Equal(1, calls.Count(c => c.DeclaringType == "KSA.GlobalShaderBindings"));
            Assert.Equal(1, CountCalls(calls, "KSA.GlobalShaderBindings", "get_DescriptorSetLayout"));
            Assert.Equal(2, CountCalls(calls, "Brutal.VulkanApi.Abstractions.DescriptorPoolExExtensions", "AllocateDescriptorSet"));
            Assert.Equal(0, calls.Count(c => c.DeclaringType == "KSA.GlobalShaderBindings"
                && (c.Name == "DescriptorSet" || c.Name == "FrameOffset" || c.Name.StartsWith("set_"))));
        }

        [Fact]
        public void ContextCtor_NeverWritesNativeGlobalState()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, "KSARS.Visuals.ObjectCaptureContext");
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                Assert.DoesNotContain(FieldRefsOf(method), f => f.DeclaringType == "KSA.GlobalShaderBindings");
            }
        }

        [Fact]
        public void ContextCtor_BuildsOwnTargetAndBuffers()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var ctor = SingleMethod(assembly.MainModule, "KSARS.Visuals.ObjectCaptureContext", ".ctor");
            var calls = CallsOf(ctor);

            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.RenderTarget", ".ctor"));
            Assert.Equal(3, CountCalls(calls, "KSARS.Visuals.ObjectCaptureContext", "CreateUniformBuffer"));
            Assert.Equal(3, CountCalls(calls, "Brutal.VulkanApi.Abstractions.MappableExtensions", "Map"));
            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.Utils", "BuildDescriptorSetLayout"));
            Assert.Equal(2, CountCalls(calls, "KSA.Rendering.Utils", "WriteDescriptorSets"));

            var helper = SingleMethod(assembly.MainModule, "KSARS.Visuals.ObjectCaptureContext", "CreateUniformBuffer");
            var helperCalls = CallsOf(helper);
            Assert.Equal(1, CountCalls(helperCalls, "Brutal.VulkanApi.Abstractions.BufferExExtensions", "CreateBuffer"));
        }

        [Fact]
        public void ContextDispose_DestroysOwnedPoolWithoutIndividualSetFrees()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var dispose = SingleMethod(assembly.MainModule, "KSARS.Visuals.ObjectCaptureContext", "Dispose");
            var calls = CallsOf(dispose);

            Assert.Equal(0, calls.Count(c => c.Name.Contains("FreeDescriptorSet")));
            Assert.Equal(1, calls.Count(c => c.DeclaringType.EndsWith("DescriptorPoolEx") && c.Name == "Dispose"));
        }

        [Fact]
        public void ContextAndAdapter_NeverWaitIdle()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var calls = AllCallsOf(TypeOf(assembly.MainModule, "KSARS.Visuals.ObjectCaptureContext"))
                .Concat(AllCallsOf(TypeOf(assembly.MainModule, "KSARS.Visuals.NativeCelestialCaptureAdapter")))
                .ToList();

            Assert.Equal(0, calls.Count(c => c.Name.Contains("WaitIdle")));
        }

        [Fact]
        public void Context_DoesNotTouchImGuiTextureRegistration()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var calls = AllCallsOf(TypeOf(assembly.MainModule, "KSARS.Visuals.ObjectCaptureContext"));

            Assert.Equal(0, calls.Count(c => c.DeclaringType.StartsWith("KSA.ImGuiBackend")
                || c.DeclaringType.StartsWith("Brutal.ImGuiApi")));
        }

        [Fact]
        public void AdapterRecord_RecordsOwnedDrawPathOnly()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = SingleMethod(assembly.MainModule, "KSARS.Visuals.NativeCelestialCaptureAdapter", "Record");
            var calls = CallsOf(record);

            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "BindDescriptorSets"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.Abstractions.CommandBufferExtensions", "PushConstants"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "DrawIndexed"));
            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.RenderTarget", "BeginRendering"));
            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.RenderTarget", "EndRendering"));
            Assert.Equal(0, CountCalls(calls, "KSA.DistantSphereRenderer", "Render"));
            Assert.Equal(0, CountCalls(calls, "KSA.Program", "RenderGame"));
            Assert.Equal(0, CountCalls(calls, "KSA.Program", "RenderViewport"));
            Assert.Equal(0, calls.Count(c => c.DeclaringType == "KSA.ViewportRegistry"));
            Assert.Equal(0, calls.Count(c => c.DeclaringType.StartsWith("KSA.Universe")));
        }

        [Fact]
        public void AdapterRecord_TracksBalancedRecoveryFlag()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, "KSARS.Visuals.NativeCelestialCaptureAdapter");
            var record = type.Methods.Single(m => m.Name == "Record");
            var recordCalls = CallsOf(record);

            Assert.True(recordCalls.Count(c =>
                c.DeclaringType == "KSARS.Visuals.NativeCelestialCaptureAdapter"
                && c.Name == "set_RecordingRecovered") >= 2);
            Assert.True(CountCalls(recordCalls, "KSA.Program", "SetViewport") >= 1);
            Assert.Equal(0, CountCalls(recordCalls, "KSA.Program", "RenderViewport"));

            foreach (var method in type.Methods.Where(m => m.HasBody && m != record))
            {
                Assert.Equal(0, CallsOf(method).Count(c => c.Name == "set_RecordingRecovered"));
            }

            var flag = type.Properties.Single(p => p.Name == "RecordingRecovered");
            Assert.True(flag.SetMethod is not null && flag.SetMethod.IsPrivate);
        }

        [Fact]
        public void Adapter_BorrowsNativeMeshAndShadersWithoutDisposing()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, "KSARS.Visuals.NativeCelestialCaptureAdapter");
            var ctorCalls = CallsOf(type.Methods.Single(m => m.Name == ".ctor"));

            Assert.True(ctorCalls.Count(c => c.DeclaringType == "KSA.ModLibrary" && c.Name == "Get") >= 2);

            var allCalls = AllCallsOf(type);
            var borrowed = new[]
            {
                "RenderCore.Mesh.SimpleVkMesh",
                "KSA.MeshReference",
                "KSA.ShaderReference",
                "KSA.GpuTextureSystem",
            };
            Assert.Equal(0, allCalls.Count(c => borrowed.Contains(c.DeclaringType)
                && (c.Name == "Dispose" || c.Name == "Free" || c.Name == "FreeDescriptorSets")));
        }

        [Fact]
        public void SnapshotTryCapture_ReadsObservedStateWithoutControlledVehicleOrGui()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var capture = SingleMethod(assembly.MainModule, "KSARS.Visuals.ObserverSnapshot", "TryCapture");
            var calls = CallsOf(capture);

            Assert.Equal(0, CountCalls(calls, "KSA.Program", "get_ControlledVehicle"));
            Assert.Equal(0, calls.Count(c => c.Name.Contains("AfterGui")
                || c.DeclaringType.StartsWith("Brutal.ImGuiApi")
                || c.DeclaringType.StartsWith("KSA.ImGuiBackend")));
            Assert.True(calls.Count(c => c.DeclaringType == "KSA.Camera" && c.Name == "get_Following") >= 1);
            Assert.True(calls.Count(c => c.Name == "GetVelocityEcl") >= 1);
            Assert.True(CountCalls(calls, "KSA.GlobalShaderBindings", "CameraData") >= 1);
            Assert.True(CountCalls(calls, "KSA.GlobalShaderBindings", "LightingData") >= 1);
            Assert.True(CountCalls(calls, "KSA.GlobalShaderBindings", "CelestialData") >= 1);
            Assert.True(CountCalls(calls, "KSA.GlobalShaderBindings", "VesselData") >= 1);
            Assert.True(CountCalls(calls, "KSA.GlobalShaderBindings", "get_Constants") >= 1);
            Assert.Equal(0, calls.Count(c => c.DeclaringType == "KSA.GlobalShaderBindings"
                && (c.Name == "DescriptorSet" || c.Name == "FrameOffset" || c.Name == "NextFrame" || c.Name.StartsWith("set_"))));
        }

        [Fact]
        public void SnapshotTryCapture_UsesCallerProvidedGlobalFrameGetter()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var capture = SingleMethod(assembly.MainModule, "KSARS.Visuals.ObserverSnapshot", "TryCapture");
            var calls = CallsOf(capture);

            Assert.True(capture.IsStatic);
            Assert.Equal("System.Boolean", capture.ReturnType.FullName);
            Assert.Equal(3, capture.Parameters.Count);
            Assert.True(calls.Count(c => c.DeclaringType == "System.Func`1<System.Int32>" && c.Name == "Invoke") >= 1);
            Assert.Equal(0, CountCalls(calls, "KSA.GlobalShaderBindings", "NextFrame"));
        }

        [Fact]
        public void SnapshotTryCapture_NeverWritesNativeFields()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, "KSARS.Visuals.ObserverSnapshot");
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                Assert.DoesNotContain(FieldStoresOf(method), d => !d.StartsWith("KSARS."));
            }
        }

        [Fact]
        public void Snapshot_IsImmutableGetOnlyWithPrivateCtor()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, "KSARS.Visuals.ObserverSnapshot");

            Assert.All(type.Properties, p => Assert.True(p.SetMethod is null, $"{p.Name} must not have a setter"));
            Assert.All(type.Fields.Where(f => !f.IsStatic),
                f => Assert.True(f.IsInitOnly, $"{f.Name} must be readonly"));
            Assert.True(type.Methods.Single(m => m.Name == ".ctor").IsPrivate);
        }
    }
}

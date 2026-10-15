using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using KSARS.Visuals;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class InstalledBaselineTests
    {
        private static List<(string DeclaringType, string Name)> CallsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference)
                .Select(i =>
                {
                    var mr = (MethodReference)i.Operand;
                    return (mr.DeclaringType.FullName, mr.Name);
                })
                .ToList();

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

        private static MethodDefinition SingleMethod(ModuleDefinition module, string declaringType, string name, params string[] parameterTypeNames)
        {
            var type = module.GetType(declaringType);
            Assert.NotNull(type);
            return type.Methods.Single(m =>
                m.Name == name &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypeNames));
        }

        [RendererBaselineFact]
        public void InstalledKsaBinary_HasSupportedIdentity()
        {
            string path = BaselineContext.KsaAssemblyPath;
            var info = FileVersionInfo.GetVersionInfo(path);
            Assert.Equal(RenderCompatibility.SupportedFileVersion, info.FileVersion);
            Assert.Equal(RenderCompatibility.SupportedProductVersion, info.ProductVersion);

            string sha256;
            using (var stream = File.OpenRead(path))
            {
                sha256 = Convert.ToHexString(SHA256.HashData(stream));
            }
            Assert.Equal(RenderCompatibility.SupportedSha256, sha256);
        }

        [RendererBaselineFact]
        public void InstalledCoreRendererBinary_HasSupportedIdentity()
        {
            string path = Path.Combine(Path.GetDirectoryName(BaselineContext.KsaAssemblyPath)!, "Planet.Render.Core.dll");
            Assert.True(File.Exists(path), $"Planet.Render.Core.dll not found at '{path}'");

            string sha256;
            using (var stream = File.OpenRead(path))
            {
                sha256 = Convert.ToHexString(SHA256.HashData(stream));
            }
            Assert.Equal(RenderCompatibility.SupportedCoreRendererSha256, sha256);
        }

        [RendererBaselineFact]
        public void InspectAssembly_OnInstalledBinary_MatchesBaselineWithMembers()
        {
            var result = RenderCompatibility.InspectAssembly(Assembly.LoadFrom(BaselineContext.KsaAssemblyPath));

            Assert.True(result.BaselineMatched, result.Reason);
            Assert.NotEmpty(result.Members);
        }

        [RendererBaselineFact]
        public void ProgramCtor_OrdersDescriptorsAndViewportSeal()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var ctor = SingleMethod(assembly.MainModule, "KSA.Program", ".ctor",
                "System.Collections.Generic.IReadOnlyList`1<System.String>");
            var calls = CallsOf(ctor);

            Assert.Equal(1, CountCalls(calls, "KSA.GlobalShaderBindings", "CreateDescriptors"));
            Assert.Equal(1, CountCalls(calls, "KSA.ModLibrary", "PrepareAll"));
            Assert.Equal(1, CountCalls(calls, "KSA.ViewportRegistry", "SealAllocation"));
            Assert.Equal(1, CountCalls(calls, "KSA.GlobalShaderBindings", "Initialize"));

            Assert.True(
                IndexOfCall(calls, "KSA.GlobalShaderBindings", "CreateDescriptors") < IndexOfCall(calls, "KSA.ModLibrary", "PrepareAll") &&
                IndexOfCall(calls, "KSA.ModLibrary", "PrepareAll") < IndexOfCall(calls, "KSA.ViewportRegistry", "SealAllocation") &&
                IndexOfCall(calls, "KSA.ViewportRegistry", "SealAllocation") < IndexOfCall(calls, "KSA.GlobalShaderBindings", "Initialize"));
        }

        [RendererBaselineFact]
        public void RenderGame_RecordsCompositionCallsOnceInOrder()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var renderGame = SingleMethod(assembly.MainModule, "KSA.Program", "RenderGame",
                "RenderCore.AcquiredFrame", "System.Double");
            var calls = CallsOf(renderGame);

            var expected = new[]
            {
                ("KSA.SunbloomRenderer", "Render"),
                ("KSA.OverallBloomRenderer", "Render"),
                ("KSA.PartSelectedRenderer", "Run"),
                ("KSA.Program", "RenderAaPreparePasses"),
                ("KSA.Program", "RenderFinalComposite"),
                ("KSA.ImGuiBackendVulkanImpl", "RenderDrawData"),
            };

            int previousIndex = -1;
            foreach (var (declaringType, name) in expected)
            {
                Assert.Equal(1, CountCalls(calls, declaringType, name));
                int index = IndexOfCall(calls, declaringType, name);
                Assert.True(index > previousIndex, $"{declaringType}.{name} out of order");
                previousIndex = index;
            }
        }

        [RendererBaselineFact]
        public void RebuildRenderer_SingleReturnTwoIdleCallsAndVerifiedTail()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var rebuild = SingleMethod(assembly.MainModule, "KSA.Program", "RebuildRenderer", "System.Boolean");
            Assert.Equal("System.Void", rebuild.ReturnType.FullName);

            var calls = rebuild.Body.Instructions
                .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference)
                .Select(i =>
                {
                    var mr = (MethodReference)i.Operand;
                    return (mr.DeclaringType.FullName, mr.Name, mr.ReturnType.FullName);
                })
                .ToList();
            int returnCount = rebuild.Body.Instructions.Count(i => i.OpCode == OpCodes.Ret);
            var tail = rebuild.Body.Instructions
                .Where(i => i.OpCode != OpCodes.Nop)
                .TakeLast(8)
                .Select(i => (i.OpCode.Name ?? i.OpCode.ToString(), i.Operand switch
                {
                    MethodReference mr => $"{mr.DeclaringType.FullName}::{mr.Name}",
                    FieldReference fr => $"{fr.DeclaringType.FullName}::{fr.Name}",
                    _ => (string?)null,
                }))
                .ToList();

            Assert.Equal(1, returnCount);
            Assert.True(
                RenderCompatibility.TryValidateRebuildBoundary(calls, returnCount, tail, out string reason),
                reason);
            Assert.Equal(2, calls.Count(c =>
                c.Item1 == "Brutal.VulkanApi.VkDeviceExtensions" && c.Item2 == "WaitIdle"));
        }

        [RendererBaselineFact]
        public void NativeWaitIdle_ReturnsVoidAndThrowsOnNonSuccessVkResult()
        {
            string vulkanPath = Path.Combine(
                Path.GetDirectoryName(BaselineContext.KsaAssemblyPath)!, "Brutal.Vulkan.dll");
            Assert.True(File.Exists(vulkanPath), $"Brutal.Vulkan.dll not found at '{vulkanPath}'");

            using var assembly = AssemblyDefinition.ReadAssembly(vulkanPath);
            var type = assembly.MainModule.GetType("Brutal.VulkanApi.VkDeviceExtensions");
            Assert.NotNull(type);
            var waitIdle = type.Methods.Single(m => m.Name == "WaitIdle" && m.Parameters.Count == 1);

            Assert.Equal("System.Void", waitIdle.ReturnType.FullName);
            var instructions = waitIdle.Body.Instructions;
            Assert.Contains(instructions, i => i.OpCode == OpCodes.Calli);
            Assert.True(instructions.Count(i => i.OpCode == OpCodes.Throw) >= 4,
                "WaitIdle must throw on every non-success VkResult");
            Assert.True(instructions.Count(i => i.OpCode == OpCodes.Newobj
                    && i.Operand is MethodReference mr && mr.DeclaringType.Name == "VkResultError") >= 4,
                "WaitIdle must construct VkResultError for failure results");
            Assert.DoesNotContain(instructions,
                i => i.OpCode == OpCodes.Leave || i.OpCode == OpCodes.Leave_S);
        }

        [RendererBaselineFact]
        public void RenderViewport_LacksSceneContentPasses()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var renderViewport = SingleMethod(assembly.MainModule, "KSA.Program", "RenderViewport",
                "Brutal.VulkanApi.CommandBuffer", "KSA.IViewport", "System.Int32");
            var calls = CallsOf(renderViewport);

            Assert.Equal(1, CountCalls(calls, "KSA.PlanetTransparenciesRenderer", "RenderAtmosphereOnly"));
            Assert.Equal(0, CountCalls(calls, "KSA.PlanetTransparenciesRenderer", "Render"));
            Assert.Equal(0, CountCalls(calls, "KSA.Rendering.Water.Rendering.OceanRenderer", "Render"));
            Assert.Equal(0, CountCalls(calls, "KSA.Rendering.Water.Rendering.OceanRenderer", "RenderUnderwater"));
            Assert.Equal(0, CountCalls(calls, "KSA.PlanetTransparenciesRenderer", "RenderVolumetricTrails"));
            Assert.Equal(0, CountCalls(calls, "KSA.VolumetricExhaustRenderer", "Render"));
        }

        [RendererBaselineFact]
        public void ProgramDispose_ReleasesViewportsBeforeDeviceTeardown()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var dispose = SingleMethod(assembly.MainModule, "KSA.Program", "Dispose");
            var calls = CallsOf(dispose);

            Assert.Equal(1, CountCalls(calls, "KSA.ViewportRegistry", "DisposeAll"));
            Assert.Equal(1, CountCalls(calls, "Core.KSADeviceContextEx", "Dispose"));
            Assert.True(
                IndexOfCall(calls, "KSA.ViewportRegistry", "DisposeAll") < IndexOfCall(calls, "Core.KSADeviceContextEx", "Dispose"),
                "ViewportRegistry.DisposeAll must precede Core.KSADeviceContextEx.Dispose");
        }

        [RendererBaselineFact]
        public void RenderGame_ContainsSceneContentPassesOnce()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var renderGame = SingleMethod(assembly.MainModule, "KSA.Program", "RenderGame",
                "RenderCore.AcquiredFrame", "System.Double");
            var calls = CallsOf(renderGame);

            Assert.Equal(1, CountCalls(calls, "KSA.PlanetTransparenciesRenderer", "Render"));
            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.Water.Rendering.OceanRenderer", "Render"));
            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.Water.Rendering.OceanRenderer", "RenderUnderwater"));
            Assert.Equal(1, CountCalls(calls, "KSA.PlanetTransparenciesRenderer", "RenderVolumetricTrails"));
            Assert.Equal(1, CountCalls(calls, "KSA.VolumetricExhaustRenderer", "Render"));
        }

        [RendererBaselineFact]
        public void ProgramRenderGame_SourceBoundaryAndDepthLoadIntact()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var renderGame = SingleMethod(assembly.MainModule, "KSA.Program", "RenderGame",
                "RenderCore.AcquiredFrame", "System.Double");
            var calls = CallsOf(renderGame);

            Instruction beginRendering = renderGame.Body.Instructions.Single(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "KSA.Rendering.RenderTarget"
                && mr.Name == "BeginRendering");

            int? depthLoad = null;
            int? colorClear = null;
            int nonNop = 0;
            for (Instruction? cursor = beginRendering.Previous; cursor != null && nonNop < 2; cursor = cursor.Previous)
            {
                if (cursor.OpCode == OpCodes.Nop)
                {
                    continue;
                }

                int? value = IntConstant(cursor);
                if (nonNop == 0)
                {
                    depthLoad = value;
                }
                else
                {
                    colorClear = value;
                }
                nonNop++;
            }

            bool nativeDepthLoad = depthLoad == 0 && colorClear == 1;
            Assert.True(nativeDepthLoad,
                $"expected BeginRendering tail args Clear=1, Load=0; observed depth={depthLoad?.ToString() ?? "?"}, color={colorClear?.ToString() ?? "?"}");
            Assert.True(RenderCompatibility.TryValidateSourceBoundary(calls, nativeDepthLoad, out string reason), reason);
        }

        private static int IndexOfCall(MethodDefinition method, Func<MethodReference, bool> match)
        {
            return method.Body.Instructions.ToList().FindIndex(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
                && i.Operand is MethodReference mr && match(mr));
        }

        private static int IndexOfFieldStore(MethodDefinition method, string name)
        {
            return method.Body.Instructions.ToList().FindIndex(i =>
                (i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Stsfld)
                && i.Operand is FieldReference fr && fr.Name == name);
        }

        [RendererBaselineFact]
        public void TryAcquireNextFrame_WaitsAndResetsSlotFenceBeforeAcquire()
        {
            string corePath = Path.Combine(Path.GetDirectoryName(BaselineContext.KsaAssemblyPath)!, "Planet.Render.Core.dll");
            using var assembly = AssemblyDefinition.ReadAssembly(corePath);
            var acquire = SingleMethod(assembly.MainModule, "Core.Renderer", "TryAcquireNextFrame");

            int syncStoreIndex = IndexOfFieldStore(acquire, "_syncObjectsIndex");
            int waitIndex = IndexOfCall(acquire, m => m.Name == "WaitForFences");
            int resetIndex = IndexOfCall(acquire, m => m.Name == "ResetFences");
            int acquireIndex = IndexOfCall(acquire, m => m.Name.StartsWith("AcquireNextImage"));

            Assert.True(syncStoreIndex >= 0, "TryAcquireNextFrame must advance _syncObjectsIndex");
            Assert.True(acquire.Body.Instructions.Any(i => i.OpCode == OpCodes.Rem),
                "sync slot advance must be modulo the ring length");
            Assert.True(waitIndex >= 0, "TryAcquireNextFrame must wait on the slot fence");
            Assert.True(resetIndex > waitIndex, "slot fence must be waited before it is reset");
            Assert.True(acquireIndex > resetIndex, "AcquireNextImage must follow the fence wait and reset");
            Assert.True(syncStoreIndex < waitIndex, "sync slot must be selected before its fence is waited");
        }

        [RendererBaselineFact]
        public void OnPreRender_AcquiresFrameBeforeRenderGame()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var program = assembly.MainModule.GetType("KSA.Program");
            Assert.NotNull(program);

            var onPreRender = SingleMethod(assembly.MainModule, "KSA.Program", "OnPreRender", "System.Double");
            Assert.True(IndexOfCall(onPreRender, m => m.DeclaringType.FullName == "Core.Renderer"
                && m.Name == "TryAcquireNextFrame") >= 0,
                "OnPreRender must acquire the next frame from the renderer");

            var driver = program.Methods.Single(m => m.HasBody
                && m != onPreRender
                && CallsOf(m).Count(c => c.DeclaringType == "KSA.Program" && c.Name == "OnPreRender") == 1
                && CallsOf(m).Count(c => c.DeclaringType == "KSA.Program" && c.Name == "Render") == 1);
            int preRenderIndex = IndexOfCall(driver, m => m.Name == "OnPreRender"
                && m.DeclaringType.FullName == "KSA.Program");
            int renderIndex = IndexOfCall(driver, m => m.Name == "Render"
                && m.DeclaringType.FullName == "KSA.Program");
            Assert.True(preRenderIndex >= 0 && renderIndex > preRenderIndex,
                "frame acquisition must precede the render call");

            var render = program.Methods.Single(m => m.HasBody
                && CallsOf(m).Count(c => c.DeclaringType == "KSA.Program" && c.Name == "RenderGame") == 1);
            Assert.True(IndexOfCall(render, m => m.Name == "RenderGame") >= 0);
        }

        [RendererBaselineFact]
        public void PostRender_SubmitsFrameThenAdvancesResourceFrameIndexModuloRing()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var postRender = SingleMethod(assembly.MainModule, "KSA.Program", "PostRender", "System.Double");

            int submitIndex = IndexOfCall(postRender, m => m.DeclaringType.FullName == "Core.Renderer"
                && m.Name == "TrySubmitFrame");
            int storeIndex = IndexOfFieldStore(postRender, "ResourceFrameIndex");

            Assert.True(submitIndex >= 0, "PostRender must submit the recorded frame");
            Assert.True(storeIndex > submitIndex,
                "ResourceFrameIndex must advance after TrySubmitFrame");
            Assert.True(postRender.Body.Instructions.Any(i => i.OpCode == OpCodes.Rem),
                "ResourceFrameIndex advance must be modulo the ring length");
            Assert.True(IndexOfCall(postRender, m => m.Name == "get_MaxFramesInFlight") >= 0
                || postRender.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldfld
                    && i.Operand is FieldReference fr && fr.Name == "MaxFramesInFlight"),
                "ResourceFrameIndex modulo must use the renderer ring length");
        }

        private static Instruction? PreviousNonNop(Instruction instruction)
        {
            Instruction? cursor = instruction.Previous;
            while (cursor is not null && cursor.OpCode == OpCodes.Nop)
            {
                cursor = cursor.Previous;
            }

            return cursor;
        }

        [RendererBaselineFact]
        public void SyncSlotAndResourceFrameIndex_StartAtZero()
        {
            string corePath = Path.Combine(Path.GetDirectoryName(BaselineContext.KsaAssemblyPath)!, "Planet.Render.Core.dll");
            using var core = AssemblyDefinition.ReadAssembly(corePath);
            var renderer = core.MainModule.GetType("Core.Renderer");
            Assert.NotNull(renderer);

            var syncField = renderer.Fields.Single(f => f.Name == "_syncObjectsIndex");
            Assert.Equal("System.Int32", syncField.FieldType.FullName);
            foreach (MethodDefinition ctor in renderer.Methods.Where(m => m.HasBody
                && (m.Name == ".ctor" || m.Name == ".cctor")))
            {
                Assert.Equal(-1, IndexOfFieldStore(ctor, "_syncObjectsIndex"));
            }

            using var ksa = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var program = ksa.MainModule.GetType("KSA.Program");
            Assert.NotNull(program);
            var resourceField = program.Fields.Single(f => f.Name == "ResourceFrameIndex");
            Assert.Equal("System.Int32", resourceField.FieldType.FullName);

            int initializers = 0;
            foreach (MethodDefinition ctor in program.Methods.Where(m => m.HasBody && m.Name == ".ctor"))
            {
                int store = IndexOfFieldStore(ctor, "ResourceFrameIndex");
                if (store < 0)
                {
                    continue;
                }

                initializers++;
                Instruction storeInstruction = ctor.Body.Instructions[store];
                Assert.Equal(0, IntConstant(PreviousNonNop(storeInstruction)!));
            }

            Assert.True(initializers >= 1, "ResourceFrameIndex must be initialized to 0 in a constructor");
        }

        [RendererBaselineFact]
        public void TryAcquireNextFrame_ModuloIncrementsSyncIndexBeforeFenceWait()
        {
            string corePath = Path.Combine(Path.GetDirectoryName(BaselineContext.KsaAssemblyPath)!, "Planet.Render.Core.dll");
            using var assembly = AssemblyDefinition.ReadAssembly(corePath);
            var acquire = SingleMethod(assembly.MainModule, "Core.Renderer", "TryAcquireNextFrame");

            int syncStoreIndex = IndexOfFieldStore(acquire, "_syncObjectsIndex");
            int waitIndex = IndexOfCall(acquire, m => m.Name == "WaitForFences");

            Assert.True(syncStoreIndex >= 0);
            Assert.True(waitIndex > syncStoreIndex);
            Assert.Equal(OpCodes.Rem, PreviousNonNop(acquire.Body.Instructions[syncStoreIndex])!.OpCode);
        }

        [RendererBaselineFact]
        public void MenuScenes_AcquireAndSubmitWithoutResourceFrameIndexStores()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            foreach (string typeName in new[] { "KSA.Loading", "KSA.ConfirmMods", "KSA.SelectSystem" })
            {
                var onFrame = SingleMethod(assembly.MainModule, typeName, "OnFrame");
                var calls = CallsOf(onFrame);
                Assert.True(CountCalls(calls, "Core.Renderer", "TryAcquireNextFrame") >= 1,
                    $"{typeName}.OnFrame must acquire frames on the shared renderer");
                Assert.True(CountCalls(calls, "Core.Renderer", "TrySubmitFrame") >= 1,
                    $"{typeName}.OnFrame must submit acquired frames");
                Assert.Equal(0, onFrame.Body.Instructions.Count(i =>
                    (i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Stsfld)
                    && i.Operand is FieldReference fr && fr.Name == "ResourceFrameIndex"));
            }
        }

        [RendererBaselineFact]
        public void UpdateShaderData_ScalesSunMeshFourMeanRadiusFromEgoPosition()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var update = SingleMethod(assembly.MainModule, "KSA.Program", "UpdateShaderData",
                "System.Double", "KSA.IViewport");
            var calls = CallsOf(update);

            Assert.True(CountCalls(calls, "KSA.Universe", "get_WorldSun") >= 1);
            Assert.True(CountCalls(calls, "KSA.Camera", "GetPositionEgo") >= 1);
            Assert.True(CountCalls(calls, "KSA.Rendering.Sun.SunRenderer", "UpdateShaderData") >= 1);
            Assert.True(update.Body.Instructions.Any(i =>
                i.OpCode == OpCodes.Ldc_R8 && i.Operand is double d && d == 4.0),
                "native CPU sun world must scale the shared sphere mesh by 4*MeanRadius");
        }

        [RendererBaselineFact]
        public void SunShaderAssets_PinInstancedInputsGlobalSunRadiusAndNoFragDepth()
        {
            string dir = Path.GetDirectoryName(BaselineContext.KsaAssemblyPath)!;
            string vert = File.ReadAllText(
                Path.Combine(dir, "Content", "Core", "Shaders", "Sun", "Sun.vert"));
            Assert.Contains("location = 3) in vec4 inInstanceData", vert);
            Assert.Contains("location = 4) in mat4", vert);
            Assert.Contains("ubo.projection * ubo.view", vert);

            string frag = File.ReadAllText(
                Path.Combine(dir, "Content", "Core", "Shaders", "Sun", "Sun.frag"));
            Assert.Contains("#define SET_GLOBAL 2", frag);
            Assert.Contains("discard;", frag);
            Assert.DoesNotContain("gl_FragDepth", frag);

            string rayMarch = File.ReadAllText(
                Path.Combine(dir, "Content", "Core", "Shaders", "RayMarching", "RayMarchingTest.glsl"));
            Assert.Contains("global.lighting.sunRadius", rayMarch);
        }

        [RendererBaselineFact]
        public void NativeSolarStructs_PinSequentialLayoutOffsets()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);

            var instanceData = assembly.MainModule.GetType("KSA.InstanceData");
            Assert.NotNull(instanceData);
            Assert.True(instanceData.IsSequentialLayout);
            Assert.Equal(1, (int)instanceData.PackingSize);
            Assert.Equal("data,model", string.Join(",", instanceData.Fields.Select(f => f.Name)));
            Assert.Equal("Brutal.Numerics.float4", instanceData.Fields[0].FieldType.FullName);
            Assert.Equal("Brutal.Numerics.float4x4", instanceData.Fields[1].FieldType.FullName);

            var ubo = assembly.MainModule.GetType("KSA.MeshRenderTechnique/Ubo");
            Assert.NotNull(ubo);
            Assert.True(ubo.IsSequentialLayout);
            Assert.Equal(1, (int)ubo.PackingSize);
            Assert.Equal("view,proj,time,exposure", string.Join(",", ubo.Fields.Select(f => f.Name)));
            Assert.Equal("Brutal.Numerics.float4x4", ubo.Fields[0].FieldType.FullName);
            Assert.Equal("Brutal.Numerics.float4x4", ubo.Fields[1].FieldType.FullName);
            Assert.Equal("System.Single", ubo.Fields[2].FieldType.FullName);
            Assert.Equal("System.Single", ubo.Fields[3].FieldType.FullName);
        }

        [RendererBaselineFact]
        public void UpdateRenderData_AnchorsMatchTranspilerExpectations()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var update = SingleMethod(assembly.MainModule, "KSA.StaticCelestialDistanceRendering",
                "UpdateRenderData", "KSA.IViewport", "System.Int32");
            Assert.True(update.IsStatic);
            Assert.Equal("System.Void", update.ReturnType.FullName);

            var calls = CallsOf(update);
            Assert.Equal(1, CountCalls(calls, "KSA.StaticCelestialDistanceRendering", "AppendStars"));
            Assert.Equal(1, CountCalls(calls, "KSA.Camera", "GetObjectDiameterPixels"));
            Assert.Equal(1, CountCalls(calls, "KSA.StaticCelestialDistanceRendering", "SpriteSphereBlend"));
            Assert.Equal(1, calls.Count(c => c.DeclaringType == "KSA.StaticCelestialDistanceRendering"
                && c.Name == "GetApparentSizeScale"));
            Assert.Equal(1, update.Body.Instructions.Count(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference mr && mr.Name == "Add"
                && mr.DeclaringType.Name == "DeviceVector"));
            Assert.Equal(1, update.Body.Instructions.Count(i =>
                i.OpCode == OpCodes.Isinst && i.Operand is TypeReference tr
                && tr.FullName == "KSA.IOrbiter"));
            Assert.True(update.Body.Instructions.Count(i =>
                i.OpCode == OpCodes.Isinst && i.Operand is TypeReference tr
                && tr.FullName == "KSA.StellarBody") >= 1,
                "the native nonstellar exclusion must remain");
        }

        [RendererBaselineFact]
        public void UpdateRenderingResources_PublishesGlobalsBeforeDistanceData()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var program = assembly.MainModule.GetType("KSA.Program");
            Assert.NotNull(program);

            var update = SingleMethod(assembly.MainModule, "KSA.Program", "UpdateRenderingResources",
                "System.Int32");
            var calls = CallsOf(update);
            int nextFrame = IndexOfCall(calls, "KSA.GlobalShaderBindings", "NextFrame");
            int camera = IndexOfCall(calls, "KSA.GlobalShaderBindings", "CameraData");
            int lighting = IndexOfCall(calls, "KSA.GlobalShaderBindings", "LightingData");
            int celestial = IndexOfCall(calls, "KSA.GlobalShaderBindings", "CelestialData");
            int vessel = IndexOfCall(calls, "KSA.GlobalShaderBindings", "VesselData");
            int distance = IndexOfCall(calls, "KSA.StaticCelestialDistanceRendering", "UpdateRenderData");
            Assert.True(nextFrame >= 0 && camera > nextFrame && lighting > nextFrame
                && celestial > nextFrame && vessel > nextFrame,
                "global UBO publication must follow GlobalShaderBindings.NextFrame");
            Assert.True(distance > camera && distance > lighting && distance > celestial
                && distance > vessel,
                "StaticCelestialDistanceRendering.UpdateRenderData must run after UBO publication");

            var driver = program.Methods.Single(m => m.HasBody
                && IndexOfCall(CallsOf(m), "KSA.Program", "UpdateRenderingResources") >= 0
                && IndexOfCall(CallsOf(m), "KSA.Program", "RenderGame") >= 0);
            var driverCalls = CallsOf(driver);
            Assert.True(
                IndexOfCall(driverCalls, "KSA.Program", "UpdateRenderingResources")
                < IndexOfCall(driverCalls, "KSA.Program", "RenderGame"),
                "UpdateRenderingResources must run before RenderGame in the frame driver");
        }
    }
}

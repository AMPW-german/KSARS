using KSARS.Visuals;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class MainAtlasTests
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

        private static List<string> LoadedStrings(MethodDefinition method) =>
            method.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string)
                .Select(i => (string)i.Operand)
                .ToList();

        private static List<int> IntConstants(MethodDefinition method) =>
            method.Body.Instructions
                .Select(i =>
                {
                    if (i.OpCode == OpCodes.Ldc_I4_M1) return -1;
                    if (i.OpCode == OpCodes.Ldc_I4_0) return 0;
                    if (i.OpCode == OpCodes.Ldc_I4_1) return 1;
                    if (i.OpCode == OpCodes.Ldc_I4_2) return 2;
                    if (i.OpCode == OpCodes.Ldc_I4_3) return 3;
                    if (i.OpCode == OpCodes.Ldc_I4_4) return 4;
                    if (i.OpCode == OpCodes.Ldc_I4_5) return 5;
                    if (i.OpCode == OpCodes.Ldc_I4_6) return 6;
                    if (i.OpCode == OpCodes.Ldc_I4_7) return 7;
                    if (i.OpCode == OpCodes.Ldc_I4_8) return 8;
                    if (i.OpCode == OpCodes.Ldc_I4_S) return (int)(sbyte)i.Operand;
                    if (i.OpCode == OpCodes.Ldc_I4) return (int)i.Operand;
                    return int.MinValue;
                })
                .Where(v => v != int.MinValue)
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

        private static MethodDefinition SingleMethod(ModuleDefinition module, string typeName, string methodName)
        {
            return TypeOf(module, typeName).Methods.Single(m => m.Name == methodName);
        }

        private static int CountCalls(List<(string DeclaringType, string Name)> calls, string declaringType, string name) =>
            calls.Count(c => c.DeclaringType == declaringType && c.Name == name);

        [Fact]
        public void Ownership_MarkPresented_OwnsBodyInSameToken()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var body = new object();
            ownership.BeginFrame(token);
            ownership.MarkPresented(token, new object[] { body });
            Assert.True(ownership.Owns(token, body));
            Assert.True(ownership.OwnsCurrent(body));
        }

        [Fact]
        public void Ownership_NewFrameToken_StaleBodiesNotOwned()
        {
            var ownership = new MainAtlasOwnership();
            var first = new object();
            var second = new object();
            var body = new object();
            ownership.BeginFrame(first);
            ownership.MarkPresented(first, new object[] { body });
            ownership.BeginFrame(second);
            Assert.False(ownership.Owns(first, body));
            Assert.False(ownership.Owns(second, body));
            Assert.False(ownership.OwnsCurrent(body));
        }

        [Fact]
        public void Ownership_MarkPresentedWithStaleToken_Ignored()
        {
            var ownership = new MainAtlasOwnership();
            var first = new object();
            var second = new object();
            var body = new object();
            ownership.BeginFrame(first);
            ownership.BeginFrame(second);
            ownership.MarkPresented(first, new object[] { body });
            Assert.False(ownership.Owns(second, body));
            Assert.False(ownership.OwnsCurrent(body));
        }

        [Fact]
        public void Ownership_FailedFrameWithoutMark_NotOwned()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var body = new object();
            ownership.BeginFrame(token);
            Assert.False(ownership.Owns(token, body));
            Assert.False(ownership.OwnsCurrent(body));
        }

        [Fact]
        public void Ownership_EmptyMark_NotOwned()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var body = new object();
            ownership.BeginFrame(token);
            ownership.MarkPresented(token, Array.Empty<object>());
            Assert.False(ownership.Owns(token, body));
            Assert.False(ownership.OwnsCurrent(body));
        }

        [Fact]
        public void Ownership_WrongBodyOrNullToken_NotOwned()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var body = new object();
            var other = new object();
            ownership.BeginFrame(token);
            ownership.MarkPresented(token, new object[] { body });
            Assert.False(ownership.Owns(token, other));
            Assert.False(ownership.Owns(null, body));
            Assert.False(ownership.OwnsCurrent(other));
        }

        [Fact]
        public void Ownership_Reset_ClearsOwnership()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var body = new object();
            ownership.BeginFrame(token);
            ownership.MarkPresented(token, new object[] { body });
            ownership.Reset();
            Assert.Null(ownership.CurrentToken);
            Assert.False(ownership.Owns(token, body));
            Assert.False(ownership.OwnsCurrent(body));
        }

        [Fact]
        public void Ownership_SameTokenBeginFrame_ClearsPresentedBodies()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var body = new object();
            ownership.BeginFrame(token);
            ownership.MarkPresented(token, new object[] { body });
            Assert.True(ownership.Owns(token, body));
            ownership.BeginFrame(token);
            Assert.False(ownership.Owns(token, body));
            Assert.False(ownership.OwnsCurrent(body));
        }

        [Fact]
        public void Ownership_ValueEqualDistinctObjects_NotOwned()
        {
            var ownership = new MainAtlasOwnership();
            string tokenA = new('t', 4);
            string tokenB = new('t', 4);
            string bodyA = new('b', 4);
            string bodyB = new('b', 4);
            Assert.NotSame(tokenA, tokenB);
            Assert.Equal(tokenA, tokenB);
            Assert.NotSame(bodyA, bodyB);
            Assert.Equal(bodyA, bodyB);
            ownership.BeginFrame(tokenA);
            ownership.MarkPresented(tokenA, new object[] { bodyA });
            Assert.True(ownership.Owns(tokenA, bodyA));
            Assert.False(ownership.Owns(tokenA, bodyB));
            Assert.False(ownership.Owns(tokenB, bodyA));
            ownership.MarkPresented(tokenB, new object[] { bodyB });
            Assert.False(ownership.Owns(tokenA, bodyB));
            Assert.False(ownership.OwnsCurrent(bodyB));
        }

        [Fact]
        public void SlotGuard_TryFindAcquiredSlot_ResolvesUniquePairIndependentOfAnyIndex()
        {
            var fences = new nint[] { 0x10, 0x20 };
            var commands = new nint[] { 0x30, 0x40 };
            Assert.True(MainAtlasSlotGuard.TryFindAcquiredSlot(fences, commands, 0x10, 0x30, out int first, out _));
            Assert.Equal(0, first);
            Assert.True(MainAtlasSlotGuard.TryFindAcquiredSlot(fences, commands, 0x20, 0x40, out int second, out _));
            Assert.Equal(1, second);
        }

        [Fact]
        public void SlotGuard_TryFindAcquiredSlot_ArbitraryOrderPairIdentity()
        {
            var fences = new nint[] { 0xAA, 0xBB, 0xCC };
            var commands = new nint[] { 0x11, 0x22, 0x33 };
            Assert.True(MainAtlasSlotGuard.TryFindAcquiredSlot(fences, commands, 0xCC, 0x33, out int index, out _));
            Assert.Equal(2, index);
            Assert.True(MainAtlasSlotGuard.TryFindAcquiredSlot(fences, commands, 0xAA, 0x11, out int first, out _));
            Assert.Equal(0, first);
        }

        [Fact]
        public void SlotGuard_TryFindAcquiredSlot_RejectsMissingZeroAmbiguousMismatchedAndWrongCommand()
        {
            var fences = new nint[] { 0x10, 0x20 };
            var commands = new nint[] { 0x30, 0x40 };
            Assert.False(MainAtlasSlotGuard.TryFindAcquiredSlot(fences, commands, 0x50, 0x30, out _, out string missing));
            Assert.Contains("not present", missing);
            Assert.False(MainAtlasSlotGuard.TryFindAcquiredSlot(fences, commands, 0, 0x30, out _, out string zeroFence));
            Assert.Contains("zero", zeroFence);
            Assert.False(MainAtlasSlotGuard.TryFindAcquiredSlot(fences, commands, 0x10, 0, out _, out _));
            Assert.False(MainAtlasSlotGuard.TryFindAcquiredSlot(
                new nint[] { 0x10, 0x10 }, new nint[] { 0x30, 0x30 }, 0x10, 0x30, out _, out string duplicate));
            Assert.Contains("multiple", duplicate);
            Assert.False(MainAtlasSlotGuard.TryFindAcquiredSlot(
                fences, new nint[] { 0x30 }, 0x10, 0x30, out _, out _));
            Assert.False(MainAtlasSlotGuard.TryFindAcquiredSlot(fences, commands, 0x10, 0x40, out _, out _));
            Assert.False(MainAtlasSlotGuard.TryFindAcquiredSlot(
                Array.Empty<nint>(), Array.Empty<nint>(), 0x10, 0x30, out _, out _));
        }

        [Fact]
        public void MainPassRecord_NeverBeginsOrEndsRendering()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = SingleMethod(assembly.MainModule, "KSARS.Visuals.AtlasMainPass", "Record");
            var calls = CallsOf(record);

            Assert.Equal(0, calls.Count(c => c.Name == "BeginRendering" || c.Name == "EndRendering"));
            Assert.Equal(1, CountCalls(calls, "KSA.Program", "SetViewport"));
            Assert.Equal(1, calls.Count(c => c.Name == "Draw"));
            Assert.Equal(1, calls.Count(c => c.Name == "BindDescriptorSets"));
            Assert.Equal(1, calls.Count(c => c.Name == "PushConstants"));
            Assert.Equal(1, calls.Count(c => c.Name == "BindPipeline"));
            Assert.True(calls.Count(c => c.DeclaringType == "KSA.GlobalShaderBindings" && c.Name == "DescriptorSet") >= 1);
            Assert.Equal(0, CountCalls(calls, "KSA.Program", "RenderGame"));
            Assert.Equal(0, CountCalls(calls, "KSA.Program", "RenderViewport"));
            Assert.Equal(0, calls.Count(c => c.DeclaringType == "KSA.ViewportRegistry"));
            Assert.Equal(0, calls.Count(c => c.Name.Contains("WaitIdle")));
        }

        private static bool IsNativeType(string declaringType) =>
            declaringType.StartsWith("KSA.") || declaringType.StartsWith("RenderCore")
                || declaringType.StartsWith("Core.");

        [Fact]
        public void MainPass_NeverWritesNativeFieldsOrIdlesDevice()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, "KSARS.Visuals.AtlasMainPass");
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                Assert.DoesNotContain(FieldStoresOf(method), IsNativeType);
            }

            var calls = AllCallsOf(type);
            Assert.Equal(0, calls.Count(c => c.Name.Contains("WaitIdle")));
            Assert.Equal(0, calls.Count(c => c.Name == "BeginRendering" || c.Name == "EndRendering"));
        }

        [Fact]
        public void MainPassPipeline_UsesReverseDepthNoWriteAndPremultipliedBlend()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var build = SingleMethod(assembly.MainModule, "KSARS.Visuals.AtlasMainPass", "BuildPipeline");
            var refs = FieldRefsOf(build);
            var calls = CallsOf(build);

            Assert.Contains(refs, f => f.DeclaringType.EndsWith("ReverseZDepthStencil") && f.Name == "DepthTestNoWrite");
            Assert.Contains(refs, f => f.DeclaringType == "KSARS.Visuals.AtlasMainPass" && f.Name == "_premulBlend");
            Assert.DoesNotContain(refs, f => f.Name == "BlendNone");
            Assert.Contains(refs, f => f.DeclaringType.EndsWith("Fill") && f.Name == "CullNone");
            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.RenderTarget", "SetupGraphicsPipeline"));
        }

        [Fact]
        public void MainPassSyncSources_WritesDepthDescriptorsWithPointSampler()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var sync = SingleMethod(assembly.MainModule, "KSARS.Visuals.AtlasMainPass", "SyncSources");
            var calls = CallsOf(sync);

            Assert.Equal(1, CountCalls(calls, "KSA.Program", "get_PointClampedSampler"));
            Assert.Equal(1, CountCalls(calls, "KSA.Program", "get_LinearClampedSampler"));
            Assert.Equal(2, CountCalls(calls, "KSARS.Visuals.AtlasMainPass", "WriteImageDescriptorArray"));

            var ints = IntConstants(sync);
            Assert.Contains(1, ints);
            Assert.Contains(2, ints);
            Assert.Contains(3, ints);
        }

        [Fact]
        public void MainPassFragment_DepthAwareRadialCompositePinned()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, "KSARS.Visuals.AtlasMainPass");
            string source = (string)type.Fields.Single(f => f.Name == "FragmentSource").Constant!;

            Assert.Contains("vec4(inUv * 2.0 - 1.0, 1.0, 1.0)", source);
            Assert.Contains("normalize(viewPos.xyz)", source);
            Assert.DoesNotContain("/ viewPos.w", source);
            Assert.Contains("texelFetch", source);
            Assert.Contains("depthTiles", source);
            Assert.Contains("colorTiles", source);
            Assert.Contains("depth > 0.0 && depth <= 1.0", source);
            Assert.Contains("texelFetch(depthTiles[i], coord, 0).r", source);
            Assert.Contains("sourceZ = b / (depth + a)", source);
            Assert.Contains("sqrt(1.0 + dot(rayXY, rayXY))", source);
            Assert.Contains("lessThan(uv, vec2(0.0))", source);
            Assert.Contains("radial < bestRadial", source);
            Assert.Contains("discard;", source);
            Assert.Contains("min(max(rgb, vec3(0.0)), vec3(65504.0))", source);
            Assert.Contains("vec4(rgb, alpha)", source);
            Assert.DoesNotContain("upCos", source);
            Assert.DoesNotContain("cos(", source);
            Assert.DoesNotContain("fwdDist", source);
        }

        [Fact]
        public void ContextWriteSnapshot_MapsOnlyOwnedBuffers()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var write = SingleMethod(assembly.MainModule, "KSARS.Visuals.ObjectCaptureContext", "WriteSnapshot");

            string[] expected =
            {
                "KSA.UboCameraData", "KSA.UboLightingData", "KSA.UboCelestialData",
                "KSA.UboVesselData", "KSA.UboGlobalConstants", "KSA.DistantSphereMaterialData",
            };
            Assert.Equal(expected.Length, write.Parameters.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.True(write.Parameters[i].ParameterType.IsByReference,
                    $"parameter {i} must be an in-reference");
                Assert.Equal(expected[i], write.Parameters[i].ParameterType.GetElementType().FullName);
            }

            var calls = CallsOf(write);
            Assert.Equal(3, calls.Count(c => c.Name == "Map"));
            Assert.Equal(3, calls.Count(c => c.Name == "Unmap"));
            Assert.Equal(0, calls.Count(c => c.Name.Contains("DescriptorSet") || c.Name.Contains("UpdateDescriptorSets")));
            Assert.Equal(0, calls.Count(c => c.Name.Contains("WaitIdle")));
            Assert.DoesNotContain(FieldStoresOf(write), d => !d.StartsWith("KSARS."));
        }

        [Fact]
        public void CoordinatorTranspiler_RewiresAllFiveAnchors()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var transpile = SingleMethod(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer", "TranspileMainRenderGame");
            var refs = FieldRefsOf(transpile);
            foreach (string field in new[]
            {
                "s_nativeClear", "s_nativeRun", "s_nativeSphere",
                "s_wrapper", "s_runWrapper", "s_sphereWrapper",
                "s_nativeSunDraw", "s_sunDrawWrapper",
                "s_nativeSunbloom", "s_sunbloomWrapper",
            })
            {
                Assert.Contains(refs, r => r.DeclaringType == "KSARS.Visuals.RelativisticSpaceRenderer" && r.Name == field);
            }
        }

        [Fact]
        public void CoordinatorPrepare_ResolvesMainAnchorsAndWrappersByName()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer", "PrepareProbe");
            var strings = LoadedStrings(prepare);

            Assert.Contains("RunNativeDistanceAndComposite", strings);
            Assert.Contains("RenderNativeSphereOrAtlas", strings);
            Assert.Contains("QueueNativeSunOrAtlas", strings);
            Assert.Contains("RenderSunbloomOrAtlas", strings);
        }

        [Fact]
        public void RunWrapper_CallsNativeRunBeforeComposite()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var wrapper = SingleMethod(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer", "RunNativeDistanceAndComposite");

            Assert.True(wrapper.IsStatic);
            Assert.Equal(
                new[] { "Brutal.VulkanApi.CommandBuffer", "KSA.IViewport", "System.Int32" },
                wrapper.Parameters.Select(p => p.ParameterType.FullName).ToArray());

            var calls = CallsOf(wrapper);
            Assert.Equal(1, CountCalls(calls, "KSA.StaticCelestialDistanceRendering", "Run"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.RelativisticSpaceRenderer", "TryRecordMainComposite"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.RelativisticSpaceRenderer", "FlushDeferredNativeSun"));
            int runIndex = calls.FindIndex(c => c.DeclaringType == "KSA.StaticCelestialDistanceRendering" && c.Name == "Run");
            int compositeIndex = calls.FindIndex(c => c.Name == "TryRecordMainComposite");
            int flushIndex = calls.FindIndex(c => c.Name == "FlushDeferredNativeSun");
            Assert.True(runIndex < compositeIndex && compositeIndex < flushIndex,
                "native distance, then composite, then the deferred native Sun flush");
        }

        [Fact]
        public void SphereWrapper_SuppressesOnlyViaOwnershipAndClearsFlag()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var wrapper = SingleMethod(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer", "RenderNativeSphereOrAtlas");

            Assert.True(wrapper.IsStatic);
            Assert.Equal(
                new[] { "KSA.StaticCelestial", "KSA.IViewport", "Brutal.VulkanApi.CommandBuffer", "System.Int32" },
                wrapper.Parameters.Select(p => p.ParameterType.FullName).ToArray());

            var calls = CallsOf(wrapper);
            Assert.Equal(1, CountCalls(calls, "KSA.StaticCelestial", "RenderSphere"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.MainAtlasOwnership", "Owns"));
            Assert.Equal(0, CountCalls(calls, "KSARS.Visuals.MainAtlasOwnership", "OwnsCurrent"));
            Assert.True(calls.Count(c => c.DeclaringType == "KSA.Program" && c.Name == "get_MainViewport") >= 1);
            Assert.True(calls.Count(c => c.DeclaringType == "KSA.Program" && c.Name == "get_RenderedViewport") >= 1);
            Assert.Contains(FieldRefsOf(wrapper),
                r => r.DeclaringType == "KSA.StaticCelestial" && r.Name == "RenderSphereThisFrame");
            foreach (string field in new[] { "s_mainProbeEnabled", "s_mainFailed", "s_lastSnapshot" })
            {
                Assert.Contains(FieldRefsOf(wrapper),
                    r => r.DeclaringType == "KSARS.Visuals.RelativisticSpaceRenderer" && r.Name == field);
            }

            Assert.DoesNotContain(FieldStoresOf(wrapper), d => IsNativeType(d)
                && !d.StartsWith("KSA.StaticCelestial"));
        }

        [Fact]
        public void CoordinatorMainPaths_NeverStoreNativeFieldsOrIdleDevice()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var module = assembly.MainModule;
            foreach (string name in new[]
            {
                "PrepareMainFrameSources", "TryRecordMainComposite",
                "RunNativeDistanceAndComposite", "RenderNativeSphereOrAtlas",
            })
            {
                var method = SingleMethod(module, "KSARS.Visuals.RelativisticSpaceRenderer", name);
                Assert.DoesNotContain(FieldStoresOf(method), d => IsNativeType(d)
                    && !d.StartsWith("KSA.StaticCelestial"));
                Assert.Equal(0, CallsOf(method).Count(c => c.Name.Contains("WaitIdle")));
            }
        }

        [Fact]
        public void CoordinatorPrepareMain_ReusesSlotBuffersAndFreshDrawData()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer", "PrepareMainFrameSources");
            var calls = CallsOf(prepare);

            Assert.True(CountCalls(calls, "KSARS.Visuals.ObjectCaptureContext", "WriteSnapshot") >= 1);
            Assert.True(CountCalls(calls, "KSARS.Visuals.NativeCelestialCaptureAdapter", "Record") >= 1);
            Assert.True(CountCalls(calls, "KSARS.Visuals.ObjectCaptureContext", ".ctor") >= 1);
            Assert.True(CountCalls(calls, "KSARS.Visuals.AtlasMainPass", ".ctor") >= 1);
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.MainAtlasOwnership", "BeginFrame"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.AtlasMainPass", "SyncSources"));
        }

        [Fact]
        public void CoordinatorShutdown_DisposesMainFramesAndWaitsIdle()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var shutdown = SingleMethod(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer", "Shutdown");
            var refs = FieldRefsOf(shutdown);
            var calls = CallsOf(shutdown);

            Assert.Contains(refs, r => r.Name == "s_mainFrames");
            Assert.True(calls.Count(c => c.Name.Contains("WaitIdle")) >= 1);
            Assert.True(calls.Count(c => c.DeclaringType.EndsWith("MainAtlasFrame") && c.Name == "Dispose") >= 1);
        }

        [Fact]
        public void SlotValidation_MatchesFenceAndCommandBufferHandles()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var validate = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "TryResolveMainSyncSlot");
            var calls = CallsOf(validate);
            var refs = FieldRefsOf(validate);

            Assert.Equal(1, CountCalls(calls, "Core.Renderer", "get_AllSyncObjects"));
            Assert.Contains(refs, r => r.DeclaringType == "Core.Renderer" && r.Name == "MaxFramesInFlight");
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.MainAtlasSlotGuard", "TryFindAcquiredSlot"));
            Assert.Equal(0, CountCalls(calls, "KSARS.Visuals.MainAtlasSlotGuard", "SyncSlotIndex"));
            Assert.True(calls.Count(c => c.Name == "get_VkHandle") >= 4,
                "acquired fence and command buffer plus the ring handle pair must all be read");
            Assert.Contains(refs,
                r => r.DeclaringType == "KSARS.Visuals.RelativisticSpaceRenderer" && r.Name == "s_getSyncObjects");
            Assert.Contains(refs,
                r => r.DeclaringType == "KSARS.Visuals.RelativisticSpaceRenderer" && r.Name == "s_mainRing");
            Assert.Contains(refs,
                r => r.DeclaringType == "Brutal.VulkanApi.Abstractions.SyncObjects" && r.Name == "Fence");
            Assert.Contains(refs,
                r => r.DeclaringType == "Brutal.VulkanApi.Abstractions.SyncObjects" && r.Name == "CommandBuffer");
            Assert.Contains(calls, c => c.Name == "ResolveSyncObjectsAccessor");
            Assert.Contains(calls, c => c.Name == "Invoke");
        }

        [Fact]
        public void PrepareMainFrameSources_ValidatesSlotBeforeAnyWritesOrAllocations()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "PrepareMainFrameSources");
            var instructions = prepare.Body.Instructions;
            int validateIndex = instructions.ToList().FindIndex(i =>
                i.Operand is MethodReference mr && mr.Name == "TryResolveMainSyncSlot");
            Assert.True(validateIndex >= 0, "PrepareMainFrameSources must call TryResolveMainSyncSlot");

            var calls = CallsOf(prepare);
            int validateCall = calls.FindIndex(c => c.Name == "TryResolveMainSyncSlot");
            foreach (string name in new[] { "WriteSnapshot", "Record", "SyncSources", "Reset", "BeginFrame" })
            {
                int call = calls.FindIndex(c => c.Name == name);
                Assert.True(call > validateCall,
                    $"{name} must only run after slot validation (validate={validateCall} {name}={call})");
            }

            foreach (string name in new[] { ".ctor" })
            {
                int call = calls.FindIndex(c => c.Name == name);
                Assert.True(call > validateCall,
                    $"first resource construction must only run after slot validation (validate={validateCall} ctor={call})");
            }

            int firstNewArr = instructions.ToList().FindIndex(i => i.OpCode == OpCodes.Newarr);
            Assert.True(firstNewArr > validateIndex,
                "ring array allocation must only run after slot validation");
        }

        [Fact]
        public void MainFrameDispose_AttemptsEveryResourceAndAggregates()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var frame = TypeOf(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer/MainAtlasFrame");
            var dispose = frame.Methods.Single(m => m.Name == "Dispose");
            var calls = CallsOf(dispose);

            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.AtlasMainPass", "Dispose"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.SolarCoverageProbe", "Dispose"));
            Assert.Equal(2, CountCalls(calls, "RenderCore.RenderTechnique", "Dispose"));
            Assert.Equal(2, CountCalls(calls, "KSARS.Visuals.ObjectCaptureContext", "Dispose"));
            Assert.True(dispose.Body.ExceptionHandlers.Count >= 6,
                "each pass/solar-adapter/solar-coverage/context disposal needs an independent handler so one failure cannot skip the rest");
            Assert.Equal(1, CountCalls(calls, "System.AggregateException", ".ctor"));
            Assert.Equal(0, calls.Count(c => c.Name.Contains("WaitIdle")));
        }

        [Fact]
        public void StatusStrings_LabelMainProbeStateAndLimits()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var module = assembly.MainModule;
            var type = TypeOf(module, "KSARS.Visuals.RelativisticSpaceRenderer");
            var stateGetter = type.Methods.Single(m => m.Name == "get_MainViewState");
            var stateStrings = LoadedStrings(stateGetter);
            Assert.Contains(stateStrings, s => s.Contains("main view stock"));
            Assert.Contains(stateStrings, s => s.Contains("65504"));
            Assert.Contains(stateStrings, s => s.Contains("6px"));
            Assert.Contains(stateStrings, s => s.Contains("resolved bodies"));

            var record = SingleMethod(module,
                "KSARS.Visuals.RelativisticSpaceRenderer", "TryRecordMainComposite");
            var recordStrings = LoadedStrings(record);
            Assert.Contains(recordStrings, s => s.Contains("main view override active"));
            Assert.Contains(recordStrings, s => s.Contains("65504"));
            Assert.Contains(recordStrings, s => s.Contains("OBSERVER OVERRIDE"));

            var allStrings = type.Methods.Where(m => m.HasBody).SelectMany(LoadedStrings).ToList();
            Assert.Contains(allStrings, s => s.Contains("mapped sync-slot mismatch"));
            Assert.Contains(allStrings, s => s.Contains("no slot writes occurred"));
            Assert.Contains(allStrings, s => s.Contains("not a production renderer"));
            Assert.Contains(allStrings, s => s.Contains("syncSlot="));
            Assert.Contains(allStrings, s => s.Contains("resourceFrame="));
        }

        [Fact]
        public void MainPassValidateState_ThrowsTypedTargetChangedException()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var pass = TypeOf(assembly.MainModule, "KSARS.Visuals.AtlasMainPass");
            var nested = pass.NestedTypes.Single(t => t.Name == "RendererTargetChangedException");
            Assert.Equal("System.InvalidOperationException", nested.BaseType.FullName);

            var validate = pass.Methods.Single(m => m.Name == "ValidateState");
            Assert.Contains(validate.Body.Instructions, i =>
                i.OpCode == OpCodes.Newobj && i.Operand is MethodReference mr
                    && mr.DeclaringType.Name == "RendererTargetChangedException");
            Assert.DoesNotContain(validate.Body.Instructions, i =>
                i.OpCode == OpCodes.Newobj && i.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "System.InvalidOperationException");
        }

        [Fact]
        public void TryRecordMainComposite_UsesPreparedFrameNotResourceIndex()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "TryRecordMainComposite");
            var refs = FieldRefsOf(record);

            Assert.Contains(refs,
                r => r.DeclaringType == "KSARS.Visuals.RelativisticSpaceRenderer" && r.Name == "s_preparedMainFrame");
            Assert.DoesNotContain(refs,
                r => r.DeclaringType == "KSARS.Visuals.RelativisticSpaceRenderer" && r.Name == "s_mainFrames");
            Assert.Contains(CallsOf(record),
                c => c.DeclaringType == "KSARS.Visuals.ObserverSnapshot" && c.Name == "get_ResourceFrameIndex");
            Assert.Contains(refs,
                r => r.DeclaringType == "KSARS.Visuals.RelativisticSpaceRenderer" && r.Name == "s_lastSnapshot");
        }

        [Fact]
        public void ClearDepthBoundary_ClearsPreparedMainFrameBeforeEarlyExits()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var clear = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "ClearDepthAndRecordSource");
            int store = clear.Body.Instructions.ToList().FindIndex(i =>
                i.OpCode == OpCodes.Stsfld
                && i.Operand is FieldReference { Name: "s_preparedMainFrame" });
            int firstRet = clear.Body.Instructions.ToList().FindIndex(i => i.OpCode == OpCodes.Ret);
            Assert.True(store >= 0, "ClearDepthAndRecordSource must clear s_preparedMainFrame");
            Assert.True(firstRet < 0 || store < firstRet,
                "s_preparedMainFrame must be cleared before any return path");
        }

        [Fact]
        public void Ownership_MarkCulled_OwnsCulledBodyInSameToken()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var body = new object();
            ownership.BeginFrame(token);
            ownership.MarkCulled(token, new object[] { body });
            Assert.True(ownership.Owns(token, body));
            Assert.True(ownership.OwnsCurrent(body));
            Assert.Contains(body, ownership.CulledBodies);
        }

        [Fact]
        public void Ownership_MarkCulledWithStaleToken_Ignored()
        {
            var ownership = new MainAtlasOwnership();
            var first = new object();
            var second = new object();
            var body = new object();
            ownership.BeginFrame(first);
            ownership.BeginFrame(second);
            ownership.MarkCulled(first, new object[] { body });
            Assert.False(ownership.Owns(second, body));
            Assert.False(ownership.OwnsCurrent(body));
            Assert.Empty(ownership.CulledBodies);
        }

        [Fact]
        public void Ownership_MarkCulledWithNullTokenAfterReset_Ignored()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var body = new object();
            ownership.BeginFrame(token);
            ownership.Reset();
            ownership.MarkCulled(null!, new object[] { body });
            Assert.Empty(ownership.CulledBodies);
            Assert.False(ownership.OwnsCurrent(body));
        }

        [Fact]
        public void Ownership_MarkPresented_ReplacesPresentedKeepsCulled()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var firstPresented = new object();
            var secondPresented = new object();
            var culled = new object();
            ownership.BeginFrame(token);
            ownership.MarkCulled(token, new object[] { culled });
            ownership.MarkPresented(token, new object[] { firstPresented });
            Assert.True(ownership.Owns(token, firstPresented));
            Assert.True(ownership.Owns(token, culled));
            ownership.MarkPresented(token, new object[] { secondPresented });
            Assert.False(ownership.Owns(token, firstPresented));
            Assert.True(ownership.Owns(token, secondPresented));
            Assert.True(ownership.Owns(token, culled));
            Assert.Contains(culled, ownership.CulledBodies);
        }

        [Fact]
        public void Ownership_MarkCulled_ReplacesOnlyCulled()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var presented = new object();
            var firstCulled = new object();
            var secondCulled = new object();
            ownership.BeginFrame(token);
            ownership.MarkPresented(token, new object[] { presented });
            ownership.MarkCulled(token, new object[] { firstCulled });
            Assert.True(ownership.Owns(token, presented));
            Assert.True(ownership.Owns(token, firstCulled));
            ownership.MarkCulled(token, new object[] { secondCulled });
            Assert.True(ownership.Owns(token, presented));
            Assert.False(ownership.Owns(token, firstCulled));
            Assert.True(ownership.Owns(token, secondCulled));
            Assert.DoesNotContain(firstCulled, ownership.CulledBodies);
            Assert.Contains(secondCulled, ownership.CulledBodies);
        }

        [Fact]
        public void Ownership_BeginFrameAndReset_ClearCulled()
        {
            var ownership = new MainAtlasOwnership();
            var first = new object();
            var second = new object();
            var body = new object();
            ownership.BeginFrame(first);
            ownership.MarkCulled(first, new object[] { body });
            ownership.BeginFrame(first);
            Assert.False(ownership.Owns(first, body));
            Assert.Empty(ownership.CulledBodies);
            ownership.MarkCulled(first, new object[] { body });
            ownership.BeginFrame(second);
            Assert.False(ownership.Owns(second, body));
            Assert.Empty(ownership.CulledBodies);
            ownership.MarkCulled(second, new object[] { body });
            ownership.Reset();
            Assert.Null(ownership.CurrentToken);
            Assert.False(ownership.OwnsCurrent(body));
            Assert.Empty(ownership.CulledBodies);
        }

        [Fact]
        public void Ownership_ValueEqualDistinctCulledObjects_NotOwned()
        {
            var ownership = new MainAtlasOwnership();
            string tokenA = new('t', 4);
            string tokenB = new('t', 4);
            string bodyA = new('b', 4);
            string bodyB = new('b', 4);
            Assert.NotSame(tokenA, tokenB);
            Assert.NotSame(bodyA, bodyB);
            Assert.Equal(bodyA, bodyB);
            ownership.BeginFrame(tokenA);
            ownership.MarkCulled(tokenA, new object[] { bodyA });
            Assert.True(ownership.Owns(tokenA, bodyA));
            Assert.False(ownership.Owns(tokenA, bodyB));
            Assert.False(ownership.Owns(tokenB, bodyA));
            ownership.MarkCulled(tokenB, new object[] { bodyB });
            Assert.False(ownership.Owns(tokenA, bodyB));
            Assert.False(ownership.OwnsCurrent(bodyB));
        }

        [Fact]
        public void CulledClassification_RearwardTransformedCapOutsideView_OwnsViaCulledOnly()
        {
            var restDirection = new OpticalVector(1.0, 0.0, 0.0);
            var viewAxis = new OpticalVector(1.0, 0.0, 0.0);
            const double viewCorner = 0.1;
            const double restAngularRadius = 0.005;

            Assert.True(RelativisticOptics.TryCreateVelocity(
                new OpticalVector(0.0, 0.0, 0.9998 * TestOptics.C), out PresentationVelocity boosted));
            Assert.True(RelativisticOptics.TryTransformSphericalCap(restDirection, restAngularRadius,
                boosted, out ObservedSphericalCap boostedCap));
            double boostedCombined = boostedCap.AngularRadius + viewCorner;
            Assert.False(
                boostedCombined >= Math.PI || boostedCap.Axis.Dot(viewAxis) >= Math.Cos(boostedCombined),
                "the boosted cap must fall outside the view cone to exercise the culled classification");

            var ownership = new MainAtlasOwnership();
            var snapshot = new object();
            var body = new object();
            ownership.BeginFrame(snapshot);
            Assert.False(ownership.Owns(snapshot, body));
            ownership.MarkCulled(snapshot, new object[] { body });
            Assert.True(ownership.Owns(snapshot, body));

            Assert.True(RelativisticOptics.TryTransformSphericalCap(restDirection, restAngularRadius,
                PresentationVelocity.Rest, out ObservedSphericalCap restCap));
            double restCombined = restCap.AngularRadius + viewCorner;
            Assert.True(
                restCombined >= Math.PI || restCap.Axis.Dot(viewAxis) >= Math.Cos(restCombined),
                "the same body at q=1 must intersect the view cone");

            var restOwnership = new MainAtlasOwnership();
            restOwnership.BeginFrame(snapshot);
            Assert.False(restOwnership.Owns(snapshot, body),
                "a visible body must not be owned before it is presented");
            restOwnership.MarkCulled(snapshot, Array.Empty<object>());
            Assert.False(restOwnership.Owns(snapshot, body));
            restOwnership.MarkPresented(snapshot, new object[] { body });
            Assert.True(restOwnership.Owns(snapshot, body));
        }

        [Fact]
        public void CulledBodies_IsReadOnlyObjectEnumerable()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var ownershipType = TypeOf(assembly.MainModule, "KSARS.Visuals.MainAtlasOwnership");
            var property = ownershipType.Properties.Single(p => p.Name == "CulledBodies");
            Assert.Null(property.SetMethod);
            Assert.Equal("System.Collections.Generic.IEnumerable`1<System.Object>",
                property.PropertyType.FullName);
        }

        [Fact]
        public void SourceCandidate_ObservedVisibility_ExplicitlyHandledByBothCallers()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var module = assembly.MainModule;
            var candidate = TypeOf(module, "KSARS.Visuals.RelativisticSpaceRenderer/SourceCandidate");
            var property = candidate.Properties.SingleOrDefault(p => p.Name == "IsObservedVisible");
            Assert.NotNull(property);
            Assert.Null(property.SetMethod);

            var builder = SingleMethod(module, "KSARS.Visuals.RelativisticSpaceRenderer",
                "TryBuildSourceCandidate");
            Assert.Contains(CallsOf(builder),
                c => c.DeclaringType == "KSARS.Visuals.RelativisticOptics"
                    && c.Name == "TryTransformSphericalCap");

            foreach (string caller in new[] { "TrySelectAndCapture", "PrepareMainFrameSources" })
            {
                var method = SingleMethod(module, "KSARS.Visuals.RelativisticSpaceRenderer", caller);
                Assert.Contains(CallsOf(method),
                    c => c.DeclaringType.EndsWith("/SourceCandidate") && c.Name == "get_IsObservedVisible");
            }
        }

        [Fact]
        public void CoordinatorMainPrep_CullsSupportedInvisibleBeforeGpuWorkAndCapacityCheck()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "PrepareMainFrameSources");
            var instructions = prepare.Body.Instructions.ToList();

            int buildIndex = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.Name == "TryBuildSourceCandidate");
            int visibilityIndex = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.Name == "get_IsObservedVisible");
            int culledIndex = instructions.FindIndex(i =>
                i.Operand is FieldReference fr && fr.Name == "CulledBodies");
            int contextIndex = instructions.FindIndex(i =>
                i.OpCode == OpCodes.Newobj && i.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "KSARS.Visuals.ObjectCaptureContext");
            int budgetIndex = instructions.FindIndex(i => i.OpCode == OpCodes.Ldc_I4_2);

            Assert.True(buildIndex >= 0 && visibilityIndex > buildIndex,
                "full candidate validation must complete before observed-visibility classification");
            Assert.True(culledIndex > visibilityIndex,
                "supported invisible bodies must be classified culled");
            Assert.True(budgetIndex > culledIndex,
                "the resolved-body capacity check must follow culled classification so scanning continues");
            Assert.True(contextIndex > culledIndex,
                "culled bodies must be classified before any source GPU resource creation");
        }

        [Fact]
        public void CoordinatorMainPrep_PublishesCulledOnlyAfterSuccessfulPreparation()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "PrepareMainFrameSources");
            var calls = CallsOf(prepare);

            var markIndices = calls.Select((c, i) => (c, i))
                .Where(x => x.c.DeclaringType == "KSARS.Visuals.MainAtlasOwnership" && x.c.Name == "MarkCulled")
                .Select(x => x.i).ToList();
            Assert.Equal(2, markIndices.Count);

            int recordIndex = calls.FindIndex(c =>
                c.DeclaringType == "KSARS.Visuals.NativeCelestialCaptureAdapter" && c.Name == "Record");
            Assert.True(recordIndex >= 0);
            Assert.All(markIndices, i => Assert.True(i > recordIndex,
                "culled ownership must publish only after the source-recording loop completes"));

            int syncIndex = calls.FindIndex(c =>
                c.DeclaringType == "KSARS.Visuals.AtlasMainPass" && c.Name == "SyncSources");
            Assert.True(syncIndex >= 0);
            Assert.True(markIndices.Any(i => i < syncIndex),
                "the zero-resolved-body branch must publish culled ownership before its early return");
            Assert.True(markIndices.Any(i => i > syncIndex),
                "the recorded-source branch must publish culled ownership after SyncSources succeeds");
        }

        [Fact]
        public void CoordinatorMainPrep_CulledStatusLabelsSuppressionNotStock()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "PrepareMainFrameSources");
            var strings = LoadedStrings(prepare);
            Assert.Contains(strings,
                s => s.Contains("outside observed view") && s.Contains("native sphere suppressed"));
        }

        [Fact]
        public void SphereWrapper_CulledBodiesFlowThroughSameOwnershipGuards()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var wrapper = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "RenderNativeSphereOrAtlas");
            var calls = CallsOf(wrapper);

            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.MainAtlasOwnership", "Owns"));
            Assert.Equal(0, calls.Count(c => c.DeclaringType == "KSARS.Visuals.MainAtlasOwnership"
                && c.Name != "Owns"));
            Assert.DoesNotContain(FieldRefsOf(wrapper), r => r.Name == "CulledBodies");
            Assert.Contains(FieldRefsOf(wrapper), r =>
                r.DeclaringType == "KSARS.Visuals.RelativisticSpaceRenderer" && r.Name == "s_mainProbeEnabled");
            Assert.Contains(FieldRefsOf(wrapper), r =>
                r.DeclaringType == "KSARS.Visuals.RelativisticSpaceRenderer" && r.Name == "s_mainFailed");
        }

        [Fact]
        public void GetStatus_ReportsCulledOwnershipSeparatelyFromPresented()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var status = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "GetStatus");
            var strings = LoadedStrings(status);
            Assert.Contains(strings, s => s.Contains("mainCulled=["));
            Assert.Contains(strings, s => s.Contains("mainCulled=none"));
            Assert.Contains(strings, s => s.Contains("mainBodies="));
            Assert.Contains(CallsOf(status), c =>
                c.DeclaringType == "KSARS.Visuals.MainAtlasOwnership" && c.Name == "get_CulledBodies");
        }

        [Fact]
        public void Ownership_MarkOmitted_OwnsOmittedBodyInSameToken()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var body = new object();
            ownership.BeginFrame(token);
            ownership.MarkOmitted(token, new object[] { body });
            Assert.True(ownership.Owns(token, body));
            Assert.True(ownership.OwnsCurrent(body));
            Assert.Contains(body, ownership.OmittedBodies);
            Assert.Empty(ownership.CulledBodies);
        }

        [Fact]
        public void Ownership_MarkOmittedWithStaleToken_Ignored()
        {
            var ownership = new MainAtlasOwnership();
            var first = new object();
            var second = new object();
            var body = new object();
            ownership.BeginFrame(first);
            ownership.BeginFrame(second);
            ownership.MarkOmitted(first, new object[] { body });
            Assert.False(ownership.Owns(second, body));
            Assert.False(ownership.OwnsCurrent(body));
            Assert.Empty(ownership.OmittedBodies);
        }

        [Fact]
        public void Ownership_MarkOmittedWithNullTokenAfterReset_Ignored()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var body = new object();
            ownership.BeginFrame(token);
            ownership.Reset();
            ownership.MarkOmitted(null!, new object[] { body });
            Assert.Empty(ownership.OmittedBodies);
            Assert.False(ownership.OwnsCurrent(body));
        }

        [Fact]
        public void Ownership_MarkOmitted_ReplacesOnlyOmitted()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var presented = new object();
            var culled = new object();
            var firstOmitted = new object();
            var secondOmitted = new object();
            ownership.BeginFrame(token);
            ownership.MarkPresented(token, new object[] { presented });
            ownership.MarkCulled(token, new object[] { culled });
            ownership.MarkOmitted(token, new object[] { firstOmitted });
            ownership.MarkOmitted(token, new object[] { secondOmitted });
            Assert.True(ownership.Owns(token, presented));
            Assert.True(ownership.Owns(token, culled));
            Assert.False(ownership.Owns(token, firstOmitted));
            Assert.True(ownership.Owns(token, secondOmitted));
            Assert.Contains(secondOmitted, ownership.OmittedBodies);
            Assert.DoesNotContain(firstOmitted, ownership.OmittedBodies);
        }

        [Fact]
        public void Ownership_MarkPresentedAndCulled_PreserveOmitted()
        {
            var ownership = new MainAtlasOwnership();
            var token = new object();
            var presented = new object();
            var culled = new object();
            var omitted = new object();
            ownership.BeginFrame(token);
            ownership.MarkOmitted(token, new object[] { omitted });
            ownership.MarkPresented(token, new object[] { presented });
            ownership.MarkCulled(token, new object[] { culled });
            Assert.True(ownership.Owns(token, omitted));
            Assert.Contains(omitted, ownership.OmittedBodies);
            Assert.True(ownership.Owns(token, presented));
            Assert.True(ownership.Owns(token, culled));
        }

        [Fact]
        public void Ownership_BeginFrameAndReset_ClearOmitted()
        {
            var ownership = new MainAtlasOwnership();
            var first = new object();
            var second = new object();
            var body = new object();
            ownership.BeginFrame(first);
            ownership.MarkOmitted(first, new object[] { body });
            ownership.BeginFrame(first);
            Assert.False(ownership.Owns(first, body));
            Assert.Empty(ownership.OmittedBodies);
            ownership.MarkOmitted(first, new object[] { body });
            ownership.BeginFrame(second);
            Assert.False(ownership.Owns(second, body));
            Assert.Empty(ownership.OmittedBodies);
            ownership.MarkOmitted(second, new object[] { body });
            ownership.Reset();
            Assert.Null(ownership.CurrentToken);
            Assert.False(ownership.OwnsCurrent(body));
            Assert.Empty(ownership.OmittedBodies);
        }

        [Fact]
        public void Ownership_ValueEqualDistinctOmittedObjects_NotOwned()
        {
            var ownership = new MainAtlasOwnership();
            string tokenA = new('t', 4);
            string tokenB = new('t', 4);
            string bodyA = new('b', 4);
            string bodyB = new('b', 4);
            Assert.NotSame(tokenA, tokenB);
            Assert.NotSame(bodyA, bodyB);
            Assert.Equal(bodyA, bodyB);
            ownership.BeginFrame(tokenA);
            ownership.MarkOmitted(tokenA, new object[] { bodyA });
            Assert.True(ownership.Owns(tokenA, bodyA));
            Assert.False(ownership.Owns(tokenA, bodyB));
            Assert.False(ownership.Owns(tokenB, bodyA));
            ownership.MarkOmitted(tokenB, new object[] { bodyB });
            Assert.False(ownership.Owns(tokenA, bodyB));
            Assert.False(ownership.OwnsCurrent(bodyB));
        }

        [Fact]
        public void OmittedBodies_IsReadOnlyObjectEnumerable()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var ownershipType = TypeOf(assembly.MainModule, "KSARS.Visuals.MainAtlasOwnership");
            var property = ownershipType.Properties.Single(p => p.Name == "OmittedBodies");
            Assert.Null(property.SetMethod);
            Assert.Equal("System.Collections.Generic.IEnumerable`1<System.Object>",
                property.PropertyType.FullName);
        }

        [Fact]
        public void TinyCutoff_IsExactlySixPixels_NoHiddenFloor()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer");
            var field = type.Fields.Single(f => f.Name == "TinyBodyPixelCutoff");
            Assert.True(field.IsLiteral);
            double cutoff = Assert.IsType<double>(field.Constant);
            Assert.Equal(6.0, cutoff);
            Assert.True(5.999 <= cutoff, "5.999px must classify as tiny");
            Assert.False(6.001 <= cutoff, "6.001px must not classify as tiny");
        }

        [Fact]
        public void FilterNativeDistantOrbiter_MainOnlyGuardsAndSixPixelCutoff()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var filter = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "FilterNativeDistantOrbiter");
            Assert.Equal("KSA.IOrbiter", filter.ReturnType.FullName);
            Assert.Equal(
                new[] { "System.Object", "KSA.IViewport", "System.Int32" },
                filter.Parameters.Select(p => p.ParameterType.FullName).ToArray());

            var fieldRefs = FieldRefsOf(filter);
            Assert.Contains(fieldRefs, r => r.Name == "s_mainProbeEnabled");
            Assert.Contains(fieldRefs, r => r.Name == "s_mainFailed");
            Assert.Contains(fieldRefs, r => r.Name == "s_recordingDisabled");
            Assert.Contains(fieldRefs, r => r.Name == "s_tinyFilterFailed");
            Assert.Contains(fieldRefs, r => r.Name == "s_tinyActiveViewport");
            Assert.Contains(fieldRefs, r => r.Name == "s_tinyActiveFrameIndex");
            Assert.Contains(fieldRefs, r => r.Name == "s_tinyMainSnapshot");
            Assert.Contains(fieldRefs, r => r.Name == "s_tinyMainOmitted");

            var calls = CallsOf(filter);
            Assert.Contains(calls, c => c.DeclaringType == "KSARS.Visuals.ObserverSnapshot" && c.Name == "get_MainViewport");
            Assert.Contains(calls, c => c.DeclaringType == "KSA.GlobalShaderBindings" && c.Name == "CameraData");
            Assert.Contains(calls, c => c.DeclaringType == "KSA.Camera" && c.Name == "GetEffectiveNearbyCelestial");
            Assert.Contains(calls, c => c.Name == "TryBuildSourceCandidate");
            Assert.DoesNotContain(calls, c => c.DeclaringType.StartsWith("KSA.")
                && (c.Name == "Add" || c.Name == "UpdateDescriptorSets" || c.Name.StartsWith("Upload")));
            Assert.All(FieldStoresOf(filter),
                dt => Assert.Equal("KSARS.Visuals.RelativisticSpaceRenderer", dt));

            var instructions = filter.Body.Instructions.ToList();
            int buildIndex = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.Name == "TryBuildSourceCandidate");
            Assert.True(buildIndex > 0);
            Assert.Contains(instructions.Take(buildIndex).TakeLast(4),
                i => i.OpCode == OpCodes.Ldc_I4_0);
            Assert.Contains(instructions,
                i => i.OpCode == OpCodes.Ldc_R8 && i.Operand is double d && d == 6.0);
        }

        [Fact]
        public void BeginNativeDistanceData_MainViewportSnapshotOnly()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var begin = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "BeginNativeDistanceData");
            Assert.Equal("System.Void", begin.ReturnType.FullName);
            Assert.Equal(
                new[] { "KSA.IViewport", "System.Int32" },
                begin.Parameters.Select(p => p.ParameterType.FullName).ToArray());

            var calls = CallsOf(begin);
            Assert.Contains(calls, c => c.DeclaringType == "KSA.Program" && c.Name == "get_MainViewport");
            Assert.Contains(calls, c => c.DeclaringType == "KSARS.Visuals.ObserverSnapshot" && c.Name == "TryCapture");
            Assert.Contains(calls, c => c.Name == "EffectiveObserver");
            var fieldRefs = FieldRefsOf(begin);
            Assert.Contains(fieldRefs, r => r.Name == "s_mainProbeEnabled");
            Assert.Contains(fieldRefs, r => r.Name == "s_tinyActiveViewport");
            Assert.Contains(fieldRefs, r => r.Name == "s_tinyActiveFrameIndex");
            Assert.Contains(fieldRefs, r => r.Name == "s_tinyMainSnapshot");
            Assert.All(FieldStoresOf(begin),
                dt => Assert.Equal("KSARS.Visuals.RelativisticSpaceRenderer", dt));
        }

        [Fact]
        public void TranspileNativeDistanceData_ValidatesAnchorsAndUniqueCast()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var transpiler = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "TranspileNativeDistanceData");
            var fieldRefs = FieldRefsOf(transpiler);
            Assert.Contains(fieldRefs, r => r.Name == "s_tinyBegin");
            Assert.Contains(fieldRefs, r => r.Name == "s_tinyFilter");
            var strings = LoadedStrings(transpiler);
            Assert.Contains(strings, s => s.Contains("IOrbiter cast anchor"));
            Assert.Contains(strings, s => s.Contains("AppendStars anchor"));
            Assert.Contains(strings, s => s.Contains("GetObjectDiameterPixels anchor"));
            Assert.Contains(strings, s => s.Contains("SpriteSphereBlend anchor"));
            Assert.Contains(strings, s => s.Contains("GetApparentSizeScale(IOrbiter) anchor"));
            Assert.Contains(strings, s => s.Contains("DeviceVector.Add anchor"));
        }

        [Fact]
        public void PrepareProbe_ResolvesTinyHelpersOnlyForMainProbe()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "PrepareProbe");
            var strings = LoadedStrings(prepare);
            Assert.Contains(strings, s => s == "BeginNativeDistanceData");
            Assert.Contains(strings, s => s == "FilterNativeDistantOrbiter");
            var fieldRefs = FieldRefsOf(prepare);
            Assert.Contains(fieldRefs, r => r.Name == "s_tinyBegin");
            Assert.Contains(fieldRefs, r => r.Name == "s_tinyFilter");
            Assert.Contains(FieldStoresOf(prepare),
                dt => dt == "KSARS.Visuals.RelativisticSpaceRenderer");
        }

        [Fact]
        public void CoordinatorMainPrep_OmitsBeforeCandidateBuildAndGpuWork()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "PrepareMainFrameSources");
            var instructions = prepare.Body.Instructions.ToList();

            int matchIndex = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.Name == "TinyClassificationsMatch");
            int omittedIndex = instructions.FindIndex(i =>
                i.Operand is FieldReference fr && fr.Name == "OmittedBodies");
            int buildIndex = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.Name == "TryBuildSourceCandidate");
            int contextIndex = instructions.FindIndex(i =>
                i.OpCode == OpCodes.Newobj && i.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "KSARS.Visuals.ObjectCaptureContext");

            Assert.True(matchIndex >= 0 && matchIndex < buildIndex,
                "tiny classification match must be evaluated before candidate validation");
            Assert.True(omittedIndex > matchIndex && omittedIndex < buildIndex,
                "omitted bodies must be classified before TryBuildSourceCandidate and its native rest-size guard");
            Assert.True(contextIndex < 0 || omittedIndex < contextIndex,
                "omitted bodies must be classified before any source GPU resource creation");
        }

        [Fact]
        public void CoordinatorMainPrep_PublishesOmittedAtBothCompletionBoundaries()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "PrepareMainFrameSources");
            var calls = CallsOf(prepare);

            var markIndices = calls.Select((c, i) => (c, i))
                .Where(x => x.c.DeclaringType == "KSARS.Visuals.MainAtlasOwnership" && x.c.Name == "MarkOmitted")
                .Select(x => x.i).ToList();
            Assert.Equal(2, markIndices.Count);
            Assert.All(markIndices, i =>
            {
                int j = i - 1;
                while (j >= 0 && (calls[j].Name == "Cast" || calls[j].Name.StartsWith("get_")))
                {
                    j--;
                }
                Assert.True(j >= 0 && calls[j].Name == "MarkCulled",
                    "each MarkOmitted publication must follow the matching MarkCulled");
            });

            int recordIndex = calls.FindIndex(c =>
                c.DeclaringType == "KSARS.Visuals.NativeCelestialCaptureAdapter" && c.Name == "Record");
            Assert.True(recordIndex >= 0);
            Assert.All(markIndices, i => Assert.True(i > recordIndex,
                "omitted ownership must publish only after the source-recording loop completes"));

            int syncIndex = calls.FindIndex(c =>
                c.DeclaringType == "KSARS.Visuals.AtlasMainPass" && c.Name == "SyncSources");
            Assert.True(syncIndex >= 0);
            Assert.True(markIndices.Any(i => i < syncIndex),
                "the zero-resolved-body branch must publish omitted ownership before its early return");
            Assert.True(markIndices.Any(i => i > syncIndex),
                "the recorded-source branch must publish omitted ownership after SyncSources succeeds");
        }

        [Fact]
        public void CoordinatorMainPrep_OmittedStatusLabelsNonphysicalSimplification()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "PrepareMainFrameSources");
            var strings = LoadedStrings(prepare);
            Assert.Contains(strings,
                s => s.Contains("tiny source(s) omitted") && s.Contains("nonphysical visibility simplification"));
            Assert.Contains(strings,
                s => s.Contains("rest<=6px observed>6px") && s.Contains("stock"));
        }

        [Fact]
        public void GetStatus_ReportsOmittedOwnershipSeparatelyWithDiameters()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var status = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "GetStatus");
            var strings = LoadedStrings(status);
            Assert.Contains(strings, s => s.Contains("mainOmitted=["));
            Assert.Contains(strings, s => s.Contains("mainOmitted=none"));
            Assert.Contains(strings, s => s.Contains("tiny distant-body omission: nonphysical visibility simplification"));
            Assert.Contains(strings, s => s.Contains("nativeOmitted=["));
            Assert.Contains(strings, s => s.Contains("nativeOmitted=none"));
            Assert.Contains(strings, s => s.Contains("tinyFilterFailure="));
            var statusCalls = CallsOf(status);
            Assert.Contains(statusCalls, c =>
                c.DeclaringType == "KSARS.Visuals.MainAtlasOwnership" && c.Name == "get_OmittedBodies");
            Assert.Contains(statusCalls, c => c.Name == "TinyClassificationsMatch");
            Assert.Contains(statusCalls, c => c.Name == "EffectiveObserver");
            Assert.Contains(FieldRefsOf(status), f => f.Name == "s_tinyMainOmitted");
            Assert.Contains(FieldRefsOf(status), f => f.Name == "s_tinyFailureStatus");
        }

        [Fact]
        public void TinyClassificationsMatch_RequiresCameraMatrixAndOriginIdentity()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var match = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "TinyClassificationsMatch");
            var calls = CallsOf(match);
            Assert.Contains(calls, c =>
                c.DeclaringType == "KSARS.Visuals.ObserverSnapshot" && c.Name == "get_MainCamera");
            Assert.Contains(calls, c =>
                c.DeclaringType == "KSARS.Visuals.ObserverSnapshot" && c.Name == "get_PositionEcl");
            Assert.Contains(calls, c => c.Name == "Equals");
            var fieldLoads = match.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();
            Assert.Contains("View", fieldLoads);
            Assert.Contains("Projection", fieldLoads);
        }

        [Fact]
        public void TinyFilterFailureStatus_PersistsSeparatelyAndClearsOnReset()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var module = assembly.MainModule;
            var renderer = TypeOf(module, "KSARS.Visuals.RelativisticSpaceRenderer");
            Assert.DoesNotContain(renderer.Fields, f => f.Name == "s_tinyFailureLogged");
            Assert.Contains(renderer.Fields, f => f.Name == "s_tinyFailureStatus");

            var disable = SingleMethod(module, renderer.FullName, "DisableTinyDistanceFilter");
            var disableStores = disable.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();
            Assert.Contains("s_tinyFailureStatus", disableStores);
            Assert.Contains("s_tinyFilterFailed", disableStores);
            Assert.DoesNotContain("s_mainStatus", disableStores);

            var clear = SingleMethod(module, renderer.FullName, "ClearTinyDistanceState");
            var clearStores = clear.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();
            Assert.Contains("s_tinyFailureStatus", clearStores);

            var reset = SingleMethod(module, renderer.FullName, "ResetProbeState");
            var rebuilt = SingleMethod(module, renderer.FullName, "OnRendererRebuilt");
            Assert.Contains(CallsOf(reset), c => c.Name == "ClearTinyDistanceState");
            Assert.Contains(CallsOf(rebuilt), c => c.Name == "ClearTinyDistanceState");
        }

        [Fact]
        public void StatusStrings_DoNotClaimSixPixelSuppressionOnly()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var renderer = TypeOf(assembly.MainModule, "KSARS.Visuals.RelativisticSpaceRenderer");
            var allStrings = renderer.Methods.Where(m => m.HasBody)
                .SelectMany(LoadedStrings)
                .ToList();
            Assert.DoesNotContain(allStrings, s => s.Contains(">6px suppression only"));
            Assert.Contains(allStrings, s => s.Contains("resolved captures require native >6px")
                && s.Contains("observed <=6px bodies omitted"));
        }

        [Fact]
        public void SourceCandidate_ObservedPixelDiameter_StoredAfterValidation()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var module = assembly.MainModule;
            var candidate = TypeOf(module, "KSARS.Visuals.RelativisticSpaceRenderer/SourceCandidate");
            var property = candidate.Properties.SingleOrDefault(p => p.Name == "ObservedPixelDiameter");
            Assert.NotNull(property);
            Assert.Null(property.SetMethod);
            Assert.Equal("System.Double", property.PropertyType.FullName);

            var builder = SingleMethod(module, "KSARS.Visuals.RelativisticSpaceRenderer",
                "TryBuildSourceCandidate");
            var calls = CallsOf(builder);
            int capIndex = calls.FindIndex(c => c.Name == "TryTransformSphericalCap");
            int helperIndex = calls.FindIndex(c =>
                c.DeclaringType == "KSARS.Visuals.AtlasProjection" && c.Name == "TryGetObservedPixelDiameter");
            Assert.True(capIndex >= 0 && helperIndex > capIndex,
                "the observed-pixel bound must be computed after the transformed cap");
        }

        [Fact]
        public void BothFragments_RestStateDefaultsPinned()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            foreach (string typeName in new[]
            {
                "KSARS.Visuals.AtlasCompositeProbe",
                "KSARS.Visuals.AtlasMainPass"
            })
            {
                var type = TypeOf(assembly.MainModule, typeName);
                string source = (string)type.Fields.Single(f => f.Name == "FragmentSource").Constant!;
                Assert.Contains("vec3 nRest = nObs;", source);
                Assert.Contains("float doppler = 1.0;", source);
                Assert.Contains("if (q != 1.0)", source);
            }
        }
    }
}

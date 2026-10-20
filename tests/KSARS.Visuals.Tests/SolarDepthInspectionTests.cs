using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class SolarDepthInspectionTests
    {
        static SolarDepthInspectionTests()
        {
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                if (!BaselineContext.Enabled)
                {
                    return null;
                }

                string path = Path.Combine(
                    Path.GetDirectoryName(BaselineContext.KsaAssemblyPath)!, name.Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
        }

        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");
        private const string ProbeType = "KSARS.Visuals.SolarDepthInspectionProbe";
        private const string CoverageType = "KSARS.Visuals.SolarCoverageProbe";
        private const string RendererType = "KSARS.Visuals.RelativisticSpaceRenderer";
        private const int Size = 512;

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

        private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type)
        {
            foreach (MethodDefinition method in type.Methods)
            {
                yield return method;
            }

            foreach (TypeDefinition nested in type.NestedTypes)
            {
                foreach (MethodDefinition method in AllMethods(nested))
                {
                    yield return method;
                }
            }
        }

        private static List<(string DeclaringType, string Name)> AllCallsOf(TypeDefinition type) =>
            AllMethods(type).Where(m => m.HasBody).SelectMany(CallsOf).ToList();

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
            var type = module.GetType(name);
            Assert.NotNull(type);
            return type;
        }

        private static MethodDefinition SingleMethod(ModuleDefinition module, string typeName, string methodName) =>
            TypeOf(module, typeName).Methods.Single(m => m.Name == methodName);

        private static AtlasProjection SourceProjection(double distance, double enclosingRadius)
        {
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, distance),
                enclosingRadius, 512, 8, out AtlasProjection projection));
            return projection;
        }

        private static bool IsStloc(OpCode op) =>
            op == OpCodes.Stloc || op == OpCodes.Stloc_S || op == OpCodes.Stloc_0
            || op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3;

        private static (float R, float G, float B) ReplicaInspect(int outputX, int outputY,
            float measuredDepth, float tanHalfFov, float radiusRatio, float nearRatio, float farRatio,
            out float measuredRadialNorm, out bool expectedHit)
        {
            measuredRadialNorm = 0.0f;
            expectedHit = false;
            int pane = outputX / Size;
            int cx = outputX % Size;
            int cy = outputY;
            if (pane < 0 || pane > 2 || cx < 0 || cy < 0 || cx >= Size || cy >= Size)
            {
                return (1.0f, 1.0f, 0.0f);
            }

            if (float.IsNaN(measuredDepth) || float.IsInfinity(measuredDepth)
                || measuredDepth < 0.0f || measuredDepth > 1.0f)
            {
                return (1.0f, 1.0f, 0.0f);
            }

            float u = (cx + 0.5f) / Size;
            float v = (cy + 0.5f) / Size;
            float px = (u * 2.0f - 1.0f) * tanHalfFov;
            float py = (v * 2.0f - 1.0f) * tanHalfFov;
            float len = MathF.Sqrt(px * px + py * py + 1.0f);
            float rx = px / len;
            float ry = py / len;
            float rz = 1.0f / len;
            float disc = radiusRatio * radiusRatio - (rx * rx + ry * ry);
            expectedHit = disc >= 0.0f;
            if (pane == 0)
            {
                float w = measuredDepth > 0.0f ? 1.0f : 0.0f;
                return (w, w, w);
            }

            if (measuredDepth == 0.0f)
            {
                return pane == 2 && expectedHit ? (1.0f, 0.0f, 1.0f) : (0.0f, 0.0f, 0.0f);
            }

            float sourceZ = nearRatio * farRatio
                / (nearRatio + measuredDepth * (farRatio - nearRatio));
            float measuredRadial = sourceZ / rz;
            if (float.IsNaN(measuredRadial) || float.IsInfinity(measuredRadial)
                || !(measuredRadial > 0.0f))
            {
                return (1.0f, 1.0f, 0.0f);
            }

            measuredRadialNorm = measuredRadial;
            if (pane == 1)
            {
                float frontness = Math.Clamp((1.0f - measuredRadial) / radiusRatio, 0.0f, 1.0f);
                float grey = 0.25f + 0.75f * frontness;
                return (grey, grey, grey);
            }

            if (!expectedHit)
            {
                return (1.0f, 0.0f, 0.0f);
            }

            float expectedRadial = rz - MathF.Sqrt(disc);
            float relativeRadiusError = MathF.Abs(measuredRadial - expectedRadial) / radiusRatio;
            float errorVis = Math.Clamp(relativeRadiusError * 1000.0f, 0.0f, 1.0f);
            return (errorVis, 1.0f - errorVis, 0.0f);
        }

        private static double UOf(int cx) => (cx + 0.5) / (double)Size;
        private static double VOf(int cy) => (cy + 0.5) / (double)Size;

        [Theory]
        [InlineData(256, 256)]
        [InlineData(280, 256)]
        public void InsidePixel_PanesMatchDoubleModel(int cx, int cy)
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));
            Assert.True(model.TrySample(UOf(cx), VOf(cy), 1.0, out double radial, out double depth),
                $"texel ({cx},{cy}) must be an interior physical hit");

            float td = (float)depth;
            var pane0 = ReplicaInspect(cx, cy, td, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, (float)model.NearRatio, (float)model.FarRatio,
                out _, out _);
            Assert.Equal((1.0f, 1.0f, 1.0f), pane0);

            var pane1 = ReplicaInspect(cx + Size, cy, td, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, (float)model.NearRatio, (float)model.FarRatio,
                out _, out _);
            Assert.True(pane1.R >= 0.25f && pane1.R <= 1.0f && pane1.R == pane1.G && pane1.G == pane1.B,
                $"pane1 must be a finite grey frontness in [.25,1], got {pane1}");

            var pane2 = ReplicaInspect(cx + 2 * Size, cy, td, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, (float)model.NearRatio, (float)model.FarRatio,
                out float measuredRadialNorm, out bool expectedHit);
            Assert.True(expectedHit);
            double radialNorm = radial / model.Distance;
            Assert.True(Math.Abs(measuredRadialNorm - radialNorm) <= 1e-5,
                $"recovered radialNorm {measuredRadialNorm} vs model {radialNorm}");
            Assert.True(pane2.G > 0.5f && pane2.R < 0.5f,
                $"safe-interior reference error must stay low (green), got {pane2}");
        }

        [Fact]
        public void ThreePanes_SampleIdenticalSourceTexel()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));
            Assert.True(model.TrySample(UOf(280), VOf(256), 1.0, out _, out double depth));

            float td = (float)depth;
            var pane0 = ReplicaInspect(280, 256, td, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, (float)model.NearRatio, (float)model.FarRatio, out _, out _);
            var pane1 = ReplicaInspect(280 + Size, 256, td, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, (float)model.NearRatio, (float)model.FarRatio, out _, out _);
            var pane2 = ReplicaInspect(280 + 2 * Size, 256, td, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, (float)model.NearRatio, (float)model.FarRatio, out _, out _);

            Assert.Equal((1.0f, 1.0f, 1.0f), pane0);
            Assert.True(pane1.R >= 0.25f && pane1.R <= 1.0f);
            Assert.True(pane2.G > 0.0f);
        }

        [Fact]
        public void ClearedDepthInside_Pane0BlackPane2Magenta()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));

            Assert.Equal((0.0f, 0.0f, 0.0f), ReplicaInspect(256, 256, 0.0f, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, (float)model.NearRatio, (float)model.FarRatio, out _, out _));
            Assert.Equal((0.0f, 0.0f, 0.0f), ReplicaInspect(256 + Size, 256, 0.0f, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, (float)model.NearRatio, (float)model.FarRatio, out _, out _));
            Assert.Equal((1.0f, 0.0f, 1.0f), ReplicaInspect(256 + 2 * Size, 256, 0.0f, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, (float)model.NearRatio, (float)model.FarRatio, out _, out _));
        }

        [Fact]
        public void ZeroDepthOutside_AllPanesBlack()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));
            Assert.False(model.TrySample(UOf(511), VOf(511), 1.0, out _, out _),
                "corner texel must be outside the physical disc");

            foreach (int offset in new[] { 0, Size, 2 * Size })
            {
                Assert.Equal((0.0f, 0.0f, 0.0f), ReplicaInspect(511 + offset, 511, 0.0f,
                    (float)model.TanHalfFov, (float)model.PhysicalRadiusRatio,
                    (float)model.NearRatio, (float)model.FarRatio, out _, out _));
            }
        }

        [Fact]
        public void PositiveDepthOutside_Pane2Red()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));

            Assert.Equal((1.0f, 0.0f, 0.0f), ReplicaInspect(511 + 2 * Size, 511, 0.5f,
                (float)model.TanHalfFov, (float)model.PhysicalRadiusRatio,
                (float)model.NearRatio, (float)model.FarRatio, out _, out _));
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(-1.0f)]
        [InlineData(1.1f)]
        public void InvalidDepth_AllPanesYellow(float injected)
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));

            foreach (int offset in new[] { 0, Size, 2 * Size })
            {
                Assert.Equal((1.0f, 1.0f, 0.0f), ReplicaInspect(256 + offset, 256, injected,
                    (float)model.TanHalfFov, (float)model.PhysicalRadiusRatio,
                    (float)model.NearRatio, (float)model.FarRatio, out _, out _));
            }
        }

        [Fact]
        public void DepthOneAtInterior_Pane2LargeErrorRed()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));

            var pane2 = ReplicaInspect(256 + 2 * Size, 256, 1.0f, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, (float)model.NearRatio, (float)model.FarRatio, out _, out _);
            Assert.Equal((1.0f, 0.0f, 0.0f), pane2);
        }

        [Fact]
        public void UnderflowedReconstruction_Pane0WhitePane1Pane2Yellow()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));

            const float nearRatio = 1e-30f;
            const float farRatio = 2e-30f;
            Assert.Equal((1.0f, 1.0f, 1.0f), ReplicaInspect(256, 256, 0.5f, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, nearRatio, farRatio, out _, out _));
            Assert.Equal((1.0f, 1.0f, 0.0f), ReplicaInspect(256 + Size, 256, 0.5f, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, nearRatio, farRatio, out _, out _));
            Assert.Equal((1.0f, 1.0f, 0.0f), ReplicaInspect(256 + 2 * Size, 256, 0.5f, (float)model.TanHalfFov,
                (float)model.PhysicalRadiusRatio, nearRatio, farRatio, out _, out _));
        }

        [Fact]
        public void FragmentSource_PinsTexelFetchPaneMappingAndInversion()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, ProbeType);
            string source = (string)type.Fields.Single(f => f.Name == "FragmentSource").Constant;

            Assert.Contains("layout(set = 0, binding = 0) uniform sampler2D sourceDepth;", source);
            Assert.Contains("textureSize(sourceDepth, 0)", source);
            Assert.Contains("ivec2(gl_FragCoord.xy)", source);
            Assert.Contains("outputCoord.x % size.x", source);
            Assert.Contains("texelFetch(sourceDepth, coord, 0).r", source);
            Assert.Contains("(vec2(coord) + 0.5) / vec2(size)", source);
            Assert.Contains("n * f / (n + measuredDepth * (f - n))", source);
            Assert.Contains("measuredRadial = sourceZ / ray.z", source);
            Assert.Contains("relativeRadiusError * 1000.0", source);
            Assert.Contains("vec4(1.0, 0.0, 1.0, 1.0)", source);
            Assert.Contains("vec4(1.0, 1.0, 0.0, 1.0)", source);
            Assert.DoesNotContain("inUv", source);

            int radialCheck = source.IndexOf("!(measuredRadial > 0.0))", StringComparison.Ordinal);
            Assert.True(radialCheck >= 0);
            int radialReturn = source.IndexOf("return;", radialCheck, StringComparison.Ordinal);
            Assert.Contains("vec4(1.0, 1.0, 0.0, 1.0)",
                source.Substring(radialCheck, radialReturn - radialCheck));
        }

        [Fact]
        public void Probe_OwnsColorOnlyTargetDescriptorSetAndPipeline()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, ProbeType);
            var ctorCalls = CallsOf(type.Methods.Single(m => m.Name == ".ctor"));

            Assert.Equal(1, ctorCalls.Count(c => c.Name == ".ctor" && c.DeclaringType.EndsWith("RenderTarget")));
            Assert.Equal(2, ctorCalls.Count(c => c.Name == "CompileModule"));
            Assert.Equal(1, ctorCalls.Count(c => c.Name == "CreateDescriptorSetLayout"));
            Assert.Equal(1, ctorCalls.Count(c => c.Name == "CreateDescriptorPool"));
            Assert.Equal(1, ctorCalls.Count(c => c.Name == "AllocateDescriptorSet"));
            Assert.Equal(1, ctorCalls.Count(c => c.Name == "CreatePipelineLayout"));
            Assert.Equal(1, ctorCalls.Count(c => c.Name == "BuildPipeline"));
            Assert.Equal(1, ctorCalls.Count(c => c.Name == "WriteImageDescriptor"));
            Assert.True(CallsOf(SingleMethod(assembly.MainModule, ProbeType, "WriteImageDescriptor"))
                    .Count(c => c.Name == "UpdateDescriptorSets") == 1,
                "the owned descriptor must be written exactly once through the helper");

            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_target"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_sourceDepth"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_setLayout"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_pool"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_set"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_pipelineLayout"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_pipeline"));

            Assert.Contains(type.Methods.Single(m => m.Name == ".ctor").Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && i.Operand is string text
                    && text == AtlasCompositeProbe.VertexSource);
            var ctorFieldRefs = FieldRefsOf(type.Methods.Single(m => m.Name == ".ctor"));
            Assert.Contains(ctorFieldRefs, f => f.Name == "_sourceDepth");
            Assert.True(ctorCalls.Count(c => c.Name == "get_PointClampedSampler") == 1,
                "the descriptor must borrow the native point-clamped sampler read-only");
            Assert.True(ctorCalls.Count(c => c.Name == "get_ImageView") == 1
                && ctorCalls.Count(c => c.Name == "get_Format") == 1
                && ctorCalls.Count(c => c.Name == "get_Width") == 1
                && ctorCalls.Count(c => c.Name == "get_Height") == 1,
                "the ctor must validate and sample-view the owned source depth image");
        }

        [Fact]
        public void Probe_HasNoGlobalsOrNativeSolarPath()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var allCalls = AllCallsOf(TypeOf(assembly.MainModule, ProbeType));

            Assert.Equal(0, allCalls.Count(c => c.DeclaringType.StartsWith("KSA.GlobalShaderBindings")));
            Assert.Equal(0, allCalls.Count(c => c.DeclaringType == "KSARS.Visuals.NativeSolarCaptureAdapter"));
            Assert.Equal(0, allCalls.Count(c => c.DeclaringType == "KSARS.Visuals.ObjectCaptureContext"));
            Assert.Equal(0, allCalls.Count(c => c.Name == "RenderGame"));
            Assert.Equal(0, allCalls.Count(c => c.Name == "RenderViewport"));
            Assert.Equal(0, allCalls.Count(c => c.Name == "WaitIdle"));
            Assert.Equal(0, allCalls.Count(c => c.DeclaringType == "KSA.Rendering.RenderImage"
                && c.Name == "Dispose"));
        }

        [Fact]
        public void Record_BarrierBeforeBeginThenBalancedRecord()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = SingleMethod(assembly.MainModule, ProbeType, "Record");
            var calls = CallsOf(record);

            Assert.Equal(1, calls.Count(c => c.Name == "PipelineBarrier2"));
            Assert.Equal(1, calls.Count(c => c.Name == "BeginRendering"));
            Assert.Equal(1, calls.Count(c => c.Name == "EndRendering"));
            Assert.Equal(1, calls.Count(c => c.Name == "BindPipeline"));
            Assert.Equal(1, calls.Count(c => c.Name == "BindDescriptorSets"));
            Assert.Equal(1, calls.Count(c => c.Name == "PushConstants"));
            Assert.Equal(1, calls.Count(c => c.Name == "Draw"));
            Assert.Equal(1, calls.Count(c => c.DeclaringType == "KSA.Program" && c.Name == "SetViewport"));

            Assert.True(record.Body.ExceptionHandlers.Count(h => h.HandlerType == ExceptionHandlerType.Finally) >= 2,
                "Record must end the render pass in a finally and restore the viewport in an outer finally");

            var instructions = record.Body.Instructions.ToList();
            int IndexOf(string name, string? declaring = null) =>
                instructions.FindIndex(i => i.Operand is MethodReference mr && mr.Name == name
                    && (declaring is null || mr.DeclaringType.FullName == declaring));

            int uploadIndex = IndexOf("GeometryUploadSupported", CoverageType);
            int barrierIndex = IndexOf("PipelineBarrier2");
            int flagIndex = IndexOf("set_RecordingRecovered", ProbeType);
            int beginIndex = IndexOf("BeginRendering");
            int bindIndex = IndexOf("BindPipeline");
            int setIndex = IndexOf("BindDescriptorSets");
            int pushIndex = IndexOf("PushConstants");
            int drawIndex = IndexOf("Draw");
            int endIndex = IndexOf("EndRendering");
            int viewportIndex = IndexOf("SetViewport", "KSA.Program");
            int recoveryIndex = IndexOf("RecordingRecoveredState", CoverageType);

            Assert.True(uploadIndex >= 0 && flagIndex > uploadIndex,
                "shared float upload validation must precede RecordingRecovered=false");
            Assert.True(barrierIndex >= 0 && barrierIndex < beginIndex,
                "the source depth sampled-read barrier must be issued before BeginRendering");
            Assert.Contains(instructions.Skip(flagIndex + 1).Take(beginIndex - flagIndex - 1),
                i => IsStloc(i.OpCode));
            Assert.True(bindIndex > beginIndex && setIndex > bindIndex && pushIndex > setIndex
                && drawIndex > pushIndex && endIndex > drawIndex,
                "Record must order Begin -> Bind pipeline -> Bind set -> Push -> Draw -> End");
            Assert.Contains(FieldRefsOf(record), f => f.Name == "_sourceDepth");
            Assert.Contains(FieldRefsOf(record), f => f.Name == "_pipeline");
            Assert.Contains(FieldRefsOf(record), f => f.Name == "_set");
            Assert.True(recoveryIndex > viewportIndex,
                "safe recovery is decided only after the program viewport restore succeeds");
        }

        [Fact]
        public void Dispose_AttemptsOwnedResourcesNeverSourceDepth()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var dispose = SingleMethod(assembly.MainModule, ProbeType, "Dispose");
            var calls = CallsOf(dispose);

            Assert.Equal(1, calls.Count(c => c.Name == "RemoveTexture"));
            Assert.Equal(1, calls.Count(c => c.Name == "DestroyPipeline"));
            Assert.Equal(1, calls.Count(c => c.Name == "DestroyPipelineLayout"));
            Assert.Equal(2, calls.Count(c => c.Name == "DestroyShaderModule"));
            Assert.True(calls.Count(c => c.Name == "Dispose") >= 3,
                "pool, descriptor set layout and target must each be disposed");
            Assert.Equal(0, calls.Count(c => c.DeclaringType == "KSA.Rendering.RenderImage"));
            Assert.True(dispose.Body.ExceptionHandlers.Count >= 8,
                "each owned resource must be disposed inside its own try/catch");
            Assert.Contains(calls, c => c.DeclaringType == "System.AggregateException" && c.Name == ".ctor");
        }

        [Fact]
        public void CoverageProbe_ExposesOwnedDepthImageOnly()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, CoverageType);
            var getter = type.Methods.Single(m => m.Name == "get_DepthImage");
            var calls = CallsOf(getter);

            Assert.Equal(1, calls.Count(c => c.Name == "get_DepthImage"));
            Assert.Equal(0, calls.Count(c => c.Name == "get_ColorImage"));
            Assert.Contains(FieldRefsOf(getter), f => f.Name == "_target");
        }

        [Fact]
        public void Coordinator_RootsInspectionBeforeRecordAndRethrowsUnbalanced()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var renderer = TypeOf(assembly.MainModule, RendererType);
            var method = renderer.Methods.Single(m => m.Name == "TryRecordSolarDepthInspection");
            var calls = CallsOf(method);

            Assert.Equal(1, calls.Count(c => c.DeclaringType == ProbeType && c.Name == ".ctor"));
            Assert.Equal(1, calls.Count(c => c.DeclaringType == CoverageType && c.Name == "get_DepthImage"));
            Assert.Equal(1, calls.Count(c => c.DeclaringType == ProbeType && c.Name == "Record"));
            Assert.Equal(1, calls.Count(c => c.DeclaringType == ProbeType && c.Name == "RegisterPreviewTexture"));

            var instructions = method.Body.Instructions.ToList();
            int ctorIndex = instructions.FindIndex(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == ProbeType && mr.Name == ".ctor");
            int storeIndex = instructions.FindIndex(i => i.OpCode == OpCodes.Stfld
                && i.Operand is FieldReference fr
                && fr.Name == "DepthInspection" && fr.DeclaringType.Name == "SolarProbeCapture");
            int recordIndex = instructions.FindIndex(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == ProbeType && mr.Name == "Record");
            Assert.True(ctorIndex >= 0 && storeIndex > ctorIndex && recordIndex > storeIndex,
                "the inspection probe must be rooted in SolarProbeCapture.DepthInspection before Record");
            Assert.Contains(instructions, i => i.OpCode == OpCodes.Rethrow);
            Assert.Contains(FieldRefsOf(method), f => f.Name == "s_solarDepthInspectionStatus");

            var solarCapture = renderer.NestedTypes.Single(t => t.Name == "SolarProbeCapture");
            Assert.NotNull(solarCapture.Fields.SingleOrDefault(
                f => f.Name == "DepthInspection" && f.FieldType.FullName == ProbeType));

            var coverageMethod = renderer.Methods.Single(m => m.Name == "TryRecordSolarCoverage");
            Assert.Contains(CallsOf(coverageMethod), c => c.Name == "TryRecordSolarDepthInspection");

            var allCalls = AllCallsOf(renderer);
            Assert.True(allCalls.Count(c => c.DeclaringType == ProbeType && c.Name == "get_RecordingRecovered") >= 2,
                "current and archived inspection probes must join the unbalanced-recording guard");
        }

        [Fact]
        public void Coordinator_DisposesInspectionBeforeCoverageAfterIdle()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var renderer = TypeOf(assembly.MainModule, RendererType);
            var shutdown = renderer.Methods.Single(m => m.Name == "Shutdown");
            var instructions = shutdown.Body.Instructions.ToList();

            int idleIndex = instructions.FindIndex(i => i.Operand is MethodReference mr && mr.Name == "WaitIdle");
            int inspectionDispose = instructions.FindIndex(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == ProbeType && mr.Name == "Dispose");
            int coverageDispose = instructions.FindIndex(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == CoverageType && mr.Name == "Dispose");

            Assert.True(idleIndex >= 0 && inspectionDispose > idleIndex,
                "inspection disposal must run only after the verified-idle device wait");
            Assert.True(coverageDispose > inspectionDispose,
                "inspection must be disposed before the coverage probe it samples");

            var status = renderer.Methods.Single(m => m.Name == "GetStatus");
            Assert.Contains(FieldRefsOf(status), f => f.Name == "s_solarDepthInspectionStatus");
        }

        [Fact]
        public void StatusStrings_InspectionIsSampledNotCalibrated()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var renderer = TypeOf(assembly.MainModule, RendererType);
            var strings = AllMethods(renderer).Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions)
                .Where(i => i.OpCode == OpCodes.Ldstr)
                .Select(i => (string)i.Operand)
                .ToList();

            Assert.Contains(strings, s => s.Contains("no CPU readback") && s.Contains("GPU depth acceptance pending"));
#if DEBUG
            Assert.Contains(strings, s => s.Contains("not native Sun opacity/calibration"));
#endif
            Assert.Contains(strings, s => s.Contains("x1000 display only") || s.Contains("gain x1000 display only"));
            Assert.DoesNotContain(strings, s => s.Contains("depth verified") || s.Contains("calibrated"));
        }
    }
}

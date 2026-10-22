using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class SolarCoverageTests
    {
        static SolarCoverageTests()
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
        private const string ProbeType = "KSARS.Visuals.SolarCoverageProbe";
        private const string RendererType = "KSARS.Visuals.RelativisticSpaceRenderer";

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

        private static int CountCalls(List<(string DeclaringType, string Name)> calls, string declaringType, string name) =>
            calls.Count(c => c.DeclaringType == declaringType && c.Name == name);

        private static AtlasProjection SourceProjection(double distance, double enclosingRadius)
        {
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, distance),
                enclosingRadius, 512, 8, out AtlasProjection projection));
            return projection;
        }

        private static double PhysicalTan(in SolarSourceGeometry model) =>
            model.PhysicalRadiusRatio / Math.Sqrt(1.0 - model.PhysicalRadiusRatio * model.PhysicalRadiusRatio);

        private static double UForFactor(in SolarSourceGeometry model, double factor) =>
            0.5 + 0.5 * factor * PhysicalTan(model) / model.TanHalfFov;

        [Fact]
        public void CenterSample_RadialIsDistanceMinusRadiusWithReverseZDepth()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));

            Assert.True(model.TrySample(0.5, 0.5, 1.0, out double radial, out double depth));
            Assert.True(Math.Abs(radial - (1e9 - 1e7)) <= 1e-12 * 1e9,
                $"center radial {radial} must equal distance-R {9.9e8}");

            double expectedDepth = (projection.Near * projection.Far / (1e9 - 1e7) - projection.Near)
                / (projection.Far - projection.Near);
            Assert.True(Math.Abs(depth - expectedDepth) <= 1e-12 * expectedDepth,
                $"center depth {depth} must equal reverse-Z reference {expectedDepth}");
        }

        [Fact]
        public void InteriorRayAtHalfPhysicalTangent_HitsPhysicalSphere()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));

            double u = UForFactor(model, 0.5);
            Assert.True(model.TrySample(u, 0.5, 1.0, out double radial, out double depth));
            Assert.True(radial > 0.0 && radial < projection.Distance);
            Assert.True(depth > 0.0 && depth <= 1.0);
        }

        [Fact]
        public void RayBeyondPhysicalTangent_MissesPhysicalButHitsNominal()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));

            double u = UForFactor(model, 1.01);
            Assert.False(model.TrySample(u, 0.5, 1.0, out _, out _));
            Assert.True(model.TrySample(u, 0.5, 2.75, out _, out _));
        }

        [Fact]
        public void RayBeyondNominalTangent_HitsEnvelopeOnly()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));

            double u = UForFactor(model, 2.8);
            Assert.False(model.TrySample(u, 0.5, 2.75, out _, out _));
            Assert.True(model.TrySample(u, 0.5, 4.0, out _, out _));
        }

        [Fact]
        public void RayOutsideEnvelope_MissesAllInsideTile()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));

            double u = UForFactor(model, 4.1);
            Assert.True(u <= 1.0, $"sample u={u} must remain inside the source tile");
            Assert.False(model.TrySample(u, 0.5, 1.0, out _, out _));
            Assert.False(model.TrySample(u, 0.5, 2.75, out _, out _));
            Assert.False(model.TrySample(u, 0.5, 4.0, out _, out _));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.25)]
        [InlineData(0.5)]
        [InlineData(0.8)]
        [InlineData(0.95)]
        public void SampledDepth_RoundTripsThroughProjectionReconstruction(double factor)
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));

            double u = UForFactor(model, factor);
            Assert.True(model.TrySample(u, 0.5, 1.0, out double radial, out double depth));
            Assert.True(projection.TryReconstructRadialDistance(u, 0.5, depth, out double reconstructed),
                $"projection must reconstruct radial distance for sampled depth at factor {factor}");
            Assert.True(Math.Abs(reconstructed - radial) <= 1e-10 * radial,
                $"reconstructed {reconstructed} vs sampled {radial} exceeded 1e-10 relative at factor {factor}");
        }

        [Fact]
        public void ScalingRadiusAndDistance_PreservesNormalizedDepthAndDoublesRadial()
        {
            AtlasProjection projection1 = SourceProjection(1e9, 4e7);
            AtlasProjection projection2 = SourceProjection(2e9, 8e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection1, 1e7, out SolarSourceGeometry model1));
            Assert.True(SolarSourceGeometry.TryCreate(in projection2, 2e7, out SolarSourceGeometry model2));

            foreach (double factor in new[] { 0.0, 0.25, 0.5, 0.8 })
            {
                double u = UForFactor(model1, factor);
                Assert.True(model1.TrySample(u, 0.5, 1.0, out double radial1, out double depth1));
                Assert.True(model2.TrySample(u, 0.5, 1.0, out double radial2, out double depth2));
                Assert.True(Math.Abs(depth1 - depth2) <= 1e-12,
                    $"normalized depth must be scale-invariant at factor {factor}: {depth1} vs {depth2}");
                Assert.True(Math.Abs(radial2 - 2.0 * radial1) <= 1e-10 * radial2,
                    $"radial must double at factor {factor}: {radial1} vs {radial2}");
            }

            foreach (double factor in new[] { 1.5, 3.5, 4.2 })
            {
                double u = UForFactor(model1, factor);
                foreach (double scale in new[] { 1.0, 2.75, 4.0 })
                {
                    bool hit1 = model1.TrySample(u, 0.5, scale, out _, out _);
                    bool hit2 = model2.TrySample(u, 0.5, scale, out _, out _);
                    Assert.Equal(hit1, hit2);
                }
            }
        }

        [Fact]
        public void InvalidModelInputs_Reject()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);

            Assert.False(SolarSourceGeometry.TryCreate(in projection, 0.0, out _));
            Assert.False(SolarSourceGeometry.TryCreate(in projection, -1e7, out _));
            Assert.False(SolarSourceGeometry.TryCreate(in projection, double.NaN, out _));
            Assert.False(SolarSourceGeometry.TryCreate(in projection, double.PositiveInfinity, out _));

            var badNear = new AtlasProjection(projection.Right, projection.Up, projection.Forward,
                1e9, projection.RestAngularRadius, projection.TanHalfFov, 0.0, projection.Far);
            Assert.False(SolarSourceGeometry.TryCreate(in badNear, 1e7, out _));

            var insideNear = new AtlasProjection(projection.Right, projection.Up, projection.Forward,
                1e9, projection.RestAngularRadius, projection.TanHalfFov, 9.95e8, projection.Far);
            Assert.False(SolarSourceGeometry.TryCreate(in insideNear, 1e7, out _),
                "near beyond the sphere front limb must reject");

            var insideFar = new AtlasProjection(projection.Right, projection.Up, projection.Forward,
                1e9, projection.RestAngularRadius, projection.TanHalfFov, projection.Near, 1.001e9);
            Assert.False(SolarSourceGeometry.TryCreate(in insideFar, 1e7, out _),
                "far inside the sphere back limb must reject");

            var badTan = new AtlasProjection(projection.Right, projection.Up, projection.Forward,
                1e9, projection.RestAngularRadius, 0.0, projection.Near, projection.Far);
            Assert.False(SolarSourceGeometry.TryCreate(in badTan, 1e7, out _));
        }

        [Fact]
        public void RadiusRatioUnderflowToZero_Rejected()
        {
            var tiny = new AtlasProjection(new OpticalVector(1.0, 0.0, 0.0),
                new OpticalVector(0.0, 1.0, 0.0), new OpticalVector(0.0, 0.0, 1.0),
                1e300, 0.5, 0.5, 1e5, 2e300);
            Assert.False(SolarSourceGeometry.TryCreate(in tiny, double.Epsilon, out _),
                "physicalRadius underflowing PhysicalRadiusRatio to zero must reject");
        }

        [RendererBaselineFact]
        public void GeometryUploadSupported_RejectsFloatUnderflowAndEnvelopeOverflow()
        {
            Assert.True(SolarCoverageProbe.GeometryUploadSupported(0.5f, 0.01f, 0.01f, 2.0f));
            Assert.False(SolarCoverageProbe.GeometryUploadSupported(0.5f, 1e-25f, 0.01f, 2.0f),
                "float radius whose square underflows to zero must reject");
            Assert.False(SolarCoverageProbe.GeometryUploadSupported(0.5f, 1e-40f, 0.01f, 2.0f),
                "subnormal float radius whose square underflows to zero must reject");
            Assert.False(SolarCoverageProbe.GeometryUploadSupported(0.5f, 0.25f, 0.01f, 2.0f),
                "float radius at .25 leaves no valid 4R envelope");
            Assert.False(SolarCoverageProbe.GeometryUploadSupported(0.5f, 0.3f, 0.01f, 2.0f));
            Assert.False(SolarCoverageProbe.GeometryUploadSupported(0.5f, 0.0f, 0.01f, 2.0f));
            Assert.False(SolarCoverageProbe.GeometryUploadSupported(0.5f, float.NaN, 0.01f, 2.0f));
        }

        [RendererBaselineFact]
        public void RecordingRecoveredState_FailsClosedOnAttemptedBegin()
        {
            Assert.True(SolarCoverageProbe.RecordingRecoveredState(ended: true, beginAttempted: true));
            Assert.True(SolarCoverageProbe.RecordingRecoveredState(ended: true, beginAttempted: false));
            Assert.True(SolarCoverageProbe.RecordingRecoveredState(ended: false, beginAttempted: false),
                "no attempted begin means no attachment state changed");
            Assert.False(SolarCoverageProbe.RecordingRecoveredState(ended: false, beginAttempted: true),
                "an attempted begin that never ended is unknown attachment state and must fail closed");
        }

        [Fact]
        public void ObserverInsideEnvelope_Rejected()
        {
            var inside = new AtlasProjection(new OpticalVector(1.0, 0.0, 0.0),
                new OpticalVector(0.0, 1.0, 0.0), new OpticalVector(0.0, 0.0, 1.0),
                3e7, 0.5, 0.5, 1e5, 1e8);
            Assert.False(SolarSourceGeometry.TryCreate(in inside, 1e7, out _),
                "observer at 3R distance with R sphere is inside the 4R envelope and must reject");

            var atEnvelope = new AtlasProjection(new OpticalVector(1.0, 0.0, 0.0),
                new OpticalVector(0.0, 1.0, 0.0), new OpticalVector(0.0, 0.0, 1.0),
                4e7, 0.5, 0.5, 1e5, 1e8);
            Assert.False(SolarSourceGeometry.TryCreate(in atEnvelope, 1e7, out _),
                "observer exactly at the 4R envelope must reject");
        }

        [Fact]
        public void InvalidUvScaleAndNonfinite_Reject()
        {
            AtlasProjection projection = SourceProjection(1e9, 4e7);
            Assert.True(SolarSourceGeometry.TryCreate(in projection, 1e7, out SolarSourceGeometry model));

            foreach (double u in new[] { -0.1, 1.1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                Assert.False(model.TrySample(u, 0.5, 1.0, out double radial, out double depth));
                Assert.Equal(0.0, radial);
                Assert.Equal(0.0, depth);
            }

            foreach (double scale in new[] { 0.0, -1.0, 4.5, double.NaN, double.PositiveInfinity })
            {
                Assert.False(model.TrySample(0.5, 0.5, scale, out double radial, out double depth));
                Assert.Equal(0.0, radial);
                Assert.Equal(0.0, depth);
            }

            var inflated = new SolarSourceGeometry(1e9, 0.05, 0.4, 0.01, 2.0);
            Assert.False(inflated.TrySample(0.5, 0.5, 3.0, out _, out _),
                "radiusScale pushing radiusRatio past 1 must reject");
            Assert.True(inflated.TrySample(0.5, 0.5, 1.0, out _, out _));
        }

        private static bool ReplicaSample(float tanHalfFov, float radiusRatio, float nearRatio,
            float farRatio, float u, float v, out float radialNorm, out float depth)
        {
            radialNorm = 0.0f;
            depth = 0.0f;
            float px = (u * 2.0f - 1.0f) * tanHalfFov;
            float py = (v * 2.0f - 1.0f) * tanHalfFov;
            float len = MathF.Sqrt(px * px + py * py + 1.0f);
            float rx = px / len;
            float ry = py / len;
            float rz = 1.0f / len;
            float transverse2 = rx * rx + ry * ry;
            float physicalDisc = radiusRatio * radiusRatio - transverse2;
            if (!float.IsFinite(physicalDisc) || physicalDisc < 0.0f)
            {
                return false;
            }

            float radial = rz - MathF.Sqrt(physicalDisc);
            float sourceZ = radial * rz;
            float reverseZ = (nearRatio * farRatio / sourceZ - nearRatio) / (farRatio - nearRatio);
            if (!(radial > 0.0f) || !(sourceZ > 0.0f) || !float.IsFinite(reverseZ)
                || !(reverseZ > 0.0f) || reverseZ > 1.0f)
            {
                return false;
            }

            radialNorm = radial;
            depth = reverseZ;
            return true;
        }

        private static double TileQuantize(double u) =>
            (Math.Floor(u * SolarCoverageProbe.SourceTileSize) + 0.5) / SolarCoverageProbe.SourceTileSize;

        [Theory]
        [InlineData(0.001)]
        [InlineData(0.005)]
        [InlineData(0.1)]
        public void FloatReplica_MatchesDoubleModelOnDeterministicGrid(double ratio)
        {
            var model = new SolarSourceGeometry(1e9, 4.2 * ratio, ratio, 0.01, 2.0);
            double physicalTan = ratio / Math.Sqrt(1.0 - ratio * ratio);

            foreach (double factor in new[] { 0.0, 0.25, 0.8 })
            {
                double u = TileQuantize(0.5 + 0.5 * factor * physicalTan / model.TanHalfFov);
                Assert.True(model.TrySample(u, 0.5, 1.0, out double radial, out double depth),
                    $"interior grid sample ratio={ratio} factor={factor} must hit");

                Assert.True(ReplicaSample((float)model.TanHalfFov, (float)ratio, (float)model.NearRatio,
                        (float)model.FarRatio, (float)u, 0.5f, out float radialNorm, out float replicaDepth),
                    $"float replica ratio={ratio} factor={factor} must hit the same interior ray");

                double radialNormDouble = radial / model.Distance;
                Assert.True(Math.Abs(radialNorm - radialNormDouble) <= 1e-5,
                    $"radialNorm float={radialNorm} vs double={radialNormDouble} ratio={ratio} factor={factor}");
                Assert.True(Math.Abs(replicaDepth - depth) <= 1e-5,
                    $"depth float={replicaDepth} vs double={depth} ratio={ratio} factor={factor}");
            }
        }

        [Fact]
        public void FloatReplica_NearLimbUncertainty_IsCharacterizedNotAccepted()
        {
            foreach (double ratio in new[] { 0.001, 0.005, 0.1 })
            {
                var model = new SolarSourceGeometry(1e9, 4.2 * ratio, ratio, 0.01, 2.0);
                double physicalTan = ratio / Math.Sqrt(1.0 - ratio * ratio);

                foreach (double factor in new[] { 0.9, 0.99, 0.999 })
                {
                    double u = TileQuantize(0.5 + 0.5 * factor * physicalTan / model.TanHalfFov);
                    bool hitDouble = model.TrySample(u, 0.5, 1.0, out double radial, out double depth);
                    bool hitFloat = ReplicaSample((float)model.TanHalfFov, (float)ratio,
                        (float)model.NearRatio, (float)model.FarRatio, (float)u, 0.5f,
                        out float radialNorm, out float replicaDepth);

                    Assert.True(hitDouble == hitFloat || factor >= 0.999,
                        $"hit/miss disagreement at ratio={ratio} factor={factor} indicates float limb uncertainty");
                    if (hitDouble && hitFloat)
                    {
                        Assert.True(Math.Abs(replicaDepth - depth) <= 1e-4,
                            $"near-limb depth float={replicaDepth} vs double={depth} ratio={ratio} factor={factor}");
                    }
                }
            }
        }

        [Fact]
        public void FragmentSource_PinsSquaredTransverseAndZeroOutside()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, ProbeType);
            string source = (string)type.Fields.Single(f => f.Name == "FragmentSource").Constant;

            Assert.Contains("layout(push_constant)", source);
            Assert.Contains("float transverse2 = dot(ray.xy, ray.xy);", source);
            Assert.Contains("float physicalDisc = r * r - transverse2;", source);
            Assert.Contains("float proceduralDisc = (2.75 * r) * (2.75 * r) - transverse2;", source);
            Assert.Contains("float envelopeDisc = (4.0 * r) * (4.0 * r) - transverse2;", source);
            Assert.Contains("gl_FragDepth = 0.0;", source);
            Assert.Contains("gl_FragDepth = depth;", source);
            Assert.DoesNotContain("texture(", source);
            Assert.DoesNotContain("sampler", source);
        }

        [Fact]
        public void PushConstants_AreSequentialFloat4UploadedToFragment()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, ProbeType);
            var push = type.NestedTypes.Single(t => t.Name == "SolarCoveragePush");
            Assert.True(push.IsSequentialLayout, "SolarCoveragePush must be sequential-layout for push constants");
            Assert.EndsWith("float4", Assert.Single(push.Fields).FieldType.Name);

            var ctorCalls = CallsOf(type.Methods.Single(m => m.Name == ".ctor"));
            Assert.Contains(ctorCalls, c => c.Name == "Of");
            var ofCall = type.Methods.Single(m => m.Name == ".ctor").Body.Instructions
                .Where(i => i.Operand is GenericInstanceMethod)
                .Select(i => (GenericInstanceMethod)i.Operand)
                .SingleOrDefault(g => g.Name == "Of" && g.DeclaringType.Name == "ByteSize");
            Assert.NotNull(ofCall);
            Assert.Equal(ProbeType + "/SolarCoveragePush", ofCall.GenericArguments[0].FullName);

            var record = SingleMethod(assembly.MainModule, ProbeType, "Record");
            var recordCalls = CallsOf(record);
            Assert.True(recordCalls.Count(c => c.Name == "PushConstants") == 1,
                "Record must upload the push constant block exactly once");
        }

        [RendererBaselineFact]
        public void PushConstantStruct_IsSixteenBytes()
        {
            Assert.Equal(16, System.Runtime.InteropServices.Marshal.SizeOf<SolarCoverageProbe.SolarCoveragePush>());
        }

        [Fact]
        public void Probe_OwnsTargetModulesLayoutPipelineAndDepthState()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, ProbeType);
            var ctorCalls = CallsOf(type.Methods.Single(m => m.Name == ".ctor"));

            Assert.Equal(1, ctorCalls.Count(c => c.Name == ".ctor" && c.DeclaringType.EndsWith("RenderTarget")));
            Assert.Equal(2, ctorCalls.Count(c => c.Name == "CompileModule"));
            Assert.Equal(1, ctorCalls.Count(c => c.Name == "CreatePipelineLayout"));
            Assert.Equal(1, ctorCalls.Count(c => c.Name == "BuildPipeline"));
            Assert.Equal(1, ctorCalls.Count(c => c.DeclaringType.StartsWith("Brutal.Memory.PtrOwner`1") && c.Name == ".ctor"));

            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_target"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_vertexModule"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_fragmentModule"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_pipelineLayout"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_pipeline"));
            Assert.NotNull(type.Fields.SingleOrDefault(f => f.Name == "_depthState"));

            Assert.Contains(type.Methods.Single(m => m.Name == ".ctor").Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && i.Operand is string text
                    && text == AtlasCompositeProbe.VertexSource);

            var buildCalls = CallsOf(SingleMethod(assembly.MainModule, ProbeType, "BuildPipeline"));
            Assert.Equal(1, buildCalls.Count(c => c.Name == "CreateGraphicsPipeline"));
            var depthStores = SingleMethod(assembly.MainModule, ProbeType, ".ctor").Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name).ToList();
            Assert.Contains("DepthTestEnable", depthStores);
            Assert.Contains("DepthWriteEnable", depthStores);
            Assert.Contains("DepthCompareOp", depthStores);
            Assert.Contains(FieldRefsOf(SingleMethod(assembly.MainModule, ProbeType, "BuildPipeline")),
                f => f.Name == "_depthState");
        }

        [Fact]
        public void Probe_HasNoDescriptorsGlobalsOrNativeSolarPath()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var allCalls = AllCallsOf(TypeOf(assembly.MainModule, ProbeType));

            Assert.Equal(0, allCalls.Count(c => c.DeclaringType.StartsWith("KSA.GlobalShaderBindings")));
            Assert.Equal(0, allCalls.Count(c => c.Name is "AllocateDescriptorSet" or "CreateDescriptorPool"
                or "CreateDescriptorSetLayout" or "UpdateDescriptorSets" or "BindDescriptorSets"));
            Assert.Equal(0, allCalls.Count(c => c.DeclaringType == "KSARS.Visuals.NativeSolarCaptureAdapter"));
            Assert.Equal(0, allCalls.Count(c => c.DeclaringType == "KSARS.Visuals.ObjectCaptureContext"));
            Assert.Equal(0, allCalls.Count(c => c.Name == "RenderGame"));
            Assert.Equal(0, allCalls.Count(c => c.Name == "RenderViewport"));
            Assert.Equal(0, allCalls.Count(c => c.Name == "WaitIdle"));
        }

        private static bool IsStloc(OpCode op) =>
            op == OpCodes.Stloc || op == OpCodes.Stloc_S || op == OpCodes.Stloc_0
            || op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3;

        [Fact]
        public void Record_BalancesRenderingAndRestoresViewport()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = SingleMethod(assembly.MainModule, ProbeType, "Record");
            var calls = CallsOf(record);

            Assert.Equal(1, calls.Count(c => c.Name == "BeginRendering"));
            Assert.Equal(1, calls.Count(c => c.Name == "EndRendering"));
            Assert.Equal(1, calls.Count(c => c.Name == "Draw"));
            Assert.Equal(1, calls.Count(c => c.DeclaringType == "KSA.Program" && c.Name == "SetViewport"));
            Assert.True(calls.Count(c => c.Name == "SetViewport") >= 2,
                "Record must set the explicit source viewport and restore the program viewport");
            Assert.Equal(1, calls.Count(c => c.Name == "BindPipeline"));

            Assert.True(record.Body.ExceptionHandlers.Count(h => h.HandlerType == ExceptionHandlerType.Finally) >= 2,
                "Record must end the render pass in a finally and restore the viewport in an outer finally");

            var instructions = record.Body.Instructions.ToList();
            int IndexOf(string name, string? declaring = null) =>
                instructions.FindIndex(i => i.Operand is MethodReference mr && mr.Name == name
                    && (declaring is null || mr.DeclaringType.FullName == declaring));

            int uploadIndex = IndexOf("GeometryUploadSupported", ProbeType);
            int flagIndex = IndexOf("set_RecordingRecovered", ProbeType);
            int beginIndex = IndexOf("BeginRendering");
            int bindIndex = IndexOf("BindPipeline");
            int pushIndex = IndexOf("PushConstants");
            int drawIndex = IndexOf("Draw");
            int endIndex = IndexOf("EndRendering");
            int viewportIndex = IndexOf("SetViewport", "KSA.Program");
            int recoveryIndex = IndexOf("RecordingRecoveredState", ProbeType);

            Assert.True(uploadIndex >= 0 && flagIndex > uploadIndex,
                "float upload validation must precede RecordingRecovered=false");
            Assert.True(beginIndex > flagIndex,
                "RecordingRecovered=false must precede the attempted begin");
            Assert.Contains(instructions.Skip(flagIndex + 1).Take(beginIndex - flagIndex - 1),
                i => IsStloc(i.OpCode));
            Assert.True(bindIndex > beginIndex && pushIndex > bindIndex && drawIndex > pushIndex
                && endIndex > drawIndex,
                "Record must order Begin -> Bind own pipeline -> Push -> Draw -> End");
            Assert.Contains(FieldRefsOf(record),
                f => f.DeclaringType == ProbeType && f.Name == "_pipeline");
            Assert.True(recoveryIndex > viewportIndex,
                "safe recovery is decided only after the program viewport restore succeeds");
        }

        [Fact]
        public void Dispose_AttemptsEveryOwnedResourceIndependently()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var dispose = SingleMethod(assembly.MainModule, ProbeType, "Dispose");
            var calls = CallsOf(dispose);

            Assert.Equal(1, calls.Count(c => c.Name == "RemoveTexture"));
            Assert.Equal(1, calls.Count(c => c.Name == "DestroyPipeline"));
            Assert.Equal(1, calls.Count(c => c.Name == "DestroyPipelineLayout"));
            Assert.Equal(2, calls.Count(c => c.Name == "DestroyShaderModule"));
            Assert.True(calls.Count(c => c.Name == "Dispose") >= 2,
                "Dispose must dispose the PtrOwner depth state and the owned target");

            Assert.True(dispose.Body.ExceptionHandlers.Count >= 7,
                "each owned resource must be disposed inside its own try/catch so one failure cannot leak the rest");
            Assert.Contains(calls, c => c.DeclaringType == "System.AggregateException" && c.Name == ".ctor");
        }

        [Fact]
        public void Coordinator_CreatesCoverageBeforeRecordAndRethrowsUnbalanced()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var renderer = TypeOf(assembly.MainModule, RendererType);
            var method = renderer.Methods.Single(m => m.Name == "TryRecordSolarCoverage");
            var calls = CallsOf(method);

            Assert.Contains(calls, c => c.DeclaringType == "KSARS.Visuals.SolarSourceGeometry" && c.Name == "TryCreate");
            Assert.Equal(1, calls.Count(c => c.DeclaringType == ProbeType && c.Name == ".ctor"));
            Assert.Equal(1, calls.Count(c => c.DeclaringType == ProbeType && c.Name == "Record"));
            Assert.Equal(1, calls.Count(c => c.DeclaringType == ProbeType && c.Name == "RegisterPreviewTexture"));

            var instructions = method.Body.Instructions.ToList();
            int ctorIndex = instructions.FindIndex(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == ProbeType && mr.Name == ".ctor");
            int storeIndex = instructions.FindIndex(i => i.OpCode == OpCodes.Stfld
                && i.Operand is FieldReference fr
                && fr.Name == "Coverage" && fr.DeclaringType.Name == "SolarProbeCapture");
            int recordIndex = instructions.FindIndex(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == ProbeType && mr.Name == "Record");
            Assert.True(ctorIndex >= 0 && storeIndex > ctorIndex && recordIndex > storeIndex,
                "coverage must be constructed and rooted in SolarProbeCapture.Coverage before Record runs");
            Assert.Contains(instructions, i => i.OpCode == OpCodes.Rethrow);
            Assert.Contains(FieldRefsOf(method), f => f.Name == "s_solarCoverageStatus");
        }

        [Fact]
        public void Coordinator_UnbalancedGuardCoversCurrentAndArchivedCoverage()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var renderer = TypeOf(assembly.MainModule, RendererType);
            var allCalls = AllCallsOf(renderer);
            var coverageFieldRefs = AllMethods(renderer).Where(m => m.HasBody)
                .SelectMany(FieldRefsOf)
                .ToList();

            Assert.Contains(coverageFieldRefs, f => f.Name == "Coverage");
            Assert.True(allCalls.Count(c => c.DeclaringType == ProbeType && c.Name == "get_RecordingRecovered") >= 2,
                "current and archived coverage must contribute to the unbalanced-recording guard");

            var recordSource = renderer.Methods.Single(m => m.Name == "TryRecordSolarSource");
            Assert.Contains(CallsOf(recordSource), c => c.Name == "TryRecordSolarCoverage");
        }

        [Fact]
        public void Coordinator_ArchiveRetainsCoverageAndShutdownDisposesAfterIdle()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var renderer = TypeOf(assembly.MainModule, RendererType);
            var archive = renderer.Methods.Single(m => m.Name == "ArchiveFrozenSources");
            var archiveFields = FieldRefsOf(archive);
            var archiveCalls = CallsOf(archive);

            Assert.Contains(archiveFields, f => f.Name == "s_solarArchive");
            Assert.Contains(archiveCalls, c => c.Name == "Add");
            Assert.Contains(archiveFields, f => f.Name == "s_solarCoverageStatus");

            var solarCapture = renderer.NestedTypes.Single(t => t.Name == "SolarProbeCapture");
            Assert.NotNull(solarCapture.Fields.SingleOrDefault(
                f => f.Name == "Coverage" && f.FieldType.FullName == ProbeType));

            var shutdown = renderer.Methods.Single(m => m.Name == "Shutdown");
            var instructions = shutdown.Body.Instructions.ToList();
            int idleIndex = instructions.FindIndex(i => i.Operand is MethodReference mr && mr.Name == "WaitIdle");
            int coverageDisposeIndex = instructions.FindIndex(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == ProbeType && mr.Name == "Dispose");
            Assert.True(idleIndex >= 0 && coverageDisposeIndex > idleIndex,
                "coverage disposal must run only after the verified-idle device wait");
            Assert.True(instructions.FindLastIndex(i => i.Operand is MethodReference mr
                && mr.Name == "RemoveTexture") < coverageDisposeIndex,
                "raw solar preview textures are removed before coverage disposal in the same verified-idle shutdown");

            var status = renderer.Methods.Single(m => m.Name == "GetStatus");
            Assert.Contains(FieldRefsOf(status), f => f.Name == "s_solarCoverageStatus");
        }

        [Fact]
        public void StatusStrings_NeverClaimNativeOpacityOrCalibration()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var renderer = TypeOf(assembly.MainModule, RendererType);
            var strings = AllMethods(renderer).Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions)
                .Where(i => i.OpCode == OpCodes.Ldstr)
                .Select(i => (string)i.Operand)
                .ToList();

            Assert.Contains(strings, s => s.Contains("not native alpha/opaque/calibration acceptance"));
            Assert.Contains(strings, s => s.Contains("raw solar color/corona alpha only, no depth/opaque/calibration claim"));
            Assert.DoesNotContain(strings, s => s.Contains("solar coverage accepted") || s.Contains("measured opacity"));
        }
    }
}

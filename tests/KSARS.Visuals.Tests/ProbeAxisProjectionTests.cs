using System.Numerics;
using KSARS.Visuals;
using Mono.Cecil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class ProbeAxisProjectionTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");

        private static OpticalVector V(double x, double y, double z) => new(x, y, z);

        private static Matrix4x4 NativeReverseZPerspective(float near, float far)
        {
            float cot = 1f / MathF.Tan((float)(60.0 * Math.PI / 180.0) * 0.5f);
            var projection = Matrix4x4.Identity;
            projection.M11 = cot / (16f / 9f);
            projection.M22 = -cot;
            projection.M33 = near / (far - near);
            projection.M34 = -1f;
            projection.M43 = near * far / (far - near);
            projection.M44 = 0f;
            return projection;
        }

        private static bool ProjectUv(Vector3 viewDirection, Matrix4x4 projection,
            out double u, out double v)
        {
            Vector4 clip = Vector4.Transform(new Vector4(viewDirection, 0f), projection);
            return ProbeAxisProjection.TryGetUv(clip.X, clip.Y, clip.W, out u, out v);
        }

        [Fact]
        public void ViewAngles_CardinalAxes()
        {
            Assert.True(ProbeAxisProjection.TryGetViewAngles(V(0, 0, -1), out double yaw, out double pitch));
            Assert.Equal(0.0, yaw, 6);
            Assert.Equal(0.0, pitch, 6);

            Assert.True(ProbeAxisProjection.TryGetViewAngles(V(1, 0, 0), out yaw, out pitch));
            Assert.Equal(90.0, yaw, 6);
            Assert.Equal(0.0, pitch, 6);

            Assert.True(ProbeAxisProjection.TryGetViewAngles(V(-1, 0, 0), out yaw, out _));
            Assert.Equal(-90.0, yaw, 6);

            Assert.True(ProbeAxisProjection.TryGetViewAngles(V(0, 0, 1), out yaw, out _));
            Assert.Equal(180.0, Math.Abs(yaw), 6);

            Assert.True(ProbeAxisProjection.TryGetViewAngles(V(0, 1, 0), out _, out pitch));
            Assert.Equal(90.0, pitch, 6);

            Assert.True(ProbeAxisProjection.TryGetViewAngles(V(0, -1, 0), out _, out pitch));
            Assert.Equal(-90.0, pitch, 6);

            Assert.False(ProbeAxisProjection.TryGetViewAngles(V(double.NaN, 0, 0), out _, out _));
        }

        [Fact]
        public void ViewAngles_ZeroRejected_ExtremeFiniteVectorStillCorrect()
        {
            Assert.False(ProbeAxisProjection.TryGetViewAngles(OpticalVector.Zero, out _, out _));

            Assert.True(ProbeAxisProjection.TryGetViewAngles(
                V(1e308, 0.0, -1e308), out double yaw, out double pitch));
            Assert.Equal(45.0, yaw, 6);
            Assert.Equal(0.0, pitch, 6);

            Assert.True(ProbeAxisProjection.TryGetViewAngles(
                V(0.0, 1e308, -1e308), out _, out pitch));
            Assert.Equal(45.0, pitch, 6);
        }

        [Fact]
        public void Uv_NativeVulkanProjection_LeftRightTopBottom()
        {
            Matrix4x4 projection = NativeReverseZPerspective(1f, 1e9f);

            Assert.True(ProjectUv(new Vector3(0f, 0f, -1f), projection, out double u, out double v));
            Assert.Equal(0.5, u, 6);
            Assert.Equal(0.5, v, 6);

            Assert.True(ProjectUv(new Vector3(-1f, 0f, -1f), projection, out u, out _));
            Assert.True(u < 0.5, $"view-left must map to u<0.5, got {u}");

            Assert.True(ProjectUv(new Vector3(1f, 0f, -1f), projection, out u, out _));
            Assert.True(u > 0.5, $"view-right must map to u>0.5, got {u}");

            Assert.True(ProjectUv(new Vector3(0f, 1f, -1f), projection, out _, out v));
            Assert.True(v < 0.5, $"view-up must map to v<0.5 under Vulkan NDC y-down, got {v}");

            Assert.True(ProjectUv(new Vector3(0f, -1f, -1f), projection, out _, out v));
            Assert.True(v > 0.5, $"view-down must map to v>0.5 under Vulkan NDC y-down, got {v}");
        }

        [Fact]
        public void Uv_BehindInvalidRejected_OffscreenReturnedUnclamped()
        {
            Matrix4x4 projection = NativeReverseZPerspective(1f, 1e9f);

            Assert.False(ProjectUv(new Vector3(0f, 0f, 1f), projection, out _, out _));
            Assert.False(ProbeAxisProjection.TryGetUv(0.0, 0.0, 0.0, out _, out _));
            Assert.False(ProbeAxisProjection.TryGetUv(0.0, 0.0, -0.5, out _, out _));
            Assert.False(ProbeAxisProjection.TryGetUv(double.NaN, 0.0, 1.0, out _, out _));
            Assert.False(ProbeAxisProjection.TryGetUv(0.0, double.PositiveInfinity, 1.0, out _, out _));
            Assert.False(ProbeAxisProjection.TryGetUv(0.0, 0.0, double.NaN, out _, out _));

            Assert.True(ProjectUv(new Vector3(5f, 0f, -1f), projection, out double u, out _));
            Assert.True(u > 1.0, $"off-screen direction must not be clamped, got u={u}");
        }

        [Fact]
        public void AngleDegrees_UnitOrthoOpposite()
        {
            Assert.True(ProbeAxisProjection.TryAngleDegrees(V(1, 0, 0), V(1, 0, 0), out double deg));
            Assert.Equal(0.0, deg, 9);
            Assert.True(ProbeAxisProjection.TryAngleDegrees(V(1, 0, 0), V(0, 1, 0), out deg));
            Assert.Equal(90.0, deg, 9);
            Assert.True(ProbeAxisProjection.TryAngleDegrees(V(1, 0, 0), V(-1, 0, 0), out deg));
            Assert.Equal(180.0, deg, 9);
            Assert.False(ProbeAxisProjection.TryAngleDegrees(OpticalVector.Zero, V(1, 0, 0), out _));
        }

        [Fact]
        public void ParentVelocityAddition_SyntheticVectors_CanProduceHundredTwentyDegrees()
        {
            var velocityCci = V(1.0, 0.0, 0.0);
            var parentEcl = V(-1.5, 0.0, Math.Sqrt(3.0) / 2.0);
            OpticalVector velocityEcl = velocityCci + parentEcl;
            Assert.True(ProbeAxisProjection.TryAngleDegrees(velocityCci, velocityEcl, out double deg));
            Assert.Equal(120.0, deg, 9);
        }

        private static PresentationVelocity ObserverAtQ(OpticalVector axis, double q)
        {
            Assert.True(RelativisticOptics.TryNormalize(axis, out OpticalVector unit));
            double beta = (1.0 - q * q) / (1.0 + q * q);
            Assert.True(RelativisticOptics.TryCreateVelocity(unit * (beta * TestOptics.C),
                out PresentationVelocity observer));
            TestOptics.AssertRel(q, observer.Q, 1e-6);
            return observer;
        }

        private static IEnumerable<OpticalVector> SampleDirections()
        {
            for (int i = 0; i < 256; i++)
            {
                double z = 1.0 - 2.0 * (i + 0.5) / 256.0;
                double phi = i * 2.39996322972865332;
                double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
                yield return new OpticalVector(r * Math.Cos(phi), r * Math.Sin(phi), z);
            }
        }

        [Fact]
        public void ForwardAberration_MovesRestSourcesTowardVelocityAxis()
        {
            var axis = V(0.3, -0.5, 0.8);
            PresentationVelocity observer = ObserverAtQ(axis, 0.25);
            OpticalVector u = observer.Direction;

            foreach (OpticalVector rest in SampleDirections())
            {
                Assert.True(RelativisticOptics.TryForward(rest, in observer, out OpticalRay ray));
                Assert.True(ProbeAxisProjection.TryAngleDegrees(ray.Direction, u, out double observedAngle));
                Assert.True(ProbeAxisProjection.TryAngleDegrees(rest, u, out double restAngle));
                if (restAngle > 1e-6 && restAngle < 180.0 - 1e-6)
                {
                    Assert.True(observedAngle < restAngle,
                        $"observed direction must fold toward +u: rest {restAngle} observed {observedAngle}");
                }
            }
        }

        [Fact]
        public void ForwardAberration_ProjectedUv_MovesTowardVelocityAxisForRotatedCamera()
        {
            var axis = V(0.2, 0.1, -1.0);
            PresentationVelocity observer = ObserverAtQ(axis, 0.25);
            OpticalVector u = observer.Direction;

            Matrix4x4 view = Matrix4x4.CreateFromYawPitchRoll(0.3f, -0.15f, 0.2f);
            Matrix4x4 viewProjection = view * NativeReverseZPerspective(1f, 1e9f);

            static bool Project(Vector3 dir, Matrix4x4 vp, out Vector2 uv)
            {
                Vector4 clip = Vector4.Transform(new Vector4(dir, 0f), vp);
                uv = Vector2.Zero;
                if (clip.W <= 0f)
                {
                    return false;
                }

                uv = new Vector2(clip.X / clip.W * 0.5f + 0.5f, clip.Y / clip.W * 0.5f + 0.5f);
                return float.IsFinite(uv.X) && float.IsFinite(uv.Y);
            }

            var uvU = new Vector3((float)u.X, (float)u.Y, (float)u.Z);
            Assert.True(Project(uvU, viewProjection, out Vector2 uvAxis));

            int checkedSamples = 0;
            foreach (OpticalVector rest in SampleDirections())
            {
                Assert.True(RelativisticOptics.TryForward(rest, in observer, out OpticalRay ray));
                var restV = new Vector3((float)rest.X, (float)rest.Y, (float)rest.Z);
                var obsV = new Vector3((float)ray.Direction.X, (float)ray.Direction.Y, (float)ray.Direction.Z);
                if (!Project(restV, viewProjection, out Vector2 uvRest)
                    || !Project(obsV, viewProjection, out Vector2 uvObserved))
                {
                    continue;
                }

                checkedSamples++;
                Assert.True(Vector2.Distance(uvObserved, uvAxis) <= Vector2.Distance(uvRest, uvAxis) + 1e-4f,
                    $"projected observed source must move toward +u: rest dist {Vector2.Distance(uvRest, uvAxis)} " +
                    $"observed {Vector2.Distance(uvObserved, uvAxis)}");
            }

            Assert.True(checkedSamples > 32, $"too few in-front samples ({checkedSamples})");
        }

        [Fact]
        public void DebugDoppler_MaximumAtPlusVelocityAxis()
        {
            var axis = V(0.3, -0.5, 0.8);
            PresentationVelocity observer = ObserverAtQ(axis, 0.25);
            OpticalVector u = observer.Direction;

            Assert.True(RelativisticOptics.TryInverse(u, in observer, out OpticalRay forward));
            TestOptics.AssertRel(1.0 / observer.Q, forward.DopplerFactor, 1e-12);

            foreach (OpticalVector n in SampleDirections())
            {
                Assert.True(RelativisticOptics.TryInverse(n, in observer, out OpticalRay ray));
                Assert.True(ray.DopplerFactor <= forward.DopplerFactor * (1.0 + 1e-12),
                    $"doppler {ray.DopplerFactor} at sample exceeded forward maximum {forward.DopplerFactor}");
            }
        }

        [Fact]
        public void ForwardAberration_NearLightSpeed_CollapsesToTinyCone()
        {
            var axis = V(0.3, -0.5, 0.8);
            PresentationVelocity observer = ObserverAtQ(axis, 1e-4);
            OpticalVector u = observer.Direction;

            foreach (OpticalVector rest in SampleDirections())
            {
                Assert.True(ProbeAxisProjection.TryAngleDegrees(rest, u, out double restAngle));
                if (restAngle > 170.0)
                {
                    continue;
                }

                Assert.True(RelativisticOptics.TryForward(rest, in observer, out OpticalRay ray));
                Assert.True(ProbeAxisProjection.TryAngleDegrees(ray.Direction, u, out double observedAngle));
                Assert.True(observedAngle < 1.0,
                    $"near-c observed cone must collapse toward +u, got {observedAngle}deg for rest {restAngle}deg");
            }
        }

        private static List<(string DeclaringType, string Name)> CallsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Where(i => (i.OpCode == Mono.Cecil.Cil.OpCodes.Call
                    || i.OpCode == Mono.Cecil.Cil.OpCodes.Callvirt)
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

        private static MethodDefinition SingleMethod(ModuleDefinition module, string typeName, string methodName)
        {
            var type = module.GetType(typeName)
                ?? module.Types.SelectMany(AllNested).FirstOrDefault(t => t.FullName == typeName);
            Assert.NotNull(type);
            return type.Methods.Single(m => m.Name == methodName);
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

        [Fact]
        public void AxisDiagnostic_UsesPublishedViewMatricesAndNavballMarker()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var diag = SingleMethod(assembly.MainModule,
                "KSARS.Visuals.RelativisticSpaceRenderer", "UpdateAxisDiagnostic");
            var calls = CallsOf(diag);
            var refs = FieldRefsOf(diag);

            Assert.Contains(calls, c => c.DeclaringType == "KSA.Program" && c.Name == "get_ControlledVehicle");
            Assert.Contains(calls, c => c.DeclaringType == "KSA.Vehicle" && c.Name == "get_NavBallMarkerData");
            Assert.Contains(calls, c => c.DeclaringType == "KSA.Vehicle" && c.Name == "get_NavBallData");
            Assert.Contains(calls, c => c.Name == "GetHelm2Cce");
            Assert.Contains(calls, c => c.Name == "ToPlayerString");
            Assert.Contains(calls, c => c.Name == "Transform");
            Assert.True(calls.Count(c => c.Name == "Transform") >= 2,
                "both the view and view-projection transforms must come from published camera matrices");
            Assert.Contains(calls,
                c => c.DeclaringType == "KSARS.Visuals.ProbeAxisProjection" && c.Name == "TryGetViewAngles");
            Assert.Contains(calls,
                c => c.DeclaringType == "KSARS.Visuals.ProbeAxisProjection" && c.Name == "TryGetUv");
            Assert.Contains(calls,
                c => c.DeclaringType == "KSARS.Visuals.ProbeAxisProjection" && c.Name == "TryAngleDegrees");
            Assert.Contains(calls, c => c.Name == "get_Id");
            Assert.Contains(calls, c => c.Name == "get_Observer");

            Assert.Contains(refs, r => r.DeclaringType == "KSA.UboCameraData" && r.Name == "View");
            Assert.Contains(refs, r => r.DeclaringType == "KSA.UboCameraData" && r.Name == "ViewProjection");
            Assert.Contains(refs, r => r.DeclaringType == "KSA.NavBallMarkerData" && r.Name == "VelocityDirHelm");
            Assert.Contains(refs, r => r.DeclaringType == "KSA.NavBallMarkerData" && r.Name == "VelocityValid");
            Assert.Contains(refs, r => r.DeclaringType == "KSA.NavBallData" && r.Name == "Frame");
            Assert.DoesNotContain(refs, r => r.Name == "WorldRotation" || r.Name == "LocalRotation");
        }

#if DEBUG
        [Fact]
        public void AxisDiagnostic_WiredAtBothRecordBoundaries()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var module = assembly.MainModule;
            var composite = SingleMethod(module,
                "KSARS.Visuals.RelativisticSpaceRenderer", "TryRecordComposite");
            var prepare = SingleMethod(module,
                "KSARS.Visuals.RelativisticSpaceRenderer", "PrepareMainFrameSources");
            Assert.Contains(CallsOf(composite),
                c => c.Name == "UpdateAxisDiagnostic");
            Assert.Contains(CallsOf(prepare),
                c => c.Name == "UpdateAxisDiagnostic");
        }
#endif
    }
}

using KSARS.Visuals;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    [Collection("VisualHookState")]
    public class MediaProbeTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");

        private static AssemblyDefinition LoadModAssembly() => AssemblyDefinition.ReadAssembly(ModAssemblyPath);

        private static TypeDefinition ModType(AssemblyDefinition assembly, string name)
        {
            var type = assembly.MainModule.GetType(name);
            Assert.NotNull(type);
            return type;
        }

        private static OpticalVector Velocity(OpticalVector direction, double beta) =>
            direction * (beta * TestOptics.C);

        [Fact]
        public void RelativeObserver_CoMoving_ReturnsFalse()
        {
            var shared = Velocity(new OpticalVector(0.6, 0.8, 0.0), 0.4);
            Assert.False(RelativisticSpaceRenderer.TryRelativeObserver(
                in shared, in shared, out PresentationVelocity _));
        }

        [Fact]
        public void RelativeObserver_RestObserverAndBody_ReturnsFalse()
        {
            var rest = OpticalVector.Zero;
            Assert.False(RelativisticSpaceRenderer.TryRelativeObserver(
                in rest, in rest, out PresentationVelocity _));
        }

        [Fact]
        public void RelativeObserver_MovingObserverRestBody_MatchesObserver()
        {
            var observer = Velocity(new OpticalVector(1.0, 0.0, 0.0), 0.5);
            var bodyRest = OpticalVector.Zero;
            Assert.True(RelativisticSpaceRenderer.TryRelativeObserver(
                in observer, in bodyRest, out PresentationVelocity relative));
            Assert.True(RelativisticOptics.TryCreateVelocity(observer, out PresentationVelocity direct));
            TestOptics.AssertRel(direct.Q, relative.Q, 1e-12);
            TestOptics.AssertRel(direct.Direction.X, relative.Direction.X, 1e-12);
        }

        [Fact]
        public void RelativeObserver_SubtractsBodyVelocity()
        {
            var observer = Velocity(new OpticalVector(1.0, 0.0, 0.0), 0.5);
            var body = Velocity(new OpticalVector(0.0, 1.0, 0.0), 0.3);
            Assert.True(RelativisticSpaceRenderer.TryRelativeObserver(
                in observer, in body, out PresentationVelocity relative));
            var expected = new OpticalVector(0.5 * TestOptics.C, -0.3 * TestOptics.C, 0.0);
            Assert.True(RelativisticOptics.TryCreateVelocity(expected, out PresentationVelocity direct));
            TestOptics.AssertRel(direct.Speed, relative.Speed, 1e-9);
            TestOptics.AssertRel(direct.Direction.X, relative.Direction.X, 1e-9);
            TestOptics.AssertRel(direct.Direction.Y, relative.Direction.Y, 1e-9);
            TestOptics.AssertRel(direct.Q, relative.Q, 1e-9);
        }

        [Fact]
        public void RelativeObserver_OrbitalSpeeds_NearlyRestButNonzero()
        {
            // Vessel orbiting a planet: observer velocity is the planet's star-
            // frame velocity plus the orbital component; relative speed is the
            // orbital speed only, so the parent body's apparent shift is real
            // but small (~arcseconds at km/s).
            double orbital = 29780.0 + 7800.0;
            var observer = new OpticalVector(orbital, 0.0, 0.0);
            var planet = new OpticalVector(29780.0, 0.0, 0.0);
            Assert.True(RelativisticSpaceRenderer.TryRelativeObserver(
                in observer, in planet, out PresentationVelocity relative));
            TestOptics.AssertRel(7800.0, relative.Speed, 1e-9);
        }

        [Fact]
        public void AberratedBodyPosition_PreservesDistance_TiltsTowardVelocity()
        {
            // Integration shape of the postfix path: rest ego -> relative
            // observer -> TryForward -> distance-preserved apparent position.
            var observer = Velocity(new OpticalVector(1.0, 0.0, 0.0), 0.5);
            var bodyRest = OpticalVector.Zero;
            Assert.True(RelativisticSpaceRenderer.TryRelativeObserver(
                in observer, in bodyRest, out PresentationVelocity relative));

            var restEgo = new OpticalVector(0.0, 4.2e10, -1.3e9);
            double distance = Math.Sqrt(restEgo.LengthSquared());
            Assert.True(RelativisticOptics.TryForward(restEgo, in relative, out OpticalRay ray));
            var apparent = ray.Direction * distance;

            TestOptics.AssertRel(distance, Math.Sqrt(apparent.LengthSquared()), 1e-12);
            Assert.True(apparent.X > 0.0);
            // Transverse components contract uniformly by 1/gamma, so their
            // ratio is preserved and the y component shrinks.
            TestOptics.AssertRel(restEgo.Z / restEgo.Y, apparent.Z / apparent.Y, 1e-12);
            Assert.True(apparent.Y < restEgo.Y);
            Assert.True(apparent.Z < 0.0);
        }

        [Fact]
        public void Install_ResolvesPositionSeam_AndTranspileTargets()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition install = type.Methods.Single(m => m.Name == "TryInstallMediaProbe");
            string[] strings = install.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldstr)
                .Select(i => (string)i.Operand)
                .ToArray();
            // The five planet-relative camera-position methods, the ego seam,
            // and the shading-rest postfix/fields must all be named so
            // signature drift fails closed.
            foreach (string required in new[]
            {
                "GetPositionEgo", "GetPositionEgoPostfix",
                "TranspileRelativeCameraPositions", "RelativeCameraPosition",
                "Render", "RenderWithCompute", "UpdateShadowVolume",
                "RenderVolumetricTrailsOnly",
                "UpdateShaderData", "UpdateShaderDataShadingRestPostfix",
                "_lightingData", "_sunPositionEgo",
            })
            {
                Assert.Contains(strings, s => s == required || s.Contains(required));
            }
        }

        [Fact]
        public void Transpiler_MatchesEclDifferencePattern()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            string[] strings = type.Methods
                .Where(m => m.Name is "TranspileRelativeCameraPositions" or "IsCallNamed" or "IsSubtractionCall")
                .SelectMany(m => m.Body.Instructions)
                .Where(i => i.OpCode == OpCodes.Ldstr)
                .Select(i => (string)i.Operand)
                .ToArray();
            Assert.Contains("get_PositionEcl", strings);
            Assert.Contains("GetPositionEcl", strings);
            Assert.Contains("op_Subtraction", strings);
        }

        [Fact]
        public void RenderHooks_GatesMediaProbe_ByEnvironmentVariable()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition hooks = ModType(assembly, "KSARS.Visuals.RenderHooks");
            MethodDefinition install = hooks.Methods.Single(m => m.Name == "TryInstall");
            string[] strings = install.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldstr)
                .Select(i => (string)i.Operand)
                .ToArray();
            Assert.Contains("KSARS_MEDIA_PROBE", strings);
        }

        [Fact]
        public void Snapshot_RestoresRestPositions_BeforeCapture()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition snapshot = ModType(assembly, "KSARS.Visuals.ObserverSnapshot");
            MethodDefinition capture = snapshot.Methods.Single(m => m.Name == "TryCapture");
            Assert.Contains(capture.Body.Instructions, i =>
                i.OpCode == OpCodes.Call
                && i.Operand is MethodReference call
                && call.Name == "RestoreRestBodyPositions");
        }

        [Fact]
        public void Restify_CoversSunPosition_SoProbesKeepSingleTransform()
        {
            // SunPositionRadius is packed from GetPositionEgo(WorldSun); with
            // the sun aberrating, the snapshot restify must restore it or the
            // lighting/shadow probes would double-aberrate.
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition restify = type.Methods.Single(m => m.Name == "RestoreRestBodyPositions");
            Assert.Contains(restify.Body.Instructions, i =>
                i.Operand is MethodReference call && call.Name == "get_WorldSun");
        }

        [Fact]
        public void SolarCandidatePaths_UseDirectEclDifference_NotPatchedSeam()
        {
            // Both solar candidate callsites must compute rest geometry via
            // GetPositionEcl - PositionEcl rather than the patched
            // GetPositionEgo, so captures keep rest inputs.
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            foreach (string name in new[] { "TryRecordSolarSource", "TryPrepareMainSolarSource" })
            {
                MethodDefinition method = type.Methods.Single(m => m.Name == name);
                Assert.DoesNotContain(method.Body.Instructions, i =>
                    i.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "KSA.Camera" && mr.Name == "GetPositionEgo");
                Assert.Contains(method.Body.Instructions, i =>
                    i.Operand is MethodReference mr && mr.Name == "GetPositionEcl");
            }
        }

        [Fact]
        public void MediaHelpers_Exist_WithExpectedSignatures()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            Assert.Contains(type.Methods, m => m.Name == "GetPositionEgoPostfix"
                && m.Parameters.Count == 3);
            Assert.Contains(type.Methods, m => m.Name == "TranspileRelativeCameraPositions");
            Assert.Contains(type.Methods, m => m.Name == "RelativeCameraPosition"
                && m.Parameters.Count == 2);
            Assert.Contains(type.Methods, m => m.Name == "TryAberrateEgo");
            Assert.Contains(type.Methods, m => m.Name == "RestoreRestBodyPositions");
            Assert.Contains(type.Methods, m => m.Name == "ResetMediaProbeState");
        }
    }
}

using System.Reflection;
using KSARS.Visuals;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    [Collection("VisualHookState")]
    public class VesselLightingProbeTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");

        private static AssemblyDefinition LoadModAssembly() => AssemblyDefinition.ReadAssembly(ModAssemblyPath);

        private static TypeDefinition ModType(AssemblyDefinition assembly, string name)
        {
            var type = assembly.MainModule.GetType(name);
            Assert.NotNull(type);
            return type;
        }

        private static MethodDefinition SingleMethod(TypeDefinition type, string name) =>
            type.Methods.Single(m => m.Name == name);

        private static List<(int Index, MethodReference Reference)> CallsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Select((instruction, index) => (instruction, index))
                .Where(x => (x.instruction.OpCode == OpCodes.Call || x.instruction.OpCode == OpCodes.Callvirt)
                    && x.instruction.Operand is MethodReference)
                .Select(x => (x.index, (MethodReference)x.instruction.Operand))
                .ToList();

        // A rest observer must produce the rest offset exactly; the native
        // rest path is returned early anyway, but the math must also hold.
        [Fact]
        public void EgoOffset_RestObserver_ReconstructsVector()
        {
            var rel = new OpticalVector(3.0, -4.0, 1.2e11);
            bool ok = RelativisticSpaceRenderer.TryAberrateEgoOffset(rel,
                PresentationVelocity.Rest, out OpticalVector shifted);
            Assert.True(ok);
            double tolerance = Math.Max(1.0, Math.Sqrt(rel.LengthSquared()) * 1e-12);
            Assert.InRange(Math.Abs(shifted.X - rel.X), 0.0, tolerance);
            Assert.InRange(Math.Abs(shifted.Y - rel.Y), 0.0, tolerance);
            Assert.InRange(Math.Abs(shifted.Z - rel.Z), 0.0, tolerance);
        }

        // Forward observer motion tilts a transverse light direction toward
        // the velocity axis; the distance (magnitude) is preserved so shader
        // angular-radius terms stay exact.
        [Fact]
        public void EgoOffset_TransverseTarget_TiltsTowardVelocityAndKeepsDistance()
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(
                new OpticalVector(0.5 * PhysicsConstants.SpeedOfLight, 0.0, 0.0),
                out PresentationVelocity observer));
            var rel = new OpticalVector(0.0, 0.0, 1.5e11);
            Assert.True(RelativisticSpaceRenderer.TryAberrateEgoOffset(rel, in observer,
                out OpticalVector shifted));
            Assert.True(shifted.X > 0.0, "aberrated direction must tilt toward the velocity axis");
            Assert.True(shifted.Z > 0.0);
            double distance = Math.Sqrt(rel.LengthSquared());
            double shiftedDistance = Math.Sqrt(shifted.LengthSquared());
            Assert.InRange(Math.Abs(shiftedDistance - distance), 0.0, distance * 1e-9);
        }

        // Directions on the velocity axis are aberration fixed points: the
        // forward direction stays forward and the rearward stays rearward.
        [Fact]
        public void EgoOffset_OnAxisDirections_AreFixedPoints()
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(
                new OpticalVector(0.5 * PhysicsConstants.SpeedOfLight, 0.0, 0.0),
                out PresentationVelocity observer));
            foreach (double sign in new[] { 1.0, -1.0 })
            {
                var rel = new OpticalVector(sign * 7.0e10, 0.0, 0.0);
                Assert.True(RelativisticSpaceRenderer.TryAberrateEgoOffset(rel, in observer,
                    out OpticalVector shifted));
                double tolerance = Math.Abs(rel.X) * 1e-9;
                Assert.InRange(Math.Abs(shifted.X - rel.X), 0.0, tolerance);
                Assert.InRange(Math.Abs(shifted.Y), 0.0, tolerance);
                Assert.InRange(Math.Abs(shifted.Z), 0.0, tolerance);
            }
        }

        // A retrograde observer tilts the transverse direction rearward, the
        // mirror image of the forward case.
        [Fact]
        public void EgoOffset_RetrogradeObserver_TiltsAwayFromForward()
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(
                new OpticalVector(-0.5 * PhysicsConstants.SpeedOfLight, 0.0, 0.0),
                out PresentationVelocity observer));
            var rel = new OpticalVector(0.0, 0.0, 1.5e11);
            Assert.True(RelativisticSpaceRenderer.TryAberrateEgoOffset(rel, in observer,
                out OpticalVector shifted));
            Assert.True(shifted.X < 0.0, "retrograde observer must tilt the direction rearward");
            Assert.True(shifted.Z > 0.0);
        }

        [Fact]
        public void EgoOffset_DegenerateAndInvalidInputs_FailClosed()
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(
                new OpticalVector(0.5 * PhysicsConstants.SpeedOfLight, 0.0, 0.0),
                out PresentationVelocity observer));

            Assert.False(RelativisticSpaceRenderer.TryAberrateEgoOffset(
                OpticalVector.Zero, in observer, out _));
            Assert.False(RelativisticSpaceRenderer.TryAberrateEgoOffset(
                new OpticalVector(double.NaN, 0.0, 0.0), in observer, out _));
            Assert.False(RelativisticSpaceRenderer.TryAberrateEgoOffset(
                new OpticalVector(double.PositiveInfinity, 0.0, 0.0), in observer, out _));
            // default(PresentationVelocity) has Q == 0: an invalid observer
            // must fail closed rather than emit garbage directions.
            Assert.False(RelativisticSpaceRenderer.TryAberrateEgoOffset(
                new OpticalVector(1.0, 0.0, 0.0), default, out _));
        }

        // Extreme-but-valid observers must still emit finite normalized
        // offsets at preserved distance.
        [Fact]
        public void EgoOffset_ExtremeBeta_StaysFiniteAndNormalized()
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(
                new OpticalVector((1.0 - 1e-12) * PhysicsConstants.SpeedOfLight, 0.0, 0.0),
                out PresentationVelocity observer));
            var rel = new OpticalVector(0.0, 0.0, -1.5e11);
            Assert.True(RelativisticSpaceRenderer.TryAberrateEgoOffset(rel, in observer,
                out OpticalVector shifted));
            Assert.True(double.IsFinite(shifted.X) && double.IsFinite(shifted.Y) && double.IsFinite(shifted.Z));
            double distance = Math.Sqrt(rel.LengthSquared());
            Assert.InRange(Math.Abs(Math.Sqrt(shifted.LengthSquared()) - distance), 0.0, distance * 1e-9);
        }

        // The eclipse factor port mirrors CelestialShadows.glsl's disk test:
        // distance early-outs, angular radii, and the penumbra smoothstep.
        [Fact]
        public void RestEclipseFactor_MatchesDiskOverlapSemantics()
        {
            var toSun = new OpticalVector(0.0, 0.0, 1.5e11);
            const double sunRadius = 7.0e8;
            double thetaSun = Math.Asin(sunRadius / 1.5e11);

            // Occluder beyond the sun cannot shadow: distance early-out.
            Assert.Equal(1.0, RelativisticSpaceRenderer.EvaluateRestEclipseFactor(
                new OpticalVector(0.0, 0.0, 3.0e11), 1.0e7, in toSun, sunRadius, out _));
            // Fragment at the occluder's center: degenerate early-out.
            Assert.Equal(1.0, RelativisticSpaceRenderer.EvaluateRestEclipseFactor(
                new OpticalVector(1.0e-4, 0.0, 0.0), 1.0e7, in toSun, sunRadius, out _));
            // Angularly separated disks stay lit.
            Assert.Equal(1.0, RelativisticSpaceRenderer.EvaluateRestEclipseFactor(
                new OpticalVector(1.0e10, 0.0, 7.5e10), 1.7e6, in toSun, sunRadius, out _));

            // A nearer occluder with a larger angular disk than the sun is
            // full umbra: direction coincident with the rest sun direction.
            double umbraRadius = 7.5e10 * Math.Sin(thetaSun * 1.5);
            Assert.Equal(0.0, RelativisticSpaceRenderer.EvaluateRestEclipseFactor(
                new OpticalVector(0.0, 0.0, 7.5e10), umbraRadius, in toSun, sunRadius, out _));

            // An equal-angular-radius disk at one sun-radius separation is the
            // penumbra midpoint.
            double sep = thetaSun;
            double equalRadius = 7.5e10 * Math.Sin(thetaSun);
            var toBody = new OpticalVector(Math.Sin(sep) * 7.5e10, 0.0, Math.Cos(sep) * 7.5e10);
            double factor = RelativisticSpaceRenderer.EvaluateRestEclipseFactor(
                in toBody, equalRadius, in toSun, sunRadius, out double t);
            Assert.InRange(t, 0.0 + 1e-9, 1.0 - 1e-9);
            Assert.InRange(factor, 0.0, 1.0);
            Assert.Equal(t * t * (3.0 - 2.0 * t), factor, 12);
        }

        // The owned bodies[] records must make the shader's apparent-space
        // test return the verdict computed at rest, for every verdict class.
        [Fact]
        public void EncodedOccluders_ReproduceRestVerdictsAgainstApparentSun()
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(
                new OpticalVector(0.9 * PhysicsConstants.SpeedOfLight, 0.0, 0.0),
                out PresentationVelocity observer));

            const double sunDistance = 1.5e11;
            const double sunRadius = 7.0e8;
            double thetaSun = Math.Asin(sunRadius / sunDistance);
            var restSunOffset = new OpticalVector(0.0, 0.0, sunDistance);
            Assert.True(RelativisticOptics.TryForward(restSunOffset, in observer, out OpticalRay sunRay));
            OpticalVector apparentSunOffset = sunRay.Direction * sunDistance;
            OpticalVector apparentSunDir = sunRay.Direction;

            // Lit verdict: even when the body's apparent direction lands on
            // top of the apparent sun, the encoded record must evaluate lit.
            Assert.True(RelativisticSpaceRenderer.TryEncodeEclipseOccluder(
                1.0, 0.0, in apparentSunDir, sunDistance, thetaSun,
                in apparentSunDir, 1.7e6, out OpticalVector litOffset, out double litRadius));
            Assert.Equal(1.0, RelativisticSpaceRenderer.EvaluateRestEclipseFactor(
                in litOffset, litRadius, in apparentSunOffset, sunRadius, out _));

            // Umbra verdict: the encoded disk must cover the apparent sun
            // even though the occluder's own apparent direction may miss it.
            Assert.True(RelativisticSpaceRenderer.TryEncodeEclipseOccluder(
                0.0, 0.0, in apparentSunDir, sunDistance, thetaSun,
                new OpticalVector(1.0, 0.0, 0.0), 1.7e6, out OpticalVector umbraOffset, out double umbraRadius));
            Assert.Equal(0.0, RelativisticSpaceRenderer.EvaluateRestEclipseFactor(
                in umbraOffset, umbraRadius, in apparentSunOffset, sunRadius, out _));

            // Penumbra verdict: the encoded equal-radius disk at separation
            // 2t*thetaSun reproduces the rest-frame smoothstep parameter and
            // therefore the exact factor.
            var partialBody = new OpticalVector(Math.Sin(thetaSun) * 7.5e10, 0.0,
                Math.Cos(thetaSun) * 7.5e10);
            double partialRadius = 7.5e10 * Math.Sin(thetaSun);
            double restFactor = RelativisticSpaceRenderer.EvaluateRestEclipseFactor(
                in partialBody, partialRadius, in restSunOffset, sunRadius, out double restT);
            Assert.InRange(restFactor, 1e-6, 1.0 - 1e-6);
            Assert.True(RelativisticSpaceRenderer.TryEncodeEclipseOccluder(
                restFactor, restT, in apparentSunDir, sunDistance, thetaSun,
                in apparentSunDir, partialRadius, out OpticalVector penOffset, out double penRadius));
            double reproduced = RelativisticSpaceRenderer.EvaluateRestEclipseFactor(
                in penOffset, penRadius, in apparentSunOffset, sunRadius, out double reproducedT);
            // acos() conditioning on near-parallel vectors caps roundtrip
            // precision near 1e-11, still far below float32 shading
            // significance.
            Assert.InRange(Math.Abs(restFactor - reproduced), 0.0, 1e-10);
            Assert.InRange(Math.Abs(restT - reproducedT), 0.0, 1e-10);
        }

        // Encoder robustness: non-finite or degenerate inputs fail closed so
        // the caller keeps the native body record instead of writing garbage.
        [Fact]
        public void EncodedOccluder_InvalidInputs_FailClosed()
        {
            var dir = new OpticalVector(0.0, 0.0, 1.0);
            Assert.False(RelativisticSpaceRenderer.TryEncodeEclipseOccluder(
                double.NaN, 0.0, in dir, 1.5e11, 0.005, in dir, 1.0, out _, out _));
            Assert.False(RelativisticSpaceRenderer.TryEncodeEclipseOccluder(
                0.5, 0.5, in dir, 0.0, 0.005, in dir, 1.0, out _, out _));
            Assert.False(RelativisticSpaceRenderer.TryEncodeEclipseOccluder(
                0.5, 0.5, in dir, 1.5e11, 0.0, in dir, 1.0, out _, out _));
            Assert.False(RelativisticSpaceRenderer.TryEncodeEclipseOccluder(
                0.5, 0.5, OpticalVector.Zero, 1.5e11, 0.005, in dir, 1.0, out _, out _));
        }

        // A rest ray leaves the sun color untouched (identity scale).
        [Fact]
        public void SunSpectrumScale_RestRay_Identity()
        {
            Assert.True(SunSpectrumScale.Try(
                1.0, 0.95, 0.85, 1.0, out double sr, out double sg, out double sb));
            Assert.Equal(1.0, sr);
            Assert.Equal(1.0, sg);
            Assert.Equal(1.0, sb);
        }

        // Approaching the sun (D>1): the shifted blackbody is bluer and
        // brighter - resolved-disk scaling carries the D^4 surface-brightness
        // law inside the thermal luminance ratio (beaming exponent 0).
        [Fact]
        public void SunSpectrumScale_Approach_BluerAndBrighter()
        {
            Assert.True(SunSpectrumScale.Try(
                1.0, 0.95, 0.85, 1.5, out double sr, out double sg, out double sb));
            Assert.True(sb > sr, $"expected bluer: ({sr},{sg},{sb})");
            Assert.True(sg > 1.0, $"expected brighter: ({sr},{sg},{sb})");
        }

        // Receding (D<1): redder and dimmer.
        [Fact]
        public void SunSpectrumScale_Recede_RedderAndDimmer()
        {
            Assert.True(SunSpectrumScale.Try(
                1.0, 0.95, 0.85, 0.8, out double sr, out double sg, out double sb));
            Assert.True(sr > sb, $"expected redder: ({sr},{sg},{sb})");
            Assert.True(sg < 1.0, $"expected dimmer: ({sr},{sg},{sb})");
        }

        // A sun Doppler-shifted below the thermal table floor contributes no
        // visible light.
        [Fact]
        public void SunSpectrumScale_BelowTableFloor_Dark()
        {
            Assert.True(SunSpectrumScale.Try(
                1.0, 0.95, 0.85, 1e-5, out double sr, out double sg, out double sb));
            Assert.Equal(0.0, sr);
            Assert.Equal(0.0, sg);
            Assert.Equal(0.0, sb);
        }

        // Degenerate inputs fail closed so the caller keeps rest colors.
        [Fact]
        public void SunSpectrumScale_InvalidInputs_FailClosed()
        {
            Assert.False(SunSpectrumScale.Try(
                1.0, 0.95, 0.85, 0.0, out _, out _, out _));
            Assert.False(SunSpectrumScale.Try(
                1.0, 0.95, 0.85, double.NaN, out _, out _, out _));
            Assert.False(SunSpectrumScale.Try(
                1.0, 0.95, 0.85, -1.0, out _, out _, out _));
            Assert.False(SunSpectrumScale.Try(
                double.NaN, 0.0, 0.0, 1.5, out _, out _, out _));
        }

        // The luminance ceiling bounds brightness growth while the
        // chromaticity shift keeps running: capped output is dimmer than
        // uncapped at the same doppler but still bluer.
        [Fact]
        public void SunSpectrumScale_GainCap_ClampsBrightnessNotHue()
        {
            double saved = SunSpectrumScale.MaxGain;
            try
            {
                SunSpectrumScale.MaxGain = double.MaxValue;
                Assert.True(SunSpectrumScale.Try(
                    1.0, 0.95, 0.85, 3.0, out _, out double uncappedG, out double uncappedB));

                SunSpectrumScale.MaxGain = 4.0;
                Assert.True(SunSpectrumScale.Try(
                    1.0, 0.95, 0.85, 3.0, out _, out double cappedG, out double cappedB));

                Assert.True(cappedG < uncappedG, $"cap should bind: {cappedG} vs {uncappedG}");
                Assert.True(cappedB < uncappedB, $"cap should bind: {cappedB} vs {uncappedB}");
                Assert.True(cappedB > cappedG, $"hue shift survives the cap: B={cappedB} G={cappedG}");
            }
            finally
            {
                SunSpectrumScale.MaxGain = saved;
            }
        }

        [Fact]
        public void Install_PatchesFourVesselWriters_AndValidatesSignatures()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition install = SingleMethod(type, "TryInstallLightingProbe");
            var calls = CallsOf(install);
            Assert.Contains(calls, c => c.Reference.Name == "Patch");
            Assert.Equal(4, calls.Count(c => c.Reference.Name == "RequiredVesselWriter"));
            // The four native vessel color writers must be resolved by
            // declaring type so the same-named prepass writer is untouched.
            Assert.True(install.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldtoken) >= 4);
            Assert.Contains(install.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "WriteCommands");
            Assert.Contains(install.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "WriteCommandsColor");
            MethodDefinition required = SingleMethod(type, "RequiredVesselWriter");
            Assert.Contains(required.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).Contains("signature changed"));
        }

        [Fact]
        public void Transpiler_RewritesExactlyOneDescriptorSetCall()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition transpiler = SingleMethod(type, "TranspileLightingDescriptorSet");
            var calls = CallsOf(transpiler);
            Assert.Contains(calls, c => c.Reference.Name == "get_Item" || c.Reference.Name == "GetEnumerator");
            // The anchor must be counted against the resolved native method
            // and a miss must flag the install to fail closed.
            Assert.Contains(transpiler.Body.Instructions,
                i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m
                    && (m.Name == "Equals" || m.Name == "op_Equality"));
            var fieldWrites = transpiler.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();
            Assert.Contains("s_lightingAnchorMissed", fieldWrites);
        }

        [Fact]
        public void Substitute_FallsBackToNativeSetOnEveryFailure()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition substitute = SingleMethod(type, "DescriptorSetOrNative");
            Assert.Equal("Brutal.VulkanApi.VkDescriptorSet", substitute.ReturnType.FullName);
            Assert.Single(substitute.Parameters);
            var calls = CallsOf(substitute);
            // The native accessor must remain reachable as the fallback for
            // every gate and after the catch.
            int nativeCalls = calls.Count(c => c.Reference.DeclaringType.FullName == "KSA.GlobalShaderBindings"
                && c.Reference.Name == "DescriptorSet");
            Assert.True(nativeCalls >= 1, "native fallback set must be reachable");
            Assert.Contains(calls, c => c.Reference.Name == "TryPrepareLightingFrame");
            Assert.Contains(calls, c => c.Reference.Name == "get_MainViewport");
            // The exception path must mark the probe failed and never rethrow.
            Assert.DoesNotContain(substitute.Body.Instructions, i => i.OpCode == OpCodes.Rethrow);
        }

        [Fact]
        public void Prepare_SingleFillPerFrame_AndCapturesEffectiveObserver()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition prepare = SingleMethod(type, "TryPrepareLightingFrame");
            Assert.Contains(CallsOf(prepare), c => c.Reference.Name == "TryFillLightingFrame"
                || c.Reference.Name == "TryCreateLightingResources");

            MethodDefinition fill = SingleMethod(type, "TryFillLightingFrame");
            var fillCalls = CallsOf(fill);
            Assert.Contains(fillCalls, c => c.Reference.Name == "TryCaptureEffectiveObserver");
            Assert.Contains(fillCalls, c => c.Reference.Name == "TryAberrateLighting");
            Assert.Contains(fillCalls, c => c.Reference.Name == "WriteFrame");
            // A rest observer must skip the owned path: identical values mean
            // the native set is equally correct and costs nothing.
            Assert.Contains(fillCalls, c => c.Reference.Name == "get_Q");
        }

        [Fact]
        public void AberrateLighting_CoversSunPlanetOccludersAndSunSpectrum()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition aberrate = SingleMethod(type, "TryAberrateLighting");
            var fieldAccesses = aberrate.Body.Instructions
                .Where(i => (i.OpCode == OpCodes.Ldfld || i.OpCode == OpCodes.Ldflda
                    || i.OpCode == OpCodes.Stfld) && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();
            Assert.Contains("SunPositionRadius", fieldAccesses);
            Assert.Contains("PlanetPosition", fieldAccesses);
            Assert.Contains("Bodies", fieldAccesses);
            Assert.Contains("BodyCount", fieldAccesses);
            // The sun-spectrum fields (SunColor and the atmosphere-transmittance
            // tint OcclusionColor) and the fill colors (PlanetColor and the
            // same-body ambient NearestCelestialColor) are Doppler-scaled
            // through the spectral helper; forward lights and cloud-volume
            // data must never be written.
            Assert.DoesNotContain("ForwardLightCount", aberrate.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name));
            Assert.DoesNotContain("ForwardLightPositionRange", aberrate.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name));
            Assert.DoesNotContain("CloudShadowVolumeHandle", aberrate.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name));
            var calls = CallsOf(aberrate);
            Assert.Contains(calls, c => c.Reference.Name == "GetPositionEgo");
            Assert.Contains(calls, c => c.Reference.Name == "TryForward");
            Assert.Contains(calls, c => c.Reference.Name == "Try"
                && c.Reference.DeclaringType.Name == "SunSpectrumScale");
            // Occlusion is a rest-frame verdict: each body must be evaluated
            // against the rest sun and re-encoded for the apparent disk test,
            // never aberrated into the test as-is.
            Assert.Contains(calls, c => c.Reference.Name == "EvaluateRestEclipseFactor");
            Assert.Contains(calls, c => c.Reference.Name == "TryEncodeEclipseOccluder");
        }

        [Fact]
        public void OwnedResources_CopyLayoutAndNeverTouchNativeBuffers()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition nested = ModType(assembly,
                "KSARS.Visuals.RelativisticSpaceRenderer/VesselLightingUbo");
            MethodDefinition ctor = nested.Methods.Single(m => m.Name == ".ctor");
            var calls = CallsOf(ctor);
            Assert.Contains(calls, c => c.Reference.Name == "CreateUniformBuffer");
            Assert.Contains(calls, c => c.Reference.Name == "CreateDescriptorPool");
            Assert.Contains(calls, c => c.Reference.Name == "AllocateDescriptorSet");
            Assert.Contains(calls, c => c.Reference.Name == "WriteDescriptorSets");
            Assert.Contains(calls, c => c.Reference.DeclaringType.FullName.Contains("GlobalShaderBindings"));
            MethodDefinition createBuffer = SingleMethod(nested, "CreateUniformBuffer");
            Assert.Contains(CallsOf(createBuffer), c => c.Reference.Name == "CreateBuffer");
            // Per-frame sets are stored into the owned set array inside the
            // ctor loop (VkDescriptorSet is a value type: stelem.any).
            Assert.Contains(ctor.Body.Instructions, i => i.OpCode.Name.StartsWith("stelem"));

            // No instruction anywhere in the partial may read or write the
            // native uniform buffer, mapped memory, or descriptor array: the
            // owned copy must leave native state bitwise untouched.
            TypeDefinition outer = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            foreach (TypeDefinition t in new[] { outer, nested })
            {
                foreach (MethodDefinition method in t.Methods)
                {
                    if (method.Body is null)
                    {
                        continue;
                    }

                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is FieldReference field
                            && field.DeclaringType.FullName == "KSA.GlobalShaderBindings")
                        {
                            Assert.DoesNotMatch("_uniformBuffer|_uniformMemory|_descriptorSets|_descriptorPool|_constantsBuffer|_constantsMemory|_descriptorSetLayout",
                                field.Name);
                        }
                    }
                }
            }
        }

        [Fact]
        public void RebuildAndShutdown_RetireOwnedResources()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            // The owned set joins the verified-idle retire list at rebuild.
            MethodDefinition collect = SingleMethod(type, "CollectLightingResources");
            Assert.Contains(CallsOf(collect), c => c.Reference.Name == "Add");
            MethodDefinition reset = SingleMethod(type, "ResetLightingProbeState");
            Assert.DoesNotContain(CallsOf(reset), c => c.Reference.Name == "Dispose");
            Assert.DoesNotContain(reset.Body.Instructions, i => i.OpCode == OpCodes.Callvirt
                && i.Operand is MethodReference m && m.Name == "WaitIdle");
            MethodDefinition nested = ModType(assembly,
                "KSARS.Visuals.RelativisticSpaceRenderer/VesselLightingUbo")
                .Methods.Single(m => m.Name == "Dispose");
            Assert.Contains(CallsOf(nested), c => c.Reference.Name == "Unmap");
            Assert.DoesNotContain(CallsOf(nested), c => c.Reference.Name == "WaitIdle");
        }

        [Fact]
        public void RenderHooks_GatesInstallOnLightingEnvVar_AndResetsChain()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition hooks = ModType(assembly, "KSARS.Visuals.RenderHooks");
            MethodDefinition install = SingleMethod(hooks, "TryInstall");
            Assert.Contains(install.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "KSARS_LIGHTING_PROBE");
            var calls = CallsOf(install);
            Assert.Contains(calls, c => c.Reference.Name == "TryInstallLightingProbe");

            MethodDefinition reset = hooks.Methods.Single(m => m.Name == "ResetState");
            Assert.Contains(CallsOf(reset), c => c.Reference.Name == "ResetLightingProbeStateIfLoaded");
            MethodDefinition guarded = SingleMethod(hooks, "ResetLightingProbeStateIfLoaded");
            Assert.Contains(CallsOf(guarded), c => c.Reference.Name == "ResetLightingProbeState");
        }

    }
}

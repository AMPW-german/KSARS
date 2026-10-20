using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSA.Rendering.Lighting;
using RenderCore;

namespace KSARS.Visuals
{
    // Relativistic vessel lighting, injected inside the four native vessel
    // color-pass command writers by rewriting their single
    // GlobalShaderBindings::DescriptorSet call to DescriptorSetOrNative. For
    // the main viewport that substitute returns an owned per-frame descriptor
    // set whose global UBO copy carries the aberrated sun and planet fill
    // directions (computed about the followed vehicle so the shader's
    // position-minus-fragment rays are the apparent ones). Celestial eclipse
    // occluders are the exception: occlusion is a rest-geometry question -
    // photons arrive along rest-frame straight paths - so each body's
    // eclipse verdict is evaluated against the rest sun and rest body
    // positions and the bodies[] records are re-encoded so the shader's
    // apparent-space disk test reproduces that verdict exactly. Planets,
    // terrain, shadow maps, prepass, and every secondary viewport keep the
    // native rest lighting. Any failure returns the untouched native set.
    // Opt-in via KSARS_LIGHTING_PROBE=1.
    internal static partial class RelativisticSpaceRenderer
    {
        private static MethodInfo? s_nativeDescriptorSet;
        private static MethodInfo? s_lightingDescriptorSet;
        private static bool s_lightingProbeInstalled;
        private static bool s_lightingFailed;
        private static bool s_lightingAnchorMissed;
        private static VesselLightingUbo? s_lightingResources;
        private static int s_lightingPreparedFrame = -1;
        private static bool s_lightingPreparedOk;
        private static long s_lightingPresented;
        private static string s_lightingStatus = "vessel lighting probe not installed";

        // Source spectra are cached in SunSpectrumScale (fitted blackbody
        // priors, resolved-disk beaming exponent 0): the sun's color shifts
        // with its own ray's Doppler factor and the planet fill
        // (PlanetColor/NearestCelestialColor) with the planet ray's.
        private static double s_lastSunDoppler = 1.0;

        // members is the validated baseline map; the lighting probe resolves
        // GlobalShaderBindings::_currentFrame itself when the source probe did
        // not run PrepareProbe, so KSARS_LIGHTING_PROBE=1 works standalone.
        internal static bool TryInstallLightingProbe(Harmony harmony,
            IReadOnlyDictionary<string, MemberInfo>? members = null)
        {
            if (s_lightingProbeInstalled)
            {
                return true;
            }

            s_lightingStatus = "";
            try
            {
                if (s_getGlobalFrameIndex is null)
                {
                    if (members is null
                        || !members.TryGetValue("KSA.GlobalShaderBindings::_currentFrame", out MemberInfo? frameMember)
                        || frameMember is not FieldInfo currentFrame
                        || currentFrame.DeclaringType != typeof(GlobalShaderBindings)
                        || !currentFrame.IsStatic
                        || currentFrame.FieldType != typeof(int))
                    {
                        throw new InvalidOperationException(
                            "vessel lighting probe unsupported: KSA.GlobalShaderBindings::_currentFrame member mapping missing; observer capture unavailable");
                    }

                    s_getGlobalFrameIndex = Expression.Lambda<Func<int>>(Expression.Field(null, currentFrame)).Compile();
                }

                s_nativeDescriptorSet = typeof(GlobalShaderBindings).GetMethod(
                    nameof(GlobalShaderBindings.DescriptorSet), BindingFlags.Public | BindingFlags.Static);
                if (s_nativeDescriptorSet is null || s_nativeDescriptorSet.ReturnType != typeof(VkDescriptorSet)
                    || s_nativeDescriptorSet.GetParameters() is not { Length: 1 } nativeParameters
                    || nativeParameters[0].ParameterType != typeof(int))
                {
                    throw new InvalidOperationException(
                        "vessel lighting probe unsupported: GlobalShaderBindings::DescriptorSet(int) signature changed");
                }

                s_lightingDescriptorSet = typeof(RelativisticSpaceRenderer).GetMethod(
                    nameof(DescriptorSetOrNative), BindingFlags.NonPublic | BindingFlags.Static);
                if (s_lightingDescriptorSet is null || s_lightingDescriptorSet.ReturnType != typeof(VkDescriptorSet)
                    || s_lightingDescriptorSet.GetParameters() is not { Length: 1 } lightingParameters
                    || lightingParameters[0].ParameterType != typeof(int))
                {
                    throw new InvalidOperationException(
                        "vessel lighting probe unsupported: DescriptorSetOrNative(int) missing or mismatched");
                }

                MethodInfo? transpiler = typeof(RelativisticSpaceRenderer).GetMethod(
                    nameof(TranspileLightingDescriptorSet), BindingFlags.NonPublic | BindingFlags.Static);
                if (transpiler is null || transpiler.ReturnType != typeof(IEnumerable<CodeInstruction>)
                    || transpiler.GetParameters() is not { Length: 1 } transpilerParameters
                    || transpilerParameters[0].ParameterType != typeof(IEnumerable<CodeInstruction>))
                {
                    throw new InvalidOperationException(
                        "vessel lighting probe unsupported: TranspileLightingDescriptorSet missing or mismatched");
                }

                // Every patched writer must bind the shared global set exactly
                // once; the transpiler flags any drift and install fails
                // closed rather than leaving a partially aberrating vessel.
                MethodInfo[] targets =
                {
                    RequiredVesselWriter(typeof(PartModelRenderer.ColorData), "WriteCommands"),
                    RequiredVesselWriter(typeof(PartModelGlass.Shared), "WriteCommandsColor"),
                    RequiredVesselWriter(typeof(SplineRenderer), "WriteCommands"),
                    RequiredVesselWriter(typeof(GenericMeshRenderer), "WriteCommands"),
                };

                s_lightingAnchorMissed = false;
                var patched = new List<MethodInfo>();
                try
                {
                    foreach (MethodInfo target in targets)
                    {
                        harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
                        if (s_lightingAnchorMissed)
                        {
                            throw new InvalidOperationException(
                                $"vessel lighting probe unsupported: {target.DeclaringType?.FullName}::{target.Name} does not bind GlobalShaderBindings::DescriptorSet exactly once");
                        }

                        patched.Add(target);
                    }
                }
                catch
                {
                    foreach (MethodInfo target in patched)
                    {
                        try
                        {
                            harmony.Unpatch(target, transpiler);
                        }
                        catch
                        {
                        }
                    }

                    throw;
                }

                s_lightingProbeInstalled = true;
                s_lightingFailed = false;
                s_lightingPreparedFrame = -1;
                s_lightingPreparedOk = false;
                s_lightingStatus = "vessel lighting probe installed: aberrated sun and planet-fill directions bound for the four native vessel color writers; eclipse occluders evaluated at rest positions and re-encoded so the apparent disk test reproduces the rest-frame verdict; everything else keeps native rest lighting";
                Console.WriteLine($"KSARS: {s_lightingStatus}");
                return true;
            }
            catch (Exception ex)
            {
                s_lightingStatus = $"vessel lighting probe disabled: {ex.GetType().Name}: {ex.Message}; native vessel lighting retained";
                Console.WriteLine($"KSARS: {s_lightingStatus}");
                return false;
            }
        }

        private static MethodInfo RequiredVesselWriter(Type declaringType, string name)
        {
            MethodInfo? method = declaringType.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            if (method is null || method.ReturnType != typeof(void)
                || method.GetParameters() is not { Length: 3 } parameters
                || parameters[0].ParameterType != typeof(CommandBuffer)
                || parameters[1].ParameterType != typeof(IViewport)
                || parameters[2].ParameterType != typeof(int))
            {
                throw new InvalidOperationException(
                    $"vessel lighting probe unsupported: {declaringType.FullName}::{name}(CommandBuffer,IViewport,int) signature changed");
            }

            return method;
        }

        internal static IEnumerable<CodeInstruction> TranspileLightingDescriptorSet(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = instructions.ToList();
            // Must stay total: Harmony re-runs remaining transpilers while
            // unpatching stacked patches on these writers, which can happen
            // after teardown cleared the install fields.
            if (s_nativeDescriptorSet is null || s_lightingDescriptorSet is null)
            {
                return list;
            }

            int anchors = 0;
            int anchorIndex = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == System.Reflection.Emit.OpCodes.Call
                    && list[i].operand is MethodInfo method && method.Equals(s_nativeDescriptorSet))
                {
                    anchors++;
                    anchorIndex = i;
                }
            }

            if (anchors != 1)
            {
                s_lightingAnchorMissed = true;
                Console.WriteLine($"KSARS: vessel lighting probe anchor mismatch: expected 1 GlobalShaderBindings.DescriptorSet call, found {anchors}");
                return list;
            }

            list[anchorIndex].operand = s_lightingDescriptorSet;
            return list;
        }

        // Substitute for GlobalShaderBindings::DescriptorSet inside the four
        // patched vessel writers. Returns the owned set for the current frame
        // when the main-viewport vessel frame was prepared this global frame,
        // and the untouched native set in every other case. Must not throw.
        private static VkDescriptorSet DescriptorSetOrNative(int viewportIndex)
        {
            try
            {
                if (s_lightingProbeInstalled && !s_lightingFailed && !s_recordingDisabled
                    && s_getGlobalFrameIndex is { } getFrameIndex
                    && Program.MainViewport is { } main
                    && viewportIndex == main.ShaderSlot
                    && TryPrepareLightingFrame(getFrameIndex, main, out VesselLightingUbo? resources, out int frame))
                {
                    return resources!.Sets[frame];
                }
            }
            catch (Exception ex)
            {
                s_lightingFailed = true;
                s_lightingStatus = $"vessel lighting probe disabled after record fault ({ex.GetType().Name}: {ex.Message}); native vessel lighting retained";
                Console.WriteLine($"KSARS: {s_lightingStatus}");
            }

            return GlobalShaderBindings.DescriptorSet(viewportIndex);
        }

        private static bool TryPrepareLightingFrame(Func<int> getFrameIndex, IViewport main,
            [NotNullWhen(true)] out VesselLightingUbo? resources, out int frame)
        {
            resources = s_lightingResources;
            frame = getFrameIndex();
            if (resources is null)
            {
                if (!TryCreateLightingResources(out resources))
                {
                    return false;
                }

                s_lightingResources = resources;
            }

            if (frame < 0 || frame >= resources.FrameCount)
            {
                return false;
            }

            // The owned sets must bind against the layout they were allocated
            // from; a layout swap without verified-idle retirement means the
            // rebuild postfix never ran (e.g. probe active without the atlas
            // coordinator), so the probe goes native rather than risk stale
            // descriptor sets.
            if (!ReferenceEquals(resources.Layout, GlobalShaderBindings.DescriptorSetLayout))
            {
                s_lightingFailed = true;
                s_lightingStatus = "vessel lighting probe disabled: global descriptor layout replaced without verified-idle retirement; native vessel lighting retained";
                Console.WriteLine($"KSARS: {s_lightingStatus}");
                return false;
            }

            if (s_lightingPreparedFrame == frame)
            {
                return s_lightingPreparedOk;
            }

            s_lightingPreparedOk = TryFillLightingFrame(resources, main, frame);
            s_lightingPreparedFrame = frame;
            return s_lightingPreparedOk;
        }

        private static bool TryCreateLightingResources([NotNullWhen(true)] out VesselLightingUbo? resources)
        {
            resources = null;
            Core.Renderer? renderer = Program.GetRenderer();
            DescriptorSetLayoutEx? layout = GlobalShaderBindings.DescriptorSetLayout;
            if (renderer is null || layout is null || layout.IsNull() || renderer.MaxFramesInFlight <= 0)
            {
                s_lightingStatus = "vessel lighting probe waiting: renderer or global descriptor layout unavailable; native vessel lighting retained";
                return false;
            }

            try
            {
                resources = new VesselLightingUbo(renderer, layout, renderer.MaxFramesInFlight);
                Console.WriteLine("KSARS: vessel lighting probe resources allocated against the global descriptor layout");
                return true;
            }
            catch (Exception ex)
            {
                // Persistent resource faults disable the probe rather than
                // retrying every frame.
                s_lightingFailed = true;
                s_lightingStatus = $"vessel lighting probe resource fault ({ex.GetType().Name}: {ex.Message}); native vessel lighting retained";
                Console.WriteLine($"KSARS: {s_lightingStatus}");
                return false;
            }
        }

        private static bool TryFillLightingFrame(VesselLightingUbo resources, IViewport main, int frame)
        {
            if (!TryCaptureEffectiveObserver(out ObserverSnapshot? snapshot, out PresentationVelocity observer)
                || snapshot is null || !ReferenceEquals(snapshot.MainViewport, main))
            {
                s_lightingStatus = "vessel lighting probe waiting: observer capture unavailable; native vessel lighting retained";
                return false;
            }

            if (observer.Q == 1.0)
            {
                s_lightingStatus = "vessel lighting probe idle: observer at rest; owned copy identical, native vessel lighting retained";
                return false;
            }

            // The snapshot captured this global frame's UBO copies for the
            // main slot; aberrate the lighting copy in place.
            UboLightingData lighting = snapshot.LightingData;
            UboCelestialData celestial = snapshot.CelestialData;
            if (!TryAberrateLighting(snapshot.MainCamera, snapshot.FollowedVehicle,
                    ref lighting, ref celestial, in observer))
            {
                s_lightingStatus = "vessel lighting probe idle: aberration transform failed this frame; native vessel lighting retained";
                return false;
            }

            UboCameraData camera = snapshot.CameraData;
            UboVesselData vessel = snapshot.VesselData;
            resources.WriteFrame(frame, in camera, in lighting, in celestial, in vessel);
            s_lightingPresented++;
            double beta = observer.Speed / PhysicsConstants.SpeedOfLight;
            s_lightingStatus = $"vessel lighting probe presented: aberrated sun + planet fill bound, sun + fill colors Doppler-shifted (D={s_lastSunDoppler:G4}, gainCap={SunSpectrumScale.MaxGain:G3}); rest-frame eclipse verdicts encoded into occluder records; beta={beta:G6} q={observer.Q:G6} frame={frame} fills={s_lightingPresented}; planets/terrain/prepass/shadows native{(s_betaOverrideEnabled ? $"; OBSERVER OVERRIDE beta={s_betaOverride:G12}" : "")}";
            return true;
        }

        // Aberrates the two light-source directions that must agree with the
        // aberrated sky for the followed vessel (the sun and the planet fill
        // light), then re-encodes the celestial occluder records. Directions
        // are computed about the followed vehicle's ego position so the
        // shader's (position - fragment) rays point along the apparent light
        // travel. The occluders are different: an eclipse is decided by
        // rest-frame geometry - the sun's light either reaches the vessel or
        // it does not - so each body's occlusion factor is evaluated against
        // the rest positions with the same angular disk test the shader runs,
        // and the record is rewritten so the shader's apparent-space test
        // returns that verdict. The sun is required and fails closed; a
        // degenerate planet or body entry keeps its rest vector. Colors,
        // forward lights, and all other fields stay verbatim.
        internal static bool TryAberrateLighting(Camera camera, Vehicle vehicle,
            ref UboLightingData lighting, ref UboCelestialData celestial,
            in PresentationVelocity observer)
        {
            double3 vesselEgo = camera.GetPositionEgo(vehicle);
            var origin = new float3((float)vesselEgo.X, (float)vesselEgo.Y, (float)vesselEgo.Z);
            if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
            {
                return false;
            }

            // Rest vectors must be sampled before SunPositionRadius is
            // overwritten: the occlusion verdict below is evaluated against
            // this rest sun, not the apparent one.
            var restSunOffset = new OpticalVector(
                lighting.SunPositionRadius.X - origin.X,
                lighting.SunPositionRadius.Y - origin.Y,
                lighting.SunPositionRadius.Z - origin.Z);
            double restSunDistance = Math.Sqrt(restSunOffset.LengthSquared());
            double sunRadius = lighting.SunPositionRadius.W;

            // The sun ray supplies the apparent direction and the relativistic
            // Doppler factor for the spectral shift below. The distance is
            // preserved so angular-radius terms in the shader stay exact.
            if (!RelativisticOptics.TryForward(restSunOffset, in observer, out OpticalRay sunRay))
            {
                return false;
            }

            double sunX = origin.X + sunRay.Direction.X * restSunDistance;
            double sunY = origin.Y + sunRay.Direction.Y * restSunDistance;
            double sunZ = origin.Z + sunRay.Direction.Z * restSunDistance;
            if (!double.IsFinite(sunX) || !double.IsFinite(sunY) || !double.IsFinite(sunZ))
            {
                return false;
            }

            var sun = new float4((float)sunX, (float)sunY, (float)sunZ, lighting.SunPositionRadius.W);
            lighting.SunPositionRadius = sun;
            s_lastSunDoppler = sunRay.DopplerFactor;

            // Photons arriving from the aberrated direction are Doppler-
            // shifted: the vessel's sun illumination gets the boosted,
            // spectrally-shifted sun rather than the rest color. Failures
            // keep the rest colors (cosmetic degradation only).
            if (SunSpectrumScale.Try(lighting.SunColor.X, lighting.SunColor.Y, lighting.SunColor.Z,
                    sunRay.DopplerFactor, out double spectrumScaleR, out double spectrumScaleG,
                    out double spectrumScaleB))
            {
                lighting.SunColor = new float4(
                    lighting.SunColor.X * (float)spectrumScaleR,
                    lighting.SunColor.Y * (float)spectrumScaleG,
                    lighting.SunColor.Z * (float)spectrumScaleB,
                    lighting.SunColor.W);
                lighting.OcclusionColor = new float4(
                    lighting.OcclusionColor.X * (float)spectrumScaleR,
                    lighting.OcclusionColor.Y * (float)spectrumScaleG,
                    lighting.OcclusionColor.Z * (float)spectrumScaleB,
                    lighting.OcclusionColor.W);
            }

            // The planet fill light gets the same treatment as the sun through
            // its own ray: its photons arrive from the planet direction, so
            // position and Doppler factor are that direction's. A degenerate
            // planet entry keeps its rest vector and rest colors.
            var restPlanetOffset = new OpticalVector(
                lighting.PlanetPosition.X - origin.X,
                lighting.PlanetPosition.Y - origin.Y,
                lighting.PlanetPosition.Z - origin.Z);
            if (RelativisticOptics.TryForward(restPlanetOffset, in observer, out OpticalRay planetRay))
            {
                double planetDistance = Math.Sqrt(restPlanetOffset.LengthSquared());
                double px = origin.X + planetRay.Direction.X * planetDistance;
                double py = origin.Y + planetRay.Direction.Y * planetDistance;
                double pz = origin.Z + planetRay.Direction.Z * planetDistance;
                if (double.IsFinite(px) && double.IsFinite(py) && double.IsFinite(pz))
                {
                    lighting.PlanetPosition = new float4(
                        (float)px, (float)py, (float)pz, lighting.PlanetPosition.W);
                }

                // Fill light is albedo-filtered sunlight rather than a thermal
                // source, but the fitted model's off-locus residual prior
                // keeps its chromaticity bounded; the luminance/direction
                // physics is unchanged. NearestCelestialColor is the same
                // body's ambient tint and scales identically.
                if (SunSpectrumScale.Try(lighting.PlanetColor.X, lighting.PlanetColor.Y,
                        lighting.PlanetColor.Z, planetRay.DopplerFactor,
                        out double planetScaleR, out double planetScaleG, out double planetScaleB))
                {
                    lighting.PlanetColor = new float4(
                        lighting.PlanetColor.X * (float)planetScaleR,
                        lighting.PlanetColor.Y * (float)planetScaleG,
                        lighting.PlanetColor.Z * (float)planetScaleB,
                        lighting.PlanetColor.W);
                    lighting.NearestCelestialColor = new float3(
                        lighting.NearestCelestialColor.X * (float)planetScaleR,
                        lighting.NearestCelestialColor.Y * (float)planetScaleG,
                        lighting.NearestCelestialColor.Z * (float)planetScaleB);
                }
            }

            var apparentSunOffset = new OpticalVector(sun.X - origin.X, sun.Y - origin.Y, sun.Z - origin.Z);
            double apparentSunDistance = Math.Sqrt(apparentSunOffset.LengthSquared());
            if (!double.IsFinite(restSunDistance) || !(restSunDistance > 0.0)
                || !double.IsFinite(apparentSunDistance) || !(apparentSunDistance > 0.0))
            {
                return false;
            }

            OpticalVector apparentSunDirection = apparentSunOffset / apparentSunDistance;
            double sunAngularRadius = Math.Asin(Math.Clamp(sunRadius / apparentSunDistance, -1.0, 1.0));

            int count = Math.Min(Math.Max(celestial.BodyCount, 0), CelestialBody24.MAX);
            for (int i = 0; i < count; i++)
            {
                CelestialBody body = celestial.Bodies[i];
                var restBodyOffset = new OpticalVector(
                    body.Position.X - origin.X, body.Position.Y - origin.Y, body.Position.Z - origin.Z);
                double factor = EvaluateRestEclipseFactor(in restBodyOffset, body.Radius,
                    in restSunOffset, sunRadius, out double penumbraT);

                // Placement preference only: the body's apparent direction.
                // The verdict itself was already decided at rest.
                OpticalVector preferred = apparentSunDirection;
                if (RelativisticOptics.TryForward(restBodyOffset, in observer, out OpticalRay bodyRay))
                {
                    preferred = bodyRay.Direction;
                }

                if (TryEncodeEclipseOccluder(factor, penumbraT, in apparentSunDirection,
                        apparentSunDistance, sunAngularRadius, in preferred, body.Radius,
                        out OpticalVector offset, out double radius))
                {
                    celestial.Bodies[i] = new CelestialBody(
                        new float3(
                            origin.X + (float)offset.X,
                            origin.Y + (float)offset.Y,
                            origin.Z + (float)offset.Z),
                        (float)radius);
                }
            }

            return true;
        }

        // Port of CelestialShadows.glsl::applyCelestialShadow evaluated at the
        // ego origin on rest-frame vectors. Returns the per-body occlusion
        // factor and, for the penumbra band, the raw smoothstep parameter the
        // shader derives from angular separation so the apparent-space
        // encoding can reproduce it exactly.
        internal static double EvaluateRestEclipseFactor(in OpticalVector toBody,
            double bodyRadius, in OpticalVector toSun, double sunRadius, out double penumbraT)
        {
            const double Epsilon = 1e-6;
            const double RadiusFactor = 0.999;
            penumbraT = 0.0;

            double toBodyDistSq = toBody.LengthSquared();
            double toSunDistSq = toSun.LengthSquared();
            if (!double.IsFinite(toBodyDistSq) || !double.IsFinite(toSunDistSq)
                || toBodyDistSq <= Epsilon || toBodyDistSq >= toSunDistSq)
            {
                return 1.0;
            }

            double invBodyDist = 1.0 / Math.Sqrt(toBodyDistSq);
            double invSunDist = 1.0 / Math.Sqrt(toSunDistSq);
            double thetaBody = Math.Asin(Math.Clamp(bodyRadius * RadiusFactor * invBodyDist, -1.0, 1.0));
            double thetaSun = Math.Asin(Math.Clamp(sunRadius * invSunDist, -1.0, 1.0));
            double sumR = thetaBody + thetaSun;
            double diffR = Math.Abs(thetaBody - thetaSun);
            double thetaSep = Math.Acos(Math.Clamp(toBody.Dot(toSun) * invBodyDist * invSunDist, -1.0, 1.0));

            if (thetaSep >= sumR)
            {
                return 1.0;
            }

            if (thetaSep <= diffR)
            {
                return 0.0;
            }

            penumbraT = Math.Clamp((thetaSep - diffR) / (sumR - diffR), 0.0, 1.0);
            return penumbraT * penumbraT * (3.0 - 2.0 * penumbraT);
        }

        // Re-encodes one bodies[] record so the shader's apparent-space disk
        // test reproduces the already-decided rest-frame verdict: lit bodies
        // are parked beyond the sun (the shader's distance early-out then
        // yields 1.0), umbral bodies are centered on the apparent sun with a
        // slightly larger disk, and penumbra bodies sit at the separation
        // that maps to the rest-frame smoothstep parameter against an
        // equal-radius disk. The stored radii are pre-divided by the
        // shader's RADIUS_FACTOR so the effective disk comes out exact.
        internal static bool TryEncodeEclipseOccluder(double factor, double penumbraT,
            in OpticalVector apparentSunDirection, double sunDistance, double sunAngularRadius,
            in OpticalVector preferredDirection, double restRadius,
            out OpticalVector offset, out double radius)
        {
            const double RadiusFactor = 0.999;
            offset = OpticalVector.Zero;
            radius = restRadius;
            if (!IsFinite(in apparentSunDirection) || !(sunDistance > 0.0)
                || !(sunAngularRadius > 0.0) || !double.IsFinite(factor)
                || !double.IsFinite(penumbraT) || apparentSunDirection.LengthSquared() <= 0.0)
            {
                return false;
            }

            if (factor >= 1.0)
            {
                OpticalVector direction = preferredDirection;
                double lengthSq = direction.LengthSquared();
                if (!IsFinite(in direction) || lengthSq <= 0.0)
                {
                    direction = apparentSunDirection;
                }
                else
                {
                    direction /= Math.Sqrt(lengthSq);
                }

                offset = direction * (2.0 * sunDistance);
                return true;
            }

            double occluderDistance = 0.5 * sunDistance;
            if (factor <= 0.0)
            {
                offset = apparentSunDirection * occluderDistance;
                radius = occluderDistance * Math.Sin(1.05 * sunAngularRadius) / RadiusFactor;
                return true;
            }

            double separation = 2.0 * Math.Clamp(penumbraT, 0.0, 1.0) * sunAngularRadius;
            OpticalVector axis = PerpendicularTowards(in apparentSunDirection, in preferredDirection);
            offset = (apparentSunDirection * Math.Cos(separation) + axis * Math.Sin(separation))
                * occluderDistance;
            radius = occluderDistance * Math.Sin(sunAngularRadius) / RadiusFactor;
            return true;
        }

        // Unit vector in the plane perpendicular to axis, pointing toward the
        // preferred direction's projection; a deterministic perpendicular is
        // chosen when the projection degenerates.
        private static OpticalVector PerpendicularTowards(in OpticalVector axis,
            in OpticalVector preferredDirection)
        {
            OpticalVector projected = preferredDirection - axis * axis.Dot(preferredDirection);
            double lengthSq = projected.LengthSquared();
            if (IsFinite(in projected) && lengthSq > 1e-24)
            {
                return projected / Math.Sqrt(lengthSq);
            }

            OpticalVector fallback = Math.Abs(axis.X) < 0.9
                ? axis.Cross(new OpticalVector(1.0, 0.0, 0.0))
                : axis.Cross(new OpticalVector(0.0, 1.0, 0.0));
            double fallbackSq = fallback.LengthSquared();
            return fallbackSq > 0.0 ? fallback / Math.Sqrt(fallbackSq) : new OpticalVector(0.0, 0.0, 1.0);
        }

        private static bool IsFinite(in OpticalVector v)
            => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

        // Returns origin-relative offset = aberrated(unit(relative)) * |relative|.
        // The distance is preserved so angular radius terms in the shader
        // (e.g. the eclipse test's thetaSun) stay exact; degenerate or
        // non-finite inputs fail closed.
        internal static bool TryAberrateEgoOffset(OpticalVector relativePosition,
            in PresentationVelocity observer, out OpticalVector aberratedOffset)
        {
            aberratedOffset = OpticalVector.Zero;
            double distance = Math.Sqrt(relativePosition.LengthSquared());
            if (!double.IsFinite(distance) || !(distance > 0.0)
                || !RelativisticOptics.TryForward(relativePosition, in observer, out OpticalRay ray))
            {
                return false;
            }

            aberratedOffset = ray.Direction * distance;
            return true;
        }

        // Called from OnRendererRebuilt before verified-idle retirement so the
        // owned UBO and descriptor sets join the same retire list as the other
        // passes. The prepared marker is reset because global frame indices
        // restart and the retired sets are no longer bindable.
        private static void CollectLightingResources(List<IDisposable> resources)
        {
            if (s_lightingResources is { } owned)
            {
                resources.Add(owned);
            }

            s_lightingPreparedFrame = -1;
            s_lightingPreparedOk = false;
        }

        internal static void ResetLightingProbeState()
        {
            s_nativeDescriptorSet = null;
            s_lightingDescriptorSet = null;
            s_lightingProbeInstalled = false;
            s_lightingFailed = false;
            s_lightingAnchorMissed = false;
            s_lightingResources = null;
            s_lightingPreparedFrame = -1;
            s_lightingPreparedOk = false;
            s_lightingPresented = 0;
            s_lightingStatus = "vessel lighting probe not installed";
            SunSpectrumScale.Reset();
            s_lastSunDoppler = 1.0;
        }

        // Owned per-frame copy of the shared global uniform block plus a
        // constants copy, bound through descriptor sets allocated from the
        // native global layout. Only the per-slot window for the frame being
        // recorded is rewritten; the native uniform buffer and descriptor
        // sets are never touched.
        private sealed class VesselLightingUbo : IDisposable
        {
            private readonly BufferEx _ubo;
            private readonly BufferEx _constants;
            private readonly MappedMemory _uboMemory;
            private readonly DescriptorPoolEx _pool;
            private readonly ByteSize _frameStride;
            private readonly ByteSize _lightingOffset;
            private readonly ByteSize _celestialOffset;
            private readonly ByteSize _vesselOffset;
            private bool _uboMapped;
            private bool _disposed;

            internal VesselLightingUbo(Core.Renderer renderer, DescriptorSetLayoutEx globalLayout, int frameCount)
            {
                ArgumentNullException.ThrowIfNull(renderer);
                ArgumentNullException.ThrowIfNull(globalLayout);
                if (frameCount <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(frameCount));
                }

                _lightingOffset = ByteSize.Of<UboCameraData>();
                _celestialOffset = _lightingOffset + ByteSize.Of<UboLightingData>();
                _vesselOffset = _celestialOffset + ByteSize.Of<UboCelestialData>();
                ByteSize payload = _vesselOffset + ByteSize.Of<UboVesselData>();
                ulong alignment = Utils.MinUniformBufferOffsetAlignment;
                ulong remainder = (ulong)payload % alignment;
                _frameStride = remainder == 0
                    ? payload
                    : payload + ByteSize.Of((long)(alignment - remainder));
                FrameCount = frameCount;
                Layout = globalLayout;
                try
                {
                    _ubo = CreateUniformBuffer(renderer, "KSARS_VesselLighting_Ubo", _frameStride * frameCount);
                    _uboMemory = _ubo.Map();
                    _uboMapped = true;
                    _constants = CreateUniformBuffer(renderer, "KSARS_VesselLighting_Constants", ByteSize.Of<UboGlobalConstants>());
                    MappedMemory constantsMemory = _constants.Map();
                    try
                    {
                        constantsMemory.As<UboGlobalConstants>() = GlobalShaderBindings.Constants;
                    }
                    finally
                    {
                        constantsMemory.Unmap();
                    }

                    _pool = DescriptorPoolExExtensions.CreateDescriptorPool(renderer.Device,
                        new DescriptorPoolEx.CreateInfo
                        {
                            MaxSets = frameCount,
                            PoolSizes = new VkDescriptorPoolSize[]
                            {
                                new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 2 * frameCount },
                            },
                        },
                        null);
                    Sets = new VkDescriptorSet[frameCount];
                    for (int i = 0; i < frameCount; i++)
                    {
                        Sets[i] = DescriptorPoolExExtensions.AllocateDescriptorSet(renderer.Device, _pool, globalLayout);
                        var globalInfo = new VkDescriptorBufferInfo
                        {
                            Buffer = _ubo.VkBuffer,
                            Offset = _frameStride * i,
                            Range = _frameStride,
                        };
                        var constantsInfo = new VkDescriptorBufferInfo
                        {
                            Buffer = _constants.VkBuffer,
                            Offset = ByteSize.Zero,
                            Range = ByteSize.Of<UboGlobalConstants>(),
                        };
                        Utils.WriteDescriptorSets(Sets[i],
                            Span<VkDescriptorImageInfo>.Empty, Span<VkDescriptorImageInfo>.Empty,
                            new[] { globalInfo, constantsInfo }, Span<VkDescriptorBufferInfo>.Empty,
                            Span<VkDescriptorBufferInfo>.Empty, renderer);
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal int FrameCount { get; }
            internal DescriptorSetLayoutEx Layout { get; }
            internal VkDescriptorSet[] Sets { get; }

            internal void WriteFrame(int frame, in UboCameraData camera, in UboLightingData lighting,
                in UboCelestialData celestial, in UboVesselData vessel)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ByteSize frameBase = _frameStride * frame;
                _uboMemory.Offset(frameBase).As<UboCameraData>() = camera;
                _uboMemory.Offset(frameBase + _lightingOffset).As<UboLightingData>() = lighting;
                _uboMemory.Offset(frameBase + _celestialOffset).As<UboCelestialData>() = celestial;
                _uboMemory.Offset(frameBase + _vesselOffset).As<UboVesselData>() = vessel;
            }

            private static BufferEx CreateUniformBuffer(Core.Renderer renderer, string name, ByteSize size)
            {
                return BufferExExtensions.CreateBuffer(renderer.Allocator, new BufferEx.CreateInfo
                {
                    Name = name,
                    BufferUsage = VkBufferUsageFlags.UniformBufferBit,
                    BufferSize = size,
                    AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                });
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_uboMapped)
                {
                    _uboMemory.Unmap();
                    _uboMapped = false;
                }

                if (!_ubo.IsNull())
                {
                    _ubo.Dispose();
                }

                if (!_constants.IsNull())
                {
                    _constants.Dispose();
                }

                _pool?.Dispose();
            }
        }
    }
}

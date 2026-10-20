using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.Numerics;
using Brutal.VulkanApi;
using HarmonyLib;
using KSA;
using KSA.Atmosphere.Rendering;
using KSA.Rendering.Water.Rendering;

namespace KSARS.Visuals
{
    // Relativistic media/terrain scope: every native consumer that renders a
    // body in ego space must agree on the apparent position, or the aberrated
    // opaque presentation separates from its atmosphere, clouds, ocean, rings,
    // terrain mesh, and orbit lines. The shared position seam is
    // Camera::GetPositionEgo(IPosition): UboCelestialData packing, the lighting
    // UBO planet position, distant-sphere push constants, ocean, atmosphere,
    // ring meshes, and orbit lines all resolve body positions through it.
    //
    // The postfix transforms the returned ego position with per-argument
    // relative velocity: the aberration driver is (effective observer -
    // arg.GetVelocityEcl()), so a co-moving body (orbited planet, followed
    // vessel, same-bubble craft) aberrates by exactly zero and keeps bitwise
    // rest geometry, while a relativistic flyby or observer override moves the
    // complete body stack consistently. Distance is preserved so distance-
    // gated selection (nearby celestial, billboard, culling radii) is
    // unchanged. Exclusions: the followed object (camera anchor, native
    // already returns -PositionCce), same-bubble vehicles (native returns a
    // bubble-relative offset that is not a world position), and anything that
    // is not IVelocity. The sun IS included — it aberrates like every other
    // body so the native Sun mesh/flare/shadow inputs stay consistent with the
    // aberrated sky (StellarBody was briefly excluded; the solar adapter's own
    // capture callsites restify to direct ECL differences). Gate:
    // only the main viewport's camera in a live non-map non-orthographic
    // flight view; secondary viewports, editor, and map keep rest positions.
    //
    // The cloud/ocean/atmosphere compute paths bypass GetPositionEgo and build
    // planet-relative camera positions as
    //   camera.PositionEcl - body.GetPositionEcl()
    // directly; five methods containing that pattern are transpiled to the
    // equivalent helper so the volumetric stack tracks the same apparent
    // position.
    //
    // Placement is apparent, shading is rest: bodies are co-moving with the
    // sun, so photons arrive at them from the rest-frame direction no matter
    // how fast the camera moves. UpdateShaderData packs one ego sun vector
    // into both shading inputs (UboLightingData::SunPositionRadius, the
    // native CSM direction _sunPositionEgo) and rendered outputs (sun mesh,
    // screenspace flare, SunDot); a postfix restores the two shading fields
    // to the rest sun while the rendered outputs keep the apparent position.
    // Only the active vessel's owned lighting/shadow copies carry the
    // apparent direction (vessel is the relativistic object), and its eclipse
    // verdicts stay rest-frame via occluder re-encoding. Residual: celestial
    // occluder records in Bodies[] stay apparent (they dual-purpose mesh
    // placement), so planet-planet eclipse tests against the rest sun mix
    // frames; vessel eclipses are unaffected.
    //
    // Probes that consume the global shader copies (ObserverSnapshot) must see
    // rest positions: RestoreRestBodyPositions reconstructs them per slot.
    // On by default; KSARS_MEDIA_PROBE=0 disables. Position-only scope: media
    // radiance keeps rest-frame colors; per-body Doppler spectral treatment of
    // native shader output is a separate backlog item.
    internal static partial class RelativisticSpaceRenderer
    {
        private static bool s_mediaInstalled;
        private static bool s_mediaFailed;
        private static bool s_mediaAnchorMissed;
        private static long s_mediaTransformed;
        private static long s_mediaTranspiledCalls;
        private static long s_mediaShadingRestored;
        private static FieldInfo? s_lightingDataField;
        private static FieldInfo? s_sunPositionEgoField;
        // The sunbloom pass (inner-sun disc ray test, occlusion mask, sun
        // visibility and ghost generation) is a rendered-only consumer of
        // UboLightingData::SunPositionRadius: everything it draws belongs to
        // the apparent sun even though the field stays rest for real shading.
        // Its GlobalShaderBindings::DescriptorSet calls are retargeted to the
        // owned apparent-sun copy (DescriptorSetOrNative); anchors are counted
        // at patch time because the renderer's internals are opaque.
        private static MethodInfo? s_sunbloomNativeSet;
        private static MethodInfo? s_sunbloomApparentSet;
        private static int s_sunbloomAnchors;
        private static bool s_sunbloomBound;
        private static string s_mediaStatus = "media probe not installed";

        internal static bool TryInstallMediaProbe(Harmony harmony,
            IReadOnlyDictionary<string, MemberInfo>? members = null)
        {
            if (s_mediaInstalled)
            {
                return true;
            }

            s_mediaStatus = "";
            try
            {
                MethodInfo? getPositionEgo = typeof(Camera).GetMethod(
                    nameof(Camera.GetPositionEgo), BindingFlags.Public | BindingFlags.Instance);
                if (getPositionEgo is null || getPositionEgo.ReturnType != typeof(double3)
                    || getPositionEgo.GetParameters() is not { Length: 1 } egoParameters
                    || egoParameters[0].ParameterType != typeof(IPosition))
                {
                    throw new InvalidOperationException(
                        "media probe unsupported: KSA.Camera::GetPositionEgo(IPosition) signature changed");
                }

                MethodInfo? postfix = typeof(RelativisticSpaceRenderer).GetMethod(
                    nameof(GetPositionEgoPostfix), BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo? transpiler = typeof(RelativisticSpaceRenderer).GetMethod(
                    nameof(TranspileRelativeCameraPositions), BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo? shadingPostfix = typeof(RelativisticSpaceRenderer).GetMethod(
                    nameof(UpdateShaderDataShadingRestPostfix), BindingFlags.NonPublic | BindingFlags.Static);
                if (postfix is null || transpiler is null || shadingPostfix is null)
                {
                    throw new InvalidOperationException(
                        "media probe unsupported: GetPositionEgoPostfix, TranspileRelativeCameraPositions or UpdateShaderDataShadingRestPostfix missing");
                }

                MethodInfo? updateShaderData = typeof(Program).GetMethod(
                    nameof(Program.UpdateShaderData), BindingFlags.Public | BindingFlags.Instance);
                s_lightingDataField = ResolveMediaField(members,
                    "KSA.Program::_lightingData", "_lightingData");
                s_sunPositionEgoField = ResolveMediaField(members,
                    "KSA.Program::_sunPositionEgo", "_sunPositionEgo");
                if (updateShaderData is null
                    || s_lightingDataField?.FieldType != typeof(UboLightingData[])
                    || s_sunPositionEgoField?.FieldType != typeof(double3))
                {
                    throw new InvalidOperationException(
                        "media probe unsupported: Program::UpdateShaderData or the _lightingData/_sunPositionEgo shading fields changed");
                }

                MethodInfo[] relativeTargets =
                {
                    RequiredMediaMethod(typeof(OceanRenderer), nameof(OceanRenderer.Render)),
                    RequiredMediaMethod(typeof(AtmosphereRenderer), nameof(AtmosphereRenderer.RenderWithCompute)),
                    RequiredMediaMethod(typeof(CloudRenderer), nameof(CloudRenderer.UpdateShadowVolume)),
                    RequiredMediaMethod(typeof(CloudRenderer), nameof(CloudRenderer.RenderWithCompute)),
                    RequiredMediaMethod(typeof(CloudRenderer), nameof(CloudRenderer.RenderVolumetricTrailsOnly)),
                };

                var patched = new List<MethodBase>();
                try
                {
                    harmony.Patch(getPositionEgo, postfix: new HarmonyMethod(postfix));
                    patched.Add(getPositionEgo);

                    s_mediaAnchorMissed = false;
                    foreach (MethodInfo target in relativeTargets)
                    {
                        harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
                        patched.Add(target);
                    }

                    harmony.Patch(updateShaderData, postfix: new HarmonyMethod(shadingPostfix));
                    patched.Add(updateShaderData);

                    // The sunbloom pass is a rendered-only consumer of
                    // UboLightingData::SunPositionRadius: its inner-sun disc
                    // test, occlusion mask, visibility and ghost chain belong
                    // to the apparent sun even though the field stays rest for
                    // real shading. Retarget its DescriptorSet calls to the
                    // owned apparent-sun copy. Optional: a missing type or
                    // zero anchors leaves the native rest anchor, which is a
                    // cosmetic defect rather than a shading fault.
                    Type? sunbloomType = typeof(Program).Assembly.GetType("KSA.SunbloomRenderer");
                    s_sunbloomNativeSet = typeof(GlobalShaderBindings).GetMethod(
                        nameof(GlobalShaderBindings.DescriptorSet), BindingFlags.Public | BindingFlags.Static);
                    s_sunbloomApparentSet = typeof(RelativisticSpaceRenderer).GetMethod(
                        nameof(DescriptorSetOrNative), BindingFlags.NonPublic | BindingFlags.Static);
                    if (sunbloomType is not null && s_sunbloomNativeSet is not null
                        && s_sunbloomApparentSet is not null)
                    {
                        MethodInfo? sunbloomTranspiler = typeof(RelativisticSpaceRenderer).GetMethod(
                            nameof(TranspileSunbloomDescriptorSet), BindingFlags.NonPublic | BindingFlags.Static);
                        if (sunbloomTranspiler is not null)
                        {
                            foreach (MethodInfo target in sunbloomType.GetMethods(
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                    | BindingFlags.Static | BindingFlags.DeclaredOnly))
                            {
                                if (target.IsSpecialName || target.IsAbstract
                                    || target.ContainsGenericParameters)
                                {
                                    continue;
                                }

                                harmony.Patch(target, transpiler: new HarmonyMethod(sunbloomTranspiler));
                                patched.Add(target);
                            }

                            s_sunbloomBound = s_sunbloomAnchors > 0;
                        }
                    }

                    if (s_mediaAnchorMissed)
                    {
                        throw new InvalidOperationException(
                            "media probe unsupported: a relative-camera-position method contains no camera.PositionEcl - body.GetPositionEcl() anchor");
                    }
                }
                catch
                {
                    foreach (MethodBase target in patched)
                    {
                        try
                        {
                            harmony.Unpatch(target, HarmonyPatchType.All, RenderHooks.OwnerId);
                        }
                        catch
                        {
                        }
                    }

                    throw;
                }

                s_mediaInstalled = true;
                s_mediaFailed = false;
                s_mediaStatus = $"media probe installed: apparent body positions bound at Camera::GetPositionEgo for main viewport (CelestialData bodies, planet lighting position, sun position, distant spheres, ocean, atmosphere, rings, orbit lines) plus planet-relative camera positions for ocean/atmosphere/cloud compute paths; per-argument relative velocity, co-moving bodies keep rest geometry; native shading keeps the rest-frame sun (SunPositionRadius, CSM); sunbloom rebind anchors={s_sunbloomAnchors}; position-only, media radiance keeps rest colors";
                Console.WriteLine($"KSARS: {s_mediaStatus}");
                return true;
            }
            catch (Exception ex)
            {
                s_mediaStatus = $"media probe disabled: {ex.GetType().Name}: {ex.Message}; native body positions retained";
                Console.WriteLine($"KSARS: {s_mediaStatus}");
                return false;
            }
        }

        private static MethodInfo RequiredMediaMethod(Type declaringType, string name)
        {
            MethodInfo[] candidates = declaringType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(method => method.Name == name).ToArray();
            if (candidates.Length == 0)
            {
                throw new InvalidOperationException(
                    $"media probe unsupported: {declaringType.FullName}::{name} missing");
            }

            // Overloads are tolerated only when exactly one takes a body-ish
            // first argument; anything else is a signature change and fails
            // closed rather than patching an unrelated overload.
            if (candidates.Length > 1)
            {
                candidates = candidates.Where(method =>
                        method.GetParameters() is { Length: > 0 } parameters
                        && (typeof(IPosition).IsAssignableFrom(parameters[0].ParameterType)
                            || parameters[0].ParameterType.Name.Contains("Planet")))
                    .ToArray();
            }

            if (candidates.Length != 1)
            {
                throw new InvalidOperationException(
                    $"media probe unsupported: {declaringType.FullName}::{name} has {candidates.Length} ambiguous overloads");
            }

            return candidates[0];
        }

        private static FieldInfo? ResolveMediaField(
            IReadOnlyDictionary<string, MemberInfo>? members, string key, string name) =>
            members is not null && members.TryGetValue(key, out MemberInfo? member)
                && member is FieldInfo field
                ? field
                : typeof(Program).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);

        // Whether apparent-position rewriting applies for this camera this
        // call. Mirrors the observer-capture validity gate: main viewport only,
        // live followed vessel, no map/orthographic/editor. Deliberately does
        // not require a captured snapshot so UpdateShaderData-time calls and
        // render-time calls agree within the frame.
        private static bool IsMediaTransformActive(Camera? camera)
        {
            if (!s_mediaInstalled || s_mediaFailed || s_recordingDisabled
                || camera is null || camera.Orthographic)
            {
                return false;
            }

            if (Program.Editor is not null || Universe.CurrentSystem is null)
            {
                return false;
            }

            IViewport? main = Program.MainViewport;
            return main is not null && main.Visible && main.Mode != CameraMode.Map
                && ReferenceEquals(main.GetCamera(), camera)
                && camera.Following is Vehicle { IsDisposed: false };
        }

        // Effective observer velocity in star frame: vessel velocity normally,
        // or the override beta along the real velocity direction (camera
        // forward when the vessel is at rest), matching EffectiveObserver.
        private static bool TryEffectiveObserverVelocity(Camera camera, out OpticalVector velocity)
        {
            velocity = OpticalVector.Zero;
            if (camera.Following is not Vehicle { IsDisposed: false } followed)
            {
                return false;
            }

            if (!s_betaOverrideEnabled)
            {
                double3 vesselVelocity = followed.GetVelocityEcl();
                if (!IsFinite(vesselVelocity))
                {
                    return false;
                }

                velocity = new OpticalVector(vesselVelocity.X, vesselVelocity.Y, vesselVelocity.Z);
                return true;
            }

            double beta = Math.Clamp(s_betaOverride, 0.0, 1.0 - 1e-15);
            double3 rawVelocity = followed.GetVelocityEcl();
            double speedSquared = rawVelocity.X * rawVelocity.X + rawVelocity.Y * rawVelocity.Y
                + rawVelocity.Z * rawVelocity.Z;
            double3 direction = speedSquared > 1e-6 && IsFinite(rawVelocity)
                ? rawVelocity.Normalized()
                : camera.GetForwardEcl();
            if (!IsFinite(direction) || direction.LengthSquared() <= 0.0)
            {
                return false;
            }

            double speed = beta * PhysicsConstants.SpeedOfLight;
            velocity = new OpticalVector(direction.X * speed, direction.Y * speed, direction.Z * speed);
            return true;
        }

        // Aberrates one ego position by the argument's relative velocity.
        // Returns false (and leaves restEgo as the out value) whenever the
        // transform does not apply: inactive gate, anchor/bubble exclusions,
        // non-IVelocity, or a co-moving argument whose relative observer
        // is exactly rest. Distance is preserved by construction.
        internal static bool TryAberrateEgo(Camera camera, object? arg, double3 restEgo,
            out double3 aberrated)
        {
            aberrated = restEgo;
            if (!IsMediaTransformActive(camera)
                || arg is null or not IVelocity
                || ReferenceEquals(arg, camera.Following))
            {
                return false;
            }

            if (camera.Following is Vehicle { BubbleLeader: not null } followedVehicle
                && arg is Vehicle otherVehicle
                && otherVehicle.BubbleLeader == followedVehicle.BubbleLeader)
            {
                return false;
            }

            if (!TryEffectiveObserverVelocity(camera, out OpticalVector observerVelocity))
            {
                return false;
            }

            double3 argVelocity = ((IVelocity)arg).GetVelocityEcl();
            if (!IsFinite(argVelocity))
            {
                return false;
            }

            if (!TryRelativeObserver(in observerVelocity,
                    new OpticalVector(argVelocity.X, argVelocity.Y, argVelocity.Z),
                    out PresentationVelocity relative))
            {
                return false;
            }

            double distance = restEgo.Length();
            if (!(distance > 0.0) || !double.IsFinite(distance)
                || !RelativisticOptics.TryForward(
                    new OpticalVector(restEgo.X, restEgo.Y, restEgo.Z), in relative, out OpticalRay ray))
            {
                return false;
            }

            aberrated = new double3(
                ray.Direction.X * distance, ray.Direction.Y * distance, ray.Direction.Z * distance);
            return true;
        }

        // The aberration driver for one argument: observer velocity expressed
        // in the argument's rest frame, approximated by the star-frame vector
        // difference (error O(beta_arg) relative at planetary speeds). Returns
        // false for degenerate or exactly-rest cases so co-moving arguments
        // keep bitwise rest geometry.
        internal static bool TryRelativeObserver(in OpticalVector observerVelocity,
            in OpticalVector argVelocity, out PresentationVelocity relative)
        {
            relative = default;
            if (!RelativisticOptics.TryCreateVelocity(
                    new OpticalVector(
                        observerVelocity.X - argVelocity.X,
                        observerVelocity.Y - argVelocity.Y,
                        observerVelocity.Z - argVelocity.Z),
                    out PresentationVelocity candidate)
                || candidate.Q == 1.0)
            {
                return false;
            }

            relative = candidate;
            return true;
        }

        // Postfix on Camera::GetPositionEgo(IPosition). Must not throw.
        private static void GetPositionEgoPostfix(Camera __instance, IPosition astronomical,
            ref double3 __result)
        {
            try
            {
                if (__instance is not null && s_mediaInstalled && !s_mediaFailed
                    && TryAberrateEgo(__instance, astronomical, __result, out double3 aberrated))
                {
                    __result = aberrated;
                    s_mediaTransformed++;
                }
            }
            catch (Exception ex)
            {
                s_mediaFailed = true;
                s_mediaStatus = $"media probe disabled after transform fault ({ex.GetType().Name}: {ex.Message}); native body positions retained";
                Console.WriteLine($"KSARS: {s_mediaStatus}");
            }
        }

        // Debug bisect gates (debug builds only): KSARS_MEDIA_NOSUNREST leaves
        // UboLightingData::SunPositionRadius apparent and KSARS_MEDIA_NOCSMREST
        // leaves Program::_sunPositionEgo apparent. Diagnoses which restified
        // field anchors a rendered artifact; not a supported configuration.
        private static readonly bool s_skipSunRadiusRest =
#if DEBUG
            Environment.GetEnvironmentVariable("KSARS_MEDIA_NOSUNREST") == "1";
#else
            false;
#endif
        private static readonly bool s_skipCsmRest =
#if DEBUG
            Environment.GetEnvironmentVariable("KSARS_MEDIA_NOCSMREST") == "1";
#else
            false;
#endif

        // Postfix on Program::UpdateShaderData(double, IViewport). Native
        // fills both rendered outputs (sun mesh, flare, SunDot) and shading
        // inputs (SunPositionRadius, _sunPositionEgo) from one apparent ego
        // sun vector; this restores the shading inputs to the rest-frame sun
        // so every co-moving surface keeps rest lighting while the rendered
        // sun stays aberrated. Runs only when the media transform is active
        // for that viewport's camera; must not throw.
        private static void UpdateShaderDataShadingRestPostfix(Program __instance,
            IViewport viewport)
        {
            try
            {
                if (viewport is null)
                {
                    return;
                }

                Camera? camera = viewport.GetCamera();
                if (!IsMediaTransformActive(camera) || Universe.WorldSun is not { } sun)
                {
                    return;
                }

                double3 restSun = sun.GetPositionEcl() - camera!.PositionEcl;
                if (!IsFinite(restSun) || restSun.LengthSquared() == 0.0)
                {
                    return;
                }

                if (!s_skipSunRadiusRest
                    && s_lightingDataField?.GetValue(__instance) is UboLightingData[] lighting
                    && (uint)viewport.ShaderSlot < (uint)lighting.Length)
                {
                    lighting[viewport.ShaderSlot].SunPositionRadius = new float4(
                        float3.Pack(in restSun),
                        lighting[viewport.ShaderSlot].SunPositionRadius.W);
                }

                if (!s_skipCsmRest && viewport.IsMain())
                {
                    s_sunPositionEgoField?.SetValue(__instance, restSun);
                }

                s_mediaShadingRestored++;
            }
            catch (Exception ex)
            {
                s_mediaFailed = true;
                s_mediaStatus = $"media probe disabled after shading-rest fault ({ex.GetType().Name}: {ex.Message}); native body positions retained";
                Console.WriteLine($"KSARS: {s_mediaStatus}");
            }
        }

        // Replaces every GlobalShaderBindings::DescriptorSet call inside the
        // sunbloom renderer with DescriptorSetOrNative so the pass binds the
        // owned apparent-sun lighting copy. Tolerant: methods without the
        // call are left untouched; the count feeds s_sunbloomBound/status.
        internal static IEnumerable<CodeInstruction> TranspileSunbloomDescriptorSet(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = instructions.ToList();
            if (s_sunbloomNativeSet is null || s_sunbloomApparentSet is null)
            {
                return list;
            }

            foreach (CodeInstruction instruction in list)
            {
                if (instruction.opcode == System.Reflection.Emit.OpCodes.Call
                    && instruction.operand is MethodInfo method
                    && method.Equals(s_sunbloomNativeSet))
                {
                    instruction.operand = s_sunbloomApparentSet;
                    s_sunbloomAnchors++;
                }
            }

            return list;
        }

        // Rewrites the IL pattern
        //   callvirt Camera::get_PositionEcl ; <load body> ;
        //   callvirt *::GetPositionEcl ; call double3::op_Subtraction
        // into RelativeCameraPosition(camera, body): removing both getters
        // leaves (camera, body) on the stack for the substitute call.
        internal static IEnumerable<CodeInstruction> TranspileRelativeCameraPositions(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = instructions.ToList();
            MethodInfo? helper = typeof(RelativisticSpaceRenderer).GetMethod(
                nameof(RelativeCameraPosition), BindingFlags.NonPublic | BindingFlags.Static);
            if (helper is null)
            {
                return list;
            }

            int replaced = 0;
            for (int i = 0; i + 2 < list.Count; i++)
            {
                if (!IsCallNamed(list[i], "get_PositionEcl"))
                {
                    continue;
                }

                int getterIndex = -1;
                for (int j = i + 1; j < list.Count && j <= i + 8; j++)
                {
                    if (IsCallNamed(list[j], "GetPositionEcl"))
                    {
                        getterIndex = j;
                        break;
                    }

                    if (IsCallNamed(list[j], "get_PositionEcl"))
                    {
                        break;
                    }
                }

                if (getterIndex < 0 || getterIndex + 1 >= list.Count
                    || !IsSubtractionCall(list[getterIndex + 1]))
                {
                    continue;
                }

                list[i].opcode = OpCodes.Nop;
                list[i].operand = null;
                list[getterIndex].opcode = OpCodes.Nop;
                list[getterIndex].operand = null;
                CodeInstruction subtraction = list[getterIndex + 1];
                subtraction.opcode = OpCodes.Call;
                subtraction.operand = helper;
                replaced++;
            }

            if (replaced == 0)
            {
                s_mediaAnchorMissed = true;
            }

            s_mediaTranspiledCalls += replaced;
            return list;
        }

        private static bool IsCallNamed(CodeInstruction instruction, string name) =>
            (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
            && instruction.operand is MethodInfo method && method.Name == name;

        private static bool IsSubtractionCall(CodeInstruction instruction) =>
            instruction.opcode == OpCodes.Call
            && instruction.operand is MethodInfo method && method.Name == "op_Subtraction";

        // Substitute for `camera.PositionEcl - body.GetPositionEcl()` inside the
        // transpiled media methods: the negated apparent ego position. Must
        // not throw.
        private static double3 RelativeCameraPosition(Camera camera, IPosition body)
        {
            double3 relative = camera.PositionEcl - body.GetPositionEcl();
            try
            {
                var rest = new double3(-relative.X, -relative.Y, -relative.Z);
                if (TryAberrateEgo(camera, body, rest, out double3 aberrated))
                {
                    return new double3(-aberrated.X, -aberrated.Y, -aberrated.Z);
                }
            }
            catch (Exception ex)
            {
                s_mediaFailed = true;
                s_mediaStatus = $"media probe disabled after relative-position fault ({ex.GetType().Name}: {ex.Message}); native body positions retained";
                Console.WriteLine($"KSARS: {s_mediaStatus}");
            }

            return relative;
        }

        // Reconstructs rest body positions for snapshot consumers after the
        // media transform rewrote the global copies. CelestialData slots are
        // matched back to their packed body by slot index, radius, and the
        // expected apparent position so default zero indices (every celestial
        // pre-maps to slot 0) cannot corrupt the parent entry. The lighting
        // planet position is restored the same way for the effective nearby
        // atmospheric body. Verification failures leave the captured value.
        internal static void RestoreRestBodyPositions(Camera camera,
            ref UboLightingData lighting, ref UboCelestialData celestial)
        {
            if (!IsMediaTransformActive(camera))
            {
                return;
            }

            int count = Math.Min(Math.Max(celestial.BodyCount, 0), CelestialBody24.MAX);
            if (count > 0 && Program.GetPlanetRenderer()?.CelestialIndices is { } indices)
            {
                foreach (KeyValuePair<Celestial, int> entry in indices)
                {
                    int slot = entry.Value;
                    if ((uint)slot >= (uint)count)
                    {
                        continue;
                    }

                    Celestial body = entry.Key;
                    CelestialBody packed = celestial.Bodies[slot];
                    if (!PackedSlotMatches(camera, body, in packed))
                    {
                        continue;
                    }

                    double3 rest = body.GetPositionEcl() - camera.PositionEcl;
                    if (IsFinite(rest))
                    {
                        celestial.Bodies[slot] = new CelestialBody(
                            float3.Pack(in rest), packed.Radius);
                    }
                }
            }

            if (camera.GetEffectiveNearbyCelestial() is AtmosphericBody nearby
                && lighting.PlanetPosition.W == 1f)
            {
                double3 rest = nearby.GetPositionEcl() - camera.PositionEcl;
                if (IsFinite(rest) && TryAberrateEgo(camera, nearby, rest, out double3 expected)
                    && ApproxMatch(lighting.PlanetPosition.X, lighting.PlanetPosition.Y,
                        lighting.PlanetPosition.Z, expected))
                {
                    lighting.PlanetPosition = new float4(
                        float3.Pack(in rest), lighting.PlanetPosition.W);
                }
            }

            // SunPositionRadius is already rest in the global copy once the
            // UpdateShaderData shading-rest postfix ran; this arm stays as a
            // defensive restore (self-verifying: it only rewrites when the
            // captured value still looks apparent).
            if (Universe.WorldSun is { } sun)
            {
                double3 restSun = sun.GetPositionEcl() - camera.PositionEcl;
                if (IsFinite(restSun) && TryAberrateEgo(camera, sun, restSun, out double3 expectedSun)
                    && ApproxMatch(lighting.SunPositionRadius.X, lighting.SunPositionRadius.Y,
                        lighting.SunPositionRadius.Z, expectedSun))
                {
                    lighting.SunPositionRadius = new float4(
                        float3.Pack(in restSun), lighting.SunPositionRadius.W);
                }
            }
        }

        private static bool PackedSlotMatches(Camera camera, Celestial body, in CelestialBody packed)
        {
            double radius = packed.Radius;
            if (!double.IsFinite(radius)
                || Math.Abs(radius - (float)body.MeanRadius)
                    > Math.Max(1.0, Math.Abs(radius) * 1e-3))
            {
                return false;
            }

            double3 rest = body.GetPositionEcl() - camera.PositionEcl;
            if (!IsFinite(rest))
            {
                return false;
            }

            _ = TryAberrateEgo(camera, body, rest, out double3 expected);
            return ApproxMatch(packed.Position.X, packed.Position.Y, packed.Position.Z, expected);
        }

        private static bool ApproxMatch(double px, double py, double pz, double3 expected)
        {
            double dx = px - expected.X;
            double dy = py - expected.Y;
            double dz = pz - expected.Z;
            double magnitude = Math.Sqrt(expected.X * expected.X + expected.Y * expected.Y
                + expected.Z * expected.Z);
            double tolerance = Math.Max(1e3, magnitude * 1e-4);
            return dx * dx + dy * dy + dz * dz <= tolerance * tolerance;
        }

        internal static void ResetMediaProbeState()
        {
            s_mediaInstalled = false;
            s_mediaFailed = false;
            s_mediaAnchorMissed = false;
            s_mediaTransformed = 0;
            s_mediaTranspiledCalls = 0;
            s_mediaShadingRestored = 0;
            s_lightingDataField = null;
            s_sunPositionEgoField = null;
            s_sunbloomNativeSet = null;
            s_sunbloomApparentSet = null;
            s_sunbloomAnchors = 0;
            s_sunbloomBound = false;
            s_mediaStatus = "media probe not installed";
        }

        internal static string MediaStatus =>
            s_mediaInstalled && !s_mediaFailed
                ? $"media probe active: apparent body positions via relative-velocity aberration (sun included); native shading keeps rest sun/CSM; sunbloom apparent-bound={(s_sunbloomBound ? $"yes ({s_sunbloomAnchors} anchors)" : "no")}; transforms={s_mediaTransformed} transpiledCalls={s_mediaTranspiledCalls} shadingRest={s_mediaShadingRestored}{(s_skipSunRadiusRest ? " NOSUNREST" : "")}{(s_skipCsmRest ? " NOCSMREST" : "")}; media radiance rest-colored; position-only scope"
                : s_mediaStatus;
    }
}

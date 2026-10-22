using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using KSARS.Visuals;
using StarMap.API;

namespace KSARS
{
    [StarMapMod]
    [HarmonyPatch]
    public class KSARS
    {
        private const double SpeedOfLight = PhysicsConstants.SpeedOfLight;

        // Last-step diagnostics written by the integration postfix and shown in
        // the KSARS window, to distinguish a real integration defect from display
        // or frame noise when watching relativistic burns at high timewarp.
        private struct DbgStep
        {
            public double Dt;
            public double StartSpeed;
            public double NewtonianDelta;
            public double Applied;
            public double OrbitDelta;
            public double ParentDelta;
            public double PhysResidual;
            public double BodyRate;
            public Situation Situation;
            public string Parent;
        }

        private const int DbgCapacity = 12;
        private static readonly DbgStep[] _dbgSteps = new DbgStep[DbgCapacity];
        private static int _dbgCount;
        private static int _dbgHead;
        private static int _nanRecoveries;

        private static bool _rotationLockX;
        private static bool _rotationLockY;
        private static bool _rotationLockZ;
        private static bool _stopRotationRequested;
        private static doubleQuat _lockedAttitude;
        private static bool _hasLockedAttitude;

        private readonly struct RelativisticIntegrationState
        {
            public RelativisticIntegrationState(BubbleOrigin origin, BubbleOrigin nextOrigin, KinematicStates kinematic, double3 positionCci, double3 velocityCci, double3 velocityEcl, double3 parentVelocityEcl, doubleQuat body2Cci)
            {
                Origin = origin;
                NextOrigin = nextOrigin;
                Kinematic = kinematic;
                PositionCci = positionCci;
                VelocityCci = velocityCci;
                VelocityEcl = velocityEcl;
                ParentVelocityEcl = parentVelocityEcl;
                Body2Cci = body2Cci;
            }

            public BubbleOrigin Origin { get; }
            public BubbleOrigin NextOrigin { get; }
            public KinematicStates Kinematic { get; }
            public double3 PositionCci { get; }
            public double3 VelocityCci { get; }
            public double3 VelocityEcl { get; }
            public double3 ParentVelocityEcl { get; }
            public doubleQuat Body2Cci { get; }
        }

        [StarMapImmediateLoad]
        public void Init(Mod definingMod)
        {
            Console.WriteLine("Hello World from KSARS!");
            var harmony = new Harmony("KSARS");
            harmony.CreateClassProcessor(typeof(KSARS)).Patch();
        }

        [StarMapBeforeMain]
        public void BeforeMain()
        {
            Visuals.RelativisticVisuals.InspectBaseline(typeof(Program).Assembly);
        }

        [StarMapUnload]
        public void Unload()
        {
            Visuals.RelativisticVisuals.Shutdown();
        }

        [StarMapAfterGui]
        public unsafe void AfterGui(double dt)
        {
            ImGui.Begin("KSARS");

            Vehicle? current = Program.ControlledVehicle;
            if (current != null)
            {
                double displaySpeed = current.GetVelocityEcl().Length();
                ImGui.Text($"Current speed: {displaySpeed:F2} m/s");
                ImGui.Text($"Current speed (relativistic): {displaySpeed / SpeedOfLight:F16} c");
                ImGui.Separator();
                ImGui.Text("recent: start(c) | applied | newt dV | spin(r/s) | situ | dt");
                for (int shown = 0; shown < _dbgCount; shown++)
                {
                    DbgStep s = _dbgSteps[(_dbgHead - 1 - shown + DbgCapacity) % DbgCapacity];
                    ImGui.Text($"  {s.StartSpeed:F8} {s.Applied,8:F2} {s.NewtonianDelta,9:F0} {s.BodyRate,7:F3} {s.Situation} @{s.Parent} {s.Dt:F0}");
                }
                if (_nanRecoveries > 0)
                {
                    ImGui.Text($"non-finite step recoveries: {_nanRecoveries}");
                }

                if (ImGui.Button("Stop rotation"))
                {
                    _stopRotationRequested = true;
                    current.GetPhysicsStatesMutable().SetBodyRates(double3.Zero);
                }
                ImGui.SameLine();
                ImGui.Checkbox("Lock X", ref _rotationLockX);
                ImGui.SameLine();
                ImGui.Checkbox("Lock Y", ref _rotationLockY);
                ImGui.SameLine();
                ImGui.Checkbox("Lock Z", ref _rotationLockZ);
                if (_rotationLockX || _rotationLockY || _rotationLockZ)
                {
                    PhysicsStates mutable = current.GetPhysicsStatesMutable();
                    double3 rates = mutable.GetBodyRates();
                    mutable.SetBodyRates(new double3(_rotationLockX ? 0.0 : rates.X, _rotationLockY ? 0.0 : rates.Y, _rotationLockZ ? 0.0 : rates.Z));
                }
            }

#if DEBUG
            string diagnostics = $"Visuals: {Visuals.RelativisticVisuals.Status}";
            ImGui.Text(diagnostics);
            if (ImGui.Button("Copy diagnostics"))
            {
                ImGui.SetClipboardText(diagnostics);
            }
#endif

            Visuals.RelativisticVisuals.DrawProbeGui();
            ImGui.End();
        }

        // Disable structural failure detection to prevent unrealistic breakage at relativistic speeds
        [HarmonyPatch(typeof(PhysicsBubble), "DetectStructuralFailure"), HarmonyPrefix]
        public static bool VehicleUpdateTask_DetectStructuralFailure_Prefix(VehicleUpdateTask __instance) => false;

        [HarmonyPatch(typeof(FlightComputer), "SolveGLoadThrottleCap"), HarmonyPostfix]
        public static void FlightComputer_SolveGLoadThrottleCap_Postfix(ref float __result) => __result = 1.0f; // Disable G-load throttle cap to allow full thrust at relativistic speeds

        [HarmonyPatch(typeof(PhysicsStates), nameof(PhysicsStates.IntegrateVelocityVerlet)), HarmonyPrefix]
        private static void PhysicsStates_IntegrateVelocityVerlet_Prefix(ref PhysicsStates __instance, in BubbleOrigin nextOrigin, out RelativisticIntegrationState __state)
        {
            __state = CaptureIntegrationState(ref __instance, in nextOrigin);
        }

        [HarmonyPatch(typeof(PhysicsStates), nameof(PhysicsStates.IntegrateVelocityVerlet)), HarmonyPostfix]
        private static void PhysicsStates_IntegrateVelocityVerlet_Postfix(ref PhysicsStates __instance, double dt, ref KinematicMeasurements measurements, RelativisticIntegrationState __state)
        {
            CorrectIntegrationResult(ref __instance, dt, ref measurements, in __state);
        }

        // The integrators extrapolate mass as StartMass + TotalMassRate·stage;
        // on a very long step that extrapolation can go non-positive (all
        // propellant would be consumed, or none in cinematic free-burn),
        // producing infinite/NaN acceleration that poisons the kinematic state.
        // Clamp the mass rate so the extrapolated mass can't drop below dry mass.
        [HarmonyPatch(typeof(PhysicsStates), nameof(PhysicsStates.ComputeDerivatives), new Type[]
        {
            typeof(double), typeof(double), typeof(double3), typeof(double3),
            typeof(ReadOnlySpan<ActiveNozzle>), typeof(ReadOnlySpan<ActiveChute>)
        }), HarmonyPostfix]
        private static void PhysicsStates_ComputeDerivatives_Postfix(ref Disturbances __result, in PhysicsStates __instance, double dt)
        {
            double minRate = -__instance.Props.TotalPropellantMass / dt;
            if (__result.TotalMassRate < minRate)
            {
                __result.TotalMassRate = minRate;
            }
        }

        // Maneuvering vehicles in a CCI bubble switch to IntegrateRk8 once the
        // sub-step exceeds two seconds, which is what high timewarp produces.
        [HarmonyPatch(typeof(PhysicsStates), nameof(PhysicsStates.IntegrateRk8)), HarmonyPrefix]
        private static void PhysicsStates_IntegrateRk8_Prefix(ref PhysicsStates __instance, in BubbleOrigin nextOrigin, out RelativisticIntegrationState __state)
        {
            __state = CaptureIntegrationState(ref __instance, in nextOrigin);
        }

        [HarmonyPatch(typeof(PhysicsStates), nameof(PhysicsStates.IntegrateRk8)), HarmonyPostfix]
        private static void PhysicsStates_IntegrateRk8_Postfix(ref PhysicsStates __instance, double dt, ref KinematicMeasurements measurements, RelativisticIntegrationState __state)
        {
            CorrectIntegrationResult(ref __instance, dt, ref measurements, in __state);
        }

        private static RelativisticIntegrationState CaptureIntegrationState(ref PhysicsStates states, in BubbleOrigin nextOrigin)
        {
            BubbleOrigin origin = states.Origin;
            if (!IsFinite(origin.PositionBub) || !IsFinite(origin.VelocityBub))
            {
                // The stored origin can come back non-finite when the bubble's
                // conic is ill-conditioned (near-radial ~c orbits make the
                // anomaly solve produce NaN/Inf). Substitute the best finite
                // anchor available so this step can still produce finite output.
                origin = IsFinite(nextOrigin.PositionBub) && IsFinite(nextOrigin.VelocityBub)
                    ? nextOrigin
                    : BubbleOrigin.CreateZero(origin.Parent, nextOrigin.Time);
                states.Origin = origin;
            }
            KinematicStates kinematic = states.Kinematic;
            PhysicsStates.GetStatesCci(in origin, in kinematic, out double3 positionCci, out double3 velocityCci, out doubleQuat body2Cci);
            double3 parentVelocityEcl = GetParentVelocityEcl(origin.Parent, origin.Time);
            double3 velocityEcl = parentVelocityEcl + velocityCci.Transform(origin.Parent.GetCci2Cce());

            return new RelativisticIntegrationState(origin, nextOrigin, kinematic, positionCci, velocityCci, velocityEcl, parentVelocityEcl, body2Cci);
        }

        private static void CorrectIntegrationResult(ref PhysicsStates states, double dt, ref KinematicMeasurements measurements, in RelativisticIntegrationState state)
        {
            BubbleOrigin origin = state.Origin;
            BubbleOrigin nextOrigin = state.NextOrigin;
            KinematicStates newKinematic = states.Kinematic;
            PhysicsStates.GetStatesCci(in origin, in newKinematic, out double3 positionCci, out double3 velocityCci, out _);

            doubleQuat cci2Cce = origin.Parent.GetCci2Cce();
            // The bubble origin rides the leader's conic and the parent keeps moving
            // during the step; both drift terms are part of the vehicle's ECL delta
            // and must go through the same relativistic scaling, or they leak in
            // unbounded, especially on long timewarp steps.
            double3 parentVelocityEclEnd = GetParentVelocityEcl(origin.Parent, nextOrigin.Time);
            double3 deltaVelocityEcl = (velocityCci - state.VelocityCci + (nextOrigin.VelocityBub - origin.VelocityBub)).Transform(cci2Cce) + (parentVelocityEclEnd - state.ParentVelocityEcl);
            double3 finalVelocityEcl = AdvanceRelativistically(state.VelocityEcl, deltaVelocityEcl);
            double3 newtonianVelocityEclEnd = state.VelocityEcl + deltaVelocityEcl;
            double3 correctedVelocityCci = (finalVelocityEcl - parentVelocityEclEnd).Transform(cci2Cce.Inverse());
            double3 correctedPositionCci = positionCci + (nextOrigin.PositionBub - origin.PositionBub) + 0.5 * dt * (finalVelocityEcl - newtonianVelocityEclEnd).Transform(cci2Cce.Inverse());

            if (!IsFinite(positionCci) || !IsFinite(velocityCci)
                || !IsFinite(origin.PositionBub) || !IsFinite(origin.VelocityBub)
                || !IsFinite(nextOrigin.PositionBub) || !IsFinite(nextOrigin.VelocityBub)
                || !IsFinite(finalVelocityEcl) || !IsFinite(newtonianVelocityEclEnd)
                || !IsFinite(correctedPositionCci) || !IsFinite(correctedVelocityCci)
                || !IsFinite(states.Kinematic.Body2Phys) || !IsFinite(states.Kinematic.AngularVelocityPhys))
            {
                // A non-finite step result can't be reconciled. Restore the
                // exact pre-step kinematic buffer rather than re-anchoring:
                // the anchors themselves may be non-finite, so any arithmetic
                // on them would just produce NaN again.
                states.Kinematic = state.Kinematic;
                if (IsFinite(origin.PositionBub) && IsFinite(origin.VelocityBub))
                {
                    states.Origin = origin;
                }
                measurements.DeltaVelocityCci = double3.Zero;
                measurements.AccelerationBody = double3.Zero;
                _nanRecoveries++;
                return;
            }

            // Optional rotation controls: per-axis locks zero the locked
            // body-rate axes every sub-step so torque cannot accumulate spin
            // over long timewarp steps; a one-shot stop request zeroes all of
            // them once. Applies to every vehicle that integrates, at any
            // speed. With all three axes locked the orientation itself is
            // pinned, since a zeroed rate still allows mid-step attitude drift.
            if (_rotationLockX || _rotationLockY || _rotationLockZ)
            {
                double3 bodyRates = states.GetBodyRates();
                states.SetBodyRates(new double3(_rotationLockX ? 0.0 : bodyRates.X, _rotationLockY ? 0.0 : bodyRates.Y, _rotationLockZ ? 0.0 : bodyRates.Z));
                if (_rotationLockX && _rotationLockY && _rotationLockZ)
                {
                    if (!_hasLockedAttitude)
                    {
                        _lockedAttitude = states.Kinematic.Body2Phys;
                        _hasLockedAttitude = true;
                    }
                    states.Kinematic.Body2Phys = _lockedAttitude;
                }
            }
            else
            {
                _hasLockedAttitude = false;
            }

            if (_stopRotationRequested)
            {
                states.SetBodyRates(double3.Zero);
                _stopRotationRequested = false;
            }

            SetStatesCci(ref states, in nextOrigin, correctedPositionCci, correctedVelocityCci);
            CorrectMeasurements(ref measurements, in state, cci2Cce, origin.GetBub2Cci());

            _dbgSteps[_dbgHead] = new DbgStep
            {
                Dt = dt,
                StartSpeed = state.VelocityEcl.Length() / SpeedOfLight,
                NewtonianDelta = deltaVelocityEcl.Length(),
                Applied = finalVelocityEcl.Length() - state.VelocityEcl.Length(),
                OrbitDelta = (nextOrigin.VelocityBub - origin.VelocityBub).Length(),
                ParentDelta = (parentVelocityEclEnd - state.ParentVelocityEcl).Length(),
                PhysResidual = states.Kinematic.VelocityPhys.Length(),
                BodyRate = states.GetBodyRates().Length(),
                Situation = states.Props.Situation,
                Parent = origin.Parent?.Id.ToString() ?? ""
            };
            _dbgHead = (_dbgHead + 1) % DbgCapacity;
            _dbgCount = Math.Min(_dbgCount + 1, DbgCapacity);
        }

        private static double3 GetParentVelocityEcl(IParentBody parent, UniverseTime time)
        {
            return parent is IOrbiter orbiter ? orbiter.GetVelocityEcl(time) : parent.GetVelocityEcl();
        }

        private static bool IsFinite(double3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

        private static bool IsFinite(doubleQuat q) => double.IsFinite(q.X) && double.IsFinite(q.Y) && double.IsFinite(q.Z) && double.IsFinite(q.W);

        // Near-radial ~c conics are numerically ill-conditioned (p -> 0 makes
        // GetOrbitalVelocity evaluate sqrt(mu/0)), so propagation can return
        // NaN/Inf vectors. Substitute the orbit's stored finite vectors so
        // consumers (bubble origins, conic display, flight computer) never
        // see non-finite state.
        [HarmonyPatch(typeof(Orbit), nameof(Orbit.GetStateVectorsAt), new Type[] { typeof(UniverseTime) }), HarmonyPostfix]
        private static void Orbit_GetStateVectorsAt_Postfix(Orbit __instance, UniverseTime simTime, ref StateVectors __result)
        {
            if (IsFinite(__result.PositionCci) && IsFinite(__result.VelocityCci))
            {
                return;
            }
            StateVectors stored = __instance.StateVectors;
            if (IsFinite(stored.PositionCci) && IsFinite(stored.VelocityCci))
            {
                __result = new StateVectors(simTime, stored.PositionCci, stored.VelocityCci, stored.TrueAnomaly);
            }
        }

        private static double3 AdvanceRelativistically(double3 velocityEcl, double3 deltaVelocityEcl)
        {
            return ToDouble3(RelativisticDynamics.Advance(ToOptical(velocityEcl), ToOptical(deltaVelocityEcl)));
        }

        private static OpticalVector ToOptical(double3 v) => new(v.X, v.Y, v.Z);

        private static double3 ToDouble3(OpticalVector v) => new(v.X, v.Y, v.Z);

        private static void SetStatesCci(ref PhysicsStates states, in BubbleOrigin nextOrigin, double3 positionCci, double3 velocityCci)
        {
            if (nextOrigin.BubFrame == BubbleFrame.Cci)
            {
                states.Kinematic.PositionPhys = positionCci - nextOrigin.PositionBub;
                states.Kinematic.VelocityPhys = velocityCci - nextOrigin.VelocityBub;
                return;
            }

            VehicleReferenceFrameEx.CciStatesToCcf(nextOrigin.Parent, nextOrigin.Time, positionCci, velocityCci, out double3 positionCcf, out double3 velocityCcf);
            states.Kinematic.PositionPhys = positionCcf - nextOrigin.PositionBub;
            states.Kinematic.VelocityPhys = velocityCcf - nextOrigin.VelocityBub;
        }

        private static void CorrectMeasurements(ref KinematicMeasurements measurements, in RelativisticIntegrationState state, doubleQuat cci2Cce, doubleQuat bub2Cci)
        {
            double3 measuredDeltaEcl = measurements.DeltaVelocityCci.Transform(cci2Cce);
            measuredDeltaEcl = AdvanceRelativistically(state.VelocityEcl, measuredDeltaEcl) - state.VelocityEcl;
            measurements.DeltaVelocityCci = measuredDeltaEcl.Transform(cci2Cce.Inverse());

            double3 accelerationEcl = measurements.AccelerationBody.Transform(state.Body2Cci).Transform(cci2Cce);
            accelerationEcl = AdvanceRelativistically(state.VelocityEcl, accelerationEcl) - state.VelocityEcl;
            measurements.AccelerationBody = accelerationEcl.Transform(cci2Cce.Inverse()).Transform(state.Body2Cci.Inverse());
        }
    }
}

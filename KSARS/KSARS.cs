using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using StarMap.API;

namespace KSARS
{
    [StarMapMod]
    [HarmonyPatch]
    public class KSARS
    {
        private const double SpeedOfLight = 299792458.0;

        private readonly struct RelativisticIntegrationState
        {
            public RelativisticIntegrationState(BubbleOrigin origin, KinematicStates kinematic, double3 positionCci, double3 velocityCci, double3 velocityEcl, doubleQuat body2Cci)
            {
                Origin = origin;
                Kinematic = kinematic;
                PositionCci = positionCci;
                VelocityCci = velocityCci;
                VelocityEcl = velocityEcl;
                Body2Cci = body2Cci;
            }

            public BubbleOrigin Origin { get; }
            public KinematicStates Kinematic { get; }
            public double3 PositionCci { get; }
            public double3 VelocityCci { get; }
            public double3 VelocityEcl { get; }
            public doubleQuat Body2Cci { get; }
        }

        [StarMapImmediateLoad]
        public void Init(Mod definingMod)
        {
            Console.WriteLine("Hello World from KSARS!");
            var harmony = new Harmony("KSARS");
            harmony.PatchAll();
        }

        [StarMapAfterGui]
        public unsafe void AfterGui(double dt)
        {
            Vehicle current = Program.ControlledVehicle;
            if (current == null) return;

            ImGui.Begin("KSARS");
            ImGui.Text($"Current speed: {current.GetVelocityEcl().Length():F2} m/s");
            ImGui.Text($"Current speed (relativistic): {current.GetVelocityEcl().Length() / SpeedOfLight:F16} c");
            ImGui.End();
        }

        [HarmonyPatch(typeof(VehicleUpdateTask), "DetectStructuralFailure"), HarmonyPrefix]
        public static bool VehicleUpdateTask_DetectStructuralFailure_Prefix(VehicleUpdateTask __instance)
        {
            // Disable structural failure detection to prevent unrealistic breakage at relativistic speeds
            return false;
        }

        [HarmonyPatch(typeof(PhysicsStates), nameof(PhysicsStates.IntegrateVelocityVerlet)), HarmonyPrefix]
        private static void PhysicsStates_IntegrateVelocityVerlet_Prefix(ref PhysicsStates __instance, out RelativisticIntegrationState __state)
        {
            BubbleOrigin origin = __instance.Origin;
            KinematicStates kinematic = __instance.Kinematic;
            PhysicsStates.GetStatesCci(in origin, in kinematic, out double3 positionCci, out double3 velocityCci, out doubleQuat body2Cci);
            double3 parentVelocityEcl = GetParentVelocityEcl(origin.Parent, origin.Time);
            double3 velocityEcl = parentVelocityEcl + velocityCci.Transform(origin.Parent.GetCci2Cce());

            __state = new RelativisticIntegrationState(origin, kinematic, positionCci, velocityCci, velocityEcl, body2Cci);
        }

        [HarmonyPatch(typeof(PhysicsStates), nameof(PhysicsStates.IntegrateVelocityVerlet)), HarmonyPostfix]
        private static void PhysicsStates_IntegrateVelocityVerlet_Postfix(ref PhysicsStates __instance, double dt, ref KinematicMeasurements measurements, RelativisticIntegrationState __state)
        {
            BubbleOrigin origin = __state.Origin;
            KinematicStates newKinematic = __instance.Kinematic;
            PhysicsStates.GetStatesCci(in origin, in newKinematic, out double3 positionCci, out double3 velocityCci, out _);

            doubleQuat cci2Cce = origin.Parent.GetCci2Cce();
            double3 deltaVelocityEcl = (velocityCci - __state.VelocityCci).Transform(cci2Cce);
            double3 relativisticDeltaEcl = ApplyRelativisticAcceleration(__state.VelocityEcl, deltaVelocityEcl);
            double3 finalVelocityEcl = LimitToLightSpeed(__state.VelocityEcl + relativisticDeltaEcl);
            relativisticDeltaEcl = finalVelocityEcl - __state.VelocityEcl;

            double3 correctedDeltaCci = relativisticDeltaEcl.Transform(cci2Cce.Inverse());
            double3 newtonianDeltaCci = velocityCci - __state.VelocityCci;
            double3 correctedVelocityCci = __state.VelocityCci + correctedDeltaCci;
            double3 correctedPositionCci = positionCci + 0.5 * dt * (correctedDeltaCci - newtonianDeltaCci);

            SetStatesCci(ref __instance, in origin, correctedPositionCci, correctedVelocityCci);
            CorrectMeasurements(ref measurements, in __state, cci2Cce, origin.GetBub2Cci());
        }

        private static double3 GetParentVelocityEcl(IParentBody parent, SimTime time)
        {
            return parent is IOrbiter orbiter ? orbiter.GetVelocityEcl(time) : parent.GetVelocityEcl();
        }

        private static double3 ApplyRelativisticAcceleration(double3 velocityEcl, double3 deltaVelocityEcl)
        {
            double speedSquared = velocityEcl.LengthSquared();
            if (speedSquared.IsNearlyZero())
            {
                return deltaVelocityEcl;
            }

            double betaSquared = Math.Min(speedSquared / (SpeedOfLight * SpeedOfLight), 1.0 - 1e-15);
            double inverseGamma = Math.Sqrt(1.0 - betaSquared);
            double3 velocityDirection = velocityEcl / Math.Sqrt(speedSquared);
            double3 parallel = double3.Dot(deltaVelocityEcl, velocityDirection) * velocityDirection;
            double3 perpendicular = deltaVelocityEcl - parallel;

            return inverseGamma * inverseGamma * inverseGamma * parallel + inverseGamma * inverseGamma * perpendicular;
        }

        private static double3 LimitToLightSpeed(double3 velocityEcl)
        {
            double speed = velocityEcl.Length();
            double maximumSpeed = Math.BitDecrement(SpeedOfLight);
            return speed > maximumSpeed ? velocityEcl * (maximumSpeed / speed) : velocityEcl;
        }

        private static void SetStatesCci(ref PhysicsStates states, in BubbleOrigin origin, double3 positionCci, double3 velocityCci)
        {
            if (origin.BubFrame == BubbleFrame.Cci)
            {
                states.Kinematic.PositionPhys = positionCci - origin.PositionBub;
                states.Kinematic.VelocityPhys = velocityCci - origin.VelocityBub;
                return;
            }

            VehicleReferenceFrameEx.CciStatesToCcf(origin.Parent, origin.Time, positionCci, velocityCci, out double3 positionCcf, out double3 velocityCcf);
            states.Kinematic.PositionPhys = positionCcf - origin.PositionBub;
            states.Kinematic.VelocityPhys = velocityCcf - origin.VelocityBub;
        }

        private static void CorrectMeasurements(ref KinematicMeasurements measurements, in RelativisticIntegrationState state, doubleQuat cci2Cce, doubleQuat bub2Cci)
        {
            double3 measuredDeltaEcl = measurements.DeltaVelocityCci.Transform(cci2Cce);
            measuredDeltaEcl = ApplyRelativisticAcceleration(state.VelocityEcl, measuredDeltaEcl);
            measurements.DeltaVelocityCci = measuredDeltaEcl.Transform(cci2Cce.Inverse());

            double3 accelerationEcl = measurements.AccelerationBody.Transform(state.Body2Cci).Transform(cci2Cce);
            accelerationEcl = ApplyRelativisticAcceleration(state.VelocityEcl, accelerationEcl);
            measurements.AccelerationBody = accelerationEcl.Transform(cci2Cce.Inverse()).Transform(state.Body2Cci.Inverse());
        }
    }
}

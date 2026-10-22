using KSARS.Visuals;

namespace KSARS
{
    // Relativistic delta-velocity model shared by the integration patches.
    // Kept on OpticalVector so the math carries no game types and stays testable.
    internal static class RelativisticDynamics
    {
        private const double SpeedOfLight = PhysicsConstants.SpeedOfLight;
        private const double SubdivisionStepSpeed = 0.005 * SpeedOfLight;
        private const int MaxDeltaSubdivisions = 2048;

        internal static OpticalVector ApplyAcceleration(OpticalVector velocityEcl, OpticalVector deltaVelocityEcl)
        {
            double speedSquared = velocityEcl.LengthSquared();
            if (speedSquared <= 0.0)
            {
                return deltaVelocityEcl;
            }

            double betaSquared = Math.Min(speedSquared / (SpeedOfLight * SpeedOfLight), 1.0 - 1e-15);
            double inverseGamma = Math.Sqrt(1.0 - betaSquared);
            OpticalVector velocityDirection = velocityEcl / Math.Sqrt(speedSquared);
            OpticalVector parallel = velocityDirection * deltaVelocityEcl.Dot(velocityDirection);
            OpticalVector perpendicular = deltaVelocityEcl - parallel;

            return parallel * (inverseGamma * inverseGamma * inverseGamma) + perpendicular * (inverseGamma * inverseGamma);
        }

        // Applies a delta velocity relativistically, subdividing large changes so
        // the gamma factor is re-evaluated against the running velocity. A single
        // long-step burn scaled only by the start-of-step gamma would otherwise
        // still gain far too much speed.
        internal static OpticalVector Advance(OpticalVector velocityEcl, OpticalVector deltaVelocityEcl)
        {
            int subdivisions = Math.Clamp((int)Math.Ceiling(Math.Sqrt(deltaVelocityEcl.LengthSquared()) / SubdivisionStepSpeed), 1, MaxDeltaSubdivisions);
            OpticalVector piece = deltaVelocityEcl / subdivisions;
            for (int i = 0; i < subdivisions; i++)
            {
                velocityEcl = LimitToLightSpeed(velocityEcl + ApplyAcceleration(velocityEcl, piece));
            }

            return velocityEcl;
        }

        internal static OpticalVector LimitToLightSpeed(OpticalVector velocityEcl)
        {
            double speedSquared = velocityEcl.LengthSquared();
            double maximumSpeed = Math.BitDecrement(SpeedOfLight);
            return speedSquared > maximumSpeed * maximumSpeed
                ? velocityEcl * (maximumSpeed / Math.Sqrt(speedSquared))
                : velocityEcl;
        }
    }
}

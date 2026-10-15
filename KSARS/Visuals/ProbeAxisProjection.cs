namespace KSARS.Visuals
{
    internal static class ProbeAxisProjection
    {
        internal static bool TryGetViewAngles(OpticalVector viewDirection,
            out double yawDegrees, out double pitchDegrees)
        {
            yawDegrees = 0.0;
            pitchDegrees = 0.0;
            if (!RelativisticOptics.TryNormalize(viewDirection, out OpticalVector unit))
            {
                return false;
            }

            yawDegrees = Math.Atan2(unit.X, -unit.Z) * (180.0 / Math.PI);
            pitchDegrees = Math.Atan2(unit.Y,
                Math.Sqrt(unit.X * unit.X + unit.Z * unit.Z)) * (180.0 / Math.PI);
            return true;
        }

        internal static bool TryGetUv(double clipX, double clipY, double clipW,
            out double u, out double v)
        {
            u = 0.0;
            v = 0.0;
            if (!double.IsFinite(clipX) || !double.IsFinite(clipY) || !double.IsFinite(clipW)
                || clipW <= 0.0)
            {
                return false;
            }

            u = 0.5 * (clipX / clipW + 1.0);
            v = 0.5 * (clipY / clipW + 1.0);
            return double.IsFinite(u) && double.IsFinite(v);
        }

        internal static bool TryAngleDegrees(OpticalVector a, OpticalVector b, out double degrees)
        {
            degrees = 0.0;
            if (!RelativisticOptics.TryNormalize(a, out OpticalVector na)
                || !RelativisticOptics.TryNormalize(b, out OpticalVector nb))
            {
                return false;
            }

            degrees = Math.Acos(Math.Clamp(na.Dot(nb), -1.0, 1.0)) * (180.0 / Math.PI);
            return double.IsFinite(degrees);
        }
    }
}

namespace KSARS.Visuals
{
    internal sealed class ProbeCaptureGeneration
    {
        internal const int MaxGenerations = 3;

        internal int Generation { get; private set; }
        internal bool Queued { get; private set; }
        internal bool BudgetExhausted => Generation + 1 >= MaxGenerations;

        internal bool TryQueue()
        {
            if (BudgetExhausted)
            {
                return false;
            }

            Queued = true;
            return true;
        }

        internal bool TryConsume(bool hasValidSnapshot)
        {
            if (!Queued || !hasValidSnapshot)
            {
                return false;
            }

            Queued = false;
            if (BudgetExhausted)
            {
                return false;
            }

            Generation++;
            return true;
        }

        internal void Reset()
        {
            Generation = 0;
            Queued = false;
        }
    }

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

    internal static class FrameResourceRetirement
    {
        internal static bool TryRetireAfterIdle(IEnumerable<IDisposable> resources, out AggregateException? error)
        {
            List<Exception>? errors = null;
            foreach (IDisposable resource in resources)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }
            }

            error = errors is null ? null : new AggregateException(errors);
            return error is null;
        }
    }
}

namespace KSARS.Visuals
{
    internal readonly record struct StellarTemperatureFit(
        double TemperatureKelvin, double ResidualU, double ResidualV, bool AtTemperatureBoundary);

    // A blackbody prior fitted in CIE 1960 UCS, not a measured stellar temperature.
    // Input is nonnegative linear XYZ; brightness/PSF scale is intentionally excluded.
    internal sealed class StellarTemperatureFitter
    {
        internal const double MinimumKelvin = 800.0;
        internal const double MaximumKelvin = 100000.0;
        private const int GridIntervals = 128;
        private readonly Func<double, (double X, double Y, double Z)> _reference;
        private readonly (double U, double V)[] _grid = new (double, double)[GridIntervals + 1];
        private static readonly double MinLog = Math.Log(MinimumKelvin);
        private static readonly double MaxLog = Math.Log(MaximumKelvin);

        internal StellarTemperatureFitter(Func<double, (double X, double Y, double Z)> reference)
        {
            ArgumentNullException.ThrowIfNull(reference);
            _reference = reference;
            for (int i = 0; i <= GridIntervals; i++)
                _grid[i] = Model(LogAt(i));
        }

        private static double LogAt(int index) => index == 0 ? MinLog
            : index == GridIntervals ? MaxLog : MinLog + (MaxLog - MinLog) * index / GridIntervals;
        private static (double U, double V) Uv(double x, double y, double z)
        {
            double denominator = x + 15.0 * y + 3.0 * z;
            return (4.0 * x / denominator, 6.0 * y / denominator);
        }

        private (double U, double V) Model(double logTemperature)
        {
            var model = _reference(Math.Exp(logTemperature));
            return Uv(model.X, model.Y, model.Z);
        }

        private static double DistanceSquared((double U, double V) a, (double U, double V) b) =>
            (a.U - b.U) * (a.U - b.U) + (a.V - b.V) * (a.V - b.V);

        internal bool TryFit(double x, double y, double z, out StellarTemperatureFit fit)
        {
            fit = default;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
                || x < 0.0 || y < 0.0 || z < 0.0)
                return false;
            double scale = Math.Max(x, Math.Max(y, z));
            if (!(scale > 0.0))
                return false;
            var target = Uv(x / scale, y / scale, z / scale);

            int bestIndex = 0;
            double bestDistance = DistanceSquared(target, _grid[0]);
            for (int i = 1; i <= GridIntervals; i++)
            {
                double distance = DistanceSquared(target, _grid[i]);
                if (distance < bestDistance)
                {
                    bestIndex = i;
                    bestDistance = distance;
                }
            }

            double bestLog = LogAt(bestIndex);
            double lower = LogAt(Math.Max(0, bestIndex - 1));
            double upper = LogAt(Math.Min(GridIntervals, bestIndex + 1));
            const double golden = 0.6180339887498948482;
            double left = upper - golden * (upper - lower);
            double right = lower + golden * (upper - lower);
            double leftDistance = DistanceSquared(target, Model(left));
            double rightDistance = DistanceSquared(target, Model(right));
            for (int i = 0; i < 64; i++)
            {
                if (leftDistance <= rightDistance)
                {
                    upper = right;
                    right = left;
                    rightDistance = leftDistance;
                    left = upper - golden * (upper - lower);
                    leftDistance = DistanceSquared(target, Model(left));
                }
                else
                {
                    lower = left;
                    left = right;
                    leftDistance = rightDistance;
                    right = lower + golden * (upper - lower);
                    rightDistance = DistanceSquared(target, Model(right));
                }
            }
            double candidate = (lower + upper) * 0.5;
            if (DistanceSquared(target, Model(candidate)) < bestDistance)
                bestLog = candidate;
            var selected = Model(bestLog);
            bool boundary = bestLog == MinLog || bestLog == MaxLog;
            double temperature = bestLog == MinLog ? MinimumKelvin
                : bestLog == MaxLog ? MaximumKelvin : Math.Exp(bestLog);
            fit = new StellarTemperatureFit(temperature,
                target.U - selected.U, target.V - selected.V, boundary);
            return true;
        }
    }
}

namespace KSARS.Visuals
{
    internal readonly struct AtlasProjection
    {
        internal AtlasProjection(OpticalVector right, OpticalVector up, OpticalVector forward, double distance, double restAngularRadius, double tanHalfFov, double near, double far)
        {
            Right = right;
            Up = up;
            Forward = forward;
            Distance = distance;
            RestAngularRadius = restAngularRadius;
            TanHalfFov = tanHalfFov;
            Near = near;
            Far = far;
        }

        public OpticalVector Right { get; }
        public OpticalVector Up { get; }
        public OpticalVector Forward { get; }
        public double Distance { get; }
        public double RestAngularRadius { get; }
        public double TanHalfFov { get; }
        public double Near { get; }
        public double Far { get; }

        public static bool TryCreate(OpticalVector restPositionEgo, double enclosingRadius, int tileSize, int guardPixels, out AtlasProjection projection)
        {
            projection = default;
            if (!double.IsFinite(enclosingRadius) || enclosingRadius <= 0.0
                || guardPixels < 1 || (long)tileSize <= 2L * guardPixels + 2L)
            {
                return false;
            }

            int innerSize = tileSize - 2 * guardPixels;

            if (!RelativisticOptics.TryNormalize(restPositionEgo, out OpticalVector forward))
            {
                return false;
            }

            double max = Math.Max(Math.Abs(restPositionEgo.X), Math.Max(Math.Abs(restPositionEgo.Y), Math.Abs(restPositionEgo.Z)));
            var scaled = restPositionEgo / max;
            double distance = max * Math.Sqrt(scaled.LengthSquared());
            if (!(distance > enclosingRadius))
            {
                return false;
            }

            double restAngularRadius = Math.Asin(enclosingRadius / distance);
            double tanHalfFov = Math.Tan(restAngularRadius) * tileSize / innerSize;
            double near = distance * 0.01;
            double far = (distance + 2.0 * enclosingRadius) * 1.01;
            float fovRadians = (float)(2.0 * Math.Atan(tanHalfFov));
            float nearF = (float)near;
            float farF = (float)far;
            if (!double.IsFinite(tanHalfFov) || !float.IsFinite(fovRadians) || !(fovRadians > 0.0f) || !(fovRadians < MathF.PI)
                || !float.IsFinite(nearF) || !(nearF > 0.0f)
                || !float.IsFinite(farF) || !(farF > nearF))
            {
                return false;
            }

            var referenceUp = Math.Abs(forward.Y) > 0.9 ? new OpticalVector(1.0, 0.0, 0.0) : new OpticalVector(0.0, 1.0, 0.0);
            if (!RelativisticOptics.TryNormalize(forward.Cross(referenceUp), out OpticalVector right))
            {
                return false;
            }

            projection = new AtlasProjection(right, right.Cross(forward), forward, distance, restAngularRadius, tanHalfFov, near, far);
            return true;
        }

        internal static bool TryGetObservedPixelDiameter(in ObservedSphericalCap cap, OpticalVector viewAxis,
            double pixelScaleX, double pixelScaleY, out double diameter)
        {
            diameter = 0.0;
            if (!double.IsFinite(cap.AngularRadius) || cap.AngularRadius < 0.0 || cap.AngularRadius >= Math.PI / 2.0)
            {
                return false;
            }

            double s = cap.SinAngularRadius;
            if (!double.IsFinite(s) || s < 0.0 || s >= 1.0)
            {
                return false;
            }

            if (!double.IsFinite(pixelScaleX) || !(pixelScaleX > 0.0)
                || !double.IsFinite(pixelScaleY) || !(pixelScaleY > 0.0))
            {
                return false;
            }

            if (!RelativisticOptics.TryNormalize(viewAxis, out OpticalVector axis))
            {
                return false;
            }

            double z = axis.Z;
            if (!(z > s + 1e-5))
            {
                return false;
            }

            double den = z * z - s * s;
            double rx = z * z + axis.X * axis.X - s * s;
            double ry = z * z + axis.Y * axis.Y - s * s;
            if (!(den > 0.0) || rx < 0.0 || ry < 0.0
                || !double.IsFinite(den) || !double.IsFinite(rx) || !double.IsFinite(ry))
            {
                return false;
            }

            double widthX = pixelScaleX * (2.0 * s * Math.Sqrt(rx) / den);
            double widthY = pixelScaleY * (2.0 * s * Math.Sqrt(ry) / den);
            double bound = Math.Max(widthX, widthY) * (1.0 + 1e-6);
            if (!double.IsFinite(bound) || bound < 0.0)
            {
                return false;
            }

            diameter = bound;
            return true;
        }

        public bool TryProjectRestRay(OpticalVector ray, out double u, out double v)
        {
            u = 0.0;
            v = 0.0;
            if (!RelativisticOptics.TryNormalize(ray, out OpticalVector direction))
            {
                return false;
            }

            double z = Forward.Dot(direction);
            if (!(z > 0.0))
            {
                return false;
            }

            double projectedU = 0.5 * (1.0 + Right.Dot(direction) / (z * TanHalfFov));
            double projectedV = 0.5 * (1.0 - Up.Dot(direction) / (z * TanHalfFov));
            if (!double.IsFinite(projectedU) || !double.IsFinite(projectedV)
                || projectedU < 0.0 || projectedU > 1.0 || projectedV < 0.0 || projectedV > 1.0)
            {
                return false;
            }

            u = projectedU;
            v = projectedV;
            return true;
        }

        public bool TryReconstructRadialDistance(double u, double v, double depth, out double radial)
        {
            radial = 0.0;
            if (!double.IsFinite(u) || !double.IsFinite(v) || u < 0.0 || u > 1.0 || v < 0.0 || v > 1.0
                || !double.IsFinite(depth) || depth <= 0.0 || depth > 1.0)
            {
                return false;
            }

            double ratio = Near / Far;
            double a = ratio / (1.0 - ratio);
            double b = Near / (1.0 - ratio);
            double z = b / (depth + a);
            double dx = (2.0 * u - 1.0) * TanHalfFov;
            double dy = (2.0 * v - 1.0) * TanHalfFov;
            double r = z * Math.Sqrt(1.0 + dx * dx + dy * dy);
            if (!double.IsFinite(r) || !(r > 0.0))
            {
                return false;
            }

            radial = r;
            return true;
        }
    }
}

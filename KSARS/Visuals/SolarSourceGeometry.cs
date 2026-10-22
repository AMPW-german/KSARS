namespace KSARS.Visuals
{
    internal readonly struct SolarSourceGeometry
    {
        internal SolarSourceGeometry(double distance, double tanHalfFov,
            double physicalRadiusRatio, double nearRatio, double farRatio)
        {
            Distance = distance;
            TanHalfFov = tanHalfFov;
            PhysicalRadiusRatio = physicalRadiusRatio;
            NearRatio = nearRatio;
            FarRatio = farRatio;
        }

        internal double Distance { get; }
        internal double TanHalfFov { get; }
        internal double PhysicalRadiusRatio { get; }
        internal double NearRatio { get; }
        internal double FarRatio { get; }

        public static bool TryCreate(in AtlasProjection projection, double physicalRadius,
            out SolarSourceGeometry model)
        {
            model = default;
            double distance = projection.Distance;
            if (!double.IsFinite(physicalRadius) || !(physicalRadius > 0.0)
                || !double.IsFinite(distance) || !(distance > 0.0))
            {
                return false;
            }

            double radiusRatio = physicalRadius / distance;
            if (!double.IsFinite(radiusRatio) || !(radiusRatio > 0.0) || !(radiusRatio < 0.25))
            {
                return false;
            }

            double tanHalfFov = projection.TanHalfFov;
            if (!double.IsFinite(tanHalfFov) || !(tanHalfFov > 0.0))
            {
                return false;
            }

            double near = projection.Near;
            double far = projection.Far;
            if (!double.IsFinite(near) || !(near > 0.0) || !(near < distance - physicalRadius)
                || !double.IsFinite(far) || !(far > distance + physicalRadius))
            {
                return false;
            }

            double nearRatio = near / distance;
            double farRatio = far / distance;
            if (!double.IsFinite(nearRatio) || !(nearRatio > 0.0)
                || !double.IsFinite(farRatio) || !(farRatio > nearRatio))
            {
                return false;
            }

            model = new SolarSourceGeometry(distance, tanHalfFov, radiusRatio,
                nearRatio, farRatio);
            return true;
        }

        internal bool TrySample(double u, double v, double radiusScale,
            out double radial, out double depth)
        {
            radial = 0.0;
            depth = 0.0;
            if (!double.IsFinite(u) || u < 0.0 || u > 1.0
                || !double.IsFinite(v) || v < 0.0 || v > 1.0
                || !double.IsFinite(radiusScale) || !(radiusScale > 0.0) || radiusScale > 4.0)
            {
                return false;
            }

            double radiusRatio = PhysicalRadiusRatio * radiusScale;
            if (!double.IsFinite(radiusRatio) || !(radiusRatio > 0.0) || !(radiusRatio < 1.0))
            {
                return false;
            }

            var direction = new OpticalVector(
                (2.0 * u - 1.0) * TanHalfFov,
                (2.0 * v - 1.0) * TanHalfFov,
                1.0);
            if (!RelativisticOptics.TryNormalize(direction, out OpticalVector ray))
            {
                return false;
            }

            double discriminant = radiusRatio * radiusRatio - (ray.X * ray.X + ray.Y * ray.Y);
            if (!double.IsFinite(discriminant) || discriminant < 0.0)
            {
                return false;
            }

            double radialNorm = ray.Z - Math.Sqrt(discriminant);
            double sourceZNorm = radialNorm * ray.Z;
            double reverseZ = (NearRatio * FarRatio / sourceZNorm - NearRatio)
                / (FarRatio - NearRatio);
            double absoluteRadial = Distance * radialNorm;
            if (!double.IsFinite(radialNorm) || !(radialNorm > 0.0)
                || !double.IsFinite(sourceZNorm) || !(sourceZNorm > 0.0)
                || !double.IsFinite(reverseZ) || !(reverseZ > 0.0) || reverseZ > 1.0
                || !double.IsFinite(absoluteRadial) || !(absoluteRadial > 0.0))
            {
                return false;
            }

            radial = absoluteRadial;
            depth = reverseZ;
            return true;
        }
    }
}

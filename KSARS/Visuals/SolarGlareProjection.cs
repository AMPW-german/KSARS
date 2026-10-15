namespace KSARS.Visuals
{
    internal readonly struct SolarGlareProjection
    {
        private SolarGlareProjection(OpticalVector observedCenter, double observedAngularRadius,
            double screenX, double screenY, double sunRadiusPixels, double glowRadiusPixels,
            double sunDot, double sourceDoppler, bool isUnityVelocity, bool anchorSupported)
        {
            ObservedCenter = observedCenter;
            ObservedAngularRadius = observedAngularRadius;
            ScreenX = screenX;
            ScreenY = screenY;
            SunRadiusPixels = sunRadiusPixels;
            GlowRadiusPixels = glowRadiusPixels;
            SunDot = sunDot;
            SourceDoppler = sourceDoppler;
            IsUnityVelocity = isUnityVelocity;
            AnchorSupported = anchorSupported;
        }

        public OpticalVector ObservedCenter { get; }
        public double ObservedAngularRadius { get; }
        public double ScreenX { get; }
        public double ScreenY { get; }
        public double SunRadiusPixels { get; }
        public double GlowRadiusPixels { get; }
        public double SunDot { get; }
        public double SourceDoppler { get; }
        public bool IsUnityVelocity { get; }
        public bool AnchorSupported { get; }

        public double BrightnessScale => SourceDoppler * SourceDoppler * SourceDoppler;

        internal static bool TryCreate(in AtlasProjection source, double physicalRadius,
            in PresentationVelocity observer, OpticalVector viewForward, OpticalVector viewRight,
            OpticalVector viewUp, double verticalFov, int width, int height,
            out SolarGlareProjection projection)
        {
            projection = default;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            if (!double.IsFinite(verticalFov) || verticalFov <= 0.0 || verticalFov >= Math.PI)
            {
                return false;
            }

            double tanHalf = Math.Tan(verticalFov * 0.5);
            double aspect = (double)width / height;
            if (!double.IsFinite(tanHalf) || !(tanHalf > 0.0)
                || !double.IsFinite(aspect) || !(aspect > 0.0))
            {
                return false;
            }

            if (!RelativisticOptics.TryNormalize(viewForward, out OpticalVector forward)
                || !RelativisticOptics.TryNormalize(viewRight, out OpticalVector right)
                || !RelativisticOptics.TryNormalize(viewUp, out OpticalVector up)
                || Math.Abs(forward.Dot(right)) > 1e-6
                || Math.Abs(forward.Dot(up)) > 1e-6
                || Math.Abs(right.Dot(up)) > 1e-6)
            {
                return false;
            }

            double handedness = right.Cross(up).Dot(forward);
            if (!double.IsFinite(handedness) || Math.Abs(handedness) < 0.999999)
            {
                return false;
            }

            if (!double.IsFinite(physicalRadius) || !(physicalRadius > 0.0)
                || !double.IsFinite(source.Distance) || !(source.Distance > 0.0))
            {
                return false;
            }

            double radiusRatio = physicalRadius / source.Distance;
            if (!double.IsFinite(radiusRatio) || !(radiusRatio > 0.0) || !(radiusRatio < 0.25))
            {
                return false;
            }

            if (!RelativisticOptics.TryForward(source.Forward, in observer, out OpticalRay ray)
                || !RelativisticOptics.TryTransformSphericalCap(source.Forward,
                    Math.Asin(radiusRatio), in observer, out ObservedSphericalCap cap)
                || !double.IsFinite(cap.AngularRadius) || cap.AngularRadius < 0.0
                || cap.AngularRadius >= Math.PI)
            {
                return false;
            }

            // Native UpdateShaderData: 0.5 * GetObjectDiameterPixels(2R, distance) which is
            // height * sin(angularRadius) / (2 * tan(fov/2)); applied to the transformed cap.
            double sunRadiusPixels = Math.Max(
                0.5 * height * Math.Sin(cap.AngularRadius) / tanHalf, 4.0);
            double glowRadiusPixels = Math.Max(50.0 * sunRadiusPixels, 100.0);
            OpticalVector center = ray.Direction;
            double sunDot = Math.Clamp(forward.Dot(center), 0.0, 1.0);
            double doppler = ray.DopplerFactor;
            double scale = doppler * doppler * doppler;
            if (!double.IsFinite(sunRadiusPixels) || !double.IsFinite(glowRadiusPixels)
                || !double.IsFinite(sunDot) || !double.IsFinite(doppler)
                || !double.IsFinite(scale))
            {
                return false;
            }

            double z = forward.Dot(center);
            double x = right.Dot(center);
            double y = up.Dot(center);
            double screenX = 0.5;
            double screenY = 0.5;
            bool anchored = false;
            if (z > 0.0 && cap.AngularRadius < Math.PI / 2.0)
            {
                double projectedX = 0.5 * (1.0 + x / (z * tanHalf * aspect));
                double projectedY = 0.5 * (1.0 - y / (z * tanHalf));
                if (double.IsFinite(projectedX) && double.IsFinite(projectedY))
                {
                    screenX = projectedX;
                    screenY = projectedY;
                    anchored = true;
                }
            }

            projection = new SolarGlareProjection(center, cap.AngularRadius, screenX, screenY,
                sunRadiusPixels, glowRadiusPixels, sunDot, doppler, observer.Q == 1.0, anchored);
            return true;
        }
    }
}

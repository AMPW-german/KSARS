namespace KSARS.Visuals
{
    // Modeled integral of the supported stock star sprite at the on-axis 1920px
    // reference view, before bloom/tone mapping. This is a relative flux prior,
    // not a measured irradiance or physical stellar angular radius.
    internal static class StarFluxReference
    {
        internal const double ReferenceWidth = 1920.0;
        internal const double SourceDistance = 19.5;
        private static readonly double MaxIrradiation =
            Math.Pow(0.01 * 512.0 / 0.123, 2.0) / (2.0 * Math.PI);

        internal static double Integrate(byte scale, int angularIntervals = 32, int radialIntervals = 256)
        {
            if (angularIntervals < 2 || angularIntervals % 2 != 0
                || radialIntervals < 2 || radialIntervals % 2 != 0)
                throw new ArgumentOutOfRangeException(nameof(angularIntervals));
            if (scale == 0) return 0.0;
            double radius = scale / 255.0;
            double maxTheta = 0.123 * Math.Sqrt(Math.Atan(radius / MaxIrradiation) * MaxIrradiation);
            double angularStep = (Math.PI / 4.0) / angularIntervals;
            double integral = 0.0;
            for (int a = 0; a <= angularIntervals; a++)
            {
                double phi = a * angularStep;
                // Exact first-octant edge of the stock UV octagon: (1,0) to (.708,.708).
                double limit = 0.708 / (0.708 * Math.Cos(phi) + 0.292 * Math.Sin(phi));
                double radialStep = limit / radialIntervals;
                double radial = 0.0;
                for (int r = 0; r <= radialIntervals; r++)
                {
                    double offset = r * radialStep;
                    int weight = (r == 0 || r == radialIntervals) ? 1 : (r % 2 == 0 ? 2 : 4);
                    radial += weight * Brightness(offset, maxTheta) * offset;
                }
                int angularWeight = (a == 0 || a == angularIntervals) ? 1 : (a % 2 == 0 ? 2 : 4);
                integral += angularWeight * radial * radialStep / 3.0;
            }
            // Eight octants; UV area has a factor 1/4 relative to the offset disk.
            double uvIntegral = 2.0 * integral * angularStep / 3.0;
            double pixelSide = radius * ReferenceWidth / (2.0 * SourceDistance);
            return uvIntegral * pixelSide * pixelSide;
        }

        private static double Smooth(double low, double high, double value)
        {
            double x = Math.Clamp((value - low) / (high - low), 0.0, 1.0);
            return x * x * (3.0 - 2.0 * x);
        }

        private static double Brightness(double offset, double maxTheta)
        {
            double theta = offset * 0.01;
            double ratio = maxTheta / Math.Max(theta, 0.00001) - 1.0;
            double wing = 0.0016 * ratio * ratio * (1.0 - Smooth(maxTheta * 0.7, maxTheta, theta));
            double centerMix = 1.0 - Smooth(0.0, 0.005, theta);
            double point = wing + (1.5 - wing) * centerMix;
            // Model the stock signed fringe too; normalized physical PSF output is nonnegative.
            return Math.Min(1.5, point * (1.0 - offset));
        }

    }
}

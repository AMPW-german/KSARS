namespace KSARS.Visuals
{
    internal static class SpectralColorSpace
    {
        // Explicit working-space prior: linear sRGB/Rec.709 primaries, D65 white.
        // No sRGB transfer-function decoding is applied to the game's raw catalog bytes.
        internal static bool TryLinearRgbToXyz(double r, double g, double b,
            out (double X, double Y, double Z) xyz)
        {
            xyz = default;
            if (!double.IsFinite(r) || !double.IsFinite(g) || !double.IsFinite(b)
                || r < 0.0 || g < 0.0 || b < 0.0)
                return false;
            double x = 0.4123907992659595 * r + 0.357584339383878 * g + 0.1804807884018343 * b;
            double y = 0.2126390058715104 * r + 0.715168678767756 * g + 0.0721923153607337 * b;
            double z = 0.0193308187155919 * r + 0.119194779794626 * g + 0.9505321522496607 * b;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                return false;
            xyz = (x, y, z);
            return true;
        }

        // Display-only gamut mapping: desaturate toward equal-RGB gray at the same Y.
        // No upper HDR clamp, exposure adjustment, tone mapping, or change to physical XYZ.
        internal static bool TryXyzToDisplayRgb(double x, double y, double z,
            out (double R, double G, double B) rgb)
        {
            rgb = default;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
                || x < 0.0 || !(y > 0.0) || z < 0.0)
                return false;
            double r = 3.2409699419045226 * x - 1.537383177570094 * y - 0.4986107602930034 * z;
            double g = -0.9692436362808796 * x + 1.8759675015077202 * y + 0.04155505740717559 * z;
            double b = 0.05563007969699366 * x - 0.20397695888897652 * y + 1.0569715142428786 * z;
            if (!double.IsFinite(r) || !double.IsFinite(g) || !double.IsFinite(b))
                return false;
            double desaturation = 0.0;
            if (r < 0.0) desaturation = Math.Max(desaturation, -r / (y - r));
            if (g < 0.0) desaturation = Math.Max(desaturation, -g / (y - g));
            if (b < 0.0) desaturation = Math.Max(desaturation, -b / (y - b));
            rgb = (Math.Max(0.0, r + desaturation * (y - r)),
                Math.Max(0.0, g + desaturation * (y - g)),
                Math.Max(0.0, b + desaturation * (y - b)));
            return true;
        }
    }
}

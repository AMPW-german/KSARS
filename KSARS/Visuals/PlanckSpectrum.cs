namespace KSARS.Visuals
{
    // SI wavelength spectral radiance: W sr^-1 m^-3 (per metre, not per nanometre).
    // Reference math only; no exposure, RGB mapping, solid angle, or renderer calibration.
    internal static class PlanckSpectrum
    {
        internal const double PlanckConstant = 6.62607015e-34;
        internal const double BoltzmannConstant = 1.380649e-23;
        private const double C = PhysicsConstants.SpeedOfLight;
        private static readonly double LogNumerator = Math.Log(2.0 * PlanckConstant * C * C);
        private static readonly double LogSecondRadiationConstant =
            Math.Log(PlanckConstant * C / BoltzmannConstant);

        // A legitimate Wien tail may return negative infinity (zero representable radiance).
        internal static bool TryLogWavelengthRadiance(double wavelengthMeters, double temperatureKelvin,
            out double logRadiance)
        {
            logRadiance = 0.0;
            if (!double.IsFinite(wavelengthMeters) || !(wavelengthMeters > 0.0)
                || !double.IsFinite(temperatureKelvin) || !(temperatureKelvin > 0.0))
            {
                return false;
            }

            double logWavelength = Math.Log(wavelengthMeters);
            double logX = LogSecondRadiationConstant - logWavelength - Math.Log(temperatureKelvin);
            double x = Math.Exp(logX);
            if (double.IsPositiveInfinity(x))
            {
                logRadiance = double.NegativeInfinity;
                return true;
            }

            double logDenominator;
            if (logX < Math.Log(1e-4))
            {
                // log(expm1(x)) = log(x) + x/2 + x^2/24 + O(x^4).
                // Retaining logX also handles x underflow without losing the RJ limit.
                logDenominator = logX + x * 0.5 + x * x / 24.0;
            }
            else if (x < 50.0)
            {
                logDenominator = Math.Log(Math.Exp(x) - 1.0);
            }
            else
            {
                // log(expm1(x)) = x + log(1-exp(-x)); omitted term < 2e-22.
                logDenominator = x;
            }

            logRadiance = LogNumerator - 5.0 * logWavelength - logDenominator;
            return true;
        }

        // Underflow is a physical negligible tail, not a visibility floor.
        // Overflow fails explicitly; use the log API rather than inventing an HDR clamp.
        internal static bool TryWavelengthRadiance(double wavelengthMeters, double temperatureKelvin,
            out double radiance)
        {
            radiance = 0.0;
            if (!TryLogWavelengthRadiance(wavelengthMeters, temperatureKelvin, out double logRadiance))
            {
                return false;
            }

            double value = Math.Exp(logRadiance);
            if (!double.IsFinite(value))
            {
                return false;
            }

            radiance = value;
            return true;
        }
    }
}

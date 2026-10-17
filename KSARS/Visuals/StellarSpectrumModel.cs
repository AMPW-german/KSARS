namespace KSARS.Visuals
{
    internal readonly record struct StellarEmission(double X, double Y, double Z, double LogFlux);

    internal sealed class StellarSpectrumModel
    {
        private readonly ThermalSpectrumTable _table;
        private readonly StellarTemperatureFit _fit;
        private readonly ThermalSample _thermalRest;
        private readonly double _restX, _restY, _restZ, _logRestLuminance;

        private StellarSpectrumModel(ThermalSpectrumTable table, StellarTemperatureFit fit,
            double x, double y, double z, double logRestLuminance)
        {
            _table = table;
            _fit = fit;
            _thermalRest = table.Evaluate(fit.TemperatureKelvin);
            _restX = x;
            _restY = y;
            _restZ = z;
            _logRestLuminance = logRestLuminance;
        }

        internal StellarTemperatureFit Fit => _fit;

        // Model thermal chromaticity and rest log luminance at the fitted
        // temperature; consumed by the GPU catalog builder for the tint-gain ratio.
        internal ThermalSample ThermalRest => _thermalRest;

        internal static bool TryCreate(ThermalSpectrumTable table, StellarTemperatureFitter fitter,
            double x, double y, double z, out StellarSpectrumModel? model)
        {
            model = null;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
                || x < 0.0 || !(y > 0.0) || z < 0.0)
                return false;
            if (!fitter.TryFit(x, y, z, out StellarTemperatureFit fit))
                return false;
            double scale = Math.Max(x, Math.Max(y, z));
            double nx = x / scale, ny = y / scale, nz = z / scale;
            double sum = nx + ny + nz;
            model = new StellarSpectrumModel(table, fit, nx / sum, ny / sum, nz / sum, Math.Log(y));
            return true;
        }

        // Unresolved point-source flux: thermal visible radiance ratio times D^2
        // relativistic beaming (photon energy and arrival rate each scale by D).
        // Absolute catalog scale/PSF calibration is a separate input, not inferred here.
        internal bool TryEvaluate(double doppler, out StellarEmission emission)
        {
            emission = default;
            if (!double.IsFinite(doppler) || !(doppler > 0.0))
                return false;
            if (doppler == 1.0)
            {
                emission = new StellarEmission(_restX, _restY, _restZ, _logRestLuminance);
                return true;
            }
            double temperature = _fit.TemperatureKelvin * doppler;
            if (!double.IsFinite(temperature) || !(temperature > 0.0))
                return false;
            ThermalSample shifted = _table.Evaluate(temperature);
            if (shifted.IsDark)
            {
                emission = new StellarEmission(0.0, 0.0, 0.0, double.NegativeInfinity);
                return true;
            }
            double logD = Math.Log(doppler);
            // Explicit off-locus prior: positive XYZ tint, smoothly fading to unity
            // as exp(-ln(D)^2). Each rest/model ratio is bounded by 1/model for
            // normalized input XYZ. Exact thermal colors have unity tint. This is not a
            // reconstruction of measured absorption lines or unobserved stellar spectra.
            double residualWeight = Math.Exp(-logD * logD);
            double x = shifted.X * ((1.0 - residualWeight) + residualWeight * (_restX / _thermalRest.X));
            double y = shifted.Y * ((1.0 - residualWeight) + residualWeight * (_restY / _thermalRest.Y));
            double z = shifted.Z * ((1.0 - residualWeight) + residualWeight * (_restZ / _thermalRest.Z));
            double sum = x + y + z;
            if (!(sum > 0.0) || !(y > 0.0))
                return false;
            emission = new StellarEmission(x / sum, y / sum, z / sum,
                _logRestLuminance + shifted.LogLuminance - _thermalRest.LogLuminance + 2.0 * logD);
            return true;
        }
    }
}

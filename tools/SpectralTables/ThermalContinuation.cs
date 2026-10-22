using KSARS.Visuals;

namespace KSARS.SpectralTables
{
    internal sealed class ThermalContinuation
    {
        private readonly double[] _coefficients;
        private ThermalContinuation(double[] coefficients) => _coefficients = coefficients;
        internal double Coefficient(int index) => _coefficients[index];

        internal static ThermalContinuation Generate(Cie1931Observer observer)
        {
            // At T >= T0, x=hc/(lambda*k*T) <= 0.0004 over the entire observer grid.
            // B/RJ = 1 - x/2 + x^2/12 + O(x^4), with |x^4/720| < 4e-17.
            var coefficients = new double[9];
            for (int i = 0; i < Cie1931Observer.SampleCount; i++)
            {
                double wavelength = (Cie1931Observer.FirstNanometer + i) * 1e-9;
                double x = PlanckSpectrum.PlanckConstant * PhysicsConstants.SpeedOfLight
                    / (wavelength * PlanckSpectrum.BoltzmannConstant * ThermalTable.MaximumKelvin);
                double weight = (i == 0 || i == Cie1931Observer.SampleCount - 1) ? 0.5 : 1.0;
                double rj = 2.0 * PhysicsConstants.SpeedOfLight * PlanckSpectrum.BoltzmannConstant
                    * ThermalTable.MaximumKelvin / Math.Pow(wavelength, 4.0)
                    * weight * 1e-9 * Cie1931Observer.PhotopicScale;
                var sample = observer.Sample(i);
                double[] matching = { sample.X, sample.Y, sample.Z };
                for (int channel = 0; channel < 3; channel++)
                {
                    double value = rj * matching[channel];
                    coefficients[channel] += value;
                    coefficients[3 + channel] -= value * x * 0.5;
                    coefficients[6 + channel] += value * x * x / 12.0;
                }
            }
            return new ThermalContinuation(coefficients);
        }

        internal ThermalColor Evaluate(double temperatureKelvin)
        {
            if (!double.IsFinite(temperatureKelvin) || temperatureKelvin < ThermalTable.MaximumKelvin)
                throw new ArgumentOutOfRangeException(nameof(temperatureKelvin));
            double ratio = ThermalTable.MaximumKelvin / temperatureKelvin;
            double x = Channel(0, ratio), y = Channel(1, ratio), z = Channel(2, ratio);
            double total = x + y + z;
            // Evaluate the growth in log space even when T/T0 or luminance would overflow.
            return new ThermalColor(x / total, y / total, z / total,
                Math.Log(y) + Math.Log(temperatureKelvin) - Math.Log(ThermalTable.MaximumKelvin));
        }

        private double Channel(int index, double ratio) =>
            _coefficients[index] + ratio * (_coefficients[3 + index] + ratio * _coefficients[6 + index]);
    }
}

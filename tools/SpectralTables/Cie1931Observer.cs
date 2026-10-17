using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KSARS.Visuals;

namespace KSARS.SpectralTables
{
    internal readonly record struct ThermalColor(double X, double Y, double Z, double LogLuminance);

    internal sealed class Cie1931Observer
    {
        internal const string SourceSha256 = "fa663e3535a7e0763a745993a1f0a192eb0275ac46ad2d1befd7626841e713c1";
        internal const int FirstNanometer = 360;
        internal const int SampleCount = 471;
        internal const double PhotopicScale = 683.0; // Conventional lm/W normalization.
        private readonly (double X, double Y, double Z)[] _samples;

        private Cie1931Observer((double X, double Y, double Z)[] samples) => _samples = samples;

        internal static Cie1931Observer Load(string path)
        {
            using var stream = File.OpenRead(path);
            return Load(stream);
        }

        internal static Cie1931Observer Load(Stream stream)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            byte[] bytes = copy.ToArray();
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CIE source checksum does not match the published original.");

            using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
            var samples = new (double X, double Y, double Z)[SampleCount];
            for (int i = 0; i < SampleCount; i++)
            {
                string[] columns = (reader.ReadLine() ?? "").Split(',');
                if (columns.Length != 4 || columns[0] != (FirstNanometer + i).ToString(CultureInfo.InvariantCulture))
                    throw new InvalidDataException("CIE source wavelength grid is not 360..830 nm at 1 nm.");
                double[] values = columns.Skip(1).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                if (values.Any(v => !double.IsFinite(v) || v < 0.0))
                    throw new InvalidDataException("CIE matching functions must be finite and nonnegative.");
                samples[i] = (values[0], values[1], values[2]);
            }
            if (reader.ReadLine() is not null)
                throw new InvalidDataException("Unexpected CIE source rows.");
            return new Cie1931Observer(samples);
        }

        internal (double X, double Y, double Z) Sample(int index) => _samples[index];

        // Trapezoidal integration at the published 1 nm grid, zero outside 360..830 nm.
        // XYZ is normalized by X+Y+Z; log luminance is natural log(cd/m^2), using 683 lm/W.
        // This is an offline reference, not native-game radiance calibration.
        internal ThermalColor Evaluate(double temperatureKelvin)
        {
            if (!double.IsFinite(temperatureKelvin) || !(temperatureKelvin > 0.0))
                throw new ArgumentOutOfRangeException(nameof(temperatureKelvin));
            Span<double> logs = stackalloc double[SampleCount];
            double peak = double.NegativeInfinity;
            for (int i = 0; i < SampleCount; i++)
            {
                if (!PlanckSpectrum.TryLogWavelengthRadiance((FirstNanometer + i) * 1e-9,
                        temperatureKelvin, out logs[i]))
                    throw new ArgumentOutOfRangeException(nameof(temperatureKelvin));
                peak = Math.Max(peak, logs[i]);
            }
            if (!double.IsFinite(peak))
                throw new ArgumentOutOfRangeException(nameof(temperatureKelvin), "Spectrum exceeds log-domain resolution.");

            double x = 0.0, y = 0.0, z = 0.0;
            for (int i = 0; i < SampleCount; i++)
            {
                double weight = (i == 0 || i == SampleCount - 1) ? 0.5 : 1.0;
                double scaled = Math.Exp(logs[i] - peak) * weight;
                x += scaled * _samples[i].X;
                y += scaled * _samples[i].Y;
                z += scaled * _samples[i].Z;
            }
            double total = x + y + z;
            // Per-metre spectral radiance requires a 1e-9 metre integration step.
            return new ThermalColor(x / total, y / total, z / total,
                peak + Math.Log(y) + Math.Log(1e-9 * PhotopicScale));
        }
    }
}

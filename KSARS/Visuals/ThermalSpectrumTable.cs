using System.Security.Cryptography;
using System.Text;

namespace KSARS.Visuals
{
    internal readonly record struct ThermalSample(double X, double Y, double Z, double LogLuminance)
    {
        internal bool IsDark => double.IsNegativeInfinity(LogLuminance);
    }

    internal sealed class ThermalSpectrumTable
    {
        internal const string ResourceName = "KSARS.ThermalTable.v2.bin";
        internal const string ArtifactSha256 = "396CA9CAC5004A5F43F680C553FD5F3160920A81FA09489BB86192D077A18B4A";
        private const string SourceSha256 = "FA663E3535A7E0763A745993A1F0A192EB0275AC46AD2D1BEFD7626841E713C1";
        private const int Count = 1024;
        private const double MaximumKelvin = 1e8;
        private static readonly double LogRange = Math.Log(MaximumKelvin);
        private readonly ThermalSample[] _entries;
        private readonly double[] _continuation;

        private ThermalSpectrumTable(ThermalSample[] entries, double[] continuation)
        {
            _entries = entries;
            _continuation = continuation;
        }

        internal static ThermalSpectrumTable LoadEmbedded()
        {
            using Stream source = typeof(ThermalSpectrumTable).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidDataException("Embedded thermal spectrum table is missing.");
            return Read(source);
        }

        internal static ThermalSpectrumTable Read(Stream source)
        {
            byte[] bytes = new byte[32904];
            source.ReadExactly(bytes);
            if (source.ReadByte() != -1)
                throw new InvalidDataException("Thermal spectrum table has trailing data.");
            if (Convert.ToHexString(SHA256.HashData(bytes)) != ArtifactSha256)
                throw new InvalidDataException("Thermal spectrum table checksum mismatch.");
            using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "KSARSTH2"
                || reader.ReadInt32() != 2 || reader.ReadInt32() != Count
                || reader.ReadDouble() != 1.0 || reader.ReadDouble() != MaximumKelvin
                || Convert.ToHexString(reader.ReadBytes(32)) != SourceSha256)
                throw new InvalidDataException("Unsupported thermal spectrum table header.");
            var entries = new ThermalSample[Count];
            double previous = double.NegativeInfinity;
            for (int i = 0; i < Count; i++)
            {
                var entry = new ThermalSample(reader.ReadDouble(), reader.ReadDouble(),
                    reader.ReadDouble(), reader.ReadDouble());
                if (!double.IsFinite(entry.X) || !double.IsFinite(entry.Y) || !double.IsFinite(entry.Z)
                    || entry.X < 0.0 || entry.Y <= 0.0 || entry.Z < 0.0
                    || Math.Abs(entry.X + entry.Y + entry.Z - 1.0) > 1e-10
                    || !double.IsFinite(entry.LogLuminance) || entry.LogLuminance <= previous)
                    throw new InvalidDataException("Invalid thermal spectrum table record.");
                previous = entry.LogLuminance;
                entries[i] = entry;
            }
            var continuation = new double[9];
            for (int i = 0; i < continuation.Length; i++)
            {
                continuation[i] = reader.ReadDouble();
                if (!double.IsFinite(continuation[i])
                    || (i < 3 && continuation[i] <= 0.0)
                    || (i >= 3 && i < 6 && continuation[i] > 0.0)
                    || (i >= 6 && continuation[i] < 0.0))
                    throw new InvalidDataException("Invalid thermal continuation coefficient.");
            }
            return new ThermalSpectrumTable(entries, continuation);
        }

        internal ThermalSample Evaluate(double temperatureKelvin)
        {
            if (!double.IsFinite(temperatureKelvin) || !(temperatureKelvin > 0.0))
                throw new ArgumentOutOfRangeException(nameof(temperatureKelvin));
            // Even at 1 K the entire visible band is far below binary64 underflow.
            if (temperatureKelvin < 1.0)
                return new ThermalSample(0.0, 0.0, 0.0, double.NegativeInfinity);
            if (temperatureKelvin > MaximumKelvin)
            {
                double ratio = MaximumKelvin / temperatureKelvin;
                double x = HotChannel(0, ratio), y = HotChannel(1, ratio), z = HotChannel(2, ratio);
                double total = x + y + z;
                return new ThermalSample(x / total, y / total, z / total,
                    Math.Log(y) + Math.Log(temperatureKelvin) - LogRange);
            }
            double coordinate = Math.Log(temperatureKelvin) / LogRange * (Count - 1);
            int lower = Math.Min((int)coordinate, Count - 2);
            double fraction = coordinate - lower;
            ThermalSample a = _entries[lower], b = _entries[lower + 1];
            return new ThermalSample(a.X + (b.X - a.X) * fraction,
                a.Y + (b.Y - a.Y) * fraction, a.Z + (b.Z - a.Z) * fraction,
                a.LogLuminance + (b.LogLuminance - a.LogLuminance) * fraction);
        }

        private double HotChannel(int index, double ratio) =>
            _continuation[index] + ratio * (_continuation[index + 3] + ratio * _continuation[index + 6]);

        // std430 rows for RelativisticStarShaders: 1024 vec4 samples
        // (xyz chromaticity, w log luminance) followed by 3 vec4 continuation
        // coefficient rows packed per polynomial order across channels.
        internal const int GpuRows = Count + 3;

        internal void WriteGpuTable(Span<Brutal.Numerics.float4> destination)
        {
            if (destination.Length < GpuRows)
                throw new ArgumentOutOfRangeException(nameof(destination));
            for (int i = 0; i < Count; i++)
            {
                ThermalSample sample = _entries[i];
                destination[i] = new Brutal.Numerics.float4((float)sample.X, (float)sample.Y,
                    (float)sample.Z, (float)sample.LogLuminance);
            }

            for (int order = 0; order < 3; order++)
            {
                destination[Count + order] = new Brutal.Numerics.float4(
                    (float)_continuation[order], (float)_continuation[order + 3],
                    (float)_continuation[order + 6], 0.0f);
            }
        }
    }
}

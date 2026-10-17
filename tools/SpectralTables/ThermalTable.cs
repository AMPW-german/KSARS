using System.Text;

namespace KSARS.SpectralTables
{
    internal sealed class ThermalTable
    {
        internal const int Count = 1024;
        internal const double MinimumKelvin = 1.0;
        internal const double MaximumKelvin = 1e8;
        private static readonly double LogRange = Math.Log(MaximumKelvin / MinimumKelvin);
        private readonly ThermalColor[] _entries;
        private readonly ThermalContinuation _continuation;

        private ThermalTable(ThermalColor[] entries, ThermalContinuation continuation)
        {
            _entries = entries;
            _continuation = continuation;
        }

        internal static double TemperatureAt(int index)
        {
            if ((uint)index >= Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (index == 0) return MinimumKelvin;
            if (index == Count - 1) return MaximumKelvin;
            return Math.Exp(Math.Log(MinimumKelvin) + LogRange * index / (Count - 1));
        }

        internal static ThermalTable Generate(Cie1931Observer observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            var entries = new ThermalColor[Count];
            for (int i = 0; i < Count; i++)
                entries[i] = observer.Evaluate(TemperatureAt(i));
            return new ThermalTable(entries, ThermalContinuation.Generate(observer));
        }

        internal ThermalColor Entry(int index) => _entries[index];

        // Offline reference; above the grid use the integrated Rayleigh-Jeans expansion.
        internal ThermalColor Interpolate(double temperatureKelvin)
        {
            if (!double.IsFinite(temperatureKelvin)
                || temperatureKelvin < MinimumKelvin)
                throw new ArgumentOutOfRangeException(nameof(temperatureKelvin));
            if (temperatureKelvin > MaximumKelvin)
                return _continuation.Evaluate(temperatureKelvin);
            double coordinate = Math.Log(temperatureKelvin / MinimumKelvin) / LogRange * (Count - 1);
            int lower = Math.Min((int)coordinate, Count - 2);
            double fraction = coordinate - lower;
            ThermalColor a = _entries[lower], b = _entries[lower + 1];
            return new ThermalColor(
                a.X + (b.X - a.X) * fraction,
                a.Y + (b.Y - a.Y) * fraction,
                a.Z + (b.Z - a.Z) * fraction,
                a.LogLuminance + (b.LogLuminance - a.LogLuminance) * fraction);
        }

        // v2: v1's 64-byte header and 1024 records, then 9 binary64 continuation coefficients.
        // Attribution and CC BY-SA 4.0 obligations accompany this CIE-derived data.
        internal void Write(Stream output)
        {
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("KSARSTH2"));
            writer.Write(2);
            writer.Write(Count);
            writer.Write(MinimumKelvin);
            writer.Write(MaximumKelvin);
            writer.Write(Convert.FromHexString(Cie1931Observer.SourceSha256));
            foreach (ThermalColor entry in _entries)
            {
                writer.Write(entry.X);
                writer.Write(entry.Y);
                writer.Write(entry.Z);
                writer.Write(entry.LogLuminance);
            }
            for (int i = 0; i < 9; i++)
                writer.Write(_continuation.Coefficient(i));
        }
    }
}

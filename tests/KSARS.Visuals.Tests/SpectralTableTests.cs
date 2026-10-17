extern alias spectraltool;

using System.Globalization;
using System.Text;
using Cie1931Observer = spectraltool::KSARS.SpectralTables.Cie1931Observer;
using ThermalColor = spectraltool::KSARS.SpectralTables.ThermalColor;
using ThermalTable = spectraltool::KSARS.SpectralTables.ThermalTable;
using ThermalContinuation = spectraltool::KSARS.SpectralTables.ThermalContinuation;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class SpectralTableTests
    {
        private static byte[] SourceBytes()
        {
            using Stream source = typeof(SpectralTableTests).Assembly.GetManifestResourceStream("KSARS.Tests.Cie1931.csv")!;
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            return copy.ToArray();
        }

        private static Cie1931Observer Observer()
        {
            using var source = new MemoryStream(SourceBytes());
            return Cie1931Observer.Load(source);
        }

        [Fact]
        public void OriginalData_MatchesPublishedGridColumnSumsAndSample()
        {
            Cie1931Observer observer = Observer();
            Assert.Equal(24021, SourceBytes().Length);
            double x = 0.0, y = 0.0, z = 0.0;
            for (int i = 0; i < 471; i++)
            {
                var sample = observer.Sample(i);
                x += sample.X;
                y += sample.Y;
                z += sample.Z;
            }
            // CIE metadata validations, not numbers fitted from generated output.
            TestOptics.AssertRel(106.865469489595, x, 1e-12);
            TestOptics.AssertRel(106.8569171011719, y, 1e-12);
            TestOptics.AssertRel(106.892251278636, z, 1e-12);
            Assert.Equal((0.1042979, 0.1334528, 0.8566193), observer.Sample(479 - 360));
        }

        [Fact]
        public void ModifiedOrTruncatedSource_IsRejectedBeforeUse()
        {
            byte[] bytes = SourceBytes();
            bytes[0] ^= 1;
            Assert.Throws<InvalidDataException>(() => Cie1931Observer.Load(new MemoryStream(bytes)));
            Assert.Throws<InvalidDataException>(() => Cie1931Observer.Load(new MemoryStream(SourceBytes()[..^1])));
        }

        [Fact]
        public void SourceParsing_IsInvariantUnderCommaDecimalCulture()
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Assert.Equal(Observer().Sample(0), (0.0001299, 0.000003917, 0.0006061));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        private static ThermalColor DirectPlanckIntegral(Cie1931Observer observer, double temperature)
        {
            const double h = 6.62607015e-34, c = 299792458.0, k = 1.380649e-23;
            double x = 0.0, y = 0.0, z = 0.0;
            for (int i = 0; i < 471; i++)
            {
                double wavelength = (360 + i) * 1e-9;
                double radiance = 2.0 * h * c * c / (Math.Pow(wavelength, 5.0)
                    * (Math.Exp(h * c / (wavelength * k * temperature)) - 1.0));
                double weight = (i == 0 || i == 470) ? 0.5 : 1.0;
                var sample = observer.Sample(i);
                x += radiance * sample.X * weight * 1e-9;
                y += radiance * sample.Y * weight * 1e-9;
                z += radiance * sample.Z * weight * 1e-9;
            }
            double total = x + y + z;
            return new ThermalColor(x / total, y / total, z / total, Math.Log(y * 683.0));
        }

        [Theory]
        [InlineData(800.0)]
        [InlineData(5772.0)]
        [InlineData(100000.0)]
        [InlineData(1e8)]
        public void ScaledLogIntegration_MatchesDirectSIIntegral(double temperature)
        {
            Cie1931Observer observer = Observer();
            ThermalColor expected = DirectPlanckIntegral(observer, temperature);
            ThermalColor actual = observer.Evaluate(temperature);
            Assert.InRange(Math.Abs(actual.X - expected.X), 0.0, 2e-10);
            Assert.InRange(Math.Abs(actual.Y - expected.Y), 0.0, 2e-10);
            Assert.InRange(Math.Abs(actual.Z - expected.Z), 0.0, 2e-10);
            Assert.InRange(Math.Abs(actual.LogLuminance - expected.LogLuminance), 0.0, 2e-9);
        }

        [Fact]
        public void SolarTemperature_HasExpectedPlanckianChromaticity()
        {
            ThermalColor color = Observer().Evaluate(5772.0);
            Assert.InRange(color.X, 0.325, 0.328);
            Assert.InRange(color.Y, 0.334, 0.338);
        }

        [Fact]
        public void ColdVisibleTail_RetainsFiniteLogLuminanceWithoutBrightnessFloor()
        {
            Cie1931Observer observer = Observer();
            ThermalColor cold = observer.Evaluate(1.0);
            ThermalColor cmb = observer.Evaluate(2.7255);
            Assert.True(double.IsFinite(cold.LogLuminance));
            Assert.True(cold.LogLuminance < -10000.0);
            Assert.True(cmb.LogLuminance > cold.LogLuminance);
            Assert.Equal(0.0, Math.Exp(cmb.LogLuminance));
            Assert.InRange(Math.Abs(cold.X + cold.Y + cold.Z - 1.0), 0.0, 1e-14);
        }

        [Fact]
        public void GeneratedGrid_IsNormalizedFiniteAndBrighterWithTemperature()
        {
            ThermalTable table = ThermalTable.Generate(Observer());
            Assert.Equal(1.0, ThermalTable.TemperatureAt(0));
            Assert.Equal(1e8, ThermalTable.TemperatureAt(1023));
            double last = double.NegativeInfinity;
            for (int i = 0; i < ThermalTable.Count; i++)
            {
                ThermalColor color = table.Entry(i);
                Assert.InRange(color.X, 0.0, 1.0);
                Assert.InRange(color.Y, 0.0, 1.0);
                Assert.InRange(color.Z, 0.0, 1.0);
                Assert.InRange(Math.Abs(color.X + color.Y + color.Z - 1.0), 0.0, 1e-14);
                Assert.True(double.IsFinite(color.LogLuminance));
                Assert.True(color.LogLuminance > last);
                last = color.LogLuminance;
            }
        }

        [Fact]
        public void InterpolatedVisibleTemperatures_MatchReferenceAtSampledLogMidpoints()
        {
            Cie1931Observer observer = Observer();
            ThermalTable table = ThermalTable.Generate(observer);
            for (int i = 0; i < ThermalTable.Count - 1; i += 8)
            {
                double temperature = Math.Sqrt(ThermalTable.TemperatureAt(i) * ThermalTable.TemperatureAt(i + 1));
                if (temperature < 800.0) continue;
                ThermalColor actual = table.Interpolate(temperature);
                ThermalColor expected = observer.Evaluate(temperature);
                Assert.InRange(Math.Abs(actual.X - expected.X), 0.0, 1e-4);
                Assert.InRange(Math.Abs(actual.Y - expected.Y), 0.0, 1e-4);
                Assert.InRange(Math.Abs(actual.Z - expected.Z), 0.0, 1e-4);
                Assert.InRange(Math.Abs(actual.LogLuminance - expected.LogLuminance), 0.0, 0.004);
            }
        }

        [Fact]
        public void BinaryOutput_IsRepeatableAndSelfDescribesEveryRecord()
        {
            Cie1931Observer observer = Observer();
            ThermalTable first = ThermalTable.Generate(observer);
            using var a = new MemoryStream();
            using var b = new MemoryStream();
            first.Write(a);
            ThermalTable.Generate(observer).Write(b);
            Assert.Equal(a.ToArray(), b.ToArray());
            Assert.Equal(32904, a.Length);
            a.Position = 0;
            using var reader = new BinaryReader(a, Encoding.UTF8, leaveOpen: true);
            Assert.Equal("KSARSTH2", Encoding.ASCII.GetString(reader.ReadBytes(8)));
            Assert.Equal(2, reader.ReadInt32());
            Assert.Equal(1024, reader.ReadInt32());
            Assert.Equal(1.0, reader.ReadDouble());
            Assert.Equal(1e8, reader.ReadDouble());
            Assert.Equal(Convert.FromHexString(Cie1931Observer.SourceSha256), reader.ReadBytes(32));
            for (int i = 0; i < 1024; i++)
            {
                var read = new ThermalColor(reader.ReadDouble(), reader.ReadDouble(),
                    reader.ReadDouble(), reader.ReadDouble());
                Assert.Equal(first.Entry(i), read);
            }
            ThermalContinuation continuation = ThermalContinuation.Generate(observer);
            for (int i = 0; i < 9; i++)
                Assert.Equal(continuation.Coefficient(i), reader.ReadDouble());
            Assert.Equal(a.Length, a.Position);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void InvalidTemperature_IsRejected(double temperature) =>
            Assert.Throws<ArgumentOutOfRangeException>(() => Observer().Evaluate(temperature));

        [Theory]
        [InlineData(0.5)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NaN)]
        public void TableInterpolation_RejectsOutOfDomainRatherThanClamping(double temperature) =>
            Assert.Throws<ArgumentOutOfRangeException>(() => ThermalTable.Generate(Observer()).Interpolate(temperature));

        [Theory]
        [InlineData(1e8)]
        [InlineData(2.73357808e8)]
        [InlineData(1e12)]
        [InlineData(1e20)]
        [InlineData(double.MaxValue)]
        public void HighTemperatureContinuation_MatchesLogPlanckReference(double temperature)
        {
            Cie1931Observer observer = Observer();
            ThermalColor actual = ThermalContinuation.Generate(observer).Evaluate(temperature);
            ThermalColor expected = observer.Evaluate(temperature);
            Assert.InRange(Math.Abs(actual.X - expected.X), 0.0, 2e-12);
            Assert.InRange(Math.Abs(actual.Y - expected.Y), 0.0, 2e-12);
            Assert.InRange(Math.Abs(actual.Z - expected.Z), 0.0, 2e-12);
            Assert.InRange(Math.Abs(actual.LogLuminance - expected.LogLuminance), 0.0, 2e-12);
        }

        [Fact]
        public void HighTemperatureContinuation_IsContinuousAtTableBoundary()
        {
            ThermalTable table = ThermalTable.Generate(Observer());
            ThermalColor boundary = table.Entry(ThermalTable.Count - 1);
            ThermalColor above = table.Interpolate(Math.BitIncrement(ThermalTable.MaximumKelvin));
            Assert.InRange(Math.Abs(above.X - boundary.X), 0.0, 2e-12);
            Assert.InRange(Math.Abs(above.Y - boundary.Y), 0.0, 2e-12);
            Assert.InRange(Math.Abs(above.Z - boundary.Z), 0.0, 2e-12);
            Assert.InRange(Math.Abs(above.LogLuminance - boundary.LogLuminance), 0.0, 2e-12);
        }
    }
}

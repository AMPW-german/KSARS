extern alias spectraltool;

using KSARS.Visuals;
using Cie1931Observer = spectraltool::KSARS.SpectralTables.Cie1931Observer;
using OfflineThermalTable = spectraltool::KSARS.SpectralTables.ThermalTable;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class RuntimeSpectrumTests
    {
        private static StellarTemperatureFitter Fitter(ThermalSpectrumTable table) => new(t =>
        {
            ThermalSample sample = table.Evaluate(t);
            return (sample.X, sample.Y, sample.Z);
        });

        private static byte[] Bytes()
        {
            using Stream stream = typeof(ThermalSpectrumTable).Assembly
                .GetManifestResourceStream(ThermalSpectrumTable.ResourceName)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        [Fact]
        public void EmbeddedTable_RuntimeReaderMatchesOfflineReference()
        {
            using Stream source = typeof(RuntimeSpectrumTests).Assembly
                .GetManifestResourceStream("KSARS.Tests.Cie1931.csv")!;
            var observer = Cie1931Observer.Load(source);
            var generated = OfflineThermalTable.Generate(observer);
            var runtime = ThermalSpectrumTable.LoadEmbedded();
            foreach (double temperature in new[] { 1.0, 2.7255, 800.0, 5772.0, 1e8, 2.73357808e8, 1e20 })
            {
                var expected = generated.Interpolate(temperature);
                var actual = runtime.Evaluate(temperature);
                Assert.InRange(Math.Abs(actual.X - expected.X), 0.0, 1e-14);
                Assert.InRange(Math.Abs(actual.Y - expected.Y), 0.0, 1e-14);
                Assert.InRange(Math.Abs(actual.Z - expected.Z), 0.0, 1e-14);
                Assert.InRange(Math.Abs(actual.LogLuminance - expected.LogLuminance), 0.0, 1e-12);
            }
        }

        [Fact]
        public void RuntimeReader_RejectsTruncatedModifiedOrTrailingData()
        {
            byte[] bytes = Bytes();
            Assert.Throws<EndOfStreamException>(() => ThermalSpectrumTable.Read(new MemoryStream(bytes[..^1])));
            byte[] modified = (byte[])bytes.Clone();
            modified[100] ^= 1;
            Assert.Throws<InvalidDataException>(() => ThermalSpectrumTable.Read(new MemoryStream(modified)));
            Assert.Throws<InvalidDataException>(() =>
                ThermalSpectrumTable.Read(new MemoryStream(bytes.Concat(new byte[] { 0 }).ToArray())));
        }

        [Fact]
        public void PositiveSubKelvinTemperatures_AreNumericallyDarkNotClampedToVisibleEmission()
        {
            var table = ThermalSpectrumTable.LoadEmbedded();
            Assert.True(table.Evaluate(0.5).IsDark);
            Assert.True(table.Evaluate(double.Epsilon).IsDark);
            Assert.Equal(0.0, Math.Exp(table.Evaluate(1.0).LogLuminance));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Evaluate(0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Evaluate(double.PositiveInfinity));
        }

        [Fact]
        public void OffLocus_D1PreservesOriginalChromaticityAndLuminance()
        {
            var table = ThermalSpectrumTable.LoadEmbedded();
            Assert.True(StellarSpectrumModel.TryCreate(table, Fitter(table), 0.2, 0.7, 0.1, out var model));
            Assert.True(model!.TryEvaluate(1.0, out var emission));
            Assert.InRange(Math.Abs(emission.X - 0.2), 0.0, 2e-16);
            Assert.InRange(Math.Abs(emission.Y - 0.7), 0.0, 2e-16);
            Assert.InRange(Math.Abs(emission.Z - 0.1), 0.0, 2e-16);
            Assert.Equal(Math.Log(0.7), emission.LogFlux);
        }

        [Theory]
        [InlineData(0.1)]
        [InlineData(2.0)]
        [InlineData(1000.0)]
        [InlineData(1e7)]
        public void ExactThermalSource_ShiftsTemperatureAndAppliesPointSourceBeaming(double doppler)
        {
            var table = ThermalSpectrumTable.LoadEmbedded();
            var rest = table.Evaluate(5772.0);
            // Absolute amplitude is deliberately arbitrary and independent of the thermal temperature.
            Assert.True(StellarSpectrumModel.TryCreate(table, Fitter(table),
                rest.X / rest.Y * 2.0, 2.0, rest.Z / rest.Y * 2.0, out var model));
            double fitTemperature = model!.Fit.TemperatureKelvin;
            Assert.InRange(Math.Abs(fitTemperature / 5772.0 - 1.0), 0.0, 1e-7);
            Assert.True(model.TryEvaluate(doppler, out var actual));
            var shifted = table.Evaluate(fitTemperature * doppler);
            var fittedRest = table.Evaluate(fitTemperature);
            Assert.InRange(Math.Abs(actual.X - shifted.X), 0.0, 1e-8);
            Assert.InRange(Math.Abs(actual.Y - shifted.Y), 0.0, 1e-8);
            Assert.InRange(Math.Abs(actual.Z - shifted.Z), 0.0, 1e-8);
            double expectedLogFlux = Math.Log(2.0) + shifted.LogLuminance
                - fittedRest.LogLuminance + 2.0 * Math.Log(doppler);
            Assert.InRange(Math.Abs(actual.LogFlux - expectedLogFlux), 0.0, 1e-12);
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(0.999999)]
        [InlineData(1.000001)]
        [InlineData(2.0)]
        [InlineData(1e5)]
        public void OffLocusTint_RemainsNonnegativeAndNormalizedWithoutFluxGain(double doppler)
        {
            var table = ThermalSpectrumTable.LoadEmbedded();
            Assert.True(StellarSpectrumModel.TryCreate(table, Fitter(table), 0.998, 0.001, 0.001, out var model));
            Assert.True(model!.TryEvaluate(doppler, out var emission));
            Assert.InRange(emission.X, 0.0, 1.0);
            Assert.InRange(emission.Y, double.Epsilon, 1.0);
            Assert.InRange(emission.Z, 0.0, 1.0);
            Assert.InRange(Math.Abs(emission.X + emission.Y + emission.Z - 1.0), 0.0, 1e-14);
            double expectedLogFlux = Math.Log(0.001)
                + table.Evaluate(model.Fit.TemperatureKelvin * doppler).LogLuminance
                - table.Evaluate(model.Fit.TemperatureKelvin).LogLuminance + 2.0 * Math.Log(doppler);
            Assert.InRange(Math.Abs(emission.LogFlux - expectedLogFlux), 0.0, 1e-12);
        }

        [Fact]
        public void VeryRedshiftedSource_HasNoVisibilityFloor()
        {
            var table = ThermalSpectrumTable.LoadEmbedded();
            Assert.True(StellarSpectrumModel.TryCreate(table, Fitter(table), 0.2, 0.7, 0.1, out var model));
            Assert.True(model!.TryEvaluate(1e-8, out var emission));
            Assert.Equal(double.NegativeInfinity, emission.LogFlux);
            Assert.Equal(0.0, Math.Exp(emission.LogFlux));
            Assert.False(model.TryEvaluate(0.0, out _));
            Assert.False(model.TryEvaluate(double.NaN, out _));
        }

        [Fact]
        public void InvalidOrNonluminousInput_DoesNotInventSpectrum()
        {
            var table = ThermalSpectrumTable.LoadEmbedded();
            var fitter = Fitter(table);
            Assert.False(StellarSpectrumModel.TryCreate(table, fitter, 0.0, 0.0, 0.0, out _));
            Assert.False(StellarSpectrumModel.TryCreate(table, fitter, 1.0, 0.0, 1.0, out _));
            Assert.False(StellarSpectrumModel.TryCreate(table, fitter, double.NaN, 1.0, 1.0, out _));
            Assert.False(StellarSpectrumModel.TryCreate(table, fitter, -1.0, 1.0, 1.0, out _));
        }

        [Theory]
        [InlineData(1.0, 0.0, 0.0)]
        [InlineData(0.0, 1.0, 0.0)]
        [InlineData(0.0, 0.0, 1.0)]
        [InlineData(0.2, 0.5, 0.9)]
        [InlineData(1000.0, 1000.0, 1000.0)]
        public void DeclaredLinearWorkingSpace_RoundTripsWithoutTransferFunction(double r, double g, double b)
        {
            Assert.True(SpectralColorSpace.TryLinearRgbToXyz(r, g, b, out var xyz));
            Assert.True(SpectralColorSpace.TryXyzToDisplayRgb(xyz.X, xyz.Y, xyz.Z, out var rgb));
            Assert.InRange(Math.Abs(rgb.R - r), 0.0, 1e-10);
            Assert.InRange(Math.Abs(rgb.G - g), 0.0, 1e-10);
            Assert.InRange(Math.Abs(rgb.B - b), 0.0, 1e-10);
        }

        [Theory]
        [InlineData(0.95, 0.025, 0.025)]
        [InlineData(0.025, 0.95, 0.025)]
        [InlineData(0.025, 0.025, 0.95)]
        public void DisplayGamutMapping_IsNonnegativeAndPreservesLuminance(double x, double y, double z)
        {
            Assert.True(SpectralColorSpace.TryXyzToDisplayRgb(x, y, z, out var rgb));
            Assert.True(rgb.R >= 0.0 && rgb.G >= 0.0 && rgb.B >= 0.0);
            Assert.True(SpectralColorSpace.TryLinearRgbToXyz(rgb.R, rgb.G, rgb.B, out var reconstructed));
            Assert.InRange(Math.Abs(reconstructed.Y - y), 0.0, 1e-12);
        }
    }
}

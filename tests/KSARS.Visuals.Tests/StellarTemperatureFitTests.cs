extern alias spectraltool;

using Cie1931Observer = spectraltool::KSARS.SpectralTables.Cie1931Observer;
using StellarTemperatureFitter = spectraltool::KSARS.Visuals.StellarTemperatureFitter;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class StellarTemperatureFitTests
    {
        private static Cie1931Observer Observer()
        {
            using Stream source = typeof(StellarTemperatureFitTests).Assembly
                .GetManifestResourceStream("KSARS.Tests.Cie1931.csv")!;
            return Cie1931Observer.Load(source);
        }

        private static StellarTemperatureFitter Fitter(Cie1931Observer observer) =>
            new(t =>
            {
                var c = observer.Evaluate(t);
                return (c.X, c.Y, c.Z);
            });

        [Theory]
        [InlineData(800.0)]
        [InlineData(1000.0)]
        [InlineData(3000.0)]
        [InlineData(5772.0)]
        [InlineData(20000.0)]
        [InlineData(100000.0)]
        public void ExactPlanckChromaticity_RecoversTemperature(double temperature)
        {
            var observer = Observer();
            var fitter = Fitter(observer);
            var color = observer.Evaluate(temperature);
            Assert.True(fitter.TryFit(color.X, color.Y, color.Z, out var fit));
            Assert.InRange(Math.Abs(fit.TemperatureKelvin / temperature - 1.0), 0.0, 1e-7);
            Assert.InRange(Math.Abs(fit.ResidualU), 0.0, 1e-10);
            Assert.InRange(Math.Abs(fit.ResidualV), 0.0, 1e-10);
        }

        [Theory]
        [InlineData(1e-200)]
        [InlineData(0.01)]
        [InlineData(100.0)]
        [InlineData(1e200)]
        public void Fit_IsIndependentOfBrightnessScale(double scale)
        {
            var observer = Observer();
            var fitter = Fitter(observer);
            var color = observer.Evaluate(5772.0);
            Assert.True(fitter.TryFit(color.X * scale, color.Y * scale, color.Z * scale, out var fit));
            Assert.InRange(Math.Abs(fit.TemperatureKelvin / 5772.0 - 1.0), 0.0, 1e-7);
        }

        [Fact]
        public void OffLocusColor_IsDeterministicAndRetainsExplicitResidual()
        {
            var observer = Observer();
            var fitter = Fitter(observer);
            Assert.True(fitter.TryFit(0.1, 0.8, 0.1, out var first));
            Assert.True(fitter.TryFit(0.1, 0.8, 0.1, out var second));
            Assert.Equal(first, second);
            Assert.True(first.ResidualU * first.ResidualU + first.ResidualV * first.ResidualV > 1e-4);
            Assert.InRange(first.TemperatureKelvin, 800.0, 100000.0);
            // Reconstruct the supplied chromaticity, not an invented on-locus replacement.
            var model = observer.Evaluate(first.TemperatureKelvin);
            double den = model.X + 15.0 * model.Y + 3.0 * model.Z;
            double inputDen = 0.1 + 15.0 * 0.8 + 3.0 * 0.1;
            Assert.InRange(Math.Abs(4.0 * model.X / den + first.ResidualU - 0.4 / inputDen), 0.0, 1e-12);
            Assert.InRange(Math.Abs(6.0 * model.Y / den + first.ResidualV - 4.8 / inputDen), 0.0, 1e-12);
            double fittedDistance = first.ResidualU * first.ResidualU + first.ResidualV * first.ResidualV;
            for (int i = 0; i <= 256; i++)
            {
                double temperature = Math.Exp(Math.Log(800.0) + Math.Log(100000.0 / 800.0) * i / 256.0);
                var candidate = observer.Evaluate(temperature);
                double candidateDen = candidate.X + 15.0 * candidate.Y + 3.0 * candidate.Z;
                double du = 4.0 * candidate.X / candidateDen - 0.4 / inputDen;
                double dv = 6.0 * candidate.Y / candidateDen - 4.8 / inputDen;
                Assert.True(fittedDistance <= du * du + dv * dv + 1e-12);
            }
        }

        [Theory]
        [InlineData(400.0, 800.0)]
        [InlineData(1e7, 100000.0)]
        public void OutOfFitRangeThermalColor_SelectsExplicitBoundary(double temperature, double expected)
        {
            var observer = Observer();
            var color = observer.Evaluate(temperature);
            var fitter = Fitter(observer);
            Assert.True(fitter.TryFit(color.X, color.Y, color.Z, out var fit));
            Assert.Equal(expected, fit.TemperatureKelvin);
            Assert.True(fit.AtTemperatureBoundary);
        }

        [Theory]
        [InlineData(0.0, 0.0, 0.0)]
        [InlineData(-1.0, 1.0, 1.0)]
        [InlineData(double.NaN, 1.0, 1.0)]
        [InlineData(1.0, double.PositiveInfinity, 1.0)]
        public void InvalidOrBlackColor_RejectsWithoutInventingTemperature(double x, double y, double z) =>
            Assert.False(Fitter(Observer()).TryFit(x, y, z, out _));
    }
}

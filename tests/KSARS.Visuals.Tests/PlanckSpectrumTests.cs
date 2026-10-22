using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class PlanckSpectrumTests
    {
        private const double H = 6.62607015e-34;
        private const double K = 1.380649e-23;
        private const double C = 299792458.0;

        private static double Radiance(double wavelength, double temperature)
        {
            Assert.True(PlanckSpectrum.TryWavelengthRadiance(wavelength, temperature, out double value));
            return value;
        }

        [Theory]
        [InlineData(360e-9, 5772.0)]
        [InlineData(555e-9, 5772.0)]
        [InlineData(830e-9, 1000.0)]
        [InlineData(1e-3, 2.7255)]
        [InlineData(555e-9, 2.73357808e8)]
        public void WavelengthRadiance_MatchesDirectSIPlanckFormula(double wavelength, double temperature)
        {
            double exponent = H * C / (wavelength * K * temperature);
            double reference = 2.0 * H * C * C
                / (Math.Pow(wavelength, 5.0) * (Math.Exp(exponent) - 1.0));
            TestOptics.AssertRel(reference, Radiance(wavelength, temperature), 2e-11);
        }

        [Theory]
        [InlineData(0.01)]
        [InlineData(0.5)]
        [InlineData(1.0)]
        [InlineData(2.0)]
        [InlineData(100.0)]
        public void ThermalDoppler_WavelengthD5EqualsShiftedTemperature(double doppler)
        {
            const double wavelength = 555e-9;
            const double temperature = 5772.0;
            double shifted = Radiance(wavelength, doppler * temperature);
            double transformed = Math.Pow(doppler, 5.0) * Radiance(doppler * wavelength, temperature);
            if (shifted == 0.0)
                Assert.Equal(0.0, transformed);
            else
                TestOptics.AssertRel(shifted, transformed, 2e-12);
        }

        [Fact]
        public void ColdTail_VisibleRestUnderflowsButBlueShiftIsNonzero()
        {
            Assert.Equal(0.0, Radiance(555e-9, 2.5));
            Assert.True(PlanckSpectrum.TryLogWavelengthRadiance(555e-9, 2.5, out double logTail));
            Assert.True(double.IsFinite(logTail));
            Assert.True(logTail < -1000.0);
            Assert.True(Radiance(555e-9, 2.5 * 2000.0) > 0.0);
        }

        [Fact]
        public void HighTemperature_ApproachesRayleighJeansWithoutExponentialCancellation()
        {
            const double wavelength = 555e-9;
            const double temperature = 1e20;
            double rayleighJeans = 2.0 * C * K * temperature / Math.Pow(wavelength, 4.0);
            TestOptics.AssertRel(rayleighJeans, Radiance(wavelength, temperature), 2e-13);
        }

        [Theory]
        [InlineData(0.999999)]
        [InlineData(1.000001)]
        public void SmallExponentBranch_IsContinuousAgainstDirectFormula(double factor)
        {
            const double wavelength = 555e-9;
            double temperature = H * C / (wavelength * K * (1e-4 * factor));
            double reference = 2.0 * H * C * C
                / (Math.Pow(wavelength, 5.0) * (Math.Exp(1e-4 * factor) - 1.0));
            TestOptics.AssertRel(reference, Radiance(wavelength, temperature), 2e-11);
        }

        [Theory]
        [InlineData(49.99999)]
        [InlineData(50.00001)]
        public void WienBranch_IsContinuousAgainstDirectFormula(double exponent)
        {
            const double wavelength = 555e-9;
            double temperature = H * C / (wavelength * K * exponent);
            double reference = 2.0 * H * C * C
                / (Math.Pow(wavelength, 5.0) * (Math.Exp(exponent) - 1.0));
            TestOptics.AssertRel(reference, Radiance(wavelength, temperature), 2e-12);
        }

        [Theory]
        [InlineData(2.7255)]
        [InlineData(5772.0)]
        [InlineData(1e8)]
        public void IntegratedRadiance_MatchesStefanBoltzmannPerSteradian(double temperature)
        {
            // Independent bolometric check of wavelength units and normalization.
            // x=hc/(lambda*k*T), |d lambda/dx|=hc/(k*T*x^2).
            const int intervals = 8192;
            const double limit = 64.0;
            double step = limit / intervals;
            double scale = H * C / (K * temperature);
            double sum = 0.0;
            for (int i = 1; i <= intervals; i++)
            {
                double x = i * step;
                double integrand = Radiance(scale / x, temperature) * scale / (x * x);
                int weight = i == intervals ? 1 : (i % 2 == 0 ? 2 : 4);
                sum += weight * integrand;
            }
            // The x=0 limiting integrand is zero; the tail beyond 64 is negligible here.
            double actual = sum * step / 3.0;
            const double stefanBoltzmann = 5.670374419e-8; // W m^-2 K^-4, rounded SI-derived value.
            double expected = stefanBoltzmann * Math.Pow(temperature, 4.0) / Math.PI;
            TestOptics.AssertRel(expected, actual, 2e-9);
        }

        [Fact]
        public void ExtremeInputs_LogDomainDistinguishesOverflowAndNegligibleTail()
        {
            Assert.True(PlanckSpectrum.TryLogWavelengthRadiance(1e-100, 1e100, out double logBright));
            Assert.True(double.IsFinite(logBright));
            Assert.True(logBright > Math.Log(double.MaxValue));
            Assert.False(PlanckSpectrum.TryWavelengthRadiance(1e-100, 1e100, out double overflow));
            Assert.Equal(0.0, overflow);

            Assert.True(PlanckSpectrum.TryLogWavelengthRadiance(double.Epsilon, double.Epsilon,
                out double logDark));
            Assert.Equal(double.NegativeInfinity, logDark);
            Assert.Equal(0.0, Radiance(double.Epsilon, double.Epsilon));
            // x itself underflows here; its logarithm must still preserve the RJ denominator.
            Assert.True(PlanckSpectrum.TryLogWavelengthRadiance(double.MaxValue, double.MaxValue,
                out double logLongWave));
            Assert.True(double.IsFinite(logLongWave));
            Assert.Equal(0.0, Radiance(double.MaxValue, double.MaxValue));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void InvalidWavelengthOrTemperature_FailsClosed(double invalid)
        {
            Assert.False(PlanckSpectrum.TryWavelengthRadiance(invalid, 5772.0, out double a));
            Assert.False(PlanckSpectrum.TryWavelengthRadiance(555e-9, invalid, out double b));
            Assert.False(PlanckSpectrum.TryLogWavelengthRadiance(invalid, 5772.0, out double c));
            Assert.False(PlanckSpectrum.TryLogWavelengthRadiance(555e-9, invalid, out double d));
            Assert.Equal(0.0, a);
            Assert.Equal(0.0, b);
            Assert.Equal(0.0, c);
            Assert.Equal(0.0, d);
        }
    }
}

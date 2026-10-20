using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class StarSpectralCacheTests
    {
        private static StarSpectralCache NewCache() => new(ThermalSpectrumTable.LoadEmbedded());

        [Fact]
        public void SameColorUnderDifferentAlphaBits_SharesOneModel()
        {
            var cache = NewCache();
            Assert.True(cache.TryGet(0xAACC8040u, out StellarSpectrumModel? first));
            Assert.True(cache.TryGet(0x11CC8040u, out StellarSpectrumModel? second));

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Equal(1, cache.CachedColorCount);
            Assert.Equal(1, cache.ModelCount);
            Assert.Null(cache.Failure);
        }

        [Fact]
        public void RedAndBlueFollowNativeByteOrder_AndMatchDirectFit()
        {
            var cache = NewCache();
            var table = ThermalSpectrumTable.LoadEmbedded();

            // byte4 memory order: R is the lowest byte, B is byte 2.
            Assert.True(cache.TryGet(0x0000ffu, out StellarSpectrumModel? red));
            Assert.True(cache.TryGet(0xff0000u, out StellarSpectrumModel? blue));
            Assert.NotNull(red);
            Assert.NotNull(blue);
            Assert.NotSame(red, blue);
            Assert.Equal(2, cache.CachedColorCount);
            Assert.Equal(2, cache.ModelCount);

            var fitter = new StellarTemperatureFitter(t =>
            {
                ThermalSample sample = table.Evaluate(t);
                return (sample.X, sample.Y, sample.Z);
            });
            Assert.True(SpectralColorSpace.TryLinearRgbToXyz(1.0, 0.0, 0.0, out var redXyz));
            Assert.True(StellarSpectrumModel.TryCreate(table, fitter,
                redXyz.X, redXyz.Y, redXyz.Z, out StellarSpectrumModel? directRed));
            Assert.True(SpectralColorSpace.TryLinearRgbToXyz(0.0, 0.0, 1.0, out var blueXyz));
            Assert.True(StellarSpectrumModel.TryCreate(table, fitter,
                blueXyz.X, blueXyz.Y, blueXyz.Z, out StellarSpectrumModel? directBlue));

            Assert.Equal(directRed!.Fit, red!.Fit);
            Assert.Equal(directBlue!.Fit, blue!.Fit);
            Assert.NotEqual(red.Fit.TemperatureKelvin, blue.Fit.TemperatureKelvin);
        }

        [Fact]
        public void BlackColor_CachesNullWithoutInventingModel()
        {
            var cache = NewCache();
            Assert.False(cache.TryGet(0u, out StellarSpectrumModel? model));
            Assert.Null(model);
            Assert.Equal(1, cache.CachedColorCount);
            Assert.Equal(0, cache.ModelCount);

            // Alpha-only bits still resolve to the same black key.
            Assert.False(cache.TryGet(0x7f000000u, out _));
            Assert.Equal(1, cache.CachedColorCount);
            Assert.Null(cache.Failure);
        }

        [Fact]
        public void NewCache_IsIsolatedAndEmpty()
        {
            var first = NewCache();
            Assert.True(first.TryGet(0x808080u, out _));

            var second = NewCache();
            Assert.Equal(0, second.CachedColorCount);
            Assert.Equal(0, second.ModelCount);
            Assert.Null(second.Failure);
            Assert.Contains("Rec709/D65", second.Describe());
            Assert.Contains("not measured", second.Describe());
        }

        // The beaming exponent separates resolved disks from point sources:
        // for the sun the thermal luminance ratio lum(D*T)/lum(T) is the whole
        // observed flux ratio (D^4 surface brightness, unchanged solid angle),
        // while point sources add the photon-energy-times-arrival-rate D^2.
        // The two evaluations must differ by exactly 2*ln(D) in log flux and
        // agree on chromaticity.
        [Fact]
        public void BeamingExponent_ShiftsLogFluxOnly()
        {
            var table = ThermalSpectrumTable.LoadEmbedded();
            var fitter = new StellarTemperatureFitter(t =>
            {
                ThermalSample sample = table.Evaluate(t);
                return (sample.X, sample.Y, sample.Z);
            });
            Assert.True(SpectralColorSpace.TryLinearRgbToXyz(1.0, 0.95, 0.85, out var xyz));
            Assert.True(StellarSpectrumModel.TryCreate(table, fitter,
                xyz.X, xyz.Y, xyz.Z, out StellarSpectrumModel? model));

            const double doppler = 1.7;
            Assert.True(model!.TryEvaluate(doppler, 0.0, out StellarEmission disk));
            Assert.True(model.TryEvaluate(doppler, 2.0, out StellarEmission point));
            Assert.Equal(disk.X, point.X, 15);
            Assert.Equal(disk.Y, point.Y, 15);
            Assert.Equal(disk.Z, point.Z, 15);
            Assert.Equal(2.0 * Math.Log(doppler), point.LogFlux - disk.LogFlux, 12);
        }
    }
}

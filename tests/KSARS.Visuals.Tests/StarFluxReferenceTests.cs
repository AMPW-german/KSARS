using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class StarFluxReferenceTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(32)]
        [InlineData(128)]
        [InlineData(255)]
        public void StockModelQuadrature_ConvergesAgainstFinerReference(byte scale)
        {
            double reference = StarFluxReference.Integrate(scale, 64, 1024);
            double actual = StarFluxReference.Integrate(scale);
            Assert.True(reference > 0.0);
            Assert.InRange(Math.Abs(actual / reference - 1.0), 0.0, 0.002);
        }

        [Fact]
        public void ZeroScale_HasNoFluxOrArtificialVisibilityFloor() =>
            Assert.Equal(0.0, StarFluxReference.Integrate(0));
    }
}

using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class DopplerTests
    {
        private static PresentationVelocity Observer(OpticalVector direction, double beta)
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(direction * (beta * TestOptics.C), out PresentationVelocity observer));
            return observer;
        }

        [Fact]
        public void RestObserver_DopplerFactorIsUnity()
        {
            var n = new OpticalVector(0.6, 0.8, 0.0);
            Assert.True(RelativisticOptics.TryForward(n, PresentationVelocity.Rest, out OpticalRay forward));
            Assert.True(RelativisticOptics.TryInverse(n, PresentationVelocity.Rest, out OpticalRay inverse));
            Assert.Equal(1.0, forward.DopplerFactor);
            Assert.Equal(1.0, inverse.DopplerFactor);
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(0.9)]
        public void AxisPoles_ForwardAndRearDopplerFactors(double beta)
        {
            var observer = Observer(new OpticalVector(1.0, 0.0, 0.0), beta);
            double forwardFactor = Math.Sqrt((1.0 + beta) / (1.0 - beta));
            double rearFactor = 1.0 / forwardFactor;

            TestOptics.AssertRel(forwardFactor, observer.ForwardD, 1e-12);
            TestOptics.AssertRel(rearFactor, observer.RearD, 1e-12);

            Assert.True(RelativisticOptics.TryForward(new OpticalVector(1.0, 0.0, 0.0), observer, out OpticalRay ahead));
            Assert.True(RelativisticOptics.TryForward(new OpticalVector(-1.0, 0.0, 0.0), observer, out OpticalRay behind));
            TestOptics.AssertRel(forwardFactor, ahead.DopplerFactor, 1e-12);
            TestOptics.AssertRel(rearFactor, behind.DopplerFactor, 1e-12);

            Assert.True(RelativisticOptics.TryInverse(new OpticalVector(1.0, 0.0, 0.0), observer, out OpticalRay inverseAhead));
            Assert.True(RelativisticOptics.TryInverse(new OpticalVector(-1.0, 0.0, 0.0), observer, out OpticalRay inverseBehind));
            TestOptics.AssertRel(forwardFactor, inverseAhead.DopplerFactor, 1e-12);
            TestOptics.AssertRel(rearFactor, inverseBehind.DopplerFactor, 1e-12);
        }

        [Fact]
        public void TransverseDoppler_AtBeta09_IsGammaAndInverseGamma()
        {
            var observer = Observer(new OpticalVector(1.0, 0.0, 0.0), 0.9);
            Assert.True(RelativisticOptics.TryForward(new OpticalVector(0.0, 1.0, 0.0), observer, out OpticalRay forward));
            Assert.True(RelativisticOptics.TryInverse(new OpticalVector(0.0, 1.0, 0.0), observer, out OpticalRay inverse));
            TestOptics.AssertRel(1.0 / Math.Sqrt(0.19), forward.DopplerFactor, 1e-12);
            TestOptics.AssertRel(Math.Sqrt(0.19), inverse.DopplerFactor, 1e-12);
        }

        [Fact]
        public void MaxSpeed_QAndGamma_AtDoublePrecisionLimit()
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(new OpticalVector(2.0 * TestOptics.C, 0.0, 0.0), out PresentationVelocity observer));
            Assert.True(observer.WasConstrained);
            Assert.Equal(Math.BitDecrement(TestOptics.C), observer.Speed);
            TestOptics.AssertRel(9.970448687e-9, observer.Q, 1e-9);
            TestOptics.AssertRel(5.01481945e7, observer.Gamma, 1e-8);
            Assert.Equal(1f, (float)(observer.Speed / TestOptics.C));
            Assert.True(double.IsFinite(observer.Q));
            Assert.True(double.IsFinite(observer.Gamma));
            Assert.True(double.IsFinite(observer.ForwardD));
            Assert.True(double.IsFinite(observer.RearD));
        }

        [Fact]
        public void ZeroVelocity_ReturnsRest()
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(OpticalVector.Zero, out PresentationVelocity observer));
            Assert.Equal(0.0, observer.Speed);
            Assert.Equal(1.0, observer.Q);
            Assert.Equal(0.0, observer.Direction.X);
            Assert.Equal(0.0, observer.Direction.Y);
            Assert.Equal(0.0, observer.Direction.Z);
            Assert.False(observer.WasConstrained);
        }

        [Theory]
        [InlineData(double.NaN, 0.0, 0.0)]
        [InlineData(0.0, double.PositiveInfinity, 0.0)]
        [InlineData(0.0, 0.0, double.NegativeInfinity)]
        public void NonFiniteVelocity_RejectedWithRestDefault(double x, double y, double z)
        {
            Assert.False(RelativisticOptics.TryCreateVelocity(new OpticalVector(x, y, z), out PresentationVelocity observer));
            Assert.Equal(0.0, observer.Speed);
            Assert.Equal(1.0, observer.Q);
            Assert.Equal(0.0, observer.Direction.X);
            Assert.Equal(0.0, observer.Direction.Y);
            Assert.Equal(0.0, observer.Direction.Z);
            Assert.False(observer.WasConstrained);
        }

        [Fact]
        public void SuperluminalVelocity_ConstrainsReturnedStateOnly()
        {
            var axis = new OpticalVector(2.0 * TestOptics.C, 0.0, 0.0);
            Assert.True(RelativisticOptics.TryCreateVelocity(axis, out PresentationVelocity observer));
            Assert.True(observer.WasConstrained);
            Assert.Equal(Math.BitDecrement(TestOptics.C), observer.Speed);
            Assert.Equal(1.0, observer.Direction.X);
            Assert.Equal(0.0, observer.Direction.Y);
            Assert.Equal(2.0 * TestOptics.C, axis.X);

            var diagonal = new OpticalVector(double.MaxValue, double.MaxValue, double.MaxValue);
            Assert.True(RelativisticOptics.TryCreateVelocity(diagonal, out PresentationVelocity constrained));
            Assert.True(constrained.WasConstrained);
            Assert.Equal(Math.BitDecrement(TestOptics.C), constrained.Speed);
            double invSqrt3 = 1.0 / Math.Sqrt(3.0);
            TestOptics.AssertRel(invSqrt3, constrained.Direction.X, 1e-15);
            TestOptics.AssertRel(invSqrt3, constrained.Direction.Y, 1e-15);
            TestOptics.AssertRel(invSqrt3, constrained.Direction.Z, 1e-15);
            Assert.Equal(double.MaxValue, diagonal.X);
            Assert.Equal(double.MaxValue, diagonal.Y);
            Assert.Equal(double.MaxValue, diagonal.Z);
        }

        [Fact]
        public void ActualMaxSpeedAxis_IsExactAndNotConstrained()
        {
            double maxSpeed = Math.BitDecrement(TestOptics.C);
            Assert.True(RelativisticOptics.TryCreateVelocity(new OpticalVector(maxSpeed, 0.0, 0.0), out PresentationVelocity observer));
            Assert.False(observer.WasConstrained);
            Assert.Equal(maxSpeed, observer.Speed);
            Assert.Equal(1.0, observer.Direction.X);
            Assert.True(double.IsFinite(observer.Q));
            TestOptics.AssertRel(9.970448687e-9, observer.Q, 1e-9);
        }

        [Fact]
        public void SubnormalVelocities_AcceptedNotClamped()
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(new OpticalVector(double.Epsilon, 0.0, 0.0), out PresentationVelocity axis));
            Assert.False(axis.WasConstrained);
            Assert.Equal(double.Epsilon, axis.Speed);
            Assert.Equal(1.0, axis.Q);
            Assert.Equal(1.0, axis.Direction.X);

            Assert.True(RelativisticOptics.TryForward(new OpticalVector(0.0, 1.0, 0.0), axis, out OpticalRay identity));
            Assert.Equal(0.0, identity.Direction.X);
            Assert.Equal(1.0, identity.Direction.Y);
            Assert.Equal(1.0, identity.DopplerFactor);

            Assert.True(RelativisticOptics.TryCreateVelocity(new OpticalVector(double.Epsilon, double.Epsilon, double.Epsilon), out PresentationVelocity diagonal));
            Assert.False(diagonal.WasConstrained);
            Assert.True(double.IsFinite(diagonal.Speed));
            Assert.True(diagonal.Speed > 0.0);
            double invSqrt3 = 1.0 / Math.Sqrt(3.0);
            TestOptics.AssertRel(invSqrt3, diagonal.Direction.X, 1e-15);
            TestOptics.AssertRel(invSqrt3, diagonal.Direction.Y, 1e-15);
            TestOptics.AssertRel(invSqrt3, diagonal.Direction.Z, 1e-15);
        }

        [Fact]
        public void ExtremeQ_AxisAndArbitrary_OutputsFiniteUnitSigned()
        {
            var axes = new[]
            {
                new OpticalVector(1.0, 0.0, 0.0),
                new OpticalVector(1.0, 2.0, -3.0) * (1.0 / Math.Sqrt(14.0)),
            };
            foreach (var u in axes)
            {
                Assert.True(RelativisticOptics.TryCreateVelocity(u * (2.0 * TestOptics.C), out PresentationVelocity observer));
                var perpendicular = PerpendicularTo(u);

                Assert.True(RelativisticOptics.TryForward(u, observer, out OpticalRay ahead));
                Assert.True(RelativisticOptics.TryForward(u * -1.0, observer, out OpticalRay behind));
                Assert.True(RelativisticOptics.TryForward(perpendicular, observer, out OpticalRay side));
                Assert.True(RelativisticOptics.TryInverse(perpendicular, observer, out OpticalRay sideInverse));

                foreach (var ray in new[] { ahead, behind, side, sideInverse })
                {
                    Assert.True(double.IsFinite(ray.Direction.X));
                    Assert.True(double.IsFinite(ray.Direction.Y));
                    Assert.True(double.IsFinite(ray.Direction.Z));
                    Assert.True(double.IsFinite(ray.DopplerFactor));
                    Assert.True(ray.DopplerFactor > 0.0);
                    Assert.True(Math.Abs(Math.Sqrt(ray.Direction.LengthSquared()) - 1.0) < 1e-12);
                }

                Assert.True(ahead.Direction.Dot(u) > 0.9999);
                Assert.True(behind.Direction.Dot(u) < -0.9999);
                Assert.True(side.Direction.Dot(u) > 0.0);
                Assert.True(sideInverse.Direction.Dot(u) < 0.0);
                TestOptics.AssertRel(1.0 / observer.Q, ahead.DopplerFactor, 1e-12);
                TestOptics.AssertRel(observer.Q, behind.DopplerFactor, 1e-12);
            }
        }

        private static OpticalVector PerpendicularTo(OpticalVector u)
        {
            var p = u.Cross(new OpticalVector(0.0, 0.0, 1.0));
            return p * (1.0 / Math.Sqrt(p.LengthSquared()));
        }
    }

    internal static class TestOptics
    {
        internal const double C = PhysicsConstants.SpeedOfLight;

        internal static void AssertRel(double expected, double actual, double relTol, string context = "")
        {
            Assert.True(
                double.IsFinite(actual) && Math.Abs(actual - expected) <= Math.Abs(expected) * relTol,
                $"expected {expected:R} actual {actual:R} {context}");
        }
    }
}

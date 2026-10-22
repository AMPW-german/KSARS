using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class AberrationTests
    {
        private static readonly OpticalVector U = new OpticalVector(1.0, 2.0, -3.0) * (1.0 / Math.Sqrt(14.0));

        private static OpticalVector Rotate(OpticalVector v) => new(-v.Y, v.X, v.Z);

        private static PresentationVelocity Observer(OpticalVector direction, double beta)
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(direction * (beta * TestOptics.C), out PresentationVelocity observer));
            return observer;
        }

        [Fact]
        public void RestObserver_ForwardAndInverse_PreservesDirectionAndDoppler()
        {
            var n = new OpticalVector(0.6, 0.8, 0.0);
            Assert.True(RelativisticOptics.TryForward(n, PresentationVelocity.Rest, out OpticalRay forward));
            Assert.True(RelativisticOptics.TryInverse(n, PresentationVelocity.Rest, out OpticalRay inverse));
            TestOptics.AssertRel(0.6, forward.Direction.X, 1e-15);
            TestOptics.AssertRel(0.8, forward.Direction.Y, 1e-15);
            Assert.Equal(0.0, forward.Direction.Z);
            Assert.Equal(1.0, forward.DopplerFactor);
            TestOptics.AssertRel(0.6, inverse.Direction.X, 1e-15);
            Assert.Equal(1.0, inverse.DopplerFactor);
        }

        [Fact]
        public void RayInput_IsNormalizedAcrossMagnitudeRange()
        {
            var rest = PresentationVelocity.Rest;
            Assert.True(RelativisticOptics.TryForward(new OpticalVector(3.0, 4.0, 0.0), rest, out OpticalRay scaled));
            TestOptics.AssertRel(0.6, scaled.Direction.X, 1e-15);
            TestOptics.AssertRel(0.8, scaled.Direction.Y, 1e-15);
            Assert.True(RelativisticOptics.TryForward(new OpticalVector(1e-300, 0.0, 0.0), rest, out OpticalRay tiny));
            Assert.Equal(1.0, tiny.Direction.X);
            Assert.True(RelativisticOptics.TryForward(new OpticalVector(1e300, 0.0, 0.0), rest, out OpticalRay huge));
            Assert.Equal(1.0, huge.Direction.X);
        }

        [Fact]
        public void SubnormalRayDirections_AcceptedFiniteUnit()
        {
            var moving = Observer(new OpticalVector(1.0, 0.0, 0.0), 0.5);
            foreach (var observer in new[] { PresentationVelocity.Rest, moving })
            {
                Assert.True(RelativisticOptics.TryForward(new OpticalVector(double.Epsilon, 0.0, 0.0), observer, out OpticalRay plus));
                Assert.True(RelativisticOptics.TryForward(new OpticalVector(-double.Epsilon, 0.0, 0.0), observer, out OpticalRay minus));
                TestOptics.AssertRel(1.0, plus.Direction.X, 1e-12);
                TestOptics.AssertRel(-1.0, minus.Direction.X, 1e-12);
                Assert.True(Math.Abs(Math.Sqrt(plus.Direction.LengthSquared()) - 1.0) < 1e-12);
                Assert.True(Math.Abs(Math.Sqrt(minus.Direction.LengthSquared()) - 1.0) < 1e-12);
            }
        }

        [Theory]
        [InlineData(0.0, 0.0, 0.0)]
        [InlineData(double.NaN, 0.0, 0.0)]
        [InlineData(0.0, double.PositiveInfinity, 0.0)]
        [InlineData(double.NegativeInfinity, double.NegativeInfinity, double.NaN)]
        public void InvalidDirection_RejectedWithSafeDefault(double x, double y, double z)
        {
            var input = new OpticalVector(x, y, z);
            var observer = Observer(U, 0.5);
            Assert.False(RelativisticOptics.TryForward(input, observer, out OpticalRay forward));
            Assert.False(RelativisticOptics.TryInverse(input, observer, out OpticalRay inverse));
            Assert.Equal(0.0, forward.Direction.X);
            Assert.Equal(0.0, forward.Direction.Y);
            Assert.Equal(0.0, forward.Direction.Z);
            Assert.Equal(1.0, forward.DopplerFactor);
            Assert.Equal(1.0, inverse.DopplerFactor);
        }

        [Fact]
        public void RestTransverse_ForwardObserved_AngleAndCosineAtBeta09()
        {
            var observer = Observer(new OpticalVector(1.0, 0.0, 0.0), 0.9);
            Assert.True(RelativisticOptics.TryForward(new OpticalVector(0.0, 1.0, 0.0), observer, out OpticalRay ray));
            TestOptics.AssertRel(0.9, ray.Direction.X, 1e-12);
            Assert.True(ray.Direction.Y > 0.0);
            double angleDeg = Math.Acos(ray.Direction.X) * 180.0 / Math.PI;
            Assert.True(Math.Abs(angleDeg - 25.8419327631671) < 1e-9, $"angle {angleDeg:R}");
            TestOptics.AssertRel(1.0 / Math.Sqrt(0.19), ray.DopplerFactor, 1e-12);
        }

        [Fact]
        public void ObservedTransverse_InverseRest_CosineAndDopplerAtBeta09()
        {
            var observer = Observer(new OpticalVector(1.0, 0.0, 0.0), 0.9);
            Assert.True(RelativisticOptics.TryInverse(new OpticalVector(0.0, 1.0, 0.0), observer, out OpticalRay ray));
            TestOptics.AssertRel(-0.9, ray.Direction.X, 1e-12);
            TestOptics.AssertRel(Math.Sqrt(0.19), ray.DopplerFactor, 1e-12);
        }

        [Fact]
        public void PoleDirections_StayOnAxis_AtBeta09()
        {
            var observer = Observer(new OpticalVector(1.0, 0.0, 0.0), 0.9);
            Assert.True(RelativisticOptics.TryForward(new OpticalVector(1.0, 0.0, 0.0), observer, out OpticalRay ahead));
            Assert.True(RelativisticOptics.TryForward(new OpticalVector(-1.0, 0.0, 0.0), observer, out OpticalRay behind));
            TestOptics.AssertRel(1.0, ahead.Direction.X, 1e-12);
            TestOptics.AssertRel(-1.0, behind.Direction.X, 1e-12);
        }

        [Fact]
        public void ForwardInverse_Roundtrip_SphereAcrossBetas()
        {
            var betas = new[] { 0.0, 0.1, 0.5, 0.9, 0.99, 0.9999 };
            foreach (double beta in betas)
            {
                var observer = Observer(U, beta);
                for (int i = 0; i <= 256; i++)
                {
                    double z = 1.0 - 2.0 * (i + 0.5) / 257.0;
                    double phi = i * Math.PI * (3.0 - Math.Sqrt(5.0));
                    double radius = Math.Sqrt(1.0 - z * z);
                    var n = new OpticalVector(radius * Math.Cos(phi), radius * Math.Sin(phi), z);

                    Assert.True(RelativisticOptics.TryForward(n, observer, out OpticalRay forward), $"beta={beta} i={i} forward");
                    Assert.True(RelativisticOptics.TryInverse(forward.Direction, observer, out OpticalRay inverse), $"beta={beta} i={i} inverse");

                    double dirError = Math.Sqrt((inverse.Direction - n).LengthSquared());
                    Assert.True(dirError < 5e-11, $"beta={beta} i={i} direction error {dirError:R}");
                    TestOptics.AssertRel(forward.DopplerFactor, inverse.DopplerFactor, 5e-11, $"beta={beta} i={i}");
                    Assert.True(Math.Abs(Math.Sqrt(forward.Direction.LengthSquared()) - 1.0) < 1e-12, $"beta={beta} i={i} forward length");
                    Assert.True(Math.Abs(Math.Sqrt(inverse.Direction.LengthSquared()) - 1.0) < 1e-12, $"beta={beta} i={i} inverse length");
                }
            }
        }

        [Fact]
        public void RotationalCovariance_QuarterTurnAboutZ()
        {
            var n = new OpticalVector(2.0, -1.0, 4.0) * (1.0 / Math.Sqrt(21.0));
            var observer1 = Observer(U, 0.9);
            var observer2 = Observer(Rotate(U), 0.9);
            Assert.True(RelativisticOptics.TryForward(n, observer1, out OpticalRay original));
            Assert.True(RelativisticOptics.TryForward(Rotate(n), observer2, out OpticalRay rotated));
            double error = Math.Sqrt((rotated.Direction - Rotate(original.Direction)).LengthSquared());
            Assert.True(error < 1e-12, $"rotation covariance error {error:R}");
            TestOptics.AssertRel(original.DopplerFactor, rotated.DopplerFactor, 1e-12);
        }

        [Fact]
        public void MalformedObserver_RejectedWithoutException()
        {
            var unit = new OpticalVector(1.0, 0.0, 0.0);
            double consistentQ = Math.Sqrt((TestOptics.C - 1e8) / (TestOptics.C + 1e8));
            var bad = new[]
            {
                default(PresentationVelocity),
                new PresentationVelocity(0.0, 0.5, OpticalVector.Zero, false),
                new PresentationVelocity(0.0, 1.0, new OpticalVector(double.NaN, 0.0, 0.0), false),
                new PresentationVelocity(0.0, 1.0, unit, false),
                new PresentationVelocity(1e8, 0.0, unit, false),
                new PresentationVelocity(1e8, double.NaN, unit, false),
                new PresentationVelocity(1e8, -0.5, unit, false),
                new PresentationVelocity(1e8, 1.5, unit, false),
                new PresentationVelocity(1e8, 0.9, unit, false),
                new PresentationVelocity(1e8, 1e-200, unit, false),
                new PresentationVelocity(TestOptics.C, consistentQ, unit, false),
                new PresentationVelocity(2.0 * TestOptics.C, consistentQ, unit, false),
                new PresentationVelocity(-1.0, consistentQ, unit, false),
                new PresentationVelocity(double.NaN, consistentQ, unit, false),
                new PresentationVelocity(double.PositiveInfinity, consistentQ, unit, false),
                new PresentationVelocity(1e8, consistentQ, OpticalVector.Zero, false),
                new PresentationVelocity(1e8, consistentQ, new OpticalVector(2.0, 0.0, 0.0), false),
            };
            foreach (var observer in bad)
            {
                bool forwardOk = true;
                bool inverseOk = true;
                var ex = Record.Exception(() =>
                {
                    forwardOk = RelativisticOptics.TryForward(unit, observer, out _);
                    inverseOk = RelativisticOptics.TryInverse(unit, observer, out _);
                });
                Assert.Null(ex);
                Assert.False(forwardOk);
                Assert.False(inverseOk);
            }
        }
    }
}

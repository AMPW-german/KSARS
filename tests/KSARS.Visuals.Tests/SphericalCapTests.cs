using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class SphericalCapTests
    {
        private static readonly OpticalVector U = new OpticalVector(1.0, 2.0, -3.0) * (1.0 / Math.Sqrt(14.0));

        private static OpticalVector Rotate(OpticalVector v) => new(-v.Y, v.X, v.Z);

        private static PresentationVelocity Observer(OpticalVector direction, double beta)
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(direction * (beta * TestOptics.C), out PresentationVelocity observer));
            return observer;
        }

        private static void TangentBasis(OpticalVector a, out OpticalVector t1, out OpticalVector t2)
        {
            var reference = Math.Abs(a.Z) < 0.9 ? new OpticalVector(0.0, 0.0, 1.0) : new OpticalVector(1.0, 0.0, 0.0);
            var raw = a.Cross(reference);
            t1 = raw * (1.0 / Math.Sqrt(raw.LengthSquared()));
            t2 = a.Cross(t1);
        }

        private static OpticalVector PerpendicularTo(OpticalVector v)
        {
            var p = v.Cross(new OpticalVector(0.0, 0.0, 1.0));
            return p * (1.0 / Math.Sqrt(p.LengthSquared()));
        }

        private static OpticalVector BoundaryDirection(OpticalVector a, double alpha, OpticalVector t1, OpticalVector t2, double phi) =>
            a * Math.Cos(alpha) + (t1 * Math.Cos(phi) + t2 * Math.Sin(phi)) * Math.Sin(alpha);

        private static void AssertConsistent(ObservedSphericalCap cap)
        {
            Assert.True(double.IsFinite(cap.Axis.X) && double.IsFinite(cap.Axis.Y) && double.IsFinite(cap.Axis.Z));
            Assert.True(double.IsFinite(cap.AngularRadius));
            Assert.True(cap.AngularRadius >= 0.0 && cap.AngularRadius <= Math.PI, $"radius {cap.AngularRadius:R}");
            Assert.True(Math.Abs(Math.Sqrt(cap.Axis.LengthSquared()) - 1.0) < 1e-12,
                $"axis length {Math.Sqrt(cap.Axis.LengthSquared()):R}");
            double normError = Math.Abs(cap.CosAngularRadius * cap.CosAngularRadius + cap.SinAngularRadius * cap.SinAngularRadius - 1.0);
            Assert.True(normError < 1e-13, $"cos/sin norm error {normError:R}");
            double angleError = Math.Abs(Math.Atan2(cap.SinAngularRadius, cap.CosAngularRadius) - cap.AngularRadius);
            Assert.True(angleError < 1e-14, $"atan2 consistency error {angleError:R}");
        }

        private static void AssertDefault(ObservedSphericalCap cap)
        {
            Assert.Equal(0.0, cap.Axis.X);
            Assert.Equal(0.0, cap.Axis.Y);
            Assert.Equal(0.0, cap.Axis.Z);
            Assert.Equal(0.0, cap.AngularRadius);
            Assert.Equal(0.0, cap.CosAngularRadius);
            Assert.Equal(0.0, cap.SinAngularRadius);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.25)]
        [InlineData(Math.PI / 2.0)]
        public void RestObserver_ReturnsOriginalAxisAndRadius(double alpha)
        {
            var center = new OpticalVector(0.0, 3.0, 4.0);
            Assert.True(RelativisticOptics.TryTransformSphericalCap(center, alpha, PresentationVelocity.Rest, out ObservedSphericalCap cap));
            Assert.Equal(0.0, cap.Axis.X);
            TestOptics.AssertRel(0.6, cap.Axis.Y, 1e-15);
            TestOptics.AssertRel(0.8, cap.Axis.Z, 1e-15);
            Assert.Equal(alpha, cap.AngularRadius);
            Assert.Equal(Math.Cos(alpha), cap.CosAngularRadius);
            Assert.Equal(Math.Sin(alpha), cap.SinAngularRadius);
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(0.9)]
        [InlineData(0.99)]
        public void TransformedBoundary_MatchesForwardAberratedCircle(double beta)
        {
            var observer = Observer(U, beta);
            var centers = new[]
            {
                U,
                U * -1.0,
                PerpendicularTo(U),
                new OpticalVector(0.6, 0.8, 0.0),
            };
            double[] alphas = { 0.05, 0.3, Math.PI / 2.0 };

            foreach (var a in centers)
            {
                TangentBasis(a, out OpticalVector t1, out OpticalVector t2);
                foreach (double alpha in alphas)
                {
                    Assert.True(RelativisticOptics.TryTransformSphericalCap(a, alpha, observer, out ObservedSphericalCap cap),
                        $"beta={beta} alpha={alpha}");
                    AssertConsistent(cap);
                    for (int i = 0; i < 64; i++)
                    {
                        double phi = 2.0 * Math.PI * i / 64.0;
                        var boundary = BoundaryDirection(a, alpha, t1, t2, phi);
                        Assert.True(RelativisticOptics.TryForward(boundary, observer, out OpticalRay ray),
                            $"beta={beta} alpha={alpha} i={i} forward");
                        double residual = Math.Abs(ray.Direction.Dot(cap.Axis) - cap.CosAngularRadius);
                        Assert.True(residual < 2e-12, $"beta={beta} alpha={alpha} i={i} residual {residual:R}");
                    }
                }
            }
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(0.9)]
        public void RestInsideOutsideAngles_MapToCapSides(double beta)
        {
            var observer = Observer(U, beta);
            var a = new OpticalVector(0.6, 0.8, 0.0);
            double alpha = 0.3;
            TangentBasis(a, out OpticalVector t1, out OpticalVector t2);
            Assert.True(RelativisticOptics.TryTransformSphericalCap(a, alpha, observer, out ObservedSphericalCap cap));
            AssertConsistent(cap);

            Assert.True(RelativisticOptics.TryForward(a, observer, out OpticalRay center));
            Assert.True(center.Direction.Dot(cap.Axis) > cap.CosAngularRadius);

            foreach (double phi in new[] { 0.0, 2.1 })
            {
                var inside = BoundaryDirection(a, alpha * 0.9, t1, t2, phi);
                var outside = BoundaryDirection(a, alpha + 0.01, t1, t2, phi);
                Assert.True(RelativisticOptics.TryForward(inside, observer, out OpticalRay insideRay));
                Assert.True(RelativisticOptics.TryForward(outside, observer, out OpticalRay outsideRay));
                double insideDot = insideRay.Direction.Dot(cap.Axis);
                double outsideDot = outsideRay.Direction.Dot(cap.Axis);
                Assert.True(insideDot > cap.CosAngularRadius,
                    $"beta={beta} phi={phi} inside dot {insideDot:R} cos {cap.CosAngularRadius:R}");
                Assert.True(outsideDot < cap.CosAngularRadius,
                    $"beta={beta} phi={phi} outside dot {outsideDot:R} cos {cap.CosAngularRadius:R}");
            }
        }

        [Fact]
        public void RotatedObserverAndCap_ProduceRotatedResult()
        {
            double alpha = 0.4;
            var a = new OpticalVector(2.0, -1.0, 4.0) * (1.0 / Math.Sqrt(21.0));
            var observer1 = Observer(U, 0.9);
            var observer2 = Observer(Rotate(U), 0.9);
            Assert.True(RelativisticOptics.TryTransformSphericalCap(a, alpha, observer1, out ObservedSphericalCap original));
            Assert.True(RelativisticOptics.TryTransformSphericalCap(Rotate(a), alpha, observer2, out ObservedSphericalCap rotated));
            double error = Math.Sqrt((rotated.Axis - Rotate(original.Axis)).LengthSquared());
            Assert.True(error < 1e-12, $"axis covariance error {error:R}");
            TestOptics.AssertRel(original.AngularRadius, rotated.AngularRadius, 1e-12);
            TestOptics.AssertRel(original.CosAngularRadius, rotated.CosAngularRadius, 1e-12);
            TestOptics.AssertRel(original.SinAngularRadius, rotated.SinAngularRadius, 1e-12);
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(0.9)]
        public void ZeroRadius_MatchesForwardTransformedCenter(double beta)
        {
            var observer = Observer(U, beta);
            var a = new OpticalVector(2.0, -1.0, 4.0) * (1.0 / Math.Sqrt(21.0));
            Assert.True(RelativisticOptics.TryTransformSphericalCap(a, 0.0, observer, out ObservedSphericalCap cap));
            Assert.True(RelativisticOptics.TryForward(a, observer, out OpticalRay ray));
            double error = Math.Sqrt((cap.Axis - ray.Direction).LengthSquared());
            Assert.True(error < 1e-14, $"axis error {error:R}");
            Assert.Equal(0.0, cap.AngularRadius);
            Assert.Equal(1.0, cap.CosAngularRadius);
            Assert.Equal(0.0, cap.SinAngularRadius);
        }

        [Fact]
        public void RearAligned_Beta09_ExpandedRadiusMatchesReference()
        {
            var observer = Observer(new OpticalVector(1.0, 0.0, 0.0), 0.9);
            double alpha = 0.05;
            Assert.True(RelativisticOptics.TryTransformSphericalCap(new OpticalVector(-1.0, 0.0, 0.0), alpha, observer, out ObservedSphericalCap cap));
            AssertConsistent(cap);
            double expected = 2.0 * Math.Atan(Math.Tan(alpha / 2.0) / observer.Q);
            TestOptics.AssertRel(expected, cap.AngularRadius, 1e-12);
            double degrees = cap.AngularRadius * 180.0 / Math.PI;
            Assert.True(Math.Abs(degrees - 12.44082) < 1e-3, $"degrees {degrees:R}");
            Assert.True(cap.AngularRadius > alpha);
            TestOptics.AssertRel(-1.0, cap.Axis.X, 1e-12);
        }

        [Fact]
        public void EpsilonDerivedQ_RearCap_ExceedsHemisphere()
        {
            double epsilon = 0.01;
            double beta = (1.0 - epsilon * epsilon) / (1.0 + epsilon * epsilon);
            var observer = Observer(new OpticalVector(1.0, 0.0, 0.0), beta);
            TestOptics.AssertRel(epsilon, observer.Q, 1e-12);
            double alpha = 0.05;
            Assert.True(RelativisticOptics.TryTransformSphericalCap(new OpticalVector(-1.0, 0.0, 0.0), alpha, observer, out ObservedSphericalCap cap));
            AssertConsistent(cap);
            double expected = 2.0 * Math.Atan(Math.Tan(alpha / 2.0) / observer.Q);
            TestOptics.AssertRel(expected, cap.AngularRadius, 1e-12);
            Assert.True(cap.AngularRadius > Math.PI / 2.0);
            Assert.True(cap.CosAngularRadius < 0.0);
            TestOptics.AssertRel(-1.0, cap.Axis.X, 1e-12);
        }

        [Fact]
        public void ActualMaxSpeedClamp_RearAndOffAxisCaps_FiniteUnitCaps()
        {
            double maxSpeed = Math.BitDecrement(TestOptics.C);
            Assert.True(RelativisticOptics.TryCreateVelocity(new OpticalVector(maxSpeed, 0.0, 0.0), out PresentationVelocity observer));
            Assert.False(observer.WasConstrained);
            double alpha = 0.05;

            Assert.True(RelativisticOptics.TryTransformSphericalCap(new OpticalVector(-1.0, 0.0, 0.0), alpha, observer, out ObservedSphericalCap rear));
            AssertConsistent(rear);
            Assert.True(rear.AngularRadius > Math.PI / 2.0);
            double rearExpected = 2.0 * Math.Atan(Math.Tan(alpha / 2.0) / observer.Q);
            Assert.True(Math.Abs(rear.AngularRadius - rearExpected) < 1e-9,
                $"radius {rear.AngularRadius:R} expected {rearExpected:R}");
            TestOptics.AssertRel(-1.0, rear.Axis.X, 1e-12);

            var poles = new[]
            {
                new OpticalVector(0.0, 0.0, 1.0),
                new OpticalVector(0.0, 1.0, 0.0),
                new OpticalVector(0.6, 0.8, 0.0),
            };
            foreach (var a in poles)
            {
                TangentBasis(a, out OpticalVector t1, out OpticalVector t2);
                Assert.True(RelativisticOptics.TryTransformSphericalCap(a, alpha, observer, out ObservedSphericalCap cap));
                AssertConsistent(cap);
                for (int i = 0; i < 16; i++)
                {
                    double phi = 2.0 * Math.PI * i / 16.0;
                    Assert.True(RelativisticOptics.TryForward(BoundaryDirection(a, alpha, t1, t2, phi), observer, out OpticalRay ray));
                    double residual = Math.Abs(ray.Direction.Dot(cap.Axis) - cap.CosAngularRadius);
                    Assert.True(residual < 1e-10, $"pole residual {residual:R}");
                }
            }
        }

        [Fact]
        public void TinyRearCap_AtMaxSpeed_PreservesNonzeroRadius()
        {
            double maxSpeed = Math.BitDecrement(TestOptics.C);
            Assert.True(RelativisticOptics.TryCreateVelocity(new OpticalVector(maxSpeed, 0.0, 0.0), out PresentationVelocity observer));
            double alpha = 1e-12;
            Assert.True(RelativisticOptics.TryTransformSphericalCap(new OpticalVector(-1.0, 0.0, 0.0), alpha, observer, out ObservedSphericalCap cap));
            AssertConsistent(cap);
            double expected = 2.0 * Math.Atan(Math.Tan(alpha / 2.0) / observer.Q);
            Assert.True(cap.AngularRadius > 0.0);
            Assert.True(cap.SinAngularRadius > 0.0);
            TestOptics.AssertRel(expected, cap.AngularRadius, 1e-6);
            TestOptics.AssertRel(-1.0, cap.Axis.X, 1e-12);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(-0.1)]
        [InlineData(-1.0)]
        [InlineData(Math.PI / 2.0 + 1e-9)]
        [InlineData(Math.PI)]
        public void InvalidRadius_RejectedWithDefaultCap(double radius)
        {
            var observer = Observer(U, 0.5);
            Assert.False(RelativisticOptics.TryTransformSphericalCap(new OpticalVector(0.6, 0.8, 0.0), radius, observer, out ObservedSphericalCap cap));
            AssertDefault(cap);
        }

        [Fact]
        public void BoundaryRadii_ZeroAndHemisphere_Accepted()
        {
            var observer = Observer(U, 0.5);
            Assert.True(RelativisticOptics.TryTransformSphericalCap(new OpticalVector(0.6, 0.8, 0.0), 0.0, observer, out _));
            Assert.True(RelativisticOptics.TryTransformSphericalCap(new OpticalVector(0.6, 0.8, 0.0), Math.PI / 2.0, observer, out _));
        }

        [Theory]
        [InlineData(0.0, 0.0, 0.0)]
        [InlineData(double.NaN, 0.0, 0.0)]
        [InlineData(0.0, double.PositiveInfinity, 0.0)]
        [InlineData(double.NegativeInfinity, double.NegativeInfinity, double.NaN)]
        public void InvalidCenterDirection_RejectedWithDefaultCap(double x, double y, double z)
        {
            var observer = Observer(U, 0.5);
            Assert.False(RelativisticOptics.TryTransformSphericalCap(new OpticalVector(x, y, z), 0.3, observer, out ObservedSphericalCap cap));
            AssertDefault(cap);
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
                new PresentationVelocity(TestOptics.C, consistentQ, unit, false),
                new PresentationVelocity(2.0 * TestOptics.C, consistentQ, unit, false),
                new PresentationVelocity(-1.0, consistentQ, unit, false),
                new PresentationVelocity(double.NaN, consistentQ, unit, false),
                new PresentationVelocity(1e8, consistentQ, OpticalVector.Zero, false),
                new PresentationVelocity(1e8, consistentQ, new OpticalVector(2.0, 0.0, 0.0), false),
            };
            foreach (var observer in bad)
            {
                bool ok = true;
                ObservedSphericalCap cap = default;
                var ex = Record.Exception(() =>
                {
                    ok = RelativisticOptics.TryTransformSphericalCap(unit, 0.3, observer, out cap);
                });
                Assert.Null(ex);
                Assert.False(ok);
                AssertDefault(cap);
            }
        }

        [Fact]
        public void SubnormalSpeedObserver_QOne_TreatedAsRestIdentity()
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(new OpticalVector(double.Epsilon, 0.0, 0.0), out PresentationVelocity observer));
            Assert.True(observer.Speed > 0.0);
            Assert.Equal(1.0, observer.Q);
            var center = new OpticalVector(0.0, 3.0, 4.0);
            Assert.True(RelativisticOptics.TryTransformSphericalCap(center, 0.4, observer, out ObservedSphericalCap cap));
            Assert.Equal(0.0, cap.Axis.X);
            TestOptics.AssertRel(0.6, cap.Axis.Y, 1e-15);
            TestOptics.AssertRel(0.8, cap.Axis.Z, 1e-15);
            Assert.Equal(0.4, cap.AngularRadius);
            Assert.Equal(Math.Cos(0.4), cap.CosAngularRadius);
            Assert.Equal(Math.Sin(0.4), cap.SinAngularRadius);
        }

        private static ObservedSphericalCap Cap(OpticalVector axis, double angularRadius) =>
            new(axis, angularRadius, Math.Cos(angularRadius), Math.Sin(angularRadius));

        [Fact]
        public void IntersectsViewCone_AlignedSmallCap_Hit()
        {
            var axis = new OpticalVector(0.0, 0.0, 1.0);
            Assert.True(Cap(axis, 0.2).IntersectsViewCone(axis, 0.3));
        }

        [Fact]
        public void IntersectsViewCone_AntipodalSmallCap_Miss()
        {
            var cap = Cap(new OpticalVector(0.0, 0.0, -1.0), 0.2);
            Assert.False(cap.IntersectsViewCone(new OpticalVector(0.0, 0.0, 1.0), 0.3));
        }

        [Fact]
        public void IntersectsViewCone_TangentEdge_Included()
        {
            var cap = Cap(new OpticalVector(Math.Sin(0.5), 0.0, Math.Cos(0.5)), 0.2);
            Assert.True(cap.IntersectsViewCone(new OpticalVector(0.0, 0.0, 1.0), 0.3));
        }

        [Fact]
        public void IntersectsViewCone_JustOutside_Miss()
        {
            var cap = Cap(new OpticalVector(Math.Sin(0.5001), 0.0, Math.Cos(0.5001)), 0.2);
            Assert.False(cap.IntersectsViewCone(new OpticalVector(0.0, 0.0, 1.0), 0.3));
        }

        [Fact]
        public void IntersectsViewCone_GiantCap_HitRegardlessOfAxis()
        {
            var coneAxis = new OpticalVector(0.0, 0.0, 1.0);
            Assert.True(Cap(coneAxis, 2.8).IntersectsViewCone(coneAxis, 0.4));
            Assert.True(Cap(new OpticalVector(0.0, 0.0, -1.0), 2.8).IntersectsViewCone(coneAxis, 0.4));
        }

        [Fact]
        public void IntersectsViewCone_FacingAwayCap_LimbOverlapHit()
        {
            var cap = Cap(new OpticalVector(Math.Sin(2.4), 0.0, Math.Cos(2.4)), 2.0);
            Assert.True(cap.IntersectsViewCone(new OpticalVector(0.0, 0.0, 1.0), 0.5));
        }
    }
}

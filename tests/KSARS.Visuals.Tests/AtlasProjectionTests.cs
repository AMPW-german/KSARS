using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class AtlasProjectionTests
    {
        private static OpticalVector RotateAboutY(OpticalVector v) => new(v.Z, v.Y, -v.X);

        [Fact]
        public void SourceCenter_MapsToHalfUV()
        {
            var position = new OpticalVector(0.6e6, 0.3e6, 0.5e6);
            Assert.True(AtlasProjection.TryCreate(position, 5e4, 512, 4, out AtlasProjection projection));
            Assert.True(projection.TryProjectRestRay(position, out double u, out double v));
            TestOptics.AssertRel(0.5, u, 1e-15);
            TestOptics.AssertRel(0.5, v, 1e-15);
        }

        [Theory]
        [InlineData(512, 4)]
        [InlineData(64, 8)]
        [InlineData(9, 3)]
        public void RestSphereTangentBoundaryRays_MapInsideGuardRegion(int tileSize, int guard)
        {
            var position = new OpticalVector(-0.4e7, 0.2e7, 0.9e7);
            double radius = 3e5;
            Assert.True(AtlasProjection.TryCreate(position, radius, tileSize, guard, out AtlasProjection projection));

            double distance = projection.Distance;
            var forward = projection.Forward;
            var right = projection.Right;
            var up = projection.Up;
            double alpha = Math.Asin(radius / distance);
            double limit = (double)guard / tileSize;

            for (int i = 0; i < 64; i++)
            {
                double phi = 2.0 * Math.PI * i / 64.0;
                var tangent = right * Math.Cos(phi) + up * Math.Sin(phi);
                var ray = forward * Math.Cos(alpha) + tangent * Math.Sin(alpha);
                Assert.True(projection.TryProjectRestRay(ray, out double u, out double v), $"i={i} rejected");
                Assert.True(u >= limit - 1e-12 && u <= 1.0 - limit + 1e-12, $"i={i} u={u:R}");
                Assert.True(v >= limit - 1e-12 && v <= 1.0 - limit + 1e-12, $"i={i} v={v:R}");
            }
        }

        [Fact]
        public void SourceBehindNominalMinusZCamera_StillProjectsToCenter()
        {
            var position = new OpticalVector(0.0, 0.0, 1.0e8);
            Assert.True(AtlasProjection.TryCreate(position, 1e5, 512, 4, out AtlasProjection projection));
            TestOptics.AssertRel(1.0, projection.Forward.Z, 1e-15);
            Assert.True(projection.TryProjectRestRay(position, out double u, out double v));
            Assert.Equal(0.5, u);
            Assert.Equal(0.5, v);
        }

        [Fact]
        public void RotationAboutReferenceUp_PreservesProjectedUV()
        {
            var position = new OpticalVector(0.7e7, 0.15e7, -0.6e7);
            Assert.True(AtlasProjection.TryCreate(position, 2e5, 512, 4, out AtlasProjection first));
            Assert.True(AtlasProjection.TryCreate(RotateAboutY(position), 2e5, 512, 4, out AtlasProjection second));
            var ray = first.Forward
                + first.Right * (0.4 * first.TanHalfFov)
                + first.Up * (0.1 * first.TanHalfFov);

            Assert.True(first.TryProjectRestRay(ray, out double u1, out double v1));
            Assert.True(second.TryProjectRestRay(RotateAboutY(ray), out double u2, out double v2));
            TestOptics.AssertRel(u1, u2, 1e-14);
            TestOptics.AssertRel(v1, v2, 1e-14);
            double axisError = Math.Sqrt((second.Forward - RotateAboutY(first.Forward)).LengthSquared());
            double rightError = Math.Sqrt((second.Right - RotateAboutY(first.Right)).LengthSquared());
            double upError = Math.Sqrt((second.Up - RotateAboutY(first.Up)).LengthSquared());
            Assert.True(axisError < 1e-12, $"forward covariance {axisError:R}");
            Assert.True(rightError < 1e-12, $"right covariance {rightError:R}");
            Assert.True(upError < 1e-12, $"up covariance {upError:R}");
        }

        [Fact]
        public void SameDirectionDifferentDistance_EyeNotRelocated()
        {
            var near = new OpticalVector(1.0e7, -0.3e7, 0.8e7);
            var far = near * 1.0e4;
            Assert.True(AtlasProjection.TryCreate(near, 1e5, 512, 4, out AtlasProjection close));
            Assert.True(AtlasProjection.TryCreate(far, 1e5 * 1.0e4, 512, 4, out AtlasProjection distant));
            Assert.True(close.TryProjectRestRay(near, out double u1, out double v1));
            Assert.True(distant.TryProjectRestRay(far, out double u2, out double v2));
            Assert.True(Math.Abs(u1 - 0.5) < 1e-13, $"u1 {u1:R}");
            Assert.True(Math.Abs(u2 - 0.5) < 1e-13, $"u2 {u2:R}");
            Assert.True(Math.Abs(v1 - 0.5) < 1e-13, $"v1 {v1:R}");
            Assert.True(Math.Abs(v2 - 0.5) < 1e-13, $"v2 {v2:R}");
            double forwardError = Math.Sqrt((close.Forward - distant.Forward).LengthSquared());
            Assert.True(forwardError < 1e-12, $"forward drift {forwardError:R}");
        }

        [Fact]
        public void ReconstructRadial_KnownOffAxisDepth()
        {
            var position = new OpticalVector(0.0, 0.0, 1.0e6);
            double radius = 3e4;
            Assert.True(AtlasProjection.TryCreate(position, radius, 512, 4, out AtlasProjection projection));

            double u = 0.7;
            double v = 0.3;
            double tanH = projection.TanHalfFov;
            var rayDirection = projection.Forward
                + projection.Right * ((2.0 * u - 1.0) * tanH)
                + projection.Up * ((1.0 - 2.0 * v) * tanH);
            Assert.True(projection.TryProjectRestRay(rayDirection, out double uOut, out double vOut));
            TestOptics.AssertRel(u, uOut, 1e-12);
            TestOptics.AssertRel(v, vOut, 1e-12);

            double viewDepth = projection.Distance * 0.97;
            double near = projection.Near;
            double far = projection.Far;
            double depth = (near * far / (far - near)) / viewDepth - near / (far - near);
            Assert.True(depth > 0.0 && depth <= 1.0, $"depth {depth:R}");

            double expectedRadial = viewDepth * Math.Sqrt(rayDirection.LengthSquared() / rayDirection.Dot(projection.Forward) / rayDirection.Dot(projection.Forward));
            Assert.True(projection.TryReconstructRadialDistance(u, v, depth, out double radial));
            TestOptics.AssertRel(expectedRadial, radial, 1e-12);
        }

        [Fact]
        public void NearDepthOne_ReconstructsNearPlane_ClearedDepthRejected()
        {
            var position = new OpticalVector(0.0, 0.0, 1.0e6);
            Assert.True(AtlasProjection.TryCreate(position, 3e4, 512, 4, out AtlasProjection projection));
            Assert.True(projection.TryReconstructRadialDistance(0.5, 0.5, 1.0, out double radial));
            TestOptics.AssertRel(projection.Near, radial, 1e-12);
            Assert.False(projection.TryReconstructRadialDistance(0.5, 0.5, 0.0, out _));
        }

        [Fact]
        public void TinySubpixelCap_RemainsFiniteAndProjectable()
        {
            var position = new OpticalVector(0.0, 0.0, 1.0e9);
            Assert.True(AtlasProjection.TryCreate(position, 1.0, 512, 4, out AtlasProjection projection));
            Assert.True(double.IsFinite(projection.TanHalfFov));
            Assert.True(projection.TanHalfFov > 0.0);
            Assert.True(projection.RestAngularRadius > 0.0);
            Assert.True(projection.TryProjectRestRay(position, out double u, out double v));
            Assert.Equal(0.5, u);
            Assert.Equal(0.5, v);
            Assert.True(projection.TryReconstructRadialDistance(0.5, 0.5, 0.9, out double radial));
            Assert.True(double.IsFinite(radial) && radial > 0.0);
        }

        [Theory]
        [InlineData(0.0, 0.0, 0.0)]
        [InlineData(double.NaN, 0.0, 0.0)]
        [InlineData(0.0, double.PositiveInfinity, 0.0)]
        [InlineData(double.NegativeInfinity, 0.0, 0.0)]
        public void InvalidPosition_Rejected(double x, double y, double z)
        {
            var position = new OpticalVector(x, y, z);
            Assert.False(AtlasProjection.TryCreate(position, 3e4, 512, 4, out AtlasProjection projection));
            Assert.Equal(0.0, projection.Distance);
            Assert.Equal(0.0, projection.TanHalfFov);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void InvalidRadius_Rejected(double radius)
        {
            var position = new OpticalVector(0.0, 0.0, 1.0e6);
            Assert.False(AtlasProjection.TryCreate(position, radius, 512, 4, out _));
        }

        [Fact]
        public void ObserverInsideOrOnBound_Rejected()
        {
            var position = new OpticalVector(0.0, 0.0, 1.0e6);
            Assert.False(AtlasProjection.TryCreate(position, 1.0e6, 512, 4, out _));
            Assert.False(AtlasProjection.TryCreate(position, 1.5e6, 512, 4, out _));
            Assert.False(AtlasProjection.TryCreate(position, double.PositiveInfinity, 512, 4, out _));
        }

        [Theory]
        [InlineData(8, 3)]
        [InlineData(4, 1)]
        [InlineData(6, 2)]
        [InlineData(512, 0)]
        [InlineData(512, -1)]
        [InlineData(int.MaxValue, int.MaxValue)]
        [InlineData(512, 1073741824)]
        [InlineData(512, int.MaxValue)]
        [InlineData(-512, 4)]
        [InlineData(-1, 1)]
        public void InvalidTileOrGuard_Rejected(int tileSize, int guard)
        {
            var position = new OpticalVector(0.0, 0.0, 1.0e6);
            Assert.False(AtlasProjection.TryCreate(position, 3e4, tileSize, guard, out _));
        }

        [Theory]
        [InlineData(0.0, 0.0, 0.0)]
        [InlineData(double.NaN, 0.0, 0.0)]
        [InlineData(0.0, double.PositiveInfinity, 0.0)]
        public void InvalidProjectRay_Rejected(double x, double y, double z)
        {
            var position = new OpticalVector(0.0, 0.0, 1.0e6);
            Assert.True(AtlasProjection.TryCreate(position, 3e4, 512, 4, out AtlasProjection projection));
            Assert.False(projection.TryProjectRestRay(new OpticalVector(x, y, z), out double u, out double v));
            Assert.Equal(0.0, u);
            Assert.Equal(0.0, v);
        }

        [Fact]
        public void RayBehindOrOnHorizon_Rejected()
        {
            var position = new OpticalVector(0.0, 0.0, 1.0e6);
            Assert.True(AtlasProjection.TryCreate(position, 3e4, 512, 4, out AtlasProjection projection));
            Assert.False(projection.TryProjectRestRay(position * -1.0, out _, out _));
            Assert.False(projection.TryProjectRestRay(projection.Right, out _, out _));
        }

        [Fact]
        public void RayOutsideTile_Rejected()
        {
            var position = new OpticalVector(0.0, 0.0, 1.0e6);
            Assert.True(AtlasProjection.TryCreate(position, 3e4, 512, 4, out AtlasProjection projection));
            double alpha = Math.Asin(3e4 / projection.Distance);
            var ray = projection.Forward * Math.Cos(alpha * 4.0) + projection.Right * Math.Sin(alpha * 4.0);
            Assert.False(projection.TryProjectRestRay(ray, out _, out _));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.5)]
        [InlineData(1.5)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void InvalidDepth_Rejected(double depth)
        {
            var position = new OpticalVector(0.0, 0.0, 1.0e6);
            Assert.True(AtlasProjection.TryCreate(position, 3e4, 512, 4, out AtlasProjection projection));
            Assert.False(projection.TryReconstructRadialDistance(0.5, 0.5, depth, out double radial));
            Assert.Equal(0.0, radial);
        }

        [Theory]
        [InlineData(-0.1, 0.5)]
        [InlineData(1.1, 0.5)]
        [InlineData(0.5, -0.1)]
        [InlineData(0.5, 1.1)]
        public void OutsideTileUV_Rejected(double u, double v)
        {
            var position = new OpticalVector(0.0, 0.0, 1.0e6);
            Assert.True(AtlasProjection.TryCreate(position, 3e4, 512, 4, out AtlasProjection projection));
            Assert.False(projection.TryReconstructRadialDistance(u, v, 0.5, out _));
        }

        private static ObservedSphericalCap ForwardCap(double alpha) =>
            new(new OpticalVector(0.0, 0.0, 1.0), alpha, Math.Cos(alpha), Math.Sin(alpha));

        [Fact]
        public void ObservedPixelDiameter_OnAxisCap_MatchesReferenceBound()
        {
            const double alpha = 0.001;
            var cap = ForwardCap(alpha);
            Assert.True(AtlasProjection.TryGetObservedPixelDiameter(in cap,
                new OpticalVector(0.0, 0.0, 1.0), 1000.0, 1000.0, out double diameter));
            double expected = 2.0 * 1000.0 * Math.Tan(alpha) * (1.0 + 1e-6);
            TestOptics.AssertRel(expected, diameter, 1e-12);
        }

        [Fact]
        public void ObservedPixelDiameter_OffAxisCap_MatchesReferenceBoundAndExceedsOnAxis()
        {
            const double alpha = 0.001;
            var cap = ForwardCap(alpha);
            var viewAxis = new OpticalVector(Math.Sin(0.7), 0.0, Math.Cos(0.7));
            Assert.True(AtlasProjection.TryGetObservedPixelDiameter(in cap, viewAxis,
                1000.0, 1000.0, out double offAxis));
            Assert.True(AtlasProjection.TryGetObservedPixelDiameter(in cap,
                new OpticalVector(0.0, 0.0, 1.0), 1000.0, 1000.0, out double onAxis));

            double s = Math.Sin(alpha);
            double z = Math.Cos(0.7);
            double ax = Math.Sin(0.7);
            double den = z * z - s * s;
            double rx = z * z + ax * ax - s * s;
            double ry = z * z - s * s;
            double widthX = 1000.0 * (2.0 * s * Math.Sqrt(rx) / den);
            double widthY = 1000.0 * (2.0 * s * Math.Sqrt(ry) / den);
            double expected = Math.Max(widthX, widthY) * (1.0 + 1e-6);
            TestOptics.AssertRel(expected, offAxis, 1e-12);
            Assert.True(offAxis > onAxis, $"off-axis bound {offAxis:R} must exceed on-axis {onAxis:R}");
        }

        [Fact]
        public void ObservedPixelDiameter_ResolutionAndFovScale_DoubleBound()
        {
            var cap = ForwardCap(0.001);
            var axis = new OpticalVector(0.0, 0.0, 1.0);
            Assert.True(AtlasProjection.TryGetObservedPixelDiameter(in cap, axis,
                1000.0, 1000.0, out double baseline));
            Assert.True(AtlasProjection.TryGetObservedPixelDiameter(in cap, axis,
                2000.0, 1000.0, out double wideResolution));
            Assert.True(AtlasProjection.TryGetObservedPixelDiameter(in cap, axis,
                1000.0, 2000.0, out double tallFov));
            TestOptics.AssertRel(2.0 * baseline, wideResolution, 1e-12);
            TestOptics.AssertRel(2.0 * baseline, tallFov, 1e-12);
        }

        [Fact]
        public void ObservedPixelDiameter_TinyForwardTransformedCapAtHighBeta_SmallBound()
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(
                new OpticalVector(0.0, 0.0, 0.999999 * TestOptics.C), out PresentationVelocity boosted));
            Assert.True(RelativisticOptics.TryTransformSphericalCap(
                new OpticalVector(0.0, 0.0, 1.0), 0.001, boosted, out ObservedSphericalCap cap));
            Assert.True(AtlasProjection.TryGetObservedPixelDiameter(in cap, cap.Axis,
                1000.0, 1000.0, out double diameter));
            Assert.True(diameter <= 6.0, $"forward transformed cap should bound below the cutoff, got {diameter:R}");
            Assert.True(diameter < 2.0 * 1000.0 * Math.Tan(0.001),
                $"forward aberration must shrink the bound below the rest value, got {diameter:R}");
        }

        [Fact]
        public void ObservedPixelDiameter_RearwardTransformedGiantCap_NeverTiny()
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(
                new OpticalVector(0.0, 0.0, 0.999999 * TestOptics.C), out PresentationVelocity boosted));
            Assert.True(RelativisticOptics.TryTransformSphericalCap(
                new OpticalVector(0.0, 0.0, -1.0), 0.001, boosted, out ObservedSphericalCap cap));
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in cap, cap.Axis,
                1000.0, 1000.0, out _),
                "a rearward transformed cap must remain unsafe to bound, never tiny");

            var giantAhead = ForwardCap(1.0);
            Assert.True(AtlasProjection.TryGetObservedPixelDiameter(in giantAhead,
                new OpticalVector(0.0, 0.0, 1.0), 1000.0, 1000.0, out double giant));
            Assert.True(giant > 6.0, $"large bounded caps must report their size, got {giant:R}");
        }

        [Fact]
        public void ObservedPixelDiameter_InvalidInputs_Rejected()
        {
            var axis = new OpticalVector(0.0, 0.0, 1.0);
            var horizonCap = ForwardCap(Math.PI / 2.0);
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in horizonCap, axis, 1000.0, 1000.0, out _));
            var behindCap = ForwardCap(0.6 * Math.PI);
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in behindCap, axis, 1000.0, 1000.0, out _));
            var negativeCap = new ObservedSphericalCap(new OpticalVector(0.0, 0.0, 1.0), -0.1, 1.0, -0.1);
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in negativeCap, axis, 1000.0, 1000.0, out _));
            var unitCap = new ObservedSphericalCap(new OpticalVector(0.0, 0.0, 1.0), 0.5, 0.0, 1.0);
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in unitCap, axis, 1000.0, 1000.0, out _));
            var nanCap = new ObservedSphericalCap(new OpticalVector(0.0, 0.0, 1.0), double.NaN, double.NaN, double.NaN);
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in nanCap, axis, 1000.0, 1000.0, out _));

            var cap = ForwardCap(0.001);
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in cap, new OpticalVector(1.0, 0.0, 0.0), 1000.0, 1000.0, out _),
                "horizon cap axis must be rejected");
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in cap, new OpticalVector(1.0, 0.0, 0.0009), 1000.0, 1000.0, out _),
                "axis forward component within the horizon margin must be rejected");
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in cap, new OpticalVector(0.0, 0.0, -1.0), 1000.0, 1000.0, out _));
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in cap, OpticalVector.Zero, 1000.0, 1000.0, out _));
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in cap,
                new OpticalVector(double.NaN, 0.0, 1.0), 1000.0, 1000.0, out _));
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in cap, axis, 0.0, 1000.0, out _));
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in cap, axis, -1.0, 1000.0, out _));
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in cap, axis, double.NaN, 1000.0, out _));
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in cap, axis, double.PositiveInfinity, 1000.0, out _));
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in cap, axis, 1000.0, 0.0, out _));
            Assert.False(AtlasProjection.TryGetObservedPixelDiameter(in cap, axis, 1000.0, double.NaN, out _));
        }
    }
}

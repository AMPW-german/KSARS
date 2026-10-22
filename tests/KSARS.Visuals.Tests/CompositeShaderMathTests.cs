using System.Numerics;
using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    // Replicates the AtlasComposite fragment shader math in float and compares it against the
    // double-precision RelativisticOptics reference. This pins the GPU formula to the CPU
    // implementation and documents where single precision degrades (the aberration fold).
    public class CompositeShaderMathTests
    {
        private static (Vector3 RestDir, float Doppler) ShaderInverse(Vector3 nObs, Vector3 u, float q)
        {
            Vector3 nRest = nObs;
            float doppler = 1.0f;
            if (q != 1.0f)
            {
                float qq = q * q;
                float a = 0.5f * Vector3.Dot(nObs - u, nObs - u);
                float b = 0.5f * Vector3.Dot(nObs + u, nObs + u);
                Vector3 nPerp = nObs - Vector3.Dot(nObs, u) * u;
                nRest = Vector3.Normalize(u * (qq * b - a) + nPerp * (2f * q));
                doppler = 2f * q / (a + qq * b);
            }

            return (nRest, doppler);
        }

        private static double AngularError(Vector3 a, OpticalVector b)
        {
            var ad = new OpticalVector(a.X, a.Y, a.Z);
            double dot = Math.Clamp(ad.Dot(b) / Math.Sqrt(ad.LengthSquared() * b.LengthSquared()), -1.0, 1.0);
            return Math.Acos(dot);
        }

        private static IEnumerable<Vector3> Directions(int seed)
        {
            var rng = new Random(seed);
            for (int i = 0; i < 4096; i++)
            {
                double z = rng.NextDouble() * 2.0 - 1.0;
                double phi = rng.NextDouble() * Math.PI * 2.0;
                double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
                yield return Vector3.Normalize(new Vector3(
                    (float)(r * Math.Cos(phi)), (float)(r * Math.Sin(phi)), (float)z));
            }
        }

        private static PresentationVelocity Observer(OpticalVector direction, double beta)
        {
            Assert.True(RelativisticOptics.TryCreateVelocity(direction * (beta * TestOptics.C), out PresentationVelocity observer));
            return observer;
        }

        private static (double DirError, double DopplerRelError) MaxErrors(
            PresentationVelocity observer, Vector3 u, int seed)
        {
            double dirErr = 0.0, dopErr = 0.0;
            foreach (Vector3 n in Directions(seed))
            {
                var nd = new OpticalVector(n.X, n.Y, n.Z);
                Assert.True(RelativisticOptics.TryInverse(nd, observer, out OpticalRay expected));
                (Vector3 restDir, float doppler) = ShaderInverse(n, u, (float)observer.Q);
                dirErr = Math.Max(dirErr, AngularError(restDir, expected.Direction));
                dopErr = Math.Max(dopErr, Math.Abs(doppler / expected.DopplerFactor - 1.0));
            }

            return (dirErr, dopErr);
        }

        [Theory]
        [InlineData(0.5, 1e-5, 1e-5)]
        [InlineData(0.9, 1e-4, 1e-5)]
        [InlineData(0.99, 1e-3, 1e-4)]
        public void ShaderInverse_MatchesDoubleReference(double beta, double dirBound, double dopBound)
        {
            var uDir = new OpticalVector(0.3, -0.5, 0.8);
            PresentationVelocity observer = Observer(uDir, beta);
            var u = Vector3.Normalize(new Vector3((float)uDir.X, (float)uDir.Y, (float)uDir.Z));
            (double dirErr, double dopErr) = MaxErrors(observer, u, 7);
            Assert.True(dirErr < dirBound, $"beta={beta} dirErr={dirErr:E3} bound={dirBound:E3}");
            Assert.True(dopErr < dopBound, $"beta={beta} dopErr={dopErr:E3} bound={dopBound:E3}");
        }

        [Fact]
        public void ShaderInverse_ExtremeSpeed_BoundedAtAberrationFold()
        {
            // The fold direction (where the rest-direction coefficient cancels) is ill-conditioned
            // in float; record the degradation rather than pretending it is exact.
            var uDir = new OpticalVector(1.0, 0.0, 0.0);
            PresentationVelocity observer = Observer(uDir, 0.9999);
            var u = new Vector3(1.0f, 0.0f, 0.0f);
            (double dirErr, double dopErr) = MaxErrors(observer, u, 11);
            Assert.True(dirErr < 0.05, $"extreme dirErr={dirErr:E3}");
            Assert.True(dopErr < 0.05, $"extreme dopErr={dopErr:E3}");
        }

        [Fact]
        public void ShaderInverse_CardinalDirections_Exact()
        {
            var u = new Vector3(0.0f, 1.0f, 0.0f);
            const float q = 0.25f;
            (Vector3 forward, float dForward) = ShaderInverse(u, u, q);
            (Vector3 rear, float dRear) = ShaderInverse(-u, u, q);
            Assert.Equal(u, forward);
            Assert.Equal(-u, rear);
            Assert.Equal(1.0f / q, dForward, 5);
            Assert.Equal(q, dRear, 5);
        }

        [Fact]
        public void ShaderInverse_RestObserver_DirectionIdentityAndUnitDoppler()
        {
            var zero = Vector3.Zero;
            foreach (Vector3 n in new[]
            {
                new Vector3(0f, 0f, -1f),
                Vector3.Normalize(new Vector3(0.6f, -0.5f, 0.62f)),
                Vector3.Normalize(new Vector3(-0.9f, 0.2f, -0.38f)),
            })
            {
                (Vector3 rest, float doppler) = ShaderInverse(n, zero, 1.0f);
                Assert.Equal(n, rest);
                Assert.Equal(1.0f, doppler);
            }

            var normalizedU = Vector3.Normalize(new Vector3(1f, 2f, 3f));
            (Vector3 restDir, float d) = ShaderInverse(new Vector3(0f, 0f, -1f), normalizedU, 1.0f);
            Assert.Equal(new Vector3(0f, 0f, -1f), restDir);
            Assert.Equal(1.0f, d);
        }

        [Fact]
        public void ShaderInverse_PerpendicularDirection_MatchesReferenceSign()
        {
            var uDir = new OpticalVector(1.0, 0.0, 0.0);
            PresentationVelocity observer = Observer(uDir, 0.8);
            var nObs = new Vector3(0.0f, 0.0f, 1.0f);
            Assert.True(RelativisticOptics.TryInverse(new OpticalVector(0.0, 0.0, 1.0), observer, out OpticalRay expected));
            (Vector3 restDir, float doppler) = ShaderInverse(nObs, new Vector3(1.0f, 0.0f, 0.0f), (float)observer.Q);
            Assert.True(AngularError(restDir, expected.Direction) < 1e-5);
            Assert.Equal(expected.DopplerFactor, doppler, 5);
        }

        private const float PerspectiveFovRadians = (float)(60.0 * Math.PI / 180.0);
        private const float PerspectiveAspect = 16f / 9f;

        private static Matrix4x4 NativeReverseZPerspective(float near, float far)
        {
            float cot = 1f / MathF.Tan(PerspectiveFovRadians * 0.5f);
            var projection = Matrix4x4.Identity;
            projection.M11 = cot / PerspectiveAspect;
            projection.M22 = -cot;
            projection.M33 = near / (far - near);
            projection.M34 = -1f;
            projection.M43 = near * far / (far - near);
            projection.M44 = 0f;
            return projection;
        }

        private static Vector3 ViewDirNearPlane(Vector2 uv, Matrix4x4 inverseProjection)
        {
            var ndc = new Vector4(uv * 2f - Vector2.One, 1f, 1f);
            Vector4 viewPos = Vector4.Transform(ndc, inverseProjection);
            return Vector3.Normalize(new Vector3(viewPos.X, viewPos.Y, viewPos.Z));
        }

        private static Vector3 ViewDirFarPlaneDivided(Vector2 uv, Matrix4x4 inverseProjection)
        {
            var ndc = new Vector4(uv * 2f - Vector2.One, 0f, 1f);
            Vector4 viewPos = Vector4.Transform(ndc, inverseProjection);
            return Vector3.Normalize(new Vector3(viewPos.X, viewPos.Y, viewPos.Z) / viewPos.W);
        }

        private static Vector2 ProjectToUv(Vector3 viewDirection, Matrix4x4 projection)
        {
            var clip = Vector4.Transform(new Vector4(viewDirection / -viewDirection.Z, 1f), projection);
            var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
            return new Vector2(ndc.X * 0.5f + 0.5f, ndc.Y * 0.5f + 0.5f);
        }

        private static readonly (Vector2 Uv, int XSign, int YSign)[] UnprojectSamples =
        {
            (new Vector2(0.50f, 0.50f), 0, 0),
            (new Vector2(0.62f, 0.35f), 1, 1),
            (new Vector2(0.25f, 0.25f), -1, 1),
            (new Vector2(0.75f, 0.25f), 1, 1),
            (new Vector2(0.25f, 0.75f), -1, -1),
            (new Vector2(0.75f, 0.75f), 1, -1),
        };

        [Theory]
        [InlineData(1e-2)]
        [InlineData(1e-9)]
        [InlineData(1e-20)]
        public void Unproject_NearPlaneRay_ReproducesUvAcrossNearFarRatios(double nearOverFar)
        {
            Matrix4x4 projection = NativeReverseZPerspective(1f, (float)(1.0 / nearOverFar));
            Assert.True(Matrix4x4.Invert(projection, out Matrix4x4 inverseProjection),
                $"single-precision inverse failed at near/far {nearOverFar:E0}");

            Vector3 center = ViewDirNearPlane(new Vector2(0.5f), inverseProjection);
            Assert.Equal(0f, center.X, 4);
            Assert.Equal(0f, center.Y, 4);
            Assert.Equal(-1f, center.Z, 4);

            foreach ((Vector2 uv, int xSign, int ySign) in UnprojectSamples)
            {
                Vector3 dir = ViewDirNearPlane(uv, inverseProjection);
                Assert.True(dir.Z < 0f, $"uv={uv} dir={dir} must face camera -Z");
                if (xSign != 0)
                {
                    Assert.Equal(xSign, Math.Sign(dir.X));
                }
                if (ySign != 0)
                {
                    Assert.Equal(ySign, Math.Sign(dir.Y));
                }
                Vector2 roundTrip = ProjectToUv(dir, projection);
                Assert.Equal(uv.X, roundTrip.X, 4);
                Assert.Equal(uv.Y, roundTrip.Y, 4);
            }
        }

        [Fact]
        public void Unproject_InverseView_IdentityYawAndRoll()
        {
            Matrix4x4 projection = NativeReverseZPerspective(1f, 1e9f);
            Assert.True(Matrix4x4.Invert(projection, out Matrix4x4 inverseProjection));

            Vector3 center = ViewDirNearPlane(new Vector2(0.5f), inverseProjection);
            Vector3 identity = Vector3.TransformNormal(center, Matrix4x4.Identity);
            Assert.Equal(0f, identity.X, 4);
            Assert.Equal(0f, identity.Y, 4);
            Assert.Equal(-1f, identity.Z, 4);

            Matrix4x4 yaw180 = Matrix4x4.CreateFromYawPitchRoll(MathF.PI, 0f, 0f);
            Vector3 yawed = Vector3.TransformNormal(center, yaw180);
            Assert.Equal(0f, yawed.X, 3);
            Assert.Equal(0f, yawed.Y, 4);
            Assert.Equal(1f, yawed.Z, 3);

            Matrix4x4 roll180 = Matrix4x4.CreateRotationZ(MathF.PI);
            var topLeft = new Vector2(0.25f, 0.25f);
            var bottomRight = new Vector2(0.75f, 0.75f);
            Vector3 rolledTopLeft = Vector3.TransformNormal(ViewDirNearPlane(topLeft, inverseProjection), roll180);
            Vector3 flatBottomRight = ViewDirNearPlane(bottomRight, inverseProjection);
            Assert.Equal(flatBottomRight.X, rolledTopLeft.X, 4);
            Assert.Equal(flatBottomRight.Y, rolledTopLeft.Y, 4);
            Assert.Equal(flatBottomRight.Z, rolledTopLeft.Z, 4);
            Assert.True(rolledTopLeft.Z < 0f);
        }

        [Fact]
        public void Unproject_DegradedFarHomogeneousW_OldDivideFails_NearStays()
        {
            Matrix4x4 projection = NativeReverseZPerspective(1f, 1e9f);
            Assert.True(Matrix4x4.Invert(projection, out Matrix4x4 inverseProjection));

            var reversed = inverseProjection;
            reversed.M44 = -1e-10f;

            Vector3 oldCenter = ViewDirFarPlaneDivided(new Vector2(0.5f), reversed);
            Vector3 newCenter = ViewDirNearPlane(new Vector2(0.5f), reversed);
            Assert.True(oldCenter.Z > 0f, $"old xyz/w far ray must reverse to +Z, got {oldCenter}");
            Assert.True(newCenter.Z < 0f);
            Assert.True(Vector3.Dot(oldCenter, newCenter) < -0.99f,
                "old divided ray is a 180-degree reversal of the homogeneous near ray");

            Vector3 oldCorner = ViewDirFarPlaneDivided(new Vector2(0.25f, 0.25f), reversed);
            Vector3 newCorner = ViewDirNearPlane(new Vector2(0.25f, 0.25f), reversed);
            Assert.True(Vector3.Dot(oldCorner, newCorner) < -0.99f);

            var collapsed = inverseProjection;
            collapsed.M44 = 1e-30f;
            Vector3 oldCollapsed = ViewDirFarPlaneDivided(new Vector2(0.5f), collapsed);
            Assert.Equal(0f, oldCollapsed.LengthSquared());
            Assert.Equal(-1f, ViewDirNearPlane(new Vector2(0.5f), collapsed).Z, 4);
        }

        [Theory]
        [InlineData(1e-2)]
        [InlineData(1e-9)]
        [InlineData(1e-20)]
        public void Unproject_RealInverse_FarWDeterminesOldDivideDirection(double nearOverFar)
        {
            Matrix4x4 projection = NativeReverseZPerspective(1f, (float)(1.0 / nearOverFar));
            Assert.True(Matrix4x4.Invert(projection, out Matrix4x4 inverseProjection));

            Vector4 farCenter = Vector4.Transform(new Vector4(0f, 0f, 0f, 1f), inverseProjection);
            Vector3 oldDir = Vector3.Normalize(
                new Vector3(farCenter.X, farCenter.Y, farCenter.Z) / farCenter.W);
            Vector3 newDir = ViewDirNearPlane(new Vector2(0.5f), inverseProjection);
            Assert.True(newDir.Z < 0f);

            bool degenerateFar = farCenter.W <= 0f || MathF.Abs(farCenter.W) < 1e-19f;
            if (degenerateFar)
            {
                bool broke = oldDir.Z >= 0f || oldDir.LengthSquared() < 0.5f || !float.IsFinite(oldDir.Z);
                Assert.True(broke,
                    $"far w={farCenter.W:E3} at n/f={nearOverFar:E0}: expected old xyz/w to break, got {oldDir}");
            }
            else
            {
                Assert.True(oldDir.Z < 0f,
                    $"far w={farCenter.W:E3} at n/f={nearOverFar:E0}: healthy w must keep -Z, got {oldDir}");
                Assert.True(Vector3.Dot(oldDir, newDir) > 0.999f);
            }
        }

        [Fact]
        public void RadialReconstruct_CenterDepthOne_IsNearPlaneDistance()
        {
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, -1000.0), 10.0, 512, 8,
                out AtlasProjection projection));
            Assert.True(projection.TryReconstructRadialDistance(0.5, 0.5, 1.0, out double radial));
            Assert.Equal(projection.Near, radial, 9);
        }

        [Fact]
        public void RadialReconstruct_TinyDepth_ApproachesFarPlane()
        {
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, -1000.0), 10.0, 512, 8,
                out AtlasProjection projection));
            Assert.True(projection.TryReconstructRadialDistance(0.5, 0.5, 1e-7, out double radial));
            Assert.InRange(Math.Abs(radial / projection.Far - 1.0), 0.0, 1e-3);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.25)]
        [InlineData(1.0000001)]
        [InlineData(double.NaN)]
        public void RadialReconstruct_RejectsDepthOutsideDrawnCoverage(double depth)
        {
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, -1000.0), 10.0, 512, 8,
                out AtlasProjection projection));
            Assert.False(projection.TryReconstructRadialDistance(0.5, 0.5, depth, out _));
        }

        [Fact]
        public void RadialReconstruct_OffCenterRayIsLongerThanAxis()
        {
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, -1000.0), 10.0, 512, 8,
                out AtlasProjection projection));
            Assert.True(projection.TryReconstructRadialDistance(0.5, 0.5, 0.5, out double center));
            Assert.True(projection.TryReconstructRadialDistance(0.9, 0.9, 0.5, out double corner));
            Assert.True(corner > center);
            Assert.False(projection.TryReconstructRadialDistance(-0.1, 0.5, 0.5, out _));
            Assert.False(projection.TryReconstructRadialDistance(0.5, 1.1, 0.5, out _));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.9)]
        [InlineData(0.9998)]
        public void ExtendedRearwardBody_ForwardThenShaderInverse_PreservesTileCoordinates(double beta)
        {
            var position = new OpticalVector(600.0, 0.0, 800.0);
            Assert.True(AtlasProjection.TryCreate(position, 50.0, 512, 8, out AtlasProjection tile));
            var observer = Observer(new OpticalVector(0.0, 0.0, -1.0), beta);
            Assert.True(tile.Forward.Z > 0.0, "source starts behind a nominal -Z camera");
            Assert.True(RelativisticOptics.TryForward(tile.Forward, observer, out OpticalRay center));
            if (beta >= 0.9)
                Assert.True(center.Direction.Z < 0.0, "aberration must bring this rearward source forward");

            CheckTileRoundTrip(tile, observer);
        }

        [Fact]
        public void GreaterThanHemisphereBody_ShaderInverse_PreservesExtendedTileMapping()
        {
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, 1000.0),
                50.0, 512, 8, out AtlasProjection tile));
            var observer = Observer(new OpticalVector(0.0, 0.0, -1.0), 0.9998);
            Assert.True(RelativisticOptics.TryTransformSphericalCap(tile.Forward,
                tile.RestAngularRadius, observer, out ObservedSphericalCap cap));
            Assert.True(cap.AngularRadius > Math.PI / 2.0);
            CheckTileRoundTrip(tile, observer);
        }

        private static void CheckTileRoundTrip(AtlasProjection tile, PresentationVelocity observer)
        {
            var axis = new Vector3((float)observer.Direction.X,
                (float)observer.Direction.Y, (float)observer.Direction.Z);
            foreach (double fraction in new[] { 0.0, 0.5, 0.95 })
            {
                for (int azimuth = 0; azimuth < 16; azimuth++)
                {
                    double phi = azimuth * (2.0 * Math.PI / 16.0);
                    double angle = fraction * tile.RestAngularRadius;
                    OpticalVector rest = tile.Forward * Math.Cos(angle)
                        + (tile.Right * Math.Cos(phi) + tile.Up * Math.Sin(phi)) * Math.Sin(angle);
                    Assert.True(tile.TryProjectRestRay(rest, out double expectedU, out double expectedV));
                    Assert.True(RelativisticOptics.TryForward(rest, observer, out OpticalRay observed));
                    var input = Vector3.Normalize(new Vector3((float)observed.Direction.X,
                        (float)observed.Direction.Y, (float)observed.Direction.Z));
                    (Vector3 actual, _) = ShaderInverse(input, axis, (float)observer.Q);
                    Assert.True(tile.TryProjectRestRay(new OpticalVector(actual.X, actual.Y, actual.Z),
                        out double actualU, out double actualV));
                    // Analytic rays, without texture sampling: a quarter of a 512px source texel.
                    Assert.InRange(Math.Abs(actualU - expectedU), 0.0, 0.25 / 512.0);
                    Assert.InRange(Math.Abs(actualV - expectedV), 0.0, 0.25 / 512.0);
                }
            }
        }

        [Fact]
        public void PhysicalDisjointSpheres_DepthReconstructionPreservesNearestSurfaceAcrossOverlap()
        {
            var centers = new[]
            {
                new OpticalVector(0.0, 0.0, 1000.0),
                new OpticalVector(15.0, 0.0, 1500.0),
            };
            double[] radii = { 50.0, 100.0 };
            Assert.True((centers[1] - centers[0]).LengthSquared()
                > (radii[0] + radii[1]) * (radii[0] + radii[1]));
            var tiles = new AtlasProjection[2];
            for (int i = 0; i < tiles.Length; i++)
                Assert.True(AtlasProjection.TryCreate(centers[i], radii[i], 512, 8, out tiles[i]));

            foreach (double x in new[] { -0.03, 0.0, 0.03 })
            foreach (double y in new[] { -0.02, 0.0, 0.02 })
            {
                Assert.True(RelativisticOptics.TryNormalize(new OpticalVector(x, y, 1.0), out OpticalVector ray));
                var measured = new float[2];
                var expected = new double[2];
                for (int i = 0; i < tiles.Length; i++)
                {
                    double along = centers[i].Dot(ray);
                    double discriminant = radii[i] * radii[i]
                        - (centers[i] - ray * along).LengthSquared();
                    Assert.True(discriminant > 0.0, "both real sphere silhouettes must cover the ray");
                    expected[i] = along - Math.Sqrt(discriminant);
                    AtlasProjection tile = tiles[i];
                    double z = expected[i] * ray.Dot(tile.Forward);
                    // Encode physical source-view Z using finite reverse-Z perspective.
                    double depth = tile.Near * (tile.Far - z) / (z * (tile.Far - tile.Near));
                    Assert.InRange(depth, double.Epsilon, 1.0);
                    Assert.True(tile.TryProjectRestRay(ray, out double u, out double v));
                    Assert.True(ShaderRadialOf(tile, (float)u, (float)v, (float)depth, out measured[i]));
                    Assert.InRange(Math.Abs(measured[i] / expected[i] - 1.0), 0.0, 2e-6);
                }

                Assert.True(expected[0] < expected[1]);
                foreach (int[] order in new[] { new[] { 0, 1 }, new[] { 1, 0 } })
                {
                    int winner = -1;
                    float best = float.PositiveInfinity;
                    foreach (int i in order)
                    {
                        if (measured[i] < best)
                        {
                            best = measured[i];
                            winner = i;
                        }
                    }
                    Assert.Equal(0, winner);
                }
            }
        }

        private static bool MainShaderRadial(float u, float v, float depth,
            float tanHalfFov, float near, float far, out float radial)
        {
            radial = 0f;
            if (!(u >= 0f && u <= 1f) || !(v >= 0f && v <= 1f) || !(depth > 0f && depth <= 1f))
            {
                return false;
            }

            float ratio = near / far;
            float a = ratio / (1f - ratio);
            float b = near / (1f - ratio);
            float sourceZ = b / (depth + a);
            float x = (2f * u - 1f) * tanHalfFov;
            float y = (2f * v - 1f) * tanHalfFov;
            float r = sourceZ * MathF.Sqrt(1f + x * x + y * y);
            if (!(r > 0f) || !float.IsFinite(r))
            {
                return false;
            }

            radial = r;
            return true;
        }

        private static bool ShaderRadialOf(AtlasProjection projection, float u, float v, float depth,
            out float radial) =>
            MainShaderRadial(u, v, depth, (float)projection.TanHalfFov,
                (float)projection.Near, (float)projection.Far, out radial);

        private static readonly (float U, float V)[] RadialSamples =
        {
            (0.5f, 0.5f),
            (0.25f, 0.7f),
            (0.9f, 0.1f),
        };

        [Theory]
        [InlineData(0.0, 0.0, -1000.0, 10.0)]
        [InlineData(1200.0, -500.0, -8000.0, 40.0)]
        public void MainShaderRadial_FloatReplicaMatchesDouble(double px, double py, double pz, double radius)
        {
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(px, py, pz), radius, 512, 8,
                out AtlasProjection projection));
            foreach ((float u, float v) in RadialSamples)
            {
                foreach (float depth in new[] { 1f, 0.5f, 0.01f, 1e-7f })
                {
                    bool expected = projection.TryReconstructRadialDistance(u, v, depth, out double reference);
                    Assert.Equal(expected, ShaderRadialOf(projection, u, v, depth, out float actual));
                    if (expected)
                    {
                        Assert.InRange(Math.Abs(actual / (float)reference - 1f), 0f, 2e-3f);
                    }
                }
            }
        }

        [Fact]
        public void MainShaderRadial_SamePhysicalRadialAcrossUnequalProjections()
        {
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, -1000.0), 10.0, 512, 8,
                out AtlasProjection near));
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, -8000.0), 40.0, 512, 8,
                out AtlasProjection far));

            const double radialTarget = 500.0;
            foreach (AtlasProjection projection in new[] { near, far })
            {
                double ratio = projection.Near / projection.Far;
                double a = ratio / (1.0 - ratio);
                double b = projection.Near / (1.0 - ratio);
                double depth = b / radialTarget - a;
                Assert.InRange(depth, 0.0, 1.0);
                Assert.True(ShaderRadialOf(projection, 0.5f, 0.5f, (float)depth, out float radial));
                Assert.InRange(Math.Abs(radial / (float)radialTarget - 1f), 0f, 2e-3f);
            }
        }

        [Fact]
        public void MainShaderRadial_OrdersByRadialNotRawDepthOrCenterDistance()
        {
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, -5000.0), 50.0, 512, 8,
                out AtlasProjection farBody));
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, -1000.0), 10.0, 512, 8,
                out AtlasProjection nearBody));
            Assert.True(farBody.Distance > nearBody.Distance);

            Assert.True(ShaderRadialOf(farBody, 0.5f, 0.5f, 0.999f, out float farBodyRadial));
            Assert.True(ShaderRadialOf(nearBody, 0.5f, 0.5f, 0.001f, out float nearBodyRadial));
            Assert.True(farBodyRadial < nearBodyRadial,
                $"far-plane-adjacent sample of the distant tile ({farBodyRadial}) must beat " +
                $"the near tile's far sample ({nearBodyRadial}) despite larger raw depth and larger center distance");
            Assert.True(farBodyRadial < farBody.Distance * 0.02);
        }

        [Fact]
        public void MainShaderRadial_ClearedDepthAndInvalidSamplesRejected()
        {
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, -1000.0), 10.0, 512, 8,
                out AtlasProjection projection));
            Assert.False(ShaderRadialOf(projection, 0.5f, 0.5f, 0f, out _));
            Assert.False(ShaderRadialOf(projection, 0.5f, 0.5f, -0.1f, out _));
            Assert.False(ShaderRadialOf(projection, 0.5f, 0.5f, 1.000001f, out _));
            Assert.False(ShaderRadialOf(projection, -0.01f, 0.5f, 0.5f, out _));
            Assert.False(ShaderRadialOf(projection, 0.5f, 1.01f, 0.5f, out _));
        }

        private static (bool InsideCap, bool ValidUv, bool DepthValid, Vector2 Uv) ShaderCoverageMask(
            Vector3 nRest, Vector3 fwd, Vector3 right, Vector3 up, float tanHalf, float depth)
        {
            float z0 = Vector3.Dot(nRest, fwd);
            if (z0 <= 0f)
            {
                return (false, false, false, default);
            }

            var tangent = new Vector2(Vector3.Dot(nRest, right), Vector3.Dot(nRest, up));
            float perp2 = Vector2.Dot(tangent, tangent);
            bool inside = perp2 <= z0 * z0 * tanHalf * tanHalf;
            float inv0 = 1f / (z0 * tanHalf);
            var uv = new Vector2(0.5f * (1f + tangent.X * inv0),
                                 0.5f * (1f - tangent.Y * inv0));
            bool validUv = float.IsFinite(uv.X) && float.IsFinite(uv.Y)
                && uv.X >= 0f && uv.X <= 1f && uv.Y >= 0f && uv.Y <= 1f;
            return (inside, validUv, validUv && depth > 0f && depth <= 1f, uv);
        }

        private static (Vector3 Fwd, Vector3 Right, Vector3 Up, float TanHalf) TileFrame(
            OpticalVector restPosition, double enclosingRadius)
        {
            Assert.True(AtlasProjection.TryCreate(restPosition, enclosingRadius, 512, 8,
                out AtlasProjection p));
            return (new Vector3((float)p.Forward.X, (float)p.Forward.Y, (float)p.Forward.Z),
                    new Vector3((float)p.Right.X, (float)p.Right.Y, (float)p.Right.Z),
                    new Vector3((float)p.Up.X, (float)p.Up.Y, (float)p.Up.Z),
                    (float)p.TanHalfFov);
        }

        [Fact]
        public void CoverageMask_FrontCenterPasses_AntipodeAndOutOfTileBlack()
        {
            (Vector3 fwd, Vector3 right, Vector3 up, float tanHalf) =
                TileFrame(new OpticalVector(0.0, 0.0, -1000.0), 10.0);

            var front = ShaderCoverageMask(fwd, fwd, right, up, tanHalf, depth: 0.5f);
            Assert.True(front.InsideCap);
            Assert.True(front.ValidUv);
            Assert.True(front.DepthValid);

            var antipode = ShaderCoverageMask(-fwd, fwd, right, up, tanHalf, depth: 0.5f);
            Assert.False(antipode.InsideCap);
            Assert.False(antipode.ValidUv);
            Assert.False(antipode.DepthValid);

            var offTile = ShaderCoverageMask(
                Vector3.Normalize(new Vector3(0.9f, 0f, -0.44f)), fwd, right, up, tanHalf, depth: 0.5f);
            Assert.False(offTile.ValidUv);
            Assert.False(offTile.DepthValid);
        }

        [Fact]
        public void CoverageMask_TinyCap_AcceptsCenterWithoutCosRounding()
        {
            const float tanHalf = 1e-7f;
            var fwd = new Vector3(0f, 0f, -1f);

            var center = ShaderCoverageMask(fwd, fwd, Vector3.UnitX, Vector3.UnitY, tanHalf, 0.5f);
            Assert.True(center.InsideCap, "cap test must not use float cos-compare, which rounds to 1");
            Assert.Equal(0.5f, center.Uv.X, 5);
            Assert.Equal(0.5f, center.Uv.Y, 5);

            Assert.True(MathF.Cos(MathF.Atan(tanHalf)) == 1f,
                "float cos(atan(1e-7)) rounding to 1 is why a z0 > cos compare would reject the center");

            var inside = ShaderCoverageMask(
                Vector3.Normalize(new Vector3(0.5e-7f, 0f, -1f)), fwd, Vector3.UnitX, Vector3.UnitY,
                tanHalf, 0.5f);
            Assert.True(inside.InsideCap);
            Assert.True(inside.ValidUv);
            Assert.Equal(0.75f, inside.Uv.X, 5);
            Assert.Equal(0.5f, inside.Uv.Y, 5);

            var edge = ShaderCoverageMask(
                Vector3.Normalize(new Vector3(2e-7f, 0f, -1f)), fwd, Vector3.UnitX, Vector3.UnitY,
                tanHalf, 0.5f);
            Assert.False(edge.InsideCap);
            Assert.False(edge.ValidUv);
            Assert.True(edge.Uv.X > 1f);

            var grazing = ShaderCoverageMask(
                Vector3.Normalize(new Vector3(1e-5f, 0f, -1f)), fwd, Vector3.UnitX, Vector3.UnitY,
                tanHalf, 0.5f);
            Assert.False(grazing.InsideCap,
                "transverse basis components survive z0 rounding to 1: a 1e-5 tilt must fail the 1e-7 cap");
            Assert.False(grazing.ValidUv);
        }

        [Fact]
        public void CoverageMask_KnownUvAxisSigns()
        {
            (Vector3 fwd, Vector3 right, Vector3 up, float tanHalf) =
                TileFrame(new OpticalVector(0.0, 0.0, -1000.0), 10.0);

            Vector3 rightTilt = Vector3.Normalize(fwd + right * tanHalf * 0.5f);
            Vector3 upTilt = Vector3.Normalize(fwd + up * tanHalf * 0.5f);

            float zRight = Vector3.Dot(rightTilt, fwd);
            float uvXRight = 0.5f * (1f + Vector3.Dot(rightTilt, right) / (zRight * tanHalf));
            Assert.True(uvXRight > 0.5f);

            float zUp = Vector3.Dot(upTilt, fwd);
            float uvYUp = 0.5f * (1f - Vector3.Dot(upTilt, up) / (zUp * tanHalf));
            Assert.True(uvYUp < 0.5f, "tile V must be flipped: +up tilts toward smaller v");
        }

        [Fact]
        public void Mode6DepthMask_ValidOnlyWithFrontUvAndPositiveDepth()
        {
            (Vector3 fwd, Vector3 right, Vector3 up, float tanHalf) =
                TileFrame(new OpticalVector(0.0, 0.0, -1000.0), 10.0);

            Assert.True(ShaderCoverageMask(fwd, fwd, right, up, tanHalf, 0.5f).DepthValid);
            Assert.False(ShaderCoverageMask(fwd, fwd, right, up, tanHalf, 0f).DepthValid,
                "cleared depth 0 is no coverage, independent of color alpha");
            Assert.False(ShaderCoverageMask(fwd, fwd, right, up, tanHalf, float.NaN).DepthValid);
            Assert.False(ShaderCoverageMask(-fwd, fwd, right, up, tanHalf, 0.5f).DepthValid);
            Assert.False(ShaderCoverageMask(
                Vector3.Normalize(new Vector3(0.9f, 0f, -0.44f)), fwd, right, up, tanHalf, 0.5f).DepthValid);
        }

        [Fact]
        public void Mode0Composite_DepthRejectionUsesNativeDepthNotColorAlpha()
        {
            Assert.True(AtlasProjection.TryCreate(new OpticalVector(0.0, 0.0, -1000.0), 10.0, 512, 8,
                out AtlasProjection projection));
            Assert.False(ShaderRadialOf(projection, 0.5f, 0.5f, 0f, out _),
                "coverage must come from the measured source depth, never from alpha");
            Assert.True(ShaderRadialOf(projection, 0.5f, 0.5f, 0.5f, out _));
        }
    }
}

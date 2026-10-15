using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class SourceBoundaryTests
    {
        private static readonly (string Type, string Name)[] Expected =
        {
            ("KSA.Rendering.RenderImageEx", "ClearDepthImages"),
            ("KSA.PrePassRenderer", "CopyDepthImageToSrc"),
            ("KSA.Rendering.RenderTarget", "BeginRendering"),
            ("KSA.Rendering.Sun.SunRenderer", "Draw"),
            ("KSA.StaticCelestialDistanceRendering", "Run"),
            ("KSA.StaticCelestial", "RenderSphere"),
            ("KSA.GenericMeshRenderer", "WriteCommands"),
            ("KSA.SunbloomRenderer", "Render"),
            ("KSA.OverallBloomRenderer", "Render"),
            ("KSA.Program", "RenderAaPreparePasses"),
        };

        [Fact]
        public void ExactSequence_WithDepthLoad_Accepted()
        {
            Assert.True(RenderCompatibility.TryValidateSourceBoundary(Expected, nativeDepthLoad: true, out string reason));
            Assert.Contains("intact", reason);
        }

        [Fact]
        public void InterleavedUnrelatedCalls_StillAccepted()
        {
            var calls = new List<(string Type, string Name)>
            {
                ("KSA.Program", "RenderGameStart"),
                ("KSA.Rendering.RenderImageEx", "ClearDepthImages"),
                ("KSA.SomeRenderer", "Unrelated"),
                ("KSA.PrePassRenderer", "CopyDepthImageToSrc"),
                ("KSA.Rendering.RenderTarget", "BeginRendering"),
                ("KSA.Rendering.Sun.SunRenderer", "Draw"),
                ("KSA.StaticCelestialDistanceRendering", "Run"),
                ("KSA.StaticCelestial", "RenderSphere"),
                ("KSA.PlanetCloudsRenderer", "Render"),
                ("KSA.GenericMeshRenderer", "WriteCommands"),
                ("KSA.SunbloomRenderer", "Render"),
                ("KSA.OverallBloomRenderer", "Render"),
                ("KSA.Program", "RenderAaPreparePasses"),
                ("KSA.Program", "ComposeScreen"),
            };

            Assert.True(RenderCompatibility.TryValidateSourceBoundary(calls, nativeDepthLoad: true, out string reason));
            Assert.Contains("intact", reason);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        public void MissingRequiredCall_Rejected(int skipIndex)
        {
            var calls = Expected.Where((_, i) => i != skipIndex).ToList();
            Assert.False(RenderCompatibility.TryValidateSourceBoundary(calls, nativeDepthLoad: true, out string reason));
            Assert.Contains("missing", reason);
            Assert.Contains(Expected[skipIndex].Name, reason);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        public void DuplicateRequiredCall_Rejected(int duplicateIndex)
        {
            var calls = Expected.ToList();
            calls.Insert(duplicateIndex + 1, Expected[duplicateIndex]);
            Assert.False(RenderCompatibility.TryValidateSourceBoundary(calls, nativeDepthLoad: true, out string reason));
            Assert.Contains("duplicate", reason);
            Assert.Contains(Expected[duplicateIndex].Name, reason);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void ReorderedNeighbors_Rejected(int swapIndex)
        {
            var calls = Expected.ToList();
            (calls[swapIndex], calls[swapIndex + 1]) = (calls[swapIndex + 1], calls[swapIndex]);
            Assert.False(RenderCompatibility.TryValidateSourceBoundary(calls, nativeDepthLoad: true, out string reason));
            Assert.Contains("out of order", reason);
        }

        [Fact]
        public void DepthClearInsteadOfLoad_Rejected()
        {
            Assert.False(RenderCompatibility.TryValidateSourceBoundary(Expected, nativeDepthLoad: false, out string reason));
            Assert.Contains("Load", reason);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(42)]
        public void HeightHandle_BoundOriginalPreserved(int original)
        {
            Assert.True(RenderCompatibility.TryResolveSourceHeightHandle(original, 0.0f, 0.0f, -1, out int resolved));
            Assert.Equal(original, resolved);
            Assert.True(RenderCompatibility.TryResolveSourceHeightHandle(original, 120.5f, -40.0f, 17, out resolved));
            Assert.Equal(original, resolved);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(17)]
        public void HeightHandle_MissingWithZeroBounds_UsesNeutralCube(int neutral)
        {
            Assert.True(RenderCompatibility.TryResolveSourceHeightHandle(-1, 0.0f, 0.0f, neutral, out int resolved));
            Assert.Equal(neutral, resolved);
        }

        [Fact]
        public void HeightHandle_MissingWithoutNeutral_Rejected()
        {
            Assert.False(RenderCompatibility.TryResolveSourceHeightHandle(-1, 0.0f, 0.0f, -1, out int resolved));
            Assert.Equal(-1, resolved);
        }

        [Theory]
        [InlineData(50.0f, 0.0f)]
        [InlineData(0.0f, 50.0f)]
        [InlineData(-12.0f, 0.0f)]
        [InlineData(0.0f, -12.0f)]
        public void HeightHandle_MissingWithNonzeroBounds_Rejected(float min, float max)
        {
            Assert.False(RenderCompatibility.TryResolveSourceHeightHandle(-1, min, max, 9, out int resolved));
            Assert.Equal(-1, resolved);
        }

        [Theory]
        [InlineData(float.NaN, 0.0f)]
        [InlineData(0.0f, float.NaN)]
        [InlineData(float.PositiveInfinity, 0.0f)]
        [InlineData(0.0f, float.NegativeInfinity)]
        public void HeightHandle_NonFiniteBounds_Rejected(float min, float max)
        {
            Assert.False(RenderCompatibility.TryResolveSourceHeightHandle(-1, min, max, 9, out int resolved));
            Assert.Equal(-1, resolved);
            Assert.False(RenderCompatibility.TryResolveSourceHeightHandle(7, min, max, 9, out resolved));
            Assert.Equal(-1, resolved);
        }

        private static readonly (string Type, string Name)[] SunDrawCalls =
        {
            ("KSA.Universe", "get_WorldSun"),
            ("KSA.Camera", "GetPositionEgo"),
            ("KSA.Rendering.Sun.SunRenderer", "StartShowingDistOf"),
            ("KSA.SomeType", "get_View"),
            ("KSA.Rendering.Sun.SunMeshRenderable", "Draw"),
        };

        private static readonly (string Type, string Name)[] MeshQueueCalls =
        {
            ("Brutal.Numerics.float4", ".ctor"),
            ("KSA.SomeQueue", "get_Instance"),
            ("KSA.SomeQueue", "DrawMeshInstanceAllPasses"),
        };

        [Fact]
        public void SolarQueueBoundary_ExpectedCalls_Accepted()
        {
            Assert.True(RenderCompatibility.TryValidateSolarQueueBoundary(
                SunDrawCalls, MeshQueueCalls, out string reason));
            Assert.Contains("verified", reason);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        public void SolarQueueBoundary_MissingRequiredCall_Rejected(int skipIndex)
        {
            var calls = SunDrawCalls.Where((_, i) => i != skipIndex).ToList();
            Assert.False(RenderCompatibility.TryValidateSolarQueueBoundary(
                calls, MeshQueueCalls, out string reason));
            Assert.Contains("missing", reason);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        public void SolarQueueBoundary_DuplicateRequiredCall_Rejected(int duplicateIndex)
        {
            var calls = SunDrawCalls.ToList();
            calls.Insert(duplicateIndex + 1, SunDrawCalls[duplicateIndex]);
            Assert.False(RenderCompatibility.TryValidateSolarQueueBoundary(
                calls, MeshQueueCalls, out _));
        }

        [Fact]
        public void SolarQueueBoundary_ReorderedRequiredCalls_Rejected()
        {
            var calls = SunDrawCalls.ToList();
            (calls[1], calls[2]) = (calls[2], calls[1]);
            Assert.False(RenderCompatibility.TryValidateSolarQueueBoundary(
                calls, MeshQueueCalls, out string reason));
            Assert.Contains("out of order", reason);
        }

        [Theory]
        [InlineData("BeginRendering")]
        [InlineData("EndRendering")]
        [InlineData("DrawIndexed")]
        [InlineData("PipelineBarrier2")]
        [InlineData("DrawMesh")]
        public void SolarQueueBoundary_GpuWorkInsideNativeDraw_Rejected(string name)
        {
            var calls = SunDrawCalls.ToList();
            calls.Add(("Some.NativeRenderer", name));
            Assert.False(RenderCompatibility.TryValidateSolarQueueBoundary(
                calls, MeshQueueCalls, out _));
        }

        [Fact]
        public void SolarQueueBoundary_MeshMissingBucketQueue_Rejected()
        {
            var mesh = MeshQueueCalls.Where(c => c.Name != "DrawMeshInstanceAllPasses").ToList();
            Assert.False(RenderCompatibility.TryValidateSolarQueueBoundary(
                SunDrawCalls, mesh, out string reason));
            Assert.Contains("DrawMeshInstanceAllPasses", reason);
        }

        [Theory]
        [InlineData("Draw")]
        [InlineData("Render")]
        [InlineData("DrawIndexed")]
        [InlineData("BeginRendering")]
        [InlineData("PipelineBarrier2")]
        public void SolarQueueBoundary_MeshReachesGpuOrOtherRenderer_Rejected(string name)
        {
            var mesh = MeshQueueCalls.ToList();
            mesh.Add(("Some.NativeRenderer", name));
            Assert.False(RenderCompatibility.TryValidateSolarQueueBoundary(
                SunDrawCalls, mesh, out _));
        }

        [Fact]
        public void SolarQueueBoundary_MeshDuplicateBucketQueue_Rejected()
        {
            var mesh = MeshQueueCalls.ToList();
            mesh.Add(("KSA.SomeQueue", "DrawMeshInstanceAllPasses"));
            Assert.False(RenderCompatibility.TryValidateSolarQueueBoundary(
                SunDrawCalls, mesh, out _));
        }
    }
}

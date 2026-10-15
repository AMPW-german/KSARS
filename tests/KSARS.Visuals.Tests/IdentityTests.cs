using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class IdentityTests
    {
        [Fact]
        public void EvaluateIdentity_ExactSupportedIdentity_IsTrue() =>
            Assert.True(RenderCompatibility.EvaluateIdentity(
                RenderCompatibility.SupportedFileVersion,
                RenderCompatibility.SupportedProductVersion,
                RenderCompatibility.SupportedSha256));

        public static IEnumerable<object?[]> MismatchedIdentities => new[]
        {
            new object?[] { null, RenderCompatibility.SupportedProductVersion, RenderCompatibility.SupportedSha256 },
            new object?[] { "", RenderCompatibility.SupportedProductVersion, RenderCompatibility.SupportedSha256 },
            new object?[] { "2026.10.7.5540", RenderCompatibility.SupportedProductVersion, RenderCompatibility.SupportedSha256 },
            new object?[] { RenderCompatibility.SupportedFileVersion, null, RenderCompatibility.SupportedSha256 },
            new object?[] { RenderCompatibility.SupportedFileVersion, "", RenderCompatibility.SupportedSha256 },
            new object?[] { RenderCompatibility.SupportedFileVersion, "2026.10.7.5541+0000000000000000000000000000000000000000", RenderCompatibility.SupportedSha256 },
            new object?[] { RenderCompatibility.SupportedFileVersion, RenderCompatibility.SupportedProductVersion.ToUpperInvariant(), RenderCompatibility.SupportedSha256 },
            new object?[] { RenderCompatibility.SupportedFileVersion, RenderCompatibility.SupportedProductVersion, null },
            new object?[] { RenderCompatibility.SupportedFileVersion, RenderCompatibility.SupportedProductVersion, "" },
            new object?[] { RenderCompatibility.SupportedFileVersion, RenderCompatibility.SupportedProductVersion, "0000000000000000000000000000000000000000000000000000000000000000" },
        };

        [Theory]
        [MemberData(nameof(MismatchedIdentities))]
        public void EvaluateIdentity_MissingOrMismatchingInput_IsFalse(string? fileVersion, string? productVersion, string? sha256) =>
            Assert.False(RenderCompatibility.EvaluateIdentity(fileVersion, productVersion, sha256));

        [Fact]
        public void EvaluateIdentity_Sha256_IsCaseInsensitive() =>
            Assert.True(RenderCompatibility.EvaluateIdentity(
                RenderCompatibility.SupportedFileVersion,
                RenderCompatibility.SupportedProductVersion,
                RenderCompatibility.SupportedSha256.ToLowerInvariant()));
    }
}

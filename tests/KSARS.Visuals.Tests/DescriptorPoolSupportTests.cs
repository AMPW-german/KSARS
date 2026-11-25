using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class DescriptorPoolSupportTests
    {
        [Fact]
        public void CombineDescriptorCounts_SumsDescriptorsAcrossLayoutsAndSets()
        {
            var global = new Dictionary<string, int>
            {
                ["uniform"] = 2,
                ["storage"] = 1,
            };
            var material = new Dictionary<string, int>
            {
                ["uniform"] = 1,
                ["dynamic"] = 1,
            };

            Dictionary<string, int> counts = DescriptorPoolSupport.CombineDescriptorCounts(
                (global, 1),
                (material, 1));

            Assert.Equal(3, counts["uniform"]);
            Assert.Equal(1, counts["storage"]);
            Assert.Equal(1, counts["dynamic"]);
        }

        [Fact]
        public void CombineDescriptorCounts_MultipliesLayoutCountsBySetCount()
        {
            var global = new Dictionary<string, int>
            {
                ["uniform"] = 2,
                ["storage"] = 1,
            };

            Dictionary<string, int> counts = DescriptorPoolSupport.CombineDescriptorCounts((global, 3));

            Assert.Equal(6, counts["uniform"]);
            Assert.Equal(3, counts["storage"]);
        }

        [Fact]
        public void CombineDescriptorCounts_RejectsNonPositiveSetCount()
        {
            var descriptors = new Dictionary<string, int> { ["uniform"] = 2 };
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                DescriptorPoolSupport.CombineDescriptorCounts((descriptors, 0)));
        }
    }
}

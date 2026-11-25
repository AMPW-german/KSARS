using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace KSARS.Visuals
{
    internal static class DescriptorPoolSupport
    {
        internal static VkDescriptorPoolSize[] CreatePoolSizes(
            params (IReadOnlyDictionary<VkDescriptorType, int> Descriptors, int SetCount)[] layouts)
        {
            return CombineDescriptorCounts(layouts)
                .Select(pair => new VkDescriptorPoolSize
                {
                    Type = pair.Key,
                    DescriptorCount = pair.Value,
                })
                .ToArray();
        }

        internal static Dictionary<TDescriptor, int> CombineDescriptorCounts<TDescriptor>(
            params (IReadOnlyDictionary<TDescriptor, int> Descriptors, int SetCount)[] layouts)
            where TDescriptor : notnull
        {
            var counts = new Dictionary<TDescriptor, int>();
            foreach ((IReadOnlyDictionary<TDescriptor, int> descriptors, int setCount) in layouts)
            {
                ArgumentNullException.ThrowIfNull(descriptors);
                if (setCount <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(layouts));
                }

                foreach ((TDescriptor type, int count) in descriptors)
                {
                    counts[type] = checked(counts.GetValueOrDefault(type) + checked(count * setCount));
                }
            }

            return counts;
        }
    }
}

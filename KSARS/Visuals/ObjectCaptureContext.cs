using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using KSA.Rendering;

namespace KSARS.Visuals
{
    internal sealed class ObjectCaptureContext : IDisposable
    {
        private readonly Core.Renderer _renderer;
        private ByteSize _lightingOffset;
        private ByteSize _celestialOffset;
        private ByteSize _vesselOffset;
        private bool _disposed;

        internal RenderTarget Target { get; private set; } = null!;
        internal DescriptorPoolEx DescriptorPool { get; private set; } = null!;
        internal DescriptorSetLayoutEx MaterialLayout { get; private set; } = null!;
        internal VkDescriptorSet GlobalSet { get; private set; }
        internal VkDescriptorSet MaterialSet { get; private set; }
        internal BufferEx GlobalBuffer { get; private set; }
        internal BufferEx ConstantsBuffer { get; private set; }
        internal BufferEx MaterialBuffer { get; private set; }
        internal int TileSize { get; }

        internal ObjectCaptureContext(Core.Renderer renderer, int tileSize, string name,
            in UboCameraData camera, in UboLightingData lighting, in UboCelestialData celestial,
            in UboVesselData vessel, in UboGlobalConstants constants, in DistantSphereMaterialData material)
        {
            ArgumentNullException.ThrowIfNull(renderer);
            if (tileSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tileSize));
            }

            _renderer = renderer;
            TileSize = tileSize;
            try
            {
                Target = new RenderTarget(renderer, name + "_Target", new VkExtent2D(tileSize, tileSize),
                    VkFormat.R32G32B32A32SFloat, VkFormat.D32SFloat, 1, VkSampleCountFlags._1Bit, true);

                ByteSize cameraOffset = ByteSize.Zero;
                ByteSize lightingOffset = cameraOffset + ByteSize.Of<UboCameraData>();
                ByteSize celestialOffset = lightingOffset + ByteSize.Of<UboLightingData>();
                ByteSize vesselOffset = celestialOffset + ByteSize.Of<UboCelestialData>();
                ByteSize payloadSize = vesselOffset + ByteSize.Of<UboVesselData>();
                _lightingOffset = lightingOffset;
                _celestialOffset = celestialOffset;
                _vesselOffset = vesselOffset;
                ulong alignment = Utils.MinUniformBufferOffsetAlignment;
                ulong remainder = (ulong)payloadSize % alignment;
                ByteSize globalStride = remainder == 0
                    ? payloadSize
                    : payloadSize + ByteSize.Of((long)(alignment - remainder));

                GlobalBuffer = CreateUniformBuffer(renderer, name + "_GlobalUbo", globalStride);
                ConstantsBuffer = CreateUniformBuffer(renderer, name + "_GlobalConstants", ByteSize.Of<UboGlobalConstants>());
                MaterialBuffer = CreateUniformBuffer(renderer, name + "_Material", DistantSphereRenderer.MaterialStride);

                MappedMemory globalMemory = GlobalBuffer.Map();
                try
                {
                    globalMemory.Offset(cameraOffset).As<UboCameraData>() = camera;
                    globalMemory.Offset(lightingOffset).As<UboLightingData>() = lighting;
                    globalMemory.Offset(celestialOffset).As<UboCelestialData>() = celestial;
                    globalMemory.Offset(vesselOffset).As<UboVesselData>() = vessel;
                }
                finally
                {
                    globalMemory.Unmap();
                }

                MappedMemory constantsMemory = ConstantsBuffer.Map();
                try
                {
                    constantsMemory.As<UboGlobalConstants>() = constants;
                }
                finally
                {
                    constantsMemory.Unmap();
                }

                MappedMemory materialMemory = MaterialBuffer.Map();
                try
                {
                    materialMemory.As<DistantSphereMaterialData>() = material;
                }
                finally
                {
                    materialMemory.Unmap();
                }

                var materialCounts = new Utils.BindingCounts { UniformDynamicBuffers = 1 };
                MaterialLayout = Utils.BuildDescriptorSetLayout(renderer, name + "_MaterialLayout",
                    materialCounts, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit);
                DescriptorPool = DescriptorPoolExExtensions.CreateDescriptorPool(renderer.Device,
                    new DescriptorPoolEx.CreateInfo
                    {
                        MaxSets = 2,
                        PoolSizes = new VkDescriptorPoolSize[]
                        {
                            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 2 },
                            new() { Type = VkDescriptorType.UniformBufferDynamic, DescriptorCount = 1 },
                        },
                    },
                    null);
                GlobalSet = DescriptorPoolExExtensions.AllocateDescriptorSet(renderer.Device, DescriptorPool,
                    GlobalShaderBindings.DescriptorSetLayout);
                MaterialSet = DescriptorPoolExExtensions.AllocateDescriptorSet(renderer.Device, DescriptorPool,
                    MaterialLayout);

                var globalInfo = new VkDescriptorBufferInfo
                {
                    Buffer = GlobalBuffer.VkBuffer,
                    Offset = ByteSize.Zero,
                    Range = globalStride,
                };
                var constantsInfo = new VkDescriptorBufferInfo
                {
                    Buffer = ConstantsBuffer.VkBuffer,
                    Offset = ByteSize.Zero,
                    Range = ByteSize.Of<UboGlobalConstants>(),
                };
                Utils.WriteDescriptorSets(GlobalSet,
                    Span<VkDescriptorImageInfo>.Empty, Span<VkDescriptorImageInfo>.Empty,
                    new[] { globalInfo, constantsInfo }, Span<VkDescriptorBufferInfo>.Empty,
                    Span<VkDescriptorBufferInfo>.Empty, renderer);

                var materialInfo = new VkDescriptorBufferInfo
                {
                    Buffer = MaterialBuffer.VkBuffer,
                    Offset = ByteSize.Zero,
                    Range = DistantSphereRenderer.MaterialStride,
                };
                Utils.WriteDescriptorSets(MaterialSet,
                    Span<VkDescriptorImageInfo>.Empty, Span<VkDescriptorImageInfo>.Empty,
                    Span<VkDescriptorBufferInfo>.Empty, new[] { materialInfo },
                    Span<VkDescriptorBufferInfo>.Empty, renderer);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void WriteSnapshot(in UboCameraData camera, in UboLightingData lighting,
            in UboCelestialData celestial, in UboVesselData vessel, in UboGlobalConstants constants,
            in DistantSphereMaterialData material)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            MappedMemory globalMemory = GlobalBuffer.Map();
            try
            {
                globalMemory.As<UboCameraData>() = camera;
                globalMemory.Offset(_lightingOffset).As<UboLightingData>() = lighting;
                globalMemory.Offset(_celestialOffset).As<UboCelestialData>() = celestial;
                globalMemory.Offset(_vesselOffset).As<UboVesselData>() = vessel;
            }
            finally
            {
                globalMemory.Unmap();
            }

            MappedMemory constantsMemory = ConstantsBuffer.Map();
            try
            {
                constantsMemory.As<UboGlobalConstants>() = constants;
            }
            finally
            {
                constantsMemory.Unmap();
            }

            MappedMemory materialMemory = MaterialBuffer.Map();
            try
            {
                materialMemory.As<DistantSphereMaterialData>() = material;
            }
            finally
            {
                materialMemory.Unmap();
            }
        }

        private static BufferEx CreateUniformBuffer(Core.Renderer renderer, string name, ByteSize size)
        {
            return BufferExExtensions.CreateBuffer(renderer.Allocator, new BufferEx.CreateInfo
            {
                Name = name,
                BufferUsage = VkBufferUsageFlags.UniformBufferBit,
                BufferSize = size,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            });
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DescriptorPool?.Dispose();
            DescriptorPool = null!;
            MaterialLayout?.Dispose();
            MaterialLayout = null!;
            if (!GlobalBuffer.IsNull())
            {
                GlobalBuffer.Dispose();
            }

            if (!ConstantsBuffer.IsNull())
            {
                ConstantsBuffer.Dispose();
            }

            if (!MaterialBuffer.IsNull())
            {
                MaterialBuffer.Dispose();
            }

            Target?.Dispose();
            Target = null!;
        }
    }
}

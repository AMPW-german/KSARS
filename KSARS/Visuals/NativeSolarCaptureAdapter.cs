using Brutal;
using Brutal.Memory;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using RenderCore;
using RenderCore.Mesh;

namespace KSARS.Visuals
{
    internal sealed class NativeSolarCaptureAdapter : RenderTechnique
    {
        private readonly Core.Renderer _renderer;
        private readonly SimpleVkMesh _mesh = null!;
        private BufferEx _sceneBuffer;
        private BufferEx _instanceBuffer;
        private DescriptorSetLayoutEx? _setLayout;
        private DescriptorSetLayoutEx? _gapLayout;
        private DescriptorPoolEx? _pool;
        private VkDescriptorSet _set;
        private PtrOwner<VkPipelineDepthStencilStateCreateInfo>? _depthState;
        private bool _disposed;

        internal bool RecordingRecovered { get; private set; } = true;

        internal NativeSolarCaptureAdapter(Core.Renderer renderer, ObjectCaptureContext context)
            : base("KSARS_NativeSolarSource", renderer, context.Target,
                new ShaderReference[]
                {
                    ModLibrary.Get<ShaderReference>("SunVert"),
                    ModLibrary.Get<ShaderReference>("SunFrag"),
                },
                Span<VkSpecializationInfo>.Empty)
        {
            _renderer = renderer;
            try
            {
                _mesh = ModLibrary.Get<MeshReference>("Sphere").DeviceMesh;
                TextureReference noise = ModLibrary.Get<TextureReference>("NoiseTex");

                _sceneBuffer = BufferExExtensions.CreateBuffer(renderer.Allocator, new BufferEx.CreateInfo
                {
                    Name = "KSARS_SolarProbe_SceneUbo",
                    BufferUsage = VkBufferUsageFlags.UniformBufferBit,
                    BufferSize = ByteSize.Of<MeshRenderTechnique.Ubo>(),
                    AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                });
                _instanceBuffer = BufferExExtensions.CreateBuffer(renderer.Allocator, new BufferEx.CreateInfo
                {
                    Name = "KSARS_SolarProbe_InstanceData",
                    BufferUsage = VkBufferUsageFlags.VertexBufferBit,
                    BufferSize = ByteSize.Of<InstanceData>(),
                    AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                });

                var bindings = new VkDescriptorSetLayoutBinding[2]
                {
                    new()
                    {
                        Binding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.UniformBuffer,
                        StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
                    },
                    new()
                    {
                        Binding = 1,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.CombinedImageSampler,
                        StageFlags = VkShaderStageFlags.FragmentBit,
                    },
                };
                _setLayout = renderer.Device.CreateDescriptorSetLayout(
                    new DescriptorSetLayoutEx.CreateInfo { Bindings = bindings }, null);
                _gapLayout = renderer.Device.CreateDescriptorSetLayout(
                    new DescriptorSetLayoutEx.CreateInfo { Bindings = Array.Empty<VkDescriptorSetLayoutBinding>() },
                    null);

                _pool = DescriptorPoolExExtensions.CreateDescriptorPool(renderer.Device,
                    new DescriptorPoolEx.CreateInfo
                    {
                        MaxSets = 1,
                        PoolSizes = new VkDescriptorPoolSize[]
                        {
                            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 1 },
                            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 },
                        },
                    },
                    null);
                _set = DescriptorPoolExExtensions.AllocateDescriptorSet(renderer.Device, _pool, _setLayout);

                var sceneInfo = new VkDescriptorBufferInfo
                {
                    Buffer = _sceneBuffer.VkBuffer,
                    Offset = ByteSize.Zero,
                    Range = ByteSize.Of<MeshRenderTechnique.Ubo>(),
                };
                WriteBufferDescriptor(_set, 0, VkDescriptorType.UniformBuffer, in sceneInfo);
                var noiseInfo = new VkDescriptorImageInfo
                {
                    Sampler = Program.Instance.TextureSystem.SamplerRepeat,
                    ImageView = noise.ImageView,
                    ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
                };
                WriteImageDescriptor(_set, 1, in noiseInfo);

                _depthState = new PtrOwner<VkPipelineDepthStencilStateCreateInfo>(
                    new VkPipelineDepthStencilStateCreateInfo
                    {
                        DepthTestEnable = true,
                        DepthWriteEnable = false,
                        DepthCompareOp = VkCompareOp.Greater,
                        DepthBoundsTestEnable = false,
                        StencilTestEnable = false,
                    });

                ReadOnlySpan<VkDescriptorSetLayout> setLayouts = stackalloc VkDescriptorSetLayout[3]
                {
                    (VkDescriptorSetLayout)_setLayout,
                    (VkDescriptorSetLayout)_gapLayout,
                    (VkDescriptorSetLayout)GlobalShaderBindings.DescriptorSetLayout,
                };
                PipelineLayout = DeviceExtensions.CreatePipelineLayout(renderer.Device, setLayouts,
                    ReadOnlySpan<VkPushConstantRange>.Empty, null);
                RebuildFrameResources();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        protected override VertexInput MakeVertexInput()
        {
            return new VertexInput(4, 8)
                .AddAttributeBinding<float3>(0, 0, VkVertexInputRate.Vertex, Behavior.None)
                .AddAttributeBinding<float3>(1, 1, VkVertexInputRate.Vertex, Behavior.None)
                .AddAttributeBinding<float2>(2, 2, VkVertexInputRate.Vertex, Behavior.None)
                .AddBindingWithAttributes<InstanceData>(3, 3, VkVertexInputRate.Instance);
        }

        protected override void OnRebuildFrameResources()
        {
            CreatePipeline(VkPrimitiveTopology.TriangleList, VkCullModeFlags.BackBit,
                VkFrontFace.CounterClockwise, VkPolygonMode.Fill,
                _depthState!, Presets.BlendState.BlendColorAlpha, out Pipeline);
        }

        internal void Record(CommandBuffer commandBuffer, ObjectCaptureContext context,
            in MeshRenderTechnique.Ubo scene, in InstanceData instance)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NativeSolarCaptureAdapter));
            }

            ArgumentNullException.ThrowIfNull(context);
            if (!ReferenceEquals(context.Target, RenderPassInfo))
            {
                throw new InvalidOperationException("Record target must be the context target this adapter was built for");
            }

            RecordingRecovered = false;
            bool began = false;
            bool ended = false;
            try
            {
                try
                {
                    MappedMemory sceneMemory = _sceneBuffer.Map();
                    try
                    {
                        sceneMemory.As<MeshRenderTechnique.Ubo>() = scene;
                    }
                    finally
                    {
                        sceneMemory.Unmap();
                    }

                    MappedMemory instanceMemory = _instanceBuffer.Map();
                    try
                    {
                        instanceMemory.As<InstanceData>() = instance;
                    }
                    finally
                    {
                        instanceMemory.Unmap();
                    }

                    context.Target.BeginRendering(commandBuffer, new VkClearValue(), new VkClearValue(),
                        VkAttachmentLoadOp.Clear, VkAttachmentLoadOp.Clear);
                    began = true;

                    VkDeviceExtensions.BindPipeline(commandBuffer, VkPipelineBindPoint.Graphics, Pipeline);

                    int tileSize = context.TileSize;
                    var viewport = new VkViewport { Width = tileSize, Height = tileSize, MinDepth = 0.0f, MaxDepth = 1.0f };
                    ReadOnlySpan<VkViewport> viewports = stackalloc VkViewport[1] { viewport };
                    VkDeviceExtensions.SetViewport(commandBuffer, 0, viewports);
                    var scissor = new VkRect2D(context.Target.Extent);
                    ReadOnlySpan<VkRect2D> scissors = stackalloc VkRect2D[1] { scissor };
                    VkDeviceExtensions.SetScissor(commandBuffer, 0, scissors);

                    ReadOnlySpan<VkDescriptorSet> sceneSets = stackalloc VkDescriptorSet[1] { _set };
                    VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics,
                        PipelineLayout, 0, sceneSets, default(Span<ByteSize32>));
                    ReadOnlySpan<VkDescriptorSet> globalSets = stackalloc VkDescriptorSet[1] { context.GlobalSet };
                    VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics,
                        PipelineLayout, 2, globalSets, default(Span<ByteSize32>));

                    _mesh.BindIndices(commandBuffer);
                    _mesh.BindVertexStream(commandBuffer, MeshAttribute.Position, 0);
                    _mesh.BindVertexStream(commandBuffer, MeshAttribute.Normal, 1);
                    _mesh.BindVertexStream(commandBuffer, MeshAttribute.Uv0, 2);
                    BindBufferExtensions.BindVertexBuffer(commandBuffer, 3, _instanceBuffer);
                    VkDeviceExtensions.DrawIndexed(commandBuffer, _mesh.IndexCount, 1, 0, 0, 0);
                }
                finally
                {
                    if (began)
                    {
                        context.Target.EndRendering(commandBuffer);
                        ended = true;
                    }
                }
            }
            finally
            {
                Program.SetViewport(commandBuffer);
                if (ended)
                {
                    RecordingRecovered = true;
                }
            }
        }

        private unsafe void WriteBufferDescriptor(VkDescriptorSet set, int binding,
            VkDescriptorType type, in VkDescriptorBufferInfo info)
        {
            fixed (VkDescriptorBufferInfo* ptr = &info)
            {
                var write = new VkWriteDescriptorSet
                {
                    DescriptorType = type,
                    DescriptorCount = 1,
                    DstBinding = binding,
                    DstSet = set,
                    BufferInfo = ptr,
                };
                _renderer.Device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(in write), default);
            }
        }

        private unsafe void WriteImageDescriptor(VkDescriptorSet set, int binding,
            in VkDescriptorImageInfo info)
        {
            fixed (VkDescriptorImageInfo* ptr = &info)
            {
                var write = new VkWriteDescriptorSet
                {
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    DescriptorCount = 1,
                    DstBinding = binding,
                    DstSet = set,
                    ImageInfo = ptr,
                };
                _renderer.Device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(in write), default);
            }
        }

        public override void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pool?.Dispose();
            _setLayout?.Dispose();
            _gapLayout?.Dispose();
            if (!_sceneBuffer.IsNull())
            {
                _sceneBuffer.Dispose();
            }

            if (!_instanceBuffer.IsNull())
            {
                _instanceBuffer.Dispose();
            }

            _depthState?.Dispose();
            base.Dispose();
        }
    }
}

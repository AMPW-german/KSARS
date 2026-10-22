using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using RenderCore;
using RenderCore.Mesh;

namespace KSARS.Visuals
{
    internal sealed class NativeCelestialCaptureAdapter : RenderTechnique
    {
        private readonly GpuTextureSystem _textureSystem = null!;
        private readonly SimpleVkMesh _mesh = null!;
        private bool _disposed;

        internal bool RecordingRecovered { get; private set; } = true;

        internal NativeCelestialCaptureAdapter(Core.Renderer renderer, ObjectCaptureContext context)
            : base("KSARS_NativeCelestialSource", renderer, context.Target,
                new ShaderReference[]
                {
                    ModLibrary.Get<ShaderReference>("DistantSphereVert"),
                    ModLibrary.Get<ShaderReference>("DistantSphereFrag"),
                },
                Span<VkSpecializationInfo>.Empty)
        {
            try
            {
                _textureSystem = Program.Instance.TextureSystem;
                _mesh = ModLibrary.Get<MeshReference>("Sphere").DeviceMesh;

                var pushRange = new VkPushConstantRange
                {
                    StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
                    Offset = ByteSize.Zero,
                    Size = ByteSize.Of<DistantSphereData>(),
                };
                ReadOnlySpan<VkDescriptorSetLayout> setLayouts = stackalloc VkDescriptorSetLayout[3]
                {
                    (VkDescriptorSetLayout)GlobalShaderBindings.DescriptorSetLayout,
                    _textureSystem.Layout,
                    (VkDescriptorSetLayout)context.MaterialLayout,
                };
                ReadOnlySpan<VkPushConstantRange> pushRanges = stackalloc VkPushConstantRange[1] { pushRange };
                PipelineLayout = DeviceExtensions.CreatePipelineLayout(renderer.Device, setLayouts, pushRanges, null);
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
            return new VertexInput(3, 12)
                .AddAttributeBinding<Brutal.Numerics.float3>(0, 0, VkVertexInputRate.Vertex, Behavior.None)
                .AddAttributeBinding<Brutal.Numerics.float3>(1, 1, VkVertexInputRate.Vertex, Behavior.None)
                .AddAttributeBinding<Brutal.Numerics.float2>(2, 2, VkVertexInputRate.Vertex, Behavior.None);
        }

        protected override void OnRebuildFrameResources()
        {
            CreatePipeline(VkPrimitiveTopology.TriangleList, VkCullModeFlags.BackBit,
                VkFrontFace.CounterClockwise, VkPolygonMode.Fill,
                RenderingPresets.ReverseZDepthStencil.DepthTestWrite,
                Presets.BlendState.BlendColorAlpha, out Pipeline);
        }

        internal void Record(CommandBuffer commandBuffer, ObjectCaptureContext context, in DistantSphereData sourceData)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NativeCelestialCaptureAdapter));
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

                    ReadOnlySpan<VkDescriptorSet> sets = stackalloc VkDescriptorSet[3]
                    {
                        context.GlobalSet,
                        _textureSystem.DescriptorSet,
                        context.MaterialSet,
                    };
                    Span<ByteSize32> dynamicOffsets = stackalloc ByteSize32[1];
                    dynamicOffsets[0] = default;
                    VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics,
                        PipelineLayout, 0, sets, dynamicOffsets);

                    CommandBufferExtensions.PushConstants(commandBuffer, PipelineLayout,
                        VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, ByteSize.Zero, sourceData);

                    _mesh.BindIndices(commandBuffer);
                    _mesh.BindVertexStream(commandBuffer, MeshAttribute.Position, 0);
                    _mesh.BindVertexStream(commandBuffer, MeshAttribute.Normal, 1);
                    _mesh.BindVertexStream(commandBuffer, MeshAttribute.Uv0, 2);
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

        public override void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            base.Dispose();
        }
    }
}

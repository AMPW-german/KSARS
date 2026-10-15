using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.Pointers.Extensions;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using KSA.Rendering;

namespace KSARS.Visuals
{
    // Owned three-stage solar glow/flare pass built from the installed native shader
    // formulas: fullscreen dual-target glow+occlusion graphics, blur/flare compute,
    // additive merge into the borrowed scene color. Borrows the global/texture/UI
    // descriptor sets and the atlas body set read-only; all flare uniforms, images,
    // pools, layouts and pipelines are owned here.
    internal sealed class SolarGlowFlarePass : IDisposable
    {
        private const VkFormat ImageFormat = VkFormat.R16G16B16A16SFloat;
        private const int LocalSize = 8;

        private readonly Core.Renderer _renderer;
        private readonly RenderTarget _borrowedTarget;
        private readonly AtlasMainPass _atlas;
        private readonly VkExtent2D _extent;

        private readonly VkShaderModule _vertexModule;
        private readonly VkShaderModule _fragmentModule;
        private readonly VkShaderModule _blurModule;
        private readonly VkShaderModule _mergeModule;

        private readonly DescriptorSetLayoutEx _graphicsSet2Layout;
        private readonly DescriptorSetLayoutEx _emptySetLayout;
        private readonly DescriptorSetLayoutEx _blurSet2Layout;
        private readonly DescriptorSetLayoutEx _flareSetLayout;
        private readonly DescriptorSetLayoutEx _mergeSet2Layout;
        private readonly DescriptorPoolEx _pool;
        private readonly VkDescriptorSet _graphicsSet2;
        private readonly VkDescriptorSet _blurSet2;
        private readonly VkDescriptorSet _flareSet;
        private readonly VkDescriptorSet _mergeSet2;

        private readonly VkPipelineLayout _graphicsLayout;
        private readonly VkPipelineLayout _blurLayout;
        private readonly VkPipelineLayout _mergeLayout;
        private readonly VkPipeline _graphicsPipeline;
        private readonly VkPipeline _blurPipeline;
        private readonly VkPipeline _mergePipeline;

        private readonly RenderImage _glowImage;
        private readonly RenderImage _occlusionImage;
        private readonly RenderImage _blurImage;
        private readonly BufferEx _flareBuffer;

        private bool _disposed;
        private bool _prepared;
        private bool _flareMapped;
        private bool _renderingActive;
        private bool _recordingFailed;
        private MappedMemory _flareMap;

        private RenderImage? _sceneColor;
        private RenderImage? _sceneDepth;
        private RenderImage? _mainColor;
        private VkDescriptorSet _globalSet;
        private VkDescriptorSet _textureSet;
        private VkDescriptorSet _uiSet;
        private VkDescriptorSet _atlasSet;
        private GlarePushGpu _push;

        internal SolarGlowFlarePass(Core.Renderer renderer, RenderTarget borrowedTarget,
            AtlasMainPass atlas, SolarGlowShaderSources sources)
        {
            ArgumentNullException.ThrowIfNull(renderer);
            ArgumentNullException.ThrowIfNull(borrowedTarget);
            ArgumentNullException.ThrowIfNull(atlas);
            ArgumentNullException.ThrowIfNull(sources);
            if (borrowedTarget.ColorImage is not { } sceneColor
                || sceneColor.Format != ImageFormat
                || sceneColor.Samples != VkSampleCountFlags._1Bit
                || borrowedTarget.DepthImage is null)
            {
                throw new InvalidOperationException(
                    "solar glare pass unsupported: borrowed scene target signature mismatched");
            }

            _renderer = renderer;
            _borrowedTarget = borrowedTarget;
            _atlas = atlas;
            _extent = new VkExtent2D(sceneColor.Extent.Width, sceneColor.Extent.Height);

            try
            {
                _vertexModule = ShaderCompileUtil.CompileModule(renderer,
                    SolarGlowShaderSources.VertexSource, nameof(ShaderKind.GlslVertexShader), "SolarGlare.vert");
                _fragmentModule = ShaderCompileUtil.CompileModule(renderer,
                    sources.FragmentSource, nameof(ShaderKind.GlslFragmentShader), "SolarGlare.frag");
                _blurModule = ShaderCompileUtil.CompileModule(renderer,
                    sources.BlurSource, nameof(ShaderKind.GlslComputeShader), "SolarGlareBlur.comp");
                _mergeModule = ShaderCompileUtil.CompileModule(renderer,
                    sources.MergeSource, nameof(ShaderKind.GlslComputeShader), "SolarGlareMerge.comp");

                _flareBuffer = BufferExExtensions.CreateBuffer(renderer.Allocator, new BufferEx.CreateInfo
                {
                    Name = "KSARS_SolarGlare_FlareData",
                    BufferUsage = VkBufferUsageFlags.UniformBufferBit,
                    BufferSize = ByteSize.Of<SunFlareData>(),
                    AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit
                        | VkMemoryPropertyFlags.HostCoherentBit,
                });
                _flareMap = _flareBuffer.Map();
                _flareMapped = true;

                _glowImage = CreateOwnedImage("KSARS_SolarGlare_Glow",
                    VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit);
                _occlusionImage = CreateOwnedImage("KSARS_SolarGlare_Occlusion",
                    VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit);
                _blurImage = CreateOwnedImage("KSARS_SolarGlare_Blur",
                    VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit);

                _graphicsSet2Layout = CreateSetLayout(
                    Bind(0, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.FragmentBit),
                    Bind(1, VkDescriptorType.UniformBuffer, VkShaderStageFlags.FragmentBit),
                    Bind(2, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.FragmentBit));
                _emptySetLayout = CreateSetLayout();
                _blurSet2Layout = CreateSetLayout(
                    Bind(0, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit),
                    Bind(1, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
                    Bind(2, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
                    Bind(3, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit));
                _flareSetLayout = CreateSetLayout(
                    Bind(0, VkDescriptorType.UniformBuffer, VkShaderStageFlags.ComputeBit));
                _mergeSet2Layout = CreateSetLayout(
                    Bind(0, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit),
                    Bind(1, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
                    Bind(2, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit));

                _pool = DescriptorPoolExExtensions.CreateDescriptorPool(renderer.Device,
                    new DescriptorPoolEx.CreateInfo
                    {
                        MaxSets = 4,
                        PoolSizes = new VkDescriptorPoolSize[]
                        {
                            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 9 },
                            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 2 },
                            new() { Type = VkDescriptorType.StorageImage, DescriptorCount = 2 },
                        },
                    }, null);
                _graphicsSet2 = DescriptorPoolExExtensions.AllocateDescriptorSet(renderer.Device,
                    _pool, _graphicsSet2Layout);
                _blurSet2 = DescriptorPoolExExtensions.AllocateDescriptorSet(renderer.Device,
                    _pool, _blurSet2Layout);
                _flareSet = DescriptorPoolExExtensions.AllocateDescriptorSet(renderer.Device,
                    _pool, _flareSetLayout);
                _mergeSet2 = DescriptorPoolExExtensions.AllocateDescriptorSet(renderer.Device,
                    _pool, _mergeSet2Layout);

                DescriptorSetLayoutEx atlasLayout = atlas.AtlasSetLayout
                    ?? throw new InvalidOperationException(
                        "solar glare pass unsupported: atlas descriptor set layout unavailable");

                ReadOnlySpan<VkDescriptorSetLayout> graphicsSets = stackalloc VkDescriptorSetLayout[5]
                {
                    GlobalShaderBindings.DescriptorSetLayout,
                    Program.Instance.TextureSystem.Layout,
                    (VkDescriptorSetLayout)_graphicsSet2Layout,
                    (VkDescriptorSetLayout)_emptySetLayout,
                    (VkDescriptorSetLayout)atlasLayout,
                };
                _graphicsLayout = DeviceExtensions.CreatePipelineLayout(renderer.Device,
                    graphicsSets, PushRanges(VkShaderStageFlags.FragmentBit), null);

                ReadOnlySpan<VkDescriptorSetLayout> blurSets = stackalloc VkDescriptorSetLayout[6]
                {
                    GlobalShaderBindings.DescriptorSetLayout,
                    Program.Instance.TextureSystem.Layout,
                    (VkDescriptorSetLayout)_blurSet2Layout,
                    (VkDescriptorSetLayout)_flareSetLayout,
                    (VkDescriptorSetLayout)atlasLayout,
                    (VkDescriptorSetLayout)Program.Instance.UiCoverageMask.MaskSetLayout,
                };
                _blurLayout = DeviceExtensions.CreatePipelineLayout(renderer.Device,
                    blurSets, PushRanges(VkShaderStageFlags.ComputeBit), null);

                ReadOnlySpan<VkDescriptorSetLayout> mergeSets = stackalloc VkDescriptorSetLayout[4]
                {
                    GlobalShaderBindings.DescriptorSetLayout,
                    Program.Instance.TextureSystem.Layout,
                    (VkDescriptorSetLayout)_mergeSet2Layout,
                    (VkDescriptorSetLayout)Program.Instance.UiCoverageMask.MaskSetLayout,
                };
                _mergeLayout = DeviceExtensions.CreatePipelineLayout(renderer.Device,
                    mergeSets, PushRanges(VkShaderStageFlags.ComputeBit), null);

                _graphicsPipeline = BuildGraphicsPipeline();
                _blurPipeline = CreateComputePipeline(_blurLayout, _blurModule, "SolarGlareBlur");
                _mergePipeline = CreateComputePipeline(_mergeLayout, _mergeModule, "SolarGlareMerge");

                var flareInfo = new VkDescriptorBufferInfo
                {
                    Buffer = _flareBuffer.VkBuffer,
                    Offset = ByteSize.Zero,
                    Range = ByteSize.Of<SunFlareData>(),
                };
                WriteBufferDescriptor(_graphicsSet2, 1, in flareInfo);
                WriteBufferDescriptor(_flareSet, 0, in flareInfo);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static VkDescriptorSetLayoutBinding Bind(int binding, VkDescriptorType type,
            VkShaderStageFlags stages) =>
            new() { Binding = binding, DescriptorType = type, DescriptorCount = 1, StageFlags = stages };

        private static ReadOnlySpan<VkPushConstantRange> PushRanges(VkShaderStageFlags stages) =>
            new VkPushConstantRange[]
            {
                new() { StageFlags = stages, Offset = ByteSize.Zero, Size = ByteSize.Of<GlarePushGpu>() },
            };

        internal AtlasMainPass Atlas => _atlas;

        private DescriptorSetLayoutEx CreateSetLayout(params VkDescriptorSetLayoutBinding[] bindings)
        {
            var info = new DescriptorSetLayoutEx.CreateInfo { Bindings = bindings };
            return _renderer.Device.CreateDescriptorSetLayout(info, null);
        }

        private unsafe RenderImage CreateOwnedImage(string name, VkImageUsageFlags usage)
        {
            var info = new RenderImageCreateInfo
            {
                Name = name,
                Allocator = _renderer.Allocator,
                Device = _renderer.Device,
                Width = _extent.Width,
                Height = _extent.Height,
                Depth = 1,
                Format = ImageFormat,
                Usage = usage,
                MipLevels = 1,
                ArrayLayers = 1,
                Kind = RenderImageKind.Image2D,
                ViewMode = RenderImageViewMode.DefaultView,
                Samples = VkSampleCountFlags._1Bit,
            };
            return new RenderImage(in info);
        }

        private unsafe VkPipeline BuildGraphicsPipeline()
        {
            Span<VkPipelineShaderStageCreateInfo> stages = stackalloc VkPipelineShaderStageCreateInfo[2]
            {
                Presets.ShaderStage.Vertex(_vertexModule),
                Presets.ShaderStage.Fragment(_fragmentModule),
            };
            Span<VkPipelineColorBlendAttachmentState> attachments =
                stackalloc VkPipelineColorBlendAttachmentState[2]
                {
                    Presets.BlendAttachment.BlendNone.Value,
                    Presets.BlendAttachment.BlendNone.Value,
                };
            var blend = new VkPipelineColorBlendStateCreateInfo
            {
                AttachmentCount = 2,
                Attachments = attachments.AsPointer(),
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo
            {
                RasterizationSamples = VkSampleCountFlags._1Bit,
            };
            Span<VkFormat> formats = stackalloc VkFormat[2] { ImageFormat, ImageFormat };
            var renderingInfo = new VkPipelineRenderingCreateInfo
            {
                ColorAttachmentCount = 2,
                ColorAttachmentFormats = formats.AsPointer(),
                DepthAttachmentFormat = VkFormat.Undefined,
                StencilAttachmentFormat = VkFormat.Undefined,
            };
            var info = new VkGraphicsPipelineCreateInfo
            {
                Layout = _graphicsLayout,
                Next = &renderingInfo,
                StageCount = stages.Length,
                Stages = stages.AsPointer(),
                DynamicState = _renderer.DynamicStateInfo,
                ViewportState = _renderer.ViewportState,
                VertexInputState = new VertexInput(0, 0),
                InputAssemblyState = Presets.InputAssembly.TriangleList,
                RasterizationState = Presets.Rasterization.Fill.CullNone,
                DepthStencilState = RenderingPresets.ReverseZDepthStencil.NoDepthTest,
                MultisampleState = &multisample,
                ColorBlendState = &blend,
            };
            return DeviceExtensions.CreateGraphicsPipeline(_renderer.Device,
                Core.PipelineCaches.Shared, info, null);
        }

        private unsafe VkPipeline CreateComputePipeline(VkPipelineLayout layout,
            VkShaderModule module, string name)
        {
            var createInfo = new VkComputePipelineCreateInfo
            {
                Layout = layout,
                Stage = Presets.ShaderStage.Compute(module),
            };
            ReadOnlySpan<VkComputePipelineCreateInfo> createInfos =
                new ReadOnlySpan<VkComputePipelineCreateInfo>(in createInfo);
            Span<VkPipeline> pipelines = stackalloc VkPipeline[1];
            VkResult result = VkDeviceExtensions.CreateComputePipelines(_renderer.Device,
                Core.PipelineCaches.Shared, createInfos, null, pipelines);
            if (result != VkResult.Success || pipelines[0].IsNull())
            {
                throw new InvalidOperationException(
                    $"solar glare pass unsupported: {name} compute pipeline creation failed (VkResult {result})");
            }

            return pipelines[0];
        }

        private unsafe void WriteBufferDescriptor(VkDescriptorSet set, int binding,
            in VkDescriptorBufferInfo info)
        {
            fixed (VkDescriptorBufferInfo* ptr = &info)
            {
                var write = new VkWriteDescriptorSet
                {
                    DescriptorType = VkDescriptorType.UniformBuffer,
                    DescriptorCount = 1,
                    DstBinding = binding,
                    DstSet = set,
                    BufferInfo = ptr,
                };
                _renderer.Device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(in write), default);
            }
        }

        private unsafe void WriteImageDescriptor(VkDescriptorSet set, int binding,
            VkDescriptorType type, in VkDescriptorImageInfo info)
        {
            fixed (VkDescriptorImageInfo* ptr = &info)
            {
                var write = new VkWriteDescriptorSet
                {
                    DescriptorType = type,
                    DescriptorCount = 1,
                    DstBinding = binding,
                    DstSet = set,
                    ImageInfo = ptr,
                };
                _renderer.Device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(in write), default);
            }
        }

        internal bool Prepared => _prepared;

        internal bool RecordingRecovered => !_recordingFailed;

        // Root owned resources and uploads against the current borrowed scene targets.
        // Descriptor writes happen here only; Record never updates descriptors.
        internal void Prepare(IViewport viewport, RenderTarget sceneTarget,
            in SunFlareData flareData, in GlarePushGpu push)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SolarGlowFlarePass));
            }

            _prepared = false;
            if (!ReferenceEquals(viewport, Program.MainViewport))
            {
                throw new InvalidOperationException(
                    "solar glare pass unsupported: prepare is main-viewport only");
            }

            if (!ReferenceEquals(sceneTarget, _borrowedTarget)
                || sceneTarget.ColorImage is not { } sceneColor
                || sceneColor.Format != ImageFormat
                || sceneColor.Samples != VkSampleCountFlags._1Bit
                || sceneColor.Extent.Width != _extent.Width
                || sceneColor.Extent.Height != _extent.Height
                || sceneTarget.DepthImage is not { } sceneDepth)
            {
                throw new RendererTargetChangedException(
                    "solar glare pass unsupported: borrowed scene target signature changed");
            }

            RenderImage mainColor = sceneColor;
            if (GameSettings.GetSampleCount() != VkSampleCountFlags._1Bit
                && Program.Instance.GetTransparenciesMsaaColorTarget()?.ColorImage is { } msaa)
            {
                if (msaa.Format != ImageFormat || msaa.Width != _extent.Width
                    || msaa.Height != _extent.Height)
                {
                    throw new RendererTargetChangedException(
                        "solar glare pass unsupported: transparencies MSAA color signature changed");
                }

                mainColor = msaa;
            }

            if (!_atlas.TryGetAtlasBinding(out VkDescriptorSet atlasSet, out _))
            {
                throw new InvalidOperationException(
                    "solar glare pass unsupported: atlas descriptor set unavailable");
            }

            _globalSet = GlobalShaderBindings.DescriptorSet(viewport.ShaderSlot);
            _textureSet = Program.Instance.TextureSystem.DescriptorSet;
            _uiSet = Program.Instance.UiCoverageMask.MaskSet;
            _atlasSet = atlasSet;
            _sceneColor = sceneColor;
            _sceneDepth = sceneDepth;
            _mainColor = mainColor;
            _push = push;

            _flareMap.Offset(ByteSize.Zero).As<SunFlareData>() = flareData;

            var depthInfo = new VkDescriptorImageInfo
            {
                Sampler = Program.PointClampedSampler,
                ImageView = sceneDepth.ImageView,
                ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal,
            };
            var mainColorInfo = new VkDescriptorImageInfo
            {
                Sampler = Program.LinearClampedSampler,
                ImageView = mainColor.ImageView,
                ImageLayout = VkImageLayout.General,
            };
            WriteImageDescriptor(_graphicsSet2, 0, VkDescriptorType.CombinedImageSampler, in depthInfo);
            WriteImageDescriptor(_graphicsSet2, 2, VkDescriptorType.CombinedImageSampler, in mainColorInfo);

            var blurStorageInfo = new VkDescriptorImageInfo
            {
                ImageView = _blurImage.ImageView,
                ImageLayout = VkImageLayout.General,
            };
            var glowReadInfo = new VkDescriptorImageInfo
            {
                Sampler = Program.LinearClampedSampler,
                ImageView = _glowImage.ImageView,
                ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
            };
            var occlusionReadInfo = new VkDescriptorImageInfo
            {
                Sampler = Program.LinearClampedSampler,
                ImageView = _occlusionImage.ImageView,
                ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
            };
            WriteImageDescriptor(_blurSet2, 0, VkDescriptorType.StorageImage, in blurStorageInfo);
            WriteImageDescriptor(_blurSet2, 1, VkDescriptorType.CombinedImageSampler, in depthInfo);
            WriteImageDescriptor(_blurSet2, 2, VkDescriptorType.CombinedImageSampler, in glowReadInfo);
            WriteImageDescriptor(_blurSet2, 3, VkDescriptorType.CombinedImageSampler, in occlusionReadInfo);

            var sceneStorageInfo = new VkDescriptorImageInfo
            {
                ImageView = sceneColor.ImageView,
                ImageLayout = VkImageLayout.General,
            };
            var blurReadInfo = new VkDescriptorImageInfo
            {
                Sampler = Program.LinearClampedSampler,
                ImageView = _blurImage.ImageView,
                ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
            };
            WriteImageDescriptor(_mergeSet2, 0, VkDescriptorType.StorageImage, in sceneStorageInfo);
            WriteImageDescriptor(_mergeSet2, 1, VkDescriptorType.CombinedImageSampler, in depthInfo);
            WriteImageDescriptor(_mergeSet2, 2, VkDescriptorType.CombinedImageSampler, in blurReadInfo);

            _prepared = true;
        }

        internal unsafe void Record(CommandBuffer commandBuffer)
        {
            if (!_prepared || _sceneColor is null || _sceneDepth is null || _mainColor is null)
            {
                throw new InvalidOperationException("solar glare pass record called without prepare");
            }

            {
                Span<VkImageMemoryBarrier2> span = stackalloc VkImageMemoryBarrier2[4];
                var batch = new BarrierBatch(span);
                batch.Add(_sceneDepth, ImageBarrierInfo.Presets.DepthSampledReadVfc, 0, false, true);
                batch.Add(_mainColor, ImageBarrierInfo.Presets.SampledReadVfc, 0, false, true);
                batch.Add(_glowImage, ImageBarrierInfo.Presets.ColorAttachmentWrite, 0, false, true);
                batch.Add(_occlusionImage, ImageBarrierInfo.Presets.ColorAttachmentWrite, 0, false, true);
                batch.SubmitAndFlush(commandBuffer);
            }

            var clear = new VkClearValue();
            Span<VkRenderingAttachmentInfo> attachments = stackalloc VkRenderingAttachmentInfo[2]
            {
                new()
                {
                    ImageView = _glowImage.ImageView,
                    ImageLayout = VkImageLayout.ColorAttachmentOptimal,
                    LoadOp = VkAttachmentLoadOp.Clear,
                    StoreOp = VkAttachmentStoreOp.Store,
                    ClearValue = clear,
                },
                new()
                {
                    ImageView = _occlusionImage.ImageView,
                    ImageLayout = VkImageLayout.ColorAttachmentOptimal,
                    LoadOp = VkAttachmentLoadOp.Clear,
                    StoreOp = VkAttachmentStoreOp.Store,
                    ClearValue = clear,
                },
            };
            var renderingInfo = new VkRenderingInfo
            {
                RenderArea = new VkRect2D(_extent.Width, _extent.Height),
                LayerCount = 1,
                ColorAttachmentCount = 2,
                ColorAttachments = attachments.AsPointer(),
            };

            _renderingActive = false;
            try
            {
                commandBuffer.BeginRendering(in renderingInfo);
                _renderingActive = true;

                VkDeviceExtensions.BindPipeline(commandBuffer, VkPipelineBindPoint.Graphics,
                    _graphicsPipeline);
                Program.SetViewport(commandBuffer);
                ReadOnlySpan<VkDescriptorSet> baseSets = stackalloc VkDescriptorSet[3]
                {
                    _globalSet, _textureSet, _graphicsSet2,
                };
                VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics,
                    _graphicsLayout, 0, baseSets, default(Span<ByteSize32>));
                ReadOnlySpan<VkDescriptorSet> atlasSets = stackalloc VkDescriptorSet[1] { _atlasSet };
                VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics,
                    _graphicsLayout, 4, atlasSets, default(Span<ByteSize32>));
                CommandBufferExtensions.PushConstants(commandBuffer, _graphicsLayout,
                    VkShaderStageFlags.FragmentBit, ByteSize.Zero, _push);
                commandBuffer.Draw(3, 1, 0, 0);

                commandBuffer.EndRendering();
                _renderingActive = false;
            }
            catch
            {
                if (_renderingActive)
                {
                    try
                    {
                        commandBuffer.EndRendering();
                    }
                    catch
                    {
                        _recordingFailed = true;
                    }
                    finally
                    {
                        _renderingActive = false;
                    }
                }
                else
                {
                    _recordingFailed = true;
                }

                throw;
            }

            {
                Span<VkImageMemoryBarrier2> span = stackalloc VkImageMemoryBarrier2[3];
                var batch = new BarrierBatch(span);
                batch.Add(_glowImage, ImageBarrierInfo.Presets.SampledReadC, 0, false, true);
                batch.Add(_occlusionImage, ImageBarrierInfo.Presets.SampledReadC, 0, false, true);
                batch.Add(_blurImage, ImageBarrierInfo.Presets.StorageWriteC, 0, false, true);
                batch.SubmitAndFlush(commandBuffer);
            }

            int groupsX = (_extent.Width + LocalSize - 1) / LocalSize;
            int groupsY = (_extent.Height + LocalSize - 1) / LocalSize;

            VkDeviceExtensions.BindPipeline(commandBuffer, VkPipelineBindPoint.Compute, _blurPipeline);
            {
                ReadOnlySpan<VkDescriptorSet> baseSets = stackalloc VkDescriptorSet[3]
                {
                    _globalSet, _textureSet, _blurSet2,
                };
                VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute,
                    _blurLayout, 0, baseSets, default(Span<ByteSize32>));
                ReadOnlySpan<VkDescriptorSet> tailSets = stackalloc VkDescriptorSet[3]
                {
                    _flareSet, _atlasSet, _uiSet,
                };
                VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute,
                    _blurLayout, 3, tailSets, default(Span<ByteSize32>));
                CommandBufferExtensions.PushConstants(commandBuffer, _blurLayout,
                    VkShaderStageFlags.ComputeBit, ByteSize.Zero, _push);
                commandBuffer.Dispatch(groupsX, groupsY, 1);
            }

            {
                Span<VkImageMemoryBarrier2> span = stackalloc VkImageMemoryBarrier2[2];
                var batch = new BarrierBatch(span);
                batch.Add(_blurImage, ImageBarrierInfo.Presets.SampledReadC, 0, false, true);
                batch.Add(_sceneColor, ImageBarrierInfo.Presets.StorageReadWriteC, 0, false, true);
                batch.SubmitAndFlush(commandBuffer);
            }

            VkDeviceExtensions.BindPipeline(commandBuffer, VkPipelineBindPoint.Compute, _mergePipeline);
            {
                ReadOnlySpan<VkDescriptorSet> baseSets = stackalloc VkDescriptorSet[3]
                {
                    _globalSet, _textureSet, _mergeSet2,
                };
                VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute,
                    _mergeLayout, 0, baseSets, default(Span<ByteSize32>));
                ReadOnlySpan<VkDescriptorSet> uiSets = stackalloc VkDescriptorSet[1] { _uiSet };
                VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute,
                    _mergeLayout, 3, uiSets, default(Span<ByteSize32>));
                CommandBufferExtensions.PushConstants(commandBuffer, _mergeLayout,
                    VkShaderStageFlags.ComputeBit, ByteSize.Zero, _push);
                commandBuffer.Dispatch(groupsX, groupsY, 1);
            }

            _prepared = false;
        }

        internal sealed class RendererTargetChangedException : InvalidOperationException
        {
            internal RendererTargetChangedException(string message) : base(message) { }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var errors = new List<Exception>();
            foreach (Action action in new Action[]
            {
                () =>
                {
                    if (_graphicsPipeline.IsNotNull())
                        _renderer.Device.DestroyPipeline(_graphicsPipeline, null);
                },
                () =>
                {
                    if (_blurPipeline.IsNotNull())
                        _renderer.Device.DestroyPipeline(_blurPipeline, null);
                },
                () =>
                {
                    if (_mergePipeline.IsNotNull())
                        _renderer.Device.DestroyPipeline(_mergePipeline, null);
                },
                () =>
                {
                    if (_graphicsLayout.IsNotNull())
                        _renderer.Device.DestroyPipelineLayout(_graphicsLayout, null);
                },
                () =>
                {
                    if (_blurLayout.IsNotNull())
                        _renderer.Device.DestroyPipelineLayout(_blurLayout, null);
                },
                () =>
                {
                    if (_mergeLayout.IsNotNull())
                        _renderer.Device.DestroyPipelineLayout(_mergeLayout, null);
                },
                () =>
                {
                    if (_vertexModule.IsNotNull())
                        _renderer.Device.DestroyShaderModule(_vertexModule, null);
                },
                () =>
                {
                    if (_fragmentModule.IsNotNull())
                        _renderer.Device.DestroyShaderModule(_fragmentModule, null);
                },
                () =>
                {
                    if (_blurModule.IsNotNull())
                        _renderer.Device.DestroyShaderModule(_blurModule, null);
                },
                () =>
                {
                    if (_mergeModule.IsNotNull())
                        _renderer.Device.DestroyShaderModule(_mergeModule, null);
                },
                () => _glowImage?.Dispose(),
                () => _occlusionImage?.Dispose(),
                () => _blurImage?.Dispose(),
                () =>
                {
                    if (_flareMapped)
                    {
                        _flareMap.Unmap();
                        _flareMapped = false;
                    }

                    if (!_flareBuffer.IsNull())
                    {
                        _flareBuffer.Dispose();
                    }
                },
                () => _pool?.Dispose(),
                () => _graphicsSet2Layout?.Dispose(),
                () => _emptySetLayout?.Dispose(),
                () => _blurSet2Layout?.Dispose(),
                () => _flareSetLayout?.Dispose(),
                () => _mergeSet2Layout?.Dispose(),
            })
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            if (errors.Count > 0)
            {
                throw new AggregateException(errors);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct GlarePushGpu
        {
            internal float4 ObserverUq;
            internal float4 SolarRest;
            internal int4 Info;
            internal float4 GlareScale;
        }
    }
}

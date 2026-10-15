using System.Runtime.InteropServices;
using Brutal;
using Brutal.ImGuiApi;
using Brutal.Memory;
using Brutal.Numerics;
using Brutal.Pointers.Extensions;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using KSA.Rendering;

namespace KSARS.Visuals
{
    internal sealed class SolarDepthInspectionProbe : IDisposable
    {
        internal const int InspectionWidth = 3 * SolarCoverageProbe.SourceTileSize;
        internal const int InspectionHeight = SolarCoverageProbe.SourceTileSize;

        internal const string FragmentSource = """
            #version 450
            layout(set = 0, binding = 0) uniform sampler2D sourceDepth;
            layout(push_constant) uniform InspectParams { vec4 geometry; } params;
            layout(location = 0) out vec4 outColor;
            void main()
            {
                ivec2 size = textureSize(sourceDepth, 0);
                ivec2 outputCoord = ivec2(gl_FragCoord.xy);
                int pane = outputCoord.x / size.x;
                ivec2 coord = ivec2(outputCoord.x % size.x, outputCoord.y);
                if (pane < 0 || pane > 2 || any(lessThan(coord, ivec2(0))) || any(greaterThanEqual(coord, size)))
                {
                    outColor = vec4(1.0, 1.0, 0.0, 1.0);
                    return;
                }
                float measuredDepth = texelFetch(sourceDepth, coord, 0).r;
                if (isnan(measuredDepth) || isinf(measuredDepth) || measuredDepth < 0.0 || measuredDepth > 1.0)
                {
                    outColor = vec4(1.0, 1.0, 0.0, 1.0);
                    return;
                }
                vec2 uv = (vec2(coord) + 0.5) / vec2(size);
                vec3 ray = normalize(vec3((uv * 2.0 - 1.0) * params.geometry.x, 1.0));
                float r = params.geometry.y;
                float disc = r * r - dot(ray.xy, ray.xy);
                bool expectedHit = disc >= 0.0;
                if (pane == 0)
                {
                    outColor = vec4(vec3(measuredDepth > 0.0 ? 1.0 : 0.0), 1.0);
                    return;
                }
                if (measuredDepth == 0.0)
                {
                    outColor = pane == 2 && expectedHit ? vec4(1.0, 0.0, 1.0, 1.0) : vec4(0.0, 0.0, 0.0, 1.0);
                    return;
                }
                float n = params.geometry.z;
                float f = params.geometry.w;
                float sourceZ = n * f / (n + measuredDepth * (f - n));
                float measuredRadial = sourceZ / ray.z;
                if (isnan(measuredRadial) || isinf(measuredRadial) || !(measuredRadial > 0.0))
                {
                    outColor = vec4(1.0, 1.0, 0.0, 1.0);
                    return;
                }
                if (pane == 1)
                {
                    float frontness = clamp((1.0 - measuredRadial) / r, 0.0, 1.0);
                    outColor = vec4(vec3(0.25 + 0.75 * frontness), 1.0);
                    return;
                }
                if (!expectedHit)
                {
                    outColor = vec4(1.0, 0.0, 0.0, 1.0);
                    return;
                }
                float expectedRadial = ray.z - sqrt(disc);
                float relativeRadiusError = abs(measuredRadial - expectedRadial) / r;
                float errorVis = clamp(relativeRadiusError * 1000.0, 0.0, 1.0);
                outColor = vec4(errorVis, 1.0 - errorVis, 0.0, 1.0);
            }
            """;

        private readonly Core.Renderer _renderer;
        private readonly RenderImage _sourceDepth;
        private RenderTarget? _target;
        private VkShaderModule _vertexModule;
        private VkShaderModule _fragmentModule;
        private DescriptorSetLayoutEx? _setLayout;
        private DescriptorPoolEx? _pool;
        private VkDescriptorSet _set;
        private VkPipelineLayout _pipelineLayout;
        private VkPipeline _pipeline;
        private bool _disposed;

        internal ImTextureRef Texture;
        internal bool TextureRegistered;
        internal ImGuiBackendVulkanImpl? Backend;
        internal bool RecordingRecovered { get; private set; } = true;

        internal SolarDepthInspectionProbe(Core.Renderer renderer, RenderImage sourceDepth)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            if (sourceDepth is null
                || sourceDepth.Format != VkFormat.D32SFloat
                || sourceDepth.Width != SolarCoverageProbe.SourceTileSize
                || sourceDepth.Height != SolarCoverageProbe.SourceTileSize)
            {
                throw new ArgumentException(
                    "source depth must be the owned 512x512 D32SFloat coverage depth image",
                    nameof(sourceDepth));
            }

            _sourceDepth = sourceDepth;
            try
            {
                _target = new RenderTarget(renderer, "KSARS_SolarDepthInspection_Target",
                    new VkExtent2D(InspectionWidth, InspectionHeight),
                    VkFormat.R32G32B32A32SFloat, VkFormat.Undefined, 1, VkSampleCountFlags._1Bit, true);

                _vertexModule = CompileModule(AtlasCompositeProbe.VertexSource,
                    ShaderKind.GlslVertexShader, "SolarDepthInspection.vert");
                _fragmentModule = CompileModule(FragmentSource,
                    ShaderKind.GlslFragmentShader, "SolarDepthInspection.frag");

                var bindings = new VkDescriptorSetLayoutBinding[1]
                {
                    new()
                    {
                        Binding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.CombinedImageSampler,
                        StageFlags = VkShaderStageFlags.FragmentBit,
                    },
                };
                _setLayout = renderer.Device.CreateDescriptorSetLayout(
                    new DescriptorSetLayoutEx.CreateInfo { Bindings = bindings }, null);
                _pool = DescriptorPoolExExtensions.CreateDescriptorPool(renderer.Device,
                    new DescriptorPoolEx.CreateInfo
                    {
                        MaxSets = 1,
                        PoolSizes = new VkDescriptorPoolSize[]
                        {
                            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 },
                        },
                    },
                    null);
                _set = DescriptorPoolExExtensions.AllocateDescriptorSet(renderer.Device, _pool, _setLayout);

                var depthInfo = new VkDescriptorImageInfo
                {
                    Sampler = Program.PointClampedSampler,
                    ImageView = _sourceDepth.ImageView,
                    ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal,
                };
                WriteImageDescriptor(_set, 0, in depthInfo);

                var pushRange = new VkPushConstantRange
                {
                    StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
                    Offset = ByteSize.Zero,
                    Size = ByteSize.Of<SolarInspectionPush>(),
                };
                ReadOnlySpan<VkPushConstantRange> pushRanges = stackalloc VkPushConstantRange[1] { pushRange };
                ReadOnlySpan<VkDescriptorSetLayout> setLayouts = stackalloc VkDescriptorSetLayout[1]
                {
                    (VkDescriptorSetLayout)_setLayout,
                };
                _pipelineLayout = DeviceExtensions.CreatePipelineLayout(_renderer.Device,
                    setLayouts, pushRanges, null);

                BuildPipeline();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void Record(CommandBuffer commandBuffer, in SolarSourceGeometry model)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SolarDepthInspectionProbe));
            }

            var push = new SolarInspectionPush
            {
                Geometry = new float4(
                    (float)model.TanHalfFov,
                    (float)model.PhysicalRadiusRatio,
                    (float)model.NearRatio,
                    (float)model.FarRatio),
            };
            if (!SolarCoverageProbe.GeometryUploadSupported(push.Geometry.X, push.Geometry.Y,
                    push.Geometry.Z, push.Geometry.W))
            {
                throw new InvalidOperationException("solar depth inspection geometry parameters unsupported for float upload");
            }

            CommandBufferEx.PipelineBarrier2(commandBuffer, _sourceDepth,
                ImageBarrierInfo.Presets.DepthSampledReadVfc, 0, false, true);

            RecordingRecovered = false;
            bool beginAttempted = false;
            bool began = false;
            bool ended = false;
            try
            {
                try
                {
                    beginAttempted = true;
                    _target!.BeginRendering(commandBuffer, new VkClearValue(), new VkClearValue(),
                        VkAttachmentLoadOp.Clear, VkAttachmentLoadOp.Clear);
                    began = true;
                    VkDeviceExtensions.BindPipeline(commandBuffer, VkPipelineBindPoint.Graphics, _pipeline);

                    var viewport = new VkViewport
                    {
                        Width = InspectionWidth,
                        Height = InspectionHeight,
                        MinDepth = 0.0f,
                        MaxDepth = 1.0f,
                    };
                    ReadOnlySpan<VkViewport> viewports = stackalloc VkViewport[1] { viewport };
                    VkDeviceExtensions.SetViewport(commandBuffer, 0, viewports);
                    var scissor = new VkRect2D(_target.Extent);
                    ReadOnlySpan<VkRect2D> scissors = stackalloc VkRect2D[1] { scissor };
                    VkDeviceExtensions.SetScissor(commandBuffer, 0, scissors);

                    ReadOnlySpan<VkDescriptorSet> sets = stackalloc VkDescriptorSet[1] { _set };
                    VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics,
                        _pipelineLayout, 0, sets, default(Span<ByteSize32>));
                    CommandBufferExtensions.PushConstants(commandBuffer, _pipelineLayout,
                        VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
                        ByteSize.Zero, push);
                    commandBuffer.Draw(3, 1, 0, 0);
                }
                finally
                {
                    if (began)
                    {
                        _target!.EndRendering(commandBuffer);
                        ended = true;
                    }
                }
            }
            finally
            {
                Program.SetViewport(commandBuffer);
                if (SolarCoverageProbe.RecordingRecoveredState(ended, beginAttempted))
                {
                    RecordingRecovered = true;
                }
            }
        }

        internal void RegisterPreviewTexture()
        {
            if (TextureRegistered || _target?.ColorImage is not { } colorImage)
            {
                return;
            }

            ImGuiBackendVulkanImpl backend = ImGuiBackend.Vulkan;
            Backend = backend;
            Texture = backend.AddTexture(Program.LinearClampedSampler, colorImage.ImageView,
                VkImageLayout.ShaderReadOnlyOptimal);
            TextureRegistered = true;
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

        private unsafe VkShaderModule CompileModule(string source, ShaderKind kind, string name)
        {
            using Compiler compiler = ShaderC.PInvoke.shaderc_compiler_initialize();
            using CompileOptions options = ShaderC.PInvoke.shaderc_compile_options_initialize();
            options.SetOptimizationLevel(OptimizationLevel.Performance);

            using CompilationResult result = compiler.Compile(source, kind, name, "main", options);
            if (result.GetCompilationStatus() != CompilationStatus.Success)
            {
                throw new InvalidOperationException(
                    $"shader compile failed for {name}: status={result.GetCompilationStatus()} errors={result.GetNumErrors().Value} {result.GetErrorMessage()}");
            }

            int length = checked((int)result.GetLength().Value);
            byte[] bytes = new ReadOnlySpan<byte>(result.GetBytes(), length).ToArray();
            return ShaderModuleExtensions.FromSpirv(_renderer.Device, bytes, null);
        }

        private unsafe void BuildPipeline()
        {
            Span<VkPipelineShaderStageCreateInfo> stages = stackalloc VkPipelineShaderStageCreateInfo[2]
            {
                Presets.ShaderStage.Vertex(_vertexModule),
                Presets.ShaderStage.Fragment(_fragmentModule),
            };
            var info = new VkGraphicsPipelineCreateInfo
            {
                Layout = _pipelineLayout,
                Subpass = 0,
                StageCount = stages.Length,
                Stages = stages.AsPointer(),
                DynamicState = _renderer.DynamicStateInfo,
                ViewportState = _renderer.ViewportState,
                VertexInputState = new VertexInput(0, 0),
                InputAssemblyState = Presets.InputAssembly.TriangleList,
                RasterizationState = Presets.Rasterization.Fill.CullNone,
                DepthStencilState = Presets.DepthStencil.NoDepthTest,
                ColorBlendState = Presets.BlendState.BlendNone,
            };
            _target!.SetupGraphicsPipeline(ref info);
            _pipeline = DeviceExtensions.CreateGraphicsPipeline(_renderer.Device,
                Core.PipelineCaches.Shared, info, null);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            List<Exception>? errors = null;
            try
            {
                if (TextureRegistered && Backend is not null)
                {
                    Backend.RemoveTexture(Texture);
                    TextureRegistered = false;
                }
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }

            try
            {
                if (!_pipeline.IsNull())
                {
                    _renderer.Device.DestroyPipeline(_pipeline, null);
                }
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }

            try
            {
                if (!_pipelineLayout.IsNull())
                {
                    _renderer.Device.DestroyPipelineLayout(_pipelineLayout, null);
                }
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }

            try
            {
                if (!_vertexModule.IsNull())
                {
                    _renderer.Device.DestroyShaderModule(_vertexModule, null);
                }
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }

            try
            {
                if (!_fragmentModule.IsNull())
                {
                    _renderer.Device.DestroyShaderModule(_fragmentModule, null);
                }
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }

            try
            {
                _pool?.Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }

            try
            {
                _setLayout?.Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }

            try
            {
                _target?.Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }

            if (errors is not null)
            {
                throw new AggregateException("solar depth inspection probe teardown had per-resource failures", errors);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SolarInspectionPush
        {
            internal float4 Geometry;
        }
    }
}

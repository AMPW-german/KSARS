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
    internal sealed class SolarCoverageProbe : IDisposable
    {
        internal const int SourceTileSize = 512;

        internal const string FragmentSource = """
            #version 450
            layout(push_constant) uniform CoverageParams { vec4 geometry; } params;
            layout(location = 0) in vec2 inUv;
            layout(location = 0) out vec4 outColor;
            void main()
            {
                vec2 p = (inUv * 2.0 - 1.0) * params.geometry.x;
                vec3 ray = normalize(vec3(p, 1.0));
                float transverse2 = dot(ray.xy, ray.xy);
                float r = params.geometry.y;
                float physicalDisc = r * r - transverse2;
                float proceduralDisc = (2.75 * r) * (2.75 * r) - transverse2;
                float envelopeDisc = (4.0 * r) * (4.0 * r) - transverse2;
                outColor = vec4(physicalDisc >= 0.0 ? 1.0 : 0.0,
                                proceduralDisc >= 0.0 ? 1.0 : 0.0,
                                envelopeDisc >= 0.0 ? 1.0 : 0.0, 1.0);
                gl_FragDepth = 0.0;
                if (physicalDisc >= 0.0)
                {
                    float radial = ray.z - sqrt(physicalDisc);
                    float sourceZ = radial * ray.z;
                    float nearRatio = params.geometry.z;
                    float farRatio = params.geometry.w;
                    float depth = (nearRatio * farRatio / sourceZ - nearRatio) / (farRatio - nearRatio);
                    if (radial > 0.0 && sourceZ > 0.0 && !isnan(depth) && !isinf(depth) && depth > 0.0 && depth <= 1.0)
                    {
                        gl_FragDepth = depth;
                    }
                }
            }
            """;

        private readonly Core.Renderer _renderer;
        private RenderTarget? _target;
        private VkShaderModule _vertexModule;
        private VkShaderModule _fragmentModule;
        private VkPipelineLayout _pipelineLayout;
        private VkPipeline _pipeline;
        private PtrOwner<VkPipelineDepthStencilStateCreateInfo>? _depthState;
        private bool _disposed;

        internal ImTextureRef Texture;
        internal bool TextureRegistered;
        internal ImGuiBackendVulkanImpl? Backend;
        internal bool RecordingRecovered { get; private set; } = true;

        internal RenderImage DepthImage
        {
            get
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(SolarCoverageProbe));
                }

                return _target?.DepthImage
                    ?? throw new InvalidOperationException("solar coverage depth image unavailable");
            }
        }

        internal SolarCoverageProbe(Core.Renderer renderer)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            try
            {
                _target = new RenderTarget(renderer, "KSARS_SolarCoverage_Target",
                    new VkExtent2D(SourceTileSize, SourceTileSize),
                    VkFormat.R32G32B32A32SFloat, VkFormat.D32SFloat, 1, VkSampleCountFlags._1Bit, true);

                _vertexModule = CompileModule(AtlasCompositeProbe.VertexSource,
                    ShaderKind.GlslVertexShader, "SolarCoverage.vert");
                _fragmentModule = CompileModule(FragmentSource,
                    ShaderKind.GlslFragmentShader, "SolarCoverage.frag");

                _depthState = new PtrOwner<VkPipelineDepthStencilStateCreateInfo>(
                    new VkPipelineDepthStencilStateCreateInfo
                    {
                        DepthTestEnable = true,
                        DepthWriteEnable = true,
                        DepthCompareOp = VkCompareOp.Always,
                        DepthBoundsTestEnable = false,
                        StencilTestEnable = false,
                    });

                var pushRange = new VkPushConstantRange
                {
                    StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
                    Offset = ByteSize.Zero,
                    Size = ByteSize.Of<SolarCoveragePush>(),
                };
                ReadOnlySpan<VkPushConstantRange> pushRanges = stackalloc VkPushConstantRange[1] { pushRange };
                _pipelineLayout = DeviceExtensions.CreatePipelineLayout(_renderer.Device,
                    ReadOnlySpan<VkDescriptorSetLayout>.Empty, pushRanges, null);

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
                throw new ObjectDisposedException(nameof(SolarCoverageProbe));
            }

            var push = new SolarCoveragePush
            {
                Geometry = new float4(
                    (float)model.TanHalfFov,
                    (float)model.PhysicalRadiusRatio,
                    (float)model.NearRatio,
                    (float)model.FarRatio),
            };
            if (!GeometryUploadSupported(push.Geometry.X, push.Geometry.Y,
                    push.Geometry.Z, push.Geometry.W))
            {
                throw new InvalidOperationException("solar coverage geometry parameters unsupported for float upload");
            }

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
                        Width = SourceTileSize,
                        Height = SourceTileSize,
                        MinDepth = 0.0f,
                        MaxDepth = 1.0f,
                    };
                    ReadOnlySpan<VkViewport> viewports = stackalloc VkViewport[1] { viewport };
                    VkDeviceExtensions.SetViewport(commandBuffer, 0, viewports);
                    var scissor = new VkRect2D(_target.Extent);
                    ReadOnlySpan<VkRect2D> scissors = stackalloc VkRect2D[1] { scissor };
                    VkDeviceExtensions.SetScissor(commandBuffer, 0, scissors);

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
                if (RecordingRecoveredState(ended, beginAttempted))
                {
                    RecordingRecovered = true;
                }
            }
        }

        internal static bool RecordingRecoveredState(bool ended, bool beginAttempted) =>
            ended || !beginAttempted;

        internal static bool GeometryUploadSupported(float tanHalfFov, float radiusRatio,
            float nearRatio, float farRatio)
        {
            float radiusSq = radiusRatio * radiusRatio;
            return float.IsFinite(tanHalfFov) && tanHalfFov > 0.0f
                && float.IsFinite(radiusRatio) && radiusRatio > 0.0f && radiusRatio < 0.25f
                && float.IsFinite(radiusSq) && radiusSq > 0.0f
                && float.IsFinite(nearRatio) && nearRatio > 0.0f
                && float.IsFinite(farRatio) && farRatio > nearRatio;
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
                DepthStencilState = _depthState,
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
                _depthState?.Dispose();
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
                _target?.Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }

            if (errors is not null)
            {
                throw new AggregateException("solar coverage probe teardown had per-resource failures", errors);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SolarCoveragePush
        {
            internal float4 Geometry;
        }
    }
}

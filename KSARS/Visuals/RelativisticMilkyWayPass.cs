using Brutal;
using Brutal.Numerics;
using Brutal.Pointers.Extensions;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using KSA.Rendering;
using RenderCore;

namespace KSARS.Visuals
{
    // Owned relativistic Milky Way draw recorded inside the native offscreen
    // pass at the MilkyWayRenderer.RenderMilkyWay seam. Reuses the borrowed
    // main target for pipeline compatibility, the per-viewport global
    // descriptor set for the camera UBO, and the native texture set (galaxy
    // cubemap + generated noise) discovered on the live MilkyWayRenderer
    // instance; only the thermal spectrum table lives in an owned buffer.
    // The fragment shader is the installed MilkyWay.frag with two injected
    // seams: inverse aberration of the reconstructed view direction and a
    // Doppler-shifted effective-temperature transform of the band radiance.
    // The native
    // ITERATIONS specialization constant is preserved, and depth-test-no-write
    // additive blending matches the native contract.
    internal sealed class RelativisticMilkyWayPass : IDisposable
    {
        private readonly Core.Renderer _renderer;
        private readonly RenderTarget _target;
        private readonly VkFormat _colorFormat;
        private readonly VkFormat _depthFormat;
        private readonly VkSampleCountFlags _samples;
        private readonly VkDescriptorSet _textureSet;
        private readonly VkDescriptorSetLayout _textureSetLayout;
        private DescriptorSetLayoutEx? _setLayout;
        private DescriptorPoolEx? _pool;
        private VkDescriptorSet _set;
        private BufferEx _thermalBuffer;
        private VkShaderModule _vertexModule;
        private VkShaderModule _fragmentModule;
        private VkPipelineLayout _pipelineLayout;
        private VkPipeline _pipeline;
        private bool _disposed;

        internal RelativisticMilkyWayPass(Core.Renderer renderer, RenderTarget target,
            ThermalSpectrumTable table, VkDescriptorSet textureSet,
            VkDescriptorSetLayout textureSetLayout)
        {
            ArgumentNullException.ThrowIfNull(renderer);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(table);
            if (target.ColorImage is not { } colorImage || target.DepthImage is not { } depthImage)
            {
                throw new InvalidOperationException("milky way pass requires a borrowed target with color and depth images");
            }

            _renderer = renderer;
            _target = target;
            _textureSet = textureSet;
            _textureSetLayout = textureSetLayout;
            _colorFormat = colorImage.Format;
            _depthFormat = depthImage.Format;
            _samples = target.Samples;
            try
            {
                _thermalBuffer = BufferExExtensions.CreateBuffer(renderer.Allocator, new BufferEx.CreateInfo
                {
                    Name = "KSARS_RelativisticMilkyWay_Thermal",
                    BufferUsage = VkBufferUsageFlags.StorageBufferBit,
                    BufferSize = ByteSize.Of<float4>() * ThermalSpectrumTable.GpuRows,
                    AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                });
                MappedMemory thermalMemory = _thermalBuffer.Map();
                try
                {
                    table.WriteGpuTable(thermalMemory.AsSpan<float4>());
                }
                finally
                {
                    thermalMemory.Unmap();
                }

                _vertexModule = CompileModule(AtlasCompositeProbe.VertexSource,
                    ShaderKind.GlslVertexShader, "RelativisticMilkyWay.vert");
                _fragmentModule = CompileModule(MilkyWayShaderSources.LoadInstalled().Fragment,
                    ShaderKind.GlslFragmentShader, "MilkyWayRelativistic.frag");

                var bindings = new VkDescriptorSetLayoutBinding[1]
                {
                    new()
                    {
                        Binding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.StorageBuffer,
                        StageFlags = VkShaderStageFlags.FragmentBit,
                    },
                };
                _setLayout = _renderer.Device.CreateDescriptorSetLayout(
                    new DescriptorSetLayoutEx.CreateInfo { Bindings = bindings }, null);
                _pool = DescriptorPoolExExtensions.CreateDescriptorPool(_renderer.Device,
                    new DescriptorPoolEx.CreateInfo
                    {
                        MaxSets = 1,
                        PoolSizes = new VkDescriptorPoolSize[]
                        {
                            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 1 },
                        },
                    },
                    null);
                _set = DescriptorPoolExExtensions.AllocateDescriptorSet(_renderer.Device, _pool, _setLayout);

                var thermalInfo = new VkDescriptorBufferInfo
                {
                    Buffer = _thermalBuffer.VkBuffer,
                    Offset = ByteSize.Zero,
                    Range = ByteSize.Of<float4>() * ThermalSpectrumTable.GpuRows,
                };
                WriteBufferDescriptor(_set, 0, in thermalInfo);

                var pushRange = new VkPushConstantRange
                {
                    StageFlags = VkShaderStageFlags.FragmentBit,
                    Offset = ByteSize.Zero,
                    Size = ByteSize.Of<float4>(),
                };
                ReadOnlySpan<VkDescriptorSetLayout> setLayouts = stackalloc VkDescriptorSetLayout[3]
                {
                    (VkDescriptorSetLayout)GlobalShaderBindings.DescriptorSetLayout,
                    textureSetLayout,
                    (VkDescriptorSetLayout)_setLayout,
                };
                ReadOnlySpan<VkPushConstantRange> pushRanges = stackalloc VkPushConstantRange[1] { pushRange };
                _pipelineLayout = DeviceExtensions.CreatePipelineLayout(renderer.Device,
                    setLayouts, pushRanges, null);

                BuildPipeline();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void ValidateState()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RelativisticMilkyWayPass));
            }

            if (!ReferenceEquals(_target, Program.OffscreenTarget)
                || _target.ColorImage is not { } colorImage || colorImage.Format != _colorFormat
                || _target.DepthImage is not { } depthImage || depthImage.Format != _depthFormat
                || _target.Samples != _samples)
            {
                throw new InvalidOperationException("milky way pass borrowed target signature changed; restart required");
            }
        }

        internal void Record(CommandBuffer commandBuffer, IViewport viewport,
            in PresentationVelocity observer)
        {
            ValidateState();
            VkDeviceExtensions.BindPipeline(commandBuffer, VkPipelineBindPoint.Graphics, _pipeline);
            Program.SetViewport(commandBuffer);

            ReadOnlySpan<VkDescriptorSet> sets = stackalloc VkDescriptorSet[3]
            {
                GlobalShaderBindings.DescriptorSet(viewport.ShaderSlot),
                _textureSet,
                _set,
            };
            VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics,
                _pipelineLayout, 0, sets, default(Span<ByteSize32>));

            var push = new float4((float)observer.Direction.X, (float)observer.Direction.Y,
                (float)observer.Direction.Z, (float)observer.Q);
            CommandBufferExtensions.PushConstants(commandBuffer, _pipelineLayout,
                VkShaderStageFlags.FragmentBit, ByteSize.Zero, push);

            commandBuffer.Draw(4, 1, 0, 0);
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
            // The native ITERATIONS specialization constant must carry over so
            // the cloud-noise detail matches the configured quality.
            VkSpecializationMapEntry mapEntry = new()
            {
                ConstantID = 0,
                Offset = ByteSize.Zero,
                Size = ByteSize.Of<int>(),
            };
            int iterations = GameSettings.GetMilkyWayQuality();
            var specialization = new VkSpecializationInfo
            {
                MapEntryCount = 1,
                MapEntries = &mapEntry,
                DataSize = ByteSize.Of<int>(),
                Data = &iterations,
            };
            var fragmentStage = Presets.ShaderStage.Fragment(_fragmentModule);
            fragmentStage.SpecializationInfo = &specialization;
            Span<VkPipelineShaderStageCreateInfo> stages = stackalloc VkPipelineShaderStageCreateInfo[2]
            {
                Presets.ShaderStage.Vertex(_vertexModule),
                fragmentStage,
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
                InputAssemblyState = Presets.InputAssembly.TriangleStrip,
                RasterizationState = Presets.Rasterization.Fill.CullNone,
                DepthStencilState = RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite,
                ColorBlendState = RenderingPresets.BlendState.BlendAdditive,
            };
            _target.SetupGraphicsPipeline(ref info);
            _pipeline = DeviceExtensions.CreateGraphicsPipeline(_renderer.Device, Core.PipelineCaches.Shared, info, null);
        }

        private unsafe void WriteBufferDescriptor(VkDescriptorSet set, int binding,
            in VkDescriptorBufferInfo info)
        {
            fixed (VkDescriptorBufferInfo* ptr = &info)
            {
                var write = new VkWriteDescriptorSet
                {
                    DescriptorType = VkDescriptorType.StorageBuffer,
                    DescriptorCount = 1,
                    DstBinding = binding,
                    DstSet = set,
                    BufferInfo = ptr,
                };
                _renderer.Device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(in write), default);
            }
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
                if (!_thermalBuffer.IsNull())
                {
                    _thermalBuffer.Dispose();
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

            if (errors is not null)
            {
                throw new AggregateException(
                    "relativistic milky way pass teardown had per-resource failures", errors);
            }
        }
    }
}

using System.Runtime.InteropServices;
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
    // Owned relativistic catalog-star draw recorded inside the native offscreen
    // pass at the InstancedStarTechnique.Render seam. Reuses the borrowed main
    // target for pipeline compatibility and the per-viewport global descriptor
    // set for the camera UBO; the catalog and thermal tables live in owned
    // host-visible storage buffers. Stars are drawn as a faithful port of the
    // stock sprite (UV-octagon profile, magnitude-scaled size, native +1 clip-z
    // bias, alpha=1) multiplied by the relativistic luminance ratio, so output
    // matches native pixel-for-pixel at rest; motion aberrated directions,
    // Doppler-shifts fitted spectra, and applies D^2 point-source beaming.
    // Depth-test-no-write additive blend matches the native contract.
    internal sealed class RelativisticStarPass : IDisposable
    {
        // Catalog republication mid-run is not expected; a fixed capacity avoids
        // descriptor rebinding on in-flight resources. Larger catalogs degrade
        // to native rendering rather than rebinding a live descriptor.
        private const int MaxStars = 262144;

        private readonly Core.Renderer _renderer;
        private readonly RenderTarget _target;
        private readonly VkFormat _colorFormat;
        private readonly VkFormat _depthFormat;
        private readonly VkSampleCountFlags _samples;
        private readonly ThermalSpectrumTable _table;
        private DescriptorSetLayoutEx? _setLayout;
        private DescriptorPoolEx? _pool;
        private VkDescriptorSet _set;
        private BufferEx _thermalBuffer;
        private BufferEx _catalogBuffer;
        private MappedMemory _catalogMemory;
        private bool _catalogMapped;
        private VkShaderModule _vertexModule;
        private VkShaderModule _fragmentModule;
        private VkPipelineLayout _pipelineLayout;
        private VkPipeline _pipeline;
        private int _builtRevision = -1;
        private int _catalogCount;
        private int _fittedCount;
        private bool _disposed;

        internal RelativisticStarPass(Core.Renderer renderer, RenderTarget target,
            ThermalSpectrumTable table)
        {
            ArgumentNullException.ThrowIfNull(renderer);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(table);
            if (target.ColorImage is not { } colorImage || target.DepthImage is not { } depthImage)
            {
                throw new InvalidOperationException("star pass requires a borrowed target with color and depth images");
            }

            _renderer = renderer;
            _target = target;
            _table = table;
            _colorFormat = colorImage.Format;
            _depthFormat = depthImage.Format;
            _samples = target.Samples;
            try
            {
                _thermalBuffer = BufferExExtensions.CreateBuffer(renderer.Allocator, new BufferEx.CreateInfo
                {
                    Name = "KSARS_RelativisticStars_Thermal",
                    BufferUsage = VkBufferUsageFlags.StorageBufferBit,
                    BufferSize = ByteSize.Of<float4>() * ThermalSpectrumTable.GpuRows,
                    AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                });
                MappedMemory thermalMemory = _thermalBuffer.Map();
                try
                {
                    _table.WriteGpuTable(thermalMemory.AsSpan<float4>());
                }
                finally
                {
                    thermalMemory.Unmap();
                }

                _catalogBuffer = BufferExExtensions.CreateBuffer(renderer.Allocator, new BufferEx.CreateInfo
                {
                    Name = "KSARS_RelativisticStars_Catalog",
                    BufferUsage = VkBufferUsageFlags.StorageBufferBit,
                    BufferSize = ByteSize.Of<StarGpuBuilder.StarGpu>() * MaxStars,
                    AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                });
                _catalogMemory = _catalogBuffer.Map();
                _catalogMapped = true;

                _vertexModule = CompileModule(RelativisticStarShaders.Vertex, ShaderKind.GlslVertexShader, "RelativisticStars.vert");
                _fragmentModule = CompileModule(RelativisticStarShaders.Fragment, ShaderKind.GlslFragmentShader, "RelativisticStars.frag");

                var bindings = new VkDescriptorSetLayoutBinding[2]
                {
                    new()
                    {
                        Binding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.StorageBuffer,
                        StageFlags = VkShaderStageFlags.VertexBit,
                    },
                    new()
                    {
                        Binding = 1,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.StorageBuffer,
                        StageFlags = VkShaderStageFlags.VertexBit,
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
                            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 2 },
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
                var catalogInfo = new VkDescriptorBufferInfo
                {
                    Buffer = _catalogBuffer.VkBuffer,
                    Offset = ByteSize.Zero,
                    Range = ByteSize.Of<StarGpuBuilder.StarGpu>() * MaxStars,
                };
                WriteBufferDescriptor(_set, 1, in catalogInfo);

                var pushRange = new VkPushConstantRange
                {
                    StageFlags = VkShaderStageFlags.VertexBit,
                    Offset = ByteSize.Zero,
                    Size = ByteSize.Of<float4>(),
                };
                ReadOnlySpan<VkDescriptorSetLayout> setLayouts = stackalloc VkDescriptorSetLayout[2]
                {
                    (VkDescriptorSetLayout)GlobalShaderBindings.DescriptorSetLayout,
                    (VkDescriptorSetLayout)_setLayout,
                };
                ReadOnlySpan<VkPushConstantRange> pushRanges = stackalloc VkPushConstantRange[1] { pushRange };
                _pipelineLayout = DeviceExtensions.CreatePipelineLayout(renderer.Device, setLayouts, pushRanges, null);

                BuildPipeline();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal int CatalogCount => _catalogCount;
        internal int FittedCount => _fittedCount;
        internal bool CatalogOverflowed { get; private set; }

        // Rebuilds the owned catalog buffer contents when the published catalog
        // revision changes. Returns false when no records are available for the
        // bound technique or the catalog exceeds the fixed capacity.
        internal bool SyncCatalog(object technique, StarSpectralCache cache)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RelativisticStarPass));
            }

            ArgumentNullException.ThrowIfNull(technique);
            ArgumentNullException.ThrowIfNull(cache);
            int revision = StarCatalog.Revision;
            if (_builtRevision == revision && _catalogCount > 0)
            {
                return true;
            }

            StarCatalog.Entry[]? entries = StarCatalog.Snapshot(technique);
            if (entries is null || entries.Length == 0)
            {
                return false;
            }

            if (entries.Length > MaxStars)
            {
                CatalogOverflowed = true;
                return false;
            }

            StarGpuBuilder.StarGpu[] records = StarGpuBuilder.Build(entries, cache, out int fitted);
            records.AsSpan().CopyTo(_catalogMemory.AsSpan<StarGpuBuilder.StarGpu>());
            _catalogCount = records.Length;
            _fittedCount = fitted;
            // A concurrent publication mid-build leaves a consistent older
            // snapshot in the buffer, but it must not be pinned as current:
            // force a rebuild on the next call instead of locking in staleness.
            _builtRevision = StarCatalog.Revision == revision ? revision : -1;
            return true;
        }

        internal void ValidateState()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RelativisticStarPass));
            }

            if (!ReferenceEquals(_target, Program.OffscreenTarget)
                || _target.ColorImage is not { } colorImage || colorImage.Format != _colorFormat
                || _target.DepthImage is not { } depthImage || depthImage.Format != _depthFormat
                || _target.Samples != _samples)
            {
                throw new InvalidOperationException("star pass borrowed target signature changed; restart required");
            }
        }

        internal void Record(CommandBuffer commandBuffer, IViewport viewport,
            in PresentationVelocity observer)
        {
            ValidateState();
            VkDeviceExtensions.BindPipeline(commandBuffer, VkPipelineBindPoint.Graphics, _pipeline);
            Program.SetViewport(commandBuffer);

            ReadOnlySpan<VkDescriptorSet> sets = stackalloc VkDescriptorSet[2]
            {
                GlobalShaderBindings.DescriptorSet(viewport.ShaderSlot),
                _set,
            };
            VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics,
                _pipelineLayout, 0, sets, default(Span<ByteSize32>));

            var push = new float4((float)observer.Direction.X, (float)observer.Direction.Y,
                (float)observer.Direction.Z, (float)observer.Q);
            CommandBufferExtensions.PushConstants(commandBuffer, _pipelineLayout,
                VkShaderStageFlags.VertexBit, ByteSize.Zero, push);

            commandBuffer.Draw(6, _catalogCount, 0, 0);
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
                if (_catalogMapped)
                {
                    _catalogMemory.Unmap();
                    _catalogMapped = false;
                }
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }

            try
            {
                if (!_catalogBuffer.IsNull())
                {
                    _catalogBuffer.Dispose();
                }
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
                    "relativistic star pass teardown had per-resource failures", errors);
            }
        }
    }
}

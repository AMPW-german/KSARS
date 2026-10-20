using System.Runtime.InteropServices;
using Brutal;
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
    internal sealed class AtlasMainPass : IDisposable
    {
        internal const int MaxBodies = 2;
        internal const int MaxSources = 3;

        private const string FragmentSource = """
            #version 450

            struct Camera
            {
                mat4 viewProjection;
                mat4 view;
                mat4 projection;
                mat4 inverseView;
                mat4 inverseProjection;
                vec4 cameraPosition;
                float nearPlane;
                float farPlane;
                float aspectRatio;
                int screenWidth;
                int screenHeight;
                float pbrExposure;
                float time;
                float simSpeed;
                int frame;
                int sdfResolution;
                float deltaTime;
                int pad0;
                mat4 galacticPlane;
            };
            layout(set = 0, binding = 0) uniform GlobalUbo { Camera camera; } globalData;

            struct AtlasBody
            {
                vec4 rightTan;
                vec4 upNear;
                vec4 fwdFar;
                vec4 sourceInfo;
            };
            layout(std140, set = 1, binding = 0) uniform AtlasBodies { AtlasBody bodies[3]; } atlas;
            layout(set = 1, binding = 1) uniform sampler2D colorTiles[3];
            layout(set = 1, binding = 2) uniform sampler2D depthTiles[3];
            layout(std430, set = 1, binding = 3) readonly buffer KsarsThermal
            {
                vec4 samples[1024];
                vec4 hot[3];
            } ksarsThermal;

            layout(push_constant) uniform MainParams
            {
                vec4 observerUq;
                ivec4 counts;
            } params;

            layout(location = 0) in vec2 inUv;
            layout(location = 0) out vec4 outColor;

            // Sunlit bodies and the solar disc share the ~5800 K solar-spectrum
            // prior; D^3 keeps the intensity term while this applies the
            // hue-only part of the Doppler shift to the composited radiance.
            const float KSARS_BODY_TEFF = 5800.0;

            vec4 ksarsSpectrum(float temperature)
            {
                if (temperature > 1e8)
                {
                    float r = 1e8 / temperature;
                    vec3 xyz = vec3(
                        ksarsThermal.hot[0].x + r * (ksarsThermal.hot[0].y + r * ksarsThermal.hot[0].z),
                        ksarsThermal.hot[1].x + r * (ksarsThermal.hot[1].y + r * ksarsThermal.hot[1].z),
                        ksarsThermal.hot[2].x + r * (ksarsThermal.hot[2].y + r * ksarsThermal.hot[2].z));
                    return vec4(xyz / (xyz.x + xyz.y + xyz.z), log(xyz.y) + log(temperature) - 18.420680744);
                }

                float p = clamp(log(temperature) * (1023.0 / 18.420680744), 0.0, 1023.0);
                int i = min(int(floor(p)), 1022);
                return mix(ksarsThermal.samples[i], ksarsThermal.samples[i + 1],
                    clamp(p - float(i), 0.0, 1.0));
            }

            vec3 ksarsBodyShift(vec3 rgb, float doppler)
            {
                if (doppler == 1.0)
                {
                    return rgb;
                }

                vec4 restS = ksarsSpectrum(KSARS_BODY_TEFF);
                vec4 shiftS = ksarsSpectrum(KSARS_BODY_TEFF * doppler);
                if (!(restS.y > 0.0) || !(shiftS.y > 0.0))
                {
                    return rgb;
                }

                vec3 m = shiftS.xyz / restS.xyz;
                float ml = dot(m, vec3(0.212639006, 0.715168679, 0.072192315));
                if (!(ml > 0.0))
                {
                    return rgb;
                }

                m /= ml;
                vec3 xyz = vec3(
                    dot(rgb, vec3(0.412390799, 0.357584339, 0.180480788)),
                    dot(rgb, vec3(0.212639006, 0.715168679, 0.072192315)),
                    dot(rgb, vec3(0.019330819, 0.119194780, 0.950532152)));
                xyz *= m;
                return max(vec3(
                    dot(vec3(3.240969942, -1.537383178, -0.498610760), xyz),
                    dot(vec3(-0.969243636, 1.875967502, 0.041555057), xyz),
                    dot(vec3(0.055630080, -0.203976959, 1.056971514), xyz)), vec3(0.0));
            }

            void main()
            {
                vec4 ndc = vec4(inUv * 2.0 - 1.0, 1.0, 1.0);
                vec4 viewPos = globalData.camera.inverseProjection * ndc;
                vec3 viewDir = normalize(viewPos.xyz);
                vec3 nObs = normalize(mat3(globalData.camera.inverseView) * viewDir);

                vec3 u = params.observerUq.xyz;
                float q = params.observerUq.w;
                float qq = q * q;
                vec3 nRest = nObs;
                float doppler = 1.0;
                if (q != 1.0)
                {
                    float A = 0.5 * dot(nObs - u, nObs - u);
                    float B = 0.5 * dot(nObs + u, nObs + u);
                    vec3 nPerp = nObs - dot(nObs, u) * u;
                    nRest = normalize(u * (qq * B - A) + nPerp * (2.0 * q));
                    doppler = (2.0 * q) / (A + qq * B);
                }

                float bestRadial = 3.0e38;
                vec3 bestRgb = vec3(0.0);
                float bestAlpha = 0.0;
                float coronaRadial = 3.0e38;
                vec3 coronaRgb = vec3(0.0);
                float coronaAlpha = 0.0;
                for (int i = 0; i < params.counts.x; i++)
                {
                    float z = dot(nRest, atlas.bodies[i].fwdFar.xyz);
                    if (!(z > 0.0))
                    {
                        continue;
                    }

                    float inv = 1.0 / (z * atlas.bodies[i].rightTan.w);
                    vec2 uv = vec2(0.5 * (1.0 + dot(nRest, atlas.bodies[i].rightTan.xyz) * inv),
                                   0.5 * (1.0 - dot(nRest, atlas.bodies[i].upNear.xyz) * inv));
                    if (any(isnan(uv)) || any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
                    {
                        continue;
                    }

                    ivec2 size = textureSize(depthTiles[i], 0);
                    ivec2 coord = clamp(ivec2(uv * vec2(size)), ivec2(0), size - 1);
                    float depth = texelFetch(depthTiles[i], coord, 0).r;
                    vec4 c = texture(colorTiles[i], uv);
                    if (any(isnan(c)) || any(isinf(c)))
                    {
                        continue;
                    }

                    bool isSolar = atlas.bodies[i].sourceInfo.x > 0.5;
                    if (depth > 0.0 && depth <= 1.0)
                    {
                        float ratio = atlas.bodies[i].upNear.w / atlas.bodies[i].fwdFar.w;
                        float a = ratio / (1.0 - ratio);
                        float b = atlas.bodies[i].upNear.w / (1.0 - ratio);
                        float sourceZ = b / (depth + a);
                        vec2 rayXY = (2.0 * uv - 1.0) * atlas.bodies[i].rightTan.w;
                        float radial = sourceZ * sqrt(1.0 + dot(rayXY, rayXY));
                        if (!(radial > 0.0))
                        {
                            continue;
                        }

                        if (radial < bestRadial)
                        {
                            bestRadial = radial;
                            bestRgb = c.rgb;
                            bestAlpha = 1.0;
                        }

                        continue;
                    }

                    if (isSolar && depth == 0.0 && atlas.bodies[i].sourceInfo.z > 0.5)
                    {
                        float coverage = clamp(c.a, 0.0, 1.0);
                        vec2 coronaXY = (2.0 * uv - 1.0) * atlas.bodies[i].rightTan.w;
                        float radial = atlas.bodies[i].sourceInfo.y * sqrt(1.0 + dot(coronaXY, coronaXY));
                        if (coverage > 0.0 && radial > 0.0 && !isnan(radial) && !isinf(radial)
                            && radial < coronaRadial)
                        {
                            coronaRadial = radial;
                            coronaRgb = c.rgb;
                            coronaAlpha = coverage;
                        }
                    }
                }

                vec3 rgb;
                float alpha;
                if (coronaAlpha > 0.0 && coronaRadial < bestRadial)
                {
                    rgb = coronaRgb + (1.0 - coronaAlpha) * bestRgb;
                    alpha = coronaAlpha + (1.0 - coronaAlpha) * bestAlpha;
                }
                else
                {
                    rgb = bestRgb;
                    alpha = bestAlpha;
                }

                if (!(alpha > 0.0))
                {
                    discard;
                }

                rgb = ksarsBodyShift(rgb, doppler);

                float scale = doppler * doppler * doppler;
                rgb *= scale;
                if (any(isnan(rgb)))
                {
                    discard;
                }

                rgb = min(max(rgb, vec3(0.0)), vec3(65504.0));
                outColor = vec4(rgb, alpha);
            }
            """;

        private readonly Core.Renderer _renderer;
        private readonly RenderTarget _target;
        private readonly VkFormat _colorFormat;
        private readonly VkFormat _depthFormat;
        private readonly VkSampleCountFlags _samples;
        private DescriptorSetLayoutEx? _setLayout;
        private DescriptorPoolEx? _pool;
        private VkDescriptorSet _set;
        private BufferEx _bodyBuffer;
        private BufferEx _thermalBuffer;
        private VkShaderModule _vertexModule;
        private VkShaderModule _fragmentModule;
        private VkPipelineLayout _pipelineLayout;
        private VkPipeline _pipeline;
        private PtrOwner<VkPipelineColorBlendAttachmentState>? _premulAttachment;
        private PtrOwner<VkPipelineColorBlendStateCreateInfo>? _premulBlend;
        private int _boundSourceCount = -1;
        private bool _disposed;

        internal AtlasMainPass(Core.Renderer renderer, RenderTarget target)
        {
            ArgumentNullException.ThrowIfNull(renderer);
            ArgumentNullException.ThrowIfNull(target);
            if (target.ColorImage is not { } colorImage || target.DepthImage is not { } depthImage)
            {
                throw new InvalidOperationException("main pass requires a borrowed target with color and depth images");
            }

            _renderer = renderer;
            _target = target;
            _colorFormat = colorImage.Format;
            _depthFormat = depthImage.Format;
            _samples = target.Samples;
            try
            {
                _bodyBuffer = BufferExExtensions.CreateBuffer(renderer.Allocator, new BufferEx.CreateInfo
                {
                    Name = "KSARS_AtlasMain_Bodies",
                    BufferUsage = VkBufferUsageFlags.UniformBufferBit,
                    BufferSize = ByteSize.Of<AtlasBodyGpu>() * MaxSources,
                    AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                });

                _thermalBuffer = BufferExExtensions.CreateBuffer(renderer.Allocator, new BufferEx.CreateInfo
                {
                    Name = "KSARS_AtlasMain_Thermal",
                    BufferUsage = VkBufferUsageFlags.StorageBufferBit,
                    BufferSize = ByteSize.Of<float4>() * ThermalSpectrumTable.GpuRows,
                    AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                });
                MappedMemory thermalMemory = _thermalBuffer.Map();
                try
                {
                    ThermalSpectrumTable.LoadEmbedded().WriteGpuTable(thermalMemory.AsSpan<float4>());
                }
                finally
                {
                    thermalMemory.Unmap();
                }

                _vertexModule = CompileModule(AtlasCompositeProbe.VertexSource, ShaderKind.GlslVertexShader, "AtlasMain.vert");
                _fragmentModule = CompileModule(FragmentSource, ShaderKind.GlslFragmentShader, "AtlasMain.frag");

                var bindings = new VkDescriptorSetLayoutBinding[4]
                {
                    new()
                    {
                        Binding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.UniformBuffer,
                        StageFlags = VkShaderStageFlags.FragmentBit,
                    },
                    new()
                    {
                        Binding = 1,
                        DescriptorCount = MaxSources,
                        DescriptorType = VkDescriptorType.CombinedImageSampler,
                        StageFlags = VkShaderStageFlags.FragmentBit,
                    },
                    new()
                    {
                        Binding = 2,
                        DescriptorCount = MaxSources,
                        DescriptorType = VkDescriptorType.CombinedImageSampler,
                        StageFlags = VkShaderStageFlags.FragmentBit,
                    },
                    new()
                    {
                        Binding = 3,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.StorageBuffer,
                        StageFlags = VkShaderStageFlags.FragmentBit,
                    },
                };
                var layoutInfo = new DescriptorSetLayoutEx.CreateInfo { Bindings = bindings };
                _setLayout = _renderer.Device.CreateDescriptorSetLayout(layoutInfo, null);

                _pool = DescriptorPoolExExtensions.CreateDescriptorPool(_renderer.Device,
                    new DescriptorPoolEx.CreateInfo
                    {
                        MaxSets = 1,
                        PoolSizes = new VkDescriptorPoolSize[]
                        {
                            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 1 },
                            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 2 * MaxSources },
                            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 1 },
                        },
                    },
                    null);
                _set = DescriptorPoolExExtensions.AllocateDescriptorSet(_renderer.Device, _pool, _setLayout);

                var bodyInfo = new VkDescriptorBufferInfo
                {
                    Buffer = _bodyBuffer.VkBuffer,
                    Offset = ByteSize.Zero,
                    Range = ByteSize.Of<AtlasBodyGpu>() * MaxSources,
                };
                WriteBufferDescriptor(_set, 0, VkDescriptorType.UniformBuffer, in bodyInfo);

                var thermalInfo = new VkDescriptorBufferInfo
                {
                    Buffer = _thermalBuffer.VkBuffer,
                    Offset = ByteSize.Zero,
                    Range = ByteSize.Of<float4>() * ThermalSpectrumTable.GpuRows,
                };
                WriteBufferDescriptor(_set, 3, VkDescriptorType.StorageBuffer, in thermalInfo);

                var pushRange = new VkPushConstantRange
                {
                    StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
                    Offset = ByteSize.Zero,
                    Size = ByteSize.Of<MainPushGpu>(),
                };
                ReadOnlySpan<VkDescriptorSetLayout> setLayouts = stackalloc VkDescriptorSetLayout[2]
                {
                    (VkDescriptorSetLayout)GlobalShaderBindings.DescriptorSetLayout,
                    (VkDescriptorSetLayout)_setLayout,
                };
                ReadOnlySpan<VkPushConstantRange> pushRanges = stackalloc VkPushConstantRange[1] { pushRange };
                _pipelineLayout = DeviceExtensions.CreatePipelineLayout(renderer.Device, setLayouts, pushRanges, null);

                CreatePremultipliedBlendState();
                BuildPipeline();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void SyncSources(IReadOnlyList<MainTileSource> sources)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AtlasMainPass));
            }

            ArgumentNullException.ThrowIfNull(sources);
            if (sources.Count < 1 || sources.Count > MaxSources)
            {
                throw new ArgumentOutOfRangeException(nameof(sources));
            }

            int solarCount = sources.Count(s => s.IsSolar);
            if (solarCount > 1 || sources.Count - solarCount > MaxBodies)
            {
                throw new ArgumentOutOfRangeException(nameof(sources),
                    "main pass supports at most two body sources plus one solar source");
            }

            Span<AtlasBodyGpu> bodies = stackalloc AtlasBodyGpu[MaxSources];
            var colorInfos = new VkDescriptorImageInfo[MaxSources];
            var depthInfos = new VkDescriptorImageInfo[MaxSources];
            for (int i = 0; i < MaxSources; i++)
            {
                MainTileSource source = sources[Math.Min(i, sources.Count - 1)];
                if (source.IsSolar && !RenderCompatibility.IsSolarDistanceSupported(source.Distance))
                {
                    throw new ArgumentOutOfRangeException(nameof(sources),
                        "solar source distance must be finite, positive and float-representable");
                }

                bodies[i] = new AtlasBodyGpu
                {
                    RightTan = new float4(
                        (float)source.Projection.Right.X, (float)source.Projection.Right.Y, (float)source.Projection.Right.Z,
                        (float)source.Projection.TanHalfFov),
                    UpNear = new float4(
                        (float)source.Projection.Up.X, (float)source.Projection.Up.Y, (float)source.Projection.Up.Z,
                        (float)source.Projection.Near),
                    FwdFar = new float4(
                        (float)source.Projection.Forward.X, (float)source.Projection.Forward.Y, (float)source.Projection.Forward.Z,
                        (float)source.Projection.Far),
                    SourceInfo = new float4(
                        source.IsSolar ? 1.0f : 0.0f, (float)source.Distance,
                        source.CoronaEnabled ? 1.0f : 0.0f, 0.0f),
                };
                colorInfos[i] = new VkDescriptorImageInfo
                {
                    Sampler = Program.LinearClampedSampler,
                    ImageView = source.ColorImage.ImageView,
                    ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
                };
                depthInfos[i] = new VkDescriptorImageInfo
                {
                    Sampler = Program.PointClampedSampler,
                    ImageView = source.DepthImage.ImageView,
                    ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal,
                };
            }

            if (!IsFinite(bodies))
            {
                throw new InvalidOperationException("main pass body parameters nonfinite");
            }

            MappedMemory memory = _bodyBuffer.Map();
            try
            {
                for (int i = 0; i < MaxSources; i++)
                {
                    memory.Offset(ByteSize.Of<AtlasBodyGpu>() * i).As<AtlasBodyGpu>() = bodies[i];
                }
            }
            finally
            {
                memory.Unmap();
            }

            WriteImageDescriptorArray(_set, 1, colorInfos);
            WriteImageDescriptorArray(_set, 2, depthInfos);
            _boundSourceCount = sources.Count;
        }

        internal bool TryGetAtlasBinding(out VkDescriptorSet set, out DescriptorSetLayoutEx? layout)
        {
            set = _set;
            layout = _setLayout;
            return !_disposed && _setLayout is not null && !_set.Equals(default) && _boundSourceCount >= 1;
        }

        // Set layout alone; available before any source binding so dependent pipelines can
        // build their layouts at construction. Callers must still pass TryGetAtlasBinding
        // before binding the set for a draw/dispatch.
        internal DescriptorSetLayoutEx? AtlasSetLayout => _disposed ? null : _setLayout;

        internal void ValidateState()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AtlasMainPass));
            }

            if (!ReferenceEquals(_target, Program.OffscreenTarget)
                || _target.ColorImage is not { } colorImage || colorImage.Format != _colorFormat
                || _target.DepthImage is not { } depthImage || depthImage.Format != _depthFormat
                || _target.Samples != _samples)
            {
                throw new RendererTargetChangedException("main pass borrowed target signature changed; restart required");
            }
        }

        internal sealed class RendererTargetChangedException : InvalidOperationException
        {
            internal RendererTargetChangedException(string message) : base(message) { }
        }

        internal void Record(CommandBuffer commandBuffer, ObserverSnapshot snapshot, in PresentationVelocity observer)
        {
            ValidateState();
            VkDeviceExtensions.BindPipeline(commandBuffer, VkPipelineBindPoint.Graphics, _pipeline);
            Program.SetViewport(commandBuffer);

            ReadOnlySpan<VkDescriptorSet> sets = stackalloc VkDescriptorSet[2]
            {
                GlobalShaderBindings.DescriptorSet(snapshot.MainViewport.ShaderSlot),
                _set,
            };
            VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics,
                _pipelineLayout, 0, sets, default(Span<ByteSize32>));

            var push = new MainPushGpu
            {
                ObserverUq = new float4(
                    (float)observer.Direction.X,
                    (float)observer.Direction.Y,
                    (float)observer.Direction.Z,
                    (float)observer.Q),
                Counts = new int4(_boundSourceCount < 0 ? 0 : _boundSourceCount, 0, 0, 0),
            };
            CommandBufferExtensions.PushConstants(commandBuffer, _pipelineLayout,
                VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, ByteSize.Zero, push);

            commandBuffer.Draw(3, 1, 0, 0);
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

        private unsafe void CreatePremultipliedBlendState()
        {
            _premulAttachment = new PtrOwner<VkPipelineColorBlendAttachmentState>(
                new VkPipelineColorBlendAttachmentState
                {
                    BlendEnable = true,
                    SrcColorBlendFactor = VkBlendFactor.One,
                    DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp = VkBlendOp.Add,
                    SrcAlphaBlendFactor = VkBlendFactor.One,
                    DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                    AlphaBlendOp = VkBlendOp.Add,
                    ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit
                        | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
                });
            var blendInfo = new VkPipelineColorBlendStateCreateInfo
            {
                AttachmentCount = 1,
                Attachments = _premulAttachment,
            };
            _premulBlend = new PtrOwner<VkPipelineColorBlendStateCreateInfo>(blendInfo);
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
                ColorBlendState = _premulBlend,
            };
            _target.SetupGraphicsPipeline(ref info);
            _pipeline = DeviceExtensions.CreateGraphicsPipeline(_renderer.Device, Core.PipelineCaches.Shared, info, null);
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

        private unsafe void WriteImageDescriptorArray(VkDescriptorSet set, int binding,
            VkDescriptorImageInfo[] infos)
        {
            fixed (VkDescriptorImageInfo* ptr = infos)
            {
                var write = new VkWriteDescriptorSet
                {
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    DescriptorCount = infos.Length,
                    DstBinding = binding,
                    DstSet = set,
                    ImageInfo = ptr,
                };
                _renderer.Device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(in write), default);
            }
        }

        private static bool IsFinite(Span<AtlasBodyGpu> bodies)
        {
            foreach (AtlasBodyGpu b in bodies)
            {
                if (!float.IsFinite(b.RightTan.X) || !float.IsFinite(b.RightTan.Y) || !float.IsFinite(b.RightTan.Z) || !float.IsFinite(b.RightTan.W)
                    || !float.IsFinite(b.UpNear.X) || !float.IsFinite(b.UpNear.Y) || !float.IsFinite(b.UpNear.Z) || !float.IsFinite(b.UpNear.W)
                    || !float.IsFinite(b.FwdFar.X) || !float.IsFinite(b.FwdFar.Y) || !float.IsFinite(b.FwdFar.Z) || !float.IsFinite(b.FwdFar.W)
                    || !float.IsFinite(b.SourceInfo.X) || !float.IsFinite(b.SourceInfo.Y)
                    || !float.IsFinite(b.SourceInfo.Z) || !float.IsFinite(b.SourceInfo.W))
                {
                    return false;
                }
            }

            return true;
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
                if (!_bodyBuffer.IsNull())
                {
                    _bodyBuffer.Dispose();
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

            try
            {
                _premulBlend?.Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }

            try
            {
                _premulAttachment?.Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }

            if (errors is not null)
            {
                throw new AggregateException(
                    "main atlas pass teardown had per-resource failures", errors);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MainPushGpu
        {
            internal float4 ObserverUq;
            internal int4 Counts;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AtlasBodyGpu
        {
            internal float4 RightTan;
            internal float4 UpNear;
            internal float4 FwdFar;
            internal float4 SourceInfo;
        }
    }

    internal readonly struct MainTileSource
    {
        internal required RenderImage ColorImage { get; init; }
        internal required RenderImage DepthImage { get; init; }
        internal required AtlasProjection Projection { get; init; }
        internal bool IsSolar { get; init; }
        internal double Distance { get; init; }
        internal bool CoronaEnabled { get; init; }
    }
}

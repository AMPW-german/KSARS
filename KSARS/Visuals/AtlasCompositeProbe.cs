using System.Runtime.InteropServices;
using Brutal;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.Pointers.Extensions;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using KSA.Rendering;

namespace KSARS.Visuals
{
    // Diagnostic atlas reprojection pass: renders observed-direction composition of the frozen
    // source tiles into an owned HDR target. Applies inverse aberration and a D^3 radiance scale
    // per pixel. This is not the production atlas; it does not touch the main render pass.
    internal sealed class AtlasCompositeProbe : IDisposable
    {
        internal const int MaxBodies = 4;
        internal const int CompositeWidth = 640;
        internal readonly int CompositeHeight;

        internal const string VertexSource = """
            #version 450
            layout(location = 0) out vec2 outUv;
            void main()
            {
                vec2 pos = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
                outUv = pos;
                gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
            }
            """;

        private const string FragmentSource = """
            #version 450

            // Must match the leading Camera member of the native Global UBO (Common/Global.glsl).
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
                vec4 rightTan;   // xyz = tile right axis, w = tan(halfFov)
                vec4 upCos;      // xyz = tile up axis,    w = cos(halfFov)
                vec4 fwdDist;    // xyz = tile forward (rest center dir), w = rest distance
                vec4 misc;       // x = enclosing radius, y = near, z = far
            };
            layout(std140, set = 1, binding = 0) uniform AtlasBodies { AtlasBody bodies[4]; } atlas;
            layout(set = 1, binding = 1) uniform sampler2D tiles[4];
            layout(set = 1, binding = 2) uniform sampler2D depthTiles[4];

            layout(push_constant) uniform CompositeParams
            {
                vec4 observerUq; // xyz = observer velocity direction, w = stable q
                ivec4 counts;    // x = bodyCount, y = debugMode, z = debug body index
            } params;

            layout(location = 0) in vec2 inUv;
            layout(location = 0) out vec4 outColor;

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
                    // Half squared chord lengths: A = 1 - mu, B = 1 + mu. Stable at all mu.
                    float A = 0.5 * dot(nObs - u, nObs - u);
                    float B = 0.5 * dot(nObs + u, nObs + u);

                    // Inverse aberration (stable q formulation; normalization drops the shared denominator).
                    vec3 nPerp = nObs - dot(nObs, u) * u;
                    nRest = normalize(u * (qq * B - A) + nPerp * (2.0 * q));

                    // Doppler factor of the observed direction: D = 2q / (A + q^2 B).
                    doppler = (2.0 * q) / (A + qq * B);
                }

                if (params.counts.y == 1)
                {
                    // Doppler map: blue = blueshift (D > 1), red = redshift (D < 1).
                    float l = log2(max(doppler, 1e-30)) * 0.125;
                    outColor = vec4(max(0.0, -l), 0.0, max(0.0, l), 1.0);
                    return;
                }

                if (params.counts.y == 2)
                {
                    // Coverage mask for body counts.z: R = inside cap, G = uv in [0,1],
                    // B = sampled alpha > 0. White disc = fully passing texels.
                    int bi = params.counts.z;
                    float z0 = dot(nRest, atlas.bodies[bi].fwdDist.xyz);
                    vec3 m = vec3(0.0);
                    if (z0 > 0.0)
                    {
                        float tanHalf = atlas.bodies[bi].rightTan.w;
                        vec2 tangent = vec2(dot(nRest, atlas.bodies[bi].rightTan.xyz),
                                            dot(nRest, atlas.bodies[bi].upCos.xyz));
                        float perp2 = dot(tangent, tangent);
                        if (perp2 <= z0 * z0 * tanHalf * tanHalf) m.r = 1.0;
                        float inv0 = 1.0 / (z0 * tanHalf);
                        vec2 uv0 = vec2(0.5 * (1.0 + tangent.x * inv0),
                                        0.5 * (1.0 - tangent.y * inv0));
                        if (!any(isnan(uv0)) && !any(isinf(uv0))
                            && all(greaterThanEqual(uv0, vec2(0.0))) && all(lessThanEqual(uv0, vec2(1.0))))
                        {
                            m.g = 1.0;
                            m.b = clamp(texture(tiles[bi], uv0).a, 0.0, 1.0);
                        }
                    }
                    outColor = vec4(m, 1.0);
                    return;
                }

                if (params.counts.y == 3)
                {
                    outColor = vec4(texture(tiles[params.counts.z], inUv).rgb, 1.0);
                    return;
                }

                if (params.counts.y == 4)
                {
                    // Direction heat for body counts.z: brightness peaks at the body's rest direction.
                    float h = dot(nRest, atlas.bodies[params.counts.z].fwdDist.xyz);
                    outColor = vec4(vec3(max(0.0, h)), 1.0);
                    return;
                }

                if (params.counts.y == 5)
                {
                    // Velocity direction heat: brightness peaks at the observer velocity direction.
                    outColor = vec4(vec3(max(0.0, dot(nObs, u))), 1.0);
                    return;
                }

                if (params.counts.y == 6)
                {
                    int bi = params.counts.z;
                    float z0 = dot(nRest, atlas.bodies[bi].fwdDist.xyz);
                    float m = 0.0;
                    if (z0 > 0.0)
                    {
                        float inv0 = 1.0 / (z0 * atlas.bodies[bi].rightTan.w);
                        vec2 uv0 = vec2(0.5 * (1.0 + dot(nRest, atlas.bodies[bi].rightTan.xyz) * inv0),
                                        0.5 * (1.0 - dot(nRest, atlas.bodies[bi].upCos.xyz) * inv0));
                        if (!any(isnan(uv0)) && !any(isinf(uv0))
                            && all(greaterThanEqual(uv0, vec2(0.0))) && all(lessThanEqual(uv0, vec2(1.0))))
                        {
                            ivec2 size = textureSize(depthTiles[bi], 0);
                            ivec2 coord = clamp(ivec2(uv0 * vec2(size)), ivec2(0), size - 1);
                            float depth = texelFetch(depthTiles[bi], coord, 0).r;
                            if (depth > 0.0 && depth <= 1.0) m = 1.0;
                        }
                    }
                    outColor = vec4(vec3(m), 1.0);
                    return;
                }

                float scale = doppler * doppler * doppler;
                float bestRadial = 3.0e38;
                vec4 best = vec4(0.0);
                for (int i = 0; i < params.counts.x; i++)
                {
                    float z = dot(nRest, atlas.bodies[i].fwdDist.xyz);
                    if (!(z > 0.0))
                    {
                        continue;
                    }

                    float inv = 1.0 / (z * atlas.bodies[i].rightTan.w);
                    vec2 uv = vec2(0.5 * (1.0 + dot(nRest, atlas.bodies[i].rightTan.xyz) * inv),
                                   0.5 * (1.0 - dot(nRest, atlas.bodies[i].upCos.xyz) * inv));
                    if (any(isnan(uv)) || any(isinf(uv))
                        || any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
                    {
                        continue;
                    }

                    ivec2 size = textureSize(depthTiles[i], 0);
                    ivec2 coord = clamp(ivec2(uv * vec2(size)), ivec2(0), size - 1);
                    float depth = texelFetch(depthTiles[i], coord, 0).r;
                    if (!(depth > 0.0 && depth <= 1.0))
                    {
                        continue;
                    }

                    vec4 c = texture(tiles[i], uv);
                    if (any(isnan(c)) || any(isinf(c)))
                    {
                        continue;
                    }

                    float ratio = atlas.bodies[i].misc.y / atlas.bodies[i].misc.z;
                    float a = ratio / (1.0 - ratio);
                    float b = atlas.bodies[i].misc.y / (1.0 - ratio);
                    float sourceZ = b / (depth + a);
                    vec2 rayXY = (2.0 * uv - 1.0) * atlas.bodies[i].rightTan.w;
                    float radial = sourceZ * sqrt(1.0 + dot(rayXY, rayXY));
                    if (!(radial > 0.0) || isinf(radial))
                    {
                        continue;
                    }

                    if (radial < bestRadial)
                    {
                        bestRadial = radial;
                        best = c;
                    }
                }

                outColor = vec4(best.rgb * scale, best.a);
            }
            """;

        private readonly Core.Renderer _renderer;
        private RenderTarget? _target;
        private DescriptorSetLayoutEx? _setLayout;
        private DescriptorPoolEx? _pool;
        private VkDescriptorSet _set;
        private BufferEx _bodyBuffer;
        private VkShaderModule _vertexModule;
        private VkShaderModule _fragmentModule;
        private VkPipelineLayout _pipelineLayout;
        private VkPipeline _pipeline;
        private int _boundBodyCount = -1;
        private bool _disposed;

        internal ImTextureRef Texture;
        internal bool TextureRegistered;
        internal ImGuiBackendVulkanImpl? Backend;
        internal bool RecordingRecovered { get; private set; } = true;
        internal int BoundBodyCount => Math.Max(0, _boundBodyCount);

        internal AtlasCompositeProbe(Core.Renderer renderer, int height)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            CompositeHeight = Math.Clamp(height, 90, 1440);
            try
            {
                _target = new RenderTarget(renderer, "KSARS_AtlasComposite_Target",
                    new VkExtent2D(CompositeWidth, CompositeHeight),
                    VkFormat.R32G32B32A32SFloat, VkFormat.Undefined, 1, VkSampleCountFlags._1Bit, true);

                _bodyBuffer = BufferExExtensions.CreateBuffer(renderer.Allocator, new BufferEx.CreateInfo
                {
                    Name = "KSARS_AtlasComposite_Bodies",
                    BufferUsage = VkBufferUsageFlags.UniformBufferBit,
                    BufferSize = ByteSize.Of<AtlasBodyGpu>() * MaxBodies,
                    AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                });

                _vertexModule = CompileModule(VertexSource, ShaderKind.GlslVertexShader, "AtlasComposite.vert");
                _fragmentModule = CompileModule(FragmentSource, ShaderKind.GlslFragmentShader, "AtlasComposite.frag");

                var bindings = new VkDescriptorSetLayoutBinding[3]
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
                        DescriptorCount = MaxBodies,
                        DescriptorType = VkDescriptorType.CombinedImageSampler,
                        StageFlags = VkShaderStageFlags.FragmentBit,
                    },
                    new()
                    {
                        Binding = 2,
                        DescriptorCount = MaxBodies,
                        DescriptorType = VkDescriptorType.CombinedImageSampler,
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
                            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 2 * MaxBodies },
                        },
                    },
                    null);
                _set = DescriptorPoolExExtensions.AllocateDescriptorSet(_renderer.Device, _pool, _setLayout);

                var bodyInfo = new VkDescriptorBufferInfo
                {
                    Buffer = _bodyBuffer.VkBuffer,
                    Offset = ByteSize.Zero,
                    Range = ByteSize.Of<AtlasBodyGpu>() * MaxBodies,
                };
                WriteBufferDescriptor(_set, 0, VkDescriptorType.UniformBuffer, in bodyInfo);

                var pushRange = new VkPushConstantRange
                {
                    StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
                    Offset = ByteSize.Zero,
                    Size = ByteSize.Of<CompositePushGpu>(),
                };
                ReadOnlySpan<VkDescriptorSetLayout> setLayouts = stackalloc VkDescriptorSetLayout[2]
                {
                    (VkDescriptorSetLayout)GlobalShaderBindings.DescriptorSetLayout,
                    (VkDescriptorSetLayout)_setLayout,
                };
                ReadOnlySpan<VkPushConstantRange> pushRanges = stackalloc VkPushConstantRange[1] { pushRange };
                _pipelineLayout = DeviceExtensions.CreatePipelineLayout(_renderer.Device, setLayouts, pushRanges, null);

                BuildPipeline();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void SyncBodies(IReadOnlyList<AtlasTileSource> sources)
        {
            int bodyCount = Math.Min(sources.Count, MaxBodies);
            if (bodyCount == 0)
            {
                _boundBodyCount = 0;
                return;
            }

            Span<AtlasBodyGpu> bodies = stackalloc AtlasBodyGpu[MaxBodies];
            var images = new VkDescriptorImageInfo[MaxBodies];
            var depthImages = new VkDescriptorImageInfo[MaxBodies];
            for (int i = 0; i < MaxBodies; i++)
            {
                AtlasTileSource source = sources[Math.Min(i, bodyCount - 1)];
                bodies[i] = new AtlasBodyGpu
                {
                    RightTan = new float4(
                        (float)source.Projection.Right.X, (float)source.Projection.Right.Y, (float)source.Projection.Right.Z,
                        (float)source.Projection.TanHalfFov),
                    UpCos = new float4(
                        (float)source.Projection.Up.X, (float)source.Projection.Up.Y, (float)source.Projection.Up.Z,
                        (float)Math.Cos(Math.Atan(source.Projection.TanHalfFov))),
                    FwdDist = new float4(
                        (float)source.Projection.Forward.X, (float)source.Projection.Forward.Y, (float)source.Projection.Forward.Z,
                        (float)source.Projection.Distance),
                    Misc = new float4(
                        (float)(Math.Sin(source.Projection.RestAngularRadius) * source.Projection.Distance),
                        (float)source.Projection.Near,
                        (float)source.Projection.Far,
                        0.0f),
                };
                images[i] = new VkDescriptorImageInfo
                {
                    Sampler = Program.LinearClampedSampler,
                    ImageView = source.Image.ImageView,
                    ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
                };
                depthImages[i] = new VkDescriptorImageInfo
                {
                    Sampler = Program.PointClampedSampler,
                    ImageView = source.DepthImage.ImageView,
                    ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal,
                };
            }

            if (!IsFinite(bodies))
            {
                throw new InvalidOperationException("composite body parameters nonfinite");
            }

            MappedMemory memory = _bodyBuffer.Map();
            try
            {
                for (int i = 0; i < MaxBodies; i++)
                {
                    memory.Offset(ByteSize.Of<AtlasBodyGpu>() * i).As<AtlasBodyGpu>() = bodies[i];
                }
            }
            finally
            {
                memory.Unmap();
            }

            WriteImageDescriptorArray(_set, 1, images);
            WriteImageDescriptorArray(_set, 2, depthImages);
            _boundBodyCount = bodyCount;
        }

        internal void Record(CommandBuffer commandBuffer, ObserverSnapshot snapshot,
            in PresentationVelocity observer, int debugMode, int debugBody)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AtlasCompositeProbe));
            }

            RecordingRecovered = false;
            bool began = false;
            bool ended = false;
            try
            {
                try
                {
                    _target!.BeginRendering(commandBuffer, new VkClearValue(), new VkClearValue(),
                        VkAttachmentLoadOp.Clear, VkAttachmentLoadOp.Clear);
                    began = true;
                    VkDeviceExtensions.BindPipeline(commandBuffer, VkPipelineBindPoint.Graphics, _pipeline);

                    var viewport = new VkViewport { Width = CompositeWidth, Height = CompositeHeight,
                        MinDepth = 0.0f, MaxDepth = 1.0f };
                    ReadOnlySpan<VkViewport> viewports = stackalloc VkViewport[1] { viewport };
                    VkDeviceExtensions.SetViewport(commandBuffer, 0, viewports);
                    var scissor = new VkRect2D(_target.Extent);
                    ReadOnlySpan<VkRect2D> scissors = stackalloc VkRect2D[1] { scissor };
                    VkDeviceExtensions.SetScissor(commandBuffer, 0, scissors);

                    ReadOnlySpan<VkDescriptorSet> sets = stackalloc VkDescriptorSet[2]
                    {
                        GlobalShaderBindings.DescriptorSet(snapshot.MainViewport.ShaderSlot),
                        _set,
                    };
                    VkDeviceExtensions.BindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics,
                        _pipelineLayout, 0, sets, default(Span<ByteSize32>));

                    var push = new CompositePushGpu
                    {
                        ObserverUq = new float4(
                            (float)observer.Direction.X,
                            (float)observer.Direction.Y,
                            (float)observer.Direction.Z,
                            (float)observer.Q),
                        Counts = new int4(_boundBodyCount < 0 ? 0 : _boundBodyCount,
                            debugMode, Math.Clamp(debugBody, 0, MaxBodies - 1), 0),
                    };
                    CommandBufferExtensions.PushConstants(commandBuffer, _pipelineLayout,
                        VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, ByteSize.Zero, push);

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
                if (ended)
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
            _pipeline = DeviceExtensions.CreateGraphicsPipeline(_renderer.Device, Core.PipelineCaches.Shared, info, null);
        }

        private static unsafe void WriteBufferDescriptor(VkDescriptorSet set, int binding,
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
                Program.GetRenderer().Device.UpdateDescriptorSets(
                    new ReadOnlySpan<VkWriteDescriptorSet>(in write), default);
            }
        }

        private static unsafe void WriteImageDescriptorArray(VkDescriptorSet set, int binding,
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
                Program.GetRenderer().Device.UpdateDescriptorSets(
                    new ReadOnlySpan<VkWriteDescriptorSet>(in write), default);
            }
        }

        private static bool IsFinite(Span<AtlasBodyGpu> bodies)
        {
            foreach (AtlasBodyGpu b in bodies)
            {
                if (!float.IsFinite(b.RightTan.X) || !float.IsFinite(b.RightTan.Y) || !float.IsFinite(b.RightTan.Z) || !float.IsFinite(b.RightTan.W)
                    || !float.IsFinite(b.UpCos.X) || !float.IsFinite(b.UpCos.Y) || !float.IsFinite(b.UpCos.Z) || !float.IsFinite(b.UpCos.W)
                    || !float.IsFinite(b.FwdDist.X) || !float.IsFinite(b.FwdDist.Y) || !float.IsFinite(b.FwdDist.Z) || !float.IsFinite(b.FwdDist.W)
                    || !float.IsFinite(b.Misc.X) || !float.IsFinite(b.Misc.Y) || !float.IsFinite(b.Misc.Z))
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
            if (TextureRegistered && Backend is not null)
            {
                Backend.RemoveTexture(Texture);
                TextureRegistered = false;
            }

            if (!_pipeline.IsNull())
            {
                _renderer.Device.DestroyPipeline(_pipeline, null);
            }

            if (!_pipelineLayout.IsNull())
            {
                _renderer.Device.DestroyPipelineLayout(_pipelineLayout, null);
            }

            _pool?.Dispose();
            _setLayout?.Dispose();
            if (!_bodyBuffer.IsNull())
            {
                _bodyBuffer.Dispose();
            }

            if (!_vertexModule.IsNull())
            {
                _renderer.Device.DestroyShaderModule(_vertexModule, null);
            }

            if (!_fragmentModule.IsNull())
            {
                _renderer.Device.DestroyShaderModule(_fragmentModule, null);
            }

            _target?.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CompositePushGpu
        {
            internal float4 ObserverUq;
            internal int4 Counts;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AtlasBodyGpu
        {
            internal float4 RightTan;
            internal float4 UpCos;
            internal float4 FwdDist;
            internal float4 Misc;
        }
    }

    internal readonly struct AtlasTileSource
    {
        internal AtlasTileSource(RenderImage image, RenderImage depthImage, AtlasProjection projection, string bodyId)
        {
            Image = image;
            DepthImage = depthImage;
            Projection = projection;
            BodyId = bodyId;
        }

        internal RenderImage Image { get; }
        internal RenderImage DepthImage { get; }
        internal AtlasProjection Projection { get; }
        internal string BodyId { get; }
    }
}

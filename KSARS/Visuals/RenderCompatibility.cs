using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace KSARS.Visuals
{
    internal static class RenderCompatibility
    {
        internal const string SupportedFileVersion = "2026.10.7.5541";
        internal const string SupportedProductVersion = "2026.10.7.5541+2804b437e2241b7e48194add88b4553f50aff5e1";
        internal const string SupportedSha256 = "5E47D0374D82A409A08B8F134E57B38AF7F64E19D2EA9B54A17CADFCE90305DF";
        internal const string SupportedCoreRendererSha256 = "D45CBAC3EAC00B614A36654C43557328C5BB9B4FFC4BEC1DF9AD76F12B7BE5E8";

        private static readonly (string Type, string Name)[] SourceBoundaryCalls =
        {
            ("KSA.Rendering.RenderImageEx", "ClearDepthImages"),
            ("KSA.PrePassRenderer", "CopyDepthImageToSrc"),
            ("KSA.Rendering.RenderTarget", "BeginRendering"),
            ("KSA.Rendering.Sun.SunRenderer", "Draw"),
            ("KSA.StaticCelestialDistanceRendering", "Run"),
            ("KSA.StaticCelestial", "RenderSphere"),
            ("KSA.GenericMeshRenderer", "WriteCommands"),
            ("KSA.SunbloomRenderer", "Render"),
            ("KSA.OverallBloomRenderer", "Render"),
            ("KSA.Program", "RenderAaPreparePasses"),
        };

        internal static bool TryResolveSourceHeightHandle(int originalHandle, float minimumHeight, float maximumHeight,
            int neutralCubemapHandle, out int resolvedHandle)
        {
            resolvedHandle = -1;
            if (!float.IsFinite(minimumHeight) || !float.IsFinite(maximumHeight))
            {
                return false;
            }

            if (originalHandle >= 0)
            {
                resolvedHandle = originalHandle;
                return true;
            }

            if (minimumHeight == 0.0f && maximumHeight == 0.0f && neutralCubemapHandle >= 0)
            {
                resolvedHandle = neutralCubemapHandle;
                return true;
            }

            return false;
        }

        internal static bool TryValidateSourceBoundary(IEnumerable<(string Type, string Name)> calls, bool nativeDepthLoad, out string reason)
        {
            if (!nativeDepthLoad)
            {
                reason = "main BeginRendering depth attachment load op is not Load";
                return false;
            }

            var list = calls as List<(string Type, string Name)> ?? calls.ToList();
            int previousIndex = -1;
            foreach (var (type, name) in SourceBoundaryCalls)
            {
                int index = -1;
                int count = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Type == type && list[i].Name == name)
                    {
                        count++;
                        if (index < 0)
                        {
                            index = i;
                        }
                    }
                }

                if (count == 0)
                {
                    reason = $"missing required source boundary call {type}::{name}";
                    return false;
                }

                if (count > 1)
                {
                    reason = $"duplicate required source boundary call {type}::{name}";
                    return false;
                }

                if (index <= previousIndex)
                {
                    reason = $"required source boundary call {type}::{name} out of order";
                    return false;
                }

                previousIndex = index;
            }

            reason = "source boundary sequence intact";
            return true;
        }

        private static readonly (string Type, string Name)[] SolarQueueRequiredCalls =
        {
            ("KSA.Universe", "get_WorldSun"),
            ("KSA.Camera", "GetPositionEgo"),
            ("KSA.Rendering.Sun.SunRenderer", "StartShowingDistOf"),
            ("KSA.Rendering.Sun.SunMeshRenderable", "Draw"),
        };

        private static readonly string[] GpuRecordingNames =
        {
            "BeginRendering", "EndRendering", "DrawIndexed", "DrawIndexedIndirect", "DrawIndirect",
            "PipelineBarrier", "PipelineBarrier2", "DrawMesh", "DrawMeshInstanced",
            "DrawMeshInstanceAllPasses", "Dispatch", "CopyImage", "CopyBuffer", "BlitImage",
        };

        internal static bool IsSolarDistanceSupported(double distance) =>
            double.IsFinite(distance) && distance > 0.0
            && float.IsFinite((float)distance) && (float)distance > 0.0f;

        internal static bool TryValidateSolarQueueBoundary(
            IEnumerable<(string Type, string Name)> sunDrawCalls,
            IEnumerable<(string Type, string Name)> meshQueueCalls,
            out string reason)
        {
            var sun = sunDrawCalls as List<(string Type, string Name)> ?? sunDrawCalls.ToList();
            var mesh = meshQueueCalls as List<(string Type, string Name)> ?? meshQueueCalls.ToList();

            int previousIndex = -1;
            foreach (var (type, name) in SolarQueueRequiredCalls)
            {
                int index = -1;
                int count = 0;
                for (int i = 0; i < sun.Count; i++)
                {
                    if (sun[i].Type == type && sun[i].Name == name)
                    {
                        count++;
                        if (index < 0)
                        {
                            index = i;
                        }
                    }
                }

                if (count == 0)
                {
                    reason = $"missing required sun queue call {type}::{name}";
                    return false;
                }

                if (count > 1)
                {
                    reason = $"duplicate required sun queue call {type}::{name}";
                    return false;
                }

                if (index <= previousIndex)
                {
                    reason = $"required sun queue call {type}::{name} out of order";
                    return false;
                }

                previousIndex = index;
            }

            var gpuInSun = sun.Where(c => GpuRecordingNames.Contains(c.Name)
                    && !(c.Type == "KSA.Rendering.Sun.SunMeshRenderable" && c.Name == "Draw"))
                .ToList();
            if (gpuInSun.Count > 0)
            {
                reason = $"SunRenderer::Draw records GPU work via {gpuInSun[0].Type}::{gpuInSun[0].Name}";
                return false;
            }

            if (mesh.Count(c => c.Name == "DrawMeshInstanceAllPasses") != 1)
            {
                reason = "SunMeshRenderable::Draw must call DrawMeshInstanceAllPasses exactly once";
                return false;
            }

            var forbidden = mesh.Where(c =>
                    (GpuRecordingNames.Contains(c.Name) && c.Name != "DrawMeshInstanceAllPasses")
                    || c.Name == "Draw" || c.Name == "Render")
                .ToList();
            if (forbidden.Count > 0)
            {
                reason = $"SunMeshRenderable::Draw reaches GPU or unrelated renderer work via {forbidden[0].Type}::{forbidden[0].Name}";
                return false;
            }

            reason = "solar queue boundary verified: draw defers to bucket queue only";
            return true;
        }

        private static readonly (string Type, string Name)[] RebuildBoundaryCalls =
        {
            ("KSA.Program", "BuildRenderTargets"),
            ("KSA.Program", "SetCameraUbo"),
            ("Core.Renderer", "AcknowledgeSwapchainRebuild"),
        };

        internal static bool TryValidateRebuildBoundary(
            IReadOnlyList<(string DeclaringType, string Name, string ReturnType)> calls,
            int returnCount,
            IReadOnlyList<(string OpCode, string? Member)> nonNopTail,
            out string reason)
        {
            if (returnCount != 1)
            {
                reason = $"rebuild boundary has {returnCount} return instructions; single-return tail required";
                return false;
            }

            var waitIdleIndices = calls
                .Select((call, index) => (call, index))
                .Where(x => x.call.DeclaringType == "Brutal.VulkanApi.VkDeviceExtensions" && x.call.Name == "WaitIdle")
                .ToList();
            if (waitIdleIndices.Count != 2)
            {
                reason = $"expected exactly 2 Brutal.VulkanApi.VkDeviceExtensions::WaitIdle calls, found {waitIdleIndices.Count}";
                return false;
            }

            if (waitIdleIndices.Any(x => x.call.ReturnType != "System.Void"))
            {
                reason = "Brutal.VulkanApi.VkDeviceExtensions::WaitIdle return type is not System.Void";
                return false;
            }

            int firstIdle = waitIdleIndices[0].index;
            int lastIdle = waitIdleIndices[1].index;
            int previousIndex = firstIdle;
            foreach (var (type, name) in RebuildBoundaryCalls)
            {
                var indices = calls
                    .Select((call, index) => (call, index))
                    .Where(x => x.call.DeclaringType == type && x.call.Name == name)
                    .Select(x => x.index)
                    .ToList();
                if (indices.Count == 0)
                {
                    reason = $"missing required rebuild boundary call {type}::{name}";
                    return false;
                }

                if (indices.Count > 1)
                {
                    reason = $"duplicate required rebuild boundary call {type}::{name}";
                    return false;
                }

                if (indices[0] <= previousIndex)
                {
                    reason = $"required rebuild boundary call {type}::{name} out of order";
                    return false;
                }

                previousIndex = indices[0];
            }

            if (lastIdle <= previousIndex)
            {
                reason = "final WaitIdle does not follow Core.Renderer::AcknowledgeSwapchainRebuild";
                return false;
            }

            if (lastIdle != calls.Count - 1)
            {
                reason = "final WaitIdle is not the last call before the single return";
                return false;
            }

            var expectedTail = new (string OpCode, string? Member)[]
            {
                ("ldsfld", "KSA.Program::_renderer"),
                ("callvirt", "Core.KSADeviceContextEx::get_Device"),
                ("call", "Brutal.VulkanApi.VkDeviceExtensions::WaitIdle"),
                ("ret", null),
            };
            if (nonNopTail.Count < expectedTail.Length)
            {
                reason = $"idle tail too short ({nonNopTail.Count} non-NOP instructions)";
                return false;
            }

            var tail = nonNopTail.Skip(nonNopTail.Count - expectedTail.Length).ToList();
            for (int i = 0; i < expectedTail.Length; i++)
            {
                if (tail[i].OpCode != expectedTail[i].OpCode || tail[i].Member != expectedTail[i].Member)
                {
                    reason = $"idle tail mismatch at -{expectedTail.Length - i}: expected {expectedTail[i].OpCode} {expectedTail[i].Member ?? "<none>"}, found {tail[i].OpCode} {tail[i].Member ?? "<none>"}";
                    return false;
                }
            }

            reason = "rebuild idle boundary verified";
            return true;
        }

        private static readonly MethodSpec[] RequiredMethods =
        {
            new("KSA.Program", ".ctor", false, T("System.Void"), new[] { T("System.Collections.Generic.IReadOnlyList`1", T("System.String")) }),
            new("KSA.Program", "OnPreRender", false, T("RenderCore.FrameAcquireResult"), new[] { T("System.Double") }),
            new("KSA.Program", "RenderGame", false, T("System.Void"), new[] { T("RenderCore.AcquiredFrame"), T("System.Double") }),
            new("KSA.Program", "RebuildRenderer", false, T("System.Void"), new[] { T("System.Boolean") }),
            new("KSA.Program", "RenderViewport", false, T("System.Void"), new[] { T("Brutal.VulkanApi.CommandBuffer"), T("KSA.IViewport"), T("System.Int32") }),
            new("KSA.Program", "UpdateShaderData", false, T("System.Void"), new[] { T("System.Double"), T("KSA.IViewport") }),
            new("KSA.Program", "Dispose", false, T("System.Void"), new TypeSpec[0]),
            new("KSA.GlobalShaderBindings", "CreateDescriptors", true, T("System.Void"), new[] { T("Core.Renderer"), T("Brutal.VulkanApi.VkShaderStageFlags") }),
            new("KSA.GlobalShaderBindings", "Initialize", true, T("System.Void"), new[] { T("Core.Renderer"), T("System.Int32") }),
            new("KSA.ViewportRegistry", "Allocate", true, T("KSA.ViewportAllocation"), new TypeSpec[0]),
            new("KSA.ViewportRegistry", "SealAllocation", true, T("System.Void"), new TypeSpec[0]),
            new("KSA.IViewport", "get_ShaderSlot", false, T("System.Int32"), new TypeSpec[0]),
        };

        private static readonly FieldSpec[] RequiredFields =
        {
            new("KSA.Program", "ResourceFrameIndex", false, T("System.Int32")),
            new("KSA.GlobalShaderBindings", "_currentFrame", true, T("System.Int32")),
            new("KSA.ViewportRegistry", "_allocationSealed", true, T("System.Boolean")),
            new("KSA.ViewportRegistry", "ShaderIndexPool", true, T("Brutal.Collections.FreeListIndexPool")),
            new("KSA.ViewportRegistry", "_views", true, T("KSA.IViewport[]")),
            new("KSA.ViewportRegistry", "_nextViewportId", true, T("System.Int32")),
            new("KSA.ViewportRegistry", "DisposalBuffer", true, T("KSA.IViewport[]")),
            new("KSA.GlobalShaderBindings", "_descriptorSets", true, T("Brutal.VulkanApi.VkDescriptorSet[]")),
            new("KSA.GlobalShaderBindings", "_frameCount", true, T("System.Int32")),
            new("KSA.Program", "_cameraData", true, T("KSA.UboCameraData[]")),
            new("KSA.Program", "_lightingData", true, T("KSA.UboLightingData[]")),
            new("KSA.Program", "_celestialData", true, T("KSA.UboCelestialData[]")),
            new("KSA.Program", "_sunPositionEgo", true, T("Brutal.Numerics.double3")),
            new("KSA.Program", "_vesselData", true, T("KSA.UboVesselData[]")),
            new("KSA.SunbloomRenderer", "_sunflareMemory", false, T("Brutal.VulkanApi.Abstractions.MappedMemory")),
            new("KSA.SunbloomRenderer", "_sunflareBuffers", false, T("Brutal.VulkanApi.Abstractions.BufferEx")),
            new("KSA.SunbloomRenderer", "_frameCount", false, T("System.Int32")),
        };

        internal static bool EvaluateIdentity(string? fileVersion, string? productVersion, string? sha256)
        {
            return !string.IsNullOrEmpty(fileVersion) && fileVersion == SupportedFileVersion
                && !string.IsNullOrEmpty(productVersion) && productVersion == SupportedProductVersion
                && !string.IsNullOrEmpty(sha256) && string.Equals(sha256, SupportedSha256, StringComparison.OrdinalIgnoreCase);
        }

        internal static InspectionResult InspectAssembly(Assembly gameAssembly)
        {
            try
            {
                string location = gameAssembly.Location;
                if (string.IsNullOrEmpty(location) || !File.Exists(location))
                {
                    return InspectionResult.Failed("game assembly has no readable file location");
                }

                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(location);
                string sha256;
                using (FileStream stream = File.OpenRead(location))
                {
                    sha256 = Convert.ToHexString(SHA256.HashData(stream));
                }

                if (!EvaluateIdentity(versionInfo.FileVersion, versionInfo.ProductVersion, sha256))
                {
                    return InspectionResult.Failed(
                        $"game binary identity mismatch (fileVersion='{versionInfo.FileVersion}', productVersion='{versionInfo.ProductVersion}', sha256='{sha256}')");
                }

                var members = new Dictionary<string, MemberInfo>();
                foreach (MethodSpec spec in RequiredMethods)
                {
                    if (!TryResolveMethod(gameAssembly, spec, out MethodBase? member))
                    {
                        return InspectionResult.Failed($"method signature not found: {spec}");
                    }
                    members[$"{spec.DeclaringType}::{spec.Name}"] = member!;
                }

                foreach (FieldSpec spec in RequiredFields)
                {
                    if (!TryResolveField(gameAssembly, spec, out FieldInfo? member))
                    {
                        return InspectionResult.Failed($"field signature not found: {spec}");
                    }
                    members[$"{spec.DeclaringType}::{spec.Name}"] = member!;
                }

                return InspectionResult.Matched(new ReadOnlyDictionary<string, MemberInfo>(members));
            }
            catch (IOException ex)
            {
                return InspectionResult.Failed($"game assembly could not be read: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                return InspectionResult.Failed($"game assembly could not be read: {ex.Message}");
            }
            catch (BadImageFormatException ex)
            {
                return InspectionResult.Failed($"game assembly is malformed: {ex.Message}");
            }
            catch (TypeLoadException ex)
            {
                return InspectionResult.Failed($"game assembly member type could not be loaded: {ex.Message}");
            }
            catch (ReflectionTypeLoadException ex)
            {
                return InspectionResult.Failed($"game assembly types could not be inspected: {ex.Message}");
            }
        }

        private static bool TryResolveMethod(Assembly assembly, MethodSpec spec, out MethodBase? member)
        {
            member = null;
            Type? type = assembly.GetType(spec.DeclaringType, throwOnError: false);
            if (type == null)
            {
                return false;
            }

            const BindingFlags visibility = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            BindingFlags flags = visibility | (spec.IsStatic ? BindingFlags.Static : BindingFlags.Instance);

            List<MethodBase> candidates = spec.Name == ".ctor"
                ? type.GetConstructors(flags).Cast<MethodBase>().ToList()
                : type.GetMethods(flags).Where(m => m.Name == spec.Name).Cast<MethodBase>().ToList();

            List<MethodBase> matches = candidates.Where(m => SignatureMatches(m, spec)).ToList();
            if (matches.Count != 1)
            {
                return false;
            }

            member = matches[0];
            return true;
        }

        private static bool TryResolveField(Assembly assembly, FieldSpec spec, out FieldInfo? member)
        {
            member = null;
            Type? type = assembly.GetType(spec.DeclaringType, throwOnError: false);
            if (type == null)
            {
                return false;
            }

            const BindingFlags visibility = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            BindingFlags flags = visibility | (spec.IsStatic ? BindingFlags.Static : BindingFlags.Instance);

            FieldInfo? field = type.GetField(spec.Name, flags);
            if (field == null || !TypeMatches(field.FieldType, spec.FieldType))
            {
                return false;
            }

            member = field;
            return true;
        }

        private static bool SignatureMatches(MethodBase method, MethodSpec spec)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != spec.Parameters.Length)
            {
                return false;
            }

            for (int i = 0; i < parameters.Length; i++)
            {
                if (!TypeMatches(parameters[i].ParameterType, spec.Parameters[i]))
                {
                    return false;
                }
            }

            return method is not MethodInfo info || TypeMatches(info.ReturnType, spec.ReturnType);
        }

        private static bool TypeMatches(Type type, TypeSpec spec)
        {
            if (spec.Arguments.Length == 0)
            {
                return !type.IsGenericType && type.FullName == spec.FullName;
            }

            if (!type.IsGenericType || type.GetGenericTypeDefinition().FullName != spec.FullName)
            {
                return false;
            }

            Type[] arguments = type.GetGenericArguments();
            if (arguments.Length != spec.Arguments.Length)
            {
                return false;
            }

            for (int i = 0; i < arguments.Length; i++)
            {
                if (!TypeMatches(arguments[i], spec.Arguments[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private static TypeSpec T(string fullName, params TypeSpec[] arguments) => new(fullName, arguments);

        private readonly struct TypeSpec
        {
            public TypeSpec(string fullName, TypeSpec[] arguments)
            {
                FullName = fullName;
                Arguments = arguments;
            }

            public string FullName { get; }
            public TypeSpec[] Arguments { get; }
        }

        private readonly struct MethodSpec
        {
            public MethodSpec(string declaringType, string name, bool isStatic, TypeSpec returnType, TypeSpec[] parameters)
            {
                DeclaringType = declaringType;
                Name = name;
                IsStatic = isStatic;
                ReturnType = returnType;
                Parameters = parameters;
            }

            public string DeclaringType { get; }
            public string Name { get; }
            public bool IsStatic { get; }
            public TypeSpec ReturnType { get; }
            public TypeSpec[] Parameters { get; }

            public override string ToString() =>
                $"{DeclaringType}::{(IsStatic ? "static " : "")}{Name}({string.Join(", ", Parameters.Select(p => p.FullName))})";
        }

        private readonly struct FieldSpec
        {
            public FieldSpec(string declaringType, string name, bool isStatic, TypeSpec fieldType)
            {
                DeclaringType = declaringType;
                Name = name;
                IsStatic = isStatic;
                FieldType = fieldType;
            }

            public string DeclaringType { get; }
            public string Name { get; }
            public bool IsStatic { get; }
            public TypeSpec FieldType { get; }

            public override string ToString() =>
                $"{DeclaringType}::{(IsStatic ? "static " : "")}{FieldType.FullName} {Name}";
        }

        internal sealed class InspectionResult
        {
            private InspectionResult(bool baselineMatched, string reason, IReadOnlyDictionary<string, MemberInfo> members)
            {
                BaselineMatched = baselineMatched;
                Reason = reason;
                Members = members;
            }

            public bool BaselineMatched { get; }
            public string Reason { get; }
            public IReadOnlyDictionary<string, MemberInfo> Members { get; }

            internal static InspectionResult Matched(IReadOnlyDictionary<string, MemberInfo> members) =>
                new(true, "supported build", members);

            internal static InspectionResult Failed(string reason) =>
                new(false, reason, new ReadOnlyDictionary<string, MemberInfo>(new Dictionary<string, MemberInfo>()));
        }
    }
}

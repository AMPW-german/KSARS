using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace KSARS.Visuals
{
    internal static class ShaderCompileUtil
    {
        internal static unsafe byte[] CompileSpirv(string source, string shaderKindName, string name)
        {
            if (!Enum.TryParse(shaderKindName, ignoreCase: false, out ShaderKind kind))
            {
                throw new InvalidOperationException($"unsupported shader kind '{shaderKindName}'");
            }

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
            return new ReadOnlySpan<byte>(result.GetBytes(), length).ToArray();
        }

        internal static VkShaderModule CompileModule(Core.Renderer renderer, string source,
            string shaderKindName, string name) =>
            ShaderModuleExtensions.FromSpirv(renderer.Device,
                CompileSpirv(source, shaderKindName, name), null);
    }
}

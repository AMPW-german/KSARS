using System.Text;
using System.Text.RegularExpressions;
using KSA;

namespace KSARS.Visuals
{
    internal sealed class SolarGlowShaderSources
    {
        internal const string VertexSource = AtlasCompositeProbe.VertexSource;
        internal const int GlarePushByteCount = 64;

        private const string FragmentFile = "sunbloom.frag";
        private const string BlurFile = "sunbloom_blur.comp";
        private const string MergeFile = "sunbloom_merge.comp";
        private const string IntersectFile = "sunbloom_intersect.glsl";

        private static readonly Regex IncludePattern =
            new("^[ \t]*#include[ \t]+\"([^\"]+)\"", RegexOptions.Compiled);

        private SolarGlowShaderSources(string fragmentSource, string blurSource, string mergeSource)
        {
            FragmentSource = fragmentSource;
            BlurSource = blurSource;
            MergeSource = mergeSource;
        }

        internal string FragmentSource { get; }
        internal string BlurSource { get; }
        internal string MergeSource { get; }

        internal static string ResolveShaderRoot(string assemblyLocation)
        {
            string? directory = Path.GetDirectoryName(assemblyLocation);
            if (directory is null)
            {
                throw new InvalidOperationException("solar glare shader sources unsupported: KSA assembly directory unresolved");
            }

            return Path.Combine(directory, "Content", "Core", "Shaders");
        }

        internal static SolarGlowShaderSources LoadInstalled() =>
            Load(ResolveShaderRoot(typeof(Program).Assembly.Location));

        internal static SolarGlowShaderSources Load(string shaderRoot)
        {
            string root = Path.GetFullPath(shaderRoot);
            string fragmentPath = Path.Combine(root, "PostProcess", FragmentFile);
            string blurPath = Path.Combine(root, "PostProcess", BlurFile);
            string mergePath = Path.Combine(root, "PostProcess", MergeFile);
            string fragment = AdaptFragment(ExpandFile(root, fragmentPath, new HashSet<string>()));
            string blur = AdaptBlur(ExpandFile(root, blurPath, new HashSet<string>(),
                stripAtmosphereBindings: true));
            string merge = AdaptMerge(ExpandFile(root, mergePath, new HashSet<string>()));
            return new SolarGlowShaderSources(fragment, blur, merge);
        }

        private static string ReadNormalized(string path)
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"solar glare shader sources unsupported: missing '{path}'");
            }

            return File.ReadAllText(path).Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static string ExpandFile(string root, string path, HashSet<string> stack,
            bool stripAtmosphereBindings = false)
        {
            string normalized = Path.GetFullPath(path);
            if (!stack.Add(normalized))
            {
                throw new InvalidOperationException(
                    $"solar glare shader sources unsupported: include cycle at '{normalized}'");
            }

            try
            {
                string text = ReadNormalized(normalized);
                if (Path.GetFileName(normalized) == IntersectFile)
                {
                    text = AdaptIntersectHeader(text);
                }
                else if (stripAtmosphereBindings
                         && Path.GetFileName(normalized) == BlurFile)
                {
                    text = StripBlurAtmosphereBindings(text);
                }

                var expanded = new StringBuilder(text.Length);
                string directory = Path.GetDirectoryName(normalized)!;
                foreach (string line in text.Split('\n'))
                {
                    Match include = IncludePattern.Match(line);
                    if (!include.Success)
                    {
                        expanded.Append(line).Append('\n');
                        continue;
                    }

                    string includePath = Path.GetFullPath(
                        Path.Combine(directory, include.Groups[1].Value));
                    if (!includePath.StartsWith(root + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"solar glare shader sources unsupported: include '{include.Groups[1].Value}' in '{normalized}' escapes the shader root");
                    }

                    expanded.Append(ExpandFile(root, includePath, stack, stripAtmosphereBindings));
                }

                return expanded.ToString();
            }
            finally
            {
                stack.Remove(normalized);
            }
        }

        private static string ReplaceOnce(string text, string seam, string replacement, string pass)
        {
            int first = text.IndexOf(seam, StringComparison.Ordinal);
            if (first < 0)
            {
                throw new InvalidOperationException(
                    $"solar glare shader sources unsupported: {pass} seam missing");
            }

            if (text.IndexOf(seam, first + seam.Length, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException(
                    $"solar glare shader sources unsupported: {pass} seam duplicated");
            }

            return string.Concat(text.AsSpan(0, first), replacement, text.AsSpan(first + seam.Length));
        }

        private static string InsertBeforeOnce(string text, string marker, string injection, string pass)
        {
            int index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"solar glare shader sources unsupported: {pass} insertion marker missing");
            }

            if (text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException(
                    $"solar glare shader sources unsupported: {pass} insertion marker duplicated");
            }

            return string.Concat(text.AsSpan(0, index), injection, text.AsSpan(index));
        }

        private const string IntersectInjection = """
            struct KsarsAtlasBody
            {
                vec4 rightTan;
                vec4 upNear;
                vec4 fwdFar;
                vec4 sourceInfo;
            };
            layout (std140, set = 4, binding = 0) uniform KsarsAtlasBodies
            {
                KsarsAtlasBody bodies[3];
            } ksarsAtlas;
            layout (set = 4, binding = 2) uniform sampler2D ksarsDepthTiles[3];

            layout (push_constant) uniform KsarsGlareParams
            {
                vec4 observerUq;
                vec4 solarRest;
                ivec4 info;
                vec4 glareScale;
            } ksarsParams;

            vec3 ksarsInverseRest(vec3 nObs)
            {
                vec3 u = ksarsParams.observerUq.xyz;
                float q = ksarsParams.observerUq.w;
                float qq = q * q;
                float a = 0.5 * dot(nObs - u, nObs - u);
                float b = 0.5 * dot(nObs + u, nObs + u);
                vec3 nPerp = nObs - dot(nObs, u) * u;
                return normalize(u * (qq * b - a) + nPerp * (2.0 * q));
            }

            bool insideSun(vec2 pixelUV)
            {
                if (ksarsParams.info.z == 0)
                {
                    return false;
                }

                if (ksarsParams.observerUq.w == 1.0)
                {
                    return ksarsStockInsideSun(pixelUV);
                }

                // pixelUV arrives centered [-0.5, 0.5] like the stock helper; ndc = uv * 2.
                vec2 ndc = pixelUV * 2.;
                vec4 clipFar = vec4(ndc, 1.0, 1.0);
                vec4 viewFar = global.camera.inverseProjection * clipFar;
                vec3 nObs = normalize(mat3(global.camera.inverseView) * viewFar.xyz);
                vec3 nRest = ksarsInverseRest(nObs);
                // The stock ray test uses a sphere of radius 2 * sunRadius (the
                // RaySphereIntersection argument is a radius), so the transformed
                // inside test also measures against 2R / distance.
                float fwdZ = dot(nRest, normalize(ksarsParams.solarRest.xyz));
                if (fwdZ <= 0.0)
                {
                    return false;
                }

                float radiusRatio = ksarsParams.solarRest.w * 2.0;
                return 1.0 - fwdZ * fwdZ <= radiusRatio * radiusRatio;
            }

            """;

        private static string AdaptIntersectHeader(string text)
        {
            text = ReplaceOnce(text, "bool insideSun(vec2 pixelUV)",
                "bool ksarsStockInsideSun(vec2 pixelUV)", "intersect stock helper rename");
            return InsertBeforeOnce(text, "#endif// SUNBLOOM_INTERSECT_INCLUDED",
                IntersectInjection, "intersect helper injection");
        }

        private const string FragmentHelpers = """
            vec3 ksarsRestDirection(vec2 uv)
            {
                vec4 clipFar = vec4(uv * 2.0 - 1.0, 1.0, 1.0);
                vec4 viewFar = global.camera.inverseProjection * clipFar;
                vec3 nObs = normalize(mat3(global.camera.inverseView) * viewFar.xyz);
                if (ksarsParams.observerUq.w == 1.0)
                {
                    return nObs;
                }

                return ksarsInverseRest(nObs);
            }

            float ksarsSolarFront(vec3 nRest)
            {
                KsarsAtlasBody solarBody = ksarsAtlas.bodies[ksarsParams.info.y];
                float z = dot(nRest, solarBody.fwdFar.xyz);
                vec2 t = vec2(dot(nRest, solarBody.rightTan.xyz), dot(nRest, solarBody.upNear.xyz));
                float radiusRatio = ksarsParams.solarRest.w;
                float disc = radiusRatio * radiusRatio - dot(t, t);
                if (z <= 0.0 || disc < 0.0)
                {
                    return solarBody.sourceInfo.y;
                }

                return solarBody.sourceInfo.y * (z - sqrt(disc));
            }

            float ksarsBodyOccludes(vec3 nRest)
            {
                float solarFront = ksarsSolarFront(nRest);
                for (int i = 0; i < ksarsParams.info.x; i++)
                {
                    if (i == ksarsParams.info.y)
                    {
                        continue;
                    }

                    KsarsAtlasBody body = ksarsAtlas.bodies[i];
                    float z = dot(nRest, body.fwdFar.xyz);
                    if (!(z > 0.0))
                    {
                        continue;
                    }

                    float inv = 1.0 / (z * body.rightTan.w);
                    vec2 bodyUv = vec2(0.5 * (1.0 + dot(nRest, body.rightTan.xyz) * inv),
                        0.5 * (1.0 - dot(nRest, body.upNear.xyz) * inv));
                    if (any(lessThan(bodyUv, vec2(0.0))) || any(greaterThan(bodyUv, vec2(1.0))))
                    {
                        continue;
                    }

                    ivec2 tileSize = textureSize(ksarsDepthTiles[i], 0);
                    ivec2 coord = clamp(ivec2(bodyUv * vec2(tileSize)), ivec2(0), tileSize - 1);
                    float depth = texelFetch(ksarsDepthTiles[i], coord, 0).r;
                    if (depth <= 0.0 || depth > 1.0)
                    {
                        continue;
                    }

                    float ratio = body.upNear.w / body.fwdFar.w;
                    float a = ratio / (1.0 - ratio);
                    float b = body.upNear.w / (1.0 - ratio);
                    float sourceZ = b / (depth + a);
                    vec2 rayXY = (2.0 * bodyUv - 1.0) * body.rightTan.w;
                    float radial = sourceZ * sqrt(1.0 + dot(rayXY, rayXY));
                    if (radial > 0.0 && radial < solarFront)
                    {
                        return 1.0;
                    }
                }

                return 0.0;
            }

            float ksarsVisibility(vec2 uv, float sunDepth)
            {
                if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
                {
                    return 0.0;
                }

                float sceneDepth = textureLod(inputDepth, uv, 0).r;
                float flareSunDepth = ksarsParams.observerUq.w == 1.0 ? sunDepth : 0.0;
                float vis = flareSunDepth < sceneDepth ? 0.0f : 1.0f;
                if (vis < 0.5 || ksarsParams.info.x <= 1)
                {
                    return vis;
                }

                return ksarsBodyOccludes(ksarsRestDirection(uv)) > 0.5 ? 0.0 : 1.0;
            }

            """;

        private static string AdaptFragment(string text)
        {
            text = InsertBeforeOnce(text,
                "float outerSun(vec2 uv, vec2 ssSunPos, float sunRadius, float sunRadiusFactor) {",
                FragmentHelpers, "fragment helper injection");
            text = ReplaceOnce(text,
                "float depthCull = (sunData.sunDepth) < depth  ? 0.0f : 1.0f;",
                "float depthCull = ksarsVisibility(inUV, sunData.sunDepth);", "fragment depth cull");
            text = ReplaceOnce(text,
                "depthCull = textureLod(mainColor, inUV, 0.0).a;",
                "depthCull *= clamp(textureLod(mainColor, inUV, 0.0).a, 0.0, 1.0);", "fragment cloud alpha");
            text = ReplaceOnce(text,
                "    if (sunWorldDist < showSurfDist * 0.78)\n    {\n        vec2 sunPosNoDivide = sunData.screenspaceSunPosition - halfUV;",
                "    if (ksarsParams.info.z != 0 && sunWorldDist < showSurfDist * 0.78)\n    {\n        vec2 sunPosNoDivide = sunData.screenspaceSunPosition - halfUV;",
                "fragment near-surface anchor adjustment");
            text = ReplaceOnce(text,
                "    innerSunTemp = innerSun(\n        pixelUV,\n        sunPos,\n        sunRadius * sunData.sunGlowRadiusMultiplier * sunData.innerGlowRadiusScalar * occScale\n        );",
                "    innerSunTemp = ksarsParams.info.z != 0 ? innerSun(\n        pixelUV,\n        sunPos,\n        sunRadius * sunData.sunGlowRadiusMultiplier * sunData.innerGlowRadiusScalar * occScale\n        ) : 0.0;",
                "fragment inner sun anchor fallback");
            text = ReplaceOnce(text,
                "float outerSun = outerSunGlowNoDither(pixelUV, sunPos, sunRadius, sunGlowRadius*occScale);",
                "float outerSun = ksarsParams.info.z != 0 ? outerSunGlowNoDither(pixelUV, sunPos, sunRadius, sunGlowRadius*occScale) : 0.0;",
                "fragment outer glow anchor fallback");
            return text;
        }

        private static string StripBlurAtmosphereBindings(string text)
        {
            text = ReplaceOnce(text, "#define ATMOSPHERE_DATA_SET 5", "", "blur atmosphere data set");
            text = ReplaceOnce(text, "#include \"../Atmosphere/AtmosphereDataUbo.glsl\"", "",
                "blur atmosphere data include");
            text = ReplaceOnce(text, "#include \"../Atmosphere/AtmosphereFunctions.glsl\"", "",
                "blur atmosphere functions include");
            text = ReplaceOnce(text,
                "layout (set = 4, binding = 0) uniform sampler2DArray transmittanceLut;\n"
                + "layout (set = 4, binding = 1) uniform sampler3D aerialPerspectiveColorRGBTransmittanceRLut;\n"
                + "layout (set = 4, binding = 2) uniform sampler3D aerialPerspectiveTransmittanceGBLut;\n"
                + "layout (set = 4, binding = 3) uniform sampler2D aerialRangeLut;\n"
                + "layout (set = 4, binding = 4) uniform sampler2DArray multipleScatteringLut;",
                "", "blur atmosphere lut bindings");
            text = ReplaceOnce(text, "#define UI_COVERAGE_MASK_SET 6", "#define UI_COVERAGE_MASK_SET 5",
                "blur ui coverage set remap");
            return text;
        }

        private static string AdaptBlur(string text)
        {
            text = ReplaceOnce(text, "vec3 allBloomAndFlare(vec2 uv, vec2 sunPos, vec2 _) {",
                "vec3 allBloomAndFlare(vec2 uv, vec2 sunPos, vec2 _) {\n    if (ksarsParams.info.z == 0) { return vec3(0.0); }",
                "blur unsupported-anchor early return");
            return ReplaceOnce(text,
                "float distScale = clamp(0.3, 5.0, 5.0 / sunData.screenspaceSunRadius);",
                "float distScale = clamp(5.0 / sunData.screenspaceSunRadius, 0.3, 5.0);",
                "blur distScale undefined-clamp fix");
        }

        private const string MergePushInjection = """
            layout (push_constant) uniform KsarsGlareParams
            {
                vec4 observerUq;
                vec4 solarRest;
                ivec4 info;
                vec4 glareScale;
            } ksarsParams;

            """;

        private static string AdaptMerge(string text)
        {
            text = InsertBeforeOnce(text, "void main()", MergePushInjection, "merge push injection");
            return ReplaceOnce(text, "    vec4 finalColor = screenCol + vec4(bloom.rgb, 0.0);",
                "    vec4 finalColor = screenCol + vec4(min(max(bloom.rgb * ksarsParams.glareScale.y, vec3(0.0)), vec3(65504.0)), 0.0);",
                "merge bounded doppler-scaled additive write");
        }
    }
}

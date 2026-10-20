using System.Text;
using System.Text.RegularExpressions;

namespace KSARS.Visuals
{
    // Adapted installed MilkyWay.frag: the stock fullscreen directional
    // background is kept byte-identical except for three injected seams.
    //   1. Before main(): the KSARS observer push constant, a set-2 thermal
    //      spectrum table, and the relativistic helpers.
    //   2. After getWorldDirection reconstruction, before the galactic-plane
    //      transform: inverse aberration maps the observed direction to its
    //      rest direction and yields the per-ray Doppler factor.
    //   3. Before output: the composited band radiance is Doppler-shifted as
    //      an effective-temperature extended source (the temperature shift
    //      absorbs the D^3 intensity transform for a thermal-modeled band).
    // The injection fails closed: any missing or duplicated seam throws, and
    // the caller keeps native rendering instead.
    internal sealed class MilkyWayShaderSources
    {
        internal MilkyWayShaderSources(string fragment)
        {
            Fragment = fragment;
        }

        internal string Fragment { get; }

        private const string FragmentFile = "MilkyWay.frag";

        internal static MilkyWayShaderSources LoadInstalled() =>
            Load(SolarGlowShaderSources.ResolveShaderRoot(typeof(KSA.Program).Assembly.Location));

        internal static MilkyWayShaderSources Load(string shaderRoot)
        {
            string root = Path.GetFullPath(shaderRoot);
            string fragmentPath = Path.Combine(root, FragmentFile);
            string fragment = AdaptFragment(ExpandFile(root, fragmentPath, new HashSet<string>()));
            return new MilkyWayShaderSources(fragment);
        }

        private static string ReadNormalized(string path)
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"milky way shader sources unsupported: missing '{path}'");
            }

            return File.ReadAllText(path).Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static string ExpandFile(string root, string path, HashSet<string> stack)
        {
            string normalized = Path.GetFullPath(path);
            if (!stack.Add(normalized))
            {
                throw new InvalidOperationException(
                    $"milky way shader sources unsupported: include cycle at '{normalized}'");
            }

            try
            {
                string text = ReadNormalized(normalized);
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
                            $"milky way shader sources unsupported: include '{include.Groups[1].Value}' in '{normalized}' escapes the shader root");
                    }

                    expanded.Append(ExpandFile(root, includePath, stack));
                }

                return expanded.ToString();
            }
            finally
            {
                stack.Remove(normalized);
            }
        }

        private static readonly Regex IncludePattern =
            new("^[ \\t]*#include[ \\t]+[\"<]([^\">]+)[\">]", RegexOptions.Compiled);

        private static string ReplaceOnce(string text, string seam, string replacement, string pass)
        {
            int first = text.IndexOf(seam, StringComparison.Ordinal);
            if (first < 0)
            {
                throw new InvalidOperationException(
                    $"milky way shader sources unsupported: {pass} seam missing");
            }

            if (text.IndexOf(seam, first + seam.Length, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException(
                    $"milky way shader sources unsupported: {pass} seam duplicated");
            }

            return string.Concat(text.AsSpan(0, first), replacement, text.AsSpan(first + seam.Length));
        }

        private static string InsertBeforeOnce(string text, string marker, string injection, string pass)
        {
            int index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"milky way shader sources unsupported: {pass} insertion marker missing");
            }

            if (text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException(
                    $"milky way shader sources unsupported: {pass} insertion marker duplicated");
            }

            return string.Concat(text.AsSpan(0, index), injection, text.AsSpan(index));
        }

        private static string AdaptFragment(string expanded)
        {
            string text = InsertBeforeOnce(expanded, "void main()", Injection, "observer");
            text = InsertBeforeOnce(text,
                "    sampleDir   = (global.camera.galacticPlane",
                "    float ksarsDoppler;\n    sampleDir = ksarsRestDirection(sampleDir, ksarsDoppler);\n",
                "aberration");
            text = ReplaceOnce(text,
                "    outColor = vec4(result, 1.0);",
                "    result = ksarsRelativisticBackground(result, ksarsDoppler);\n    outColor = vec4(result, 1.0);",
                "output");
            return text;
        }

        // Injected immediately before `void main()`. The observer push
        // constant is vec4(u, q) in the fragment stage; the thermal table is
        // the same packed layout ThermalSpectrumTable.WriteGpuTable emits
        // (1024 log-spaced samples + 3 per-channel hot-continuation rows).
        // spectrum()/displayRgb() mirror the relativistic star vertex shader.
        private const string Injection = """
            // ---- KSARS relativistic background (injected) ----
            layout(push_constant) uniform KsarsObserver { vec4 uq; } ksarsObserver;
            layout(std430, set = 2, binding = 0) readonly buffer KsarsThermal {
                vec4 samples[1024]; vec4 hot[3];
            } ksarsThermal;

            // Effective band temperature: the Milky Way texture is a mix of
            // starlight and dust; a ~5000 K thermal model is a documented
            // first-order approximation for its Doppler chroma shift. The
            // prior is only valid inside a limited temperature window: past
            // KSARS_MW_TEFF_VALID the fake Rayleigh-Jeans tail of the 5000 K
            // model would glow as a saturated blue point forever (the real
            // composite spectrum's visible window has long since shifted
            // past), so the band term fades out over [VALID, GONE] and lets
            // the true blackbody terms - the catalog stars - own the
            // extreme-beta cap. The sky correctly goes dark in between.
            const float KSARS_MW_TEFF       = 5000.0;
            const float KSARS_MW_TEFF_VALID = 4.0e4;
            const float KSARS_MW_TEFF_GONE  = 2.0e5;

            vec4 ksarsSpectrum(float temperature) {
                if (temperature > 1e8) {
                    float r = 1e8 / temperature;
                    vec3 xyz = vec3(
                        ksarsThermal.hot[0].x + r * (ksarsThermal.hot[0].y + r * ksarsThermal.hot[0].z),
                        ksarsThermal.hot[1].x + r * (ksarsThermal.hot[1].y + r * ksarsThermal.hot[1].z),
                        ksarsThermal.hot[2].x + r * (ksarsThermal.hot[2].y + r * ksarsThermal.hot[2].z));
                    return vec4(xyz / (xyz.x + xyz.y + xyz.z), log(xyz.y) + log(temperature) - 18.420680744);
                }
                float p = clamp(log(temperature) * (1023.0 / 18.420680744), 0.0, 1023.0);
                int i = min(int(floor(p)), 1022);
                return mix(ksarsThermal.samples[i], ksarsThermal.samples[i+1],
                    clamp(p - float(i), 0.0, 1.0));
            }

            vec3 ksarsDisplayRgb(vec3 xyz) {
                vec3 rgb = vec3(
                    dot(vec3(3.240969942,-1.537383178,-0.498610760), xyz),
                    dot(vec3(-0.969243636,1.875967502,0.041555057), xyz),
                    dot(vec3(0.055630080,-0.203976959,1.056971514), xyz));
                float t = 0.0;
                if (rgb.r < 0.0) t = max(t, -rgb.r / (xyz.y - rgb.r));
                if (rgb.g < 0.0) t = max(t, -rgb.g / (xyz.y - rgb.g));
                if (rgb.b < 0.0) t = max(t, -rgb.b / (xyz.y - rgb.b));
                return max(mix(rgb, vec3(xyz.y), t), vec3(0.0)) / xyz.y;
            }

            // Observed -> rest direction (stable-q inverse aberration) and
            // the per-ray Doppler factor D = 2q / (A + q^2 B).
            vec3 ksarsRestDirection(vec3 nObs, out float doppler) {
                doppler = 1.0;
                float q = ksarsObserver.uq.w;
                if (q == 1.0) return nObs;
                vec3 u = normalize(ksarsObserver.uq.xyz);
                float A = 0.5 * dot(nObs - u, nObs - u);
                float B = 0.5 * dot(nObs + u, nObs + u);
                vec3 nPerp = nObs - dot(nObs, u) * u;
                doppler = (2.0 * q) / (A + q * q * B);
                return normalize(u * (q * q * B - A) + nPerp * (2.0 * q));
            }

            // Extended-source transform: for a thermal-modeled band the D^3
            // intensity transform is absorbed into the shifted temperature,
            // so the visible-band scale is just visLum(T*D)/visLum(T). The
            // off-locus residual gain is preserved the same way the star
            // path treats non-thermal color structure. mwFade is the
            // model-validity fade: once the band's effective temperature
            // leaves the window where a 5000 K prior means anything, the
            // band's contribution goes to zero rather than glowing as a
            // saturated blue point.
            vec3 ksarsRelativisticBackground(vec3 rest, float doppler) {
                if (doppler == 1.0) return rest;
                float mwFade = 1.0 - smoothstep(KSARS_MW_TEFF_VALID, KSARS_MW_TEFF_GONE,
                    KSARS_MW_TEFF * doppler);
                float restY = dot(rest, vec3(0.212639006, 0.715168679, 0.072192315));
                if (!(restY > 0.0) || !(doppler > 0.0)) return rest * mwFade;
                vec3 xyz = vec3(
                    dot(rest, vec3(0.412390799, 0.357584339, 0.180480788)),
                    restY,
                    dot(rest, vec3(0.019330819, 0.119194780, 0.950532152)));
                float sum = xyz.x + xyz.y + xyz.z;
                if (!(sum > 0.0)) return rest * mwFade;
                vec3 originalXyz = xyz / sum;
                vec4 restS = ksarsSpectrum(KSARS_MW_TEFF);
                vec4 shiftS = ksarsSpectrum(KSARS_MW_TEFF * doppler);
                if (!(shiftS.y > 0.0) || !(restS.y > 0.0)) return rest * mwFade;
                float logD = log(doppler);
                float weight = exp(-logD * logD);
                vec3 gain = mix(vec3(1.0), originalXyz / restS.xyz, weight);
                vec3 chroma = shiftS.xyz * gain;
                float csum = chroma.x + chroma.y + chroma.z;
                if (!(csum > 0.0)) return rest * mwFade;
                vec3 rgbPerY = ksarsDisplayRgb(chroma / csum);
                return rgbPerY * restY * exp(shiftS.w - restS.w) * mwFade;
            }

            """;
    }
}

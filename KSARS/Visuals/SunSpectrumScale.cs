namespace KSARS.Visuals
{
    // Doppler-scaled light colors for relativistic vessel lighting. Each
    // distinct source color (sun, planet fill, nearest-celestial ambient) is
    // fitted once to a blackbody prior and re-evaluated at that source ray's
    // Doppler factor. These are extended/resolved sources, so the model is
    // evaluated with point-source beaming exponent 0 - the thermal luminance
    // ratio lum(D*T)/lum(T) already carries the full relativistic
    // surface-brightness scaling and the source's solid angle is unchanged
    // under distance-preserving aberration. Deliberately a standalone type:
    // nothing here touches game assemblies, so the scale computation stays
    // unit-testable outside the runtime.
    //
    // Scope note: diffuse aggregate-sky irradiance (starlight + Milky Way ~
    // 1e-8 of solar irradiance at rest) is deliberately NOT modeled. The
    // searchlight boost needed to raise it into significance (~1e8, D>~140)
    // pushes its spectrum above the thermal table's 100000K range - the
    // physically honest result is that everything exits the visible band at
    // extreme beta and the vessel darkens. Discrete resolvable sources (sun,
    // planet fill) are the only terms that carry visible flux.
    internal static class SunSpectrumScale
    {
        // Physically the resolved-disk surface brightness scales D^4 without
        // bound (≈360x at beta 0.9), which saturates the shading and feeds
        // bloom until the vessel reads as a white mass. The luminance gain is
        // clamped at this ceiling while the chromaticity shift remains
        // untouched, so the source keeps blue-shifting with beta after its
        // intensity saturates. Default sits just under the reported washout
        // onset (D^4 ~ 4 at beta ~0.33); KSARS_SUN_GAIN_CAP overrides.
        internal const double DefaultMaxGain = 4.0;
        private static double s_maxGain = ParseMaxGain();

        internal static double MaxGain
        {
            get => s_maxGain;
            set
            {
                if (double.IsFinite(value) && value > 0.0)
                {
                    s_maxGain = value;
                }
            }
        }

        private static double ParseMaxGain()
        {
            string? raw = Environment.GetEnvironmentVariable("KSARS_SUN_GAIN_CAP");
            return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                && double.IsFinite(parsed) && parsed > 0.0
                ? parsed
                : DefaultMaxGain;
        }

        private sealed class FitEntry
        {
            internal StellarSpectrumModel? Model;
            internal StellarEmission RestEmission;
            internal double RestR = 1.0, RestG = 1.0, RestB = 1.0;
            internal bool Failed;
        }

        // Source colors are stable frame to frame; distinct keys fit once each.
        // The dictionary is bounded so runaway distinct values cannot grow it.
        private const int MaxFits = 16;
        private static readonly Dictionary<(double X, double Y, double Z), FitEntry> s_fits = new();
        private static ThermalSpectrumTable? s_table;
        private static StellarTemperatureFitter? s_fitter;

        // Per-channel scale for a source color: composes a capped bolometric
        // gain with a per-channel chromaticity ratio so the rest color maps to
        // itself exactly at D=1. A source shifted out of the thermal table's
        // range contributes no visible light (zero scale). Any failure returns
        // false so the caller keeps rest colors - cosmetic degradation, not a
        // correctness fault.
        internal static bool Try(double r, double g, double b,
            double doppler, out double scaleR, out double scaleG, out double scaleB)
        {
            scaleR = scaleG = scaleB = 1.0;
            if (!double.IsFinite(doppler) || !(doppler > 0.0))
            {
                return false;
            }

            if (doppler == 1.0)
            {
                return true;
            }

            if (!SpectralColorSpace.TryLinearRgbToXyz(r, g, b,
                    out (double X, double Y, double Z) xyz))
            {
                return false;
            }

            var key = (xyz.X, xyz.Y, xyz.Z);
            if (!s_fits.TryGetValue(key, out FitEntry? entry))
            {
                if (s_fits.Count >= MaxFits)
                {
                    return false;
                }

                entry = Fit(table(), fitter(), xyz);
                s_fits[key] = entry;
            }

            if (entry.Failed || entry.Model is null)
            {
                return false;
            }

            if (!entry.Model.TryEvaluate(doppler, 0.0, out StellarEmission shifted))
            {
                return false;
            }

            // A source boosted out of the thermal table's range contributes no
            // visible light.
            if (double.IsNegativeInfinity(shifted.LogFlux))
            {
                scaleR = scaleG = scaleB = 0.0;
                return true;
            }

            double gain = Math.Exp(shifted.LogFlux - entry.RestEmission.LogFlux);
            if (!double.IsFinite(gain) || !(gain >= 0.0))
            {
                return false;
            }

            // Luminance ceiling: intensity saturates at MaxGain while the
            // per-channel chromaticity ratio below keeps shifting hue.
            gain = Math.Min(gain, s_maxGain);

            if (!SpectralColorSpace.TryXyzToDisplayRgb(shifted.X, shifted.Y, shifted.Z,
                    out (double R, double G, double B) shiftedRgb))
            {
                return false;
            }

            const double epsilon = 1e-9;
            double sx = gain * (entry.RestR > epsilon ? shiftedRgb.R / entry.RestR : 1.0);
            double sy = gain * (entry.RestG > epsilon ? shiftedRgb.G / entry.RestG : 1.0);
            double sz = gain * (entry.RestB > epsilon ? shiftedRgb.B / entry.RestB : 1.0);
            if (!double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(sz)
                || sx < 0.0 || sy < 0.0 || sz < 0.0)
            {
                return false;
            }

            scaleR = sx;
            scaleG = sy;
            scaleB = sz;
            return true;
        }

        private static FitEntry Fit(ThermalSpectrumTable table, StellarTemperatureFitter fitter,
            in (double X, double Y, double Z) xyz)
        {
            var entry = new FitEntry { Failed = true };
            try
            {
                if (StellarSpectrumModel.TryCreate(table, fitter,
                        xyz.X, xyz.Y, xyz.Z, out StellarSpectrumModel? model)
                    && model!.TryEvaluate(1.0, 0.0, out entry.RestEmission)
                    && SpectralColorSpace.TryXyzToDisplayRgb(
                        entry.RestEmission.X, entry.RestEmission.Y, entry.RestEmission.Z,
                        out (double R, double G, double B) restRgb))
                {
                    entry.Model = model;
                    entry.RestR = restRgb.R;
                    entry.RestG = restRgb.G;
                    entry.RestB = restRgb.B;
                    entry.Failed = false;
                }
            }
            catch (Exception)
            {
                // Table load or fit failure: the failed entry is keyed so this
                // is not retried per frame.
            }

            return entry;
        }

        private static ThermalSpectrumTable table() => s_table ??= ThermalSpectrumTable.LoadEmbedded();

        private static StellarTemperatureFitter fitter()
        {
            s_fitter ??= new StellarTemperatureFitter(temperature =>
            {
                ThermalSample sample = table().Evaluate(temperature);
                return (sample.X, sample.Y, sample.Z);
            });
            return s_fitter;
        }

        // Drops the cached fits (called from the lighting probe reset path so
        // stale models cannot survive probe state resets). The thermal table
        // and fitter are pure data and are kept; the gain ceiling is re-read
        // from the environment.
        internal static void Reset()
        {
            s_maxGain = ParseMaxGain();
            s_fits.Clear();
        }
    }
}

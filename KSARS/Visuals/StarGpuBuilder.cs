using System.Runtime.InteropServices;

namespace KSARS.Visuals
{
    // CPU-side packing of published catalog records into the std430 star SSBO
    // consumed by RelativisticStarShaders. colorFlux w retains the modeled
    // native sprite energy integral (StarFluxReference) as a calibration prior;
    // sprite emission itself reuses the native color*B profile so output is
    // pixel-identical to stock at rest. aux x is the native scale byte
    // normalized to [0,1]; it drives the magnitude-dependent sprite size and
    // radial profile. Spectral columns come from the per-color fitted prior
    // cache; unfitted colors get temperature <= 0 and keep raw color at all D.
    internal static class StarGpuBuilder
    {
        internal const int RecordBytes = 64;

        // Four sequential vec4-equivalent rows; identical std430 layout to
        // float4 fields but keeps the record free of engine numeric types.
        [StructLayout(LayoutKind.Sequential)]
        internal struct StarGpu
        {
            internal float DirectionX;
            internal float DirectionY;
            internal float DirectionZ;
            internal float Temperature;
            internal float ColorR;
            internal float ColorG;
            internal float ColorB;
            internal float Flux;
            internal float ThermalX;
            internal float ThermalY;
            internal float ThermalZ;
            internal float LogRestLuminance;
            internal float Scale01;
            internal float Aux1;
            internal float Aux2;
            internal float Aux3;
        }

        internal static StarGpu[] Build(IReadOnlyList<StarCatalog.Entry> entries,
            StarSpectralCache cache, out int fittedCount)
        {
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(cache);

            var output = new StarGpu[entries.Count];
            fittedCount = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                StarCatalog.Entry entry = entries[i];
                uint key = entry.PackedRgb & 0xffffffu;
                float r = (key & 0xffu) / 255.0f;
                float g = ((key >> 8) & 0xffu) / 255.0f;
                float b = ((key >> 16) & 0xffu) / 255.0f;
                float flux = (float)StarFluxReference.Integrate(entry.Scale);

                float temperature = -1.0f;
                float tx = 0.0f, ty = 0.0f, tz = 0.0f, logRest = 0.0f;
                if (cache.TryGet(key, out StellarSpectrumModel? model) && model is not null)
                {
                    ThermalSample rest = model.ThermalRest;
                    temperature = (float)model.Fit.TemperatureKelvin;
                    tx = (float)rest.X;
                    ty = (float)rest.Y;
                    tz = (float)rest.Z;
                    logRest = (float)rest.LogLuminance;
                    fittedCount++;
                }

                output[i] = new StarGpu
                {
                    DirectionX = (float)entry.Direction.X,
                    DirectionY = (float)entry.Direction.Y,
                    DirectionZ = (float)entry.Direction.Z,
                    Temperature = temperature,
                    ColorR = r,
                    ColorG = g,
                    ColorB = b,
                    Flux = flux,
                    ThermalX = tx,
                    ThermalY = ty,
                    ThermalZ = tz,
                    LogRestLuminance = logRest,
                    Scale01 = entry.Scale / 255.0f,
                };
            }

            return output;
        }
    }
}

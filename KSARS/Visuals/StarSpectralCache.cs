namespace KSARS.Visuals
{
    // Per-raw-catalog-color spectral model cache. Catalog bytes are treated as
    // raw linear Rec.709/D65 working-space values (no gamma decode), fitted to a
    // blackbody prior via the shared StellarTemperatureFitter. These are fitted
    // priors, not measured stellar temperatures; no spectral rendering or GPU
    // effect is active.
    internal sealed class StarSpectralCache
    {
        private readonly object _lock = new();
        private readonly Dictionary<uint, StellarSpectrumModel?> _models = new();
        private readonly ThermalSpectrumTable _table;
        private readonly StellarTemperatureFitter _fitter;
        private string? _failure;

        internal StarSpectralCache(ThermalSpectrumTable table)
        {
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _fitter = new StellarTemperatureFitter(temperature =>
            {
                ThermalSample sample = table.Evaluate(temperature);
                return (sample.X, sample.Y, sample.Z);
            });
        }

        internal int CachedColorCount
        {
            get
            {
                lock (_lock)
                {
                    return _models.Count;
                }
            }
        }

        internal int ModelCount
        {
            get
            {
                lock (_lock)
                {
                    int count = 0;
                    foreach (StellarSpectrumModel? model in _models.Values)
                    {
                        if (model is not null)
                        {
                            count++;
                        }
                    }
                    return count;
                }
            }
        }

        internal string? Failure
        {
            get
            {
                lock (_lock)
                {
                    return _failure;
                }
            }
        }

        internal bool TryGet(uint packedRgb, out StellarSpectrumModel? model)
        {
            // Native byte4 memory order: R in the lowest byte. Alpha/scale byte
            // is excluded from the key; identical colors share one model.
            uint key = packedRgb & 0xffffffu;
            lock (_lock)
            {
                if (_models.TryGetValue(key, out model))
                {
                    return model is not null;
                }

                if (key == 0)
                {
                    _models[key] = null;
                    model = null;
                    return false;
                }

                double r = (key & 0xffu) / 255.0;
                double g = ((key >> 8) & 0xffu) / 255.0;
                double b = ((key >> 16) & 0xffu) / 255.0;
                if (!SpectralColorSpace.TryLinearRgbToXyz(r, g, b, out (double X, double Y, double Z) xyz)
                    || !StellarSpectrumModel.TryCreate(_table, _fitter, xyz.X, xyz.Y, xyz.Z, out model))
                {
                    _failure = $"spectral fit rejected catalog color 0x{key:x6}; cache retains prior entries";
                    model = null;
                    return false;
                }

                _models[key] = model;
                return true;
            }
        }

        internal string Describe()
        {
            lock (_lock)
            {
                int models = 0;
                foreach (StellarSpectrumModel? model in _models.Values)
                {
                    if (model is not null)
                    {
                        models++;
                    }
                }

                string state = _failure is null ? "" : $"; degraded: {_failure}";
                return $"{_models.Count} catalog color(s) -> {models} fitted model(s), linear Rec709/D65 prior, fitted not measured{state}";
            }
        }
    }
}

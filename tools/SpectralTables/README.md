# SpectralTables — offline thermal table generator

Generates the KSARS blackbody thermal lookup table from the bundled CIE 1931
2-degree observer data. Standalone and fully offline: no game DLLs, no network
access, no runtime/mod hookup. The tool compiles the exact reference sources
(`KSARS/PhysicsConstants.cs`, `KSARS/Visuals/PlanckSpectrum.cs`) via links, so
the table math is identical to the mod's verified Planck implementation.
`KSARS/Visuals/StellarTemperatureFitter.cs` is likewise linked so the tool's
fitting checks exercise the same runtime code — no duplicated algorithm.

The output is a thermal-only artifact — it is not the final spectra/basis
table. The v2 table plus its license/source notices are embedded as managed
resources in `KSARS.dll` (`KSARS.ThermalTable.v2.bin`, `KSARS.SpectralData.*`)
and consumed by the runtime `ThermalSpectrumTable` reader and the read-only
star spectral cache; no renderer or GPU path consumes it yet.

## Usage

```powershell
dotnet run --project tools/SpectralTables/SpectralTables.csproj -- `
    tools/SpectralTables/Data/CIE_xyz_1931_2deg.csv <fresh-output-path>
```

- Exactly two arguments: source CSV and output path; anything else prints
  usage and exits 2.
- The output is created with `FileMode.CreateNew`: an existing file is
  refused (no overwrite), and a missing parent directory is not created
  automatically.
- I/O, argument, and malformed-input errors are reported on stderr with
  exit code 1.

## Table definition

- 1024 logarithmically spaced temperature samples from 1 K to 1e8 K (lnT grid).
- Per sample, Planck spectral radiance (SI, W sr^-1 m^-3 per metre) is
  integrated against the CIE 1931 x̄/ȳ/z̄ colour-matching functions by trapezoid
  summation over 360–830 nm at 1 nm; the integrand is zero outside the tabulated
  observer range.
- Photopic efficacy constant 683 lm/W is applied for the luminance channel.
- XYZ is normalized so x + y + z = 1 (normalized chromaticity), and the record
  stores the natural logarithm of photopic luminance in cd/m^2 — an SI
  quantity, not an arbitrary game HDR calibration.
- `ThermalTable.Interpolate` accepts any finite T >= 1 K, including temperatures
  beyond the grid edge (served by the high-temperature continuation below);
  T < 1 and non-finite inputs are rejected.

## High-temperature continuation (v2)

For T >= T0 = 1e8 K, with r = T0 / T:

- `XYZ_scaled = C0 + r * C1 + r^2 * C2` using the nine Float64 coefficients
  stored as triplets C0(XYZ), C1(XYZ), C2(XYZ); the record's normalized
  chromaticity is XYZ_scaled divided by its sum.
- `logY = ln(Y_scaled) + ln(T) - ln(T0)`.
- The coefficients come from integrating the Rayleigh-Jeans expansion
  B/RJ = 1 - x/2 + x^2/12; max x < 0.0004, so the discarded x^4/720 term is
  < 4e-17 pointwise (this bounds the discarded series term, not the complete
  integration error).
- Tested at actual max 2.73357808e8 K as well as 1e12, 1e20, and
  `double.MaxValue`, including join continuity against logPlanck at T0.

## StellarTemperatureFitter (shared runtime source)

`StellarTemperatureFitter` maps a non-negative, finite, non-black **linear**
XYZ triplet to a blackbody temperature estimate:

- Brightness is normalized away before fitting.
- Distance metric: CIE 1960 UCS coordinates u = 4X/(X+15Y+3Z),
  v = 6Y/(X+15Y+3Z).
- Search: logarithmic 129-point coarse scan over 800..100000 K followed by 64
  golden-section refinement iterations; returns the deterministic (u, v)
  residual plus a boundary flag — an estimate, not a measured temperature.
- Exact Planck recovery is tested to 1e-7 relative; the residual is reported
  explicitly and is not bounded.
- Catalog colors are emitted raw so they are never silently sRGB-decoded
  (InstancedStarTechnique inspected). The read-only runtime catalog capture
  seam and `StarSpectralCache` feed these raw bytes through the explicit
  linear Rec.709/D65 working-space prior (`SpectralColorSpace`); no
  renderer-facing RGB conversion choice or runtime render changes are made.

## StellarSpectrumModel (CPU runtime model — no shader)

`StellarSpectrumModel` (lead-authored, `KSARS/Visuals`) caches the fit plus the
rest-frame thermal sample and evaluates Doppler-shifted emission:

- D = 1 is the identity: returns the rest normalized XYZ and rest log
  luminance unchanged.
- Chromaticity: explicit off-locus prior — a positive per-channel tint ratio
  restNormalized/thermalRestNormalized, bounded per fit by 1/thermalRest,
  blended smoothly toward unity by exp(-ln(D)^2); the result is renormalized
  to chromaticity, so tint never changes flux. Exact thermal colors have unity
  tint. This is not a reconstruction of measured absorption lines.
- Luminance: pure-thermal Planck(D*T)/Planck(T) log ratio divided once by D^2
  — the unresolved-source observed solid-angle factor; no absolute catalog
  scale/PSF calibration is inferred.
- A Doppler-shifted temperature below 1 K evaluates as numerically dark
  (zero emission, -inf log flux) — it is not clamped bright.
- `SpectralColorSpace.TryXyzToDisplayRgb` is display-only gamut mapping that
  leaves physical XYZ untouched: out-of-gamut colors desaturate toward
  equal-RGB gray at the same Y, with no HDR clamp, exposure, or tone mapping.
- All of this is reference/model code; no spectral star shader or GPU path is
  active.

## Embedded resources and integrity

The v2 table and notices are embedded into `KSARS.dll`; a Cecil/manifest test
pins all four resource names, the 32,904-byte length, and the table SHA-256
(`ThermalSpectrumTable.ArtifactSha256`), so the embedded artifact is
checksum-validated by the suite — not merely present.

## Binary format (`<output>.bin`)

Little-endian throughout. **v2** (current) total size 32,904 bytes:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 8 | magic `KSARSTH2` |
| 8 | 4 | Int32 version = 2 |
| 12 | 4 | Int32 sample count = 1024 |
| 16 | 8 | Float64 min temperature = 1 |
| 24 | 8 | Float64 max temperature = 1e8 |
| 32 | 32 | SHA-256 of the source CSV |
| 64 | 1024 x 32 | records: (x, y, z, lnY) as four Float64 |
| 32832 | 72 | 9 Float64 continuation coefficients: C0(XYZ), C1(XYZ), C2(XYZ) |

Historical **v1** output used magic `KSARSTH1`/version 1 and the same 64-byte
header + 1024 records without the coefficient tail (32,832 bytes).

## Determinism

Repeated writes on the same runtime produce identical bytes. Bitwise identity
across platforms/runtimes is not promised until verified.

## Attribution and licensing

- The CIE 1931 2-degree observer dataset is the unmodified original under
  `Data/` (CC BY-SA 4.0, CIE Vienna 2019, DOI 10.25039/CIE.DS.xvudnb9b) — see
  `Data/README.md` and `Data/LICENSE-CC-BY-SA-4.0.txt`.
- Derived thermal table data produced by this tool is likewise CC BY-SA 4.0
  with CIE attribution retained.
- The generator code is under this repository's existing license; the
  attribution does not relicense the mod.

## Limitations

- The low-temperature visible tail is retained only as logarithmic radiance;
  normalized chromaticity alone conveys no brightness information.
- Offline `ThermalTable.Interpolate` rejects T < 1 K and non-finite inputs;
  finite T >= 1 including above the 1e8 K grid edge is served by the
  documented continuation. The runtime `ThermalSpectrumTable.Evaluate`
  instead evaluates positive T < 1 as numerically dark — different contract,
  deliberately separate.
- The fitter residual is explicit and unbounded; the model's tint is the
  documented bounded prior, not measured spectra, and no renderer consumes it.
- No runtime/exposure calibration or reflected-light basis is implemented
  here.

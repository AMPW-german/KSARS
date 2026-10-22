# Generated thermal tables

## ThermalTable.bin

Offline, thermal-only lookup table derived from the bundled CIE 1931 2-degree
observer data: normalized XYZ chromaticity plus natural-log photopic luminance
(cd/m^2) over 1024 lnT samples, 1..1e8 K. Format details: `../README.md`.

- Source dataset: `../Data/` — CIE 1931 2° colour-matching functions, CC BY-SA
  4.0, DOI 10.25039/CIE.DS.xvudnb9b (attribution/license details in
  `../Data/README.md`).
- Derived output license: CC BY-SA 4.0 with CIE attribution retained.
- Created 2026-10-16 with:
  `dotnet run --project tools/SpectralTables/SpectralTables.csproj -- tools/SpectralTables/Data/CIE_xyz_1931_2deg.csv tools/SpectralTables/Generated/ThermalTable.bin`
- Size: 32,832 bytes. SHA-256:
  `339B55E4DE4F7F35F0818C2EED15DA5E0C99FAA7B86A1FBC3D5372B52DD05269`
- The generator refuses to overwrite an existing output (verified 2026-10-16:
  second run exited 1, file unchanged).

## ThermalTable.v2.bin

Current artifact — same table plus the 9 Float64 high-T continuation
coefficients (magic `KSARSTH2`, version 2; see `../README.md`). v1
`ThermalTable.bin` above is retained unchanged as historical output.

- Created 2026-10-16 with:
  `dotnet run --no-build --project tools/SpectralTables/SpectralTables.csproj -- tools/SpectralTables/Data/CIE_xyz_1931_2deg.csv tools/SpectralTables/Generated/ThermalTable.v2.bin`
- Size: 32,904 bytes. SHA-256:
  `396CA9CAC5004A5F43F680C553FD5F3160920A81FA09489BB86192D077A18B4A`
- Same source dataset and CC BY-SA 4.0 attribution as v1.

Embedded into `KSARS.dll` as managed resources (the generator itself remains
fully offline): `KSARS.ThermalTable.v2.bin` (`ThermalTable.v2.bin`, 32,904
bytes), `KSARS.SpectralData.LICENSE.txt` (`../Data/LICENSE-CC-BY-SA-4.0.txt`),
`KSARS.SpectralData.SOURCE.md` (`../Data/README.md`), and
`KSARS.SpectralData.TABLES.md` (this file). v1 `ThermalTable.bin` remains
repo-only and is not embedded.

Embedding is integrity-pinned: `StarCatalogHookTests` asserts all four
resource names, the 32,904-byte length, and the embedded table's SHA-256
against the value above. `ThermalSpectrumTable.LoadEmbedded` loads and
checksum-verifies the table before any catalog hook installs; the three
notices are embedded and test-pinned only — the loader does not read them.

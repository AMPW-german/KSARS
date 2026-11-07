# KSAModTools

Shared MSBuild configuration, KSA/StarMap/NuGet assembly resolution, mod packaging targets, Git hooks, and composite GitHub Actions for Kitten Space Agency (KSA) mods.

## Structure

- `msbuild/KsaMod.props` — Author-neutral SDK/compiler defaults (`Debug`/`Release`), local KSA path fallbacks (`KSADir`, `KSAModsDir`, `KSADllRepoDir`), `UseKsaNuGetPackages` mode switch, `KsaNuGet.props` import, and `Lib.Harmony` package reference.
- `msbuild/KsaMod.targets` — Assembly/version defaults, local vs. NuGet KSA assembly resolution, `StarMap.API` reference, `SetVersionMetadata`, and `CopyBuildOutput` + `mod.toml` generation.
- `scripts/update_ksa_dlls.py` — Updates a KSA DLL snapshot repository with `KSA.dll`, `Planet.Core.dll`, `Planet.Render.Core.dll`, `ksa-versions.csv`, and `KsaNuGet.props` (resolving `Ahwoo.BRUTAL.*`, `BepuPhysics`, `BepuUtilities`, `Samboy063.Tomlet` on NuGet.org and `Lib.Harmony` from `StarMapLoader/StarMap`).
- `githooks/post-merge` & `githooks/post-checkout` — Keeps the `tooling/` submodule synced on `git pull` and branch switches while honoring dirty-tree safety and local branch/commit overrides.
- `ci/build-package/action.yml` & `ci/release/action.yml` — Composite GitHub Actions for building, testing, packaging, creating GitHub Releases, and publishing to SpaceDock.
- `.github/workflows/bump-consumers.yml` — Discovers repositories with the public `KSAModTools` GitHub App installed and opens/auto-merges `chore/bump-tooling` PRs on push to `main`.

## Usage in a Mod Repository

1. Add `KSAModTools` as a submodule at `tooling/`:
   ```sh
   git submodule add -b main https://github.com/AMPW-german/KSAModTools.git tooling
   git config core.hooksPath tooling/githooks
   ```
2. Import `KsaMod.props` at the top of your `.csproj` and `KsaMod.targets` at the bottom:
   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <Import Project="..\tooling\msbuild\KsaMod.props" />
     <PropertyGroup>
       <ModName>MyMod</ModName>
       <VersionPrefix>1.0.0</VersionPrefix>
       <AssemblyVersion>1.0.0.0</AssemblyVersion>
       <Company>YourName</Company>
       <Authors>YourName</Authors>
       <Copyright>MIT</Copyright>
     </PropertyGroup>
     <Import Project="..\tooling\msbuild\KsaMod.targets" />
   </Project>
   ```

## Local Tooling Override

To test a custom `KSAModTools` branch or commit locally without modifying tracked files:

- Via Git config:
  ```sh
  git config --local submodule.tooling.override <branch-or-commit-or-off>
  ```
- Or in `.csproj.user` / `.csproj`:
  ```xml
  <PropertyGroup>
    <ToolingRef>my-feature-branch</ToolingRef>
  </PropertyGroup>
  ```

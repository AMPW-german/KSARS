# Vulkan API Validation for KSA Mods

This guide explains how to run an isolated Kitten Space Agency instance with the Khronos
validation layer enabled, so Vulkan API errors made by the game or an injected mod are
reported instead of silently producing undefined behavior.

## What this is (and is not)

`VK_LAYER_KHRONOS_validation` checks supported Vulkan API usage against the specification
(state, synchronization, object lifetimes, handles, parameters). With synchronization
validation enabled it also tracks memory hazards between reads and writes of the same
resources. Its checks are not exhaustive — it validates what it has rules for, not the
whole spec.

It reports **API usage correctness**. It does **not** report:

- whether rendered output is visually correct (compare screenshots/reference renders),
- whether CPU-side logic or test suites pass (the managed test suite is unrelated),
- GPU performance numbers (use a profiler such as RenderDoc capture, Nsight Graphics, or
  Nsight Systems for that),
- driver-internal correctness. A run with no findings means none of the layer's checks
  fired for the exercised workload; it is **not** certification of spec compliance (the
  checks are incomplete) or of rendered correctness.

Validation adds meaningful per-call CPU overhead and synchronization tracking. Treat it as a
development-only aid: enable it for dedicated test runs, not for normal play. Mods shipped to
users do not depend on it.

## Installed layer (this machine, this user)

- Layer: `VK_LAYER_KHRONOS_validation`, Vulkan API version **1.4.363**, from the official
  **LunarG Vulkan SDK 1.4.363.0** installer (`vulkansdk-windows-X64-1.4.363.0-Installer.exe`).
- Provenance: `https://sdk.lunarg.com/sdk/download/1.4.363.0/windows/vulkan_sdk.exe`;
  installer Authenticode signature Valid, signer `LunarG, Inc.`; downloaded file SHA-256
  `94A82D378F7A5E3E54C9DB7D2FB7016AF136E14AC0A18DBF0F2F67A36352D141` (locally computed; no
  published checksum for this installer was verified — the Authenticode signature is the
  provenance evidence).
- Installed by file copy only (`copy_only=1`), per-user, no elevation, no PATH or
  environment changes:
  - SDK root: `C:\Users\AMPW\AppData\Local\VulkanValidationLayers\1.4.363.0\SDK`
  - Manifest: `...\SDK\Bin\VkLayer_khronos_validation.json`
  - x64 DLL: `...\SDK\Bin\VkLayer_khronos_validation.dll`
- Registration: `HKCU\SOFTWARE\Khronos\Vulkan\ExplicitLayers`, value name = the absolute
  manifest path above, `DWORD 0`. This makes the layer **discoverable by every process of
  this Windows account** (any KSA mod run under this user), without admin rights and without
  affecting other Windows users or machine-wide state. Discoverable does not mean active:
  the layer only engages for processes that request it via `VK_INSTANCE_LAYERS`, an explicit
  `vkCreateInstance` layer list, or Vulkan Configurator.

## Confirm the layer is discoverable

```powershell
vulkaninfo --summary
```

`VK_LAYER_KHRONOS_validation` must appear in the `Instance Layers` list. Loader warnings
about duplicate overlay layers (Steam, OBS, Epic EOS) are unrelated to validation.

## Per-run launch recipe (any KSA mod)

Every run needs an isolated instance directory prepared beforehand (for example via the
`StageRuntimeProbe` target or an equivalent copy). The instance's own `manifest.toml`
decides which mods load; the launch never modifies it. Each run gets a fresh evidence
directory so validation output is never overwritten.

```powershell
# --- user input: an existing isolated instance directory ---
$instancePath = 'C:\path\to\your\isolated\instance'
$starMapExe   = 'C:\Users\AMPW\Documents\My Games\Kitten Space Agency\mods\StarMap\StarMap.exe'

# --- fail fast if the instance, its manifest, or the launcher is missing ---
if (-not (Test-Path $instancePath))                             { throw "instance not found: $instancePath" }
if (-not (Test-Path (Join-Path $instancePath 'manifest.toml'))) { throw "manifest.toml missing in $instancePath" }
if (-not (Test-Path $starMapExe))                               { throw "launcher not found: $starMapExe" }

# --- unique per-run evidence directory (fails instead of overwriting) ---
$runDir = Join-Path $env:LOCALAPPDATA (
    'VulkanValidationLayers\runs\{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $runDir -ErrorAction Stop | Out-Null

# --- validation settings for THIS run; VK_LAYER_SETTINGS_PATH selects the directory ---
@(
    'khronos_validation.validate_sync = true',
    'khronos_validation.debug_action = VK_DBG_LAYER_ACTION_LOG_MSG',
    "khronos_validation.log_filename = $runDir\vvl-messages.log",
    'khronos_validation.report_flags = error,warn,perf,info'
) | Set-Content -Path (Join-Path $runDir 'vk_layer_settings.txt')

# --- remember any pre-existing process-level values so we restore, not erase ---
$names = 'STARMAP_INSTANCE_PATH','VK_INSTANCE_LAYERS','VK_LAYER_SETTINGS_PATH','VK_LOADER_DEBUG','KSARS_ATLAS_PROBE','KSARS_ATLAS_MAIN_PROBE'
$saved = @{}
foreach ($n in $names) { $saved[$n] = [Environment]::GetEnvironmentVariable($n, 'Process') }

$stdoutLog = Join-Path $runDir 'stdout.log'
$stderrLog = Join-Path $runDir 'stderr.log'

try {
    $env:STARMAP_INSTANCE_PATH  = $instancePath
    $env:VK_INSTANCE_LAYERS     = 'VK_LAYER_KHRONOS_validation'
    $env:VK_LAYER_SETTINGS_PATH = $runDir
    $env:VK_LOADER_DEBUG        = 'warn,error,info'

    # Optional, KSARS-only diagnostic probes (omit for other mods):
    # $env:KSARS_ATLAS_PROBE      = '1'
    # $env:KSARS_ATLAS_MAIN_PROBE = '1'

    Start-Process -FilePath $starMapExe `
        -WorkingDirectory (Split-Path $starMapExe -Parent) `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError  $stderrLog `
        -Wait
}
finally {
    foreach ($n in $names) {
        if ($null -eq $saved[$n]) { Remove-Item "Env:\$n" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($n, $saved[$n], 'Process') }
    }
}

Write-Output "Run evidence: $runDir"
```

All variable changes are process-scoped and restored in `finally`; nothing is written to the
user or machine environment, the registry, or any mod manifest. `StarMap.exe` is launched
from its own directory because mods load through the StarMap launcher, not `KSA.exe`.

For the KSARS mod specifically, the optional diagnostic probes can additionally be enabled
by uncommenting `$env:KSARS_ATLAS_PROBE = '1'` and `$env:KSARS_ATLAS_MAIN_PROBE = '1'` in the
`try` block. Both names are already in the saved list, so `finally` restores their previous
values either way. They are KSARS-internal switches and meaningless for other mods.

## Reading the evidence

After the app has actually created its Vulkan instance and run a workload, check
`vvl-messages.log` in the run directory:

```powershell
# activation record: emitted once per VkInstance when 'info' severity is enabled
Select-String -Path .\vvl-messages.log -Pattern 'CURRENT-VALIDATION-ENABLED'
# confirms synchronization validation is among the active checks
Select-String -Path .\vvl-messages.log -Pattern 'Synchronization'
# actual findings (no assumptions about count or absence)
Select-String -Path .\vvl-messages.log -Pattern 'Validation Error|Validation Warning|SYNC-HAZARD|VUID-'
```

The `CURRENT-VALIDATION-ENABLED` record is the runtime proof that the layer engaged and that
synchronization validation is active — it lists checks such as `Core Checks`,
`Synchronization`, `Stateless Parameter`, `Object lifetime`, `Thread Safety`,
`Handle Wrapping`. This recipe expects that record; its absence is not definitive proof the
layer never engaged (`info` severity or log routing could be misconfigured), but investigate
it before trusting the rest of the log — check `VK_INSTANCE_LAYERS`, the registry entry,
and that the process really used Vulkan. `stderr.log` additionally shows `VK_LOADER_DEBUG`
lines such as `Insert instance layer` / `Inserted device layer "VK_LAYER_KHRONOS_validation"`.

Interpretation cautions:

- A log with no findings certifies only that **none of the layer's checks fired for the
  exercised workload** — not spec compliance (the checks are incomplete) and not visual
  correctness. Exercise the code paths you care about; an idle launcher proves little.
- `enable_message_limit` (default on, `duplicate_message_limit` = 10) suppresses repeats —
  the number of matching lines in the log is **not** the true message frequency.
- An **orderly exit matters**: resource-lifetime and leak checks largely run at
  `vkDestroy*`/`vkDestroyDevice` time, so a killed process yields incomplete evidence.
- Loader duplicate-layer warnings and the application's own startup messages (e.g. the known
  `<Patch>` XML deserialization noise in KSA) are **not** validation findings and are not
  automatically harmless either — attribute anything suspicious by comparing against an
  isolated baseline run under a matching workload.
- The baseline keeps the **same validation settings active**; it excludes only the target
  mod (e.g. KSARS) — not the layer. Running baseline and modded runs both validated makes
  findings attributable to the mod stand out from base-game findings. Baseline and modded
  runs need the same launch procedure and workload, and a new launch still needs the same
  explicit approval process as any other game launch.

## Suggested workload for a KSARS validation run

Exercise the same paths in baseline and modded runs and preserve every log:

- a beta-zero baseline view, then nonzero beta (e.g. via the probe's observer-speed override)
- an atlas-managed body in view, the Sun, and views through IVA glass
- a window resize / fullscreen change (exercises the renderer-rebuild path)
- a queued "Recapture sources at current observer"
- an orderly in-game exit — never kill the process
- keep `vvl-messages.log`, `stdout.log`, `stderr.log`, and copied diagnostics per run

## Removal

Only unregister if you decide the layer should no longer be offered to any mod on this
account. Keep the installed files and run logs — they are the provenance record and the
evidence store. Remove exactly this one value, and only when no other mod or tool relies on
a registration under the same key:

```powershell
Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Khronos\Vulkan\ExplicitLayers' `
    -Name 'C:\Users\AMPW\AppData\Local\VulkanValidationLayers\1.4.363.0\SDK\Bin\VkLayer_khronos_validation.json'
```

Do not delete the `ExplicitLayers` key itself — other registrations may live under it. The
`C:\Users\AMPW\AppData\Local\VulkanValidationLayers` tree can stay indefinitely; deleting it
is only needed to reclaim disk space and destroys the stored run evidence.

## Current state (2026-10-16)

Instance `KSARS/obj/runtime-validation-20261016-1` ran under a lead-owned interactive shell,
launched with process-local environment variables equivalent to the recipe (not the literal
`Start-Process` call shown here), and exited normally — visual-owner uninstallation at
17:34:46, pipeline cache saved 17:34:47, shell exit code 0.

- Activation confirmed in the actual KSA process: `vvl-messages.log` contains
  `CURRENT-VALIDATION-ENABLED` listing Core Checks, Synchronization, Stateless Parameter,
  Object lifetime, Thread Safety, and Handle Wrapping.
- Lead-reviewed stdout recorded raw Sol/coverage/depth source captures and the first main
  Luna composite submission at frame 1, syncSlot 0, resourceFrame 0, rendererEpoch 1, with
  eight viewport slots; a resize to 1920x1200 produced rendererEpoch 2 with
  mainResume/compositeResume true, and main composition resumed at nativeFrame 16685 with
  `bodies=[]` (no post-resize body composition is implied). Queued recaptures: generation 2
  recorded Sol raw/coverage/depth at frame 21111 then Pallas/Borisov; generation 3 recorded
  Sol at frame 22213 then Earth/Luna.
- The VVL log contains these unique message categories (header names; counts intentionally
  omitted):
  - `Validation Error` `UNASSIGNED-non-acquired-swapchain-image-used` — `vkQueueSubmit`
    layout transitions on a non-acquired presentable image.
  - `Validation Error` `VUID-vkCmdDraw-imageLayout-00344` — a combined sampler descriptor
    (Set 2, Binding 2, Index 0) declared `GENERAL` while the image's last known layout was
    `SHADER_READ_ONLY_OPTIMAL`.
  - `Validation Performance Warning` `WARNING-Shader-OutputNotConsumed` — an unconsumed
    vertex attribute at location 3.
  Attribution is unproven — no object names identify the origin — so this is **not** a clean
  validation pass. An isolated baseline run with identical validation settings and workload
  (baseline excludes only the mod, not the layer) is required before assigning any finding
  to game or mod.
- No gate is claimed by this run.

Run log: `C:\Users\AMPW\AppData\Local\VulkanValidationLayers\runs\runtime-validation-20261016-1\vvl-messages.log`

### Baseline comparison (2026-10-16)

A Core-only baseline instance `KSARS/obj/runtime-validation-baseline-20261016-1` (same
manifest schema, no mods enabled) ran under a lead-owned shell with identical validation
settings — only `log_filename` differed — and its `CURRENT-VALIDATION-ENABLED` marker listed
the same checks. Baseline stdout recorded a resize to 1920x1200 at 17:41:20 and a normal
shutdown at 17:41:55 with pipeline cache saved at 17:41:55, exit code 0.

Baseline `vvl-messages.log` contains only `Validation Performance Warning`
`WARNING-Shader-OutputNotConsumed` — the same category seen in the modded run — and **no**
`Validation Error` headers: neither `UNASSIGNED-non-acquired-swapchain-image-used` nor
`VUID-vkCmdDraw-imageLayout-00344` appears in the baseline. The two errors are therefore
observed only in the modded run in this comparison; that is correlation evidence, **not**
proof of a KSARS root cause — the workloads differ (beta/recapture exercises are impossible
without the mod) and a single baseline cannot cover all stock code paths. Root-cause
tracing is the next step; the Vulkan gate is **not** passed.

Baseline log: `C:\Users\AMPW\AppData\Local\VulkanValidationLayers\runs\runtime-validation-baseline-20261016-1\vvl-messages.log`

### Fix checkpoint: descriptor layout alignment (2026-10-16)

`SolarGlowFlarePass.Prepare` now declares the `mainColorInfo` combined sampler
(graphicsSet2 binding 2) as `ShaderReadOnlyOptimal`, matching the `SampledReadVfc` barrier
transition applied to `_mainColor` in `Record` — the installed preset's layout was verified
as `ShaderReadOnlyOptimal` via an installed-binary cctor pin. Storage-image descriptors
(blur, scene) remain `General`. Focused `SolarGlowFlareTests` passed 24/24 with
`VerifyRendererBaseline=true` (0 skipped).

This is compile/test-level evidence only — no runtime run has executed the fix, so
`VUID-vkCmdDraw-imageLayout-00344` is **not** claimed resolved. The second error,
`UNASSIGNED-non-acquired-swapchain-image-used`, remains unresolved: the native rebuild path
and custom resource disposal were inspected without a proved root cause, and no speculative
patch was applied. A corrected build is staged — unlaunched — at
`KSARS/obj/runtime-validation-20261016-2` with matching per-run settings at
`C:\Users\AMPW\AppData\Local\VulkanValidationLayers\runs\runtime-validation-20261016-2\vk_layer_settings.txt`.
The Vulkan gate is still **not** passed.

### runtime-validation-20261016-2 result (2026-10-16)

The "staged — unlaunched" note above is historical; the `-2` instance has since run under a
lead-owned shell (exit code 0) with identical validation settings. It shut down normally at
17:53:57 with visual-owner uninstallation and pipeline cache save logged.

- Activation confirmed: `vvl-messages.log` contains `CURRENT-VALIDATION-ENABLED` listing the
  same checks (Core Checks, Synchronization, Stateless Parameter, Object lifetime, Thread
  Safety, Handle Wrapping).
- Stdout: first main Luna composite submission at frame 1, syncSlot 1, resourceFrame 0,
  rendererEpoch 1, eight viewport slots; generation 2 recapture recorded Sol
  raw/coverage/depth at frame 6054 then HaleBoppComet/HalleysComet.
- Unique VVL categories (headers, not counts): only `Validation Performance Warning`
  `WARNING-Shader-OutputNotConsumed` — the same category the Core-only baseline produced.
  **No** `Validation Error` headers: neither `VUID-vkCmdDraw-imageLayout-00344` nor
  `UNASSIGNED-non-acquired-swapchain-image-used` recurred.
- Caveats: no post-startup gameplay resize or swapchain rebuild was logged, so the clean
  result covers the exercised workload only and does **not** close the swapchain-resize
  concern; and non-recurrence alone does not prove causal linkage to the layout fix.

The gate is still **not** fully passed: a targeted validation-enabled resize/rebuild run is
the next check, and the previously noted quantitative/profiling work remains pending.
Run log: `C:\Users\AMPW\AppData\Local\VulkanValidationLayers\runs\runtime-validation-20261016-2\vvl-messages.log`

### Targeted resize run — runtime-validation-20261016-2-resize-1 (2026-10-16)

The same corrected `runtime-validation-20261016-2` instance was relaunched under a
lead-owned shell (exit code 0) with a fresh settings/log directory
(`runtime-validation-20261016-2-resize-1`) so this run's evidence stands alone.

- Activation confirmed: `CURRENT-VALIDATION-ENABLED` listed the same checks (Core Checks,
  Synchronization, Stateless Parameter, Object lifetime, Thread Safety, Handle Wrapping).
- Stdout: first main Luna composite submission at frame 1, syncSlot 0, resourceFrame 0,
  rendererEpoch 1, eight viewport slots. A gameplay FullScreenExclusive resize to 1920x1200
  at 18:02:06 produced verified-idle retirement at rendererEpoch 2 with
  mainResume/compositeResume true; main composition resumed at frame 413, syncSlot 1,
  resourceFrame 1, `bodies=[]` (no post-resize body composition is implied).
- Unique VVL categories (headers, not counts): only `Validation Performance Warning`
  `WARNING-Shader-OutputNotConsumed` — the same category the Core-only baseline produced.
  **No** `Validation Error` headers: neither `VUID-vkCmdDraw-imageLayout-00344` nor
  `UNASSIGNED-non-acquired-swapchain-image-used` recurred.
- Normal shutdown at 18:02:31 logged visual-owner uninstallation; pipeline cache saved at
  18:02:32; shell exit 0. User reported the workload works as expected.

**Conclusion:** validation-enabled resize/rebuild and orderly teardown are now exercised
without validation errors for the tested workload. The earlier resize concern is
checked/non-reproduced — this does not prove the original root cause or universal
correctness, and no repeat resize run is needed absent new changes. This advances the GPU
API/lifecycle evidence only; the prototype gate is **not** fully passed — quantitative
mapping, depth-order, and profiling work remain pending, and no phase-5 completion is
claimed.

Run log: `C:\Users\AMPW\AppData\Local\VulkanValidationLayers\runs\runtime-validation-20261016-2-resize-1\vvl-messages.log`

### User decision: profiling and frame inspection deferred (2026-10-16)

The user has decided that neither performance profiling nor RenderDoc frame inspection is
needed now — the rendering looks good and any artifacts can be fixed later. Performance
profiling is deferred until the feature is complete (low priority), and RenderDoc/frame
inspection is reserved for when artifacts or specific bugs warrant it; neither blocks
continued implementation. This supersedes earlier wording that kept further work behind
pending profiling or frame inspection.

This acceptance applies to the tested views only. All recorded CPU-test and VVL evidence
above stands, and the documented limitations remain: unverified mathematical extremes and
calibration are still not proved, and no claim of full mathematical or GPU correctness is
made. The next implementation work is the spectral foundation — a prerequisite for the
phase-5 stars/Milky Way/CMB scope.

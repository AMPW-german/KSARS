# Copilot Instructions

If there are ambiguities, ASK. Don't assume, and don't hide confusion. Surface tradeoffs. If multiple interpretations exist, present them - don't pick silently. If a simpler approach exists, say so. Push back when warranted. If something is unclear, stop. Name what's confusing. Ask.

If changes break legacy compatibility and it's not explicitly stated how it should be handled, ASK. Don't assume.

Never use emojis, slang, or informal language in code comments or documentation. Use clear, professional language.
Never add yourself to the list of authors in code comments or documentation.

Think about the asked changes and determine, if they are a good solution/change or if there's a better approach. If you think there is a better approach, ASK. Don't assume.

When writing a plan:
- Never use emojis, slang, or informal language.
- Write a checkmark list with narrowly defined steps.
- Use markdown checkboxes for each step. DO NOT ADD EMOJIS TO SHOW THEM AS DONE, e.g. "- [x] Step 1"
- Don't add a progress bar or percentage completion to the plan. Use the checkboxes to indicate completed steps.

Think critically about the requested changes and determine if they are a good solution/change or if there's a better approach. If you think there is a better approach, explain and ASK. Don't assume.
Avoid duplicating existing code. If you think a new function or class is needed, check if it already exists. If it does, use it instead of creating a new one.

---
name: karpathy-guidelines
description: Behavioral guidelines to reduce common LLM coding mistakes. Use when writing, reviewing, or refactoring code to avoid overcomplication, make surgical changes, surface assumptions, and define verifiable success criteria.
license: MIT
---

# Karpathy Guidelines

Behavioral guidelines to reduce common LLM coding mistakes, derived from [Andrej Karpathy's observations](https://x.com/karpathy/status/2015883857489522876) on LLM coding pitfalls.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

## Safe build and test commands

Use these for local verification. Staging the compiled probe into a fresh isolated runtime instance directory (refuses an existing directory and any path outside `KSARS/obj`; packaging and copy targets stay disabled):

```powershell
dotnet build tests/KSARS.Visuals.Tests/KSARS.Visuals.Tests.csproj -t:StageRuntimeProbe -p:SkipModPackaging=true -p:Configuration=Debug -p:RuntimeInstanceDir="C:/Users/AMPW/source/repos/KSARS/KSARS/obj/<fresh-instance-name>" -p:KSADir="C:/Program Files/Kitten Space Agency/" -p:KSAModsDir="C:/Users/AMPW/Documents/My Games/Kitten Space Agency/mods/"
```

A successful stage only writes a disposable test instance; it does not imply any GPU or renderer acceptance.

Launching the game, deploying, or replacing a release directory requires separate explicit approval. Pass `-p:SkipModPackaging=true` so the copy/package targets are skipped.

```powershell
dotnet build KSARS/KSARS.csproj -c Debug -p:SkipModPackaging=true -p:KSADir="C:/Program Files/Kitten Space Agency/" -p:KSAModsDir="C:/Users/AMPW/Documents/My Games/Kitten Space Agency/mods/"
dotnet build KSARS/KSARS.csproj -c Release -p:SkipModPackaging=true -p:KSADir="C:/Program Files/Kitten Space Agency/" -p:KSAModsDir="C:/Users/AMPW/Documents/My Games/Kitten Space Agency/mods/"
dotnet test tests/KSARS.Visuals.Tests/KSARS.Visuals.Tests.csproj -p:SkipModPackaging=true -p:KSADir="C:/Program Files/Kitten Space Agency/" -p:KSAModsDir="C:/Users/AMPW/Documents/My Games/Kitten Space Agency/mods/"
```

Installed-binary baseline checks are opt-in and additionally require `-p:VerifyRendererBaseline=true` with an explicit `-p:KSADir` pointing at the game install. If the flag is set without a path the build fails; without the flag those tests report as skipped.

```powershell
dotnet test tests/KSARS.Visuals.Tests/KSARS.Visuals.Tests.csproj -p:SkipModPackaging=true -p:VerifyRendererBaseline=true -p:KSADir="C:/Program Files/Kitten Space Agency/" -p:KSAModsDir="C:/Users/AMPW/Documents/My Games/Kitten Space Agency/mods/"
```

Note: matching binary metadata or IL structure is not GPU validation. Baseline tests prove the installed build is the expected one and that call sites exist; they do not prove any renderer hook, capture path, or visual feature works at runtime. Several contract tests inspect the built mod assembly with Mono.Cecil and differ between configurations: diagnostics are `#if DEBUG`-gated in production, so debug-only tests are `#if DEBUG`-gated as well. Always run both `-c Debug` and `-c Release` test passes.

## Current state

The relativistic-visuals architecture is delivered and runtime-verified on the `blueshift` branch:

- All render paths are enabled by default; `KSARS_*` environment variables are opt-out debug switches (`=0` disables).
- Media/body placement aberrates via the shared `Camera.GetPositionEgo` seam plus transpiled planet-relative positions; co-moving bodies keep bitwise rest geometry; shading stays rest-frame except the active vessel's owned apparent lighting/shadow copies; eclipse verdicts are rest-frame; Sunbloom binds the apparent-sun copy.
- `AtlasMainPass` composites up to two resolved distant bodies plus the sun into the main view per frame: inverse-aberrated per-pixel sampling of fresh native source color/depth, radial-depth ordering, depth-test-only foreground occlusion, premultiplied corona, D^3 brightness plus a hue-only 5800 K Doppler chroma shift.
- Stars aberrate and Doppler-shift through the fitted thermal model; the Milky Way band is inverse-aberrated and shifted as a ~5000 K extended source.
- `RelativisticOptics` provides stable-q aberration/Doppler math; `ThermalSpectrumTable` provides the fitted spectrum table shared by all owned radiance paths.
- Verified: ordering, foreground occlusion, solar replacement, resize/rebuild, Vulkan validation (sync checks) — no mod-attributable findings. Known limits are documented in README.md.

### Runtime evidence conventions

- Game instances are staged under `KSARS/obj/` via `StageRuntimeProbe` and launched through the installed StarMap launcher with process-local `STARMAP_INSTANCE_PATH` and the `KSARS_*` flags. Isolated-launch approval covers repository-local `obj` instances only.
- Vulkan validation: `VULKAN_VALIDATION.md` documents the layer-settings recipe; runs go under `%LOCALAP%/VulkanValidationLayers/runs/`. Attribute every finding (mod vs. stock) before claiming a clean run.
- Specialized tools deny reads of ignored `obj` evidence; preserve actual command stdout rather than bypassing with shell file reads.

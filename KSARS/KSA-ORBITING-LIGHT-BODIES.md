# Orbiting Self-Luminous Bodies in KSA

## Scope and conclusion

This report evaluates whether Kitten Space Agency can support an additional celestial body that:

- follows a normal orbit;
- looks self-luminous rather than reflective; and
- actually illuminates planets, terrain, atmospheres, rings, vehicles, and other rendered objects.

The analysis covers the installed `KSA.dll` version `2026.8.22.5348`, product commit `d1134693eb4bbd6de18248b992c63ecc1ca85f46`. It does not rely on conclusions drawn from the older `2026.8.5.5168` binary referenced by `KSA-VEHICLE-UPDATES.md`.

**Conclusion: it is technically possible, but stock KSA does not expose it as a body option.** KSA already computes the primary sun position in camera-relative coordinates every frame, so the global light can move. The obstacle is that the source is hard-wired to one stationary `StellarBody`, while orbital behavior is implemented by a separate `Celestial` hierarchy.

There are three materially different outcomes:

1. **An emissive orbital body with a local point light** is feasible with moderate effort, but it will not behave like a true sun in all render paths.
2. **An orbital body replacing the one global sun** is feasible and can provide complete stock-style illumination, but only one body can be the effective global sun at a time.
3. **An additional global sun alongside Sol** requires substantial renderer and shader work. It is possible in principle, but it is not a normal Harmony/XML-only mod and is highly sensitive to game updates.

For the requested combination of appearance and real illumination, the recommended first implementation is to redirect the existing single global-sun pipeline to a specially configured normal orbital body. If Sol must continue illuminating the scene simultaneously, the requirement becomes a multi-star renderer project rather than a new body type.

## Evidence classification

Unless stated otherwise, findings below are confirmed by static decompilation of the installed managed assemblies and inspection of installed Core XML. No runtime instrumentation or visual proof of concept was performed. Runtime behavior inferred from update methods is identified as an inference rather than an observation.

## Installed binaries and content

Relevant installed components include:

| Component | Version | Role |
| --- | --- | --- |
| `KSA.dll` | `2026.8.22.5348` | Celestial hierarchy, simulation, global render orchestration, lighting data, sun rendering |
| `Planet.Core.dll` | `1.0.0+d1134693...` | Planet data support |
| `Planet.Render.Core.dll` | `1.0.0+d1134693...` | Planet rendering support |
| `Brutal.Vulkan.dll` | `2026.6.0` | Vulkan render infrastructure |
| `Brutal.ShaderC.dll` | `2026.7.0` | Shader compilation infrastructure |
| `Brutal.Core.Numerics.dll` | `2026.6.0.0` | Float- and double-precision coordinate types |

Core content defines the sun and orbiting bodies separately in `Content/Core/Astronomicals.xml`:

- `Sol` is a `<StellarBody>` with `MeanRadius`, `Mass`, `Color`, and `Sunlight`.
- Mercury and other orbiters use `<PlanetaryBody>`, `<AtmosphericBody>`, or related elements with an `<Orbit>` definition and a `Parent`.

No installed XML declaration combines `StellarBody` and `Orbit`.

## Celestial type model

### XML and template creation

`SystemTemplate.Bodies` accepts these relevant XML types:

- `StellarBodyTemplate`
- `PlanetaryBodyTemplate`
- `TerrestrialBodyTemplate`
- `AtmosphericBodyTemplate`
- minor-body, asteroid, and comet templates

`AstronomicalTemplate.CreateInto(...)` is the factory boundary. `StellarBodyTemplate.CreateInto(...)` always creates a `StellarBody` and ignores the supplied parent. `PlanetaryBodyTemplate.CreateInto(...)` creates a `PlanetaryBody` with a required parent.

`CelestialTemplate`, the base for normal orbiting bodies, owns `OrbitTemplate`, `RotationTemplate`, mass, radius, and sphere-of-influence data. `StellarBodyTemplate` inherits directly from `AstronomicalTemplate`; it has mass and radius but no orbit or rotation template.

`SystemTemplate.OnDataLoad(...)` records the first `StellarBodyTemplate` as its `Parent`. `CelestialSystem.GetWorldSun()` similarly returns the first runtime `StellarBody`.

### Runtime hierarchy

The runtime split is structural:

- `StellarBody` derives from `Astronomical` and implements `IParentBody`.
- `Celestial` derives from `Astronomical` and implements both `IParentBody` and `IOrbiter`.
- `StaticCelestial`, `PlanetaryBody`, and the other normal body classes derive through `Celestial`.

`Celestial` owns an `Orbit`, requires a non-null parent, calculates rank and sphere of influence, and caches parent-relative and ecliptic position and velocity.

`StellarBody` has no `Orbit`. Its ecliptic position and velocity always return zero, its coordinate-frame transforms return identity, its rank is zero, and its sphere of influence is infinite.

`CelestialSystem.CreateTreeFrom(...)` requires every parented body to implement `IOrbiter`; otherwise it throws that the body cannot orbit another body. A stock `StellarBody` therefore cannot be placed under another body merely by adding `Parent` or `Orbit` XML.

### Concrete star assumptions

Several systems use `StellarBody` rather than a general light-source interface:

- `Astronomical.IsStar()` and `HasOrbit()` use concrete `StellarBody` checks.
- `Celestial.Class` and `IsMoon()` classify bodies according to whether their parent is a `StellarBody`.
- home-body fallback excludes `StellarBody`.
- orbit hover/UI paths exclude `StellarBody`.
- `CelestialSystem.GetWorldSun()` returns the first `StellarBody`.
- `Universe.WorldSun` is typed as `StellarBody`.
- solar panels and solar trackers request a `StellarBody` directly.

This means a new independent interface such as `ILightEmittingBody` would not be recognized without patching all affected consumers.

## Rendering and lighting model

### One global sun

`Universe.LoadSystem(...)` caches `CurrentSystem.GetWorldSun()` in `Universe.WorldSun`. `Universe.SunlightColor` reads `WorldSun.BodyTemplate.LightColorRgb`, which is populated by the `<Sunlight>` XML element.

`Program` creates one `UboLightingData` record per viewport. The record contains singular fields including:

- `SunPositionRadius`
- `SunColor`
- `SunRadius`
- `PlanetPosition`
- `PlanetColor`
- `OcclusionColor`

During `Program.UpdateShaderData(...)`, KSA calculates:

```text
camera.GetPositionEgo(Universe.WorldSun)
```

and writes it to `SunPositionRadius`. That camera-relative position is then used by the sun renderer, bloom/flare positioning, global shader bindings, terrain and cascaded shadows, ray tracing, atmosphere calculations, and other effects.

The important result is that **the global light position is updated every frame and is not intrinsically fixed at the coordinate origin**. It is stationary only because `StellarBody.GetPositionEcl()` always returns zero. Supplying the ecliptic position of an `IOrbiter` would produce a moving camera-relative light position without changing the coordinate system.

### Self-luminous appearance

KSA has a dedicated `SunRenderer` using `SunSurfaceGlb`, `SunVert`, and `SunFrag`. `Program` also owns dedicated sun-bloom and flare renderers. Their position is driven from the same world-sun path.

This renderer has assumptions that must be patched for a different source:

- `SunRenderer.MeshRadius`, `StartShowingDist`, and `OrbitCamDistPow` are static values initialized from `Universe.WorldSun.MeanRadius`.
- `SunRenderer.Draw()` measures distance to `Universe.WorldSun` directly.
- initial flare color comes from `Universe.WorldSun.GetColor()`.
- the main update uses four times the world-sun radius when updating the rendered sphere.

A normal `PlanetaryBody` remains reflective unless a custom emissive renderer is added or the existing sun renderer is redirected to it.

### Global shadows, atmosphere, rings, and distant rendering

The single global source reaches more than ordinary surface lighting:

- `SunShadowTechnique` receives the global sun position for terrain shadows.
- the cascaded shadow system uses the cached sun position.
- `UpdatePlanetShaderData(...)` uses the sun vector for atmospheric-body occlusion and transmittance.
- ring rendering obtains `camera.GetPositionEgo(Universe.WorldSun)`.
- distant celestial lighting and glints use `Universe.WorldSun`.
- solar panels and tracking modules use `GetWorldSun()` for direction, distance, and parent-body eclipse checks.

Changing only a visible sphere or adding a point light would leave these systems pointed at Sol.

### Local point and spot lights

KSA also contains a general clustered-light system. `Light.CreatePointLight(...)` and `CreateSpotLight(...)` accept a double-precision camera-relative position, finite float range, color, intensity, and shadow flags. Vehicle `LightModule` instances submit these lights each frame. The ray-tracing path reserves up to 16 lights and adds submitted point lights after the global sun and planet entries.

This infrastructure proves that moving local lights are supported. It is not equivalent to a second sun:

- point and spot lights have finite `float` range;
- they use local clustered or ray-traced lighting paths;
- they do not become `UboLightingData.SunPositionRadius`;
- atmosphere, rings, sun bloom/flare, global terrain/cascade shadows, solar gameplay, and some distant rendering continue to use `WorldSun`;
- behavior differs between raster, ray-traced, viewport, and graphics-setting paths.

## Modification strategies

### Strategy A: Orbit-capable `StellarBody` subtype

Create a class derived from `StellarBody` that also implements `IOrbiter`, with an associated template containing orbit and rotation data.

Advantages:

- existing concrete `StellarBody` checks continue to recognize it as a star;
- it can become `WorldSun` without changing that property’s type;
- stock global lighting consumers would obtain its overridden moving position;
- solar panels and tracking can continue to accept a `StellarBody`.

Required work:

- register a new XML/template type with the system serializer;
- provide orbit, rotation, parent, state-vector, coordinate-frame, UI, targeting, and per-frame cache behavior;
- override the stationary `StellarBody` position, velocity, and frame methods;
- ensure `CreateTreeFrom(...)` sees it as an `IOrbiter`;
- patch static sun-render sizing if its radius differs from Sol;
- verify save/load and hierarchy registration.

Risks:

- much of `Celestial` behavior cannot be inherited because C# has single inheritance;
- reproducing or composing the large orbital/frame contract is error-prone;
- `SystemTemplate` uses explicit `XmlElement` type mappings, so a new subtype is not automatically accepted;
- serializer or factory hooks may require invasive patches before system deserialization;
- this still does not create two globally active suns because `GetWorldSun()` selects one.

Assessment: semantically clean but not the minimum-work option. Prefer it only if a reusable first-class stellar body type is required.

### Strategy B: Redirect the single global sun to a normal orbital body

Define the luminous object as an existing orbit-capable body type, identify it through mod configuration or a stable ID, and patch render/gameplay consumers to use it as the effective sun.

Advantages:

- reuses proven orbit, hierarchy, SOI, coordinate-frame, map, and save behavior;
- the global renderer already accepts a moving camera-relative sun position;
- preserves complete stock-style lighting if every world-sun consumer is redirected coherently;
- avoids recreating `Celestial` internals.

Required patch areas:

- `Program.UpdateShaderData(...)` for position, radius, color, sun mesh, and flare data;
- `SunRenderer.Draw()` and static radius-dependent thresholds;
- `Universe.SunlightColor` or the lighting-data initialization/update path;
- rings and any direct `Universe.WorldSun` reads;
- terrain/cascade shadow paths that bypass the UBO;
- solar panels, solar trackers, eclipses, and distant-body/glint calculations;
- menus and camera commands if the effective sun should be exposed as such.

Risks:

- `Universe.WorldSun` cannot simply hold a normal `Celestial` because it is typed as `StellarBody`;
- some patches need private fields or IL transpilers rather than stable public seams;
- missing one consumer creates visibly inconsistent lighting or gameplay;
- Sol remains the stock `StellarBody` unless separately hidden or visually demoted;
- only the selected orbital source provides global illumination.

Assessment: **recommended for a single effective moving sun**. It offers the best balance of correctness and effort, provided replacing Sol as the global source is acceptable.

### Strategy C: Emissive orbital body plus synthetic local light

Use a normal orbital body, add a custom emissive sphere/glow, and submit a point light at its camera-relative position.

Advantages:

- can coexist visually with Sol;
- avoids changing the body hierarchy;
- uses existing clustered-light and shadow infrastructure;
- is the smallest implementation.

Limitations:

- finite range makes astronomical-scale illumination unsuitable or numerically fragile;
- atmosphere, rings, sun bloom/flare, solar panels, and global sun shadows remain based on Sol;
- different rendering modes may produce different results;
- it is a local luminous object, not a complete second star.

Assessment: useful as a visual/gameplay approximation, not as the requested fully illuminating celestial body.

### Strategy D: Extend the renderer for multiple global suns

Replace singular sun fields with an array or structured light list used by every global render path.

Required work includes:

- changing global UBO structures and descriptor layouts;
- modifying all relevant raster and ray-tracing shaders;
- defining how multiple lights combine in atmosphere, ocean, clouds, rings, terrain, vehicles, and distant rendering;
- adding multiple global shadow sets or choosing which star casts shadows;
- extending eclipse and solar-power gameplay;
- adding an emissive renderer and bloom/flare instances per star;
- handling Vulkan pipeline and shader resource rebuilds;
- deciding light count and performance budgets.

Assessment: possible only as a substantial renderer modification. Harmony alone cannot safely change shader struct layouts and every native/GPU consumer. This is the only route to two or more fully correct simultaneous global stars.

## Runtime and gameplay risks

### Essential blockers

1. **Single global source:** `UboLightingData` and many direct consumers model one sun.
2. **Hard-typed provider:** `WorldSun`, solar systems, and several helpers require `StellarBody`.
3. **Template registration:** stock XML has no orbit-capable stellar type.
4. **Patch coherence:** render position, visible source, shadows, atmosphere, rings, and gameplay must all select the same body.
5. **Shader compatibility:** additive global lights require synchronized CPU structs, descriptor layouts, and shaders.

### Important integration risks

- **Hierarchy and gravity:** a luminous normal `Celestial` can have mass, SOI, children, and normal gravity. A custom stellar subtype must reproduce those semantics.
- **Reference frames:** normal `Celestial` already caches double-precision ecliptic state and converts to camera-relative coordinates. This makes a moving source viable and avoids using large absolute positions directly in shaders.
- **Eclipses:** solar panels and distant-body visibility raycast from `GetWorldSun()` and only test particular parent-body cases. These need a shared light-source abstraction to remain consistent.
- **UI classification:** concrete star checks affect planet/moon labels, orbit visibility, camera menus, and celestial information.
- **Static render constants:** `SunRenderer` derives static thresholds from the initial world sun and will not automatically adapt to another radius.
- **Multiple viewports:** lighting data is per viewport, while some ray-tracing code reads viewport index zero and some sun state is cached for the main viewport.
- **Render modes:** clustered raster lights and ray-traced lights have separate submission paths.
- **Resource lifecycle:** a custom emissive renderer must rebuild frame resources and dispose Vulkan resources with the game renderer.
- **Save compatibility:** using a stock `Celestial` minimizes save risk. A custom runtime/template type requires explicit verification across save/load and mod removal.
- **Updates:** private methods, fields, shader layouts, and exact IL sequences are not a supported API and can change without compatibility guarantees.

### Optional polish

These do not block a first correct single-source implementation:

- unique star icons and map labels;
- configurable flare texture and bloom profile;
- separate spectral color and luminosity models;
- physically accurate inverse-square irradiance;
- binary-star barycentric mechanics;
- multiple shadow-casting stars;
- editor UI for authoring luminous bodies.

## Effort estimate

Assumptions:

- one developer familiar with C#, Harmony, KSA mod loading, Vulkan concepts, and shader debugging;
- estimates include implementation and focused testing but not external release management;
- one developer-day is approximately 6 to 8 focused engineering hours;
- no source code or official renderer extension API is available;
- estimates are for KSA `2026.8.22.5348` only.

| Outcome | Engineering | Testing and stabilization | Total | Confidence |
| --- | ---: | ---: | ---: | --- |
| Emissive orbital body plus local point light | 2-4 days | 2-3 days | **4-7 days** | Moderate |
| Replace the one global sun with an orbital body | 5-8 days | 3-5 days | **8-13 days** | Moderate |
| Reusable orbit-capable `StellarBody` and template | 10-15 days | 5-10 days | **15-25 days** | Low to moderate |
| Additional fully global star while retaining Sol | 20-35 days | 10-15 days | **30-50 days** | Low to moderate |

The 30-50 day estimate is not just for adding another shader light. It includes keeping atmosphere, rings, shadows, ray tracing, bloom, visibility, solar gameplay, multiple viewports, and graphics settings coherent.

Expected maintenance after a KSA update:

| Approach | Typical compatible update | Renderer/type-system change |
| --- | ---: | ---: |
| Local-light workaround | 0.5-2 days | 2-5 days |
| Redirected global sun | 1-3 days | 3-8 days |
| Custom stellar subtype | 1-4 days | 5-10 days |
| Multi-global-light renderer | 3-8 days | 10+ days or partial rewrite |

These maintenance estimates are directional. Early-access or renderer-heavy updates can invalidate them.

## Update-resilience requirements

Any future implementation should:

1. require an exact supported `KSA.dll` file or assembly version;
2. validate every target type, method signature, field, and expected patch count at startup;
3. validate shader/UBO layouts before enabling renderer changes;
4. centralize effective-light selection so rendering and gameplay cannot silently choose different bodies;
5. log the selected body and enabled lighting features clearly;
6. disable the complete feature if a required hook fails, rather than leaving partial lighting active;
7. keep optional UI patches separate from required simulation/render patches;
8. include a compatibility table for every tested KSA build.

Transpilers against `Program.UpdateShaderData(...)` and direct access to private renderer fields are the most fragile likely hooks. Patches against public virtual position methods are less fragile but do not solve source selection by themselves.

## Known unknowns

The following require a runtime proof of concept before committing to an implementation schedule:

- whether all shipped shader sources can be replaced by a mod without repackaging protected game assets;
- whether a custom system-template subtype can be registered early enough through the current mod loader;
- whether extreme clustered-light ranges remain stable and performant;
- exact shadow-map budgets and visible artifacts for an astronomical point light;
- behavior of every graphics preset, especially ray tracing and non-main viewports;
- save/load behavior for a custom `Astronomical` subtype;
- whether future KSA builds introduce an official multi-star or custom-light API.

These unknowns are why the custom subtype and multi-global-light estimates have lower confidence.

## Recommended path

If one moving body may replace Sol as the effective light source:

1. define it as a stock orbit-capable celestial body;
2. identify it through explicit mod configuration rather than a naming convention;
3. create one shared effective-sun service returning position, radius, visual color, and light color;
4. redirect all global render and solar-gameplay consumers to that service;
5. reuse the existing sun surface/bloom/flare pipeline at the orbital position;
6. retain a strict version guard and fail closed when a hook is missing.

This should take approximately **8-13 developer-days** for the analyzed build.

If Sol must remain active and the new body must also illuminate the entire system with equivalent correctness, plan for a renderer project of approximately **30-50 developer-days**, followed by ongoing update maintenance. The simpler 4-7 day local-light approach should only be selected if its atmosphere, ring, distance, and gameplay limitations are acceptable.

## Acceptance criteria for a future implementation

A single-effective-sun implementation is complete only when all of the following are verified:

- the configured luminous body follows its declared orbit at normal speed and time warp;
- its rendered emissive sphere, bloom, and flare remain centered on its orbital position in every viewport;
- nearby terrain, vehicles, oceans, clouds, atmospheres, and rings use the same light direction and color;
- terrain and vehicle shadows move consistently with the body;
- raster and ray-traced modes agree on source position and color;
- eclipses occur when the configured source is geometrically occluded;
- solar panels and trackers point toward and calculate distance from the configured source;
- map and close camera modes do not show a second stale sun at Sol;
- save/load and system reload preserve the body and its orbit;
- changing to an unsupported KSA binary disables the feature with an actionable error;
- no required Harmony patch reports zero or unexpected matches.

A true multi-star implementation additionally requires:

- Sol and the new body illuminate the same scene simultaneously;
- each source has independent position, radius, color, and intensity;
- atmosphere, rings, and distant rendering combine both sources deterministically;
- the shadow policy for multiple stars is documented and tested;
- solar gameplay defines whether irradiance is summed and verifies the result;
- performance remains within an agreed frame-time budget for every supported graphics mode.

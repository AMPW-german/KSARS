# KSARS

KSA Relativistic Speeds is a small mod for Kitten Space Agency that adds acceleration decrease at relativistic speeds.\
It always uses an external observer as the reference frame so the acceleration read in game decreases as the ship approaches the speed of light.

IRL the acceleration stays constant in the ship's frame of reference as the time dilation also increases.\
KSARS does NOT simulate time dilation or adjust the acceleration readout in the ship's frame of reference — but it does render relativistic visual effects (see below).

This is just a small mod to make the game more realistic and ~~annoying~~ fun for those who want to explore relativistic speeds.

To help get to relativistic speeds, this mod increases the efficiency of Hydrolox by yes and adjust the "LR91 Sea" engine to have 2.47 GN of thrust for much higher acceleration.\
This may break the game and cause uncontrollable spin at higher timewarp (> 30x) so use at your own risk.\
The `Vehicle.DetectStructuralFailure` method is disabled as it would cause vessel destruction at <= 50G which is really low for relativistic speeds.

## Internal Mechanics

It patches the `PhysicsStates.IntegrateVelocityVerlet` method and reduces the acceleration based on the ship's speed relative to the speed of light for the parallel and perpendicular components of the acceleration vector.\
This is not a true relativistic simulation but a simple approximation that is good enough for the game.

Parallel acceleration is reduced by a factor of gamma more than the perpendicular acceleration.

## Visual effects

The mod renders relativistic observer effects whenever the installed KSA build matches the verified baseline (binary identity and renderer member signatures are checked at startup; on a mismatch the visuals stay disabled and physics still works). Everything below is enabled by default — the `KSARS_*` environment variables remain only as opt-out debug switches (`=0` disables a path). All effects are exact at rest: at zero observer speed every path reproduces the stock image.

**Aberrated positions for bodies and media.** Every native planet presentation — planet mesh, terrain, atmosphere, clouds, oceans, rings, distant-sphere bodies and orbit lines — is moved to the apparent aberrated position by rewriting the shared `Camera.GetPositionEgo` seam, plus five transpiled planet-relative camera positions in the ocean/atmosphere/cloud compute paths. Each body aberrates by its own relative velocity, so a co-moving orbited or landed planet keeps bitwise rest geometry while a relativistic flyby displaces the whole body-and-media stack as one rigid apparent image — this is what keeps an aberrated Earth attached to its atmosphere instead of separating into two planets. The sun aberrates like every other body, so the native Sun mesh and flare stay consistent with the aberrated sky. Map, editor, orthographic and secondary viewports, followed anchors, same-bubble vehicles and capture windows keep native positions; install and runtime faults fail closed to native rendering.

**Terrell rotation**, reproduced to leading order for free: the visible hemisphere is whichever face points along the aberrated view line, so a body's surface appears rotated by the aberration angle — at high boost a transverse planet shows the hemisphere facing the apex. Since a sphere's true relativistic image is itself still a sphere (Penrose–Terrell), the rigid position transform is the correct visual for spherical bodies. Limitation: disc shape and angular size are not warped, so an extended body near the anti-apex renders as a normal sphere rather than the physically correct rear-cap ring.

**Rest-frame shading, apparent-frame rendering.** Bodies are co-moving with the sun, so their lit sides, atmosphere illumination and terrain shadow cascades are evaluated against the rest-frame sun direction — photons arrive from the rest direction even while the body appears elsewhere. Only the active vessel's owned lighting and shadow copies carry the apparent direction, with rest-frame eclipse verdicts (the sun does not light the vessel through an occluding planet). The sunbloom post-process binds the same apparent-sun copy, so its disc ray test, occlusion mask and glare ghosts stay glued to the apparent sun.

**Atlas-composited distant bodies and sun.** A fullscreen composite pass records fresh native source color and depth each frame for at most two resolved distant bodies plus the world sun. Every pixel is unprojected, inverse-aberrated to its rest direction, sampled against each source tile and depth-ordered by reconstructed radial distance, with solar corona premultiplied over the opaque disc. The pass depth-tests against the main buffer without writing, so the vessel and native foreground correctly occlude composited bodies, and only successfully presented bodies' native sphere draws are suppressed that frame. Source buffers, descriptors and targets are isolated per acquired synchronization slot, identified by the actual fence and command-buffer handles; target-format or synchronization mismatches fail closed, and resources are retired only after the verified device-idle rebuild boundary.

**Relativistic stars.** Catalog star directions aberrate under observer motion, colors Doppler-shift through a fitted thermal spectrum model, and point-source flux scales with D^2 (searchlight brightening ahead, fading behind). At rest the stock sprite is reproduced — sharp core, halo wing, magnitude-dependent size, edge taper — energy-normalized to the modeled native sprite integral.

**Relativistic Milky Way.** The installed `MilkyWay.frag` is adapted at two seams: each pixel's reconstructed view direction is inverse-aberrated to its rest direction before the cubemap/cloud lookup, so the band visibly swings toward the velocity axis, and the band's composited radiance is Doppler-shifted as an effective-temperature (~5000 K) extended source.

**Spectral treatment.** Composited radiance scales by D^3 (clamped to finite HDR range 0–65504) and takes a hue-only Doppler chroma shift through the 5800 K solar-spectrum thermal table — the same fitted model the Milky Way band uses. The shift covers all owned radiance paths: catalog stars, the Milky Way band, the solar disc/corona/glare, atlas-composited bodies and the vessel's lighting copies. Physically motivated, but not a calibrated spectral model — no radiance calibration claim.

### Verification status

Runtime-verified in LEO and Jupiter orbit up to beta 0.98: composited bodies appear at apparent positions with correct radial ordering, vessel/foreground occlusion holds through the depth test, atmosphere layers correctly over composited discs, and window resize/renderer rebuilds retire and recreate cleanly. A Vulkan-validation run with synchronization checks produced no mod-attributable findings — only pre-existing stock-game issues (BC4 texture-streaming blit, shutdown swapchain transitions). See `VULKAN_VALIDATION.md` for the run log.

### Known limitations

- At most two resolved bodies plus the sun per composite frame; observed ≤6px bodies fall back to native sprites.
- Natively rendered surfaces — the co-moving nearby planet's mesh/terrain/atmosphere stack and tiny sprites — keep rest colors; the observer-side spectral shift is a per-pixel post-shading treatment that no data seam exposes without owning the native planet pipelines.
- External vessels, transparency and reflections stay native.
- Celestial UI and picking are not transformed; planet-planet eclipse occlusion keeps apparent occluder records.
- Body discs are rigidly translated, not angular-size-scaled or rear-cap warped.

## Disclaimer

This was just a small side project and it has a very low priority so it may not be updated for future versions of KSA.\
Please create an issue on GitHub for any issues or suggestions. Help requests are preferably on GitHub as well but the KSA forums is also fine.\
This mod is provided as-is and I am not responsible for any issues it may cause.\
This mod is not affiliated with Kitten Space Agency or its developers in any way.

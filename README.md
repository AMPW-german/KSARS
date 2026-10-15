# KSARS

KSA Relativistic Speeds is a small mod for Kitten Space Agency that adds acceleration decrease at relativistic speeds.\
It always uses an external observer as the reference frame so the acceleration read in game decreases as the ship approaches the speed of light.

IRL the acceleration stays constant in the ship's frame of reference as the time dilation also increases.\
KSARS does NOT simulate time dilation, visual effects or adjust the acceleration readout in the ship's frame of reference.

This is just a small mod to make the game more realistic and ~~annoying~~ fun for those who want to explore relativistic speeds.

To help get to relativistic speeds, this mod increases the efficiency of Hydrolox by yes and adjust the "LR91 Sea" engine to have 2.47 GN of thrust for much higher acceleration.\
This may break the game and cause uncontrollable spin at higher timewarp (> 30x) so use at your own risk.\
The `Vehicle.DetectStructuralFailure` method is disabled as it would cause vessel destruction at <= 50G which is really low for relativistic speeds.

## Internal Mechanics

It patches the `PhysicsStates.IntegrateVelocityVerlet` method and reduces the acceleration based on the ship's speed relative to the speed of light for the parallel and perpendicular components of the acceleration vector.\
This is not a true relativistic simulation but a simple approximation that is good enough for the game.

Parallel acceleration is reduced by a factor of gamma more than the perpendicular acceleration.

## Visual effects

Production relativistic visual effects are not yet delivered; the opt-in modes below are prototype diagnostics. At startup the mod verifies that the installed KSA build matches one known baseline (binary identity and renderer member signatures) and reports the result in the in-game KSARS window. On a matched build it additionally installs a read-only startup probe that observes renderer allocation boundaries and logs what it finds; the probe changes no scene or renderer arguments, results, allocations, or GPU resources. CPU-side optics math (aberration, Doppler, spherical-cap transforms, and conservative tile projections) is implemented and tested; in the opt-in probe below it feeds source-candidate selection. None of it is GPU acceptance and no enabled physical composite exists. Any visual feature stays disabled until an object-atlas/foreground path is proven to work at runtime.

Setting `KSARS_ATLAS_PROBE=1` opts into Phase 1 diagnostics on a matched build: at most two frozen native distant-body source captures, an offscreen inverse-aberration preview, and observer-speed/debug controls. These previews do not change the main view. Perspective-ray reconstruction uses homogeneous near-plane directions, avoiding an unstable far-plane divide that can reverse or collapse the ray. The user confirmed direction alignment after accounting for the navball's parent-centered prograde reference; observer optics retains the approved ECL/star-relative velocity. Read-only diagnostics expose the predicted ECL velocity-axis screen position and its angle from the displayed navball marker. At zero velocity, both shaders now explicitly retain identity rays and D=1. Diagnostic tiles remain frozen until you click "Recapture sources at current observer". Requests are consumed at the render boundary, not from ImGui; there are three generations per run (at most two body tiles plus one solar tile per generation). Older resources are retained until verified-idle teardown, with a maximum of nine frozen source captures. The offscreen compositor has separate buffers/descriptors/targets per acquired sync slot and refreshes bindings even when recapture preserves the tile count. Coverage mode 2 rejects backward rays and invalid tile UVs; mode 6 shows coverage from native source depth. The offscreen composite now orders body samples by depth-reconstructed radial distance rather than an analytic enclosing sphere. These diagnostics still require GPU coverage/ordering validation.

Also setting `KSARS_ATLAS_MAIN_PROBE=1` opts into an incomplete main-pass atlas prototype. It records fresh native source color/depth each frame for at most two supported distant bodies, composites inside the native depth-loaded pass, and suppresses only successfully presented bodies' native sphere draws in that frame. Source buffers, descriptors, and targets are isolated per acquired synchronization slot, identified by the actual fence and command-buffer handles before writes. Loading/menu frames can advance the synchronization ring independently of the native resource-frame index; no fixed offset between them is assumed. Native vessel/cockpit matrices, sky, Sol, UI, and unsupported bodies remain stock. This prototype supports only bodies with native rest-frame diameters greater than six pixels, avoiding duplicate native sprites; production observed-footprint LOD is not implemented. Brightness is diagnostic `D^3`, clamped to finite HDR range 0–65504, not a spectral model. Unexpected target-format/sample changes or synchronization mismatches fail closed. A verified normal-return hook on the native renderer rebuild retires per-frame resources only after the game's final successful device-idle wait; they recreate lazily on the next acquired slot, preserving frozen captures and observer controls. Permanent recording/compatibility failures remain disabled. A separate owned solar adapter borrows the native Sun shaders, sphere mesh, and noise texture without using the shared Sun renderer or mesh buckets. It copies the source camera/global state into owned resources and records a frozen raw-color tile. Native mesh scale 4R and procedural radius 2.75R are shown explicitly against physical radius R; corona alpha is not treated as an opaque photosphere or a calibrated thermal spectrum. Solar capture is not yet composited into the main view, and native Sun draws remain unchanged. The first successful main body-composite command recording is logged once with body IDs and actual frame/slot identities; command recording is not GPU acceptance.

Both main-pass shaders compile through the game's bundled ShaderC, but GPU depth sampling, coverage, radial overlap ordering, foreground occlusion, resize/lifecycle, and the solar adapter remain acceptance gates. The full approved object-atlas/foreground gate has not passed. Spectra, calibrated CMB, all-direction stellar/Milky Way rendering, and celestial UI/picking remain pending; neither diagnostic mode is a reduced substitute for the approved feature.

The approved direction is a space-first object atlas: distant bodies as shaded objects plus the native foreground view, with no six-world scene captures. Secondary media (atmospheres, oceans, clouds), terrain, external vessels, transparency, and reflections are known limitations of that planned path and are not delivered.

## Disclaimer

This was just a small side project and it has a very low priority so it may not be updated for future versions of KSA.\
Please create an issue on GitHub for any issues or suggestions. Help requests are preferably on GitHub as well but the KSA forums is also fine.\
This mod is provided as-is and I am not responsible for any issues it may cause.\
This mod is not affiliated with Kitten Space Agency or its developers in any way.

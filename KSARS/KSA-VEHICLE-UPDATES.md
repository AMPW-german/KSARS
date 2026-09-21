# KSA Vehicle Update Pipeline

This document summarizes how Kitten Space Agency updates vehicle motion each simulation frame and distinguishes authoritative simulation state from derived per-frame data.

The findings are based on decompilation of `KSA.dll` version `2026.8.5.5168`. Internal implementation details may change between game versions.

## High-level update order

Vehicle updates are produced by worker tasks and later applied on the main update path. The relevant order is:

1. Vehicle solver tasks calculate the next simulation state.
2. `Universe.ApplyVehicleSolvers()` checks for completed solver tasks.
3. `VehicleUpdateTask.ApplyResultsToVehicles()` applies each completed result.
4. `Vehicle.UpdateFromTaskResults(...)` copies the result into the vehicle.
5. `Universe` publishes the completed simulation step as the last applied step.
6. `CelestialSystem.UpdatePerFrameData()` traverses the astronomical hierarchy.
7. `Vehicle.UpdatePerFrameData()` rebuilds coordinate-frame and presentation caches.

In simplified form:

```text
Vehicle solver
	-> VehicleUpdateTask.ApplyResultsToVehicles
		-> Vehicle.UpdateFromTaskResults
	-> publish last simulation step
	-> CelestialSystem.UpdatePerFrameData
		-> Vehicle.UpdatePerFrameData
```

This ordering is important because `UpdatePerFrameData` runs after the solver output has already become the vehicle's current state.

## Applying solver results

`VehicleUpdateTask.ApplyResultsToVehicles()` calls `Vehicle.UpdateFromTaskResults(...)` for every vehicle represented by the task. The update data can contain several related products, including:

- New kinematic state
- New vehicle properties
- New physics environment
- New kinematic measurements
- New analytic state vectors
- Updated orientation and angular velocity
- Flight-plan and module state changes

`Vehicle.UpdateFromTaskResults(...)` copies these products into the live `Vehicle`. In particular, the incoming kinematic state replaces the vehicle's current kinematic state.

This method is therefore a result-publication boundary, not the location where forces are calculated or motion is integrated.

### Consequence for velocity patches

Changing the complete result velocity at this boundary is not equivalent to changing acceleration. For example, multiplying the result velocity by a factor every frame scales all existing motion and produces artificial drag.

A result-boundary correction must, at minimum, distinguish the newly generated velocity change from the existing velocity:

```text
newtonian delta velocity = solver result velocity - previous velocity
corrected result velocity = previous velocity + transformed delta velocity
```

Even that correction can leave the simulation internally inconsistent if only the kinematic velocity is changed. The solver may already have produced matching position, analytic state vectors, measurements, flight-plan data, and transition decisions from the unmodified velocity.

## Authoritative and derived velocity representations

KSA maintains multiple representations of vehicle motion.

### Kinematic state

`KinematicStates` contains physics-frame values such as:

- `PositionPhys`
- `VelocityPhys`
- `Body2Phys`
- `AngularVelocityPhys`

These values are used by the local physics simulation.

`KinematicStates.ClampVelocities()` imposes a hard maximum linear speed of `299,792,458 m/s`. This prevents a physics-frame velocity from exceeding the speed of light, but it is only a magnitude clamp. It does not implement relativistic acceleration or the different response of acceleration parallel and perpendicular to the current velocity.

### Analytic state

Vehicles also have orbital or analytic state vectors. These contain position and velocity in the parent body's inertial coordinate frame and are used by orbital propagation and other systems.

The analytic and kinematic states are synchronized by solver code at specific points. Modifying one representation after the solver completes does not automatically guarantee that the other representation remains consistent.

### Kinematic measurements

`KinematicMeasurements.DeltaVelocityCci` records a measured velocity contribution in the parent-centered inertial frame. It is consumed by systems such as the flight computer.

Changing this measurement alone does not change vehicle motion. It describes part of what happened during integration; it is not the authoritative velocity accumulator.

## Physics integration

For unconstrained off-rails motion, `VehicleUpdateTask` eventually calls:

```text
PhysicsStates.IntegrateVelocityVerlet(...)
```

This method performs the velocity-Verlet integration. Its responsibilities include:

- Computing force and acceleration terms
- Applying thrust and propellant mass changes
- Updating orientation and angular velocity
- Updating `Kinematic.PositionPhys`
- Updating `Kinematic.VelocityPhys`
- Recording acceleration and delta-velocity measurements
- Clamping the resulting velocity

This is the main integration-level location for changing how acceleration produces velocity. Applying a transformation here allows position and velocity to be advanced as part of the same simulation operation.

However, this method is not the only motion path. Vehicles participating in contacts or constraints use the constraint simulation. A modification that affects only `IntegrateVelocityVerlet` will not automatically cover constrained vehicles.

## Per-frame data refresh

`CelestialSystem.UpdatePerFrameData()` traverses the loaded astronomical hierarchy. It starts at root parent bodies and recursively invokes `UpdatePerFrameData()` on their children.

For a vehicle, `Vehicle.UpdatePerFrameData()` derives cached values from the already-applied orbital state. Its core work includes rebuilding:

- Parent-centered position and velocity
- Ecliptic position and velocity
- Travel statistics
- Orbital event times
- Navball data and markers
- Vehicle-region information

The ecliptic caches are formed by combining the vehicle's parent-relative state with the parent's ecliptic state:

```text
vehicle position Ecl = parent position Ecl + vehicle position Cce
vehicle velocity Ecl = parent velocity Ecl + vehicle velocity Cce
```

This method does not calculate forces, acceleration, or delta velocity. It publishes derived values for the state that the solver has already applied.

### Why it is not an integration hook

Changing velocity in `UpdatePerFrameData()` means changing a cache after integration. Such a change does not inherently update:

- Physics-frame velocity
- Integrated position
- Analytic orbital state
- Solver measurements
- Constraint state
- Flight-plan calculations

A cache-only change can therefore affect what consumers observe without changing the actual simulated trajectory, or it can be overwritten during the next refresh.

## Selecting an interception point

The appropriate interception point depends on the desired effect.

### Presentation-only effect

A per-frame cache or rendering hook is appropriate when the simulation should remain unchanged and only displayed values need adjustment.

### End-of-step correction

`Vehicle.UpdateFromTaskResults(...)` can be used to inspect or alter completed solver output immediately before it is installed on the live vehicle. This is comparatively easy to intercept, but all related state products must be considered. Modifying only the final kinematic velocity can create disagreement with position and analytic state.

### Physical acceleration model

An integration-level hook is preferable when changing the relationship between force, acceleration, and velocity. The unconstrained and constrained simulation paths must both be considered if the behavior is intended to apply in every physical situation.

For a velocity-dependent model, the transformation should use a clearly selected inertial frame. Applying it directly to a local physics-frame velocity without accounting for the frame origin can make the result depend on bubble or reference-frame motion.

## Relativistic acceleration considerations

A relativistic acceleration implementation generally needs to:

1. Select the inertial frame in which velocity is evaluated.
2. Separate the proposed acceleration or delta velocity into components parallel and perpendicular to the current velocity.
3. Apply the appropriate Lorentz-factor scaling to each component.
4. Integrate the transformed delta velocity without allowing the result to reach or exceed light speed.
5. Keep position, kinematic state, analytic state, and measurements mutually consistent.
6. Handle both unconstrained and constrained physics paths.

The existing hard speed clamp is useful as a final numerical safeguard, but it should not be the primary relativistic model. A proper transformation should make acceleration approach zero in the relevant direction as speed approaches light speed, leaving the clamp to handle only floating-point or integration overshoot.

## ECL-based integration patch

The integration patch captures the vehicle state immediately before `PhysicsStates.IntegrateVelocityVerlet(...)` and converts it to the current parent's CCI frame with `PhysicsStates.GetStatesCci(...)`. It then constructs star-relative velocity without reading the previous frame's vehicle cache:

```text
vehicle velocity Ecl = parent velocity Ecl at the integration epoch
					 + vehicle velocity Cci transformed to Cce/Ecl axes
```

After the original velocity-Verlet step, the patch extracts the Newtonian CCI velocity change and transforms it into ECL axes. Relative to the initial ECL velocity, the change is split into parallel and perpendicular components and scaled by:

```text
parallel:      1 / gamma^3
perpendicular: 1 / gamma
```

The corrected ECL change is transformed back to CCI. The resulting CCI position and velocity are then written back to either a CCI or rotating CCF physics bubble. Position is reconciled with a trapezoidal half-step correction based on the difference between the Newtonian and relativistic velocity changes. Delta-velocity and linear-acceleration measurements are scaled using the same ECL velocity.

The final ECL velocity is constrained to the greatest representable `double` below `299,792,458 m/s`. KSA's existing local-physics velocity clamp remains in place as an additional safeguard.

This patch affects the unconstrained maneuvering path that calls `IntegrateVelocityVerlet`. Contact and constraint simulation is performed through 
and does not call this integrator, so constrained vehicles do not yet receive the same relativistic transformation.

## Summary

- Solver tasks generate authoritative vehicle updates.
- `Vehicle.UpdateFromTaskResults(...)` installs completed solver products.
- `Vehicle.UpdatePerFrameData()` runs afterward and rebuilds derived coordinate-frame and presentation caches.
- `UpdatePerFrameData()` is not an appropriate place to modify physical delta velocity.
- Scaling the complete result velocity at the application boundary creates artificial drag rather than modified acceleration.
- `PhysicsStates.IntegrateVelocityVerlet(...)` is the principal unconstrained integration path.
- Constrained motion uses a separate integration path and requires separate handling.
- Kinematic velocity, analytic state vectors, integrated position, and measurements must remain consistent when changing the acceleration model.

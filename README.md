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

## Disclaimer

This was just a small side project and it has a very low priority so it may not be updated for future versions of KSA.\
Please create an issue on GitHub for any issues or suggestions. Help requests are preferably on GitHub as well but the KSA forums is also fine.\
This mod is provided as-is and I am not responsible for any issues it may cause.\
This mod is not affiliated with Kitten Space Agency or its developers in any way.

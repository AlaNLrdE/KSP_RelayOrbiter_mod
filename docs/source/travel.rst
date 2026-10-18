Deploying at another body
=========================

The carrier can travel before it deploys: to a moon of the body it orbits, to another
planet around the same star, or to a moon of that planet. The deployment then runs as
usual around the destination.

Choosing the destination
------------------------

**Deploy around ◀ ▶**, under the satellite count, cycles through the reachable bodies:

.. list-table::
   :header-rows: 1
   :widths: 30 70

   * - Destination
     - Route
   * - The body orbited
     - No trip: deploy here.
   * - One of its moons (Mun, Minmus)
     - *Moon*: one direct transfer.
   * - A sibling (Duna, Eve...; Minmus from the Mun)
     - *Transfer*: another body around the same parent.
   * - A sibling's moon (Ike, Laythe...)
     - *Via* the sibling: transfer there, stop in a low orbit, then the moon transfer.

The target altitude options (synchronous, at a moon, midpoint, custom) are computed for
the destination. The Maneuvers tab only plans around the body orbited.

The trip
--------

The Automation tab shows the trip before the plan: the next window and the phase angle
it needs, the ejection burn, the delta-v reserved for course corrections, the capture
burn and the cruise time. They are all added to the carrier's delta-v check.

Steps, as Automation runs them (each one skipped when not needed):

1. **Into the moon's plane** (moons only). When the carrier's orbit is more than 0.2°
   off the moon's orbital plane (Minmus is inclined 6°), the plane change happens where
   the two planes cross.
2. **Ejection at the window.**

   - The Hohmann phase angle gives the window: about 110° for the Mun from low Kerbin
     orbit, 44° for Duna.
   - The burn is then searched against KSP's own prediction: the time (for a planet,
     the point in the parking orbit), the size and a little radial and normal
     delta-v.
   - The search keeps the trajectory whose encounter has the capture periapsis, in a
     prograde orbit and above the surface and atmosphere. Without an encounter yet, it
     follows the closest approach.

3. **Course corrections.**

   - One correction 10 minutes after the ejection, and, between planets, a second one
     at halfway.
   - Each is a precise burn searched the same way.
   - Each is skipped when the predicted periapsis is already within 3 % of the capture
     radius.

4. **Coast** to the destination's sphere of influence, with time warp.
5. **Capture**: a circularization at the periapsis, straight onto the target radius
   (or a low parking orbit at an intermediate stop).

Then the deployment's own steps adjust the altitude and the inclination if needed, and
release the satellites.

**From the launch pad**, MechJeb launches into a low orbit, with the moon's inclination
when the destination is a moon, and the trip starts once in orbit.

MechJeb as the planner
----------------------

With MechJeb 2 installed, **Transfer planner** offers *Relay Orbiter (recommended)* or
*MechJeb 2*.

- With MechJeb, its maneuver planner makes the ejection (*Hohmann transfer to target*
  for a moon, *Interplanetary transfer* at the phase angle for a planet) and the course
  corrections (*Fine tune closest approach*, to the capture altitude).
- Relay Orbiter still flies the nodes, coasts and captures.
- When MechJeb can't make a node, Relay Orbiter's own planner takes over for that step.

Limits
------

- The capture is propulsive: no aerobraking.
- Interplanetary windows come once per synodic period (about 2.1 Kerbin years for
  Duna): the window wait can be long, and it is all time warp.
- Searching the ejection asks KSP for hundreds of predictions in one frame: the game
  can freeze for a second or two.
- Trips back to the parent (from the Mun to Kerbin) or between moons of different
  planets (from the Mun to Ike) are not planned.

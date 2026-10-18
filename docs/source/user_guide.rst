User guide
==========

Open the window with the Relay Orbiter toolbar button in flight. It closes with the
**×** in its corner or the toolbar button, and hides with the rest of the interface
(**F2**). Clicks and keystrokes over the window don't reach the game.

The window always plans for the body the **active vessel orbits**, and follows it when the
vessel changes sphere of influence.

Header: satellites and target altitude
--------------------------------------

**Satellites [−] N [+]** sets the size of the constellation, from 2 to 12; the spacing
(360°/N) is shown next to it. It is locked while a deployment runs.

**Target altitude** has four sources:

.. list-table::
   :header-rows: 1
   :widths: 22 78

   * - Source
     - Altitude
   * - Synchronous
     - The orbit whose period equals the body's rotation (2,863.33 km at Kerbin). If it is
       below the surface or beyond the sphere of influence, it is rejected (the Mun's,
       for instance, is outside its SOI).
   * - At a moon
     - The orbital altitude of one of the body's moons (planets, around the Sun).
   * - Midpoint
     - Halfway between the orbits of two moons (or planets).
   * - Custom
     - Any altitude in km.

An altitude below the surface, inside the atmosphere or beyond the sphere of influence is
rejected with the reason.

Results tab
-----------

The plan for the chosen altitude and number of satellites, from a circular orbit:

- **Final orbit:** altitude, orbital speed and period of the satellites.
- **Parking orbit:** inner or outer (see :doc:`how_it_works`), its apoapsis, periapsis
  and period, and the carrier's and each satellite's burn. A periapsis underground or in
  the atmosphere, or an apoapsis outside the SOI, is shown in orange with the minimum
  usable target altitude.
- **Deployment procedure:** the steps to fly it by hand.

Maneuvers tab
-------------

For flying the deployment yourself, from the active vessel's real orbit:

- The **parking orbit burn** at the next release point (apoapsis for an inner parking
  orbit, periapsis for an outer one): time to the burn, delta-v and resulting orbit.
- **Create parking-orbit node** creates the node and, optionally, one **Alarm Clock**
  alarm per release, 60 s before it, stopping time warp.
- **Circularize at next apoapsis / periapsis** creates the node a released satellite needs.
- **Delete N existing node(s)**: the tab never removes your nodes on its own. Deleting
  the parking-orbit node also clears its release schedule and alarms.

Automation tab
--------------

Runs the whole deployment. From top to bottom:

**Satellites.** Every satellite on the vessel is listed: anything a decoupler or a
docking port would detach that has a probe core (see :doc:`vessel_design`). Click them in
release order; they are numbered #1, #2, ... If the vessel has fewer satellites than the
number chosen, it says so.

**Target orbit.** The target altitude from the header, marked *(already there)* when the
carrier is on it, and **Change inclination to [x]°** with the current inclination. When
needed, the transfer burn, the circularization at the target and the plane change are
listed with their delta-v.

**Plan.** The parking orbit (*INNER* or *OUTER*, and why), SAT-1 (released on the circular
orbit, no burn of its own), the carrier's parking burn and each satellite's
circularization burn with their durations, the total time, each satellite's fuel and the
**carrier delta-v needed**, compared with what the carrier alone has.

**Options.**

- **Top up the satellites' tanks from the carrier** (at start and before each release).
- **Extend the satellites' solar panels before release.**
- **Name them ... and mark them as relays.** The preview shows the first name.

**Start deployment** is disabled while the plan has a problem; the problems are listed
above it in orange. While the deployment runs, the tab shows the current step and its
status, with **Fast forward** (when waiting on time warp) and **Abort**.

.. important::

   While a deployment runs, **don't touch the controls, switch vessels or leave the
   flight scene.** The mod switches to each satellite and back by itself. Switching to a
   vessel that is not loaded, or leaving the scene, reloads it and stops the deployment.

Launching from the pad
~~~~~~~~~~~~~~~~~~~~~~

On the ground (pad, landed or splashed down) the Automation tab shows **Launch to orbit
(MechJeb)** instead of the plan:

- **Launch to:** a low circular orbit (30 km above the atmosphere, or 20 km on airless
  bodies; the target itself when it is lower), at the chosen inclination or the launch
  site's latitude.
- **Then:** the transfer and deployment that follow.
- **Continue with the deployment once in orbit:** when MechJeb has circularized,
  the deployment starts on its own, exactly as if you pressed Start.

**Launch with MechJeb** configures MechJeb's ascent (altitude, inclination, autostage) and
engages it. **Abort launch** disengages it. Watch the ascent as any MechJeb launch.

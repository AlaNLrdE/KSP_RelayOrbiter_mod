Automation reference
====================

The steps
---------

**Start deployment** (or the end of a MechJeb launch) builds this sequence. Every step is
written to ``KSP.log`` as ``[RelayOrbiter] Step k/n: ...``.

.. list-table::
   :header-rows: 1
   :widths: 30 70

   * - Step
     - What happens
   * - Plan / fly the transfer burn
     - Only when the carrier is not on the target orbit. Burn at periapsis or apoapsis
       (see :doc:`how_it_works`).
   * - Plan / fly the circularization at the target
     - Right after the transfer.
   * - Plan / fly the plane change
     - Only with *Change inclination*; before the transfer when lowering, after it when
       raising. Needs SAS maneuver mode.
   * - Warp to before the release point
     - Lead time: half the next burn, plus 60 s to point, plus time to orient, release
       and switch.
   * - Orient the carrier
     - Turn so the separation axis is perpendicular to the velocity (90° ± 3°, held 1 s,
       vessel not spinning). Extends the satellite's solar panels and tops up its tanks.
       After 60 s it releases anyway.
   * - Release SAT-k
     - Fires its decoupler or docking port, once the carrier is off rails.
   * - Let the vessels separate (SAT-1)
     - 5 s. SAT-1 is renamed.
   * - Plan / fly the parking-orbit burn
     - At the time fixed before SAT-1's release (see `Near-circular orbits`_).
   * - Turn the carrier away (SAT-2 onwards)
     - With RCS, the carrier first pushes itself away along the separation axis (up to
       3 s, or 0.5 m/s), checking after 1 s that it moves away and reversing otherwise.
       Then it turns 90° and waits (up to 45 s) for the satellite to be 10 m further away,
       so the satellite can turn for its burn without hitting it. The satellite is
       renamed.
   * - Switch to SAT-k
     - The satellite's engines are activated.
   * - Plan / fly SAT-k's circularization
     - At the release point (apoapsis or periapsis).
   * - Switch back to the carrier
     - Then the next satellite, one parking orbit later.

Flying a node
-------------

Every burn is flown by the same executor:

- It warps to 60 s before the burn's start (half its duration before the node), points
  with SAS (maneuver mode, or prograde/retrograde for the purely tangential burns) and
  starts on time.
- Full throttle, easing off over the last second of thrust; no thrust while pointing
  more than 5° off.
- The remaining delta-v is measured along the burn's initial direction, and for the last
  1.5 s of thrust SAS holds attitude instead of chasing the node, whose direction swings
  as it shrinks. It cuts off at 0.05 m/s, or on overshoot.
- **Staging:** when a carrier's engine runs dry, it stages away the spent stage (up to
  4 times per burn), never a stage that would release a satellite still on board.
- It gives up after 60 s without pointing, after 30 s without SAS (reporting the control
  level, electric charge, SAS level and CommNet signal), or when there is nothing left
  to stage.

Safety mechanisms
-----------------

.. list-table::
   :header-rows: 1
   :widths: 35 65

   * - Mechanism
     - Why
   * - Plan checks before Start
     - Satellite count and release order, engines and SAS on the satellites, an active
       carrier engine, a safe parking orbit, the carrier's own delta-v, a valid
       inclination, no existing nodes.
   * - Carrier stays loaded up to 22.5 km
     - KSP unloads orbiting vessels 2.5 km away; switching back to an unloaded carrier
       would reload the scene. The carrier's load range is raised during the deployment
       and restored at the end, on abort or on failure.
   * - Nothing separates on rails
     - While time warp winds down the carrier is still "packed"; separating parts then
       gives the new vessel a NaN orbit and KSP deletes it. Steps wait for physics.
   * - Warp is retried
     - If time warp drops out early, it is requested again every 3 s.
   * - Tanks topped up before each release
     - Docking ports cross-feed, so the carrier's burns drain the satellites still
       attached. Only the carrier's own tanks are used, the largest first.
   * - Abort, leaving the scene
     - Abort stops thrust and warp and restores the load range. Leaving the flight scene
       stops the deployment, but it is saved and can be resumed (see :doc:`user_guide`).

Near-circular orbits
--------------------

On an orbit with an eccentricity close to zero the apoapsis is ill-defined: SAT-1's
release push can move it anywhere around the orbit. So the parking burn's time is fixed
at the first warp, before the release, and its delta-v uses the carrier's radius at that
time; that point becomes the parking orbit's apoapsis (or periapsis).

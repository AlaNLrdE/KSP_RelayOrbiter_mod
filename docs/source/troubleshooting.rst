Troubleshooting
===============

Every step, decision and problem is written to ``KSP.log`` (in the KSP folder) with the
``[RelayOrbiter]`` prefix. KSP overwrites that file every time it starts, so read it, or
copy it, before launching the game again.

Before starting
---------------

Messages in the Automation tab, in orange, that keep **Start deployment** disabled.

.. list-table::
   :header-rows: 1
   :widths: 40 60

   * - Message
     - What to do
   * - *No satellites found: each must hang on a decoupler or a docking port and have a
       probe core.*
     - See :doc:`vessel_design`. ``KSP.log`` lists every separator and what its docking
       face holds.
   * - *N satellites need N probe cores on separators; this vessel has M.*
     - Lower **Satellites**, or add satellites.
   * - *Pick exactly N satellites (M picked).*
     - Click N satellites, in release order.
   * - *Release X before Y: it is mounted on it.*
     - A stacked satellite must be released before the one carrying it: change the order.
   * - *SAT-k (...) has no engine to circularize.*
     - Every satellite but the first needs an engine.
   * - *The carrier has no active engine: stage its engine first.*
     - Stage until the carrier's engine runs.
   * - *SAT-k has no probe core with SAS (level 1+) to point its burn.*
     - Use a probe core with SAS.
   * - *SAT-k's burn is too long (... s): it would leave the carrier's loading range.*
     - Give the satellite a stronger engine.
   * - *The parking orbit's periapsis would be too low* / *apoapsis would leave the SOI*
     - Choose another altitude, or another number of satellites.
   * - *... km is inside the atmosphere / below the surface / beyond the sphere of
       influence.*
     - Choose another target altitude.
   * - *Not enough Δv: the carrier needs about X and has about Y.*
     - Add fuel to the carrier, or choose a closer target.
   * - *The plane change needs SAS maneuver mode (probe core or pilot with SAS level 3).*
     - Use a better probe core or pilot, or untick *Change inclination*.
   * - *Fix the target inclination (0 to 180°).*
     - Enter a valid inclination.
   * - *Get into a stable orbit first.* / *Delete the vessel's maneuver nodes first.*
     - Self-explanatory; nodes are deleted from the Maneuvers tab.

During a deployment
-------------------

When a deployment stops, the tab shows *Stopped at step k/n (...): reason*.

.. list-table::
   :header-rows: 1
   :widths: 40 60

   * - Reason
     - Cause and fix
   * - *The maneuver node disappeared: it was deleted, or the game reloaded the flight
       scene ...*
     - You switched to an unloaded vessel, left the scene or deleted the node. Load the
       quicksave and don't switch vessels while it runs.
   * - *The vessel can't point along the burn: it has no electric charge ...*
     - The probe ran out of power: batteries dropped with a stage, drained through the
       docking ports, or solar panels in shadow. Keep power on the carrier's last stage
       and give satellites panels or batteries.
   * - *... it has no control* / *... no CommNet signal*
     - The probe core is unpowered or out of contact (with *Require signal for control*).
   * - *This burn needs SAS maneuver mode (SAS level 3)*
     - The plane change needs maneuver mode.
   * - *Could not point along the burn.*
     - The vessel didn't line up within 60 s: not enough torque (add reaction wheels) or
       it is spinning.
   * - *Out of thrust with X m/s left, and the next stage would release a satellite*
     - The carrier ran dry and the next stage holds a satellite. Add fuel, or move the
       satellites' separators to later stages.
   * - *Out of fuel with X m/s left: no stage left.*
     - Not enough fuel.
   * - *... is already past apoapsis; the release came too late.*
     - The satellite was released after its apoapsis. Rare; report it with the log.
   * - *The carrier drifted out of range and was unloaded.*
     - A satellite's burn took it more than 22.5 km away. Use stronger satellite engines.
   * - *Could not release SAT-k* / *SAT-k did not separate*
     - The separator didn't fire. Check it is not already used and that it holds the
       satellite.

Not errors
----------

These show only in ``KSP.log``; the deployment goes on.

- *Orientation timed out ...: releasing anyway* — the carrier didn't get within 3° of
  perpendicular in 60 s.
- *Clearing timed out ...: switching anyway* — docking ports push gently, so the satellite
  may still be a few metres away after 45 s; the carrier has already turned 90°.
- *Can't point Normal: releasing SAT-k without orienting (...)* — the carrier's SAS has no
  normal mode (level 1).
- *Already on the target orbit: no transfer needed* and similar.

MechJeb
-------

- *MechJeb 2 is not installed* — install it, or launch by hand.
- *This vessel has no MechJeb* — add a MechJeb part, or install MechJebForAll.
- *MechJeb is installed but its API changed* (in ``KSP.log``) — the MechJeb version is
  not supported (tested with 2.15); launch by hand.

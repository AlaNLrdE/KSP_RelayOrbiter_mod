Vessel design
=============

A deployment needs a **carrier** (the vessel you fly) holding the **satellites**.

Satellites
----------

Each satellite needs:

.. list-table::
   :header-rows: 1
   :widths: 30 70

   * - Part
     - Why
   * - A probe core with SAS
     - To point its circularization burn. SAS level 1 (prograde/retrograde) is enough;
       level 3 (maneuver mode) is used when available.
   * - An engine and fuel
     - For its circularization burn (not needed for SAT-1, which stays on the circular
       orbit). Its tanks can start empty: the carrier tops them up.
   * - Electric charge
     - Batteries or solar panels. A probe without power has no SAS (see below).
   * - An antenna
     - It is a relay, after all.

Each satellite hangs on a **separator**, and the mod recognizes:

- **A decoupler** (stack or radial), whichever side it stays on.
- **A single docking port joined in the editor**, for example a Clamp-O-Tron Jr. mounted
  on a fairing base: the port releases what its docking face holds.
- **A pair of docking ports**, joined in the editor or docked in flight.

Satellites can be stacked: a satellite mounted on another one is released first (the plan
checks the order you click).

The burn of each satellite moves it about Δv·t/2 away from the carrier; the plan rejects a
satellite whose burn would take it more than 15 km away, since the carrier must stay
loaded. Use an engine strong enough for a burn of a minute or so.

Carrier
-------

- **A probe core (or pilot) with SAS.** Level 2 (normal) lets it orient before each
  release; level 3 (maneuver) is required for a plane change.
- **An engine and enough fuel** for the transfer, plane change and parking burn. The plan
  computes the carrier's own delta-v (its engines and tanks, minus what goes to the
  satellites, carrying every satellite) and refuses to start when it is not enough.
- **Electric charge that stays on board.** Keep batteries or panels on the part of the
  carrier that remains after staging.

Staging
-------

During the carrier's burns the mod stages away a spent stage, but **never a stage that
would release a satellite still on board**. So:

- Put every satellite's separator in the **last** stages, after the carrier's engine.
- On the pad, stage only until the carrier's engine runs (or let MechJeb do it).

.. note::

   **Docking ports share fuel and electric charge.** While the satellites are attached,
   the carrier's engine and probe can drain them. The mod refills the satellites' tanks
   before each release (option on by default), but not their batteries: give them solar
   panels (the mod extends them before the release) or enough battery.

Fairings
--------

Open the fairings around the satellites before starting: a satellite released inside a
closed fairing hits it.

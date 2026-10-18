Overview
========

What it does
------------

Placing relay satellites evenly around a body by hand means timing every release and
circularization burn. Relay Orbiter does it with a **resonant parking orbit**: the
carrier flies an orbit whose period is a fraction of the satellites' period, so every
time it comes back to the release point it is exactly 360°/N away from the satellites
already released. It releases one satellite per parking orbit and, after N−1 parking
orbits, the N satellites are evenly spaced. The math is explained in
:doc:`how_it_works`.

Features
--------

- **2 to 12 satellites**, 360°/N apart (120° for three, 60° for six).
- **Target orbit:** synchronous orbit, the orbital altitude of a moon, the midpoint
  between two moons (or two planets around the Sun), or any altitude.
- **Inclination:** keep the current plane, or choose one. From the pad MechJeb launches
  straight to it; in orbit the plane is changed at a node.
- **Full automation** (Automation tab): transfer to the target orbit, plane change,
  every release, vessel switch and circularization burn, with time warp handled.
- **Launch from the pad** with MechJeb 2 (optional dependency): MechJeb flies the ascent
  and Relay Orbiter carries on in orbit.
- **Manual mode** (Maneuvers tab): nodes and Alarm Clock alarms to fly it yourself.
- **Planning** (Results tab): every orbit, speed and burn for any body and altitude.
- **Safety:** the plan is checked before starting, satellites are never staged away,
  tanks are topped up before each release, and the carrier orients itself so the
  release push doesn't change the satellite's period. See :doc:`automation`.
- **Unique names:** ``Kerbin Orion 2/6 - SYNC i10``, with the vessel type set to Relay.
- **CommNet check:** whether the satellites will see and reach each other.
- **Resume:** a deployment interrupted by a scene reload can be resumed.
- **RCS separation:** with RCS, the carrier pushes itself away from each satellite.
- **Planet packs:** the bodies come from the running game (stock, Kopernicus, Outer
  Planets Mod...).

What a deployment looks like
----------------------------

.. mermaid::

   flowchart LR
       A[On the pad] -->|MechJeb ascent| B[Low orbit]
       B -->|Hohmann transfer<br/>+ plane change| C[Target orbit]
       C --> D[Release SAT-1]
       D -->|parking burn| E[Parking orbit]
       E -->|one parking orbit| F[Release SAT-k<br/>switch, circularize,<br/>switch back]
       F -->|more satellites| E
       F -->|last one| G[Constellation deployed]

Every arrow is automatic once you press **Start deployment** (in orbit) or **Launch with
MechJeb** (on the pad). Steps that are not needed, such as the transfer when the carrier
is already on the target orbit, are skipped.

Requirements
------------

.. list-table::
   :header-rows: 1
   :widths: 30 70

   * - Item
     - Notes
   * - Kerbal Space Program
     - 1.12.x (tested on 1.12.5).
   * - MechJeb 2 (optional)
     - Only to launch from the ground (tested with 2.15). Without it, launch by hand and
       start from orbit.

Relation to the calculator
--------------------------

Relay Orbiter grew out of `KSP_RelayOrbiter_calculator
<https://github.com/AlaNLrdE/KSP_RelayOrbiter_calculator>`_, a C++ command-line
calculator for the same method. The mod's orbital math is a C# port checked against
golden vectors exported by the calculator (see :doc:`development`).

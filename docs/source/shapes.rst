Constellation shapes
====================

Besides the ring (every satellite in one plane, 360°/N apart), Relay Orbiter knows
these shapes. Each satellite has a **slot**: its orbit (plane, size and shape) and its
phase along it.

.. list-table::
   :header-rows: 1
   :widths: 20 45 35

   * - Shape
     - Slots
     - What for
   * - Ring
     - One plane, N satellites 360°/N apart.
     - Equatorial or inclined coverage; what one carrier deploys.
   * - Walker Delta i:T/P/F
     - T satellites in P planes at inclination i, nodes 360°/P apart. In each plane
       they are evenly spaced; plane p is shifted p·F·360°/T ahead. When T doesn't
       split exactly into P planes, some planes take one satellite more.
     - Global coverage (GPS-like). For example 12:3/1 at 55°.
   * - Walker Star
     - The same with the nodes 180°/P apart, near-polar planes.
     - Poles and high latitudes.
   * - Molniya
     - Elliptical, period half the rotation, apoapsis over the north (ω 270°); one
       satellite per plane, nodes and phases 360°/N apart.
     - Long passes over one hemisphere. KSP has no planet flattening, so any inclination
       keeps its apoapsis in place (63.4° is only needed on Earth).
   * - Tundra
     - The same with a period of one rotation.
     - Like Molniya, higher.
   * - Tetrahedron
     - Draim's four elliptical orbits (i 31.3°, e 0.263, period about 1.1 rotations,
       nodes 90° apart).
     - The whole surface always sees at least one satellite, with only four.
   * - Trojans
     - In a moon's orbit, 60° ahead of and behind the moon (and, with three, opposite it).
     - Relays for the moon's far side, and for the space around it.

Deploying a shape
-----------------

**Ring**: as always (Automation).

**Walker Δ or ★, one carrier per plane.** In Automation, **Shape → Walker Δ plane** (or
★):

1. Set the inclination, the number of planes and F. The carrier's satellites are one
   plane's.
2. Pick the plane this carrier deploys.
3. The first plane takes the carrier's own node. The next carriers find the satellites
   already up (by their names) and aim for their plane's node.
4. From the launch pad, MechJeb launches straight to the inclination; in orbit the
   carrier changes into the plane (inclination and node), then deploys its ring.

The satellites are named across the whole constellation, with the shape in the tag:
``Kerbin Orion 5/12 - SYNC WD55-3-1``.

**Molniya, Tundra, Tetrahedron, Trojans: one satellite per plane.** Their planes are
too far apart for one carrier: Molniya's three planes are about 100° from each other.

1. Launch each satellite on its own, in a low orbit close to its plane.
2. In Maintenance, tick the constellation's satellites (the new one included).
3. Pick the shape and press **Put … into its slot**.

Big constellations
------------------

A constellation can have up to 120 slots: 96 satellites as a Walker Star of 8 planes of
12, for instance.

Every burn means switching to its satellite: a scene reload, pointing and the burn, a
minute or two of real time each. Correcting a hundred satellites at once can therefore
take hours. Do it **plane by plane**:

1. Pick a **Reference** satellite that stays put, so every run uses the same slots.
2. Press **Put plane k into the shape** (with **−** and **+** to pick the plane).
3. Repeat for each plane.

Putting satellites into a shape (Maintenance)
---------------------------------------------

In Maintenance, **Shape** picks the shape. When the names carry a shape tag it is picked
for you, with its parameters.

1. The shape's parameters: inclination, planes and F (Walker); periapsis altitude
   (Molniya, Tundra); the moon (Trojans).
2. Every member is fitted into a slot: the reference node and phase are those that
   need the least plane change and phasing (or the reference satellite's, or the
   moon's for Trojans).
3. The list shows each satellite's slot, plane, phase error and angle to its plane.
   The window shows the plane changes' total Δv.
4. **Name them by slot** gives them names in our scheme with the shape's tag.
5. **Put … into its slot** (the active satellite), or **Put every satellite into the
   shape** (all of them, in parallel, as for a ring).

Each satellite goes through these steps, skipping the ones it doesn't need:

1. **Plane change** into its slot's plane. When it goes up, this happens after the
   transfer, where it is cheaper.
2. **Transfer** to the slot's radius (an elliptical slot: its periapsis radius). For a
   circular slot the burn is timed to arrive at the slot itself, so there is almost no
   phasing left (and Trojans keep clear of their moon).
3. **Apsis** (elliptical slots): a prograde burn at the point where the slot's periapsis
   must be raises the apoapsis to the slot's.
4. **Phasing** and the **period trim**. For an elliptical slot both happen at the
   periapsis, which stays in place.

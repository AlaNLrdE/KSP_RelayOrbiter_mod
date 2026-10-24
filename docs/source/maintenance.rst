Constellation maintenance
=========================

The **Maintenance** tab puts the satellites of an existing constellation back in
their slots, 360°/N apart, and on the same period so they stay there.

Why constellations drift
------------------------

KSP does not perturb orbits on rails: a satellite keeps its orbit forever. The
spacing still drifts when the periods differ slightly, because each satellite gains
or loses a little every orbit. A period only 0.01 % off moves a satellite about
0.04° per orbit. That is barely visible after a few orbits, but adds up to several
degrees after a hundred. The burns that put the satellites in place can't be more
precise than a few cm/s, so some drift is unavoidable. The fix is to match the
periods precisely and, when a satellite is already off its slot, to send it on a
phasing orbit.

Finding the constellation
-------------------------

Switch to one of the satellites and open the tab. It suggests the constellation from
the active vessel's name:

- **Our names** (``Kerbin Orion 2/6 - SYNC``): every ``Kerbin Orion k/6`` orbiting the
  same body, with 6 slots.
- **Numbered names** (``Kerbin Sat 3``): every ``Kerbin Sat <number>``, with as many
  slots as the highest number (or the number of satellites). The window says it was
  guessed: check it.

Tick or untick satellites to correct the suggestion, set the number of **Slots** (2 to 120: bigger than one carrier deploys)
(empty slots are fine: a satellite can be missing), and press **Suggest again** to
start over. The list shows, for each member, its slot, how far it is off and its
drift per orbit.

Grouping relays by plane
------------------------

**Group by plane** gathers relays that were not deployed together (or have free
names) into one constellation.

1. It ticks every vessel orbiting the same body whose orbital plane is within the
   **tolerance** of the active satellite's plane. The tolerance is 5° by default.

   - It compares the angle between the planes, not just the inclinations: two orbits
     inclined 10° with opposite nodes are 20° apart.
   - A retrograde orbit never counts as the same plane.

2. The list shows each vessel's plane difference and altitude. Tick or untick vessels
   to adjust the group.
3. The preview shows the new names in our scheme.

   - The callsign is the members' own when they already share one, otherwise the
     first free one around the body.
   - The numbers follow the orbit from the active satellite. When the members already
     have our names, the numbering starts from the one named 1/N.
   - The tag carries the group's median altitude (or ``SYNC``) and mean inclination.
4. **Rename them and form the constellation** renames them for good, the save file
   included, and marks them as relays. It then switches back to **Group by name**
   with the new constellation selected, ready to be corrected.

Slots, reference and final altitude
-----------------------------------

**Reference**

- *Best fit* (the default) places the slots where they need the smallest corrections
  overall.
- Clicking the button cycles through the members: that satellite stays put and the
  slots are measured from it.

**Final altitude**

- *Suggested*: the synchronous orbit when the names say ``SYNC``, otherwise the mean
  of the satellites' orbits.
- *Custom*: any altitude between a safe minimum and the SOI.
- With a reference, the final altitude is the reference's.

**Phasing orbits (at least)**: more phasing orbits take longer but need less Δv.

**Match the orbital planes too** (on by default) rotates every orbit into one common
plane before anything else. The common plane is the reference's, or the mean plane of
the members.

- Satellites in different planes keep their period, but they drift towards and away
  from each other twice an orbit, so the spacing never holds everywhere.
- Each plane change happens where the satellite's plane crosses the common plane.
  It costs 2v·sin(θ/2) and leaves the period unchanged.
- It needs SAS maneuver mode (level 3) on each satellite that turns.

Correcting
----------

**Correct this satellite** corrects the active satellite only.

**Correct the whole constellation** corrects every member off its slot or off the
period.

1. The slots are frozen when the correction starts, and advance at the target
   period.
2. Every satellite is corrected against those frozen slots, so they all phase in
   parallel. The whole correction takes about as long as the slowest satellite, not
   the sum of them all.
3. The correction switches to each satellite when its burn is due. Switching to a
   far satellite reloads the flight scene. The correction is saved before the
   reload and carries on by itself after it, with the window open.

Each satellite goes through these stages, skipping the ones it doesn't need:

1. **Plane change**, when matching planes and more than 0.05° off the common plane.
2. **Transfer and circularize**, only when an apsis is more than 1 % off the
   final orbit.
3. **Phasing**: one burn changes the period so that, after *k* orbits, the satellite
   is back in its slot. A second burn at the same point sets the final period. The
   phasing orbit must stay out of the atmosphere and inside the SOI, so *k* grows
   when it would not. When that never works, the satellite goes the long way round.
   Two satellites' phasing returns are kept at least 30 minutes apart, so neither
   misses its pass.
4. **Period trim**, when the period is still more than 0.01 % off.

After the trim, a satellite still more than 0.5° off its slot (a missed pass, for
instance) is phased once more.

All maintenance burns are precise: they stop 0.005 m/s from the target instead of
0.05 m/s. That matters on slow, high orbits, where a few cm/s already change the
period noticeably.

The window shows the burns done against an estimate of the total (the estimate
shrinks as stages turn out not to be needed) and each satellite's next burn.

If the game is quickloaded during a correction, the tab offers **Resume correction**
or **Discard it**.

.. note::

   Don't switch vessels or touch the controls until the window says
   *Constellation corrected*. Unloaded satellites can't be checked for Δv: make sure
   each one has some fuel left.

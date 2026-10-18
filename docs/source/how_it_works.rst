How it works
============

The resonant parking orbit
--------------------------

The satellites end on a circular orbit of radius :math:`r_c` and period :math:`T_c`. The
carrier flies a **parking orbit** that touches that circle at one point, the **release
point**, and whose period is a fixed fraction :math:`k` of :math:`T_c`:

.. math::

   T_e = k\,T_c, \qquad
   k = \begin{cases}
       \dfrac{N-1}{N} & \text{inner parking orbit} \\[2mm]
       \dfrac{N+1}{N} & \text{outer parking orbit}
   \end{cases}

Each time the carrier comes back to the release point, one parking orbit later, the
satellites already released have travelled :math:`k` of their orbit, so the carrier is
:math:`1/N` of an orbit (360°/N) away from the last one. It releases the next satellite
there, which circularizes. After N−1 parking orbits the N satellites are evenly spaced.

.. image:: ../images/deployment.svg
   :alt: One release per parking orbit, three satellites
   :align: center

From Kepler's third law (:math:`T \propto a^{3/2}`), the parking orbit's semi-major axis
and its other apsis are:

.. math::

   a_e = r_c\,k^{2/3}, \qquad r_\text{other} = 2a_e - r_c

.. list-table::
   :header-rows: 1
   :widths: 20 40 40

   * -
     - Inner (:math:`k < 1`)
     - Outer (:math:`k > 1`)
   * - Release point
     - apoapsis :math:`= r_c`
     - periapsis :math:`= r_c`
   * - Other apsis
     - periapsis :math:`2a_e - r_c`, below the target
     - apoapsis :math:`2a_e - r_c`, above the target
   * - Carrier's burn
     - retrograde
     - prograde
   * - Satellites' burn
     - prograde
     - retrograde
   * - Fails when
     - the periapsis is underground or in the atmosphere
     - the apoapsis leaves the SOI

.. image:: ../images/orbit_geometry.svg
   :alt: Inner parking orbit for three satellites, drawn to scale
   :align: center

The mod **recommends** the inner parking orbit when its periapsis clears the surface and
the atmosphere, and the outer one otherwise (if it stays inside the SOI). With three
satellites the inner periapsis is at :math:`0.526\,r_c`; with two it is
:math:`0.26\,r_c`, which is why N = 2 often needs the outer orbit.

Every burn comes from the vis-viva equation, :math:`v^2 = \mu\,(2/r - 1/a)`: from a
circular orbit the carrier changes its speed at the release point from
:math:`\sqrt{\mu/r_c}` to the parking orbit's, and each satellite makes the opposite
change.

SAT-1 is released first
~~~~~~~~~~~~~~~~~~~~~~~

SAT-1 is released **before** the carrier's parking burn, while the carrier is still on
the circular target orbit, so SAT-1 needs no burn at all. Released a minute or two before
the burn, it stays practically on top of the carrier until the burn, which is the
reference for the spacing.

The release push
~~~~~~~~~~~~~~~~

What really spoils the spacing is a difference in period. A push :math:`\Delta v` along
the orbit changes the period by :math:`\Delta T/T \approx 3\,\Delta v/v`: 0.3 m/s at
1,485 m/s is 0.06%, about 0.2° of drift per orbit. So before each release the carrier
turns so the separation axis is **perpendicular to the velocity**: it points *normal* when
the satellite sits on its axis, and *prograde* when the satellite is mounted on its side.

Reaching the target orbit
-------------------------

**Synchronous orbit.** The radius whose period equals the body's rotation period
:math:`T_r`:

.. math::

   r_\text{sync} = \left(\mu \left(\frac{T_r}{2\pi}\right)^2\right)^{1/3}

**Hohmann transfer.** From the current orbit, the carrier burns at periapsis when the
target is at or above it (to raise the apoapsis, or to lower only the apoapsis), and at
apoapsis otherwise (to lower the periapsis). It then circularizes at the target. The
transfer is skipped within 1 km, or 0.05% of the target radius.

**Plane change.** Rotating a velocity :math:`v` by :math:`\Delta i` costs
:math:`2v\sin(\Delta i/2)`; the burn has a normal component :math:`\pm v\sin\Delta i`
(positive at the ascending node for a higher inclination) and a prograde component
:math:`v(\cos\Delta i - 1)`. It happens at the next node with the equator, whose true
anomaly is :math:`360° - \omega` (ascending) or :math:`540° - \omega` (descending), where
it is cheapest: **after raising** the orbit, or **before lowering** it. Below 0.1° it is
skipped.

**From the pad.** MechJeb launches to a low orbit (30 km above the atmosphere, or 20 km
and at least 5% of the radius on airless bodies), directly at the chosen inclination,
which is far cheaper than changing the plane later. The transfer follows.

Delta-v and burn times
----------------------

The carrier's available delta-v uses the rocket equation,
:math:`\Delta v = I_{sp}\,g_0 \ln(m_\text{wet}/m_\text{dry})`, with the Isp of its running
engines, the propellant in its own tanks minus what goes to the satellites, and the whole
vessel mass. KSP's own readout counts the satellites' stages too, so it would say there is
enough when there isn't.

Burn durations, used to start each burn half its duration before the node and to time
the warps, come from the same equation:
:math:`t = m_0\,v_e/F\,\left(1 - e^{-\Delta v/v_e}\right)`, :math:`v_e = I_{sp}\,g_0`.

Names
-----

Each satellite is named ``<body> <callsign> <k>/<N> - <orbit>[ i<inclination>]``, for
example ``Kerbin Orion 2/6 - SYNC i10``:

- **callsign:** the first of Aurora, Beacon, Comet, Draco, Echo, Fenix, Gemini, Helios,
  Icarus, Juno, Kestrel, Lyra, Meteor, Nova, Orion, Pulsar, Quasar, Raptor, Sirius, Titan,
  Umbra, Vega, Wraith, Xenon, Zenith not yet used around that body; then *Aurora II*, ...
- **orbit:** ``SYNC`` within 1% of the synchronous altitude, else the altitude in km
  (``1000K``).
- **inclination:** only when it is 0.5° or more.

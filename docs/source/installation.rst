Installation
============

Install
-------

1. Download ``RelayOrbiter-<version>.zip`` from the `releases
   <https://github.com/AlaNLrdE/KSP_RelayOrbiter_mod/releases>`_, or build it (see
   :doc:`development`).
2. Copy the ``RelayOrbiter`` folder into the ``GameData`` folder of your KSP install.
   You should end up with::

      GameData/
        RelayOrbiter/
          Plugins/
            RelayOrbiter.dll
            RelayOrbiter.Core.dll

3. Start KSP and load a flight. The Relay Orbiter button (a planet with three
   satellites around it) appears in the stock toolbar in flight and in map view. It does
   not appear in the Space Center, the VAB or the Tracking Station: the mod works on the
   vessel you are flying.

``KSP.log`` shows ``[RelayOrbiter] Loaded`` when the mod has loaded.

Optional: MechJeb 2
-------------------

To launch from the pad, install `MechJeb 2 <https://github.com/MuMech/MechJeb2>`_ and make
sure the vessel has MechJeb (a MechJeb part, or MechJebForAll). Relay Orbiter finds
MechJeb at run time; without it everything else still works.

Settings
--------

The window remembers its position, last tab, number of satellites, inclination and
naming choice in::

   GameData/RelayOrbiter/Plugins/PluginData/RelayOrbiter/config.xml

Delete that file to reset them.

Uninstall
---------

Delete ``GameData/RelayOrbiter``. Satellites already deployed keep their names and orbits.

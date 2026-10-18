Limitations
===========

- **KSP 1.12 only.** Built against 1.12.5; other versions may work but are untested.
- **One plane, no LAN control.** The constellation ends in whatever plane results; the
  longitude of the ascending node is not chosen. For relay coverage it makes no
  difference.
- **2 to 12 satellites**, all on the same orbit.
- **No resuming.** If the game reloads the flight scene during a deployment (switching to
  an unloaded vessel, leaving the scene, quickloading), the deployment stops; load the
  quicksave from before it and start again.
- **Gentle separation.** Docking ports push satellites away slowly; there is no RCS
  assist yet, only the carrier's 90° turn.
- **Electric charge is not topped up**, only fuel. Satellites need their own power.
- **MechJeb is reached by reflection** (tested with 2.15); a MechJeb release that changes
  its internals can break the launch step, but nothing else.
- **SAS requirements.** Satellites need SAS level 1; orienting the carrier needs level 2;
  plane changes need level 3.
- **Results and Maneuvers** plan from a circular starting orbit or from the vessel's
  current apsides; only Automation transfers to the target.

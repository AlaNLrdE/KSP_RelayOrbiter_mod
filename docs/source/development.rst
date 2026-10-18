Development
===========

Repository layout
-----------------

.. list-table::
   :header-rows: 1
   :widths: 35 65

   * - Path
     - Contents
   * - ``src/RelayOrbiter.Core/``
     - Orbital math and planning. No KSP or Unity references; builds for ``net472`` (KSP)
       and ``netstandard2.0`` (tests).
   * - ``src/RelayOrbiter.Plugin/``
     - The KSP plugin (``net472``): window, panels, deployment runner, node executor.
   * - ``tests/RelayOrbiter.Core.Tests/``
     - xUnit tests of the core (``net8.0``).
   * - ``GameData/RelayOrbiter/``
     - The mod folder; builds stage the DLLs into ``Plugins/``.
   * - ``docs/``
     - This documentation (Sphinx, Mermaid diagrams).
   * - ``deploy.sh``
     - Builds and installs into the Steam install of KSP.

Architecture
------------

Everything that can be decided without the game lives in the core and is unit tested;
the plugin turns those decisions into KSP calls.

.. mermaid::

   flowchart TB
       subgraph Plugin["RelayOrbiter.Plugin (KSP)"]
           Addon[RelayOrbiterAddon<br/>toolbar, settings]
           Window[RelayOrbiterWindow]
           Results[Results tab]
           Maneuvers[ManeuversPanel]
           Automation[AutomationPanel]
           Runner[DeploymentRunner]
           Executor[NodeExecutor]
           Game[GameBodyDatabase<br/>VesselParts<br/>MechJebAscent]
       end
       subgraph Core["RelayOrbiter.Core (no KSP)"]
           Calc[ConstellationCalculator<br/>Resonance]
           Plans[DeploymentPlanner<br/>TransferPlanner<br/>PlaneChange]
           Seq[DeploymentSequence]
           Tree[PartTree<br/>ResourceTopUp]
           Burn[NodeBurn<br/>BurnTiming<br/>StagingGuard]
           Misc[AltitudePlanner<br/>ConstellationNaming<br/>LaunchPlanner]
       end
       Addon --> Window --> Results & Maneuvers & Automation
       Automation --> Runner --> Executor
       Results --> Calc
       Maneuvers --> Plans
       Automation --> Tree & Plans & Seq & Misc
       Runner --> Plans
       Executor --> Burn
       Game -.-> Tree

.. list-table:: Core
   :header-rows: 1
   :widths: 30 70

   * - Class
     - Responsibility
   * - ``OrbitalMechanics``, ``KspBodyDatabase``
     - Kepler, vis-viva, synchronous orbit; the stock bodies (tests only, the plugin reads
       the game's).
   * - ``ConstellationCalculator``, ``Resonance``
     - The parking orbit for N satellites, inner or outer, and which one to use.
   * - ``DeploymentPlanner``
     - Burns and release times from the vessel's actual orbit.
   * - ``TransferPlanner``, ``PlaneChange``
     - Hohmann transfer and plane change.
   * - ``DeploymentSequence``
     - The ordered steps of a deployment.
   * - ``PartTree``, ``ResourceTopUp``
     - Satellites on a vessel, release order and stats; tank top-up transfers.
   * - ``NodeBurn``, ``BurnTiming``, ``StagingGuard``
     - Throttle and cut-off, burn durations and delta-v, safe staging.
   * - ``AltitudePlanner``, ``ConstellationNaming``, ``LaunchPlanner``
     - Target altitude options, satellite names, launch altitude.

Interfaces (``IOrbitalMechanics``, ``IBodyDatabase``, ``IConstellationCalculator``,
``IDeploymentPlanner``, ``IAltitudePlanner``) keep the classes injectable; the plugin's
``RelayOrbiterAddon`` is the composition root.

Building
--------

Needs the .NET SDK 8 or later and a KSP 1.12 install to compile against.

.. code-block:: bash

   cp KspPaths.props.example KspPaths.props   # set KSPDir (and KSPMainDir) to your installs
   dotnet build                               # builds; stages GameData/RelayOrbiter/Plugins
   dotnet test                                # runs the core tests

``KspPaths.props`` is not committed. The plugin references ``Assembly-CSharp.dll`` and the
``UnityEngine*.dll`` modules from ``KSPDir`` without copying them.

Deploying
---------

.. code-block:: bash

   ./deploy.sh              # find the Steam install, build Release, copy GameData/RelayOrbiter
   ./deploy.sh --dry-run    # only show which install it found
   ./deploy.sh --no-build   # copy what is already built
   ./deploy.sh "/path/to/Kerbal Space Program"

``deploy.sh`` reads Steam's install path from the Windows registry (from WSL) or the usual
locations, follows ``libraryfolders.vdf`` to the library holding KSP (app 220200), and
refuses to run while KSP is running. Alternatively:
``dotnet build src/RelayOrbiter.Plugin -p:DeployToKsp=true`` deploys to ``KSPDir``, and
``-p:DeployTarget=main`` to ``KSPMainDir``.

Testing
-------

The core is developed test first (xUnit). Besides unit tests, the orbital math is checked
against **golden vectors** exported by the C++ calculator, for every stock body and a set
of altitudes, to a relative tolerance of 1e-12. To regenerate them:

.. code-block:: bash

   cd ../KSP_RelayOrbiter_calculator
   make -C build vectors
   cp build/obj/golden_vectors.json ../KSP_RelayOrbiter_mod/tests/RelayOrbiter.Core.Tests/Data/

What only the game can show (pointing, staging, vessel switching) is tested in KSP;
``KSP.log`` records every step for that.

Documentation
-------------

.. code-block:: bash

   make -C docs html        # creates docs/.venv on first use; output in docs/_build/html

The *Documentation* GitHub Actions workflow builds it on every push and publishes it to
GitHub Pages from ``master``.

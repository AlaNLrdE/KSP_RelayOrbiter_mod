using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RelayOrbiter.Core;
using UnityEngine;
using Body = RelayOrbiter.Core.CelestialBody;

namespace RelayOrbiter.Plugin
{
    // Flight-only panel: pick the three satellites, check the plan, and run the
    // whole deployment with DeploymentRunner.
    internal sealed class AutomationPanel
    {
        // A satellite's circularization burn moves it about Δv·t/2 from the carrier.
        // During the run the carrier stays loaded up to 22.5 km (DeploymentRunner);
        // beyond that the switch back would fail.
        private const double MaxSeparation = 15_000.0;  // m
        private const float  LabelW = 230f;
        private const int    ManeuverSasSkill = 3;  // SAS level that unlocks maneuver mode

        private readonly IDeploymentPlanner       _planner;
        private readonly IAltitudePlanner         _altitudes;
        private readonly IConstellationCalculator _calculator;
        private readonly ConstellationSettings    _settings;
        private readonly TransferPlanner          _transfer;
        private readonly MoonTransferPlanner      _moonPlanner = new MoonTransferPlanner(new OrbitalMechanics());
        private const double MoonCorrectionMargin = 30.0;  // m/s kept for a course correction
        private readonly Func<double, string>     _formatTime;
        private string _inclinationText = "";
        private MechJebAscent? _launch;           // MechJeb is flying the ascent
        private bool _continueAfterLaunch = true;
        private bool _autoStart;                  // in orbit after a launch: start on its own
        private bool _inclinationValid = true;

        private readonly List<uint> _order = new List<uint>();  // separator flight ids
        private uint _orderVessel;
        private bool _extendPanels = true;
        private bool _topUp = true;
        private bool _startRequested;  // start on the next frame, after the top-up
        private DeploymentRunner? _runner;
        private GUIStyle? _ok, _warn, _header;

        public AutomationPanel(IDeploymentPlanner planner, IAltitudePlanner altitudes,
                               IConstellationCalculator calculator, ConstellationSettings settings,
                               TransferPlanner transfer, Func<double, string> formatTime)
        {
            _transfer   = transfer;
            _planner    = planner;
            _altitudes  = altitudes;
            _calculator = calculator;
            _settings   = settings;
            _formatTime = formatTime;
            // What the save file keeps while a deployment runs.
            RelayOrbiterScenario.Provider = () => IsRunning ? _runner!.ToState() : null;
        }

        public bool IsRunning => _runner != null && _runner.State == DeploymentRunner.RunState.Running;

        public bool IsLaunching => _launch != null;

        // A deployment from the save file can be resumed.
        public bool HasPending => _runner == null && RelayOrbiterScenario.Pending != null;

        public void Tick()
        {
            _runner?.Tick();
            TickLaunch();
        }

        public void Abort()
        {
            _runner?.Abort();
            StopLaunch("Launch aborted.");
        }

        // current: the body orbited; selected: where to deploy (current, or one of its
        // moons to travel to first). target: the window's valid target altitude around
        // `selected`, if any; targetProblem: why it isn't valid.
        public void Draw(Body current, Body selected, double? target, string? targetProblem)
        {
            if (_ok == null) CreateStyles();
            if (_runner != null) { DrawRunner(); return; }
            if (_launch != null) { DrawLaunching(); return; }  // first, so Abort is always there
            if (RelayOrbiterScenario.Pending != null) { DrawResume(RelayOrbiterScenario.Pending); return; }

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) { Warn("No active vessel."); return; }
            var orbit = vessel.orbit;
            GUILayout.Label($"Carrier: {vessel.vesselName}", _header);

            bool newVessel = _orderVessel != vessel.persistentId;
            if (newVessel) { _order.Clear(); _orderVessel = vessel.persistentId; }

            var tree = new PartTree(VesselParts.From(vessel));
            var candidates = tree.Candidates();
            if (newVessel)
                Debug.Log($"[RelayOrbiter] {vessel.vesselName}: {candidates.Count} satellite(s) found " +
                          $"[{string.Join(", ", candidates.Select(c => c.Name + " via " + c.SeparatorTitle))}]. " +
                          $"Separators: {VesselParts.DescribeSeparators(vessel)}");
            _order.RemoveAll(id => candidates.All(c => c.SeparatorId != id));

            GUILayout.Label($"Satellites (click {_settings.Satellites} in release order)", _header);
            if (candidates.Count == 0)
            {
                Warn("No satellites found: each must hang on a decoupler or a docking port and have a probe core.");
                return;
            }
            foreach (var c in candidates)
            {
                int pos = _order.IndexOf(c.SeparatorId);
                string label = (pos >= 0 ? $"#{pos + 1}  " : "      ") + $"{c.Name}  — via {c.SeparatorTitle}";
                bool on = GUILayout.Toggle(pos >= 0, label);
                if (on && pos < 0)       _order.Add(c.SeparatorId);
                else if (!on && pos >= 0) _order.RemoveAt(pos);
            }

            var order = _order.Select(id => candidates.First(c => c.SeparatorId == id)).ToList();
            int n = _settings.Satellites;
            var situation = vessel.situation;
            bool onGround = situation == Vessel.Situations.PRELAUNCH || situation == Vessel.Situations.LANDED ||
                            situation == Vessel.Situations.SPLASHED;
            // Before launch the carrier's engine is not staged yet.
            var releases = tree.PlanReleases(order, n, requireActiveCarrierEngine: !onGround && _launch == null);
            if (!releases.IsValid)
            {
                Warn(candidates.Count < n
                    ? $"{n} satellites need {n} probe cores on separators; this vessel has {candidates.Count}."
                    : releases.Error!);
                return;
            }

            // The vessel's checks come after the satellites', so they show on the launch pad too.
            var moon = selected.Name == current.Name ? null
                     : FlightGlobals.Bodies.FirstOrDefault(b => GameBodyDatabase.NameOf(b) == selected.Name);
            if (selected.Name != current.Name && moon == null) { Warn($"{selected.Name} not found."); return; }
            if (onGround) { DrawLaunch(vessel, current, selected, target, targetProblem, n, order); return; }
            if (vessel.situation != Vessel.Situations.ORBITING) { Warn("Get into a stable orbit first."); return; }
            if (_altitudes.Validate(current, orbit.ApA) != AltitudeStatus.Ok) { Warn("The apoapsis is not usable."); return; }
            var solver = vessel.patchedConicSolver;
            if (solver == null) { Warn("Maneuver nodes are not available yet."); return; }
            if (solver.maneuverNodes.Count > 0) { Warn("Delete the vessel's maneuver nodes first (Maneuvers tab)."); return; }

            // ── Target orbit: transfer and plane change before the deployment ──
            GUILayout.Label("Target orbit", _header);
            if (!target.HasValue)
            {
                Warn((targetProblem ?? "Choose a valid target altitude above.") +
                     " Pick another one above (Synchronous, At a moon, Midpoint or Custom).");
                return;
            }
            double rGoal = selected.Radius + target.Value;
            var transfer = _transfer.Plan(selected.Mu, orbit.ApR, orbit.PeR, rGoal);
            MoonTransferPlan? toMoon = null;
            double moonWait = 0.0, moonPlaneAngle = 0.0, moonPlaneDv = 0.0;
            if (moon != null)
            {
                // Travel first: captured straight onto the target orbit's radius.
                toMoon = _moonPlanner.Plan(current.Mu, orbit.semiMajorAxis, moon.orbit.semiMajorAxis, selected.Mu, rGoal);
                double now0 = Planetarium.GetUniversalTime();
                double phase = MoonTransferPlanner.PhaseAngleDeg(MeanLongitude.At(orbit, now0), MeanLongitude.At(moon.orbit, now0));
                moonWait = MoonTransferPlanner.WaitTime(phase, toMoon.PhaseAngleDeg, orbit.period, moon.orbit.period);
                transfer = new TransferPlan(false, Apsis.Periapsis, rGoal, 0.0, 0.0, 0.0);
                Row($"Transfer to {selected.Name}", $"window in {_formatTime(moonWait)} (phase {phase:F0}°, needs " +
                                                   $"{toMoon.PhaseAngleDeg:F0}°), {_formatTime(toMoon.TransferTime)} on the way");
                var moonPlane = new OrbitPlane(moon.orbit.inclination, moon.orbit.LAN);
                moonPlaneAngle = PlaneGrouping.RelativeInclinationDeg(new OrbitPlane(orbit.inclination, orbit.LAN), moonPlane);
                if (moonPlaneAngle >= 0.2)
                {
                    moonPlaneDv = PlaneGrouping.PlaneChangeDv(orbit.orbitalSpeed, moonPlaneAngle);
                    Row($"Into {selected.Name}'s plane", $"{moonPlaneAngle:F1}°, {Ms(moonPlaneDv)} first");
                }
                Row("Ejection burn", $"{Ms(Math.Abs(toMoon.EjectionDv))} {ReleasePoint.Direction(toMoon.EjectionDv)}, " +
                                     "fine-tuned with KSP's prediction");
                Row("Course correction", $"if needed, about {Ms(MoonCorrectionMargin)} reserved");
                Row($"Capture at {Km(target.Value)}", $"{Ms(Math.Abs(toMoon.CaptureDv))} retrograde at the periapsis");
                if (orbit.eccentricity > 0.05)
                    Warn("The orbit is not circular: the window estimate is rough (the ejection is fine-tuned anyway).");
            }
            Row("Altitude", Km(target.Value) + (transfer.Needed || moon != null ? "" : "  (already there)"));
            DrawInclination(moon != null ? 0.0 : orbit.inclination);
            // Around a moon the arrival plane isn't known yet: assume its equator.
            double startInc = moon != null ? 0.0 : orbit.inclination;
            bool planeChange = _settings.ChangeInclination &&
                               (moon != null || PlaneChange.Needed(orbit.inclination, _settings.InclinationDeg));
            // Change the plane where the carrier is slowest: before lowering, after raising.
            bool planeFirst = moon == null && transfer.Needed && rGoal < orbit.semiMajorAxis;
            double vPlane = Math.Sqrt(selected.Mu / (planeFirst ? orbit.semiMajorAxis : rGoal));
            var pc = PlaneChange.DeltaV(vPlane, startInc, _settings.InclinationDeg, ascending: true);
            double planeDv = planeChange ? Math.Sqrt(pc.prograde * pc.prograde + pc.normal * pc.normal) : 0.0;
            if (transfer.Needed)
            {
                Row("Transfer burn", $"{Ms(Math.Abs(transfer.FirstDv))} {ReleasePoint.Direction(transfer.FirstDv)} " +
                                     $"at {transfer.FirstBurnAt.ToString().ToLowerInvariant()}");
                Row("Circularize at target", $"{Ms(Math.Abs(transfer.SecondDv))} {ReleasePoint.Direction(transfer.SecondDv)}");
            }
            if (planeChange)
                Row("Plane change", $"about {Ms(planeDv)} at the next node" + (planeFirst ? ", before the transfer" : ""));

            // Inner parking orbit (released at apoapsis) when its periapsis is safe, else outer (periapsis).
            var parking = _calculator.Recommend(selected, target.Value, n);
            string point = ReleasePoint.Name(parking);
            double now  = Planetarium.GetUniversalTime();
            var plan    = _planner.Plan(selected.Mu, rGoal, rGoal, now, n, parking);
            double transferTime = transfer.Needed
                ? Math.PI * Math.Sqrt(Math.Pow(transfer.TransferSma, 3) / selected.Mu) : 0.0;
            if (toMoon != null) transferTime = moonWait + toMoon.TransferTime;
            double carrierBurn = BurnTiming.Duration(plan.ParkingDeltaV, releases.CarrierMassAfterFirst,
                                                     releases.CarrierThrust, releases.CarrierIsp) ?? 0.0;
            var satBurns = releases.Satellites.Skip(1)
                .Select(s => BurnTiming.Duration(plan.SatelliteCircularizeDeltaV, s.Mass, s.Thrust, s.Isp) ?? 0.0)
                .ToArray();
            string satDir = ReleasePoint.Direction(plan.SatelliteCircularizeDeltaV);

            GUILayout.Label($"Plan: {n} satellites, {Resonance.SpacingDeg(n):F0}° apart", _header);
            Row("Parking orbit", ParkingDescription(selected, target.Value, n, parking));
            Row($"SAT-1 {releases.Satellites[0].Candidate.Name}",
                "released before the carrier's burn; stays on this circular orbit, no burn of its own");
            Row("Carrier parking burn",
                $"{Ms(Math.Abs(plan.ParkingDeltaV))} {ReleasePoint.Direction(plan.ParkingDeltaV)}, {carrierBurn:F0} s");
            for (int k = 1; k < n; ++k)
                Row($"SAT-{k + 1} {releases.Satellites[k].Candidate.Name}",
                    $"{Ms(Math.Abs(plan.SatelliteCircularizeDeltaV))} {satDir}, {satBurns[k - 1]:F0} s");
            Row("Total time", "about " + _formatTime((n - 1) * plan.ParkingPeriod + transferTime + plan.CircularPeriod));

            // Fuel: each satellite's tanks, and what the carrier (outside the satellites) can give.
            var satIds = order.SelectMany(c => c.PartIds).ToList();
            var satTanks = VesselParts.Tanks(vessel, satIds);
            var carrierTanks = VesselParts.Tanks(vessel,
                vessel.parts.Select(p => p.flightID).Where(id => !satIds.Contains(id)));
            var transfers = ResourceTopUp.Plan(satTanks, carrierTanks);
            for (int k = 0; k < n; ++k)
            {
                double fill = ResourceTopUp.FillFraction(VesselParts.Tanks(vessel, order[k].PartIds));
                Row($"SAT-{k + 1} fuel", fill >= 0.999 ? "full"
                    : $"{fill:P0}" + (_topUp ? ", topped up from the carrier at start" : ""));
            }
            _topUp = GUILayout.Toggle(_topUp, " Top up the satellites' tanks from the carrier (at start and before each release)");

            DrawCoverage(vessel, selected, rGoal, order, n);
            if (_topUp && ResourceTopUp.Shortfall(satTanks, transfers) > 1e-3)
                Warn("The carrier doesn't have enough fuel to fill every satellite; they get what is left.");

            // Δv of the carrier alone: its running engines and its own propellant, minus what
            // goes to the satellites, while carrying every satellite (the worst case). KSP's
            // own readout also counts the satellites' stages.
            double carrierDv = transfer.TotalDv + planeDv + Math.Abs(plan.ParkingDeltaV) +
                               (toMoon != null ? toMoon.TotalDv + MoonCorrectionMargin + moonPlaneDv : 0.0);
            double available = VesselParts.CarrierDeltaV(vessel, new HashSet<uint>(satIds),
                _topUp ? transfers : (IReadOnlyList<ResourceTransfer>)Array.Empty<ResourceTransfer>(), releases.CarrierIsp);
            Row("Carrier Δv needed", $"{Ms(carrierDv)}  (carrier has about {Ms(available)})",
                available >= carrierDv ? _ok : _warn);

            var problems = new List<string>();
            if (available < carrierDv)
                problems.Add($"Not enough Δv: the carrier needs about {Ms(carrierDv)} and has about {Ms(available)}.");
            if (_settings.ChangeInclination && !_inclinationValid)
                problems.Add("Fix the target inclination (0 to 180°).");
            // CanSetMode(Maneuver) is false while there is no node yet; ask for the SAS skill instead.
            if ((planeChange || moonPlaneDv > 0.0) && vessel.VesselValues.AutopilotSkill.value < ManeuverSasSkill &&
                !vessel.Autopilot.CanSetMode(VesselAutopilot.AutopilotMode.Maneuver))
                problems.Add("The plane change needs SAS maneuver mode (probe core or pilot with SAS level 3).");
            if (parking == ParkingOrbit.Inner)
            {
                double hPeri = plan.PeriapsisRadius - selected.Radius;
                if (hPeri <= 0.0 || hPeri < selected.Atm)
                    problems.Add("The parking orbit's periapsis would be too low. Raise the orbit.");
            }
            else if (selected.Soi.HasValue && plan.ApoapsisRadius > selected.Soi.Value)
                problems.Add($"The parking orbit's apoapsis would leave {selected.Name}'s SOI. " +
                             "Lower the orbit or use more satellites.");
            for (int k = 0; k < n; ++k)
            {
                var s = releases.Satellites[k];
                if (k > 0 && VesselParts.SasLevel(vessel, s.Candidate.PartIds) < 1)
                    problems.Add($"SAT-{k + 1} has no probe core with SAS (level 1+) to point its burn.");
                if (k > 0 && Math.Abs(plan.SatelliteCircularizeDeltaV) * satBurns[k - 1] / 2.0 > MaxSeparation)
                    problems.Add($"SAT-{k + 1}'s burn is too long ({satBurns[k - 1]:F0} s): it would leave " +
                                 "the carrier's loading range. Use a stronger engine.");
            }
            foreach (var p in problems) Warn(p);

            _extendPanels = GUILayout.Toggle(_extendPanels, " Extend the satellites' solar panels before release");
            NodeExecutor.AutoActivateEngines = GUILayout.Toggle(NodeExecutor.AutoActivateEngines,
                " Activate the current stage's engines when none is running before a burn");

            // Names: callsign unique around this body, orbit tag, inclination.
            string callsign = ConstellationNaming.Callsign(selected.Name, FlightGlobals.Vessels.Select(v => v.vesselName));
            string tag = ConstellationNaming.OrbitTag(target.Value, _altitudes.Synchronous(selected).Altitude);
            double finalInc = _settings.ChangeInclination ? _settings.InclinationDeg : orbit.inclination;
            var names = Enumerable.Range(1, n)
                .Select(k => ConstellationNaming.SatelliteName(selected.Name, callsign, k, n, tag, finalInc))
                .ToArray();
            _settings.NameSatellites = GUILayout.Toggle(_settings.NameSatellites,
                $" Name them {names[0]} … {n}/{n} and mark them as relays");

            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && problems.Count == 0;
            // After a launch with "continue automatically", start as if Start was clicked
            // (in the Repaint pass, so the next Layout pass sees the new state).
            bool autoStart = _autoStart && Event.current.type == EventType.Repaint;
            if (autoStart)
            {
                _autoStart = false;
                if (problems.Count > 0)
                    Debug.Log("[RelayOrbiter] In orbit, but not continuing on its own: " + string.Join(" ", problems));
            }
            if ((GUILayout.Button("Start deployment") || autoStart && problems.Count == 0) && !_startRequested)
            {
                if (_topUp && transfers.Count > 0)
                {
                    // Fill the tanks, then start next frame so the burn estimates use the new masses.
                    VesselParts.Apply(vessel, transfers);
                    Debug.Log("[RelayOrbiter] Topped up the satellites: " + string.Join(", ",
                        transfers.GroupBy(t => t.Resource).Select(g => $"{g.Key} +{g.Sum(t => t.Amount):F1}")));
                    _startRequested = true;
                }
                else StartRun(vessel, order, carrierBurn, satBurns, parking, rGoal,
                              new TransferSteps(transfer.Needed || moon != null, planeChange, planeFirst, moon != null),
                              names, moon);
            }
            else if (_startRequested && Event.current.type == EventType.Repaint)
            {
                // Repaint is the last pass of the frame: the next Layout pass sees the runner.
                _startRequested = false;
                if (problems.Count == 0)
                    StartRun(vessel, order, carrierBurn, satBurns, parking, rGoal,
                             new TransferSteps(transfer.Needed || moon != null, planeChange, planeFirst, moon != null),
                             names, moon);
            }
            GUI.enabled = wasEnabled;
            GUILayout.Label("While it runs: don't touch the controls and don't switch vessels or leave the " +
                            $"flight scene; the mod switches to SAT-2 … SAT-{n} itself. Time warp is handled for you.",
                            _warn);
        }

        // ── CommNet coverage (a warning, never a blocker) ────────────────────

        private void DrawCoverage(Vessel vessel, Body body, double radius, List<SatelliteCandidate> order, int n)
        {
            if (!VesselParts.CommNetEnabled) return;
            double chord = Coverage.NeighbourDistance(radius, n);
            if (!Coverage.LineOfSight(radius, body.Radius, n))
            {
                double rMin = Coverage.MinRadiusForLineOfSight(body.Radius, n);
                Warn(double.IsInfinity(rMin)
                    ? $"CommNet: with {n} satellites the neighbours are on opposite sides of {body.Name} " +
                      "and can't see each other."
                    : $"CommNet: {body.Name} blocks the line between neighbours; it needs an orbit above " +
                      $"{Km(rMin - body.Radius)}.");
                return;
            }
            var powers = order.Select(c => VesselParts.RelayPower(vessel, c.PartIds)).ToArray();
            int weakest = Array.IndexOf(powers, powers.Min());
            if (powers[weakest] <= 0.0)
            {
                Warn($"CommNet: SAT-{weakest + 1} has no relay antenna.");
                return;
            }
            // Neighbours on the orbit are consecutive in release order (cyclically).
            double range = Enumerable.Range(0, n).Min(k => Coverage.Range(powers[k], powers[(k + 1) % n]));
            string text = $"neighbours {Km(chord)} apart, relay range {Km(range)}";
            if (range >= chord) Row("CommNet", text + ": linked");
            else Warn($"CommNet: {text}. They won't link: use stronger relay antennas or more satellites.");
        }

        // ── Launch from the ground with MechJeb ──────────────────────────────

        // current: the body launched from; body: where the constellation goes.
        private void DrawLaunch(Vessel vessel, Body current, Body body, double? target, string? targetProblem, int n,
                                List<SatelliteCandidate> order)
        {
            GUILayout.Label("Launch to orbit (MechJeb)", _header);
            if (!target.HasValue)
            {
                Warn((targetProblem ?? "Choose a valid target altitude above.") +
                     " Pick another one above (Synchronous, At a moon, Midpoint or Custom).");
                return;
            }
            if (!MechJebAscent.Installed)
            {
                Warn("MechJeb 2 is not installed: launch by hand, then come back to this tab in orbit.");
                return;
            }
            var mechJeb = MechJebAscent.For(vessel);
            if (mechJeb == null)
            {
                Warn("This vessel has no MechJeb: add a MechJeb part (or install MechJebForAll), or launch by hand.");
                return;
            }

            bool toMoon = body.Name != current.Name;
            // Travelling on: a low parking orbit around the launch body.
            double launchAlt = toMoon ? LaunchPlanner.ParkingAltitude(current) : LaunchPlanner.LaunchAltitude(body, target.Value);
            DrawInclination(Math.Abs(vessel.latitude));
            // Travelling to a moon: launch into its orbital plane's inclination (the node is
            // matched in orbit); the target inclination applies around the moon.
            var moonBody = toMoon ? FlightGlobals.Bodies.FirstOrDefault(b => GameBodyDatabase.NameOf(b) == body.Name) : null;
            double inclination = moonBody != null ? moonBody.orbit.inclination
                               : _settings.ChangeInclination ? _settings.InclinationDeg : 0.0;
            Row("Launch to", $"{Km(launchAlt)} circular, inclination {inclination:F1}°" +
                             (inclination < Math.Abs(vessel.latitude) ? " (or the launch site's latitude)" : ""));
            Row("Then", (toMoon ? $"travel to {body.Name}, capture at {Km(target.Value)}, "
                         : launchAlt < target.Value - 1.0 ? $"transfer to {Km(target.Value)}, " : "") +
                        $"deploy {n} satellites, {Resonance.SpacingDeg(n):F0}° apart");
            DrawCoverage(vessel, body, body.Radius + target.Value, order, n);
            _continueAfterLaunch = GUILayout.Toggle(_continueAfterLaunch, " Continue with the deployment once in orbit");

            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && (!_settings.ChangeInclination || _inclinationValid);
            if (GUILayout.Button("Launch with MechJeb"))
            {
                mechJeb.Configure(launchAlt, inclination);
                mechJeb.Engage(this);
                _launch = mechJeb;
                Debug.Log($"[RelayOrbiter] Launching with MechJeb to {launchAlt / 1000.0:F1} km, " +
                          $"inclination {inclination:F1}°; continue afterwards: {_continueAfterLaunch}.");
            }
            GUI.enabled = wasEnabled;
            GUILayout.Label("MechJeb flies the ascent and stages; watch it like any MechJeb launch.");
        }

        private void DrawLaunching()
        {
            GUILayout.Label("Launching with MechJeb", _header);
            GUILayout.Label(_launch!.Status, _ok);
            if (_continueAfterLaunch) GUILayout.Label("The deployment starts on its own once in orbit.");
            if (GUILayout.Button("Abort launch")) StopLaunch("Launch aborted by the player.");
        }

        private void TickLaunch()
        {
            if (_launch == null) return;
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) { StopLaunch("Launch stopped: no active vessel."); return; }
            // MechJeb disengages itself once the orbit is circularized.
            if (vessel.situation == Vessel.Situations.ORBITING && !_launch.Enabled)
            {
                _launch = null;
                _autoStart = _continueAfterLaunch;
                Debug.Log($"[RelayOrbiter] In orbit after the launch: {vessel.orbit.ApA / 1000.0:F1} x " +
                          $"{vessel.orbit.PeA / 1000.0:F1} km, {vessel.orbit.inclination:F2}°.");
            }
        }

        private void StopLaunch(string why)
        {
            if (_launch == null) return;
            _launch.Disengage(this);
            _launch = null;
            _autoStart = false;
            Debug.Log("[RelayOrbiter] " + why);
        }

        private void DrawInclination(double current)
        {
            if (_inclinationText.Length == 0)
                _inclinationText = _settings.InclinationDeg.ToString("F1", CultureInfo.InvariantCulture);
            GUILayout.BeginHorizontal();
            _settings.ChangeInclination = GUILayout.Toggle(_settings.ChangeInclination, " Change inclination to",
                                                           GUILayout.Width(160f));
            _inclinationText = GUILayout.TextField(_inclinationText, GUILayout.Width(55f));
            GUILayout.Label($"°   (now {current:F2}°)");
            GUILayout.EndHorizontal();
            _inclinationValid = double.TryParse(_inclinationText, NumberStyles.Float, CultureInfo.InvariantCulture,
                                                out double deg) && deg >= 0.0 && deg <= 180.0;
            if (_inclinationValid) _settings.InclinationDeg = deg;
            else if (_settings.ChangeInclination) Warn("Inclination must be between 0 and 180°.");
        }

        private static string Km(double metres) =>
            (metres / 1000.0).ToString("N2", CultureInfo.InvariantCulture) + " km";

        private string ParkingDescription(Body body, double altitude, int n, ParkingOrbit parking)
        {
            if (parking == ParkingOrbit.Inner)
                return "INNER: below the target, released at apoapsis";
            var inner = _calculator.Calculate(body, altitude, n, ParkingOrbit.Inner);
            string why = !inner.SurfaceOk ? "underground" : "inside the atmosphere";
            return $"OUTER: above the target, released at periapsis (the inner orbit's periapsis would be {why})";
        }

        private void StartRun(Vessel vessel, List<SatelliteCandidate> order, double carrierBurn, double[] satBurns,
                              ParkingOrbit parking, double goalRadius, TransferSteps prelude, string[] names,
                              CelestialBody? destination)
        {
            var parts = order.Select(c => VesselParts.CommandPart(vessel, c)).ToArray();
            if (parts.Any(p => p == null))
            {
                Debug.Log("[RelayOrbiter] Could not find a satellite's command part.");
                return;
            }
            var state = new DeploymentState
            {
                Carrier          = vessel.persistentId,
                Separators       = order.Select(c => c.SeparatorId).ToArray(),
                CommandParts     = parts.Select(p => p!.flightID).ToArray(),
                SatelliteParts   = order.Select(c => c.PartIds.ToArray()).ToArray(),
                Names            = _settings.NameSatellites ? names : null,
                ExtendPanels     = _extendPanels,
                TopUp            = _topUp,
                Parking          = parking,
                GoalRadius       = goalRadius,
                GoalInclination  = _settings.ChangeInclination ? _settings.InclinationDeg : (double?)null,
                CarrierBurn      = carrierBurn,
                SatelliteBurns   = satBurns,
                Transfer         = prelude.Transfer,
                PlaneChange      = prelude.PlaneChange,
                PlaneChangeFirst = prelude.PlaneChangeFirst,
                Destination      = destination?.bodyName,
            };
            _runner = new DeploymentRunner(state, vessel, false, _planner, _transfer, _formatTime);
        }

        // ── Resuming an interrupted deployment ───────────────────────────────

        private void DrawResume(DeploymentState pending)
        {
            GUILayout.Label("Interrupted deployment", _header);
            int total = DeploymentSequence.Build(pending.CarrierBurn, pending.SatelliteBurns,
                new TransferSteps(pending.Transfer, pending.PlaneChange, pending.PlaneChangeFirst,
                                  pending.Destination != null)).Count;
            Vessel? carrier = null;
            foreach (var v in FlightGlobals.Vessels)
                if (v.persistentId == pending.Carrier) carrier = v;
            Row("Satellites", $"{pending.Separators.Length}, {pending.LastReleased + 1} released");
            Row("Stopped at", $"step {pending.Step + 1}/{total}");

            if (carrier == null)
                Warn("The carrier of that deployment no longer exists.");
            else if (!carrier.loaded)
                Warn($"The carrier ({carrier.vesselName}) is not loaded: switch to it, or near it, to resume.");
            else
            {
                GUILayout.Label("The game reloaded the flight scene. The deployment can go on from where it was.");
                if (GUILayout.Button("Resume deployment"))
                {
                    _runner = new DeploymentRunner(pending, carrier, true, _planner, _transfer, _formatTime);
                    RelayOrbiterScenario.Pending = null;
                }
            }
            if (GUILayout.Button("Discard it"))
            {
                Debug.Log("[RelayOrbiter] Interrupted deployment discarded.");
                RelayOrbiterScenario.Pending = null;
            }
        }

        private void DrawRunner()
        {
            var r = _runner!;
            GUILayout.Label($"Step {r.Step}/{r.Steps}: {r.CurrentStep}", _header);
            GUILayout.Label(r.Status, r.State == DeploymentRunner.RunState.Running ||
                                      r.State == DeploymentRunner.RunState.Done ? _ok : _warn);
            if (r.State == DeploymentRunner.RunState.Running)
            {
                bool wasEnabled = GUI.enabled;
                GUI.enabled = wasEnabled && r.CanFastForward;
                if (GUILayout.Button("Fast forward")) r.FastForward();
                GUI.enabled = wasEnabled;
                if (GUILayout.Button("Abort")) r.Abort();
            }
            else if (GUILayout.Button("Close"))
            {
                _runner = null;
                _order.Clear();
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void Warn(string text) => GUILayout.Label(text, _warn);

        private void Row(string label, string value, GUIStyle? style = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelW));
            GUILayout.Label(value, style ?? _ok);
            GUILayout.EndHorizontal();
        }

        private static string Ms(double v) => v.ToString("N1", CultureInfo.InvariantCulture) + " m/s";

        // Shared with every panel, from the skin.
        private void CreateStyles()
        {
            _ok     = RelayOrbiterSkin.Ok;
            _warn   = RelayOrbiterSkin.Warn;
            _header = RelayOrbiterSkin.Header;
        }
    }
}

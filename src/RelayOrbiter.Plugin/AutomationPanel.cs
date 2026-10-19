using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KSP.UI.Screens;
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
        private const double InterplanetaryCorrectionMargin = 50.0;  // m/s for each of the two
        private readonly InterplanetaryPlanner _interplanetary = new InterplanetaryPlanner(new OrbitalMechanics());
        private bool _useMechJeb;
        private readonly List<uint> _heldSatelliteParts = new List<uint>();  // engines held during a launch
        private readonly Func<double, string>     _formatTime;
        private string _inclinationText = "";
        private ILaunchAutopilot? _launch;        // a launch in progress (ours or MechJeb's)
        private string _launchKmText = "";
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
        public void Draw(Body current, Body selected, TransferRoute route, double? target, string? targetProblem)
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
            var moon = selected.Name == current.Name ? null : KspBody(selected.Name);  // the destination, when travelling
            if (selected.Name != current.Name && moon == null) { Warn($"{selected.Name} not found."); return; }
            if (route == TransferRoute.Unsupported) { Warn($"Relay Orbiter can't plan a trip to {selected.Name} from here."); return; }
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
            var travel = moon != null ? DrawTravel(vessel, current, route, moon, target.Value, rGoal) : null;
            if (travel != null) transfer = new TransferPlan(false, Apsis.Periapsis, rGoal, 0.0, 0.0, 0.0);
            Row("Altitude", Km(target.Value) + (transfer.Needed || moon != null ? "" : "  (already there)"));
            var walker = DrawDeployShape(vessel, selected, moon, n, rGoal);
            if (_deployShape == DeployOnePerPlane) return;
            if (walker == null && _deployShape != DeployRing) return;  // invalid Walker parameters
            if (walker == null) DrawInclination(moon != null ? 0.0 : orbit.inclination);
            // Around a moon the arrival plane isn't known yet: assume its equator.
            double startInc = moon != null ? 0.0 : orbit.inclination;
            bool planeChange = walker != null
                ? moon != null || PlaneMatch.AngleDeg(orbit, walker.Plane, Planetarium.GetUniversalTime()) >= PlaneChange.ToleranceDeg
                : _settings.ChangeInclination &&
                  (moon != null || PlaneChange.Needed(orbit.inclination, _settings.InclinationDeg));
            // Change the plane where the carrier is slowest: before lowering, after raising.
            bool planeFirst = moon == null && transfer.Needed && rGoal < orbit.semiMajorAxis;
            double vPlane = Math.Sqrt(selected.Mu / (planeFirst ? orbit.semiMajorAxis : rGoal));
            var pc = PlaneChange.DeltaV(vPlane, startInc, _settings.InclinationDeg, ascending: true);
            double planeDv = !planeChange ? 0.0
                : walker != null ? PlaneGrouping.PlaneChangeDv(vPlane, moon != null ? walker.Plane.InclinationDeg
                                                                      : PlaneMatch.AngleDeg(orbit, walker.Plane, Planetarium.GetUniversalTime()))
                : Math.Sqrt(pc.prograde * pc.prograde + pc.normal * pc.normal);
            _goalPlane = walker?.Plane;
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
            if (travel != null) transferTime = travel.Value.time;
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
                               (travel?.dv ?? 0.0);
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
            if ((planeChange || travel != null) && vessel.VesselValues.AutopilotSkill.value < ManeuverSasSkill &&
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
            Warp.Enabled = GUILayout.Toggle(Warp.Enabled, " Use time warp (off: you warp, burns still start on time)");

            // Names: callsign unique around this body, orbit tag, inclination.
            string callsign = walker?.Callsign ??
                              ConstellationNaming.Callsign(selected.Name, FlightGlobals.Vessels.Select(v => v.vesselName));
            string tag = ConstellationNaming.OrbitTag(target.Value, _altitudes.Synchronous(selected).Altitude);
            double finalInc = _settings.ChangeInclination ? _settings.InclinationDeg : orbit.inclination;
            // A Walker plane: numbered across the whole constellation, the shape in the tag.
            var names = walker != null
                ? Enumerable.Range(1, n).Select(k => $"{selected.Name} {callsign} {walker.PlaneIndex * n + k}/" +
                                                     $"{walker.Spec.Total} - {tag} {walker.Spec.Code()}").ToArray()
                : Enumerable.Range(1, n)
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
                              new TransferSteps(transfer.Needed || moon != null, planeChange, planeFirst,
                                                Routes.EndsAtMoon(route), Routes.Interplanetary(route)),
                              names, moon, route);
            }
            else if (_startRequested && Event.current.type == EventType.Repaint)
            {
                // Repaint is the last pass of the frame: the next Layout pass sees the runner.
                _startRequested = false;
                if (problems.Count == 0)
                    StartRun(vessel, order, carrierBurn, satBurns, parking, rGoal,
                             new TransferSteps(transfer.Needed || moon != null, planeChange, planeFirst,
                                                Routes.EndsAtMoon(route), Routes.Interplanetary(route)),
                             names, moon, route);
            }
            GUI.enabled = wasEnabled;
            GUILayout.Label("While it runs: don't touch the controls and don't switch vessels or leave the " +
                            $"flight scene; the mod switches to SAT-2 … SAT-{n} itself. Time warp is handled for you.",
                            _warn);
        }

        // ── Shapes: a Walker plane per carrier ───────────────────────────────

        private const int DeployRing = 0, DeployWalkerDelta = 1, DeployWalkerStar = 2, DeployOnePerPlane = 3;
        private int _deployShape, _wPlanes = 3, _wPlane, _wF = 1;
        private string _wIncText = "55";
        private OrbitPlane? _goalPlane;  // the Walker plane this carrier goes to

        private sealed class WalkerDeploy
        {
            public WalkerDeploy(ShapeSpec spec, int planeIndex, OrbitPlane plane, string callsign)
            {
                Spec = spec; PlaneIndex = planeIndex; Plane = plane; Callsign = callsign;
            }
            public ShapeSpec  Spec       { get; }
            public int        PlaneIndex { get; }  // 0-based
            public OrbitPlane Plane      { get; }
            public string     Callsign   { get; }
        }

        // Ring (one plane), a Walker plane, or shapes with one satellite per plane (which
        // a carrier can't deploy: launched one by one, then Maintenance puts each in its slot).
        private WalkerDeploy? DrawDeployShape(Vessel vessel, Body selected, CelestialBody? moon, int n, double rGoal)
        {
            _deployShape = Choice.Draw("Shape", _deployShape,
                new[] { "Ring", "Walker Δ plane", "Walker ★ plane", "Molniya, Tundra…" }, 80f);
            if (_deployShape == DeployRing) { _goalPlane = null; return null; }
            if (_deployShape == DeployOnePerPlane)
            {
                GUILayout.Label("Molniya, Tundra, Draim's tetrahedron and Trojans have one satellite per plane, too " +
                                "far apart for one carrier: launch each satellite on its own (or release them " +
                                "anywhere near), then in Maintenance pick the shape and \"Put … into its slot\".", _warn);
                return null;
            }

            bool star = _deployShape == DeployWalkerStar;
            if (_wIncText.Length == 0) _wIncText = star ? "88" : "55";
            GUILayout.BeginHorizontal();
            GUILayout.Label("Inclination [°]", GUILayout.Width(LabelW));
            _wIncText = GUILayout.TextField(_wIncText, GUILayout.Width(80f));
            GUILayout.EndHorizontal();
            Stepper("Planes", ref _wPlanes, 2, Math.Max(2, StationKeeping.MaxSlots / n), $"{_wPlanes * n} satellites in all");
            Stepper("This carrier deploys plane", ref _wPlane, 0, _wPlanes - 1, $"of {_wPlanes}", 1);
            Stepper("Phasing F", ref _wF, 0, _wPlanes - 1, "between planes");
            if (!double.TryParse(_wIncText, NumberStyles.Float, CultureInfo.InvariantCulture, out double inc) ||
                inc < 0.0 || inc > 180.0)
            {
                Warn("Enter an inclination between 0 and 180°.");
                return null;
            }
            var spec = star ? ConstellationShapes.WalkerStar(_wPlanes * n, _wPlanes, _wF, inc, rGoal)
                            : ConstellationShapes.WalkerDelta(_wPlanes * n, _wPlanes, _wF, inc, rGoal);
            double nodeOffset = _wPlane * (star ? 180.0 : 360.0) / _wPlanes;

            // The constellation may exist already (planes deployed before): its node and callsign.
            var around = moon ?? vessel.mainBody;
            var existing = FlightGlobals.Vessels
                .Where(v => v != null && v.mainBody == around && v.situation == Vessel.Situations.ORBITING &&
                            ConstellationName.Parse(v.vesselName) is { } p && p.Tag.Contains(spec.Code()))
                .ToList();
            double lan0;
            string callsign;
            if (existing.Count > 0 && existing.Count <= spec.Total)
            {
                double now = Planetarium.GetUniversalTime();
                var fit = ShapeFit.Fit(spec, existing.Select(v => new ShapeMember(v.orbit.inclination, v.orbit.LAN,
                                                                                  MeanLongitude.ArgLat(v.orbit, now))).ToList());
                lan0 = fit.Lan0Deg;
                callsign = ConstellationName.Parse(existing[0].vesselName)!.Callsign;
                Row("Constellation", $"{existing.Count} satellite(s) of {selected.Name} {callsign} already up; " +
                                     $"its plane 1 node Ω0 {lan0:F1}°");
            }
            else
            {
                // The first plane: wherever the carrier's own plane is (the others follow).
                lan0 = moon != null ? 0.0 : StationKeeping.Mod360(vessel.orbit.LAN - nodeOffset);
                callsign = ConstellationNaming.Callsign(selected.Name, FlightGlobals.Vessels.Select(v => v.vesselName));
                Row("Constellation", moon != null ? "new; its planes start from node 0°"
                                                  : $"new; plane {_wPlane + 1} takes the carrier's node, Ω0 {lan0:F1}°");
            }
            var plane = new OrbitPlane(inc, StationKeeping.Mod360(lan0 + nodeOffset));
            Row($"Plane {_wPlane + 1}", $"i {plane.InclinationDeg:F1}°, Ω {plane.LanDeg:F1}°, {n} satellites");
            GUILayout.Label("The planes' phasing (F) is set afterwards: in Maintenance, the shape " +
                            $"{(star ? "Walker ★" : "Walker Δ")} with these parameters puts every satellite in its slot.", _ok);
            return new WalkerDeploy(spec, _wPlane, plane, callsign);
        }

        private void Stepper(string label, ref int value, int min, int max, string note, int shown = 0)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelW));
            if (GUILayout.Button("−", GUILayout.Width(24f))) value--;
            value = Mathf.Clamp(value, min, max);
            GUILayout.Label((value + shown).ToString(CultureInfo.InvariantCulture), GUILayout.Width(24f));
            if (GUILayout.Button("+", GUILayout.Width(24f))) value++;
            value = Mathf.Clamp(value, min, max);
            GUILayout.Label(note);
            GUILayout.EndHorizontal();
        }

        // ── Travelling to another body ───────────────────────────────────────

        private static CelestialBody? KspBody(string name) =>
            FlightGlobals.Bodies.FirstOrDefault(b => GameBodyDatabase.NameOf(b) == name);

        // The trip's plan, shown as rows: (Δv the carrier needs, time until captured).
        private (double dv, double time)? DrawTravel(Vessel vessel, Body current, TransferRoute route, CelestialBody dest,
                                                     double targetAltitude, double rGoal)
        {
            var orbit = vessel.orbit;
            var here = vessel.mainBody;
            double now = Planetarium.GetUniversalTime(), dv = 0.0, time = 0.0;
            GUILayout.Label($"Trip to {GameBodyDatabase.NameOf(dest)}", _header);
            if (MechJebTransfer.Installed)
            {
                _useMechJeb = Choice.Draw("Transfer planner", _useMechJeb ? 1 : 0,
                                          new[] { "Relay Orbiter (recommended)", "MechJeb 2" }) == 1;
                if (_useMechJeb)
                    GUILayout.Label("MechJeb plans the ejection and the course corrections; Relay Orbiter flies them " +
                                    "and does the rest (falls back to its own planner if MechJeb can't).", _ok);
            }

            // Leg 1: to a sibling (Duna, or Minmus from the Mun), unless the destination is our moon.
            var first = route == TransferRoute.SiblingsMoon ? dest.referenceBody : dest;
            if (Routes.Interplanetary(route))
            {
                double capture = route == TransferRoute.SiblingsMoon ? ParkingRadius(first) : rGoal;
                var p = _interplanetary.Plan(here.referenceBody.gravParameter, here.orbit.semiMajorAxis,
                                             first.orbit.semiMajorAxis, here.gravParameter, orbit.semiMajorAxis,
                                             first.gravParameter, capture);
                double phase = MoonTransferPlanner.PhaseAngleDeg(MeanLongitude.At(here.orbit, now),
                                                                 MeanLongitude.At(first.orbit, now));
                double wait = MoonTransferPlanner.WaitTime(phase, p.PhaseAngleDeg, here.orbit.period, first.orbit.period);
                string name = GameBodyDatabase.NameOf(first);
                Row($"Window to {name}", $"in {_formatTime(wait)} (phase {phase:F0}°, needs {p.PhaseAngleDeg:F0}°)");
                Row("Cruise", _formatTime(p.TransferTime));
                Row("Ejection burn", $"{Ms(p.EjectionDv)}, fine-tuned with KSP's prediction");
                Row("Course corrections", $"two if needed, about {Ms(2 * InterplanetaryCorrectionMargin)} reserved");
                Row($"Capture around {name}", $"{Ms(Math.Abs(p.CaptureDv))} at {Km(capture - first.Radius)}" +
                                              (first.atmosphere ? " (no aerobraking)" : ""));
                dv += p.TotalDv + 2 * InterplanetaryCorrectionMargin;
                time += wait + p.TransferTime;
            }
            // Leg 2 (or the only one): to a moon of the body we are around by then.
            if (Routes.EndsAtMoon(route))
            {
                var around = route == TransferRoute.SiblingsMoon ? first : here;
                double r0 = route == TransferRoute.SiblingsMoon ? ParkingRadius(first) : orbit.semiMajorAxis;
                var m = _moonPlanner.Plan(around.gravParameter, r0, dest.orbit.semiMajorAxis, dest.gravParameter, rGoal);
                string name = GameBodyDatabase.NameOf(dest);
                if (route == TransferRoute.Moon)
                {
                    double phase = MoonTransferPlanner.PhaseAngleDeg(MeanLongitude.At(orbit, now), MeanLongitude.At(dest.orbit, now));
                    double wait = MoonTransferPlanner.WaitTime(phase, m.PhaseAngleDeg, orbit.period, dest.orbit.period);
                    Row($"Window to {name}", $"in {_formatTime(wait)} (phase {phase:F0}°, needs {m.PhaseAngleDeg:F0}°)");
                    time += wait;
                    double angle = PlaneGrouping.RelativeInclinationDeg(new OrbitPlane(orbit.inclination, orbit.LAN),
                                                                         new OrbitPlane(dest.orbit.inclination, dest.orbit.LAN));
                    if (angle >= 0.2)
                    {
                        double planeDv = PlaneGrouping.PlaneChangeDv(orbit.orbitalSpeed, angle);
                        Row($"Into {name}'s plane", $"{angle:F1}°, {Ms(planeDv)} first");
                        dv += planeDv;
                    }
                }
                else
                {
                    // Around the intermediate planet the plane isn't known yet: assume the worst
                    // plane change into the moon's orbit at the parking orbit.
                    double planeDv = PlaneGrouping.PlaneChangeDv(Math.Sqrt(first.gravParameter / r0),
                                                                 Math.Max(5.0, dest.orbit.inclination));
                    Row($"Then to {name}", $"into its plane (up to about {Ms(planeDv)}), then the transfer");
                    dv += planeDv;
                }
                Row($"Ejection to {name}", $"{Ms(Math.Abs(m.EjectionDv))}, {_formatTime(m.TransferTime)} on the way");
                Row("Course correction", $"if needed, about {Ms(MoonCorrectionMargin)} reserved");
                Row($"Capture at {Km(targetAltitude)}", $"{Ms(Math.Abs(m.CaptureDv))} retrograde at the periapsis");
                dv += m.TotalDv + MoonCorrectionMargin;
                time += m.TransferTime;
            }
            if (orbit.eccentricity > 0.05)
                Warn("The orbit is not circular: the window estimate is rough (the ejection is fine-tuned anyway).");
            return (dv, time);
        }

        // A low orbit to stop at on the way (Duna before Ike): as for a launch.
        private static double ParkingRadius(CelestialBody b) =>
            b.Radius + (b.atmosphere ? b.atmosphereDepth + LaunchPlanner.AboveAtmosphere
                                     : Math.Max(LaunchPlanner.MinAirless, LaunchPlanner.AirlessFraction * b.Radius));

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
        // Relay Orbiter's own ascent as a choice next to MechJeb's (off: MechJeb only).
        private static readonly bool OwnAscentOffered = false;

        private void DrawLaunch(Vessel vessel, Body current, Body body, double? target, string? targetProblem, int n,
                                List<SatelliteCandidate> order)
        {
            GUILayout.Label("Launch to orbit", _header);
            if (!target.HasValue)
            {
                Warn((targetProblem ?? "Choose a valid target altitude above.") +
                     " Pick another one above (Synchronous, At a moon, Midpoint or Custom).");
                return;
            }
            // Who flies the ascent: MechJeb 2, Relay Orbiter staging. Relay Orbiter's own ascent
            // (AscentAutopilot) is set aside for now, not offered: KSP's SAS, which steers it,
            // let a SRB-heavy rocket tumble at max-q that MechJeb flew fine.
            var mechJeb = MechJebAscent.Installed ? MechJebAscent.For(vessel) : null;
            if (OwnAscentOffered && mechJeb != null)
            {
                _settings.MechJebAscent = Choice.Draw("Ascent autopilot", _settings.MechJebAscent ? 1 : 0,
                    new[] { "Relay Orbiter", "MechJeb 2" }) == 1;
            }
            if (!OwnAscentOffered && mechJeb == null)
            {
                Warn(MechJebAscent.Installed
                    ? "Launching needs a MechJeb core on this vessel (a MechJeb part, or the MechJeb module on its command part)."
                    : "Launching from the pad needs MechJeb 2 installed: it flies the ascent, Relay Orbiter stages.");
                return;
            }
            bool useMechJeb = mechJeb != null && (!OwnAscentOffered || _settings.MechJebAscent);

            bool toMoon = body.Name != current.Name;
            // Travelling on: a low parking orbit around the launch body.
            double autoAlt = toMoon ? LaunchPlanner.ParkingAltitude(current) : LaunchPlanner.LaunchAltitude(body, target.Value);
            double? chosen = DrawLaunchAltitude(current, autoAlt);
            if (chosen == null) return;
            double launchAlt = chosen.Value;
            DrawInclination(Math.Abs(vessel.latitude));
            // Travelling to a moon: launch into its orbital plane's inclination (the node is
            // matched in orbit); the target inclination applies around the moon.
            var moonBody = toMoon ? KspBody(body.Name) : null;
            // A Walker plane: straight to its inclination (its node is matched in orbit).
            var walker = DrawDeployShape(vessel, body, moonBody, n, body.Radius + target.Value);
            if (_deployShape == DeployOnePerPlane || _deployShape != DeployRing && walker == null) return;
            // A moon of the launch body: its inclination. Elsewhere (a planet): equatorial.
            double inclination = walker != null && !toMoon ? walker.Plane.InclinationDeg
                               : moonBody != null && moonBody.referenceBody == vessel.mainBody ? moonBody.orbit.inclination
                               : moonBody != null ? 0.0
                               : _settings.ChangeInclination ? _settings.InclinationDeg : 0.0;
            Row("Orbit after the launch", $"{Km(launchAlt)} circular, inclination {inclination:F1}°" +
                             (inclination < Math.Abs(vessel.latitude) ? " (or the launch site's latitude)" : ""));
            Row("Then", (toMoon ? $"travel to {body.Name}, capture at {Km(target.Value)}, "
                         : launchAlt < target.Value - 1.0 ? $"transfer to {Km(target.Value)}, " : "") +
                        $"deploy {n} satellites, {Resonance.SpacingDeg(n):F0}° apart");
            DrawCoverage(vessel, body, body.Radius + target.Value, order, n);
            _continueAfterLaunch = GUILayout.Toggle(_continueAfterLaunch, " Continue with the deployment once in orbit");
            Warp.Enabled = GUILayout.Toggle(Warp.Enabled, " Use time warp (to the circularization, and later waits)");

            // MechJeb stops autostaging above the satellites' stages: the carrier's own engines
            // must all fire before that.
            int stageLimit = SatelliteStageLimit(vessel, order);
            var satIds = new HashSet<uint>(order.SelectMany(c => c.PartIds).Concat(order.Select(c => c.SeparatorId)));
            int carrierLast = vessel.FindPartModulesImplementing<ModuleEngines>()
                .Where(e => !satIds.Contains(e.part.flightID)).Select(e => e.part.inverseStage)
                .DefaultIfEmpty(int.MaxValue).Min();
            bool stagingOk = carrierLast >= stageLimit;
            if (!stagingOk)
                Warn($"The carrier has an engine in stage {carrierLast}, after a satellite's stage: the launch stops " +
                     $"staging at stage {stageLimit} so it never fires the satellites, and would never light that " +
                     "engine. Move the satellites' separators and engines to the last stages.");
            else
                Row("Staging during the ascent", $"Relay Orbiter's, down to stage {stageLimit} (satellites below)");

            // The carrier's own stages must reach orbit: the satellites' Δv isn't available.
            double needed = LaunchPlanner.AscentDeltaV(current, launchAlt, inclination);
            double carrierDv = CarrierAscentDeltaV(vessel, stageLimit);
            if (!double.IsNaN(carrierDv))
            {
                Row("Carrier's Δv to orbit", $"{Ms(carrierDv)} in stages {stageLimit} and up (needs about {Ms(needed)})",
                    carrierDv >= needed ? _ok : _warn);
                if (carrierDv < needed)
                    Warn("The carrier's own stages may not reach orbit: the satellites' fuel can't be used for the ascent. " +
                         "Add fuel to the carrier, or launch to a lower inclination (a retrograde one costs most).");
            }

            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && stagingOk && (!_settings.ChangeInclination || _inclinationValid);
            if (GUILayout.Button(useMechJeb ? "Launch with MechJeb" : "Launch"))
            {
                var protect = order.SelectMany(c => c.PartIds).Concat(order.Select(c => c.SeparatorId)).ToList();
                var staging = new LaunchStaging(protect);
                // Whatever fires, the satellites' engines stay off during the ascent.
                _heldSatelliteParts.Clear();
                _heldSatelliteParts.AddRange(order.SelectMany(c => c.PartIds));
                int held = VesselParts.HoldEngines(vessel, _heldSatelliteParts, true);
                Debug.Log($"[RelayOrbiter] Holding {held} satellite engine(s) off during the ascent; staging stops at " +
                          $"stage {stageLimit}.");
                if (useMechJeb)
                {
                    // MechJeb flies, Relay Orbiter stages: MechJeb's autostaging went through the
                    // satellites' stages even with its staging limit set.
                    mechJeb!.Configure(launchAlt, inclination, autostage: false);
                    mechJeb.LimitStaging(stageLimit, this);
                    mechJeb.Engage(this);
                    _launch = new MechJebLaunch(mechJeb, staging, this);
                }
                else
                    _launch = new AscentAutopilot(vessel, launchAlt, inclination, protect, staging, _planner, _formatTime);
                Debug.Log($"[RelayOrbiter] Launching ({_launch.Name}) to {launchAlt / 1000.0:F1} km, " +
                          $"inclination {inclination:F1}°; continue afterwards: {_continueAfterLaunch}.");
                RoLog.Dbg($"Launch settings: constellation inclination {(_settings.ChangeInclination ? $"{_settings.InclinationDeg:F1}°" : "unchanged")}, " +
                          $"latitude {vessel.latitude:F3}°, {protect.Count} protected part(s), stage limit {stageLimit}, " +
                          $"MechJeb ascent {useMechJeb}; craft {RoLog.Propulsion(vessel)}.");
#if DEBUG
                for (int stage = VesselParts.CurrentStage(vessel) - 1; stage >= 0; --stage)
                {
                    int s = stage;
                    var ids = vessel.parts.Where(p => p.inverseStage == s).Select(p => p.flightID).ToList();
                    if (ids.Count > 0)
                        RoLog.Dbg($"Launch staging plan: stage {s}: {VesselParts.DescribeParts(vessel, ids)}" +
                                  (ids.Any(protect.Contains) ? " (satellite parts: never fired by Relay Orbiter)" : "") + ".");
                }
#endif
            }
            GUI.enabled = wasEnabled;
            GUILayout.Label(useMechJeb ? "MechJeb flies the ascent, Relay Orbiter stages."
                                       : "Relay Orbiter flies the ascent: a gravity turn, then the circularization.");
        }

        // The orbit to launch to: automatic (a low orbit, or the target when lower), or typed in.
        private double? DrawLaunchAltitude(Body current, double autoAlt)
        {
            bool custom = !double.IsNaN(_settings.LaunchAltitudeKm);
            int mode = Choice.Draw("Launch to", custom ? 1 : 0, new[] { $"Auto ({autoAlt / 1000.0:F0} km)", "Custom" });
            if (mode == 0) { _settings.LaunchAltitudeKm = double.NaN; return autoAlt; }
            if (!custom)
            {
                _settings.LaunchAltitudeKm = autoAlt / 1000.0;
                _launchKmText = _settings.LaunchAltitudeKm.ToString("F0", CultureInfo.InvariantCulture);
            }
            if (_launchKmText.Length == 0)
                _launchKmText = _settings.LaunchAltitudeKm.ToString("F0", CultureInfo.InvariantCulture);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Launch altitude [km]", GUILayout.Width(LabelW));
            _launchKmText = GUILayout.TextField(_launchKmText, GUILayout.Width(100f));
            GUILayout.EndHorizontal();
            double min = (current.Atm > 0.0 ? current.Atm : 0.0) + LaunchMargin;
            double max = current.Soi.HasValue ? current.Soi.Value - current.Radius : double.MaxValue;
            if (!double.TryParse(_launchKmText, NumberStyles.Float, CultureInfo.InvariantCulture, out double km) ||
                km * 1000.0 < min || km * 1000.0 >= max)
            {
                Warn($"Enter a launch altitude above {Km(min)}" + (current.Soi.HasValue ? $" and below {Km(max)}." : "."));
                return null;
            }
            _settings.LaunchAltitudeKm = km;
            return km * 1000.0;
        }

        private const double LaunchMargin = 5_000.0;  // m above the atmosphere, at least

        // KSP's own Δv for the stages from `lowest` up: the first to fire at sea level, the
        // others in vacuum. NaN until KSP has computed it.
        private static double CarrierAscentDeltaV(Vessel vessel, int lowest)
        {
            var dv = vessel.VesselDeltaV;
            if (dv == null || !dv.IsReady || dv.OperatingStageInfo == null) return double.NaN;
            var stages = dv.OperatingStageInfo.Where(st => st.stage >= lowest).ToList();
            if (stages.Count == 0) return double.NaN;
            int first = stages.Max(st => st.stage);
            return stages.Sum(st => st.stage == first ? st.deltaVatASL : st.deltaVinVac);
        }

        // One above the highest stage holding a satellite's part or separator.
        private static int SatelliteStageLimit(Vessel vessel, List<SatelliteCandidate> order)
        {
            var ids = new HashSet<uint>(order.SelectMany(c => c.PartIds).Concat(order.Select(c => c.SeparatorId)));
            int highest = vessel.parts.Where(p => ids.Contains(p.flightID)).Select(p => p.inverseStage)
                                .DefaultIfEmpty(-1).Max();
            return highest + 1;
        }

        private void DrawLaunching()
        {
            GUILayout.Label($"Launching ({_launch!.Name})", _header);
            GUILayout.Label(_launch.Status, _ok);
            if (_continueAfterLaunch) GUILayout.Label("The deployment starts on its own once in orbit.");
            if (GUILayout.Button("Abort launch")) StopLaunch("Launch aborted by the player.");
        }

        private void TickLaunch()
        {
            if (_launch == null) return;
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) { StopLaunch("Launch stopped: no active vessel."); return; }
            // A held satellite engine lit anyway (a mod ignoring the disabled module): off again.
            int lit = VesselParts.ShutHeldEngines(vessel, _heldSatelliteParts);
            if (lit > 0) Debug.Log($"[RelayOrbiter] Shut down {lit} satellite engine(s) lit during the ascent.");
            _launch.Tick(vessel);
            if (_launch.Error != null) { StopLaunch("Launch stopped: " + _launch.Error); return; }
            if (_launch.InOrbit)
            {
                _launch.Stop();  // MechJeb: lifts the staging limit, gives its autostage setting back
                _launch = null;
                _heldSatelliteParts.Clear();  // still held: the deployment frees each one at its release
                _autoStart = _continueAfterLaunch;
                Debug.Log($"[RelayOrbiter] In orbit after the launch: {vessel.orbit.ApA / 1000.0:F1} x " +
                          $"{vessel.orbit.PeA / 1000.0:F1} km, {vessel.orbit.inclination:F2}°.");
            }
        }

        private void StopLaunch(string why)
        {
            if (_launch == null) return;
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel != null && _heldSatelliteParts.Count > 0)
                VesselParts.HoldEngines(vessel, _heldSatelliteParts, false);  // aborted: give them back
            _heldSatelliteParts.Clear();
            _launch.Stop();
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
                              CelestialBody? destination, TransferRoute route)
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
                GoalInclination  = _goalPlane?.InclinationDeg ??
                                   (_settings.ChangeInclination ? _settings.InclinationDeg : (double?)null),
                GoalLan          = _goalPlane?.LanDeg,
                CarrierBurn      = carrierBurn,
                SatelliteBurns   = satBurns,
                Transfer         = prelude.Transfer,
                PlaneChange      = prelude.PlaneChange,
                PlaneChangeFirst = prelude.PlaneChangeFirst,
                Destination      = destination?.bodyName,
                Via              = route == TransferRoute.SiblingsMoon ? destination?.referenceBody.bodyName : null,
                Interplanetary   = Routes.Interplanetary(route),
                UseMechJeb       = _useMechJeb && MechJebTransfer.Installed,
            };
            _runner = new DeploymentRunner(state, vessel, false, _planner, _transfer, _formatTime);
        }

        // ── Resuming an interrupted deployment ───────────────────────────────

        private void DrawResume(DeploymentState pending)
        {
            GUILayout.Label("Interrupted deployment", _header);
            int total = DeploymentSequence.Build(pending.CarrierBurn, pending.SatelliteBurns,
                pending.Prelude).Count;
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
                // Its satellites still on board had their engines held: give them back.
                var aboard = FlightGlobals.Vessels.FirstOrDefault(v => v != null && v.persistentId == pending.Carrier);
                if (aboard != null && aboard.loaded)
                    VesselParts.HoldEngines(aboard, pending.SatelliteParts.SelectMany(p => p), false);
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

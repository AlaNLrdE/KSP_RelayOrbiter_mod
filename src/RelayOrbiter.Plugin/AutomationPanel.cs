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

        private readonly IDeploymentPlanner       _planner;
        private readonly IAltitudePlanner         _altitudes;
        private readonly IConstellationCalculator _calculator;
        private readonly ConstellationSettings    _settings;
        private readonly Func<double, string>     _formatTime;

        private readonly List<uint> _order = new List<uint>();  // separator flight ids
        private uint _orderVessel;
        private bool _extendPanels = true;
        private bool _topUp = true;
        private bool _startRequested;  // start on the next frame, after the top-up
        private DeploymentRunner? _runner;
        private GUIStyle? _ok, _warn, _header;

        public AutomationPanel(IDeploymentPlanner planner, IAltitudePlanner altitudes,
                               IConstellationCalculator calculator, ConstellationSettings settings,
                               Func<double, string> formatTime)
        {
            _planner    = planner;
            _altitudes  = altitudes;
            _calculator = calculator;
            _settings   = settings;
            _formatTime = formatTime;
        }

        public bool IsRunning => _runner != null && _runner.State == DeploymentRunner.RunState.Running;

        public void Tick()  => _runner?.Tick();
        public void Abort() => _runner?.Abort();

        public void Draw(Body selected)
        {
            if (_ok == null) CreateStyles();
            if (_runner != null) { DrawRunner(); return; }

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
                Warn("No satellites found: each must hang on a decoupler or a docking-port pair and have a probe core.");
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
            var releases = tree.PlanReleases(order, n);
            if (!releases.IsValid)
            {
                Warn(candidates.Count < n
                    ? $"{n} satellites need {n} probe cores on separators; this vessel has {candidates.Count}."
                    : releases.Error!);
                return;
            }

            // The vessel's checks come after the satellites', so they show on the launch pad too.
            if (vessel.situation != Vessel.Situations.ORBITING) { Warn("Get into a stable orbit first."); return; }
            if (_altitudes.Validate(selected, orbit.ApA) != AltitudeStatus.Ok) { Warn("The apoapsis is not usable."); return; }
            var solver = vessel.patchedConicSolver;
            if (solver == null) { Warn("Maneuver nodes are not available yet."); return; }
            if (solver.maneuverNodes.Count > 0) { Warn("Delete the vessel's maneuver nodes first (Maneuvers tab)."); return; }

            // Inner parking orbit (released at apoapsis) when its periapsis is safe, else outer (periapsis).
            var parking = _calculator.Recommend(selected, orbit.ApA, n);
            string point = ReleasePoint.Name(parking);
            double now  = Planetarium.GetUniversalTime();
            double ut   = _planner.NextPassUT(now, ReleasePoint.TimeTo(orbit, parking), orbit.period, 60.0);
            var plan    = _planner.Plan(selected.Mu, ReleasePoint.Radius(orbit, parking), orbit.semiMajorAxis, ut, n, parking);
            double carrierBurn = BurnTiming.Duration(plan.ParkingDeltaV, releases.CarrierMassAfterFirst,
                                                     releases.CarrierThrust, releases.CarrierIsp) ?? 0.0;
            var satBurns = releases.Satellites.Skip(1)
                .Select(s => BurnTiming.Duration(plan.SatelliteCircularizeDeltaV, s.Mass, s.Thrust, s.Isp) ?? 0.0)
                .ToArray();
            string satDir = ReleasePoint.Direction(plan.SatelliteCircularizeDeltaV);

            GUILayout.Label($"Plan: {n} satellites, {Resonance.SpacingDeg(n):F0}° apart", _header);
            Row("Parking orbit", ParkingDescription(selected, orbit.ApA, n, parking));
            Row($"SAT-1 {releases.Satellites[0].Candidate.Name}",
                "released before the carrier's burn; stays on this circular orbit, no burn of its own");
            Row("Carrier parking burn",
                $"{Ms(Math.Abs(plan.ParkingDeltaV))} {ReleasePoint.Direction(plan.ParkingDeltaV)}, {carrierBurn:F0} s");
            for (int k = 1; k < n; ++k)
                Row($"SAT-{k + 1} {releases.Satellites[k].Candidate.Name}",
                    $"{Ms(Math.Abs(plan.SatelliteCircularizeDeltaV))} {satDir}, {satBurns[k - 1]:F0} s");
            Row("Total time", "about " + _formatTime((n - 1) * plan.ParkingPeriod + (ut - now)));

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
            if (_topUp && ResourceTopUp.Shortfall(satTanks, transfers) > 1e-3)
                Warn("The carrier doesn't have enough fuel to fill every satellite; they get what is left.");

            var problems = new List<string>();
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

            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && problems.Count == 0;
            if (GUILayout.Button("Start deployment") && !_startRequested)
            {
                if (_topUp && transfers.Count > 0)
                {
                    // Fill the tanks, then start next frame so the burn estimates use the new masses.
                    VesselParts.Apply(vessel, transfers);
                    Debug.Log("[RelayOrbiter] Topped up the satellites: " + string.Join(", ",
                        transfers.GroupBy(t => t.Resource).Select(g => $"{g.Key} +{g.Sum(t => t.Amount):F1}")));
                    _startRequested = true;
                }
                else StartRun(vessel, order, carrierBurn, satBurns, parking);
            }
            else if (_startRequested && Event.current.type == EventType.Repaint)
            {
                // Repaint is the last pass of the frame: the next Layout pass sees the runner.
                _startRequested = false;
                if (problems.Count == 0) StartRun(vessel, order, carrierBurn, satBurns, parking);
            }
            GUI.enabled = wasEnabled;
            GUILayout.Label("While it runs: don't touch the controls and don't switch vessels or leave the " +
                            $"flight scene; the mod switches to SAT-2 … SAT-{n} itself. Time warp is handled for you.",
                            _warn);
        }

        private string ParkingDescription(Body body, double altitude, int n, ParkingOrbit parking)
        {
            if (parking == ParkingOrbit.Inner)
                return "INNER: below the target, released at apoapsis";
            var inner = _calculator.Calculate(body, altitude, n, ParkingOrbit.Inner);
            string why = !inner.SurfaceOk ? "underground" : "inside the atmosphere";
            return $"OUTER: above the target, released at periapsis (the inner orbit's periapsis would be {why})";
        }

        private void StartRun(Vessel vessel, List<SatelliteCandidate> order, double carrierBurn, double[] satBurns,
                              ParkingOrbit parking)
        {
            var parts = order.Select(c => VesselParts.CommandPart(vessel, c)).ToArray();
            if (parts.Any(p => p == null))
            {
                Debug.Log("[RelayOrbiter] Could not find a satellite's command part.");
                return;
            }
            _runner = new DeploymentRunner(
                DeploymentSequence.Build(carrierBurn, satBurns), vessel,
                order.Select(c => c.SeparatorId).ToArray(), parts!,
                order.Select(c => c.Name).ToArray(),
                order.Select(c => c.PartIds.ToArray()).ToArray(), _extendPanels, _topUp, parking,
                _planner, _formatTime);
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

        private void Row(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelW));
            GUILayout.Label(value, _ok);
            GUILayout.EndHorizontal();
        }

        private static string Ms(double v) => v.ToString("N1", CultureInfo.InvariantCulture) + " m/s";

        private void CreateStyles()
        {
            _ok     = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(0.55f, 0.95f, 0.55f) }, wordWrap = true };
            _warn   = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(1.00f, 0.75f, 0.30f) }, wordWrap = true };
            _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        }
    }
}

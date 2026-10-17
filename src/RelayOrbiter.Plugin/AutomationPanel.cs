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

        private readonly IDeploymentPlanner _planner;
        private readonly IAltitudePlanner _altitudes;
        private readonly Func<double, string> _formatTime;

        private readonly List<uint> _order = new List<uint>();  // separator flight ids
        private uint _orderVessel;
        private bool _extendPanels = true;
        private DeploymentRunner? _runner;
        private GUIStyle? _ok, _warn, _header;

        public AutomationPanel(IDeploymentPlanner planner, IAltitudePlanner altitudes,
                               Func<double, string> formatTime)
        {
            _planner    = planner;
            _altitudes  = altitudes;
            _formatTime = formatTime;
        }

        public bool IsRunning => _runner != null && _runner.State == DeploymentRunner.RunState.Running;

        public void Tick()  => _runner?.Tick();
        public void Abort() => _runner?.Abort();

        public void Draw(Body selected, Action<string> selectBody)
        {
            if (_ok == null) CreateStyles();
            if (_runner != null) { DrawRunner(); return; }

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) { Warn("No active vessel."); return; }
            var orbit = vessel.orbit;
            string bodyName = GameBodyDatabase.NameOf(orbit.referenceBody);
            GUILayout.Label($"Carrier: {vessel.vesselName} — orbiting {bodyName}", _header);

            if (bodyName != selected.Name)
            {
                Warn($"The vessel is orbiting {bodyName}, but {selected.Name} is selected.");
                if (GUILayout.Button($"Select {bodyName}")) selectBody(bodyName);
                return;
            }
            if (vessel.situation != Vessel.Situations.ORBITING) { Warn("Get into a stable orbit first."); return; }
            if (_altitudes.Validate(selected, orbit.ApA) != AltitudeStatus.Ok) { Warn("The apoapsis is not usable."); return; }
            var solver = vessel.patchedConicSolver;
            if (solver == null) { Warn("Maneuver nodes are not available yet."); return; }
            if (solver.maneuverNodes.Count > 0) { Warn("Delete the vessel's maneuver nodes first (Maneuvers tab)."); return; }

            bool newVessel = _orderVessel != vessel.persistentId;
            if (newVessel) { _order.Clear(); _orderVessel = vessel.persistentId; }

            var tree = new PartTree(VesselParts.From(vessel));
            var candidates = tree.Candidates();
            if (newVessel)
                Debug.Log($"[RelayOrbiter] {vessel.vesselName}: {candidates.Count} satellite(s) found " +
                          $"[{string.Join(", ", candidates.Select(c => c.Name + " via " + c.SeparatorTitle))}]. " +
                          $"Separators: {VesselParts.DescribeSeparators(vessel)}");
            _order.RemoveAll(id => candidates.All(c => c.SeparatorId != id));

            GUILayout.Label("Satellites (click in release order)", _header);
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
            var releases = tree.PlanReleases(order);
            if (!releases.IsValid) { Warn(releases.Error!); return; }

            double now  = Planetarium.GetUniversalTime();
            double ap   = _planner.NextApoapsisUT(now, orbit.timeToAp, orbit.period, 60.0);
            var plan    = _planner.Plan(selected.Mu, orbit.ApR, orbit.semiMajorAxis, ap);
            double hPeri = plan.PeriapsisRadius - selected.Radius;
            double carrierBurn = BurnTiming.Duration(plan.ParkingDeltaV, releases.CarrierMassAfterFirst,
                                                     releases.CarrierThrust, releases.CarrierIsp) ?? 0.0;
            var satBurns = releases.Satellites.Skip(1)
                .Select(s => BurnTiming.Duration(plan.SatelliteCircularizeDeltaV, s.Mass, s.Thrust, s.Isp) ?? 0.0)
                .ToArray();

            GUILayout.Label("Plan", _header);
            Row($"SAT-1 {releases.Satellites[0].Candidate.Name}",
                "released before the carrier's burn; stays on this circular orbit, no burn of its own");
            Row("Carrier parking burn", $"{Ms(-plan.ParkingDeltaV)} retrograde, {carrierBurn:F0} s");
            for (int k = 1; k < 3; ++k)
                Row($"SAT-{k + 1} {releases.Satellites[k].Candidate.Name}",
                    $"{Ms(plan.SatelliteCircularizeDeltaV)} prograde, {satBurns[k - 1]:F0} s");
            Row("Total time", "about " + _formatTime(2.0 * plan.ParkingPeriod + orbit.timeToAp));

            var problems = new List<string>();
            if (hPeri <= 0.0 || hPeri < selected.Atm)
                problems.Add("The parking orbit's periapsis would be too low. Raise the apoapsis.");
            for (int k = 0; k < 3; ++k)
            {
                var s = releases.Satellites[k];
                if (k > 0 && VesselParts.SasLevel(vessel, s.Candidate.PartIds) < 1)
                    problems.Add($"SAT-{k + 1} has no probe core with SAS (level 1+) to point its burn.");
                if (k > 0 && plan.SatelliteCircularizeDeltaV * satBurns[k - 1] / 2.0 > MaxSeparation)
                    problems.Add($"SAT-{k + 1}'s burn is too long ({satBurns[k - 1]:F0} s): it would leave " +
                                 "the carrier's loading range. Use a stronger engine.");
            }
            foreach (var p in problems) Warn(p);

            _extendPanels = GUILayout.Toggle(_extendPanels, " Extend the satellites' solar panels before release");

            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && problems.Count == 0;
            if (GUILayout.Button("Start deployment"))
            {
                var parts = order.Select(c => VesselParts.CommandPart(vessel, c)).ToArray();
                if (parts.Any(p => p == null)) Warn("Could not find a satellite's command part.");
                else
                    _runner = new DeploymentRunner(
                        DeploymentSequence.Build(carrierBurn, satBurns), vessel,
                        order.Select(c => c.SeparatorId).ToArray(), parts!,
                        order.Select(c => c.Name).ToArray(),
                        order.Select(c => c.PartIds.ToArray()).ToArray(), _extendPanels,
                        _planner, _formatTime);
            }
            GUI.enabled = wasEnabled;
            GUILayout.Label("While it runs: don't touch the controls and don't switch vessels or leave the " +
                            "flight scene; the mod switches to SAT-2 and SAT-3 itself. Time warp is handled for you.",
                            _warn);
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

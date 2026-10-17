using System;
using System.Globalization;
using System.Linq;
using RelayOrbiter.Core;
using UnityEngine;
using Body = RelayOrbiter.Core.CelestialBody;

namespace RelayOrbiter.Plugin
{
    // Flight-only panel: plans from the active vessel's real orbit and turns the
    // plan into maneuver nodes and stock Alarm Clock alarms.
    internal sealed class ManeuversPanel
    {
        private const double MinLead   = 60.0;  // s: never place a node closer than this
        private const double AlarmLead = 60.0;  // s: alarms fire this long before each release
        private const float  LabelW    = 230f;

        private readonly IDeploymentPlanner _planner;
        private readonly IAltitudePlanner   _altitudes;
        private readonly Func<double, string> _formatTime;

        private bool _addAlarms = true;
        private DeploymentPlan? _lastPlan;
        private uint _lastPlanVessel;
        private string _message = "";

        private GUIStyle? _ok, _warn, _header;

        public ManeuversPanel(IDeploymentPlanner planner, IAltitudePlanner altitudes,
                              Func<double, string> formatTime)
        {
            _planner    = planner;
            _altitudes  = altitudes;
            _formatTime = formatTime;
        }

        // selected: body chosen in the window; target: its valid target altitude, if any.
        public void Draw(Body selected, double? target, Action<string> selectBody)
        {
            if (_ok == null) CreateStyles();

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) { Warn("No active vessel."); return; }

            var orbit = vessel.orbit;
            string bodyName = GameBodyDatabase.NameOf(orbit.referenceBody);
            GUILayout.Label($"{vessel.vesselName} — orbiting {bodyName}", _header);

            if (bodyName != selected.Name)
            {
                Warn($"The vessel is orbiting {bodyName}, but {selected.Name} is selected.");
                if (GUILayout.Button($"Select {bodyName}")) selectBody(bodyName);
                return;
            }
            if (vessel.situation != Vessel.Situations.ORBITING)
            {
                Warn("Get into a stable orbit first (periapsis above the atmosphere, not escaping).");
                return;
            }

            Row("Apoapsis",  Km(orbit.ApA));
            Row("Periapsis", Km(orbit.PeA));
            Row("Period",    _formatTime(orbit.period));

            var apStatus = _altitudes.Validate(selected, orbit.ApA);
            if (apStatus != AltitudeStatus.Ok)
            {
                Warn($"Apoapsis is not usable ({apStatus}).");
                return;
            }
            if (target.HasValue && Math.Abs(orbit.ApA - target.Value) > Math.Max(0.01 * target.Value, 1_000.0))
                Warn($"Apoapsis differs from the selected target ({Km(target.Value)}). " +
                     "The constellation forms at the apoapsis.");

            double now    = Planetarium.GetUniversalTime();
            double nextAp = _planner.NextApoapsisUT(now, orbit.timeToAp, orbit.period, MinLead);
            var plan      = _planner.Plan(selected.Mu, orbit.ApR, orbit.semiMajorAxis, nextAp);
            double hPeri  = plan.PeriapsisRadius - selected.Radius;
            bool periOk   = hPeri > 0.0 && hPeri >= selected.Atm;

            GUILayout.Label("Parking orbit burn (carrier, at next apoapsis)", _header);
            Row("Burn in",            _formatTime(nextAp - now));
            Row("Δv (retrograde)",    Ms(-plan.ParkingDeltaV));
            Row("Resulting periapsis", Km(hPeri), periOk ? _ok : _warn);
            Row("Parking period",     _formatTime(plan.ParkingPeriod));
            Row("Each satellite: circularize", Ms(plan.SatelliteCircularizeDeltaV) + " prograde");
            if (!periOk)
                Warn(hPeri <= 0.0 ? "Periapsis would be underground. Raise the apoapsis."
                                  : "Periapsis would be inside the atmosphere. Raise the apoapsis.");

            var solver = vessel.patchedConicSolver;
            if (solver == null)
            {
                Warn("Maneuver nodes are not available yet (upgrade Mission Control and the Tracking Station).");
                return;
            }

            int existing = solver.maneuverNodes.Count;
            if (existing > 0)
            {
                Warn($"The vessel already has {existing} maneuver node(s).");
                if (GUILayout.Button($"Delete {existing} existing node(s)"))
                {
                    foreach (var node in solver.maneuverNodes.ToList()) node.RemoveSelf();
                    _message = "Existing nodes deleted.";
                }
            }

            _addAlarms = GUILayout.Toggle(_addAlarms, " Add release alarms to the Alarm Clock");

            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && existing == 0 && periOk;
            if (GUILayout.Button("Create parking-orbit node"))
            {
                CreateNode(solver, plan.BurnUT, plan.ParkingDeltaV);
                _lastPlan = plan;
                _lastPlanVessel = vessel.persistentId;
                int alarms = _addAlarms ? AddReleaseAlarms(vessel, plan) : 0;
                _message = $"Parking-orbit node created." +
                           (_addAlarms ? $" {alarms} release alarm(s) added." : "");
            }
            GUI.enabled = wasEnabled && existing == 0;
            if (GUILayout.Button("Circularize at next apoapsis"))
            {
                double dv = _planner.CircularizeAtApoapsis(selected.Mu, orbit.ApR, orbit.semiMajorAxis);
                CreateNode(solver, nextAp, dv);
                _message = $"Circularization node created ({Ms(dv)} prograde).";
            }
            GUI.enabled = wasEnabled;

            if (_message.Length > 0) GUILayout.Label(_message, _ok);

            DrawReleaseSchedule(vessel, now);
        }

        private void DrawReleaseSchedule(Vessel vessel, double now)
        {
            if (_lastPlan == null || _lastPlanVessel != vessel.persistentId) return;

            GUILayout.Label("Release schedule (at apoapsis)", _header);
            for (int k = 0; k < _lastPlan.ReleaseUTs.Count; ++k)
            {
                double dt = _lastPlan.ReleaseUTs[k] - now;
                Row($"SAT-{k + 1}", dt > 0.0 ? "in " + _formatTime(dt) : "passed");
            }
            GUILayout.Label("After each release: switch to the satellite and use " +
                            "\"Circularize at next apoapsis\".");
        }

        // ── KSP calls ────────────────────────────────────────────────────────

        private static void CreateNode(PatchedConicSolver solver, double ut, double progradeDv)
        {
            var node = solver.AddManeuverNode(ut);
            // Node Δv components: x radial, y normal, z prograde.
            node.OnGizmoUpdated(new Vector3d(0.0, 0.0, progradeDv), ut);
        }

        private int AddReleaseAlarms(Vessel vessel, DeploymentPlan plan)
        {
            int added = 0;
            for (int k = 0; k < plan.ReleaseUTs.Count; ++k)
            {
                var alarm = new AlarmTypeRaw
                {
                    title        = $"Release SAT-{k + 1}",
                    description  = $"{vessel.vesselName}: release SAT-{k + 1} at apoapsis in " +
                                   $"{AlarmLead:F0} s, then circularize it " +
                                   $"({Ms(plan.SatelliteCircularizeDeltaV)} prograde).",
                    ut           = plan.ReleaseUTs[k] - AlarmLead,
                    vesselId     = vessel.persistentId,
                    vesselName   = vessel.vesselName,
                    linkToVessel = true,
                };
                if (alarm.actions == null) alarm.actions = new AlarmActions();
                alarm.actions.warp    = AlarmActions.WarpEnum.KillWarp;
                alarm.actions.message = AlarmActions.MessageEnum.Yes;
                if (AlarmClockScenario.AddAlarm(alarm)) ++added;
            }
            return added;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void Warn(string text) => GUILayout.Label(text, _warn);

        private void Row(string label, string value, GUIStyle? style = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelW));
            GUILayout.Label(value, style ?? GUI.skin.label);
            GUILayout.EndHorizontal();
        }

        private static string Km(double metres) =>
            (metres / 1000.0).ToString("N2", CultureInfo.InvariantCulture) + " km";

        private static string Ms(double v) =>
            v.ToString("N1", CultureInfo.InvariantCulture) + " m/s";

        private void CreateStyles()
        {
            _ok     = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(0.55f, 0.95f, 0.55f) }, wordWrap = true };
            _warn   = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(1.00f, 0.75f, 0.30f) }, wordWrap = true };
            _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        }
    }
}

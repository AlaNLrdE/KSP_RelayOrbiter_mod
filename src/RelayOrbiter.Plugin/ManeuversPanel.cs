using System;
using System.Collections.Generic;
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

        private readonly IDeploymentPlanner       _planner;
        private readonly IAltitudePlanner         _altitudes;
        private readonly IConstellationCalculator _calculator;
        private readonly ConstellationSettings    _settings;
        private readonly Func<double, string>     _formatTime;

        private bool _addAlarms = true;
        private DeploymentPlan? _lastPlan;
        private uint _lastPlanVessel;
        private readonly List<uint> _planAlarms = new List<uint>();  // alarms created for _lastPlan
        private string _message = "";

        private GUIStyle? _ok, _warn, _header;

        public ManeuversPanel(IDeploymentPlanner planner, IAltitudePlanner altitudes,
                              IConstellationCalculator calculator, ConstellationSettings settings,
                              Func<double, string> formatTime)
        {
            _planner    = planner;
            _altitudes  = altitudes;
            _calculator = calculator;
            _settings   = settings;
            _formatTime = formatTime;
        }

        // selected: the body the vessel orbits; target: the window's valid target altitude, if any.
        public void Draw(Body selected, double? target)
        {
            if (_ok == null) CreateStyles();

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) { Warn("No active vessel."); return; }

            var orbit = vessel.orbit;
            GUILayout.Label(vessel.vesselName, _header);

            if (vessel.situation != Vessel.Situations.ORBITING)
            {
                Warn("Get into a stable orbit first (periapsis above the atmosphere, not escaping).");
                return;
            }

            Row("Apoapsis",  Km(orbit.ApA));
            Row("Periapsis", Km(orbit.PeA));
            Row("Period",    _formatTime(orbit.period));

            // The constellation forms at the release point: apoapsis (inner parking
            // orbit) or periapsis (outer), whichever is safe at this altitude.
            int n = _settings.Satellites;
            var parking = _calculator.Recommend(selected, orbit.ApA, n);
            string point = ReleasePoint.Name(parking);
            double rRelease = ReleasePoint.Radius(orbit, parking);
            var status = _altitudes.Validate(selected, rRelease - selected.Radius);
            if (status != AltitudeStatus.Ok)
            {
                Warn($"The {point} is not usable ({status}).");
                return;
            }
            if (target.HasValue && Math.Abs(rRelease - selected.Radius - target.Value) > Math.Max(0.01 * target.Value, 1_000.0))
                Warn($"The {point} differs from the selected target ({Km(target.Value)}). " +
                     $"The constellation forms at the {point}.");

            double now    = Planetarium.GetUniversalTime();
            double nextAp = _planner.NextPassUT(now, ReleasePoint.TimeTo(orbit, parking), orbit.period, MinLead);
            var plan      = _planner.Plan(selected.Mu, rRelease, orbit.semiMajorAxis, nextAp, n, parking);
            bool periOk   = OtherApsisOk(selected, plan, out string otherName, out double hOther, out string problem);

            GUILayout.Label($"Parking orbit burn (carrier, at next {point}; {parking.ToString().ToLowerInvariant()} " +
                            $"parking orbit, {n} satellites {Resonance.SpacingDeg(n):F0}° apart)", _header);
            Row("Burn in",            _formatTime(nextAp - now));
            Row("Δv",                 $"{Ms(Math.Abs(plan.ParkingDeltaV))} {ReleasePoint.Direction(plan.ParkingDeltaV)}");
            Row($"Resulting {otherName}", Km(hOther), periOk ? _ok : _warn);
            Row("Parking period",     _formatTime(plan.ParkingPeriod));
            Row("Each satellite: circularize",
                $"{Ms(Math.Abs(plan.SatelliteCircularizeDeltaV))} {ReleasePoint.Direction(plan.SatelliteCircularizeDeltaV)}");
            if (!periOk) Warn(problem);

            var solver = vessel.patchedConicSolver;
            if (solver == null)
            {
                Warn("Maneuver nodes are not available yet (upgrade Mission Control and the Tracking Station).");
                return;
            }

            // The schedule belongs to the parking-orbit node: drop it (and its alarms)
            // if that node was deleted or moved before the burn.
            if (_lastPlan != null && _lastPlanVessel == vessel.persistentId && now < _lastPlan.BurnUT &&
                !solver.maneuverNodes.Any(n => Math.Abs(n.UT - _lastPlan.BurnUT) < 1.0))
            {
                int removed = ClearPlan();
                _message = "The parking-orbit node was removed or moved; its release schedule" +
                           (removed > 0 ? $" and {removed} alarm(s) were" : " was") + " cleared.";
            }

            int existing = solver.maneuverNodes.Count;
            if (existing > 0)
            {
                Warn($"The vessel already has {existing} maneuver node(s).");
                if (GUILayout.Button($"Delete {existing} existing node(s)"))
                {
                    foreach (var node in solver.maneuverNodes.ToList()) node.RemoveSelf();
                    int removed = ClearPlan();
                    _message = "Existing nodes deleted" +
                               (removed > 0 ? $", with {removed} release alarm(s)." : ".");
                }
            }

            _addAlarms = GUILayout.Toggle(_addAlarms, " Add release alarms to the Alarm Clock");

            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && existing == 0 && periOk;
            if (GUILayout.Button("Create parking-orbit node"))
            {
                ClearPlan();  // a new plan replaces the previous one and its alarms
                CreateNode(solver, plan.BurnUT, plan.ParkingDeltaV);
                _lastPlan = plan;
                _lastPlanVessel = vessel.persistentId;
                int alarms = _addAlarms ? AddReleaseAlarms(vessel, plan) : 0;
                _message = $"Parking-orbit node created." +
                           (_addAlarms ? $" {alarms} release alarm(s) added." : "");
            }
            GUI.enabled = wasEnabled && existing == 0;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Circularize at next apoapsis"))
                Circularize(solver, selected.Mu, orbit.ApR, orbit.semiMajorAxis,
                            _planner.NextPassUT(now, orbit.timeToAp, orbit.period, MinLead));
            if (GUILayout.Button("Circularize at next periapsis"))
                Circularize(solver, selected.Mu, orbit.PeR, orbit.semiMajorAxis,
                            _planner.NextPassUT(now, orbit.timeToPe, orbit.period, MinLead));
            GUILayout.EndHorizontal();
            GUI.enabled = wasEnabled;

            if (_message.Length > 0) GUILayout.Label(_message, _ok);

            DrawReleaseSchedule(vessel, now);
        }

        private void DrawReleaseSchedule(Vessel vessel, double now)
        {
            if (_lastPlan == null || _lastPlanVessel != vessel.persistentId) return;

            string point = ReleasePoint.Name(_lastPlan.Parking);
            GUILayout.Label($"Release schedule (at {point})", _header);
            for (int k = 0; k < _lastPlan.ReleaseUTs.Count; ++k)
            {
                double dt = _lastPlan.ReleaseUTs[k] - now;
                Row($"SAT-{k + 1}", dt > 0.0 ? "in " + _formatTime(dt) : "passed");
            }
            GUILayout.Label("After each release: switch to the satellite and use " +
                            $"\"Circularize at next {point}\".");
        }

        private void Circularize(PatchedConicSolver solver, double mu, double r, double sma, double ut)
        {
            double dv = _planner.CircularizeAt(mu, r, sma);
            CreateNode(solver, ut, dv);
            _message = $"Circularization node created ({Ms(Math.Abs(dv))} {ReleasePoint.Direction(dv)}).";
        }

        // Inner parking orbit: its periapsis must clear the surface and atmosphere.
        // Outer: its apoapsis must stay inside the SOI.
        private static bool OtherApsisOk(Body body, DeploymentPlan plan,
                                         out string name, out double altitude, out string problem)
        {
            if (plan.Parking == ParkingOrbit.Inner)
            {
                name = "periapsis";
                altitude = plan.PeriapsisRadius - body.Radius;
                problem = altitude <= 0.0 ? "Periapsis would be underground. Raise the orbit."
                                          : "Periapsis would be inside the atmosphere. Raise the orbit.";
                return altitude > 0.0 && altitude >= body.Atm;
            }
            name = "apoapsis";
            altitude = plan.ApoapsisRadius - body.Radius;
            problem = $"Apoapsis would leave {body.Name}'s sphere of influence. Lower the orbit or use more satellites.";
            return !body.Soi.HasValue || plan.ApoapsisRadius <= body.Soi.Value;
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
                    description  = $"{vessel.vesselName}: release SAT-{k + 1} at {ReleasePoint.Name(plan.Parking)} in " +
                                   $"{AlarmLead:F0} s, then circularize it " +
                                   $"({Ms(Math.Abs(plan.SatelliteCircularizeDeltaV))} {ReleasePoint.Direction(plan.SatelliteCircularizeDeltaV)}).",
                    ut           = plan.ReleaseUTs[k] - AlarmLead,
                    vesselId     = vessel.persistentId,
                    vesselName   = vessel.vesselName,
                    linkToVessel = true,
                };
                if (alarm.actions == null) alarm.actions = new AlarmActions();
                alarm.actions.warp    = AlarmActions.WarpEnum.KillWarp;
                alarm.actions.message = AlarmActions.MessageEnum.Yes;
                if (AlarmClockScenario.AddAlarm(alarm))
                {
                    _planAlarms.Add(alarm.Id);
                    ++added;
                }
            }
            return added;
        }

        // Forget the current plan and delete the alarms created for it.
        // Returns how many alarms were deleted.
        private int ClearPlan()
        {
            int removed = 0;
            foreach (uint id in _planAlarms)
                if (AlarmClockScenario.DeleteAlarm(id)) ++removed;
            _planAlarms.Clear();
            _lastPlan = null;
            return removed;
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

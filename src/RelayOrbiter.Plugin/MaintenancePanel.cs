using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RelayOrbiter.Core;
using UnityEngine;
using Body = RelayOrbiter.Core.CelestialBody;

namespace RelayOrbiter.Plugin
{
    // Flight-only panel: find the active satellite's constellation (suggested from
    // the names, confirmed by the player), show how far each satellite is from its
    // slot, and put the active one, or all of them, back with MaintenanceRunner.
    internal sealed partial class MaintenancePanel
    {
        private const float LabelW = 230f;
        private const double RingSpreadWarning = 20.0;  // degrees between members' planes
        private const int   NameChars = 30;

        private readonly IOrbitalMechanics    _mechanics;
        private readonly TransferPlanner      _transfer;
        private readonly PhasingPlanner       _phasing;
        private readonly IDeploymentPlanner   _planner;
        private readonly Func<double, string> _formatTime;

        private uint _forVessel;                                  // active vessel the selection is for
        private readonly HashSet<uint> _selected = new HashSet<uint>();  // persistent ids
        private GroupSuggestion? _suggestion;
        private int  _slots = 3;
        private uint _referenceId;                                // 0: best fit
        private int  _minOrbits = 1;
        private bool _matchPlanes = true;  // different planes drift apart in places: match them by default
        private bool _customAltitude;                             // final altitude typed in by the player
        private string _customKm = "";
        private MaintenanceRunner? _runner;
        private GUIStyle? _ok, _warn, _header;

        public MaintenancePanel(IOrbitalMechanics mechanics, TransferPlanner transfer, PhasingPlanner phasing,
                                IDeploymentPlanner planner, Func<double, string> formatTime)
        {
            _mechanics  = mechanics;
            _transfer   = transfer;
            _phasing    = phasing;
            _planner    = planner;
            _formatTime = formatTime;
            // What the save file keeps while a correction runs.
            RelayOrbiterScenario.MaintenanceProvider = () => IsRunning ? _runner!.ToState() : null;
        }

        public bool IsRunning => _runner != null && _runner.State == MaintenanceRunner.RunState.Running;

        // A correction from the save file can be resumed.
        public bool HasPending => _runner == null && RelayOrbiterScenario.PendingMaintenance != null;

        public void Tick()
        {
            // The scene reloaded because the correction switched to a far satellite:
            // carry on by itself once that satellite is the active vessel.
            var pending = RelayOrbiterScenario.PendingMaintenance;
            var active = FlightGlobals.ActiveVessel;
            if (_runner == null && pending != null && pending.Current >= 0 && active != null &&
                active.persistentId == pending.Members[pending.Current])
                Resume(pending);
            _runner?.Tick();
        }

        public void Abort() => _runner?.Abort();

        private void Resume(MaintenanceState state)
        {
            RelayOrbiterScenario.PendingMaintenance = null;
            _runner = new MaintenanceRunner(state, true, _transfer, _phasing, _planner, _formatTime);
        }

        public void Draw(Body body)
        {
            if (_ok == null) CreateStyles();
            if (_runner != null) { DrawRunner(); return; }
            if (RelayOrbiterScenario.PendingMaintenance is { } pending) { DrawResume(pending); return; }

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) { Warn("No active vessel."); return; }
            if (vessel.situation != Vessel.Situations.ORBITING) { Warn("The active vessel must be in orbit."); return; }

            var candidates = Candidates(vessel);
            if (_forVessel != vessel.persistentId) Suggest(vessel, candidates);

            _byPlane = GUILayout.Toolbar(_byPlane ? 1 : 0, new[] { "Group by name", "Group by plane" }) == 1;
            if (_byPlane) { DrawPlaneGroup(body, vessel, candidates); return; }

            // ── The constellation ──
            GUILayout.Label("Constellation", _header);
            if (_suggestion == null)
                GUILayout.Label($"No constellation found in the name \"{vessel.vesselName}\": tick its satellites below.",
                                _warn);
            else
                GUILayout.Label($"Suggested from the names: {_suggestion.Label}, {_suggestion.Count} slots" +
                                (_suggestion.FromOurNames ? "." : " (guessed from the numbers in the names; check it)."),
                                _ok);
            if (GUILayout.Button("Suggest again")) Suggest(vessel, candidates);

            var members = candidates.Where(v => _selected.Contains(v.persistentId)).ToList();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Slots", _header, GUILayout.Width(80f));
            if (GUILayout.Button("−", GUILayout.Width(24f))) _slots--;
            _slots = Mathf.Clamp(_slots, Math.Max(StationKeeping.MinSlots, members.Count), StationKeeping.MaxSlots);
            GUILayout.Label(_slots.ToString(CultureInfo.InvariantCulture), GUILayout.Width(24f));
            if (GUILayout.Button("+", GUILayout.Width(24f))) _slots++;
            _slots = Mathf.Clamp(_slots, Math.Max(StationKeeping.MinSlots, members.Count), StationKeeping.MaxSlots);
            GUILayout.Label($"{360.0 / _slots:0.#}° apart");
            GUILayout.EndHorizontal();

            if (DrawShapeSelector()) { DrawShaped(body, vessel, candidates, members); return; }

            int active = members.IndexOf(vessel);
            int? reference = ReferenceIndex(members);
            double mu = vessel.mainBody.gravParameter;
            double targetSma = TargetSma(body, members, reference, active);
            double targetPeriod = _mechanics.OrbitalPeriod(targetSma, mu);
            double now = Planetarium.GetUniversalTime();
            SlotFit? fit = members.Count > 0 && members.Count <= _slots
                ? StationKeeping.Fit(members.Select(v => MeanLongitude.At(v.orbit, now)).ToList(), _slots,
                                     reference, reference.HasValue || active < 0 ? (int?)null : active)
                : null;

            GUILayout.Label("Satellites (tick the members)   slot · off by · drift per orbit", _header);
            BeginList(candidates.Count, ref _listScroll);
            foreach (var v in candidates)
            {
                int i = members.IndexOf(v);
                string info = fit != null && i >= 0
                    ? $"  #{fit.SlotOf[i] + 1} · {fit.ErrorsDeg[i]:+0.00;-0.00}° · " +
                      $"{StationKeeping.DriftDegPerOrbit(v.orbit.period, targetPeriod):+0.000;-0.000}°"
                    : "";
                string mark = v == vessel ? " (active)" : i >= 0 && i == reference ? " (reference)" : "";
                bool on = GUILayout.Toggle(i >= 0, Short(v.vesselName) + mark + info);
                if (on != (i >= 0))
                {
                    if (on) _selected.Add(v.persistentId); else _selected.Remove(v.persistentId);
                }
            }
            EndList(candidates.Count);
            if (members.Count > _slots) { Warn($"{members.Count} satellites don't fit in {_slots} slots."); return; }
            if (members.Count < 2) { Warn("Tick at least two satellites."); return; }
            double spread = PlaneGrouping.Spread(members.Select(v => new OrbitPlane(v.orbit.inclination, v.orbit.LAN)).ToList());
            if (spread > RingSpreadWarning)
                Warn($"These satellites are in planes up to {spread:F0}° apart: one ring can't hold them without huge " +
                     "plane changes. Untick the others, use Group by plane to split them, or pick a Walker shape.");

            if (GUILayout.Button("Reference: " + (reference.HasValue
                    ? Short(members[reference.Value].vesselName) + " (stays put)"
                    : "best fit of the others")))
                NextReference(members);
            if (!DrawFinalAltitude(body, members, reference, active)) return;
            Row("Target orbit", $"{Km(targetSma - vessel.mainBody.Radius)}, period {_formatTime(targetPeriod)}");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Phasing orbits (at least)", GUILayout.Width(LabelW));
            if (GUILayout.Button("−", GUILayout.Width(24f))) _minOrbits--;
            _minOrbits = Mathf.Clamp(_minOrbits, 1, StationKeeping.MaxPhasingOrbits);
            GUILayout.Label(_minOrbits.ToString(CultureInfo.InvariantCulture), GUILayout.Width(24f));
            if (GUILayout.Button("+", GUILayout.Width(24f))) _minOrbits++;
            _minOrbits = Mathf.Clamp(_minOrbits, 1, StationKeeping.MaxPhasingOrbits);
            GUILayout.Label("(more: slower, less Δv)");
            GUILayout.EndHorizontal();
            _matchPlanes = GUILayout.Toggle(_matchPlanes, "Match the orbital planes too (plane change first)");
            NodeExecutor.AutoActivateEngines = GUILayout.Toggle(NodeExecutor.AutoActivateEngines,
                "Activate the satellites' engines when none is running before a burn");
            Warp.Enabled = GUILayout.Toggle(Warp.Enabled, "Use time warp (off: you warp, burns still start on time)");
            if (_matchPlanes)
            {
                var plane = CommonPlane(members, reference, null);
                Row("Common plane", (reference.HasValue ? "the reference's" : "mean of the members") +
                                    $": i {plane.InclinationDeg:F2}°, Ω {plane.LanDeg:F2}°");
                if (vessel.VesselValues.AutopilotSkill.value < 3)
                    Warn("This satellite has no SAS maneuver mode (level 3): plane changes need it on every satellite that turns.");
            }

            DrawActive(body, vessel, members, active, reference, fit!, targetSma, targetPeriod, now);
            DrawWhole(body, members, reference, now);
        }

        // ── Correct the active satellite ─────────────────────────────────────

        private void DrawActive(Body body, Vessel vessel, List<Vessel> members, int active, int? reference,
                                SlotFit fit, double targetSma, double targetPeriod, double now)
        {
            GUILayout.Label($"Correct {Short(vessel.vesselName)}", _header);
            if (active < 0) { Warn("The active vessel is not ticked as a member."); return; }
            if (reference == active) { Warn("The active vessel is the reference: it stays put."); return; }
            var solver = vessel.patchedConicSolver;
            if (solver == null) { Warn("Maneuver nodes are not available yet."); return; }
            if (solver.maneuverNodes.Count > 0) { Warn("Delete the vessel's maneuver nodes first."); return; }

            var o = vessel.orbit;
            double mu = vessel.mainBody.gravParameter;
            double error = fit.ErrorsDeg[active];
            var transfer = _transfer.Plan(mu, o.ApR, o.PeR, targetSma);
            bool transferNeeded = StationKeeping.NeedsTransfer(o.ApR, o.PeR, targetSma);
            bool periodOk = PeriodOk(o, targetSma, targetPeriod);
            bool phaseOk = StationKeeping.PhaseOk(error);
            double planeOff = _matchPlanes ? PlaneOff(vessel, CommonPlane(members, reference, active)) : 0.0;
            bool planeOk = planeOff < PlaneToleranceDeg;
            if (_matchPlanes)
                Row("Off the common plane", $"{planeOff:F2}°", planeOk ? _ok : _warn);
            Row("Off its slot", $"{error:+0.00;-0.00}°", phaseOk ? _ok : _warn);
            Row("Period", $"{o.period - targetPeriod:+0.0;-0.0} s from the target " +
                          $"({StationKeeping.DriftDegPerOrbit(o.period, targetPeriod):+0.000;-0.000}°/orbit)",
                periodOk ? _ok : _warn);

            double dv = 0.0, time = 0.0;
            if (!planeOk)
            {
                double planeDv = PlaneGrouping.PlaneChangeDv(o.orbitalSpeed, planeOff);
                Row("Plane change", Ms(planeDv));
                dv += planeDv;
            }
            if (transferNeeded)
            {
                Row("Transfer to the target", Ms(transfer.TotalDv));
                dv += transfer.TotalDv;
                Warn("Far from the target orbit: it transfers first, then plans the phasing from where it ends up.");
            }
            else if (!phaseOk)
            {
                var p = _phasing.Choose(mu, o.radius, o.semiMajorAxis, targetSma, error,
                                        PhasingPlanner.MinRadius(body), MaxRadius(body), _minOrbits);
                if (p == null) { Warn("No safe phasing orbit within " + StationKeeping.MaxPhasingOrbits + " orbits."); return; }
                Row("Phasing", $"{p.Orbits} orbit(s), {_formatTime(p.Duration)}");
                Row("Phasing orbit's other apsis", Km(p.OtherApsisRadius - body.Radius));
                if (Math.Abs(p.ErrorDeg - error) > 1.0)
                    Row("Direction", $"the long way round ({p.ErrorDeg:+0;-0}°): the short one is unsafe", _warn);
                Row("Burns", $"{Ms(p.FirstDv)} now, {Ms(p.SecondDv)} at the end");
                dv += p.TotalDv;
                time += p.Duration;
            }
            else if (!periodOk)
            {
                double trim = _phasing.TrimDv(mu, o.radius, o.semiMajorAxis, targetSma);
                Row("Period trim", trim.ToString("N3", CultureInfo.InvariantCulture) + " m/s");
                dv += Math.Abs(trim);
            }

            if (!transferNeeded && phaseOk && periodOk && planeOk)
            {
                GUILayout.Label("In its slot, on the target period: nothing to correct.", _ok);
                return;
            }
            Row("Total Δv (estimate)", Ms(dv));
            double available = vessel.VesselDeltaV != null ? vessel.VesselDeltaV.TotalDeltaVVac : double.NaN;
            if (!double.IsNaN(available))
                Row("Satellite's Δv", Ms(available), available >= dv * 1.1 ? _ok : _warn);
            if (time > 0.0) Row("Takes about", _formatTime(time));

            if (GUILayout.Button("Correct this satellite"))
            {
                var state = Start(body, members, fit, now, targetSma, targetPeriod, reference,
                                  CommonPlane(members, reference, active));
                for (int i = 0; i < members.Count; ++i)
                    if (i != active) state.Stage[i] = CorrectionStage.Done;
                _runner = new MaintenanceRunner(state, false, _transfer, _phasing, _planner, _formatTime);
            }
        }

        // ── Correct every satellite ──────────────────────────────────────────

        private void DrawWhole(Body body, List<Vessel> members, int? reference, double now)
        {
            GUILayout.Label("Correct the whole constellation", _header);
            double mu = body.Mu;
            // Every satellite counts for the best fit here: they all move.
            double targetSma = TargetSma(body, members, reference, -1);
            double targetPeriod = _mechanics.OrbitalPeriod(targetSma, mu);
            var fit = StationKeeping.Fit(members.Select(v => MeanLongitude.At(v.orbit, now)).ToList(), _slots, reference);
            var plane = CommonPlane(members, reference, null);
            var off = Enumerable.Range(0, members.Count)
                .Where(i => i != reference &&
                            (!StationKeeping.PhaseOk(fit.ErrorsDeg[i]) ||
                             _matchPlanes && PlaneOff(members[i], plane) >= PlaneToleranceDeg ||
                             StationKeeping.NeedsTransfer(members[i].orbit.ApR, members[i].orbit.PeR, targetSma) ||
                             !PeriodOk(members[i].orbit, targetSma, targetPeriod)))
                .ToList();
            Row("Target orbit", $"{Km(targetSma - body.Radius)}, period {_formatTime(targetPeriod)}");
            Row("Largest slot error", $"{fit.ErrorsDeg.Max(e => Math.Abs(e)):F2}°");
            if (_matchPlanes)
            {
                Row("Largest plane difference", $"{members.Max(v => PlaneOff(v, plane)):F2}°");
                Row("Plane changes (Δv)", Ms(members.Sum(v => PlaneOff(v, plane) < PlaneToleranceDeg ? 0.0
                                            : PlaneGrouping.PlaneChangeDv(v.orbit.orbitalSpeed, PlaneOff(v, plane)))) +
                                          " in all");
            }
            if (off.Count == 0)
            {
                GUILayout.Label("Every satellite is in its slot, on the target period: nothing to correct.", _ok);
                return;
            }
            // The worst few; the list above has every satellite's error.
            const int shown = 6;
            var worst = off.OrderByDescending(i => Math.Abs(fit.ErrorsDeg[i])).Take(shown)
                           .Select(i => $"{Short(members[i].vesselName)} {fit.ErrorsDeg[i]:+0.0;-0.0}°");
            Row("To correct", $"{off.Count} satellite(s): " + string.Join(", ", worst) +
                              (off.Count > shown ? $" and {off.Count - shown} more" : ""));
            GUILayout.Label("They phase in parallel: it switches to each satellite when its burn is due " +
                            "(far ones reload the scene and it carries on by itself). Don't switch vessels until it " +
                            "says the constellation is corrected.", _warn);
            if (GUILayout.Button("Correct the whole constellation"))
            {
                var state = Start(body, members, fit, now, targetSma, targetPeriod, reference, plane);
                for (int i = 0; i < members.Count; ++i)
                    if (!off.Contains(i)) state.Stage[i] = CorrectionStage.Done;
                _runner = new MaintenanceRunner(state, false, _transfer, _phasing, _planner, _formatTime);
            }
        }

        private MaintenanceState Start(Body body, List<Vessel> members, SlotFit fit, double now, double targetSma,
                                       double targetPeriod, int? reference, OrbitPlane plane) =>
            MaintenanceState.Start(members.Select(v => v.persistentId).ToList(), fit, now, targetSma, targetPeriod,
                                   reference, _minOrbits, PhasingPlanner.MinRadius(body), MaxRadius(body),
                                   _matchPlanes ? plane : null);

        // ── Planes ───────────────────────────────────────────────────────────

        private const double PlaneToleranceDeg = 0.05;

        private static OrbitPlane PlaneOf(Vessel v) => new OrbitPlane(v.orbit.inclination, v.orbit.LAN);

        // The reference's plane, else the mean plane of the members (but `exclude`).
        private static OrbitPlane CommonPlane(List<Vessel> members, int? reference, int? exclude)
        {
            if (reference.HasValue) return PlaneOf(members[reference.Value]);
            var others = members.Where((_, i) => i != exclude).Select(PlaneOf).ToList();
            return PlaneGrouping.MeanPlane(others.Count > 0 ? others : members.Select(PlaneOf).ToList());
        }

        private static double PlaneOff(Vessel v, OrbitPlane plane) =>
            PlaneGrouping.RelativeInclinationDeg(PlaneOf(v), plane);

        // On the target period, or closer than a precise burn can trim.
        private bool PeriodOk(Orbit o, double targetSma, double targetPeriod) =>
            StationKeeping.PeriodOk(o.period, targetPeriod) ||
            Math.Abs(_phasing.TrimDv(o.referenceBody.gravParameter, o.radius, o.semiMajorAxis, targetSma)) <
            2 * NodeBurn.PreciseDv;

        private void DrawResume(MaintenanceState pending)
        {
            int left = pending.Stage.Count(st => st != CorrectionStage.Done);
            GUILayout.Label("Interrupted constellation correction", _header);
            GUILayout.Label($"{left} of {pending.Members.Length} satellites still to correct. It carries on from " +
                            "where it was, with the slots it started with.", _ok);
            if (GUILayout.Button("Resume correction")) Resume(pending);
            if (GUILayout.Button("Discard it"))
            {
                Debug.Log("[RelayOrbiter] Interrupted constellation correction discarded.");
                RelayOrbiterScenario.PendingMaintenance = null;
            }
        }

        private void DrawRunner()
        {
            var r = _runner!;
            GUILayout.Label(r.State == MaintenanceRunner.RunState.Running
                ? $"Alignment in progress: {r.BurnsDone} of ~{r.BurnsEstimate} burns done ({r.Progress:P0})"
                : "Constellation correction", _header);
            if (r.State == MaintenanceRunner.RunState.Running)
            {
                GUILayout.Label($"{r.Remaining} satellite(s) still to correct. Now: {r.CurrentStep}");
                foreach (var line in r.Upcoming) GUILayout.Label("  " + line);
            }
            GUILayout.Label(r.Status, r.State == MaintenanceRunner.RunState.Running ||
                                      r.State == MaintenanceRunner.RunState.Done ? _ok : _warn);
            if (r.State == MaintenanceRunner.RunState.Running)
            {
                bool wasEnabled = GUI.enabled;
                GUI.enabled = wasEnabled && r.CanFastForward;
                if (GUILayout.Button("Fast forward")) r.FastForward();
                GUI.enabled = wasEnabled;
                if (GUILayout.Button("Abort")) r.Abort();
            }
            else if (GUILayout.Button("Close")) _runner = null;
        }

        // ── Selection ────────────────────────────────────────────────────────

        // Vessels that can be members: in orbit around the same body, not debris.
        private static List<Vessel> Candidates(Vessel active) =>
            FlightGlobals.Vessels
                .Where(v => v != null && v.mainBody == active.mainBody &&
                            v.situation == Vessel.Situations.ORBITING &&
                            v.vesselType != VesselType.Debris && v.vesselType != VesselType.SpaceObject &&
                            v.vesselType != VesselType.Flag && v.vesselType != VesselType.EVA &&
                            v.vesselType != VesselType.Unknown)
                .OrderBy(v => ConstellationName.Parse(v.vesselName)?.Index ?? int.MaxValue)
                .ThenBy(v => v.vesselName, StringComparer.Ordinal)
                .ToList();

        private void Suggest(Vessel vessel, List<Vessel> candidates)
        {
            _forVessel = vessel.persistentId;
            _selected.Clear();
            _referenceId = 0;
            _suggestion = ConstellationGroup.Suggest(vessel.vesselName,
                                                     candidates.Where(v => v != vessel).Select(v => v.vesselName));
            if (_suggestion != null)
            {
                foreach (var v in candidates)
                    if (_suggestion.Members.Contains(v.vesselName)) _selected.Add(v.persistentId);
                _slots = _suggestion.Count;
                SuggestShape(candidates.Where(v => _selected.Contains(v.persistentId)).ToList());
            }
            else
            {
                _selected.Add(vessel.persistentId);
            }
            Debug.Log($"[RelayOrbiter] Constellation of {vessel.vesselName}: " +
                      (_suggestion == null ? "none found in the name."
                       : $"{_suggestion.Label}, {_suggestion.Count} slots, {_selected.Count} member(s) " +
                         $"({(_suggestion.FromOurNames ? "our names" : "numbered names")})."));
        }

        private int? ReferenceIndex(List<Vessel> members)
        {
            int i = members.FindIndex(v => v.persistentId == _referenceId);
            return i >= 0 ? i : (int?)null;
        }

        // Best fit → each member in turn → best fit.
        private void NextReference(List<Vessel> members)
        {
            int i = members.FindIndex(v => v.persistentId == _referenceId);
            _referenceId = i + 1 < members.Count ? members[i + 1].persistentId : 0;
        }

        // Suggested or typed in. False when the typed altitude can't be used.
        private bool DrawFinalAltitude(Body body, List<Vessel> members, int? reference, int active)
        {
            if (reference.HasValue)
            {
                Row("Final altitude", "the reference's (it stays put)");
                return true;
            }
            double suggested = SuggestedSma(body, members, null, active) - body.Radius;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Final altitude", GUILayout.Width(LabelW));
            int mode = GUILayout.Toolbar(_customAltitude ? 1 : 0, new[] { "Suggested", "Custom" });
            GUILayout.EndHorizontal();
            if (mode == 1 && !_customAltitude)
                _customKm = (suggested / 1000.0).ToString("F0", CultureInfo.InvariantCulture);
            _customAltitude = mode == 1;
            if (!_customAltitude) return true;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Altitude [km]", GUILayout.Width(LabelW));
            _customKm = GUILayout.TextField(_customKm, GUILayout.Width(120f));
            GUILayout.EndHorizontal();
            Row("Suggested", Km(suggested));
            if (CustomSma(body) == null)
            {
                Warn($"Enter an altitude between {Km(PhasingPlanner.MinRadius(body) - body.Radius)} and " +
                     (body.Soi.HasValue ? Km(body.Soi.Value - body.Radius) + " (the SOI)." : "the SOI."));
                return false;
            }
            return true;
        }

        // The typed-in final orbit's semi-major axis, when valid.
        private double? CustomSma(Body body)
        {
            if (!_customAltitude ||
                !double.TryParse(_customKm, NumberStyles.Float, CultureInfo.InvariantCulture, out double km))
                return null;
            double r = body.Radius + km * 1000.0;
            return r >= PhasingPlanner.MinRadius(body) && r < MaxRadius(body) ? r : (double?)null;
        }

        // The final orbit: the reference's when there is one, else typed in, else suggested.
        private double TargetSma(Body body, List<Vessel> members, int? reference, int active) =>
            !reference.HasValue && CustomSma(body) is double custom ? custom
                                                                   : SuggestedSma(body, members, reference, active);

        // Synchronous when the constellation's names say so.
        private double SuggestedSma(Body body, List<Vessel> members, int? reference, int active)
        {
            if (members.Count == 0) return FlightGlobals.ActiveVessel.orbit.semiMajorAxis;
            bool sync = members.Any(v => ConstellationName.Parse(v.vesselName)?.Tag.StartsWith("SYNC", StringComparison.Ordinal) == true);
            double? syncSma = sync ? body.Radius + _mechanics.SyncOrbitAltitude(body) : null;
            return StationKeeping.TargetSma(members.Select(v => v.orbit.semiMajorAxis).ToList(), reference, syncSma,
                                            active >= 0 ? active : (int?)null);
        }

        private static double MaxRadius(Body body) => body.Soi ?? double.MaxValue;

        // ── Helpers ──────────────────────────────────────────────────────────

        // Long lists scroll, so the window stays on screen.
        private const int   ListRows = 12;
        private const float ListHeight = 300f;
        private Vector2 _listScroll, _planeScroll;

        private static void BeginList(int rows, ref Vector2 scroll)
        {
            if (rows > ListRows) scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(ListHeight));
        }

        private static void EndList(int rows)
        {
            if (rows > ListRows) GUILayout.EndScrollView();
        }

        private static string Short(string name) => name.Length <= NameChars ? name : name.Substring(0, NameChars - 1) + "…";

        private void Warn(string text) => GUILayout.Label(text, _warn);

        private void Row(string label, string value, GUIStyle? style = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelW));
            GUILayout.Label(value, style ?? _ok);
            GUILayout.EndHorizontal();
        }

        private static string Km(double metres) =>
            (metres / 1000.0).ToString("N2", CultureInfo.InvariantCulture) + " km";

        private static string Ms(double v) => v.ToString("N2", CultureInfo.InvariantCulture) + " m/s";

        private void CreateStyles()
        {
            _ok     = RelayOrbiterSkin.Ok;
            _warn   = RelayOrbiterSkin.Warn;
            _header = RelayOrbiterSkin.Header;
        }
    }
}

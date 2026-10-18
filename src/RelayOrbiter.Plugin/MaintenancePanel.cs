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
    // slot, and put the active one back with MaintenanceRunner.
    internal sealed class MaintenancePanel
    {
        private const float LabelW = 230f;
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
        }

        public bool IsRunning => _runner != null && _runner.State == MaintenanceRunner.RunState.Running;

        public void Tick()  => _runner?.Tick();
        public void Abort() => _runner?.Abort();

        public void Draw(Body body)
        {
            if (_ok == null) CreateStyles();
            if (_runner != null) { DrawRunner(); return; }

            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) { Warn("No active vessel."); return; }
            if (vessel.situation != Vessel.Situations.ORBITING) { Warn("The active vessel must be in orbit."); return; }

            var candidates = Candidates(vessel);
            if (_forVessel != vessel.persistentId) Suggest(vessel, candidates);

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
            _slots = Mathf.Clamp(_slots, Math.Max(Resonance.MinSatellites, members.Count), Resonance.MaxSatellites);
            GUILayout.Label(_slots.ToString(CultureInfo.InvariantCulture), GUILayout.Width(24f));
            if (GUILayout.Button("+", GUILayout.Width(24f))) _slots++;
            _slots = Mathf.Clamp(_slots, Math.Max(Resonance.MinSatellites, members.Count), Resonance.MaxSatellites);
            GUILayout.Label($"{Resonance.SpacingDeg(_slots):F0}° apart");
            GUILayout.EndHorizontal();

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
            if (members.Count > _slots) { Warn($"{members.Count} satellites don't fit in {_slots} slots."); return; }
            if (members.Count < 2) { Warn("Tick at least two satellites."); return; }

            if (GUILayout.Button("Reference: " + (reference.HasValue
                    ? Short(members[reference.Value].vesselName) + " (stays put)"
                    : "best fit of the others")))
                NextReference(members);
            Row("Target orbit", $"{Km(targetSma - vessel.mainBody.Radius)}, period {_formatTime(targetPeriod)}");

            // ── The active satellite ──
            GUILayout.Label($"Correct {Short(vessel.vesselName)}", _header);
            if (active < 0) { Warn("The active vessel is not ticked as a member."); return; }
            if (reference == active) { Warn("The active vessel is the reference: it stays put. Pick another reference."); return; }
            var solver = vessel.patchedConicSolver;
            if (solver == null) { Warn("Maneuver nodes are not available yet."); return; }
            if (solver.maneuverNodes.Count > 0) { Warn("Delete the vessel's maneuver nodes first."); return; }

            var o = vessel.orbit;
            double error = fit!.ErrorsDeg[active];
            var transfer = _transfer.Plan(mu, o.ApR, o.PeR, targetSma);
            bool transferNeeded = StationKeeping.NeedsTransfer(o.ApR, o.PeR, targetSma);
            // A trim smaller than a precise burn can fly is as good as it gets.
            bool periodOk = StationKeeping.PeriodOk(o.period, targetPeriod) ||
                            Math.Abs(_phasing.TrimDv(mu, o.radius, o.semiMajorAxis, targetSma)) < 2 * NodeBurn.PreciseDv;
            bool phaseOk = StationKeeping.PhaseOk(error);
            Row("Off its slot", $"{error:+0.00;-0.00}°", phaseOk ? _ok : _warn);
            Row("Period", $"{o.period - targetPeriod:+0.0;-0.0} s from the target " +
                          $"({StationKeeping.DriftDegPerOrbit(o.period, targetPeriod):+0.000;-0.000}°/orbit)",
                periodOk ? _ok : _warn);

            double dv = 0.0, time = 0.0;
            if (transferNeeded)
            {
                Row("Transfer to the target", Ms(transfer.TotalDv));
                dv += transfer.TotalDv;
                Warn("Far from the target orbit: it transfers first, then plans the phasing from where it ends up.");
            }
            else if (!phaseOk)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Phasing orbits (at least)", GUILayout.Width(LabelW));
                if (GUILayout.Button("−", GUILayout.Width(24f))) _minOrbits--;
                _minOrbits = Mathf.Clamp(_minOrbits, 1, StationKeeping.MaxPhasingOrbits);
                GUILayout.Label(_minOrbits.ToString(CultureInfo.InvariantCulture), GUILayout.Width(24f));
                if (GUILayout.Button("+", GUILayout.Width(24f))) _minOrbits++;
                _minOrbits = Mathf.Clamp(_minOrbits, 1, StationKeeping.MaxPhasingOrbits);
                GUILayout.EndHorizontal();

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

            if (!transferNeeded && phaseOk && periodOk)
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
                _runner = new MaintenanceRunner(vessel, members, _slots, reference, targetSma,
                                                PhasingPlanner.MinRadius(body), MaxRadius(body), _minOrbits,
                                                _transfer, _phasing, _planner, _formatTime);
        }

        private void DrawRunner()
        {
            var r = _runner!;
            GUILayout.Label($"Step {r.Step}/{r.Steps}: {r.CurrentStep}", _header);
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

        // Synchronous when the constellation's names say so.
        private double TargetSma(Body body, List<Vessel> members, int? reference, int active)
        {
            if (members.Count == 0) return FlightGlobals.ActiveVessel.orbit.semiMajorAxis;
            bool sync = members.Any(v => ConstellationName.Parse(v.vesselName)?.Tag.StartsWith("SYNC", StringComparison.Ordinal) == true);
            double? syncSma = sync ? body.Radius + _mechanics.SyncOrbitAltitude(body) : null;
            return StationKeeping.TargetSma(members.Select(v => v.orbit.semiMajorAxis).ToList(), reference, syncSma,
                                            active >= 0 ? active : (int?)null);
        }

        private static double MaxRadius(Body body) => body.Soi ?? double.MaxValue;

        // ── Helpers ──────────────────────────────────────────────────────────

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

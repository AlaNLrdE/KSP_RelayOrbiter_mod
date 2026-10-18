using System;
using System.Collections.Generic;
using System.Linq;
using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Puts satellites back in their slots, one Tick per frame. Each satellite goes
    // through the CorrectionStages: transfer and circularize (only when far off), a
    // phasing orbit out and back, a period trim. Every satellite is phased against
    // the same frozen SlotFrame, so they all work in parallel: the runner always
    // handles the next burn due, switching to that satellite when it is not the
    // active vessel. Switching to an unloaded satellite reloads the flight scene; the
    // state is in the save file (RelayOrbiterScenario) and the run resumes on its own.
    internal sealed class MaintenanceRunner
    {
        public enum RunState { Running, Done, Failed, Aborted }

        private const double NodeLead      = 120.0;  // s from now to a node placed "now"
        private const double MinNodeLead   = 60.0;
        private const double SwitchLead    = 300.0;  // s before a burn: switch to its satellite
        private const double SwitchTimeout = 30.0;   // s for a switch to a loaded vessel
        private const double PlaneToleranceDeg = 0.05;  // planes this close count as the same
        private const double ReturnGap     = 1800.0; // s between two satellites' phasing returns
        private const double MinBurn       = 2 * NodeBurn.PreciseDv;  // m/s: smaller burns are skipped

        private readonly MaintenanceState _state;
        private readonly TransferPlanner _transfer;
        private readonly PhasingPlanner _phasing;
        private readonly IDeploymentPlanner _planner;
        private readonly Func<double, string> _formatTime;
        private readonly Warp _warp = new Warp();

        private NodeExecutor? _executor;
        private bool _switching;
        private double _switchUntil;

        // A new correction, or one resumed from the save file after a scene reload.
        public MaintenanceRunner(MaintenanceState state, bool resumed, TransferPlanner transfer,
                                 PhasingPlanner phasing, IDeploymentPlanner planner, Func<double, string> formatTime)
        {
            _state      = state;
            _transfer   = transfer;
            _phasing    = phasing;
            _planner    = planner;
            _formatTime = formatTime;

            double now = Planetarium.GetUniversalTime();
            int toDo = state.Stage.Count(s => s != CorrectionStage.Done);
            Log((resumed ? "Correction resumed: " : "Correction started: ") +
                $"{toDo} of {state.Members.Length} satellites to correct, {state.Slots} slots, reference " +
                $"{(state.Reference >= 0 ? Name(state.Reference) : "best fit")}, target orbit " +
                $"{state.TargetSma:F0} m (period {state.TargetPeriod:F1} s).");
            for (int i = 0; i < state.Members.Length; ++i)
            {
                var v = Member(i);
                if (v == null) { Log($"  member {state.Members[i]}: not found"); continue; }
                var o = v.orbit;
                Log($"  {v.vesselName}: {state.Stage[i]}, L {MeanLongitude.At(o, now):F2}° (Ω {o.LAN:F2}, ω " +
                    $"{o.argumentOfPeriapsis:F2}), slot #{state.SlotOf[i] + 1}, off {SlotError(i, now):+0.00;-0.00}°, " +
                    $"a {o.semiMajorAxis:F0} m, e {o.eccentricity:F5}, i {o.inclination:F2}°, T {o.period:F1} s");
            }
        }

        public RunState State  { get; private set; } = RunState.Running;
        public string   Status { get; private set; } = "";
        public string   Error  { get; private set; } = "";
        public string   CurrentStep { get; private set; } = "";
        public int      Remaining => _state.Stage.Count(s => s != CorrectionStage.Done);
        public int      BurnsDone => _state.BurnsDone;
        public double   Progress  => _state.Progress;
        public int      BurnsEstimate => _state.BurnsEstimate;

        // The satellites still to correct and their next burn, from the last schedule.
        public IReadOnlyList<string> Upcoming => _upcoming;
        private readonly List<string> _upcoming = new List<string>();

        public MaintenanceState ToState() => _state;

        public bool CanFastForward =>
            State == RunState.Running && (_executor == null ? !_switching : _executor.IsWaiting);

        public void FastForward()
        {
            _warp.Now();
            _executor?.FastForward();
        }

        public void Tick()
        {
            if (State != RunState.Running) return;
            double now = Planetarium.GetUniversalTime();
            if (_state.Current < 0) Schedule(now);
            else Work(_state.Current, now);
        }

        public void Abort()
        {
            if (State != RunState.Running) return;
            Stop();
            State = RunState.Aborted;
            // Switching to an unloaded satellite destroys the scene (and aborts this
            // runner) after the game saved the state: the run carries on after the reload.
            Status = _switching ? "Scene reloading for the switch: the correction carries on after it."
                                : "Aborted" + (CurrentStep.Length > 0 ? $" ({CurrentStep})." : ".");
            Log(Status);
        }

        // ── Scheduling ───────────────────────────────────────────────────────

        // Find the next burn due among the satellites (skipping the stages they don't
        // need); warp towards it, and take it on once it is close.
        private void Schedule(double now)
        {
            int next = -1;
            double nextUT = double.MaxValue;
            var due = new List<(double ut, string text)>();
            for (int i = 0; i < _state.Members.Length; ++i)
            {
                var v = Member(i);
                if (_state.Stage[i] == CorrectionStage.Done) continue;
                if (v == null) { Fail($"Satellite {_state.Members[i]} is gone."); return; }
                Burn? burn;
                while ((burn = Plan(i, v.orbit, now, commit: false)) == null && State == RunState.Running &&
                       _state.Stage[i] != CorrectionStage.Done)
                    AdvanceStage(i, now);
                if (State != RunState.Running) return;
                if (burn == null) continue;
                due.Add((burn.UT, $"{Name(i)}: {Describe(_state.Stage[i])} in {_formatTime(Math.Max(0.0, burn.UT - now))}"));
                if (burn.UT < nextUT) { nextUT = burn.UT; next = i; }
            }
            _upcoming.Clear();
            _upcoming.AddRange(due.OrderBy(d => d.ut).Select(d => d.text));

            if (next < 0)
            {
                State = RunState.Done;
                CurrentStep = "";
                Status = "Constellation corrected: " + string.Join(", ",
                    Enumerable.Range(0, _state.Members.Length)
                              .Select(i => $"{Name(i)} {SlotError(i, now):+0.00;-0.00}°"));
                Log(Status);
                return;
            }
            if (nextUT - now > SwitchLead)
            {
                _warp.Towards(nextUT - SwitchLead);
                CurrentStep = $"Next: {Name(next)}, {Describe(_state.Stage[next])}";
                Status = $"Waiting: {_formatTime(nextUT - now)} to {Name(next)}'s burn";
                return;
            }
            if (TimeWarp.CurrentRateIndex > 0) TimeWarp.SetRate(0, true, false);
            _state.Current = next;
            _switching = false;
        }

        // Switch to satellite i, then plan and fly its burn.
        private void Work(int i, double now)
        {
            var vessel = Member(i);
            if (vessel == null) { Fail($"Satellite {_state.Members[i]} is gone."); return; }
            CurrentStep = $"{vessel.vesselName}: {Describe(_state.Stage[i])}";

            if (FlightGlobals.ActiveVessel != vessel)
            {
                if (_executor != null) { Fail("The satellite is no longer the active vessel."); return; }
                if (!_switching)
                {
                    // An unloaded satellite reloads the scene: this runner ends here and
                    // the saved state resumes on the satellite.
                    _switching = true;
                    _switchUntil = now + SwitchTimeout;
                    Log($"Switching to {vessel.vesselName}" + (vessel.loaded ? "." : " (unloaded: the scene reloads)."));
                    FlightGlobals.ForceSetActiveVessel(vessel);
                }
                else if (now > _switchUntil) Fail($"Could not switch to {vessel.vesselName}.");
                Status = $"Switching to {vessel.vesselName}";
                return;
            }
            _switching = false;
            if (vessel.packed) { Status = "Waiting for physics"; return; }

            if (_executor == null)
            {
                var solver = vessel.patchedConicSolver;
                if (solver == null) { Fail("Maneuver nodes are not available."); return; }
                foreach (var n in solver.maneuverNodes.ToList()) n.RemoveSelf();
                VesselParts.ActivateEngines(vessel);
                var burn = Plan(i, vessel.orbit, now, commit: true);
                if (State != RunState.Running) return;
                if (burn == null) { AdvanceStage(i, now); _state.Current = -1; return; }
                var node = solver.AddManeuverNode(burn.UT);
                node.OnGizmoUpdated(new Vector3d(0.0, burn.Normal, burn.Dv), burn.UT);  // x radial, y normal, z prograde
                if (burn.Normal != 0.0) CheckPlaneChangeSign(solver, node, burn);
                Log($"{vessel.vesselName}: {burn.Log}");
                _executor = new NodeExecutor(vessel, _formatTime, maneuverModeRequired: burn.Normal != 0.0, precise: true);
            }

            var r = _executor.Tick();
            Status = _executor.Status;
            if (r == NodeExecutor.Result.Failed) { Fail(_executor.Error); return; }
            if (r != NodeExecutor.Result.Done) return;
            _executor = null;
            _state.BurnsDone++;
            AdvanceStage(i, now);
            _state.Current = -1;
        }

        private void AdvanceStage(int i, double now)
        {
            var before = _state.Stage[i];
            _state.Advance(i, StationKeeping.PhaseOk(SlotError(i, now)));
            if (before == CorrectionStage.Trim && _state.Stage[i] == CorrectionStage.Phase)
                Log($"{Name(i)} is still {SlotError(i, now):+0.00;-0.00}° off its slot: phasing again.");
            if (_state.Stage[i] == CorrectionStage.Done)
                Log($"{Name(i)} done: {SlotError(i, now):+0.00;-0.00}° from its slot, " +
                    $"drift {Drift(i):+0.000;-0.000}°/orbit.");
        }

        // ── Burns ────────────────────────────────────────────────────────────

        private sealed class Burn
        {
            public Burn(double ut, double dv, string log, double normal = 0.0)
            {
                UT = ut; Dv = dv; Log = log; Normal = normal;
            }

            public double UT     { get; }
            public double Dv     { get; }  // prograde
            public double Normal { get; }  // plane changes only
            public string Log    { get; }
        }

        // The burn satellite i's current stage needs, or null when it needs none (the
        // caller advances the stage). commit: this burn is about to be flown (records
        // the phasing orbit and logs skips).
        private Burn? Plan(int i, Orbit o, double now, bool commit)
        {
            double mu = o.referenceBody.gravParameter, a = _state.TargetSma;
            switch (_state.Stage[i])
            {
                case CorrectionStage.Plane:
                {
                    if (!_state.MatchPlanes) return Skip(commit, "planes not matched.");
                    var n2 = TargetNormal(o, now);
                    var n1 = Normal(o, now);
                    double angle = Vector3d.Angle(n1, n2);
                    if (angle < PlaneToleranceDeg) return Skip(commit, $"{angle:F3}° from the common plane, no plane change.");
                    double ut = NextPlaneCrossing(o, n2, now + MinNodeLead);
                    double speed = o.getOrbitalVelocityAtUT(ut).magnitude;
                    double rad = angle * Math.PI / 180.0;
                    double prograde = speed * (Math.Cos(rad) - 1.0), normal = speed * Math.Sin(rad);
                    if (PlaneGrouping.PlaneChangeDv(speed, angle) < MinBurn)
                        return Skip(commit, $"{angle:F3}° from the common plane: too small to fly.");
                    return new Burn(ut, prograde, $"plane change, {angle:F2}° into the common plane " +
                                    $"(i {_state.PlaneInclination:F2}°, Ω {_state.PlaneLan:F2}°): " +
                                    $"{PlaneGrouping.PlaneChangeDv(speed, angle):F3} m/s.", normal);
                }
                case CorrectionStage.Transfer:
                {
                    if (!StationKeeping.NeedsTransfer(o.ApR, o.PeR, a)) return Skip(commit, "close to the target orbit, no transfer.");
                    var t = _transfer.Plan(mu, o.ApR, o.PeR, a);
                    double ut = _planner.NextPassUT(now, t.FirstBurnAt == Apsis.Periapsis ? o.timeToPe : o.timeToAp,
                                                    o.period, MinNodeLead);
                    return Make(commit, ut, t.FirstDv,
                                $"transfer burn, {t.FirstDv:F2} m/s at {t.FirstBurnAt.ToString().ToLowerInvariant()}.");
                }
                case CorrectionStage.Circularize:
                {
                    if (!StationKeeping.NeedsTransfer(o.ApR, o.PeR, a)) return Skip(commit, "close to the target orbit, no circularization.");
                    bool atAp = Math.Abs(o.ApR - a) <= Math.Abs(o.PeR - a);
                    double ut = _planner.NextPassUT(now, atAp ? o.timeToAp : o.timeToPe, o.period, MinNodeLead);
                    double dv = _planner.CircularizeAt(mu, atAp ? o.ApR : o.PeR, o.semiMajorAxis);
                    return Make(commit, ut, dv, $"circularize, {dv:F2} m/s at {(atAp ? "apoapsis" : "periapsis")}.");
                }
                case CorrectionStage.Phase:
                {
                    double ut = now + NodeLead;
                    double error = ErrorAt(i, o, ut);
                    if (StationKeeping.PhaseOk(error)) return Skip(commit, $"{error:+0.00;-0.00}° from its slot, no phasing.");
                    double r = o.getRelativePositionAtUT(ut).magnitude;
                    // The quickest safe phasing whose return doesn't fall on another
                    // satellite's: one of them would miss its pass and over-correct.
                    PhasingPlan? p = null;
                    for (int k = _state.MinOrbits; k <= StationKeeping.MaxPhasingOrbits; k = p.Orbits + 1)
                    {
                        p = _phasing.Choose(mu, r, o.semiMajorAxis, a, error, _state.MinRadius, _state.MaxRadius, k);
                        if (p == null || !ReturnClashes(i, ut + p.Duration)) break;
                    }
                    if (p == null)
                    {
                        Fail($"{Name(i)}: no safe phasing orbit for {error:F1}° within {StationKeeping.MaxPhasingOrbits} orbits.");
                        return null;
                    }
                    if (commit)
                    {
                        _state.PhaseUT[i] = ut;
                        _state.PhaseOrbits[i] = p.Orbits;
                    }
                    return Make(commit, ut, p.FirstDv,
                                $"phasing, {error:+0.00;-0.00}° off its slot, removing {p.ErrorDeg:F2}° in {p.Orbits} " +
                                $"orbit(s) of {_formatTime(p.Period)} (other apsis " +
                                $"{(p.OtherApsisRadius - o.referenceBody.Radius) / 1000.0:F1} km): {p.FirstDv:F3} m/s.");
                }
                case CorrectionStage.PhaseReturn:
                {
                    if (double.IsNaN(_state.PhaseUT[i])) return Skip(commit, "no phasing orbit to come back from.");
                    // Back at the first burn's point after the phasing orbits; when that
                    // pass was missed (another satellite's burn), the next one.
                    double ut = _state.PhaseUT[i] + _state.PhaseOrbits[i] * o.period;
                    while (ut < now + MinNodeLead) ut += o.period;
                    double r = o.getRelativePositionAtUT(ut).magnitude;
                    double dv = _phasing.TrimDv(mu, r, o.semiMajorAxis, a);
                    return Make(commit, ut, dv, $"end of phasing, {dv:F3} m/s back to the target period.");
                }
                case CorrectionStage.Trim:
                {
                    if (StationKeeping.PeriodOk(o.period, _state.TargetPeriod))
                        return Skip(commit, $"period within {StationKeeping.PeriodToleranceRel:P2} of the target, no trim.");
                    double ut = now + NodeLead;
                    double dv = _phasing.TrimDv(mu, o.getRelativePositionAtUT(ut).magnitude, o.semiMajorAxis, a);
                    return Make(commit, ut, dv,
                                $"period trim, {o.period - _state.TargetPeriod:+0.0;-0.0} s off, {dv:F3} m/s.");
                }
                default:
                    return null;
            }

            Burn? Make(bool log, double ut, double dv, string text)
            {
                if (Math.Abs(dv) < MinBurn) return Skip(log, text + " Too small to fly: skipped.");
                return new Burn(ut, dv, text);
            }

            Burn? Skip(bool log, string why)
            {
                if (log) Log($"{Name(i)}: {why}");
                return null;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        // ── Planes ───────────────────────────────────────────────────────────
        // Normals are taken in the Orbit class's own frame (position × velocity), for
        // the satellites and for the common plane alike, so only angles and signs of
        // dot products are used: they don't depend on how that frame is oriented.

        private static Vector3d Normal(Orbit o, double ut) =>
            Vector3d.Cross(o.getRelativePositionAtUT(ut), o.getOrbitalVelocityAtUT(ut)).normalized;

        // The common plane's normal: from a circular orbit in that plane.
        private Vector3d TargetNormal(Orbit o, double ut)
        {
            var plane = new Orbit(_state.PlaneInclination, 0.0, o.semiMajorAxis, _state.PlaneLan, 0.0, 0.0, ut,
                                  o.referenceBody);
            return Normal(plane, ut);
        }

        // Next time after `from` the satellite crosses the common plane (the line of
        // nodes between the two planes): where position · normal changes sign.
        private static double NextPlaneCrossing(Orbit o, Vector3d n2, double from)
        {
            const int steps = 90;
            double F(double t) => Vector3d.Dot(o.getRelativePositionAtUT(t), n2);
            double dt = o.period / steps, t0 = from, f0 = F(t0);
            for (int k = 1; k <= steps; ++k)
            {
                double t1 = from + k * dt, f1 = F(t1);
                if (Math.Sign(f1) != Math.Sign(f0))
                {
                    for (int j = 0; j < 50; ++j)
                    {
                        double tm = (t0 + t1) / 2.0, fm = F(tm);
                        if (Math.Sign(fm) == Math.Sign(f0)) { t0 = tm; f0 = fm; } else t1 = tm;
                    }
                    return (t0 + t1) / 2.0;
                }
                t0 = t1; f0 = f1;
            }
            return from;  // already in the plane: any time will do
        }

        // The normal component's sign depends on which node this is: check the orbit
        // KSP predicts after the node, and flip the burn when it tilts the wrong way.
        private void CheckPlaneChangeSign(PatchedConicSolver solver, ManeuverNode node, Burn burn)
        {
            double Off()
            {
                solver.UpdateFlightPlan();
                var next = node.nextPatch;
                return next == null ? double.NaN
                    : PlaneGrouping.RelativeInclinationDeg(new OrbitPlane(next.inclination, next.LAN), _state.Plane);
            }
            double plus = Off();
            node.OnGizmoUpdated(new Vector3d(0.0, -burn.Normal, burn.Dv), burn.UT);
            double minus = Off();
            if (double.IsNaN(minus) || plus <= minus)
            {
                node.OnGizmoUpdated(new Vector3d(0.0, burn.Normal, burn.Dv), burn.UT);
                solver.UpdateFlightPlan();
            }
            Log($"Plane change: {Math.Min(plus, double.IsNaN(minus) ? plus : minus):F3}° from the common plane " +
                $"after the node (normal +: {plus:F3}°, −: {minus:F3}°).");
        }

        // Too close to another satellite's phasing return to fly both.
        private bool ReturnClashes(int i, double ut)
        {
            for (int j = 0; j < _state.Members.Length; ++j)
            {
                if (j == i || _state.Stage[j] != CorrectionStage.PhaseReturn || double.IsNaN(_state.PhaseUT[j])) continue;
                var v = Member(j);
                if (v == null) continue;
                double other = _state.PhaseUT[j] + _state.PhaseOrbits[j] * v.orbit.period;
                if (Math.Abs(other - ut) < ReturnGap) return true;
            }
            return false;
        }

        private Vessel? Member(int i)
        {
            uint id = _state.Members[i];
            return FlightGlobals.Vessels.FirstOrDefault(v => v != null && v.persistentId == id);
        }

        private string Name(int i) => Member(i)?.vesselName ?? $"satellite {_state.Members[i]}";

        private double ErrorAt(int i, Orbit o, double ut) =>
            _state.Frame.ErrorDeg(MeanLongitude.At(o, ut), _state.SlotOf[i], ut);

        private double SlotError(int i, double ut)
        {
            var v = Member(i);
            return v == null ? double.NaN : ErrorAt(i, v.orbit, ut);
        }

        private double Drift(int i)
        {
            var v = Member(i);
            return v == null ? double.NaN : StationKeeping.DriftDegPerOrbit(v.orbit.period, _state.TargetPeriod);
        }

        private void Fail(string error)
        {
            Stop();
            State = RunState.Failed;
            Error = error;
            Status = "Stopped" + (CurrentStep.Length > 0 ? $" ({CurrentStep})" : "") + ": " + error;
            Log(Status);
        }

        private void Stop()
        {
            _executor?.Finish();
            if (TimeWarp.CurrentRateIndex > 0) TimeWarp.SetRate(0, true, false);
        }

        private static string Describe(CorrectionStage s) => s switch
        {
            CorrectionStage.Plane       => "plane change into the common plane",
            CorrectionStage.Transfer    => "transfer towards the target orbit",
            CorrectionStage.Circularize => "circularize at the target orbit",
            CorrectionStage.Phase       => "phasing burn",
            CorrectionStage.PhaseReturn => "end of phasing",
            CorrectionStage.Trim        => "period trim",
            _                           => "done",
        };

        private static void Log(string text) => Debug.Log("[RelayOrbiter] " + text);
    }

    internal static class MeanLongitude
    {
        // Ω + ω + M in degrees: advances uniformly, so slots and drift are linear in it.
        public static double At(Orbit o, double ut) =>
            StationKeeping.Mod360(o.LAN + o.argumentOfPeriapsis + 360.0 * o.getObtAtUT(ut) / o.period);
    }
}

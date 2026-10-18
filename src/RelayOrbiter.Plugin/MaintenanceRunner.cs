using System;
using System.Collections.Generic;
using System.Linq;
using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Puts the active satellite back in its slot of the constellation, one Tick per
    // frame: shape (Hohmann to the target orbit, only when far off), phasing orbit
    // (out and back), period trim. Each step plans its node from the orbits as they
    // are then, so it corrects whatever the previous burns left.
    internal sealed class MaintenanceRunner
    {
        public enum RunState { Running, Done, Failed, Aborted }

        private enum Kind { Transfer, Circularize, Phase, PhaseReturn, Trim }

        private static readonly Kind[] Sequence =
            { Kind.Transfer, Kind.Circularize, Kind.Phase, Kind.PhaseReturn, Kind.Trim };

        private const double NodeLead = 120.0;  // s from now to a node placed "now"
        private const double MinNodeLead = 60.0;
        private const double MinBurn = 2 * NodeBurn.PreciseDv;  // m/s: smaller burns are skipped

        private readonly Vessel _vessel;
        private readonly IReadOnlyList<Vessel> _members;  // the constellation, _vessel included
        private readonly int _active, _slots;
        private readonly int? _reference;
        private readonly double _targetSma, _minRadius, _maxRadius;
        private readonly int _minOrbits;
        private readonly TransferPlanner _transfer;
        private readonly PhasingPlanner _phasing;
        private readonly IDeploymentPlanner _planner;
        private readonly Func<double, string> _formatTime;

        private int _index;
        private bool _started;
        private NodeExecutor? _executor;
        private double _phaseUT = double.NaN;
        private int _phaseOrbits;

        public MaintenanceRunner(Vessel vessel, IReadOnlyList<Vessel> members, int slots, int? reference,
                                 double targetSma, double minRadius, double maxRadius, int minOrbits,
                                 TransferPlanner transfer, PhasingPlanner phasing, IDeploymentPlanner planner,
                                 Func<double, string> formatTime)
        {
            _vessel     = vessel;
            _members    = members;
            _active     = members.ToList().IndexOf(vessel);
            _slots      = slots;
            _reference  = reference;
            _targetSma  = targetSma;
            _minRadius  = minRadius;
            _maxRadius  = maxRadius;
            _minOrbits  = minOrbits;
            _transfer   = transfer;
            _phasing    = phasing;
            _planner    = planner;
            _formatTime = formatTime;
            if (_active < 0) throw new ArgumentException("The vessel is not in the constellation.", nameof(vessel));
            VesselParts.ActivateEngines(vessel);
            Log($"Correcting {vessel.vesselName}: {members.Count} satellites in {slots} slots, reference " +
                $"{(reference.HasValue ? members[reference.Value].vesselName : "best fit")}, " +
                $"target orbit {(targetSma - vessel.mainBody.Radius) / 1000.0:F2} km.");
            double now = Planetarium.GetUniversalTime();
            var fit = FitAt(now);
            for (int i = 0; i < members.Count; ++i)
            {
                var o = members[i].orbit;
                Log($"  {members[i].vesselName}: L {MeanLongitude.At(o, now):F2}° (Ω {o.LAN:F2}, ω " +
                    $"{o.argumentOfPeriapsis:F2}), slot #{fit.SlotOf[i] + 1}, off {fit.ErrorsDeg[i]:+0.00;-0.00}°, " +
                    $"a {o.semiMajorAxis:F0} m, e {o.eccentricity:F5}, i {o.inclination:F2}°, T {o.period:F1} s");
            }
        }

        public RunState State  { get; private set; } = RunState.Running;
        public string   Status { get; private set; } = "";
        public string   Error  { get; private set; } = "";
        public int      Step   => Math.Min(_index + 1, Sequence.Length);
        public int      Steps  => Sequence.Length;
        public string   CurrentStep => _index < Sequence.Length ? Describe(Sequence[_index]) : "Done";

        public bool CanFastForward => State == RunState.Running && _executor != null && _executor.IsWaiting;

        public void FastForward() => _executor?.FastForward();

        public void Tick()
        {
            if (State != RunState.Running) return;
            if (_index >= Sequence.Length)
            {
                State = RunState.Done;
                var fit = FitAt(Planetarium.GetUniversalTime());
                Status = $"{_vessel.vesselName} corrected: {fit.ErrorsDeg[_active]:+0.00;-0.00}° from its slot, " +
                         $"drift {Drift(_vessel.orbit):+0.000;-0.000}°/orbit.";
                Log(Status);
                return;
            }
            if (FlightGlobals.ActiveVessel != _vessel) { Fail("The satellite is no longer the active vessel."); return; }

            var kind = Sequence[_index];
            if (!_started)
            {
                _started = true;
                Log($"Step {Step}/{Steps}: {Describe(kind)}");
                _executor = CreateNode(kind) ? new NodeExecutor(_vessel, _formatTime, precise: true) : null;
                if (State != RunState.Running) return;
            }
            if (_executor != null)
            {
                var r = _executor.Tick();
                Status = _executor.Status;
                if (r == NodeExecutor.Result.Failed) { Fail(_executor.Error); return; }
                if (r != NodeExecutor.Result.Done) return;
                _executor = null;
            }
            _index++;
            _started = false;
        }

        public void Abort()
        {
            if (State != RunState.Running) return;
            Stop();
            State = RunState.Aborted;
            Status = $"Aborted at step {Step}/{Steps} ({CurrentStep}).";
            Log(Status);
        }

        // ── Nodes ────────────────────────────────────────────────────────────

        // True when a node was created (and must be flown).
        private bool CreateNode(Kind kind)
        {
            var solver = _vessel.patchedConicSolver;
            if (solver == null) { Fail("Maneuver nodes are not available."); return false; }
            foreach (var n in solver.maneuverNodes.ToList()) n.RemoveSelf();

            var o = _vessel.orbit;
            double mu = o.referenceBody.gravParameter;
            double now = Planetarium.GetUniversalTime();
            switch (kind)
            {
                case Kind.Transfer:
                {
                    if (!StationKeeping.NeedsTransfer(o.ApR, o.PeR, _targetSma))
                        return Skip("Close to the target orbit: no transfer needed.");
                    var t = _transfer.Plan(mu, o.ApR, o.PeR, _targetSma);
                    double timeTo = t.FirstBurnAt == Apsis.Periapsis ? o.timeToPe : o.timeToAp;
                    double ut = _planner.NextPassUT(now, timeTo, o.period, MinNodeLead);
                    return Add(solver, ut, t.FirstDv,
                               $"Transfer burn: {t.FirstDv:F2} m/s at {t.FirstBurnAt.ToString().ToLowerInvariant()}.");
                }
                case Kind.Circularize:
                {
                    if (!StationKeeping.NeedsTransfer(o.ApR, o.PeR, _targetSma))
                        return Skip("Close to the target orbit: no circularization needed.");
                    bool atAp = Math.Abs(o.ApR - _targetSma) <= Math.Abs(o.PeR - _targetSma);
                    double ut = _planner.NextPassUT(now, atAp ? o.timeToAp : o.timeToPe, o.period, MinNodeLead);
                    double dv = _planner.CircularizeAt(mu, atAp ? o.ApR : o.PeR, o.semiMajorAxis);
                    return Add(solver, ut, dv, $"Circularize: {dv:F2} m/s at {(atAp ? "apoapsis" : "periapsis")}.");
                }
                case Kind.Phase:
                {
                    double ut = now + NodeLead;
                    double error = FitAt(ut).ErrorsDeg[_active];
                    if (StationKeeping.PhaseOk(error))
                        return Skip($"{error:+0.00;-0.00}° from its slot: no phasing needed.");
                    double r = o.getRelativePositionAtUT(ut).magnitude;
                    var p = _phasing.Choose(mu, r, o.semiMajorAxis, _targetSma, error, _minRadius, _maxRadius, _minOrbits);
                    if (p == null)
                    {
                        Fail($"No safe phasing orbit for {error:F1}° within {StationKeeping.MaxPhasingOrbits} orbits.");
                        return false;
                    }
                    _phaseUT = ut;
                    _phaseOrbits = p.Orbits;
                    return Add(solver, ut, p.FirstDv,
                               $"Phasing: {error:+0.00;-0.00}° off its slot, removing {p.ErrorDeg:F2}° in " +
                               $"{p.Orbits} orbit(s) of {_formatTime(p.Period)} (other apsis " +
                               $"{(p.OtherApsisRadius - o.referenceBody.Radius) / 1000.0:F1} km): {p.FirstDv:F2} m/s.");
                }
                case Kind.PhaseReturn:
                {
                    if (double.IsNaN(_phaseUT)) return Skip("No phasing orbit to come back from.");
                    // Back at the first burn's point after the phasing orbits.
                    double ut = _phaseUT + _phaseOrbits * o.period;
                    if (ut < now + MinNodeLead) { Fail("Missed the end of the phasing orbit."); return false; }
                    double r = o.getRelativePositionAtUT(ut).magnitude;
                    double dv = _phasing.TrimDv(mu, r, o.semiMajorAxis, _targetSma);
                    return Add(solver, ut, dv, $"End of phasing: {dv:F3} m/s, back to the target period.");
                }
                default:  // Trim
                {
                    double target = TargetPeriod(mu);
                    if (StationKeeping.PeriodOk(o.period, target))
                        return Skip($"Period within {StationKeeping.PeriodToleranceRel:P2} of the target: no trim needed.");
                    double ut = now + NodeLead;
                    double r = o.getRelativePositionAtUT(ut).magnitude;
                    double dv = _phasing.TrimDv(mu, r, o.semiMajorAxis, _targetSma);
                    return Add(solver, ut, dv, $"Period trim: {o.period - target:+0.0;-0.0} s off, {dv:F3} m/s.");
                }
            }
        }

        private bool Add(PatchedConicSolver solver, double ut, double dv, string log)
        {
            if (Math.Abs(dv) < MinBurn) return Skip(log + " Too small to fly: skipped.");
            var node = solver.AddManeuverNode(ut);
            node.OnGizmoUpdated(new Vector3d(0.0, 0.0, dv), ut);  // x radial, y normal, z prograde
            Log(log);
            return true;
        }

        private static bool Skip(string why)
        {
            Log(why);
            return false;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private SlotFit FitAt(double ut) =>
            StationKeeping.Fit(_members.Select(v => MeanLongitude.At(v.orbit, ut)).ToList(), _slots,
                               _reference, _reference.HasValue ? (int?)null : _active);

        private double TargetPeriod(double mu) => 2.0 * Math.PI * Math.Sqrt(_targetSma * _targetSma * _targetSma / mu);

        private double Drift(Orbit o) =>
            StationKeeping.DriftDegPerOrbit(o.period, TargetPeriod(o.referenceBody.gravParameter));

        private void Fail(string error)
        {
            Stop();
            State = RunState.Failed;
            Error = error;
            Status = $"Stopped at step {Step}/{Steps} ({CurrentStep}): {error}";
            Log(Status);
        }

        private void Stop()
        {
            _executor?.Finish();
            if (TimeWarp.CurrentRateIndex > 0) TimeWarp.SetRate(0, true, false);
        }

        private static string Describe(Kind k) => k switch
        {
            Kind.Transfer    => "Transfer towards the target orbit",
            Kind.Circularize => "Circularize at the target orbit",
            Kind.Phase       => "Phasing burn: drift back to the slot",
            Kind.PhaseReturn => "End of phasing: back to the target period",
            _                => "Trim the period",
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

using System;
using System.Collections.Generic;
using System.Linq;
using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Executes a DeploymentSequence step by step, one Tick per frame.
    internal sealed class DeploymentRunner
    {
        public enum RunState { Running, Done, Failed, Aborted }

        private const double SwitchTimeout = 30.0;  // s for a vessel switch to complete
        private const float  MaxSpinRate   = 0.02f; // rad/s: stopped rotating
        private const double AlignedHold   = 1.0;   // s aligned and still before releasing
        private const double MinClearance  = 15.0;  // m between carrier and satellite before switching
        private const double MinTurnDeg    = 60.0;  // carrier turn that moves its end off the satellite's path
        private const double ClearDelay    = 2.0;   // s after release before the carrier starts turning
        // KSP unloads orbiting vessels 2.5 km from the active one; switching back to an
        // unloaded carrier reloads the scene and ends the run. Keep it loaded meanwhile.
        private const float  LoadRange     = 22_500f;  // m
        private const float  UnloadRange   = 25_000f;  // m

        private readonly IReadOnlyList<SequenceStep> _steps;
        private readonly Vessel _carrier;
        private readonly uint[] _separators;      // flight ids, release order
        private readonly Part[] _satelliteParts;  // command parts: identify each satellite's vessel
        private readonly string[] _names;
        private readonly uint[][] _satellitePartIds;  // every part each release detaches
        private readonly bool _extendPanels, _topUp;
        private Vector3 _clearFrom;
        private VesselAutopilot.AutopilotMode? _clearMode;
        private double _clearStart;
        private readonly IDeploymentPlanner _planner;
        private readonly Func<double, string> _formatTime;

        private int _index;
        private bool _started;
        private double _until;
        private Vessel? _target;
        private NodeExecutor? _executor;
        private readonly Warp _warp = new Warp();
        private bool _orienting;  // false when orientation was skipped
        private VesselAutopilot.AutopilotMode _orientMode;
        private double _alignedSince = double.NaN;
        // Parking burn time, fixed at the first warp: on a near-circular orbit the
        // apoapsis is ill-defined, and SAT-1's separation push can move it anywhere.
        private double _parkingUT = double.NaN;
        private bool _released;
        private readonly float _savedLoad, _savedUnload;

        public DeploymentRunner(IReadOnlyList<SequenceStep> steps, Vessel carrier,
                                uint[] separators, Part[] satelliteParts, string[] names,
                                uint[][] satellitePartIds, bool extendPanels, bool topUp,
                                IDeploymentPlanner planner, Func<double, string> formatTime)
        {
            _steps          = steps;
            _carrier        = carrier;
            _separators     = separators;
            _satelliteParts = satelliteParts;
            _names          = names;
            _satellitePartIds = satellitePartIds;
            _extendPanels   = extendPanels;
            _topUp          = topUp;
            _planner        = planner;
            _formatTime     = formatTime;

            var orbit = carrier.vesselRanges.orbit;
            _savedLoad   = orbit.load;
            _savedUnload = orbit.unload;
            if (orbit.unload < UnloadRange)
            {
                orbit.load   = LoadRange;
                orbit.unload = UnloadRange;
            }
            Log($"Deployment started. Carrier stays loaded up to {orbit.load / 1000f:F1} km " +
                $"(was {_savedLoad / 1000f:F1} km).");
        }

        public RunState State  { get; private set; } = RunState.Running;
        public string   Status { get; private set; } = "";
        public string   Error  { get; private set; } = "";
        public int      Step   => Math.Min(_index + 1, _steps.Count);
        public int      Steps  => _steps.Count;
        public string   CurrentStep => _index < _steps.Count ? Describe(_steps[_index]) : "Done";

        public void Tick()
        {
            if (State != RunState.Running) return;
            if (_index >= _steps.Count)
            {
                State = RunState.Done;
                RestoreRanges();
                Status = "Constellation deployed: 3 satellites, 120° apart.";
                Log(Status);
                return;
            }

            var step = _steps[_index];
            if (!_started)
            {
                _started = true;
                Log($"Step {Step}/{Steps}: {Describe(step)}");
                Begin(step);
                if (State != RunState.Running) return;
            }
            if (Advance(step))
            {
                _index++;
                _started = false;
            }
        }

        // True while the current step is waiting on time warp.
        public bool CanFastForward =>
            State == RunState.Running && _index < _steps.Count &&
            (_steps[_index].Kind == StepKind.WarpToApoapsis ||
             _steps[_index].Kind == StepKind.ExecuteNode && _executor != null && _executor.IsWaiting);

        // Ask for time warp right away (the user's "Fast forward").
        public void FastForward()
        {
            _warp.Now();
            _executor?.FastForward();
            Log("Fast forward requested.");
        }

        public void Abort()
        {
            if (State != RunState.Running) return;
            Stop();
            State = RunState.Aborted;
            Status = $"Aborted at step {Step}/{Steps} ({CurrentStep}).";
            Log(Status);
        }

        // ── Steps ────────────────────────────────────────────────────────────

        private void Begin(SequenceStep s)
        {
            double now = Planetarium.GetUniversalTime();
            switch (s.Kind)
            {
                case StepKind.WarpToApoapsis:
                {
                    var o = _carrier.orbit;
                    double apUT = _planner.NextApoapsisUT(now, o.timeToAp, o.period, s.Seconds + 10.0);
                    if (_index == 0) _parkingUT = apUT;
                    _until = apUT - s.Seconds;
                    _warp.Now();
                    break;
                }
                case StepKind.OrientForRelease:
                    BeginOrient(s.Satellite, now);
                    break;

                case StepKind.Release:
                    _released = false;  // fired in Advance, once the carrier has physics
                    break;
                case StepKind.Wait:
                    _until = now + s.Seconds;
                    break;

                case StepKind.ClearSatellite:
                    BeginClear(now, s.Seconds);
                    break;

                case StepKind.SwitchToSatellite:
                    _target = _satelliteParts[s.Satellite].vessel;
                    if (_target == null || _target == _carrier) { Fail($"{Sat(s.Satellite)} did not separate."); break; }
                    _until = now + SwitchTimeout;
                    FlightGlobals.ForceSetActiveVessel(_target);
                    break;

                case StepKind.SwitchToCarrier:
                    if (!_carrier.loaded) { Fail("The carrier drifted out of range and was unloaded."); break; }
                    _target = _carrier;
                    _until = now + SwitchTimeout;
                    FlightGlobals.ForceSetActiveVessel(_carrier);
                    break;

                case StepKind.CreateParkingNode:
                case StepKind.CreateCircularizeNode:
                    CreateNode(s);
                    break;

                case StepKind.ExecuteNode:
                    _executor = new NodeExecutor(FlightGlobals.ActiveVessel, _formatTime);
                    break;
            }
        }

        // True when the step is complete.
        private bool Advance(SequenceStep s)
        {
            double now = Planetarium.GetUniversalTime();
            switch (s.Kind)
            {
                case StepKind.WarpToApoapsis:
                    _warp.Towards(_until);
                    Status = $"Warping: {_formatTime(Math.Max(0.0, _until - now))} to go";
                    // Rate 1x is not enough: while warp winds down the carrier is still on
                    // rails, and separating parts on rails gives the new vessel a NaN orbit.
                    return now >= _until - 1.0 && TimeWarp.CurrentRateIndex == 0 && !_carrier.packed;

                case StepKind.Release:
                {
                    if (_carrier.packed) { Status = "Waiting for physics before the release"; return false; }
                    if (_released) return true;
                    _released = true;
                    var part = VesselParts.Find(_carrier, _separators[s.Satellite]);
                    if (part == null || !VesselParts.Release(part))
                    {
                        Fail($"Could not release {Sat(s.Satellite)}.");
                        return false;
                    }
                    return true;
                }

                case StepKind.Wait:
                    Status = $"Separating ({Math.Max(0.0, _until - now):F0} s)";
                    return now >= _until;

                case StepKind.OrientForRelease:
                    return AdvanceOrient(s.Satellite, now);

                case StepKind.ClearSatellite:
                    return AdvanceClear(s.Satellite, now);

                case StepKind.SwitchToSatellite:
                case StepKind.SwitchToCarrier:
                    if (FlightGlobals.ActiveVessel == _target && !_target!.packed)
                    {
                        if (s.Kind == StepKind.SwitchToSatellite) VesselParts.ActivateEngines(_target);
                        return true;
                    }
                    if (now > _until) Fail($"Could not switch to {_target!.vesselName}.");
                    Status = $"Switching to {_target!.vesselName}";
                    return false;

                case StepKind.ExecuteNode:
                {
                    var r = _executor!.Tick();
                    Status = _executor.Status;
                    if (r == NodeExecutor.Result.Failed) Fail(_executor.Error);
                    return r == NodeExecutor.Result.Done;
                }

                default:  // Release, Create*Node: done in Begin
                    return true;
            }
        }

        // ── Orientation for release ──────────────────────────────────────────

        // Separation axis: from the separator to the satellite's command part.
        private Vector3 SeparationAxis(int k)
        {
            var separator = VesselParts.Find(_carrier, _separators[k]);
            return separator == null ? Vector3.zero
                : _satelliteParts[k].transform.position - separator.transform.position;
        }

        private void BeginOrient(int k, double now)
        {
            _until = now + SeparationAttitude.TimeoutSeconds;
            _alignedSince = double.NaN;
            if (_extendPanels)
            {
                int n = VesselParts.ExtendSolarPanels(_carrier, _satellitePartIds[k]);
                if (n > 0) Log($"Extending {n} solar panel(s) on {Sat(k)}.");
            }
            if (_topUp) TopUp(k);
            var axis = SeparationAxis(k);
            var ap = _carrier.Autopilot;
            var mode = SeparationAttitude.Choose(Vector3.Angle(_carrier.ReferenceTransform.up, axis))
                       == SeparationMode.Normal
                ? VesselAutopilot.AutopilotMode.Normal
                : VesselAutopilot.AutopilotMode.Prograde;
            _orientMode = mode;
            _orienting = axis != Vector3.zero && ap.CanSetMode(mode);
            if (!_orienting)
            {
                Log($"Can't point {mode} (SAS level too low?): releasing {Sat(k)} without orienting.");
                return;
            }
            _carrier.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            ap.SetMode(mode);
            Log($"Pointing {mode} so {Sat(k)}'s separation push is perpendicular to the velocity.");
        }

        private bool AdvanceOrient(int k, double now)
        {
            if (_carrier.packed) { Status = "Waiting for physics"; return false; }
            if (!_orienting) return true;
            // KSP drops the mode if SAS wasn't on yet when it was set: keep applying it.
            _carrier.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            if (_carrier.Autopilot.Mode != _orientMode) _carrier.Autopilot.SetMode(_orientMode);
            double angle = Vector3.Angle(SeparationAxis(k), (Vector3)_carrier.obt_velocity);
            bool still = _carrier.angularVelocity.magnitude < MaxSpinRate;
            if (SeparationAttitude.IsReady(angle) && still)
            {
                // Hold it for a moment: one good reading can be a fluke.
                if (double.IsNaN(_alignedSince)) _alignedSince = now;
                if (now - _alignedSince >= AlignedHold)
                {
                    Log($"Oriented: separation axis {angle:F1}° from the velocity.");
                    return true;
                }
            }
            else _alignedSince = double.NaN;
            if (now > _until)
            {
                Log($"Orientation timed out (axis {angle:F1}° from the velocity): releasing anyway.");
                return true;
            }
            Status = $"Orienting for release: axis {angle:F1}° from the velocity (target 90°)";
            return false;
        }

        // Fill SAT-k's tanks right before its release: the docking ports cross-feed,
        // so the carrier's burns drain the satellites still attached. Only the
        // carrier's own tanks are used, never those of satellites still on board.
        private void TopUp(int k)
        {
            var onBoard = new HashSet<uint>(_satellitePartIds.Skip(k).SelectMany(ids => ids));
            var satTanks = VesselParts.Tanks(_carrier, _satellitePartIds[k]);
            var carrierTanks = VesselParts.Tanks(_carrier,
                _carrier.parts.Select(p => p.flightID).Where(id => !onBoard.Contains(id)));
            var transfers = ResourceTopUp.Plan(satTanks, carrierTanks);
            if (transfers.Count > 0)
            {
                VesselParts.Apply(_carrier, transfers);
                Log($"Topped up {Sat(k)}: " + string.Join(", ", transfers.GroupBy(t => t.Resource)
                    .Select(g => $"{g.Key} +{g.Sum(t => t.Amount):F1}")));
            }
            double fill = ResourceTopUp.FillFraction(VesselParts.Tanks(_carrier, _satellitePartIds[k]));
            if (fill < 0.999) Log($"{Sat(k)}'s tanks are only {fill:P0} full: the carrier has no more fuel for it.");
        }

        // ── Getting clear of a released satellite ────────────────────────────

        private void BeginClear(double now, double timeout)
        {
            _until = now + timeout;
            _clearStart = now;
            _clearFrom = _carrier.ReferenceTransform.up;
            // Turn 90°: from normal (axial mounts) to prograde, or from prograde (radial) to normal.
            var mode = _orientMode == VesselAutopilot.AutopilotMode.Normal
                ? VesselAutopilot.AutopilotMode.Prograde
                : VesselAutopilot.AutopilotMode.Normal;
            _clearMode = _carrier.Autopilot.CanSetMode(mode) ? mode : (VesselAutopilot.AutopilotMode?)null;
        }

        private bool AdvanceClear(int k, double now)
        {
            if (_carrier.packed) { Status = "Waiting for physics"; return false; }
            var sat = _satelliteParts[k].vessel;
            if (sat == null || sat == _carrier) { Fail($"{Sat(k)} did not separate."); return false; }

            if (_clearMode != null && now - _clearStart >= ClearDelay)
            {
                _carrier.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
                if (_carrier.Autopilot.Mode != _clearMode.Value) _carrier.Autopilot.SetMode(_clearMode.Value);
            }
            double turned = Vector3.Angle(_carrier.ReferenceTransform.up, _clearFrom);
            double distance = (sat.CoMD - _carrier.CoMD).magnitude;
            bool turnedEnough = _clearMode == null || turned >= MinTurnDeg;

            if (turnedEnough && distance >= MinClearance)
            {
                Log($"Clear of {Sat(k)}: carrier turned {turned:F0}°, satellite {distance:F1} m away.");
                return true;
            }
            if (now > _until)
            {
                Log($"Clearing timed out (carrier turned {turned:F0}°, satellite {distance:F1} m away): switching anyway.");
                return true;
            }
            Status = $"Getting clear: carrier turned {turned:F0}°, {Sat(k)} {distance:F1} m away";
            return false;
        }

        private void CreateNode(SequenceStep s)
        {
            var vessel = FlightGlobals.ActiveVessel;
            var solver = vessel.patchedConicSolver;
            if (solver == null) { Fail("Maneuver nodes are not available."); return; }

            var o = vessel.orbit;
            double now = Planetarium.GetUniversalTime();
            double mu = o.referenceBody.gravParameter;
            double ut, dv;
            if (s.Kind == StepKind.CreateParkingNode)
            {
                // At the time fixed before SAT-1's release, using the radius there
                // (vis-viva holds anywhere on the orbit): that point becomes the
                // parking orbit's apoapsis.
                ut = _parkingUT;
                if (double.IsNaN(ut) || ut - now < 5.0)
                {
                    Fail("The parking burn's time has already passed.");
                    return;
                }
                double r = o.getRelativePositionAtUT(ut).magnitude;
                dv = _planner.Plan(mu, r, o.semiMajorAxis, ut).ParkingDeltaV;
            }
            else
            {
                // Satellites are on the parking orbit, whose apoapsis is well defined.
                if (o.timeToAp > o.period / 2.0)
                {
                    Fail($"{vessel.vesselName} is already past apoapsis; the release came too late.");
                    return;
                }
                ut = now + o.timeToAp;
                dv = _planner.CircularizeAtApoapsis(mu, o.ApR, o.semiMajorAxis);
            }

            var node = solver.AddManeuverNode(ut);
            node.OnGizmoUpdated(new Vector3d(0.0, 0.0, dv), ut);  // x radial, y normal, z prograde
            Log($"Node for {vessel.vesselName}: {dv:F1} m/s prograde at apoapsis.");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

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
            RestoreRanges();
            _executor?.Finish();
            if (TimeWarp.CurrentRateIndex > 0) TimeWarp.SetRate(0, true, false);
        }

        private void RestoreRanges()
        {
            if (_carrier == null) return;
            var orbit = _carrier.vesselRanges.orbit;
            orbit.load   = _savedLoad;
            orbit.unload = _savedUnload;
        }

        private string Sat(int k) => $"SAT-{k + 1} ({_names[k]})";

        private string Describe(SequenceStep s) => s.Kind switch
        {
            StepKind.WarpToApoapsis        => $"Warp to {s.Seconds:F0} s before apoapsis",
            StepKind.OrientForRelease      => $"Orient the carrier to release {Sat(s.Satellite)}",
            StepKind.Release               => s.Satellite == 0
                                                  ? $"Release {Sat(0)}: it stays on this circular orbit, no burn needed"
                                                  : $"Release {Sat(s.Satellite)}",
            StepKind.Wait                  => "Let the vessels separate",
            StepKind.ClearSatellite        => $"Turn the carrier away and let {Sat(s.Satellite)} get clear",
            StepKind.SwitchToSatellite     => $"Switch to {Sat(s.Satellite)}",
            StepKind.SwitchToCarrier       => "Switch back to the carrier",
            StepKind.CreateParkingNode     => "Plan the carrier's parking-orbit burn",
            StepKind.CreateCircularizeNode => $"Plan {Sat(s.Satellite)}'s circularization",
            StepKind.ExecuteNode           => s.Satellite < 0 ? "Carrier: parking-orbit burn"
                                                              : $"{Sat(s.Satellite)}: circularization burn",
            _ => s.Kind.ToString(),
        };

        private static void Log(string text) => Debug.Log("[RelayOrbiter] " + text);
    }
}

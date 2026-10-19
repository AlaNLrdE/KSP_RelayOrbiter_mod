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
        private const double MinClearance  = 10.0;  // m between carrier and satellite before switching
        private const double MinTurnDeg    = 60.0;  // carrier turn that moves its end off the satellite's path
        private const double ClearDelay    = 2.0;   // s after release before the carrier starts turning
        private const double OrientSasWait = 10.0;  // s for SAS to accept the orientation mode (just out of warp)
        private const double ReleaseSettle = 1.0;     // s of physics before a release
        private const double SeparationTimeout = 5.0; // s for a released satellite to become its own vessel
        private const double MinNodeLead   = 60.0;  // s: never place a transfer node closer than this
        // KSP unloads orbiting vessels 2.5 km from the active one; switching back to an
        // unloaded carrier reloads the scene and ends the run. Keep it loaded meanwhile.
        private const float  LoadRange     = 22_500f;  // m
        private const float  UnloadRange   = 25_000f;  // m

        private readonly IReadOnlyList<SequenceStep> _steps;
        private readonly DeploymentState _state;      // what was started (and is saved for resuming)
        private readonly Vessel _carrier;
        private readonly uint[] _separators;          // flight ids, release order
        private readonly uint[] _commandParts;        // flight ids: identify each satellite's vessel
        private readonly string[] _names;
        private readonly uint[][] _satellitePartIds;  // every part each release detaches
        private readonly bool _extendPanels, _topUp;
        private readonly ParkingOrbit _parking;  // inner: release at apoapsis; outer: at periapsis
        private readonly TransferPlanner _transfer;
        private readonly MoonTransferPlanner _moonPlanner = new MoonTransferPlanner(new OrbitalMechanics());
        private readonly InterplanetaryPlanner _interplanetary = new InterplanetaryPlanner(new OrbitalMechanics());
        private readonly double _goalRadius;          // target circular orbit radius
        private readonly double? _goalInclination;    // degrees; null = keep the current plane
        private readonly double? _goalLan;            // with it: a whole plane (inclination and node)
        private bool _skipExecute;                    // the node step found nothing to do
        private bool _nextNeedsManeuverMode;          // the next node is a plane change
        private readonly List<CelestialBody> _legs = new List<CelestialBody>();  // bodies to travel to, in order
        private readonly bool _useMechJeb;            // MechJeb plans the ejections and corrections
        private bool _nextPrecise;                    // the next node is a course correction
        private readonly string[]? _satelliteNames;   // null = keep the names they have
        private int _lastReleased = -1;
        private Vector3 _clearFrom;
        private VesselAutopilot.AutopilotMode? _clearMode;
        private double _clearStart;
        private double _clearFromDistance = double.NaN;  // satellite distance right after release
        private double _recedeSince = double.NaN, _recedeFrom, _nextClearReport;  // drift measured once turned away
        private const double RecedeWatch        = 3.0;   // s of steady drift before trusting it
        private const double MinRecedeSpeed     = 0.05;  // m/s apart: drifting off, not lingering
        private const double MinRecedeClearance = 2.0;   // m apart at least

        // RCS push away from a released satellite, before the carrier turns.
        private const double RcsPushSeconds = 3.0;   // s at most
        private const double RcsTargetSpeed = 0.5;   // m/s of separation: enough
        private const double RcsCheckAfter  = 1.0;   // s: then check the push goes the right way
        private bool _rcsPushing, _rcsChecked, _rcsWasOn, _rcsAttached;
        private double _rcsStart, _rcsStartSpeed;
        private float _rcsSign = 1f;
        private Vector3 _translate;                  // local translation command (X, Y, Z)
        private readonly IDeploymentPlanner _planner;
        private readonly Func<double, string> _formatTime;

        private int _index;
        private bool _started;
        private bool _resumePending;   // first Tick after a resume: make the current step safe to re-run
        private double _until;
        private Vessel? _target;
        private NodeExecutor? _executor;
        private readonly Warp _warp = new Warp();
        private bool _orienting;  // false when orientation was skipped
        private bool _modeSet;    // SAS has accepted the orientation mode
        private double _orientSince;
        private double _unpackedSince = double.NaN;  // Release: since when the carrier has physics
        private VesselAutopilot.AutopilotMode _orientMode;
        private double _alignedSince = double.NaN;
        // Parking burn time, fixed at the first warp: on a near-circular orbit the
        // apoapsis is ill-defined, and SAT-1's separation push can move it anywhere.
        private double _parkingUT = double.NaN;
        private bool _released;
        private readonly float _savedLoad, _savedUnload;

        // A new deployment (resumed = false) or one resumed from a saved state.
        public DeploymentRunner(DeploymentState state, Vessel carrier, bool resumed,
                                IDeploymentPlanner planner, TransferPlanner transfer, Func<double, string> formatTime)
        {
            _state            = state;
            _steps            = DeploymentSequence.Build(state.CarrierBurn, state.SatelliteBurns,
                                    state.Prelude);
            _carrier          = carrier;
            _separators       = state.Separators;
            _commandParts     = state.CommandParts;
            _satelliteNames   = state.Names;
            _names            = state.Names ?? state.Separators.Select(_ => "satellite").ToArray();
            _satellitePartIds = state.SatelliteParts;
            _extendPanels     = state.ExtendPanels;
            _topUp            = state.TopUp;
            _parking          = state.Parking;
            _goalRadius       = state.GoalRadius;
            _goalInclination  = state.GoalInclination;
            _goalLan          = state.GoalLan;
            _useMechJeb       = state.UseMechJeb;
            foreach (var name in new[] { state.Via, state.Destination })
            {
                var b = name == null ? null : FlightGlobals.Bodies.FirstOrDefault(x => x.bodyName == name);
                if (b != null) _legs.Add(b);
            }
            _planner          = planner;
            _transfer         = transfer;
            _formatTime       = formatTime;
            _index            = state.Step;
            _parkingUT        = state.ParkingUT;
            _lastReleased     = state.LastReleased;
            _resumePending    = resumed;
            // The satellites still on board keep their engines off until they are released.
            int held = VesselParts.HoldEngines(carrier, SatellitesStillAboard(), true);
            if (held > 0) Log($"Holding {held} satellite engine(s) off until each satellite is released.");

            var orbit = carrier.vesselRanges.orbit;
            _savedLoad   = resumed ? state.SavedLoad : orbit.load;
            _savedUnload = resumed ? state.SavedUnload : orbit.unload;
            if (orbit.unload < UnloadRange)
            {
                orbit.load   = LoadRange;
                orbit.unload = UnloadRange;
            }
            Log((resumed ? $"Deployment resumed at step {Step}/{Steps}: " : "Deployment started: ") +
                $"{_separators.Length} satellites, {_parking} parking orbit. " +
                $"Carrier stays loaded up to {orbit.load / 1000f:F1} km (was {_savedLoad / 1000f:F1} km).");
            DumpPlan(carrier);
        }

        // Everything the run was started with, for reading a failure back from the log.
        private void DumpPlan(Vessel carrier)
        {
            foreach (var kv in _state.ToValues()) RoLog.Dbg($"Plan: {kv.Key} = {kv.Value}");
            RoLog.Dbg($"Plan: legs [{string.Join(" → ", _legs.Select(b => b.bodyName))}], goal altitude " +
                      $"{RoLog.Km(_goalRadius - (_legs.Count > 0 ? _legs[_legs.Count - 1].Radius : carrier.mainBody.Radius))}, " +
                      $"goal inclination {(_goalInclination.HasValue ? $"{_goalInclination.Value:F2}°" : "none (keep the arrival plane)")}" +
                      (_goalLan.HasValue ? $", goal Ω {_goalLan.Value:F2}°" : "") + $", MechJeb planner {_useMechJeb}.");
            for (int i = 0; i < _steps.Count; ++i)
                RoLog.Dbg($"Plan: step {i + 1}/{_steps.Count} {_steps[i].Kind} (satellite {_steps[i].Satellite}, " +
                          $"leg {_steps[i].Leg}, {_steps[i].Seconds:F0} s): {Describe(_steps[i])}");
            RoLog.Dbg($"Carrier at start: {RoLog.Describe(carrier.orbit)}; {RoLog.Propulsion(carrier)}.");
        }

        private CelestialBody? _lastBody;   // the active vessel's body at the last Tick (SOI changes)
        private double _stepStartUT;

        // SOI changes are what transfers and captures hinge on: log each one as it happens.
        private void TrackBody()
        {
            var v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            if (_lastBody != null && v.mainBody != _lastBody)
                Log($"{v.vesselName} changed sphere of influence: {_lastBody.bodyName} → {v.mainBody.bodyName} " +
                    $"during step {Step}/{Steps} ({CurrentStep}). Now {RoLog.Describe(v.orbit)}.");
            _lastBody = v.mainBody;
        }

        private static string Snapshot()
        {
            var v = FlightGlobals.ActiveVessel;
            return v == null ? "no active vessel" : $"{v.vesselName}, {RoLog.Describe(v.orbit)}; {RoLog.Propulsion(v)}";
        }

        // The state to save so this deployment can be resumed after a scene reload.
        public DeploymentState ToState()
        {
            var v = _state.ToValues();
            var copy = DeploymentState.FromValues(new Dictionary<string, string>(v));
            copy.Step         = Math.Min(_index, _steps.Count);
            copy.ParkingUT    = _parkingUT;
            copy.LastReleased = _lastReleased;
            copy.SavedLoad    = _savedLoad;
            copy.SavedUnload  = _savedUnload;
            return copy;
        }

        // A satellite's command part and vessel, found by flight id among the loaded
        // vessels (references don't survive a scene reload).
        private Part? CommandPart(int k)
        {
            foreach (var v in FlightGlobals.Vessels)
                if (v.loaded)
                    foreach (var p in v.parts)
                        if (p.flightID == _commandParts[k]) return p;
            return null;
        }

        private Vessel? SatelliteVessel(int k) => CommandPart(k)?.vessel;

        // After a resume, make the current step safe to run again: no duplicate nodes,
        // no node step without its node, and a fresh parking burn time if it passed.
        private void PrepareResume()
        {
            double now = Planetarium.GetUniversalTime();
            int parkingNode = IndexOf(StepKind.CreateParkingNode);
            if (_index <= parkingNode && !double.IsNaN(_parkingUT) && _parkingUT < now + MinNodeLead)
            {
                _parkingUT = double.NaN;
                _index = IndexOf(StepKind.WarpToRelease);
                Log("The parking burn's time passed while interrupted: waiting for the next one.");
            }
            var vessel = FlightGlobals.ActiveVessel;
            var solver = vessel != null ? vessel.patchedConicSolver : null;
            if (_index < _steps.Count && _steps[_index].Kind == StepKind.ExecuteNode &&
                (solver == null || solver.maneuverNodes.Count == 0) && _index > 0)
                _index--;  // plan the node again
            if (_index < _steps.Count && IsCreateNode(_steps[_index].Kind) && solver != null)
                foreach (var node in solver.maneuverNodes.ToList()) node.RemoveSelf();
        }

        private int IndexOf(StepKind kind)
        {
            for (int i = 0; i < _steps.Count; ++i) if (_steps[i].Kind == kind) return i;
            return -1;
        }

        private static bool IsCreateNode(StepKind k) =>
            k == StepKind.CreateParkingNode || k == StepKind.CreateCircularizeNode ||
            k == StepKind.CreateTransferNode || k == StepKind.CreateTargetCircularizeNode ||
            k == StepKind.CreatePlaneChangeNode || k == StepKind.CreateEjectionNode || k == StepKind.CreateMoonPlaneNode ||
            k == StepKind.CreateCourseCorrectionNode || k == StepKind.CreateCaptureNode;

        public RunState State  { get; private set; } = RunState.Running;
        public string   Status { get; private set; } = "";
        public string   Error  { get; private set; } = "";
        public int      Step   => Math.Min(_index + 1, _steps.Count);
        public int      Steps  => _steps.Count;
        public string   CurrentStep => _index < _steps.Count ? Describe(_steps[_index]) : "Done";

        public void Tick()
        {
            if (State != RunState.Running) return;
            if (_resumePending)
            {
                _resumePending = false;
                PrepareResume();
            }
            if (_index >= _steps.Count)
            {
                State = RunState.Done;
                RestoreRanges();
                int n = _separators.Length;
                Status = $"Constellation deployed: {n} satellites, {Resonance.SpacingDeg(n):F0}° apart.";
                Log(Status);
                return;
            }

            TrackBody();
            var step = _steps[_index];
            if (!_started)
            {
                _started = true;
                _stepStartUT = Planetarium.GetUniversalTime();
                Log($"Step {Step}/{Steps}: {Describe(step)}");
                RoLog.Dbg($"Step {Step} begins: {Snapshot()}.");
                Begin(step);
                if (State != RunState.Running) return;
            }
            if (Advance(step))
            {
                RoLog.Dbg($"Step {Step} done after {Planetarium.GetUniversalTime() - _stepStartUT:F0} s game time: {Snapshot()}.");
                _index++;
                _started = false;
            }
        }

        // True while the current step is waiting on time warp.
        public bool CanFastForward =>
            State == RunState.Running && _index < _steps.Count &&
            (_steps[_index].Kind == StepKind.WarpToRelease ||
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
                case StepKind.WarpToRelease:
                {
                    var o = _carrier.orbit;
                    double apUT = _planner.NextPassUT(now, ReleasePoint.TimeTo(o, _parking), o.period, s.Seconds + 10.0);
                    // The first warp of the deployment (after any transfer) leads to SAT-1's release.
                    if (double.IsNaN(_parkingUT)) _parkingUT = apUT;
                    _until = apUT - s.Seconds;
                    _warp.Now();
                    break;
                }
                case StepKind.OrientForRelease:
                    BeginOrient(s.Satellite, now);
                    break;

                case StepKind.Release:
                    _released = false;  // fired in Advance, once the carrier has had physics a moment
                    _unpackedSince = _carrier.packed ? double.NaN : now;
                    break;
                case StepKind.Wait:
                    _until = now + s.Seconds;
                    NameSatellite(_lastReleased);
                    break;

                case StepKind.ClearSatellite:
                    BeginClear(now, s.Seconds);
                    NameSatellite(s.Satellite);
                    break;

                case StepKind.SwitchToSatellite:
                    _target = SatelliteVessel(s.Satellite);
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

                case StepKind.CreateTransferNode:
                case StepKind.CreateTargetCircularizeNode:
                case StepKind.CreatePlaneChangeNode:
                    CreatePreludeNode(s.Kind, now);
                    break;

                case StepKind.ExecuteNode:
                {
                    var nodes = FlightGlobals.ActiveVessel.patchedConicSolver?.maneuverNodes;
                    if (_skipExecute) RoLog.Dbg("Burn skipped: the node step found nothing to do.");
                    else if (nodes != null && nodes.Count > 0)
                        RoLog.Dbg($"Burn: node at UT {nodes[0].UT:F0} (in {_formatTime(nodes[0].UT - now)}), Δv (radial " +
                                  $"{nodes[0].DeltaV.x:F2}, normal {nodes[0].DeltaV.y:F2}, prograde {nodes[0].DeltaV.z:F2}) = " +
                                  $"{nodes[0].DeltaV.magnitude:F1} m/s, maneuver mode {(_nextNeedsManeuverMode ? "required" : "optional")}, " +
                                  $"precise {_nextPrecise}; after it: {RoLog.Describe(nodes[0].nextPatch)}.");
                    else RoLog.Dbg("Burn: no maneuver node to fly.");
                }
                    _executor = _skipExecute ? null
                        : new NodeExecutor(FlightGlobals.ActiveVessel, _formatTime, _nextNeedsManeuverMode, precise: _nextPrecise,
                                           protectedParts:
                                           FlightGlobals.ActiveVessel == _carrier ? SatellitesOnBoard() : null);
                    _skipExecute = false;
                    _nextNeedsManeuverMode = false;
                    _nextPrecise = false;
                    break;

                case StepKind.CreateMoonPlaneNode:
                case StepKind.CreateEjectionNode:
                case StepKind.CreateCourseCorrectionNode:
                case StepKind.CreateCaptureNode:
                    CreateTravelNode(s, now);
                    break;

                case StepKind.WarpToSoi:
                case StepKind.WarpToMidcourse:
                {
                    var target = Leg(s);
                    if (target == null || _carrier.mainBody == target) break;
                    var patch = EncounterSearch.PatchAround(_carrier.orbit, target);
                    RoLog.Dbg($"{s.Kind} towards {target.bodyName}: predicted arrival {RoLog.Patch(patch)}" +
                              (patch == null ? $", closest approach {RoLog.Km(EncounterSearch.ClosestApproach(_carrier.orbit, target, 0.75 * target.orbit.period))}" : "") + ".");
                    if (patch == null)
                    {
                        if (s.Kind == StepKind.WarpToMidcourse) { _until = now; break; }  // correct right away
                        Fail($"No encounter with {target.bodyName} any more.");
                        break;
                    }
                    _until = s.Kind == StepKind.WarpToSoi ? patch.StartUT + 10.0 : now + (patch.StartUT - now) / 2.0;
                    RoLog.Dbg($"{s.Kind}: warping to UT {_until:F0} ({_formatTime(_until - now)} from now).");
                    _warp.Now();
                    break;
                }
            }
        }

        // True when the step is complete.
        private bool Advance(SequenceStep s)
        {
            double now = Planetarium.GetUniversalTime();
            switch (s.Kind)
            {
                case StepKind.WarpToRelease:
                    _warp.Towards(_until);
                    Status = $"Warping: {_formatTime(Math.Max(0.0, _until - now))} to go";
                    // Rate 1x is not enough: while warp winds down the carrier is still on
                    // rails, and separating parts on rails gives the new vessel a NaN orbit.
                    return now >= _until - 1.0 && TimeWarp.CurrentRateIndex == 0 && !_carrier.packed;

                case StepKind.Release:
                {
                    if (_carrier.packed) { _unpackedSince = double.NaN; Status = "Waiting for physics before the release"; return false; }
                    if (_released) return true;
                    // Just out of time warp the vessel is still settling: let it have physics for a moment.
                    if (double.IsNaN(_unpackedSince)) _unpackedSince = now;
                    if (now - _unpackedSince < ReleaseSettle) { Status = "Settling before the release"; return false; }
                    _released = true;
                    _lastReleased = s.Satellite;
                    var part = VesselParts.Find(_carrier, _separators[s.Satellite]);
                    var already = SatelliteVessel(s.Satellite);
                    if (part == null && already != null && already != _carrier)
                    {
                        Log($"{Sat(s.Satellite)} was already released.");
                        return true;
                    }
                    // Its engines were held while on board: it needs them from now on.
                    int freed = VesselParts.HoldEngines(_carrier, _satellitePartIds[s.Satellite], false);
                    if (freed > 0) Log($"{Sat(s.Satellite)}: {freed} engine(s) enabled again for its own burns.");
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
                        return true;  // its engine is activated (current stage only) by the node executor
                    if (now > _until) Fail($"Could not switch to {_target!.vesselName}.");
                    Status = $"Switching to {_target!.vesselName}";
                    return false;

                case StepKind.WarpToSoi:
                {
                    var target = Leg(s);
                    if (target == null || _carrier.mainBody == target)
                        return TimeWarp.CurrentRateIndex == 0 || StopWarp();
                    _warp.Towards(_until);
                    Status = $"Coasting to {target.bodyName}: {_formatTime(Math.Max(0.0, _until - now))} to its SOI";
                    if (now > _until + 600.0) Fail($"Did not enter {target.bodyName}'s SOI.");
                    return false;
                }
                case StepKind.WarpToMidcourse:
                {
                    var target = Leg(s);
                    if (target == null || _carrier.mainBody == target || now >= _until - 1.0)
                        return TimeWarp.CurrentRateIndex == 0 || StopWarp();
                    _warp.Towards(_until);
                    Status = $"Cruising to {target.bodyName}: {_formatTime(_until - now)} to the midcourse correction";
                    return false;
                }

                case StepKind.ExecuteNode:
                {
                    if (_executor == null) return true;  // the node step skipped its burn
                    var r = _executor.Tick();
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
                : (CommandPart(k)?.transform.position ?? separator.transform.position) - separator.transform.position;
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
            _orientSince = now;
            // SAS on first: KSP refuses the mode while SAS is off, and for a moment after the
            // carrier leaves time warp. AdvanceOrient keeps trying for a few seconds.
            _carrier.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            _orienting = axis != Vector3.zero;
            if (!_orienting)
            {
                Log($"No separation axis for {Sat(k)}: releasing without orienting.");
                return;
            }
            _modeSet = ap.CanSetMode(mode);
            if (_modeSet) ap.SetMode(mode);
            Log($"Pointing {mode} so {Sat(k)}'s separation push is perpendicular to the velocity" +
                (_modeSet ? "." : " (waiting for SAS to accept the mode)."));
        }

        private bool AdvanceOrient(int k, double now)
        {
            if (_carrier.packed) { Status = "Waiting for physics"; return false; }
            if (!_orienting) return true;
            // KSP drops the mode if SAS wasn't on yet when it was set: keep applying it.
            _carrier.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            if (!_modeSet)
            {
                _modeSet = _carrier.Autopilot.CanSetMode(_orientMode);
                if (!_modeSet)
                {
                    if (now - _orientSince < OrientSasWait)
                    {
                        Status = $"Waiting for SAS to accept {_orientMode}";
                        return false;
                    }
                    Log($"Can't point {_orientMode}: releasing {Sat(k)} without orienting " +
                        $"({VesselParts.ControlReport(_carrier)}).");
                    _orienting = false;
                    return true;
                }
                Log($"SAS accepted {_orientMode} after {now - _orientSince:F1} s.");
            }
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

        // Once released, the satellite is its own vessel: give it its constellation name.
        private void NameSatellite(int k)
        {
            if (_satelliteNames == null || k < 0) return;
            var sat = SatelliteVessel(k);
            if (sat == null || sat == _carrier) return;
            VesselRename.Apply(sat, _satelliteNames[k]);
        }

        // ── Getting clear of a released satellite ────────────────────────────

        private void BeginClear(double now, double timeout)
        {
            _until = now + timeout;
            _clearStart = now;
            _clearFrom = _carrier.ReferenceTransform.up;
            _clearFromDistance = double.NaN;
            _recedeSince = double.NaN;
            _nextClearReport = now;
            // Turn 90°: from normal (axial mounts) to prograde, or from prograde (radial) to normal.
            var mode = _orientMode == VesselAutopilot.AutopilotMode.Normal
                ? VesselAutopilot.AutopilotMode.Prograde
                : VesselAutopilot.AutopilotMode.Normal;
            _clearMode = _carrier.Autopilot.CanSetMode(mode) ? mode : (VesselAutopilot.AutopilotMode?)null;

            _rcsPushing = VesselParts.HasRcs(_carrier);
            _rcsChecked = false;
            _rcsSign = 1f;
            _rcsStart = now;
            _rcsStartSpeed = double.NaN;
            if (_rcsPushing)
            {
                _rcsWasOn = _carrier.ActionGroups[KSPActionGroup.RCS];
                _carrier.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                if (!_rcsAttached) { _carrier.OnFlyByWire += Translate; _rcsAttached = true; }
            }
        }

        private void Translate(FlightCtrlState s)
        {
            s.X = _translate.x;
            s.Y = _translate.y;
            s.Z = _translate.z;
        }

        // Push the carrier away from the satellite with RCS, along the separation axis
        // (perpendicular to the velocity, so the carrier's period barely changes).
        // True while still pushing.
        private bool PushAway(Vessel sat, double now)
        {
            if (!_rcsPushing) return false;
            Vector3d away = (_carrier.CoMD - sat.CoMD).normalized;
            double speed = Vector3d.Dot(_carrier.obt_velocity - sat.obt_velocity, away);
            if (double.IsNaN(_rcsStartSpeed)) _rcsStartSpeed = speed;

            // Make sure the push goes the right way: if the carrier is closing in, flip it.
            if (!_rcsChecked && now - _rcsStart >= RcsCheckAfter)
            {
                _rcsChecked = true;
                if (speed < _rcsStartSpeed)
                {
                    _rcsSign = -_rcsSign;
                    Log("RCS pushed the wrong way: reversing it.");
                }
            }

            if (speed >= RcsTargetSpeed || now - _rcsStart >= RcsPushSeconds)
            {
                StopRcs();
                Log($"RCS push done: separating at {speed:F2} m/s.");
                return false;
            }

            // ModuleRCS translates along ReferenceTransform.rotation * (X, Z, Y).
            Vector3 local = _carrier.ReferenceTransform.InverseTransformDirection((Vector3)away) * _rcsSign;
            _translate = new Vector3(local.x, local.z, local.y);
            Status = $"RCS push away from the satellite: {speed:F2} m/s";
            return true;
        }

        private void StopRcs()
        {
            if (!_rcsPushing) return;
            _rcsPushing = false;
            _translate = Vector3.zero;
            if (_carrier == null) { _rcsAttached = false; return; }  // scene being destroyed
            if (_rcsAttached) { _carrier.OnFlyByWire -= Translate; _rcsAttached = false; }
            _carrier.ActionGroups.SetGroup(KSPActionGroup.RCS, _rcsWasOn);
        }

        private bool AdvanceClear(int k, double now)
        {
            if (_carrier.packed) { Status = "Waiting for physics"; return false; }
            var sat = SatelliteVessel(k);
            if (sat == null || sat == _carrier)
            {
                // The split into two vessels can take a few physics frames (docking ports,
                // a carrier that just left time warp): wait for it before calling it a failure.
                if (now - _clearStart < SeparationTimeout) { Status = $"Waiting for {Sat(k)} to separate"; return false; }
                Fail($"{Sat(k)} did not separate.");
                return false;
            }

            if (PushAway(sat, now)) return false;
            if (_clearMode == null && now - _clearStart >= ClearDelay)
            {
                // SAS may not have been ready when the step began: pick the mode now.
                var mode = _orientMode == VesselAutopilot.AutopilotMode.Normal
                    ? VesselAutopilot.AutopilotMode.Prograde : VesselAutopilot.AutopilotMode.Normal;
                _carrier.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
                if (_carrier.Autopilot.CanSetMode(mode)) _clearMode = mode;
            }
            if (_clearMode != null && now - _clearStart >= ClearDelay)
            {
                _carrier.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
                if (_carrier.Autopilot.Mode != _clearMode.Value) _carrier.Autopilot.SetMode(_clearMode.Value);
            }
            double turned = Vector3.Angle(_carrier.ReferenceTransform.up, _clearFrom);
            // How far the satellite moved away since the release: the distance between the
            // centres of mass is large from the start on a long vessel.
            double between = (sat.CoMD - _carrier.CoMD).magnitude;
            if (double.IsNaN(_clearFromDistance)) _clearFromDistance = between;
            double distance = between - _clearFromDistance;
            bool turnedEnough = _clearMode == null || turned >= MinTurnDeg;

            if (turnedEnough && distance >= MinClearance)
            {
                Log($"Clear of {Sat(k)}: carrier turned {turned:F0}°, satellite {distance:F1} m further away.");
                return true;
            }
            // Turned away and the satellite drifting off steadily: the gap only grows, so
            // waiting for the full clearance (minutes at a docking port's gentle push, the
            // timeout every time on a carrier without RCS) gains nothing.
            if (turnedEnough)
            {
                if (double.IsNaN(_recedeSince)) { _recedeSince = now; _recedeFrom = distance; }
                double watched = now - _recedeSince, speed = watched > 0.0 ? (distance - _recedeFrom) / watched : 0.0;
                if (now >= _nextClearReport)
                {
                    _nextClearReport = now + 5.0;
                    RoLog.Dbg($"Clearing {Sat(k)}: carrier turned {turned:F0}°, satellite {distance:F2} m further away, " +
                              $"receding at {speed:F3} m/s over {watched:F0} s (RCS push {_rcsPushing}).");
                }
                if (watched >= RecedeWatch && speed >= MinRecedeSpeed && distance >= MinRecedeClearance)
                {
                    Log($"Clear of {Sat(k)}: carrier turned {turned:F0}°, satellite {distance:F1} m away and receding " +
                        $"at {speed:F2} m/s.");
                    return true;
                }
            }
            if (now > _until)
            {
                Log($"Clearing timed out (carrier turned {turned:F0}°, satellite {distance:F1} m further away): " +
                    "switching anyway.");
                return true;
            }
            Status = $"Getting clear: carrier turned {turned:F0}°, {Sat(k)} {distance:F1} m further away";
            return false;
        }

        // ── Reaching the target orbit ────────────────────────────────────────

        private void CreatePreludeNode(StepKind kind, double now)
        {
            var o = _carrier.orbit;
            var solver = _carrier.patchedConicSolver;
            if (FlightGlobals.ActiveVessel != _carrier || solver == null)
            {
                Fail("The carrier must be the active vessel and able to plan maneuver nodes.");
                return;
            }
            double mu = o.referenceBody.gravParameter;

            switch (kind)
            {
                case StepKind.CreateTransferNode:
                {
                    var t = _transfer.Plan(mu, o.ApR, o.PeR, _goalRadius);
                    if (!t.Needed) { Skip("Already on the target orbit: no transfer needed."); return; }
                    double timeTo = t.FirstBurnAt == Apsis.Periapsis ? o.timeToPe : o.timeToAp;
                    double ut = _planner.NextPassUT(now, timeTo, o.period, MinNodeLead);
                    AddNode(solver, ut, new Vector3d(0.0, 0.0, t.FirstDv));
                    Log($"Transfer burn: {t.FirstDv:F1} m/s prograde at {t.FirstBurnAt.ToString().ToLowerInvariant()}.");
                    break;
                }
                case StepKind.CreateTargetCircularizeNode:
                {
                    // After the transfer burn the target radius is one of the apsides.
                    bool atAp = Math.Abs(o.ApR - _goalRadius) <= Math.Abs(o.PeR - _goalRadius);
                    double r = atAp ? o.ApR : o.PeR;
                    double ut = _planner.NextPassUT(now, atAp ? o.timeToAp : o.timeToPe, o.period, MinNodeLead);
                    double dv = _planner.CircularizeAt(mu, r, o.semiMajorAxis);
                    if (Math.Abs(dv) < 0.1) { Skip("Already circular at the target."); return; }
                    AddNode(solver, ut, new Vector3d(0.0, 0.0, dv));
                    Log($"Circularize at the target: {dv:F1} m/s prograde at {(atAp ? "apoapsis" : "periapsis")}.");
                    break;
                }
                case StepKind.CreatePlaneChangeNode when _goalInclination.HasValue && _goalLan.HasValue:
                {
                    // A whole plane (a Walker plane): where the two planes cross.
                    var plane = new OrbitPlane(_goalInclination.Value, _goalLan.Value);
                    double angle = PlaneMatch.AngleDeg(o, plane, now);
                    if (angle < PlaneChange.ToleranceDeg) { Skip($"{angle:F2}° from the target plane: no plane change."); return; }
                    double ut = PlaneMatch.NextCrossing(o, PlaneMatch.TargetNormal(plane, o, now), now + MinNodeLead);
                    var (prograde, normal) = PlaneMatch.DeltaV(o.getOrbitalVelocityAtUT(ut).magnitude, angle);
                    var node = solver.AddManeuverNode(ut);
                    node.OnGizmoUpdated(new Vector3d(0.0, normal, prograde), ut);
                    PlaneMatch.CheckSign(solver, node, ut, prograde, normal, plane, Log);
                    _nextNeedsManeuverMode = true;
                    Log($"Into the target plane (i {plane.InclinationDeg:F2}°, Ω {plane.LanDeg:F2}°): {angle:F2}°, " +
                        $"{PlaneGrouping.PlaneChangeDv(o.getOrbitalVelocityAtUT(ut).magnitude, angle):F1} m/s.");
                    break;
                }
                case StepKind.CreatePlaneChangeNode:
                {
                    if (!_goalInclination.HasValue || !PlaneChange.Needed(o.inclination, _goalInclination.Value))
                    {
                        Skip("Already at the target inclination.");
                        return;
                    }
                    var (ut, ascending) = NextNode(o, now);
                    double speed = o.getOrbitalVelocityAtUT(ut).magnitude;
                    var (prograde, normal) = PlaneChange.DeltaV(speed, o.inclination, _goalInclination.Value, ascending);
                    AddNode(solver, ut, new Vector3d(0.0, normal, prograde));
                    _nextNeedsManeuverMode = true;
                    Log($"Plane change {o.inclination:F2}° → {_goalInclination.Value:F2}° at the " +
                        $"{(ascending ? "ascending" : "descending")} node: " +
                        $"{Math.Sqrt(prograde * prograde + normal * normal):F1} m/s.");
                    break;
                }
            }
        }

        // ── Travelling to a moon ─────────────────────────────────────────────

        private const double CorrectionTolerance = 0.03;  // periapsis within 3%: no correction
        private const double CaptureTolerance    = 0.5;   // periapsis further off than 50%: no capture there
        private const double MoonPlaneTolerance  = 0.2;   // degrees from the moon's plane: close enough
        private const double CorrectionDelay     = 600.0; // s after the ejection

        private bool StopWarp()
        {
            TimeWarp.SetRate(0, true, false);
            return false;
        }

        // The body a travel step heads for (null when there is none).
        private CelestialBody? Leg(SequenceStep s) => s.Leg < _legs.Count ? _legs[s.Leg] : null;

        // Capture radius around a leg's body: the target orbit at the end, a low
        // parking orbit at an intermediate stop (Duna, on the way to Ike).
        private double CaptureRadius(SequenceStep s, CelestialBody body) =>
            s.Leg == _legs.Count - 1 ? _goalRadius
            : body.Radius + (body.atmosphere ? body.atmosphereDepth + LaunchPlanner.AboveAtmosphere
                                             : Math.Max(LaunchPlanner.MinAirless, LaunchPlanner.AirlessFraction * body.Radius));

        // Into the moon's plane; ejection at the next window, refined against KSP's
        // prediction; course corrections when the encounter's periapsis is off; the
        // capture burn. Works for a moon of the body orbited and for a sibling (another
        // planet around the same star, or another moon of the same planet).
        private void CreateTravelNode(SequenceStep step, double now)
        {
            var o = _carrier.orbit;
            var solver = _carrier.patchedConicSolver;
            var target = Leg(step);
            if (FlightGlobals.ActiveVessel != _carrier || solver == null || target == null)
            {
                Fail("The carrier must be the active vessel, able to plan nodes, with a destination.");
                return;
            }
            var here = _carrier.mainBody;
            bool moon = target.referenceBody == here;
            bool sibling = !moon && here.referenceBody != null && target.referenceBody == here.referenceBody;
            double captureR = CaptureRadius(step, target);
            double minPe = target.Radius + Math.Max(target.atmosphere ? target.atmosphereDepth : 0.0, 10_000.0);
            if (step.Kind != StepKind.CreateCaptureNode && here == target) { Skip($"Already around {target.bodyName}."); return; }
            if (step.Kind != StepKind.CreateCaptureNode && !moon && !sibling)
            {
                Fail($"Can't plan a transfer from {here.bodyName} to {target.bodyName}.");
                return;
            }
            // Time span to look for the closest approach: about the transfer time.
            double span = moon ? 0.6 * target.orbit.period : 0.75 * target.orbit.period;
            RoLog.Dbg($"{step.Kind}: from {here.bodyName} to {target.bodyName} ({(moon ? "a moon" : sibling ? "a sibling" : "here")}), " +
                      $"capture periapsis {RoLog.Km(captureR - target.Radius)}, minimum {RoLog.Km(minPe - target.Radius)}, " +
                      $"search span {_formatTime(span)}, arrival plane wanted " +
                      $"{(_goalInclination.HasValue ? $"i {_goalInclination.Value:F2}° (aimed at on the way, the rest after the capture)" : "none: the arrival plane is kept")}; " +
                      $"carrier {RoLog.Describe(o)}.");

            switch (step.Kind)
            {
                case StepKind.CreateMoonPlaneNode:
                {
                    if (!moon) { Skip($"{target.bodyName} is not a moon of {here.bodyName}: no plane change first."); return; }
                    var plane = new OrbitPlane(target.orbit.inclination, target.orbit.LAN);
                    double angle = PlaneMatch.AngleDeg(o, plane, now);
                    if (angle < MoonPlaneTolerance)
                    {
                        Skip($"{angle:F2}° from {target.bodyName}'s orbital plane: no plane change needed.");
                        return;
                    }
                    double ut = PlaneMatch.NextCrossing(o, PlaneMatch.TargetNormal(plane, o, now), now + MinNodeLead);
                    var (prograde, normal) = PlaneMatch.DeltaV(o.getOrbitalVelocityAtUT(ut).magnitude, angle);
                    var node = solver.AddManeuverNode(ut);
                    node.OnGizmoUpdated(new Vector3d(0.0, normal, prograde), ut);
                    PlaneMatch.CheckSign(solver, node, ut, prograde, normal, plane, Log);
                    _nextNeedsManeuverMode = true;
                    Log($"Into {target.bodyName}'s orbital plane (i {plane.InclinationDeg:F2}°, Ω {plane.LanDeg:F2}°): " +
                        $"{angle:F2}°, {PlaneGrouping.PlaneChangeDv(o.getOrbitalVelocityAtUT(ut).magnitude, angle):F1} m/s.");
                    break;
                }
                case StepKind.CreateEjectionNode:
                {
                    if (_useMechJeb && MechJebTransfer.Available(_carrier))
                    {
                        string? error = MechJebTransfer.PlanEjection(_carrier, target, sibling, Log);
                        if (error == null) { _nextNeedsManeuverMode = true; break; }
                        Log($"MechJeb could not plan the transfer ({error}): using Relay Orbiter's planner.");
                    }
                    PlanEjection(o, solver, target, moon, captureR, minPe, span, now);
                    break;
                }
                case StepKind.CreateCourseCorrectionNode:
                {
                    var patch = EncounterSearch.PatchAround(o, target);
                    double cost = EncounterSearch.Cost(o, target, captureR, minPe, span, _goalInclination);
                    RoLog.Dbg($"Course correction check: arrival {RoLog.Patch(patch)}, cost {cost:F4} " +
                              $"(tolerance {CorrectionTolerance}), periapsis error " +
                              (patch != null ? $"{RelayOrbiter.Core.EncounterCost.PeriapsisError(patch.PeR, captureR):P1}" : "n/a") +
                              (patch == null ? $", closest approach {RoLog.Km(EncounterSearch.ClosestApproach(o, target, span))}" : "") + ".");
                    if (patch != null && cost < CorrectionTolerance)
                    {
                        Skip($"On course: periapsis {(patch.PeR - target.Radius) / 1000.0:F1} km, i {patch.inclination:F1}° " +
                             $"around {target.bodyName}, no correction needed.");
                        return;
                    }
                    // MechJeb's correction aims the periapsis only: with the periapsis already
                    // right and just the arrival plane off, ours aims both.
                    bool peOff = patch == null ||
                                 RelayOrbiter.Core.EncounterCost.PeriapsisError(patch.PeR, captureR) >= CorrectionTolerance;
                    if (_useMechJeb && peOff && MechJebTransfer.Available(_carrier))
                    {
                        string? error = MechJebTransfer.PlanCorrection(_carrier, target, captureR - target.Radius, Log);
                        if (error == null) { _nextNeedsManeuverMode = true; _nextPrecise = true; break; }
                        Log($"MechJeb could not plan the correction ({error}): using Relay Orbiter's planner.");
                    }
                    double ut = now + CorrectionDelay;
                    var node = solver.AddManeuverNode(ut);
                    var search = new EncounterSearch(solver, node, target, captureR, minPe, span, _goalInclination);
                    double range = sibling ? 50.0 : 20.0;
                    var (dv, after) = search.BestCorrection(ut, Vector3d.zero, range);
                    RoLog.Dbg($"Course correction search (±{range:F0} m/s per axis, node at UT {ut:F0}): best " +
                              $"(radial {dv.x:F2}, normal {dv.y:F2}, prograde {dv.z:F2}) m/s, cost {cost:F4} → {after:F4}, " +
                              $"arrival {RoLog.Patch(search.Patch)}, {search.Evaluations} predictions.");
                    if (after >= cost || dv.magnitude < 0.05)
                    {
                        node.RemoveSelf();
                        if (patch == null) { Fail($"Missed {target.bodyName} and no correction finds it again."); return; }
                        Skip($"No better correction found (periapsis {(patch.PeR - target.Radius) / 1000.0:F1} km).");
                        return;
                    }
                    _nextNeedsManeuverMode = Math.Abs(dv.x) > 0.01 || Math.Abs(dv.y) > 0.01;
                    _nextPrecise = true;
                    var p2 = search.Patch;
                    Log($"Course correction: {dv.magnitude:F2} m/s (radial {dv.x:F2}, normal {dv.y:F2}, prograde " +
                        $"{dv.z:F2}); periapsis {Pe(patch, target)} → {Pe(p2, target)}, i " +
                        $"{(patch != null ? $"{patch.inclination:F1}°" : "n/a")} → {(p2 != null ? $"{p2.inclination:F1}°" : "n/a")} " +
                        $"({search.Evaluations} predictions).");
                    break;
                }
                default:  // capture
                {
                    RoLog.Dbg($"Capture check: {RoLog.Describe(o)}, time to periapsis {_formatTime(o.timeToPe)}, " +
                              $"planned periapsis {RoLog.Km(captureR - target.Radius)}, error " +
                              $"{RelayOrbiter.Core.EncounterCost.PeriapsisError(o.PeR, captureR):P1} (limit {CaptureTolerance:P0}).");
                    if (here != target) { Fail($"Not around {target.bodyName}."); return; }
                    if (o.eccentricity < 1.0 && o.ApR < target.sphereOfInfluence)
                    {
                        Skip($"Already captured around {target.bodyName}.");
                        return;
                    }
                    if (o.timeToPe < MinNodeLead) { Fail("Too close to the periapsis to plan the capture."); return; }
                    // Far from the planned periapsis (the corrections missed): circularizing
                    // there spends the fuel the rest of the plan needs, on an orbit that may
                    // not even stay inside the sphere of influence (a Duna capture at
                    // 47,526 km, aiming at 1,000 km, escaped to the Sun).
                    if (RelayOrbiter.Core.EncounterCost.PeriapsisError(o.PeR, captureR) > CaptureTolerance)
                    {
                        Fail($"Arriving with the periapsis at {Pe(o, target)} around {target.bodyName}, planned " +
                             $"{(captureR - target.Radius) / 1000.0:F1} km: a capture there would waste the fuel the " +
                             "deployment needs. Lower the periapsis now (a radial burn far out is cheap), then resume.");
                        return;
                    }
                    double ut = now + o.timeToPe;
                    double dv = _planner.CircularizeAt(target.gravParameter, o.PeR, o.semiMajorAxis);
                    AddNode(solver, ut, new Vector3d(0.0, 0.0, dv));
                    Log($"Capture around {target.bodyName}: {dv:F1} m/s at the periapsis " +
                        $"({(o.PeR - target.Radius) / 1000.0:F1} km), i {o.inclination:F2}°.");
                    RoLog.Dbg($"Capture node at UT {ut:F0}: speed at periapsis {Math.Sqrt(target.gravParameter * (2.0 / o.PeR - 1.0 / o.semiMajorAxis)):F1} m/s → circular " +
                              $"{Math.Sqrt(target.gravParameter / o.PeR):F1} m/s; carrier {RoLog.Propulsion(_carrier)}.");
                    break;
                }
            }
        }

        private static string Pe(Orbit? patch, CelestialBody body) =>
            patch == null ? "no encounter" : $"{(patch.PeR - body.Radius) / 1000.0:F1} km";

        // Our ejection: wait for the Hohmann window, then search KSP's prediction for
        // the burn point and the Δv (prograde, then a little radial/normal) that meets
        // the target with the capture periapsis.
        private void PlanEjection(Orbit o, PatchedConicSolver solver, CelestialBody target, bool moon, double captureR,
                                  double minPe, double span, double now)
        {
            var here = _carrier.mainBody;
            double phaseNow, phaseNeeded, ejection, transferTime, tFrom, tTo;
            if (moon)
            {
                var plan = _moonPlanner.Plan(here.gravParameter, o.semiMajorAxis, target.orbit.semiMajorAxis,
                                             target.gravParameter, captureR);
                phaseNow = MoonTransferPlanner.PhaseAngleDeg(MeanLongitude.At(o, now), MeanLongitude.At(target.orbit, now));
                phaseNeeded = plan.PhaseAngleDeg; ejection = plan.EjectionDv; transferTime = plan.TransferTime;
                tFrom = o.period; tTo = target.orbit.period;
            }
            else
            {
                var plan = _interplanetary.Plan(here.referenceBody.gravParameter, here.orbit.semiMajorAxis,
                                                target.orbit.semiMajorAxis, here.gravParameter, o.semiMajorAxis,
                                                target.gravParameter, captureR);
                phaseNow = MoonTransferPlanner.PhaseAngleDeg(MeanLongitude.At(here.orbit, now), MeanLongitude.At(target.orbit, now));
                phaseNeeded = plan.PhaseAngleDeg; ejection = plan.EjectionDv; transferTime = plan.TransferTime;
                tFrom = here.orbit.period; tTo = target.orbit.period;
            }
            double wait = MoonTransferPlanner.WaitTime(phaseNow, phaseNeeded, tFrom, tTo);
            double earliest = now + MinNodeLead + 300.0;  // room to point a long burn
            double ut0 = now + wait;
            while (ut0 < earliest) ut0 += MoonTransferPlanner.SynodicPeriod(tFrom, tTo);
            span = Math.Max(span, 1.5 * transferTime);

            var node = solver.AddManeuverNode(ut0);
            var search = new EncounterSearch(solver, node, target, captureR, minPe, span, _goalInclination);
            (double ut, double cost) best = (ut0, double.MaxValue);
            Vector3d bestDv = Vector3d.zero;
            // Moons: around the window; a planet: the burn point within one parking orbit
            // (the escape direction), the window itself being days wide.
            double width = moon ? o.period / 8.0 : o.period / 2.0;
            RoLog.Dbg($"Ejection to {target.bodyName}: phase now {phaseNow:F2}°, needed {phaseNeeded:F2}°, wait " +
                      $"{_formatTime(wait)}, window UT {ut0:F0}, ejection {ejection:F1} m/s, transfer {_formatTime(transferTime)}, " +
                      $"burn-time scan ±{_formatTime(width)}, span {_formatTime(span)}.");
            foreach (double extra in moon ? new[] { 0.0, 0.005, 0.01, 0.02 } : new[] { 0.0, 0.01, 0.03 })
            {
                var dv = new Vector3d(0.0, 0.0, ejection * (1.0 + extra));
                var r = search.BestTime(ut0, width, dv, earliest);
                RoLog.Dbg($"Ejection scan +{extra:P1} ({dv.z:F1} m/s): best UT {r.ut:F0} (window {r.ut - ut0:+0;-0} s), " +
                          $"cost {r.cost:F4}.");
                if (r.cost < best.cost) { best = r; bestDv = dv; }
            }
            if (best.cost >= 1e6)
            {
                node.RemoveSelf();
                Fail($"No trajectory towards {target.bodyName} found near the window " +
                     $"(phase {phaseNow:F1}°, needed {phaseNeeded:F1}°).");
                return;
            }
            // Then the Δv itself (direction and size), and the time again.
            if (best.cost >= CorrectionTolerance)
            {
                var (dv, cost) = search.BestCorrection(best.ut, bestDv, Math.Max(10.0, 0.03 * ejection));
                RoLog.Dbg($"Ejection refinement: Δv (radial {dv.x:F2}, normal {dv.y:F2}, prograde {dv.z:F2}) m/s, cost " +
                          $"{best.cost:F4} → {cost:F4}.");
                if (cost < best.cost) { bestDv = dv; best = (best.ut, cost); }
                var r = search.BestTime(best.ut, width / 10.0, bestDv, earliest);
                RoLog.Dbg($"Ejection retiming: UT {best.ut:F0} → {r.ut:F0}, cost {best.cost:F4} → {r.cost:F4}.");
                if (r.cost < best.cost) best = r;
            }
            double final = search.Evaluate(best.ut, bestDv);
            // KSP's prediction of a long trajectory is sensitive: the same node may predict
            // a different arrival when evaluated again.
            RoLog.Dbg($"Ejection chosen: UT {best.ut:F0}, Δv (radial {bestDv.x:F2}, normal {bestDv.y:F2}, prograde " +
                      $"{bestDv.z:F2}) m/s, search cost {best.cost:F4}, re-evaluated {final:F4}, arrival {RoLog.Patch(search.Patch)}.");
            if (Math.Abs(bestDv.x) > 0.01 || Math.Abs(bestDv.y) > 0.01) _nextNeedsManeuverMode = true;
            var patch = search.Patch;
            string predicted = patch != null
                ? $"periapsis {Pe(patch, target)}, i {patch.inclination:F1}°"
                : $"closest approach {EncounterSearch.ClosestApproach(node.nextPatch, target, span) / 1000.0:F0} km " +
                  "(no encounter yet: the course corrections aim it)";
            Log($"Transfer to {target.bodyName}: window in {_formatTime(best.ut - now)} (phase now {phaseNow:F1}°, " +
                $"needed {phaseNeeded:F1}°), {bestDv.magnitude:F1} m/s; predicted {predicted} " +
                $"({search.Evaluations} predictions).");
        }

        // Parts of the satellites not released yet (their separators excluded).
        private IEnumerable<uint> SatellitesStillAboard() =>
            Enumerable.Range(_lastReleased + 1, Math.Max(0, _separators.Length - _lastReleased - 1))
                      .SelectMany(k => _satellitePartIds[k]);

        // Parts the carrier must never stage away: the satellites not released yet,
        // with their separators.
        private ISet<uint> SatellitesOnBoard()
        {
            var parts = new HashSet<uint>();
            for (int k = _lastReleased + 1; k < _separators.Length; ++k)
            {
                parts.Add(_separators[k]);
                parts.UnionWith(_satellitePartIds[k]);
            }
            return parts;
        }

        private void Skip(string why)
        {
            _skipExecute = true;
            Log(why);
        }

        // The sooner of the ascending and descending nodes (with the equator).
        private (double ut, bool ascending) NextNode(Orbit o, double now)
        {
            double an = NodeUT(o, PlaneChange.NodeTrueAnomalyDeg(o.argumentOfPeriapsis, true), now);
            double dn = NodeUT(o, PlaneChange.NodeTrueAnomalyDeg(o.argumentOfPeriapsis, false), now);
            return an <= dn ? (an, true) : (dn, false);
        }

        // UT of the next passage through a true anomaly given in degrees. Whatever unit
        // GetUTforTrueAnomaly expects, keep the candidate that is on the equator.
        private static double NodeUT(Orbit o, double trueAnomalyDeg, double now)
        {
            double best = double.NaN, bestLat = double.MaxValue;
            foreach (double ta in new[] { trueAnomalyDeg * Math.PI / 180.0, trueAnomalyDeg })
            {
                double ut = o.GetUTforTrueAnomaly(ta, o.period);
                while (ut < now + MinNodeLead) ut += o.period;
                double lat = Math.Abs(o.referenceBody.GetLatitude(o.getPositionAtUT(ut), false));
                if (lat < bestLat) { bestLat = lat; best = ut; }
            }
            return best;
        }

        private static void AddNode(PatchedConicSolver solver, double ut, Vector3d dv)
        {
            var node = solver.AddManeuverNode(ut);
            node.OnGizmoUpdated(dv, ut);  // x radial, y normal, z prograde
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
                dv = _planner.Plan(mu, r, o.semiMajorAxis, ut, _separators.Length, _parking).ParkingDeltaV;
            }
            else
            {
                // Satellites are on the parking orbit, whose apsides are well defined:
                // circularize at the release point (apoapsis inner, periapsis outer).
                double timeTo = ReleasePoint.TimeTo(o, _parking);
                if (timeTo > o.period / 2.0)
                {
                    Fail($"{vessel.vesselName} is already past {ReleasePoint.Name(_parking)}; the release came too late.");
                    return;
                }
                ut = now + timeTo;
                dv = _planner.CircularizeAt(mu, ReleasePoint.Radius(o, _parking), o.semiMajorAxis);
            }

            var node = solver.AddManeuverNode(ut);
            node.OnGizmoUpdated(new Vector3d(0.0, 0.0, dv), ut);  // x radial, y normal, z prograde
            Log($"Node for {vessel.vesselName}: {dv:F1} m/s prograde at {ReleasePoint.Name(_parking)}.");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void Fail(string error)
        {
            Stop();
            State = RunState.Failed;
            Error = error;
            Status = $"Stopped at step {Step}/{Steps} ({CurrentStep}): {error}";
            Log(Status);
            RoLog.Dbg($"At the failure: {Snapshot()}.");
        }

        private void Stop()
        {
            // Stopped early: the satellites still on board get their engines back.
            if (_carrier != null && _carrier.loaded)
            {
                int freed = VesselParts.HoldEngines(_carrier, SatellitesStillAboard(), false);
                if (freed > 0) Log($"{freed} satellite engine(s) enabled again.");
            }
            StopRcs();
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

        private string Sat(int k) => _satelliteNames != null ? $"SAT-{k + 1} ({_names[k]})" : $"SAT-{k + 1}";

        private string Describe(SequenceStep s) => s.Kind switch
        {
            StepKind.WarpToRelease         => $"Warp to {s.Seconds:F0} s before {ReleasePoint.Name(_parking)}",
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
            StepKind.CreateTransferNode          => "Plan the transfer burn to the target orbit",
            StepKind.CreateTargetCircularizeNode => "Plan the circularization at the target orbit",
            StepKind.CreatePlaneChangeNode       => "Plan the plane change",
            StepKind.CreateMoonPlaneNode => $"Plan the plane change into {Leg(s)?.bodyName}'s orbital plane",
            StepKind.WarpToMidcourse => $"Cruise to halfway, for a midcourse correction towards {Leg(s)?.bodyName}",
            StepKind.CreateEjectionNode => $"Plan the transfer to {Leg(s)?.bodyName}",
            StepKind.CreateCourseCorrectionNode => $"Plan a course correction towards {Leg(s)?.bodyName}",
            StepKind.WarpToSoi => $"Coast to {Leg(s)?.bodyName}'s sphere of influence",
            StepKind.CreateCaptureNode => $"Plan the capture around {Leg(s)?.bodyName}",
            StepKind.ExecuteNode           => s.Satellite < 0 ? "Carrier: burn"
                                                              : $"{Sat(s.Satellite)}: circularization burn",
            _ => s.Kind.ToString(),
        };

        private static void Log(string text) => Debug.Log("[RelayOrbiter] " + text);
    }
}

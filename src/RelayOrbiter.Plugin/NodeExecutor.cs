using System;
using System.Collections.Generic;
using System.Linq;
using KSP.UI.Screens;
using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Flies the active vessel's next maneuver node: warps close, points with SAS,
    // throttles with NodeBurn and removes the node when done.
    internal sealed class NodeExecutor
    {
        public enum Result { Running, Done, Failed }

        private const double AlignTimeout = 60.0;  // s allowed to point before giving up
        private const double FlameoutGrace = 2.0;  // s without thrust before giving up
        private const double SasWait       = 30.0; // s to wait for SAS (just switched, panels charging)
        private const double AlignedDeg    = 1.0;  // pointing this close: aligned
        private const float  StillRate     = 0.02f; // rad/s: no longer turning
        private const double IgnitionLead  = 10.0; // s before ignition to stop warping once aligned

        private readonly Vessel _vessel;
        private readonly Func<double, string> _formatTime;
        private readonly NodeBurn _burn;
        private VesselAutopilot.AutopilotMode? _mode;
        private bool _burning, _attached, _tail, _thrusted;
        private readonly Warp _warp = new Warp();
        private Vector3d _direction;  // burn direction when ignition started
        private double _misalignedSince = double.NaN, _noThrustSince = double.NaN, _noSasSince = double.NaN;
        private float _throttle;
        private double _nextReport;                  // Time.time of the next burn log line
        private const double ReportEvery = 5.0;      // s between burn log lines

        // Before a burn, with no engine running: ignite the current stage's engines that
        // are off (never a satellite's still on board). A player setting, on by default.
        public static bool AutoActivateEngines = true;
        private bool _activationTried;

        private readonly bool _maneuverModeRequired;
        private readonly ISet<uint>? _protectedParts;  // null: never stage
        private int _staged;

        // maneuverModeRequired: the burn is not purely prograde/retrograde (plane change),
        // so the prograde/retrograde SAS fallback would point the wrong way.
        // protectedParts: when set, a spent stage may be staged away, as long as the next
        // stage holds none of these parts (the satellites still on board).
        // precise: stop at NodeBurn.PreciseDv instead of NodeBurn.DoneDv.
        public NodeExecutor(Vessel vessel, Func<double, string> formatTime, bool maneuverModeRequired = false,
                            ISet<uint>? protectedParts = null, bool precise = false)
        {
            _burn = new NodeBurn(precise ? NodeBurn.PreciseDv : NodeBurn.DoneDv);
            _vessel = vessel;
            _formatTime = formatTime;
            _maneuverModeRequired = maneuverModeRequired;
            _protectedParts = protectedParts;
        }

        public string Status { get; private set; } = "";
        public bool   IsWaiting { get; private set; }  // before ignition, warping towards the burn

        public void FastForward() => _warp.Now();
        public string Error  { get; private set; } = "";

        public Result Tick()
        {
            if (FlightGlobals.ActiveVessel != _vessel) return Fail("The vessel is no longer the active vessel.");
            var solver = _vessel.patchedConicSolver;
            if (solver == null || solver.maneuverNodes.Count == 0)
                return Fail("The maneuver node disappeared: it was deleted, or the game reloaded the " +
                            "flight scene (switching to an unloaded vessel or leaving the scene does that).");
            Attach();

            var node    = solver.maneuverNodes[0];
            double now  = Planetarium.GetUniversalTime();
            var burnVec = node.GetBurnVector(_vessel.orbit);
            double dvLeft = burnVec.magnitude;
            var (thrust, isp) = VesselParts.ActiveEngines(_vessel);
            double mass = _vessel.GetTotalMass();
            if (_burning && Time.time >= _nextReport)
            {
                _nextReport = Time.time + ReportEvery;
                RoLog.Dbg($"{_vessel.vesselName} burning: {Vector3d.Dot(burnVec, _direction):F1} m/s left along the burn " +
                          $"({dvLeft:F1} total), throttle {_throttle:F2}, " +
                          $"{Vector3.Angle(_vessel.ReferenceTransform.up, (Vector3)(_tail ? _direction : burnVec)):F1}° off, " +
                          $"tail {_tail}, {RoLog.Propulsion(_vessel)}; now {RoLog.Describe(_vessel.orbit)}.");
            }

            if (!_tail && !PointAt(node))
            {
                // A vessel just switched to, or short of power, may get SAS back shortly.
                if (double.IsNaN(_noSasSince)) _noSasSince = now;
                _throttle = 0f;
                if (now - _noSasSince < SasWait)
                {
                    Status = "Waiting for SAS: " + VesselParts.ControlReport(_vessel);
                    return Result.Running;
                }
                Debug.Log($"[RelayOrbiter] {_vessel.vesselName} can't point: {VesselParts.ControlReport(_vessel)}");
                return Fail((_maneuverModeRequired
                    ? "This burn needs SAS maneuver mode (SAS level 3)"
                    : "The vessel can't point along the burn") +
                    $": {VesselParts.LikelyControlProblem(_vessel)}.");
            }
            _noSasSince = double.NaN;

            if (!_burning)
            {
                if (thrust <= 0.0)
                {
                    if (AutoActivateEngines && !_activationTried)
                    {
                        _activationTried = true;
                        int n = ActivateStageEngines();
                        if (n > 0)
                        {
                            Debug.Log($"[RelayOrbiter] {_vessel.vesselName}: no engine running, activated {n} " +
                                      "engine(s) of the current stage.");
                            _noThrustSince = now;  // give them time to ignite
                            Status = "Activating the engines";
                            return Result.Running;
                        }
                    }
                    // A spent stage before the burn: stage it away (after a short grace,
                    // so a stage just activated has time to ignite).
                    if (double.IsNaN(_noThrustSince)) _noThrustSince = now;
                    else if (now - _noThrustSince > FlameoutGrace) return StageOrFail(now, dvLeft);
                    Status = "Waiting for a running engine";
                    return Result.Running;
                }
                _noThrustSince = double.NaN;
                double duration = BurnTiming.Duration(node.DeltaV.magnitude, mass, thrust, isp) ?? 0.0;
                double start = node.UT - duration / 2.0;
                if (now < start)
                {
                    _throttle = 0f;
                    IsWaiting = true;
                    // Pointing time is only needed until it points: once aligned and still,
                    // warp on (rails warp keeps the attitude) until just before ignition.
                    bool aligned = Vector3.Angle(_vessel.ReferenceTransform.up, (Vector3)burnVec) < AlignedDeg &&
                                   _vessel.angularVelocity.magnitude < StillRate;
                    _warp.Towards(start - (aligned ? IgnitionLead : DeploymentSequence.AlignLead));
                    Status = $"Burn of {dvLeft:F1} m/s in {_formatTime(start - now)} ({duration:F0} s)";
                    return Result.Running;
                }
                if (TimeWarp.CurrentRateIndex > 0) TimeWarp.SetRate(0, true, false);
                IsWaiting = false;
                _burning = true;
                _direction = burnVec.normalized;
                RoLog.Dbg($"{_vessel.vesselName} ignition: {dvLeft:F1} m/s, about {duration:F0} s, {(node.UT - now):F0} s " +
                          $"before the node, SAS {_mode}, {Vector3.Angle(_vessel.ReferenceTransform.up, (Vector3)burnVec):F1}° off, " +
                          $"{RoLog.Propulsion(_vessel)}; protected parts {_protectedParts?.Count ?? 0}.");
            }

            // Measure what is left along the original burn direction: the sideways
            // residue is just pointing error, and a negative value means overshoot.
            double accel = thrust > 0.0 ? thrust / mass : 0.0;
            dvLeft = Vector3d.Dot(burnVec, _direction);
            // Only after thrusting aligned once, so a short burn doesn't freeze a bad attitude.
            if (!_tail && _thrusted && NodeBurn.IsTail(dvLeft, accel))
            {
                _tail = true;
                if (_vessel.Autopilot.CanSetMode(VesselAutopilot.AutopilotMode.StabilityAssist))
                    _vessel.Autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);
                RoLog.Dbg($"{_vessel.vesselName}: burn tail, attitude held ({dvLeft:F2} m/s left, {accel:F2} m/s²).");
            }

            if (_burn.Update(dvLeft))
            {
                RoLog.Dbg($"{_vessel.vesselName}: burn complete, {dvLeft:F3} m/s left along the burn ({burnVec.magnitude:F3} " +
                          $"total), staged {_staged} engine stage(s); now {RoLog.Describe(_vessel.orbit)}; {RoLog.Propulsion(_vessel)}.");
                Finish();
                node.RemoveSelf();
                Status = "Burn complete.";
                return Result.Done;
            }

            // Out of fuel?
            if (thrust <= 0.0)
            {
                if (double.IsNaN(_noThrustSince)) _noThrustSince = now;
                else if (now - _noThrustSince > FlameoutGrace) return StageOrFail(now, dvLeft);
            }
            else _noThrustSince = double.NaN;

            // In the tail the attitude is held: compare with the original direction.
            double angle = Vector3.Angle(_vessel.ReferenceTransform.up, (Vector3)(_tail ? _direction : burnVec));
            if (angle > NodeBurn.MaxAlignmentDeg)
            {
                if (double.IsNaN(_misalignedSince)) _misalignedSince = now;
                else if (now - _misalignedSince > AlignTimeout) return Fail("Could not point along the burn.");
            }
            else _misalignedSince = double.NaN;

            _throttle = (float)NodeBurn.Throttle(dvLeft, accel, angle);
            if (_throttle > 0f) _thrusted = true;
            Status = $"Burning: {dvLeft.ToString(dvLeft < 1.0 ? "F3" : "F1")} m/s left, {angle:F1}° off" + (_tail ? " (holding attitude)" : "");
            return Result.Running;
        }

        private Result StageOrFail(double now, double dvLeft)
        {
            if (_protectedParts == null) return Fail($"Out of thrust with {dvLeft:F1} m/s left.");
            var (next, parts, released) = VesselParts.NextStage(_vessel);
            var decision = StagingGuard.Decide(next, parts, _protectedParts, _staged, released);
            RoLog.Dbg($"{_vessel.vesselName} out of thrust ({dvLeft:F1} m/s left): next stage {next} has " +
                      $"{VesselParts.DescribeParts(_vessel, parts)}; releases {released.Count} part(s) " +
                      $"({released.Count(_protectedParts.Contains)} protected); engine stages lit so far {_staged}; " +
                      $"decision {decision}; {RoLog.Propulsion(_vessel)}.");
            switch (decision)
            {
                case StagingDecision.Stage:
                    _throttle = 0f;
                    // Only a stage that lights engines counts against the limit: fairings and
                    // decouplers between the spent stage and the next engine are free.
                    bool lights = VesselParts.HasEngines(_vessel, parts);
                    StageManager.ActivateNextStage();
                    if (lights) _staged++;
                    _noThrustSince = now;  // give the new stage time to ignite
                    Status = $"Staging: stage {next} (spent stage, {dvLeft:F1} m/s left)";
                    Debug.Log($"[RelayOrbiter] {_vessel.vesselName}: out of thrust with {dvLeft:F1} m/s left, " +
                              $"staged stage {next}" + (lights ? "." : " (no engines)."));
                    return Result.Running;
                case StagingDecision.WouldReleaseSatellite:
                    return Fail($"Out of thrust with {dvLeft:F1} m/s left, and the next stage would release " +
                                "a satellite: refuel the carrier or move the satellites to later stages.");
                case StagingDecision.NothingLeft:
                    return Fail($"Out of fuel with {dvLeft:F1} m/s left: no stage left.");
                default:
                    return Fail($"Still out of thrust after lighting {_staged} stages of engines ({dvLeft:F1} m/s left).");
            }
        }

        // The engines of the current stage (already staged) that are off, outside the
        // protected parts. A lone satellite (no protected parts) whose current stage has
        // none gets the next stage's engines instead: only those, never all of them.
        // Returns how many were activated.
        private int ActivateStageEngines()
        {
            int stage = _vessel.currentStage;
            var off = _vessel.FindPartModulesImplementing<ModuleEngines>()
                .Where(e => e.isEnabled && !e.EngineIgnited && !e.flameout &&
                            (_protectedParts == null || !_protectedParts.Contains(e.part.flightID)))
                .ToList();
            var current = off.Where(e => e.part.inverseStage >= stage).ToList();
            if (current.Count == 0 && _protectedParts == null && off.Count > 0)
            {
                int next = off.Max(e => e.part.inverseStage);
                current = off.Where(e => e.part.inverseStage == next).ToList();
            }
            foreach (var e in current) e.Activate();
            if (current.Count > 0)
                Debug.Log($"[RelayOrbiter] {_vessel.vesselName}: activated " +
                          string.Join(", ", current.Select(e => $"{e.part.partInfo.title} (stage {e.part.inverseStage})")) +
                          $"; current stage {stage}.");
            return current.Count;
        }

        // Stop thrusting and give the controls back. Safe to call more than once.
        // Also called while the flight scene is being destroyed: everything may be gone.
        public void Finish()
        {
            _throttle = 0f;
            if (_attached)
            {
                if (_vessel != null) _vessel.OnFlyByWire -= Drive;
                _attached = false;
            }
            if (_vessel == null || FlightGlobals.ActiveVessel != _vessel) return;
            if (FlightInputHandler.state != null) FlightInputHandler.state.mainThrottle = 0f;
            var autopilot = _vessel.Autopilot;
            if (autopilot != null && autopilot.CanSetMode(VesselAutopilot.AutopilotMode.StabilityAssist))
                autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);
        }

        private Result Fail(string error)
        {
            if (_vessel != null)
                RoLog.Dbg($"{_vessel.vesselName} burn failed: {error} Burning {_burning}, tail {_tail}, SAS mode {_mode}, " +
                          $"{RoLog.Propulsion(_vessel)}; {RoLog.Describe(_vessel.orbit)}.");
            Finish();
            Error = error;
            return Result.Failed;
        }

        private void Attach()
        {
            if (_attached) return;
            _vessel.OnFlyByWire += Drive;
            _attached = true;
        }

        private void Drive(FlightCtrlState s) => s.mainThrottle = _throttle;

        // Maneuver mode when available; the deployment's burns are purely prograde or
        // retrograde, so those SAS modes (level 1) work as a fallback.
        private bool PointAt(ManeuverNode node)
        {
            if (_mode == null)
            {
                var ap = _vessel.Autopilot;
                if (ap.CanSetMode(VesselAutopilot.AutopilotMode.Maneuver))
                    _mode = VesselAutopilot.AutopilotMode.Maneuver;
                else if (_maneuverModeRequired)
                    return false;
                else if (node.DeltaV.z >= 0.0 && ap.CanSetMode(VesselAutopilot.AutopilotMode.Prograde))
                    _mode = VesselAutopilot.AutopilotMode.Prograde;
                else if (node.DeltaV.z < 0.0 && ap.CanSetMode(VesselAutopilot.AutopilotMode.Retrograde))
                    _mode = VesselAutopilot.AutopilotMode.Retrograde;
                else
                    return false;
            }
            _vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            if (_vessel.Autopilot.Mode != _mode.Value) _vessel.Autopilot.SetMode(_mode.Value);
            return true;
        }
    }
}

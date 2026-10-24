using System;
using System.Collections.Generic;
using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Relay Orbiter's own launch: AscentGuidance's gravity turn, steered by KSP's SAS
    // (LockRotation), throttled through fly-by-wire, staged by LaunchStaging, and the
    // circularization flown as a maneuver node by the NodeExecutor.
    internal sealed class AscentAutopilot : ILaunchAutopilot
    {
        private readonly Vessel _vessel;
        private readonly AscentGuidance _guidance;
        private readonly LaunchStaging _staging;
        private readonly HashSet<uint> _protected;
        private readonly IDeploymentPlanner _planner;
        private readonly Func<double, string> _formatTime;
        private readonly double _targetAltitude, _headingDeg;
        private NodeExecutor? _executor;
        private float _throttle;
        private bool _attached, _stopped;
        private double _nextReport, _noThrustSince = double.NaN;
        private const double ReportEvery = 20.0;      // s between ascent log lines
        private const double NoThrustTimeout = 10.0;  // s wanting thrust without any

        public AscentAutopilot(Vessel vessel, double altitude, double inclinationDeg, IEnumerable<uint> protectedParts,
                               LaunchStaging staging, IDeploymentPlanner planner, Func<double, string> formatTime)
        {
            _vessel = vessel;
            _targetAltitude = altitude;
            var body = vessel.mainBody;
            _guidance = new AscentGuidance(altitude, body.atmosphere ? body.atmosphereDepth : 0.0);
            _staging = staging;
            _protected = new HashSet<uint>(protectedParts);
            _planner = planner;
            _formatTime = formatTime;
            double orbital = Math.Sqrt(body.gravParameter / (body.Radius + altitude));
            double surface = body.rotationPeriod > 0.0 ? 2.0 * Math.PI * body.Radius / body.rotationPeriod : 0.0;
            _headingDeg = AscentGuidance.LaunchHeadingDeg(inclinationDeg, vessel.latitude, orbital, surface);
            Debug.Log($"[RelayOrbiter] Ascent: to {altitude / 1000.0:F1} km, inclination {inclinationDeg:F1}°, " +
                      $"heading {_headingDeg:F1}° from latitude {vessel.latitude:F2}°, turn complete by " +
                      $"{AscentGuidance.TurnEndAltitude(altitude, body.atmosphere ? body.atmosphereDepth : 0.0) / 1000.0:F0} km.");
        }

        public string Name => "Relay Orbiter";
        public string Status { get; private set; } = "Ready for liftoff";
        public bool InOrbit { get; private set; }
        public string? Error { get; private set; }

        public void Tick(Vessel vessel)
        {
            if (_stopped || InOrbit || Error != null) return;
            if (vessel != _vessel) { Fail("The vessel is no longer the active vessel."); return; }

            // The circularization: the node executor's.
            if (_executor != null)
            {
                var r = _executor.Tick();
                Status = "Circularizing: " + _executor.Status;
                if (r == NodeExecutor.Result.Failed) Fail(_executor.Error);
                else if (r == NodeExecutor.Result.Done)
                {
                    InOrbit = true;
                    Status = $"In orbit: {_vessel.orbit.ApA / 1000.0:F1} × {_vessel.orbit.PeA / 1000.0:F1} km";
                    Release();
                }
                return;
            }

            Attach();
            _staging.Tick(vessel, _throttle);
            var o = vessel.orbit;
            var command = _guidance.Update(vessel.altitude, vessel.srfSpeed, o.ApA, o.timeToAp);
            var (thrust, _) = VesselParts.ActiveEngines(vessel);
            double now = Time.time;
            if (now >= _nextReport)
            {
                _nextReport = now + ReportEvery;
                Debug.Log($"[RelayOrbiter] Ascent: {command.Phase}, altitude {vessel.altitude / 1000.0:F1} km, apoapsis " +
                          $"{o.ApA / 1000.0:F1} km, pitch {command.PitchDeg:F0}°, throttle {_throttle:F2}, " +
                          $"thrust {thrust:F0} kN, q {vessel.dynamicPressurekPa:F1} kPa, " +
                          $"angle of attack {Vector3.Angle(_vessel.ReferenceTransform.up, (Vector3)_vessel.srf_velocity):F1}°, " +
                          $"stage {VesselParts.CurrentStage(vessel)}.");
            }
            // Thrust wanted but none for a while, nothing left to stage: say so, don't coast on.
            if (command.Throttle > 0.0 && thrust <= 0.0)
            {
                if (double.IsNaN(_noThrustSince)) _noThrustSince = now;
                else if (now - _noThrustSince > NoThrustTimeout)
                {
                    Fail($"no thrust for {NoThrustTimeout:F0} s with the apoapsis at {o.ApA / 1000.0:F1} km " +
                         $"(target {_targetAltitude / 1000.0:F0} km): the carrier's stages are spent, or its engine " +
                         "didn't light. Check the staging; the satellites' stages are never fired.");
                    return;
                }
            }
            else _noThrustSince = double.NaN;
            if (command.Phase == AscentPhase.Circularize)
            {
                _throttle = 0f;
                Release();
                PlanCircularization();
                return;
            }
            double q = vessel.dynamicPressurekPa;
            _throttle = (float)AscentGuidance.LimitThrottle(command.Throttle, q);
            var wanted = command.Phase == AscentPhase.Coast ? _vessel.obt_velocity.normalized  // least drag
                                                            : Direction(command.PitchDeg);
            Point(LimitAoA(wanted, AscentGuidance.AllowedAoADeg(q)));
            Status = $"{Describe(command.Phase)}: pitch {command.PitchDeg:F0}°, heading {_headingDeg:F0}°, apoapsis " +
                     $"{o.ApA / 1000.0:F1} of {_targetAltitude / 1000.0:F0} km";
        }

        public void Stop()
        {
            _stopped = true;
            _executor?.Finish();
            Release();
        }

        // The circular orbit at the apoapsis, as a node for the node executor (the
        // satellites' stages stay protected).
        private void PlanCircularization()
        {
            var o = _vessel.orbit;
            var solver = _vessel.patchedConicSolver;
            if (solver == null) { Fail("Maneuver nodes are not available."); return; }
            foreach (var n in new List<ManeuverNode>(solver.maneuverNodes)) n.RemoveSelf();
            double ut = Planetarium.GetUniversalTime() + o.timeToAp;
            double dv = _planner.CircularizeAt(o.referenceBody.gravParameter, o.ApR, o.semiMajorAxis);
            var node = solver.AddManeuverNode(ut);
            node.OnGizmoUpdated(new Vector3d(0.0, 0.0, dv), ut);
            _executor = new NodeExecutor(_vessel, _formatTime, protectedParts: _protected);
            Debug.Log($"[RelayOrbiter] Ascent: coasting done at {_vessel.altitude / 1000.0:F1} km; circularization " +
                      $"{dv:F1} m/s at the apoapsis ({o.ApA / 1000.0:F1} km).");
            Status = $"Circularization planned: {dv:F0} m/s";
        }

        // Pitch above the horizon along the launch heading.
        private Vector3d Direction(double pitchDeg)
        {
            double p = pitchDeg * Math.PI / 180.0, h = _headingDeg * Math.PI / 180.0;
            var up = _vessel.upAxis;
            var horizontal = (_vessel.north * Math.Cos(h) + _vessel.east * Math.Sin(h)).normalized;
            return (up * Math.Sin(p) + horizontal * Math.Cos(p)).normalized;
        }

        // The wanted direction, but no further than maxDeg from the surface velocity: the
        // turn then follows the trajectory (a true gravity turn) instead of forcing it.
        private Vector3d LimitAoA(Vector3d wanted, double maxDeg)
        {
            var airflow = _vessel.srf_velocity;
            if (maxDeg >= 180.0 || airflow.magnitude < 1.0) return wanted;
            var limited = Vector3.RotateTowards((Vector3)airflow.normalized, (Vector3)wanted,
                                                (float)(maxDeg * Math.PI / 180.0), 0f);
            return ((Vector3d)limited).normalized;
        }

        // Hold the nose along dir with KSP's SAS (stability assist, locked rotation).
        private void Point(Vector3d dir)
        {
            var ap = _vessel.Autopilot;
            _vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            if (ap.Mode != VesselAutopilot.AutopilotMode.StabilityAssist &&
                ap.CanSetMode(VesselAutopilot.AutopilotMode.StabilityAssist))
                ap.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);
            // The vessel's nose is its transform's up: rotate forward onto dir, keep the roll.
            var rotation = Quaternion.LookRotation((Vector3)dir, -_vessel.ReferenceTransform.forward) *
                           Quaternion.Euler(90f, 0f, 0f);
            ap.SAS.LockRotation(rotation);
        }

        private void Attach()
        {
            if (_attached) return;
            _vessel.OnFlyByWire += Drive;
            _attached = true;
        }

        private void Release()
        {
            if (!_attached) return;
            _vessel.OnFlyByWire -= Drive;
            _attached = false;
            if (FlightInputHandler.state != null) FlightInputHandler.state.mainThrottle = 0f;
        }

        private void Drive(FlightCtrlState s) => s.mainThrottle = _throttle;

        private void Fail(string error)
        {
            Error = error;
            Status = "Launch stopped: " + error;
            _executor?.Finish();
            Release();
            Debug.Log("[RelayOrbiter] " + Status);
        }

        private static string Describe(AscentPhase p) => p switch
        {
            AscentPhase.Vertical    => "Vertical climb",
            AscentPhase.GravityTurn => "Gravity turn",
            _                       => "Coasting to the apoapsis",
        };
    }
}

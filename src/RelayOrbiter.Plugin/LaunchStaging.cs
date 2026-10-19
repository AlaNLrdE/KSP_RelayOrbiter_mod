using System.Collections.Generic;
using System.Linq;
using KSP.UI.Screens;
using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Staging during a launch, whoever flies it: when thrust is wanted and nothing burns,
    // the next stage; while the core burns, a next stage that only drops spent boosters.
    // Never one holding (or dropping) a satellite's part or separator, and never twice
    // before the last staging shows.
    internal sealed class LaunchStaging
    {
        private const double StageConfirmTimeout = 3.0;  // s for a staging to show
        private const double BoosterCheckEvery   = 0.5;  // s between looks for spent boosters

        private readonly AscentStager _stager = new AscentStager();
        private readonly HashSet<uint> _protected;
        private bool _stopped;
        private int? _awaiting;   // staged, not yet showing as fired
        private double _stagedAt, _nextBoosterCheck;
#if DEBUG
        private string? _boosterNote;  // the last booster-drop reasoning logged
#endif

        public LaunchStaging(IEnumerable<uint> protectedParts)
        {
            _protected = new HashSet<uint>(protectedParts);
        }

        // throttle: what the autopilot asks for (0..1).
        public void Tick(Vessel vessel, double throttle)
        {
            double now = Time.time;
            WatchStages(vessel);
            if (_awaiting.HasValue)
            {
                if (VesselParts.CurrentStage(vessel) <= _awaiting.Value) _awaiting = null;
                else if (now - _stagedAt < StageConfirmTimeout) return;
                else
                {
                    // Not confirmed: carry on from what KSP says now (the guard checks the
                    // stage that really comes next).
                    Debug.Log($"[RelayOrbiter] Ascent: stage {_awaiting} not confirmed (stage counters " +
                              $"{vessel.currentStage} / {StageManager.CurrentStage}); carrying on.");
                    _awaiting = null;
                }
            }
            if (_stopped) return;
            if (DropSpentBoosters(vessel, now)) return;
            var (thrust, _) = VesselParts.ActiveEngines(vessel);
            if (!_stager.ShouldStage(now, thrust, throttle)) return;
            var (next, parts, released) = VesselParts.NextStage(vessel);
            var decision = StagingGuard.Decide(next, parts, _protected, _stager.StagedSinceThrust, released);
            RoLog.Dbg($"Launch staging, no thrust: next stage {next} has {VesselParts.DescribeParts(vessel, parts)}; releases " +
                      $"{released.Count} part(s) ({released.Count(_protected.Contains)} protected); engine stages lit " +
                      $"without thrust {_stager.StagedSinceThrust}; decision {decision}; {RoLog.Propulsion(vessel)}.");
            if (decision == StagingDecision.Stage)
            {
                bool lights = VesselParts.HasEngines(vessel, parts);
                StageManager.ActivateNextStage();
                _stager.Staged(now, lightsEngines: lights);
                _awaiting = next;
                _ourStaging = next;
                _stagedAt = now;
                Debug.Log($"[RelayOrbiter] Ascent: no thrust, staged stage {next}" +
                          (released.Count > 0 ? $" (drops {released.Count} part(s))." : "."));
                return;
            }
            _stager.Staged(now, fired: false);
            Stop($"no thrust, but not staging stage {next}: " + decision switch
            {
                StagingDecision.WouldReleaseSatellite => "it holds or drops a satellite. The carrier is out of fuel: check its stages.",
                StagingDecision.NothingLeft           => "no stage left.",
                _                                     => $"already lit {StagingGuard.MaxStagesPerBurn} stages of engines without thrust.",
            });
        }

        // Burnt-out boosters beside a core that still burns: the next stage, when it only
        // drops them. (Nothing burning at all is the no-thrust staging's case.)
        private bool DropSpentBoosters(Vessel vessel, double now)
        {
            if (now < _nextBoosterCheck) return false;
            _nextBoosterCheck = now + BoosterCheckEvery;
            var engines = VesselParts.EngineStates(vessel);
            if (!engines.ContainsValue(EngineState.Spent) || !engines.ContainsValue(EngineState.Running)) return false;
            var (next, parts, released) = VesselParts.NextStage(vessel);
            var guard = StagingGuard.Decide(next, parts, _protected, 0, released);
            bool onlySpent = guard == StagingDecision.Stage && BoosterSeparation.DropsOnlySpentBoosters(parts, released, engines);
#if DEBUG
            // Why spent engines stay on (logged when the reason changes, not every half second).
            string note =$"next stage {next}: {VesselParts.DescribeParts(vessel, parts)}; releases {released.Count} part(s): " +
                          $"{engines.Count(e => released.Contains(e.Key) && e.Value == EngineState.Spent)} spent, " +
                          $"{engines.Count(e => released.Contains(e.Key) && e.Value == EngineState.Running)} running, " +
                          $"{engines.Count(e => released.Contains(e.Key) && e.Value == EngineState.Unlit)} unlit engine(s); " +
                          $"guard {guard}, only spent boosters {onlySpent}";
            if (note != _boosterNote)
            {
                _boosterNote = note;
                RoLog.Dbg($"Spent engine(s) beside running ones: {note}.");
            }
#endif
            if (!onlySpent) return false;
            StageManager.ActivateNextStage();
            _awaiting = next;
            _ourStaging = next;
            _stagedAt = now;
            Debug.Log($"[RelayOrbiter] Ascent: spent booster(s), staged stage {next} (drops {released.Count} part(s)).");
            return true;
        }

        private int? _lastStage;   // the vessel's stage at the last Tick
        private int? _ourStaging;  // the last stage Relay Orbiter activated

        // Every stage change, and whether Relay Orbiter fired it: a stage it didn't fire
        // came from MechJeb or the player.
        private void WatchStages(Vessel vessel)
        {
            int stage = VesselParts.CurrentStage(vessel);
            if (_lastStage.HasValue && stage != _lastStage.Value)
            {
                bool ours = _ourStaging.HasValue && stage <= _ourStaging.Value;
                string text = $"Stage {_lastStage} → {stage} during the launch, " +
                              (ours ? "fired by Relay Orbiter" : "NOT fired by Relay Orbiter (MechJeb or the player)") +
                              $"; {RoLog.Propulsion(vessel)}, altitude {RoLog.Km(vessel.altitude)}.";
                if (ours) RoLog.Dbg(text); else RoLog.Info(text);
            }
            _lastStage = stage;
        }

        public bool OutOfStages => _stopped;

        private void Stop(string why)
        {
            if (_stopped) return;
            _stopped = true;
            Debug.Log("[RelayOrbiter] Ascent: " + why);
        }
    }

    // Who flies a launch: Relay Orbiter's own ascent, or MechJeb's.
    internal interface ILaunchAutopilot
    {
        string Name     { get; }
        string Status   { get; }
        bool   InOrbit  { get; }   // done: circular orbit reached
        string? Error   { get; }   // set when it gave up
        void   Tick(Vessel vessel);
        void   Stop();
    }

    // MechJeb's ascent, staged by Relay Orbiter (its own autostaging goes through the
    // satellites' stages).
    internal sealed class MechJebLaunch : ILaunchAutopilot
    {
        private readonly MechJebAscent _mechJeb;
        private readonly LaunchStaging _staging;
        private readonly object _user;

        public MechJebLaunch(MechJebAscent mechJeb, LaunchStaging staging, object user)
        {
            _mechJeb = mechJeb;
            _staging = staging;
            _user    = user;
        }

        public string Name => "MechJeb 2";
        public string Status => _mechJeb.Status;
        public bool InOrbit { get; private set; }
        public string? Error => null;

        private double _nextReport;

        public void Tick(Vessel vessel)
        {
            _staging.Tick(vessel, vessel.ctrlState.mainThrottle);
            if (Time.time >= _nextReport)
            {
                _nextReport = Time.time + (vessel.dynamicPressurekPa >= 2.5 ? 5.0 : 20.0);
                RoLog.Dbg($"MechJeb ascent: \"{_mechJeb.Status}\", engaged {_mechJeb.Enabled}, altitude {RoLog.Km(vessel.altitude)}, " +
                          $"apoapsis {RoLog.Km(vessel.orbit.ApA)}, i {vessel.orbit.inclination:F2}°, q {vessel.dynamicPressurekPa:F1} kPa, " +
                          $"angle of attack {Vector3.Angle(vessel.ReferenceTransform.up, (Vector3)vessel.srf_velocity):F1}°, " +
                          $"throttle {vessel.ctrlState.mainThrottle:F2}, {RoLog.Propulsion(vessel)}.");
            }
            // MechJeb disengages itself once the orbit is circularized.
            if (vessel.situation == Vessel.Situations.ORBITING && !_mechJeb.Enabled) InOrbit = true;
        }

        public void Stop() => _mechJeb.Disengage(_user);
    }
}

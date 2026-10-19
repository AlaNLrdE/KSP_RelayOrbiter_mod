using System.Collections.Generic;
using KSP.UI.Screens;
using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Staging during a launch, whoever flies it: when thrust is wanted and nothing burns,
    // the next stage, but never one holding (or dropping) a satellite's part or
    // separator, and never twice before the last staging shows.
    internal sealed class LaunchStaging
    {
        private const double StageConfirmTimeout = 3.0;  // s for a staging to show

        private readonly AscentStager _stager = new AscentStager();
        private readonly HashSet<uint> _protected;
        private bool _stopped;
        private int? _awaiting;   // staged, not yet showing as fired
        private double _stagedAt;

        public LaunchStaging(IEnumerable<uint> protectedParts)
        {
            _protected = new HashSet<uint>(protectedParts);
        }

        // throttle: what the autopilot asks for (0..1).
        public void Tick(Vessel vessel, double throttle)
        {
            double now = Time.time;
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
            var (thrust, _) = VesselParts.ActiveEngines(vessel);
            if (!_stager.ShouldStage(now, thrust, throttle)) return;
            var (next, parts, released) = VesselParts.NextStage(vessel);
            var decision = StagingGuard.Decide(next, parts, _protected, _stager.StagedSinceThrust, released);
            if (decision == StagingDecision.Stage)
            {
                StageManager.ActivateNextStage();
                _stager.Staged(now);
                _awaiting = next;
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
                _                                     => $"already staged {StagingGuard.MaxStagesPerBurn} times without thrust.",
            });
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

        public void Tick(Vessel vessel)
        {
            _staging.Tick(vessel, vessel.ctrlState.mainThrottle);
            // MechJeb disengages itself once the orbit is circularized.
            if (vessel.situation == Vessel.Situations.ORBITING && !_mechJeb.Enabled) InOrbit = true;
        }

        public void Stop() => _mechJeb.Disengage(_user);
    }
}

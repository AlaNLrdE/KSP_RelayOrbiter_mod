using System;

namespace RelayOrbiter.Core
{
    // When to stage during a launch flown by another autopilot (MechJeb), staging
    // ourselves instead of its autostaging: when thrust is wanted (throttle up) and no
    // engine gives any for a moment. What may fire is StagingGuard's decision.
    public sealed class AscentStager
    {
        public const double Delay       = 0.5;   // s without thrust before staging (lets a new stage ignite)
        public const double MinThrottle = 0.01;  // below this no thrust is wanted (a coast)

        private double _noThrustSince = double.NaN;

        // Stages fired since the last time an engine gave thrust (StagingGuard's limit).
        public int StagedSinceThrust { get; private set; }

        public bool ShouldStage(double now, double thrust, double throttle)
        {
            if (thrust > 0.0)
            {
                _noThrustSince = double.NaN;
                StagedSinceThrust = 0;
                return false;
            }
            if (throttle < MinThrottle) { _noThrustSince = double.NaN; return false; }
            if (double.IsNaN(_noThrustSince)) { _noThrustSince = now; return false; }
            return now - _noThrustSince >= Delay;
        }

        // A stage fired (or staging was refused): wait again before the next one.
        public void Staged(double now, bool fired = true)
        {
            _noThrustSince = now;
            if (fired) StagedSinceThrust++;
        }
    }
}

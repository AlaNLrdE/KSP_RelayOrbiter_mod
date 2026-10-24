using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Time warp towards a target UT that survives interruptions: KSP (or another mod)
    // can drop out of warp early, and a single WarpTo call then leaves the wait at 1x.
    internal sealed class Warp
    {
        private const double Margin       = 5.0;  // s: close enough, no need to warp
        private const float  RetrySeconds = 3f;   // real seconds between WarpTo calls

        private float _nextAttempt;

        // The player's setting: off, Relay Orbiter never time-warps (waits run at the
        // player's own warp; a burn still drops out of warp on time).
        public static bool Enabled = true;

        // Call every frame while waiting for targetUT.
        public void Towards(double targetUT)
        {
            if (!Enabled) return;
            double now = Planetarium.GetUniversalTime();
            if (now >= targetUT - Margin || TimeWarp.CurrentRateIndex > 0) return;
            if (Time.realtimeSinceStartup < _nextAttempt) return;
            _nextAttempt = Time.realtimeSinceStartup + RetrySeconds;
            TimeWarp.fetch.WarpTo(targetUT, 8.0, 2.5);
        }

        // Retry right away on the next frame (the "Fast forward" button).
        public void Now() => _nextAttempt = 0f;
    }
}

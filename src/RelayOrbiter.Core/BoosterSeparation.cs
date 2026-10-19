using System.Collections.Generic;

namespace RelayOrbiter.Core
{
    public enum EngineState
    {
        Unlit,    // never ignited (a later stage, a separation motor)
        Running,  // ignited and burning
        Spent,    // ignited and flamed out: its propellant is gone (a burnt-out booster)
    }

    // Dropping spent boosters while the core still burns. KSP keeps a burnt-out booster
    // attached (dead weight and drag) until something stages, and the launch staging
    // otherwise stages only once nothing burns at all.
    public static class BoosterSeparation
    {
        // nextStageParts: the parts the next stage activates; releasedParts: every part its
        // decouplers would cut loose; engines: the state of every engine part on the vessel.
        // True when that stage drops at least one spent engine and nothing else that
        // matters: no running engine, no unlit engine that stays unlit, and it lights no
        // engine that stays on board (separation motors on the boosters themselves are fine).
        public static bool DropsOnlySpentBoosters(IEnumerable<uint> nextStageParts,
                                                  IEnumerable<uint> releasedParts,
                                                  IReadOnlyDictionary<uint, EngineState> engines)
        {
            var activated = new HashSet<uint>(nextStageParts);
            var released = new HashSet<uint>(releasedParts);
            foreach (uint id in activated)
                if (engines.ContainsKey(id) && !released.Contains(id)) return false;

            bool dropsSpent = false;
            foreach (uint id in released)
            {
                if (!engines.TryGetValue(id, out var state)) continue;
                switch (state)
                {
                    case EngineState.Spent:   dropsSpent = true; break;
                    case EngineState.Running: return false;
                    case EngineState.Unlit:   if (!activated.Contains(id)) return false; break;
                }
            }
            return dropsSpent;
        }
    }
}

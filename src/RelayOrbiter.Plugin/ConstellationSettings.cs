using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // The constellation the player is planning, shared by the window's tabs.
    internal sealed class ConstellationSettings
    {
        private int _satellites = 3;

        public int Satellites
        {
            get => _satellites;
            set => _satellites = Mathf.Clamp(value, Resonance.MinSatellites, Resonance.MaxSatellites);
        }
    }
}

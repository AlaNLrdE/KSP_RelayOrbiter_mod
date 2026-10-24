using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // The constellation the player is planning, shared by the window's tabs.
    internal sealed class ConstellationSettings
    {
        private int _satellites = 3;

        // Target inclination for the constellation; off = keep the current plane.
        public bool   ChangeInclination { get; set; }
        public double InclinationDeg    { get; set; }

        // Rename the satellites ("Kerbin Orion 2/6 - SYNC") and mark them as relays.
        public bool   NameSatellites    { get; set; } = true;

        // Launches: the orbit to launch to (km; NaN: automatic) and who flies the ascent.
        public double LaunchAltitudeKm  { get; set; } = double.NaN;
        public bool   MechJebAscent     { get; set; }

        public int Satellites
        {
            get => _satellites;
            set => _satellites = Mathf.Clamp(value, Resonance.MinSatellites, Resonance.MaxSatellites);
        }
    }
}

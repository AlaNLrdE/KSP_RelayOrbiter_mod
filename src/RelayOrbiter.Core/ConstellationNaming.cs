using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RelayOrbiter.Core
{
    // Unique, readable names for the satellites of a constellation, e.g.
    // "Kerbin Orion 2/6 - SYNC i10".
    public static class ConstellationNaming
    {
        public static readonly IReadOnlyList<string> Callsigns = new[]
        {
            "Aurora", "Beacon", "Comet", "Draco", "Echo", "Fenix", "Gemini", "Helios",
            "Icarus", "Juno", "Kestrel", "Lyra", "Meteor", "Nova", "Orion", "Pulsar",
            "Quasar", "Raptor", "Sirius", "Titan", "Umbra", "Vega", "Wraith", "Xenon", "Zenith",
        };

        public const double SyncTolerance     = 0.01;  // fraction of the synchronous altitude
        public const double MinInclinationDeg = 0.5;   // below this the orbit counts as equatorial

        // First callsign not yet used by a constellation around this body
        // ("Aurora", ..., "Zenith", then "Aurora II", ...).
        public static string Callsign(string body, IEnumerable<string> existingVesselNames)
        {
            var names = existingVesselNames.ToList();
            for (int series = 1; ; ++series)
                foreach (var c in Callsigns)
                {
                    string callsign = series == 1 ? c : $"{c} {Roman(series)}";
                    string prefix = $"{body} {callsign} ";
                    if (!names.Any(n => n.StartsWith(prefix, StringComparison.Ordinal)))
                        return callsign;
                }
        }

        public static string OrbitTag(double altitude, double? syncAltitude)
        {
            if (syncAltitude.HasValue &&
                Math.Abs(altitude - syncAltitude.Value) <= SyncTolerance * syncAltitude.Value)
                return "SYNC";
            return Math.Round(altitude / 1000.0).ToString("F0", CultureInfo.InvariantCulture) + "K";
        }

        public static string SatelliteName(string body, string callsign, int index, int count,
                                           string orbitTag, double inclinationDeg)
        {
            string name = $"{body} {callsign} {index}/{count} - {orbitTag}";
            return Math.Abs(inclinationDeg) >= MinInclinationDeg
                ? name + " i" + Math.Round(inclinationDeg).ToString("F0", CultureInfo.InvariantCulture)
                : name;
        }

        private static string Roman(int n)
        {
            var map = new[] { (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
            var result = "";
            foreach (var (value, symbol) in map)
                while (n >= value) { result += symbol; n -= value; }
            return result;
        }
    }
}

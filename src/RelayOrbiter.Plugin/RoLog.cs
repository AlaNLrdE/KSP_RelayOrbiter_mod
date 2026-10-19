using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Debug = UnityEngine.Debug;

namespace RelayOrbiter.Plugin
{
    // Relay Orbiter's log lines in KSP.log: "[RelayOrbiter]" for what the player follows,
    // "[RelayOrbiter][DBG]" for diagnostics (every decision, with the numbers behind it),
    // so `grep "\[RelayOrbiter\]\[DBG\]"` or `grep -v` separates them. Debug lines exist
    // only in a Debug build (deploy.sh -D): in Release the compiler drops every Dbg call,
    // its message included.
    internal static class RoLog
    {
        public static void Info(string text) => Debug.Log("[RelayOrbiter] " + text);

        [Conditional("DEBUG")]
        public static void Dbg(string text) => Debug.Log("[RelayOrbiter][DBG] " + text);

        public static string Km(double metres) => (metres / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " km";

        // An orbit, as KSP sees it now: altitudes above the body's surface.
        public static string Describe(Orbit? o)
        {
            if (o == null) return "no orbit";
            var b = o.referenceBody;
            string ap = o.eccentricity < 1.0 ? Km(o.ApA) : "hyperbolic";
            return $"{b.bodyName}: Pe {Km(o.PeA)}, Ap {ap}, i {o.inclination:F2}°, e {o.eccentricity:F4}, " +
                   $"Ω {o.LAN:F1}°, ω {o.argumentOfPeriapsis:F1}°, SOI {Km(b.sphereOfInfluence)}, " +
                   $"patch {o.patchStartTransition}→{o.patchEndTransition}";
        }

        // A predicted patch around a destination, for encounter decisions.
        public static string Patch(Orbit? p) =>
            p == null ? "no encounter"
                      : $"Pe {Km(p.PeA)}, i {p.inclination:F2}°, e {p.eccentricity:F4}, Ω {p.LAN:F1}°, " +
                        $"enters SOI at UT {p.StartUT:F0}";

        // The vessel's propulsion state.
        public static string Propulsion(Vessel v)
        {
            var (thrust, isp) = VesselParts.ActiveEngines(v);
            var engines = VesselParts.EngineStates(v);
            return $"stage {VesselParts.CurrentStage(v)}, mass {v.GetTotalMass():F2} t, thrust {thrust:F0} kN, " +
                   $"Isp {isp:F0} s, engines {engines.Count(e => e.Value == Core.EngineState.Running)} running / " +
                   $"{engines.Count(e => e.Value == Core.EngineState.Spent)} spent / " +
                   $"{engines.Count(e => e.Value == Core.EngineState.Unlit)} unlit";
        }
    }
}

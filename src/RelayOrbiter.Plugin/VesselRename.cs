using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Renames a vessel for good. KSP keeps the name in three places: the vessel, its
    // ProtoVessel (what is saved while it is unloaded) and the parts' vessel naming
    // (which can give the old name back, e.g. a probe core with a naming priority).
    internal static class VesselRename
    {
        public static void Apply(Vessel vessel, string name, VesselType type = VesselType.Relay)
        {
            string old = vessel.vesselName;
            vessel.vesselName = name;
            vessel.vesselType = type;
            if (vessel.loaded)
                foreach (var p in vessel.parts)
                    if (p.vesselNaming != null) p.vesselNaming.vesselName = name;
            var proto = vessel.protoVessel;
            if (proto != null)
            {
                proto.vesselName = name;
                proto.vesselType = type;
                foreach (var s in proto.protoPartSnapshots)
                    if (s.vesselNaming != null) s.vesselNaming.vesselName = name;
            }
            GameEvents.onVesselRename.Fire(new GameEvents.HostedFromToAction<Vessel, string>(vessel, old, name));
            Debug.Log($"[RelayOrbiter] Renamed {old} to {name} ({type.ToString().ToLowerInvariant()}).");
        }
    }
}

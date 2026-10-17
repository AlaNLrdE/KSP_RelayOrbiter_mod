using System.Collections.Generic;

namespace RelayOrbiter.Core
{
    // (ISP) Read-only access to celestial body data.
    public interface IBodyDatabase
    {
        CelestialBody? Find(string name);
        IReadOnlyList<CelestialBody> All();
        // Bodies orbiting parentName, sorted by orbital radius ascending.
        IReadOnlyList<CelestialBody> ChildrenOf(string parentName);
    }
}

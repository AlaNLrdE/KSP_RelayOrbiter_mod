using System.Collections.Generic;

namespace RelayOrbiter.Core
{
    // (ISP) Offers and validates target altitudes for a body; no UI knowledge.
    public interface IAltitudePlanner
    {
        AltitudeStatus Validate(CelestialBody body, double altitude);

        AltitudeOption Synchronous(CelestialBody body);

        // One option per child at its orbital altitude, sorted by orbital radius.
        IReadOnlyList<AltitudeOption> Children(CelestialBody body);

        // Halfway between the orbits of two children of body.
        AltitudeOption Midpoint(CelestialBody body, CelestialBody childA, CelestialBody childB);

        AltitudeOption Custom(CelestialBody body, double altitude);
    }
}

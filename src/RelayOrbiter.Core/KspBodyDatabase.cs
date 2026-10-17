using System.Collections.Generic;

namespace RelayOrbiter.Core
{
    // Hardcodes all 17 stock KSP bodies.
    public sealed class KspBodyDatabase : IBodyDatabase
    {
        // STUB — table intentionally empty until implementation step.
        private static readonly CelestialBody[] Bodies = new CelestialBody[0];

        public CelestialBody? Find(string name) => null;

        public IReadOnlyList<CelestialBody> All() => Bodies;

        public IReadOnlyList<CelestialBody> ChildrenOf(string parentName) => new CelestialBody[0];
    }
}

using System;

namespace RelayOrbiter.Core
{
    // (DIP) Depends on IOrbitalMechanics, not the concrete class.
    public sealed class ConstellationCalculator : IConstellationCalculator
    {
        private readonly IOrbitalMechanics _mechanics;

        public ConstellationCalculator(IOrbitalMechanics mechanics)
        {
            _mechanics = mechanics ?? throw new ArgumentNullException(nameof(mechanics));
        }

        // STUB — returns a default OrbitalResult (all zeros / false).
        public OrbitalResult Calculate(CelestialBody body, double hTarget) => new OrbitalResult();
    }
}

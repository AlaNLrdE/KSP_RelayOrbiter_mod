using System;
using System.Collections.Generic;

namespace RelayOrbiter.Core
{
    // (DIP) Depends on the body database and mechanics interfaces.
    public sealed class AltitudePlanner : IAltitudePlanner
    {
        private readonly IBodyDatabase     _db;
        private readonly IOrbitalMechanics _mechanics;

        public AltitudePlanner(IBodyDatabase db, IOrbitalMechanics mechanics)
        {
            _db        = db        ?? throw new ArgumentNullException(nameof(db));
            _mechanics = mechanics ?? throw new ArgumentNullException(nameof(mechanics));
        }

        public AltitudeStatus Validate(CelestialBody body, double altitude)
        {
            if (altitude <= 0.0)     return AltitudeStatus.BelowSurface;
            if (altitude < body.Atm) return AltitudeStatus.InsideAtmosphere;
            double? soiMax = _mechanics.SoiMaxAltitude(body);
            if (soiMax.HasValue && altitude > soiMax.Value) return AltitudeStatus.ExceedsSoi;
            return AltitudeStatus.Ok;
        }

        public AltitudeOption Synchronous(CelestialBody body)
        {
            double h = _mechanics.SyncOrbitAltitude(body) ?? 0.0;
            return Option(AltitudeSource.Synchronous, "", body, h);
        }

        public IReadOnlyList<AltitudeOption> Children(CelestialBody body)
        {
            var kids = _db.ChildrenOf(body.Name);
            var options = new List<AltitudeOption>(kids.Count);
            foreach (var kid in kids)
                options.Add(Option(AltitudeSource.Child, kid.Name, body,
                                   OrbitalRadius(kid) - body.Radius));
            return options;
        }

        public AltitudeOption Midpoint(CelestialBody body, CelestialBody childA, CelestialBody childB)
        {
            double rMid = (OrbitalRadius(childA) + OrbitalRadius(childB)) / 2.0;
            return Option(AltitudeSource.Midpoint, childA.Name + "+" + childB.Name, body,
                          rMid - body.Radius);
        }

        public AltitudeOption Custom(CelestialBody body, double altitude) =>
            Option(AltitudeSource.Custom, "", body, altitude);

        private AltitudeOption Option(AltitudeSource source, string name, CelestialBody body, double h) =>
            new AltitudeOption(source, name, h, Validate(body, h));

        private static double OrbitalRadius(CelestialBody child) =>
            child.OrbitalRadius ?? throw new ArgumentException(
                $"{child.Name} has no orbital radius (is it the star?)", nameof(child));
    }
}

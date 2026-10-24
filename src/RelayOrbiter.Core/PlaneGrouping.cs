using System;
using System.Collections.Generic;
using System.Linq;

namespace RelayOrbiter.Core
{
    // An orbit's plane, from its inclination and longitude of the ascending node (degrees).
    public sealed class OrbitPlane
    {
        public OrbitPlane(double inclinationDeg, double lanDeg)
        {
            InclinationDeg = inclinationDeg;
            LanDeg         = lanDeg;
        }

        public double InclinationDeg { get; }
        public double LanDeg         { get; }

        // Unit normal (angular momentum direction). Equatorial orbits have no defined
        // node, but their normal does not depend on it.
        public (double x, double y, double z) Normal
        {
            get
            {
                double i = InclinationDeg * Math.PI / 180.0, o = LanDeg * Math.PI / 180.0;
                return (Math.Sin(i) * Math.Sin(o), -Math.Sin(i) * Math.Cos(o), Math.Cos(i));
            }
        }
    }

    // Relays that share an orbital plane, grouped into one constellation with names
    // of our scheme.
    public static class PlaneGrouping
    {
        public const double DefaultToleranceDeg = 5.0;

        // Angle between two planes (0..180): 0 same plane and direction, 180 same
        // plane but opposite directions.
        public static double RelativeInclinationDeg(OrbitPlane a, OrbitPlane b)
        {
            var (ax, ay, az) = a.Normal;
            var (bx, by, bz) = b.Normal;
            // atan2(|a×b|, a·b): accurate for small angles too, unlike acos.
            double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
            return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), ax * bx + ay * by + az * bz) * 180.0 / Math.PI;
        }

        // The planes within toleranceDeg of each other, grown from the seed: the
        // closest to the seed first, each one only when it is within the tolerance of
        // every plane already in. (Within the tolerance of the seed alone is not enough:
        // two planes 45° from it on opposite sides are 90° apart.)
        public static IReadOnlyList<int> SamePlane(IReadOnlyList<OrbitPlane> planes, int seed, double toleranceDeg)
        {
            var group = new List<int> { seed };
            foreach (int i in Enumerable.Range(0, planes.Count)
                                        .Where(i => i != seed)
                                        .OrderBy(i => RelativeInclinationDeg(planes[seed], planes[i])))
                if (Fits(planes, group, i, toleranceDeg)) group.Add(i);
            group.Sort();
            return group;
        }

        // Plane i is within the tolerance of every plane of the group.
        public static bool Fits(IReadOnlyList<OrbitPlane> planes, IEnumerable<int> group, int i, double toleranceDeg) =>
            group.All(j => RelativeInclinationDeg(planes[i], planes[j]) <= toleranceDeg);

        // The largest angle between two of the planes.
        public static double Spread(IReadOnlyList<OrbitPlane> planes) =>
            planes.Count < 2 ? 0.0
            : planes.Max(a => planes.Max(b => RelativeInclinationDeg(a, b)));

        // The members' current callsign when they already share one of ours,
        // otherwise the first one free around the body.
        public static string ChooseCallsign(string body, IReadOnlyList<string> memberNames,
                                            IEnumerable<string> otherNames)
        {
            var parsed = memberNames.Select(ConstellationName.Parse).ToList();
            if (parsed.All(p => p != null && p.Body == body) &&
                parsed.Select(p => p!.Callsign).Distinct().Count() == 1)
                return parsed[0]!.Callsign;
            return ConstellationNaming.Callsign(body, otherNames);
        }

        // New names, in the input order: the seed is 1/N and the numbers follow the
        // direction of motion (increasing mean longitude).
        public static IReadOnlyList<string> Names(string body, string callsign, IReadOnlyList<double> longitudesDeg,
                                                  int seed, string orbitTag, double inclinationDeg)
        {
            int n = longitudesDeg.Count;
            var order = Enumerable.Range(0, n)
                .OrderBy(i => StationKeeping.Mod360(longitudesDeg[i] - longitudesDeg[seed]))
                .ToList();
            var names = new string[n];
            for (int k = 0; k < n; ++k)
                names[order[k]] = ConstellationNaming.SatelliteName(body, callsign, k + 1, n, orbitTag, inclinationDeg);
            return names;
        }

        // The plane closest to all of them: the mean of their normals.
        public static OrbitPlane MeanPlane(IReadOnlyList<OrbitPlane> planes)
        {
            double x = 0.0, y = 0.0, z = 0.0;
            foreach (var p in planes)
            {
                var (nx, ny, nz) = p.Normal;
                x += nx; y += ny; z += nz;
            }
            double n = Math.Sqrt(x * x + y * y + z * z);
            if (n == 0.0) return planes[0];
            x /= n; y /= n; z /= n;
            double inc = Math.Acos(Math.Max(-1.0, Math.Min(1.0, z))) * 180.0 / Math.PI;
            // Normal = (sin i sin Ω, −sin i cos Ω, cos i).
            double lan = Math.Sqrt(x * x + y * y) < 1e-12 ? 0.0 : StationKeeping.Mod360(Math.Atan2(x, -y) * 180.0 / Math.PI);
            return new OrbitPlane(inc, lan);
        }

        // Δv to rotate an orbit's plane by angleDeg at a speed: 2v·sin(θ/2).
        public static double PlaneChangeDv(double speed, double angleDeg) =>
            2.0 * speed * Math.Sin(angleDeg * Math.PI / 360.0);

        // Mean inclination of the group, for the name tag.
        public static double MeanInclinationDeg(IEnumerable<OrbitPlane> planes) =>
            planes.Select(p => p.InclinationDeg).DefaultIfEmpty(0.0).Average();
    }
}

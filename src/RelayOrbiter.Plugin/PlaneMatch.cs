using System;
using RelayOrbiter.Core;

namespace RelayOrbiter.Plugin
{
    // Rotating an orbit into another plane (given by inclination and node), at the
    // line of nodes between the two. Normals are taken in the Orbit class's own frame
    // (position × velocity), for both planes alike, so only angles and signs of dot
    // products are used: they don't depend on how that frame is oriented.
    internal static class PlaneMatch
    {
        public static Vector3d Normal(Orbit o, double ut) =>
            Vector3d.Cross(o.getRelativePositionAtUT(ut), o.getOrbitalVelocityAtUT(ut)).normalized;

        // The plane's normal: from a circular orbit in that plane around the same body.
        public static Vector3d TargetNormal(OrbitPlane plane, Orbit o, double ut) =>
            Normal(new Orbit(plane.InclinationDeg, 0.0, o.semiMajorAxis, plane.LanDeg, 0.0, 0.0, ut, o.referenceBody), ut);

        public static double AngleDeg(Orbit o, OrbitPlane plane, double ut) =>
            Vector3d.Angle(Normal(o, ut), TargetNormal(plane, o, ut));

        // Next time after `from` the orbit crosses the plane of normal n2 (the line of
        // nodes between the two planes): where position · normal changes sign.
        public static double NextCrossing(Orbit o, Vector3d n2, double from)
        {
            const int steps = 90;
            double F(double t) => Vector3d.Dot(o.getRelativePositionAtUT(t), n2);
            double dt = o.period / steps, t0 = from, f0 = F(t0);
            for (int k = 1; k <= steps; ++k)
            {
                double t1 = from + k * dt, f1 = F(t1);
                if (Math.Sign(f1) != Math.Sign(f0))
                {
                    for (int j = 0; j < 50; ++j)
                    {
                        double tm = (t0 + t1) / 2.0, fm = F(tm);
                        if (Math.Sign(fm) == Math.Sign(f0)) { t0 = tm; f0 = fm; } else t1 = tm;
                    }
                    return (t0 + t1) / 2.0;
                }
                t0 = t1; f0 = f1;
            }
            return from;  // already in the plane: any time will do
        }

        // Next time after `from` the orbit's position points along dir (within one
        // period): a scan, then a golden-section refinement.
        public static double NextAlignment(Orbit o, Vector3d dir, double from)
        {
            const int steps = 180;
            double Off(double t) => Vector3d.Angle(o.getRelativePositionAtUT(t), dir);
            double dt = o.period / steps, bestT = from, best = double.MaxValue;
            for (int k = 0; k < steps; ++k)
            {
                double t = from + k * dt, a = Off(t);
                if (a < best) { best = a; bestT = t; }
            }
            double lo = Math.Max(from, bestT - dt), hi = bestT + dt;
            const double g = 0.6180339887498949;
            for (int i = 0; i < 40; ++i)
            {
                double c = hi - g * (hi - lo), d = lo + g * (hi - lo);
                if (Off(c) < Off(d)) hi = d; else lo = c;
            }
            return (lo + hi) / 2.0;
        }

        // (prograde, normal) Δv rotating the velocity by angleDeg at speed; the normal
        // sign is settled by CheckSign.
        public static (double prograde, double normal) DeltaV(double speed, double angleDeg)
        {
            double rad = angleDeg * Math.PI / 180.0;
            return (speed * (Math.Cos(rad) - 1.0), speed * Math.Sin(rad));
        }

        // The normal component's sign depends on which node this is: check the orbit
        // KSP predicts after the node and keep the sign that ends closer to the plane.
        public static void CheckSign(PatchedConicSolver solver, ManeuverNode node, double ut, double prograde,
                                     double normal, OrbitPlane plane, Action<string> log)
        {
            double Off()
            {
                solver.UpdateFlightPlan();
                var next = node.nextPatch;
                return next == null ? double.NaN
                    : PlaneGrouping.RelativeInclinationDeg(new OrbitPlane(next.inclination, next.LAN), plane);
            }
            double plus = Off();
            node.OnGizmoUpdated(new Vector3d(0.0, -normal, prograde), ut);
            double minus = Off();
            if (double.IsNaN(minus) || plus <= minus)
            {
                node.OnGizmoUpdated(new Vector3d(0.0, normal, prograde), ut);
                solver.UpdateFlightPlan();
            }
            log($"Plane change: {Math.Min(plus, double.IsNaN(minus) ? plus : minus):F3}° from the target plane " +
                $"after the node (normal +: {plus:F3}°, −: {minus:F3}°).");
        }
    }
}

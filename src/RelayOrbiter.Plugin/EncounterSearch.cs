using System;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Fine-tunes a maneuver node against KSP's own patched-conic prediction, so the
    // trajectory meets the destination moon with the periapsis wanted, in a prograde
    // orbit. Every evaluation moves the node and asks the solver for a new flight plan.
    internal sealed class EncounterSearch
    {
        private readonly PatchedConicSolver _solver;
        private readonly ManeuverNode _node;
        private readonly CelestialBody _moon;
        private readonly double _targetPe, _minPe;

        public EncounterSearch(PatchedConicSolver solver, ManeuverNode node, CelestialBody moon,
                               double targetPeriapsis, double minPeriapsis)
        {
            _solver   = solver;
            _node     = node;
            _moon     = moon;
            _targetPe = targetPeriapsis;
            _minPe    = minPeriapsis;
        }

        public int Evaluations { get; private set; }

        // The first predicted patch around the moon after `start`, or null.
        public static Orbit? PatchAround(Orbit? start, CelestialBody moon)
        {
            for (int guard = 0; start != null && guard < 12; ++guard, start = start.nextPatch)
                if (start.referenceBody == moon) return start;
            return null;
        }

        // 0 is perfect: the periapsis error relative to the target, plus penalties for a
        // retrograde orbit, an impact and no encounter at all (the worst).
        public static double Cost(Orbit? patch, double targetPe, double minPe)
        {
            if (patch == null) return 1e6;
            double cost = Math.Abs(patch.PeR - targetPe) / targetPe;
            if (patch.inclination > 90.0) cost += 10.0;
            if (patch.PeR < minPe) cost += 100.0 + (minPe - patch.PeR) / minPe;
            return cost;
        }

        public double Evaluate(double ut, Vector3d dv)
        {
            Evaluations++;
            _node.OnGizmoUpdated(dv, ut);  // x radial, y normal, z prograde
            _solver.UpdateFlightPlan();
            return Cost(PatchAround(_node.nextPatch, _moon), _targetPe, _minPe);
        }

        public Orbit? Patch => PatchAround(_node.nextPatch, _moon);

        // Best burn time for a fixed Δv: a coarse scan over [ut0 − span, ut0 + span],
        // then a golden-section refinement around the best sample.
        public (double ut, double cost) BestTime(double ut0, double span, Vector3d dv, double earliest)
        {
            const int samples = 48;
            double bestUt = ut0, best = double.MaxValue, step = 2.0 * span / samples;
            for (int k = 0; k <= samples; ++k)
            {
                double ut = ut0 - span + k * step;
                if (ut < earliest) continue;
                double c = Evaluate(ut, dv);
                if (c < best) { best = c; bestUt = ut; }
            }
            if (best >= 1e6) return (bestUt, best);
            var (t, cost) = Golden(Math.Max(earliest, bestUt - step), bestUt + step, x => Evaluate(x, dv));
            return cost < best ? (t, cost) : (bestUt, best);
        }

        // Best correction Δv around `dv`: coordinate search over prograde, radial and
        // normal, coarse then fine.
        public (Vector3d dv, double cost) BestCorrection(double ut, Vector3d dv, double range)
        {
            double best = Evaluate(ut, dv);
            foreach (double r in new[] { range, range / 10.0 })
                for (int pass = 0; pass < 2; ++pass)
                    foreach (int axis in new[] { 2, 0, 1 })  // prograde first: it moves the periapsis most
                    {
                        Vector3d With(double x) { var v = dv; v[axis] = x; return v; }
                        double centre = dv[axis];
                        var (x, c) = Golden(centre - r, centre + r, x => Evaluate(ut, With(x)));
                        if (c < best) { best = c; dv = With(x); }
                    }
            Evaluate(ut, dv);  // leave the node on the best
            return (dv, best);
        }

        private static (double x, double f) Golden(double a, double b, Func<double, double> f)
        {
            const double g = 0.6180339887498949;
            double c = b - g * (b - a), d = a + g * (b - a), fc = f(c), fd = f(d);
            for (int i = 0; i < 30; ++i)
            {
                if (fc < fd) { b = d; d = c; fd = fc; c = b - g * (b - a); fc = f(c); }
                else         { a = c; c = d; fc = fd; d = a + g * (b - a); fd = f(d); }
            }
            return fc < fd ? (c, fc) : (d, fd);
        }
    }
}

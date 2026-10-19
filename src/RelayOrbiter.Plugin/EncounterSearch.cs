using System;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Fine-tunes a maneuver node against KSP's own patched-conic prediction, so the
    // trajectory meets the destination (a moon, or a planet) with the periapsis
    // wanted, in a prograde orbit. Every evaluation moves the node and asks the solver
    // for a new flight plan. Without an encounter the cost is the closest approach,
    // so the search always has a slope to follow (an interplanetary encounter is a
    // very narrow target).
    internal sealed class EncounterSearch
    {
        private readonly PatchedConicSolver _solver;
        private readonly ManeuverNode _node;
        private readonly CelestialBody _moon;
        private readonly double _targetPe, _minPe, _span;
        private readonly double? _goalInc;

        // span: how long after the node to look for the closest approach (about the
        // transfer time). goalInclination: the plan's target inclination around the
        // destination, aimed at as well; null keeps whatever arrival plane.
        public EncounterSearch(PatchedConicSolver solver, ManeuverNode node, CelestialBody moon,
                               double targetPeriapsis, double minPeriapsis, double span, double? goalInclination)
        {
            _goalInc  = goalInclination;
            _solver   = solver;
            _node     = node;
            _moon     = moon;
            _targetPe = targetPeriapsis;
            _minPe    = minPeriapsis;
            _span     = span;
            // Room for departure, cruise and arrival patches after the node (the limit
            // is not public).
            var limit = typeof(PatchedConicSolver).GetField("patchLimit",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (limit != null && limit.GetValue(solver) is int n && n < MinPatches) limit.SetValue(solver, MinPatches);
        }

        public int Evaluations { get; private set; }

        // The first predicted patch around the moon after `start`, or null.
        public static Orbit? PatchAround(Orbit? start, CelestialBody moon)
        {
            for (int guard = 0; start != null && guard < 12; ++guard, start = start.nextPatch)
                if (start.referenceBody == moon) return start;
            return null;
        }

        public const double NoEncounter = 10.0;  // costs from here up: no encounter
        private const int MinPatches = 6;

        // EncounterCost's bands for an encounter; no encounter: 10 plus the closest
        // approach in SOI radii.
        public static double Cost(Orbit? patch, double targetPe, double minPe, double? goalInc) =>
            patch == null ? 1e6 : RelayOrbiter.Core.EncounterCost.Of(patch.PeR, patch.inclination, targetPe, minPe, goalInc);

        // The cost of a trajectory starting at `start` (a node's next patch, or the
        // vessel's orbit).
        public static double Cost(Orbit? start, CelestialBody moon, double targetPe, double minPe, double span,
                                  double? goalInc)
        {
            var patch = PatchAround(start, moon);
            if (patch != null) return Cost(patch, targetPe, minPe, goalInc);
            double d = ClosestApproach(start, moon, span);
            return double.IsNaN(d) ? 1e6 : NoEncounter + d / moon.sphereOfInfluence;
        }

        // Closest distance to the target along the patches that orbit its parent,
        // within `span` seconds of each patch's start.
        public static double ClosestApproach(Orbit? start, CelestialBody target, double span)
        {
            var parent = target.referenceBody;
            double best = double.NaN;
            for (int guard = 0; start != null && guard < 12; ++guard, start = start.nextPatch)
            {
                if (start.referenceBody != parent) continue;
                double t0 = start.StartUT;
                double t1 = start.patchEndTransition == Orbit.PatchTransitionType.FINAL || double.IsInfinity(start.EndUT)
                    ? t0 + span : Math.Min(start.EndUT, t0 + span);
                if (!(t1 > t0)) continue;
                var patch = start;
                double D(double t) => (patch.getPositionAtUT(t) - target.orbit.getPositionAtUT(t)).magnitude;
                const int samples = 120;
                double step = (t1 - t0) / samples, bestT = t0, bestD = double.MaxValue;
                for (int k = 0; k <= samples; ++k)
                {
                    double t = t0 + k * step, d = D(t);
                    if (d < bestD) { bestD = d; bestT = t; }
                }
                var (_, refined) = Golden(Math.Max(t0, bestT - step), Math.Min(t1, bestT + step), D, 20);
                double dMin = Math.Min(bestD, refined);
                if (double.IsNaN(best) || dMin < best) best = dMin;
            }
            return best;
        }

        private double _bestLogged = double.MaxValue;

        public double Evaluate(double ut, Vector3d dv)
        {
            Evaluations++;
            _node.OnGizmoUpdated(dv, ut);  // x radial, y normal, z prograde
            _solver.UpdateFlightPlan();
            double cost = Cost(_node.nextPatch, _moon, _targetPe, _minPe, _span, _goalInc);
            // Each clear improvement (2% or more), not every evaluation (hundreds) nor every
            // fourth-decimal gain: how the search converged.
            if (cost < _bestLogged - Math.Max(1e-4, 0.02 * _bestLogged))
            {
                _bestLogged = cost;
                RoLog.Dbg($"Encounter search #{Evaluations}: UT {ut:F0}, Δv (radial {dv.x:F2}, normal {dv.y:F2}, " +
                          $"prograde {dv.z:F2}) → cost {cost:F4}, arrival {RoLog.Patch(Patch)}.");
            }
            return cost;
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
            if (best >= 1e6) return (bestUt, best);  // no trajectory at all
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

        private static (double x, double f) Golden(double a, double b, Func<double, double> f, int iterations = 30)
        {
            const double g = 0.6180339887498949;
            double c = b - g * (b - a), d = a + g * (b - a), fc = f(c), fd = f(d);
            for (int i = 0; i < iterations; ++i)
            {
                if (fc < fd) { b = d; d = c; fd = fc; c = b - g * (b - a); fc = f(c); }
                else         { a = c; c = d; fc = fd; d = a + g * (b - a); fd = f(d); }
            }
            return fc < fd ? (c, fc) : (d, fd);
        }
    }
}

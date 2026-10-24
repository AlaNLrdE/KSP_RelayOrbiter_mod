using System;
using System.Collections.Generic;
using System.Linq;

namespace RelayOrbiter.Core
{
    // A satellite as the shape fit sees it: its plane and its mean argument of
    // latitude (ω + M, degrees: where it is along its own plane, uniformly moving).
    public sealed class ShapeMember
    {
        public ShapeMember(double inclinationDeg, double lanDeg, double argLatDeg)
        {
            Plane     = new OrbitPlane(inclinationDeg, lanDeg);
            ArgLatDeg = argLatDeg;
        }

        public OrbitPlane Plane     { get; }
        public double     ArgLatDeg { get; }
    }

    // Which slot each satellite takes, with the constellation's reference node and
    // phase, and how far each satellite is from its slot.
    public sealed class ShapeAssignment
    {
        public ShapeAssignment(IReadOnlyList<SlotOrbit> slots, double lan0Deg, double theta0Deg, int[] slotOf,
                               double[] phaseErrorsDeg, double[] planeErrorsDeg)
        {
            Slots          = slots;
            Lan0Deg        = lan0Deg;
            Theta0Deg      = theta0Deg;
            SlotOf         = slotOf;
            PhaseErrorsDeg = phaseErrorsDeg;
            PlaneErrorsDeg = planeErrorsDeg;
        }

        public IReadOnlyList<SlotOrbit> Slots { get; }
        public double Lan0Deg   { get; }
        public double Theta0Deg { get; }
        public int[]  SlotOf    { get; }
        public double[] PhaseErrorsDeg { get; }  // −180..180, positive = ahead
        public double[] PlaneErrorsDeg { get; }  // angle to the slot's plane

        public SlotOrbit SlotFor(int member) => Slots[SlotOf[member]];
        public OrbitPlane PlaneFor(int member) => PlaneOf(SlotFor(member), Lan0Deg);
        public static OrbitPlane PlaneOf(SlotOrbit slot, double lan0) =>
            new OrbitPlane(slot.InclinationDeg, StationKeeping.Mod360(lan0 + slot.LanOffsetDeg));
    }

    public static class ShapeFit
    {
        // Fit the shape's slots to the satellites: first the reference node Ω0 (every
        // satellite into the nearest plane with room), then the reference phase θ0
        // (every satellite into the nearest free slot of its plane). reference: that
        // satellite defines both. fixedLan0/fixedTheta0: imposed (a moon's, for Trojans).
        // exclude: the satellite being corrected doesn't pull the fit.
        public static ShapeAssignment Fit(ShapeSpec spec, IReadOnlyList<ShapeMember> members, int? reference = null,
                                          int? exclude = null, double? fixedLan0 = null, double? fixedTheta0 = null)
        {
            var slots = ConstellationShapes.Slots(spec);
            int m = members.Count;
            if (m == 0) throw new ArgumentException("No satellites.", nameof(members));
            if (m > slots.Count) throw new ArgumentException($"{m} satellites for {slots.Count} slots.", nameof(members));
            var planeOffsets = slots.GroupBy(s => s.Plane).Select(g => g.First()).ToList();
            bool Counts(int i) => i != exclude || m == 1;

            // ── Reference node ──
            var lanCandidates = new List<double>();
            if (fixedLan0.HasValue) lanCandidates.Add(fixedLan0.Value);
            else
                foreach (int i in Enumerable.Range(0, m).Where(i => reference.HasValue ? i == reference : Counts(i)))
                    foreach (var p in planeOffsets)
                        lanCandidates.Add(StationKeeping.Mod360(members[i].Plane.LanDeg - p.LanOffsetDeg));
            if (lanCandidates.Count == 0) lanCandidates.Add(0.0);
            lanCandidates = Distinct(lanCandidates);  // satellites sharing a plane give the same candidates

            int[]? bestPlanes = null;
            double bestLan = 0.0, bestPlaneCost = double.MaxValue;
            foreach (double lan0 in lanCandidates)
            {
                var (planeOf, cost) = AssignPlanes(spec, planeOffsets, members, lan0, Counts, reference);
                if (cost < bestPlaneCost - 1e-9) { bestPlaneCost = cost; bestLan = lan0; bestPlanes = planeOf; }
            }
            var planesOf = bestPlanes!;

            // ── Reference phase ──
            var thetaCandidates = new List<double>();
            if (fixedTheta0.HasValue) thetaCandidates.Add(fixedTheta0.Value);
            else
                foreach (int i in Enumerable.Range(0, m).Where(i => reference.HasValue ? i == reference : Counts(i)))
                    foreach (var s in slots.Where(s => s.Plane == planesOf[i]))
                        thetaCandidates.Add(StationKeeping.Mod360(members[i].ArgLatDeg - s.PhaseOffsetDeg));
            if (thetaCandidates.Count == 0) thetaCandidates.Add(0.0);
            thetaCandidates = Distinct(thetaCandidates);

            int[]? bestSlots = null;
            double bestTheta = 0.0, bestPhaseCost = double.MaxValue;
            foreach (double theta in thetaCandidates)
            {
                var (slotOf, cost) = AssignSlots(slots, members, planesOf, theta, Counts, reference);
                if (cost < bestPhaseCost - 1e-9) { bestPhaseCost = cost; bestTheta = theta; bestSlots = slotOf; }
            }
            var slotsOf = bestSlots!;
            // Best fit: the circular mean of where each satellite says θ0 is.
            if (!fixedTheta0.HasValue && !reference.HasValue)
            {
                double x = 0.0, y = 0.0;
                for (int i = 0; i < m; ++i)
                {
                    if (!Counts(i)) continue;
                    double t = (members[i].ArgLatDeg - slots[slotsOf[i]].PhaseOffsetDeg) * Math.PI / 180.0;
                    x += Math.Cos(t); y += Math.Sin(t);
                }
                if (x != 0.0 || y != 0.0) bestTheta = StationKeeping.Mod360(Math.Atan2(y, x) * 180.0 / Math.PI);
            }

            var phaseErrors = new double[m];
            var planeErrors = new double[m];
            for (int i = 0; i < m; ++i)
            {
                var slot = slots[slotsOf[i]];
                phaseErrors[i] = StationKeeping.Wrap180(members[i].ArgLatDeg - bestTheta - slot.PhaseOffsetDeg);
                planeErrors[i] = PlaneGrouping.RelativeInclinationDeg(members[i].Plane,
                                                                      ShapeAssignment.PlaneOf(slot, bestLan));
            }
            return new ShapeAssignment(slots, bestLan, bestTheta, slotsOf, phaseErrors, planeErrors);
        }

        // Candidates closer than this are the same one: with many satellites in few planes
        // most candidates repeat, and each one costs a full assignment.
        private const double CandidateStepDeg = 0.05;

        private static List<double> Distinct(List<double> candidates) =>
            candidates.GroupBy(c => (long)Math.Round(StationKeeping.Mod360(c) / CandidateStepDeg) %
                                    (long)Math.Round(360.0 / CandidateStepDeg))
                      .Select(g => g.First()).ToList();

        // Every satellite into the nearest plane that still has room, nearest pairs first.
        private static (int[] planeOf, double cost) AssignPlanes(ShapeSpec spec, List<SlotOrbit> planes,
                                                                IReadOnlyList<ShapeMember> members, double lan0,
                                                                Func<int, bool> counts, int? first)
        {
            int m = members.Count;
            var pairs = new List<(double angle, int member, int plane)>();
            for (int i = 0; i < m; ++i)
                foreach (var p in planes)
                    pairs.Add((PlaneGrouping.RelativeInclinationDeg(members[i].Plane, ShapeAssignment.PlaneOf(p, lan0)),
                               i, p.Plane));
            var planeOf = Enumerable.Repeat(-1, m).ToArray();
            var room = planes.ToDictionary(p => p.Plane, p => spec.InPlane(p.Plane));
            double cost = 0.0;
            // The reference goes first: it picks its own plane.
            foreach (var (angle, i, p) in pairs.OrderBy(x => x.member == first ? 0 : 1).ThenBy(x => x.angle))
            {
                if (planeOf[i] >= 0 || room[p] == 0) continue;
                planeOf[i] = p;
                room[p]--;
                if (counts(i)) cost += angle * angle;
            }
            return (planeOf, cost);
        }

        // Within each plane, every satellite into the nearest free slot, nearest first.
        private static (int[] slotOf, double cost) AssignSlots(IReadOnlyList<SlotOrbit> slots,
                                                              IReadOnlyList<ShapeMember> members, int[] planeOf,
                                                              double theta0, Func<int, bool> counts, int? first)
        {
            int m = members.Count;
            var pairs = new List<(double err, int member, int slot)>();
            for (int i = 0; i < m; ++i)
                foreach (var s in slots.Where(s => s.Plane == planeOf[i]))
                    pairs.Add((Math.Abs(StationKeeping.Wrap180(members[i].ArgLatDeg - theta0 - s.PhaseOffsetDeg)), i, s.Index));
            var slotOf = Enumerable.Repeat(-1, m).ToArray();
            var taken = new HashSet<int>();
            double cost = 0.0;
            foreach (var (err, i, s) in pairs.OrderBy(x => x.member == first ? 0 : 1).ThenBy(x => x.err))
            {
                if (slotOf[i] >= 0 || taken.Contains(s)) continue;
                slotOf[i] = s;
                taken.Add(s);
                if (counts(i)) cost += err * err;
            }
            return (slotOf, cost);
        }
    }
}

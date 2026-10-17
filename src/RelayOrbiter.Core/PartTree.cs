using System;
using System.Collections.Generic;
using System.Linq;

namespace RelayOrbiter.Core
{
    // Finds releasable satellites in a vessel and checks a chosen release order.
    public sealed class PartTree
    {
        public const int Satellites = 3;

        private readonly List<PartInfo> _parts;
        private readonly Dictionary<uint, PartInfo> _byId;
        private readonly Dictionary<uint, List<uint>> _children;

        public PartTree(IEnumerable<PartInfo> parts)
        {
            _parts    = parts.ToList();
            _byId     = _parts.ToDictionary(p => p.Id);
            _children = _parts.Where(p => p.ParentId.HasValue)
                              .GroupBy(p => p.ParentId!.Value)
                              .ToDictionary(g => g.Key, g => g.Select(p => p.Id).ToList());
        }

        // Every separator whose detached side contains a command part.
        public IReadOnlyList<SatelliteCandidate> Candidates()
        {
            var result = new List<SatelliteCandidate>();
            foreach (var p in _parts)
            {
                var detached = DetachedSide(p);
                if (detached == null) continue;
                var command = detached.Select(id => _byId[id]).FirstOrDefault(x => x.IsCommand);
                if (command == null) continue;
                // Both ports of a pair joined in the editor release the same satellite.
                if (result.Any(c => c.PartIds.Count == detached.Count && !c.PartIds.Except(detached).Any()))
                    continue;
                result.Add(new SatelliteCandidate(p.Id, p.Title, command.Title, detached));
            }
            return result;
        }

        public ReleasePlan PlanReleases(IReadOnlyList<SatelliteCandidate> order)
        {
            if (order.Count != Satellites)
                return Fail($"Pick exactly {Satellites} satellites.");
            if (order.Select(c => c.SeparatorId).Distinct().Count() != order.Count)
                return Fail("Each satellite can be picked only once.");

            for (int i = 0; i < order.Count; ++i)
            for (int j = i + 1; j < order.Count; ++j)
                if (order[i].PartIds.Contains(order[j].SeparatorId))
                    return Fail($"Release {order[j].Name} before {order[i].Name}: it is mounted on it.");

            // What each release detaches, given the earlier releases.
            var released = new HashSet<uint>();
            var stats = new List<SatelliteStats>();
            for (int k = 0; k < order.Count; ++k)
            {
                var detached = order[k].PartIds.Where(id => !released.Contains(id)).ToList();
                released.UnionWith(detached);
                var (thrust, isp) = Propulsion(detached.Select(id => _byId[id]));
                if (k > 0 && thrust <= 0.0)
                    return Fail($"SAT-{k + 1} ({order[k].Name}) has no engine to circularize.");
                stats.Add(new SatelliteStats(order[k], Mass(detached), thrust, isp));
            }

            var carrier = _parts.Where(p => !released.Contains(p.Id));
            var (carrierThrust, carrierIsp) = Propulsion(carrier.Where(p => p.EngineActive));
            if (carrierThrust <= 0.0)
                return Fail("The carrier has no active engine: stage its engine first.");

            double massAfterFirst = _parts.Sum(p => p.Mass) - stats[0].Mass;
            return new ReleasePlan(null, stats, massAfterFirst, carrierThrust, carrierIsp);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Part ids that leave the vessel when p fires, or null if p is not a separator.
        private List<uint>? DetachedSide(PartInfo p)
        {
            if (p.Separator == SeparatorKind.None) return null;

            // Decouplers, and docking ports joined in the editor, detach the part on
            // their explosive node / docking face.
            if (p.DecouplesFromParent && p.ParentId.HasValue) return Subtree(p.Id);
            if (p.DecoupledChildId is uint child && _byId.ContainsKey(child)) return Subtree(child);

            // Ports docked in flight: the outer port (whose parent is the other port) leaves.
            if (p.Separator == SeparatorKind.DockingPort && p.ParentId is uint parent &&
                _byId[parent].Separator == SeparatorKind.DockingPort)
                return Subtree(p.Id);

            return null;
        }

        // id and all its descendants, breadth-first (closest parts first).
        private List<uint> Subtree(uint id)
        {
            var result = new List<uint>();
            var queue = new Queue<uint>();
            queue.Enqueue(id);
            while (queue.Count > 0)
            {
                uint cur = queue.Dequeue();
                result.Add(cur);
                if (_children.TryGetValue(cur, out var kids))
                    foreach (var k in kids) queue.Enqueue(k);
            }
            return result;
        }

        private double Mass(IEnumerable<uint> ids) => ids.Sum(id => _byId[id].Mass);

        // Total thrust and thrust-weighted Isp: Isp = ΣF / Σ(F / Isp).
        private static (double thrust, double isp) Propulsion(IEnumerable<PartInfo> parts)
        {
            double f = 0.0, flow = 0.0;
            foreach (var p in parts.Where(x => x.Thrust > 0.0 && x.Isp > 0.0))
            {
                f    += p.Thrust;
                flow += p.Thrust / p.Isp;
            }
            return (f, flow > 0.0 ? f / flow : 0.0);
        }

        private static ReleasePlan Fail(string error) =>
            new ReleasePlan(error, Array.Empty<SatelliteStats>(), 0.0, 0.0, 0.0);
    }
}

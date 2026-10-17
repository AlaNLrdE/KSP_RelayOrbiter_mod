using System;
using System.Collections.Generic;
using System.Linq;

namespace RelayOrbiter.Core
{
    // One resource container of one part.
    public sealed class TankInfo
    {
        public TankInfo(uint partId, string resource, double amount, double max)
        {
            PartId   = partId;
            Resource = resource;
            Amount   = amount;
            Max      = max;
        }

        public uint   PartId   { get; }
        public string Resource { get; }
        public double Amount   { get; }
        public double Max      { get; }
    }

    public sealed class ResourceTransfer
    {
        public ResourceTransfer(uint from, uint to, string resource, double amount)
        {
            From     = from;
            To       = to;
            Resource = resource;
            Amount   = amount;
        }

        public uint   From     { get; }
        public uint   To       { get; }
        public string Resource { get; }
        public double Amount   { get; }
    }

    // Fills the satellites' tanks from the carrier's, taking from the largest tank first.
    public static class ResourceTopUp
    {
        private const double Epsilon = 1e-6;

        public static IReadOnlyList<ResourceTransfer> Plan(IEnumerable<TankInfo> satellite,
                                                           IEnumerable<TankInfo> carrier)
        {
            // Carrier tanks by resource, largest capacity first, with what is left in them.
            var sources = carrier.Where(t => t.Amount > Epsilon)
                                 .GroupBy(t => t.Resource)
                                 .ToDictionary(g => g.Key,
                                               g => g.OrderByDescending(t => t.Max)
                                                     .Select(t => (part: t.PartId, left: t.Amount))
                                                     .ToList());
            var result = new List<ResourceTransfer>();
            foreach (var tank in satellite)
            {
                double need = tank.Max - tank.Amount;
                if (need <= Epsilon || !sources.TryGetValue(tank.Resource, out var list)) continue;
                for (int i = 0; i < list.Count && need > Epsilon; ++i)
                {
                    double take = Math.Min(need, list[i].left);
                    if (take <= Epsilon) continue;
                    result.Add(new ResourceTransfer(list[i].part, tank.PartId, tank.Resource, take));
                    list[i] = (list[i].part, list[i].left - take);
                    need -= take;
                }
            }
            return result;
        }

        public static double Shortfall(IEnumerable<TankInfo> satellite,
                                       IEnumerable<ResourceTransfer> transfers)
        {
            var moved = transfers.ToList();
            return satellite.Sum(t => Math.Max(0.0, t.Max - t.Amount -
                moved.Where(m => m.To == t.PartId && m.Resource == t.Resource).Sum(m => m.Amount)));
        }

        public static double FillFraction(IEnumerable<TankInfo> tanks)
        {
            var list = tanks.ToList();
            double max = list.Sum(t => t.Max);
            return max > Epsilon ? list.Sum(t => t.Amount) / max : 1.0;
        }
    }
}

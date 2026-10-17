using System.Collections.Generic;

namespace RelayOrbiter.Core
{
    // A satellite the carrier can release: the parts a separator would detach.
    public sealed class SatelliteCandidate
    {
        public SatelliteCandidate(uint separatorId, string separatorTitle, string name,
                                  IReadOnlyCollection<uint> partIds)
        {
            SeparatorId    = separatorId;
            SeparatorTitle = separatorTitle;
            Name           = name;
            PartIds        = partIds;
        }

        public uint   SeparatorId    { get; }  // decoupler or docking port to fire
        public string SeparatorTitle { get; }
        public string Name           { get; }  // title of the satellite's command part
        // Every part that detaches when the separator fires (including nested satellites).
        public IReadOnlyCollection<uint> PartIds { get; }
    }

    // What one release actually detaches, given the satellites released before it.
    public sealed class SatelliteStats
    {
        public SatelliteStats(SatelliteCandidate candidate, double mass, double thrust, double isp)
        {
            Candidate = candidate;
            Mass      = mass;
            Thrust    = thrust;
            Isp       = isp;
        }

        public SatelliteCandidate Candidate { get; }
        public double Mass   { get; }  // t
        public double Thrust { get; }  // kN, all of the satellite's engines
        public double Isp    { get; }  // s, thrust-weighted
    }

    public sealed class ReleasePlan
    {
        public ReleasePlan(string? error, IReadOnlyList<SatelliteStats> satellites,
                           double carrierMassAfterFirst, double carrierThrust, double carrierIsp)
        {
            Error                 = error;
            Satellites            = satellites;
            CarrierMassAfterFirst = carrierMassAfterFirst;
            CarrierThrust         = carrierThrust;
            CarrierIsp            = carrierIsp;
        }

        public string? Error { get; }  // null when the selection can be deployed
        public bool    IsValid => Error == null;
        public IReadOnlyList<SatelliteStats> Satellites { get; }
        public double CarrierMassAfterFirst { get; }  // t, for the parking burn
        public double CarrierThrust { get; }          // kN, carrier's active engines
        public double CarrierIsp    { get; }          // s
    }
}

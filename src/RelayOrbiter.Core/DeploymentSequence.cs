using System;
using System.Collections.Generic;

namespace RelayOrbiter.Core
{
    public enum StepKind
    {
        WarpToApoapsis,         // warp until Seconds before the carrier's next apoapsis
        OrientForRelease,       // turn the carrier so Satellite's separation push is perpendicular to the velocity
        Release,                // fire Satellite's separator
        Wait,                   // let the vessels separate for Seconds
        SwitchToSatellite,
        SwitchToCarrier,
        CreateParkingNode,      // carrier: parking-orbit burn at the next apoapsis
        CreateCircularizeNode,  // active vessel: circularize at the next apoapsis
        ExecuteNode,            // fly the active vessel's next node
    }

    public sealed class SequenceStep
    {
        public SequenceStep(StepKind kind, int satellite = -1, double seconds = 0.0)
        {
            Kind      = kind;
            Satellite = satellite;
            Seconds   = seconds;
        }

        public StepKind Kind      { get; }
        public int      Satellite { get; }  // 0-based, -1 when not about a satellite
        public double   Seconds   { get; }  // WarpToApoapsis lead or Wait duration
    }

    // Steps for the full 3-satellite deployment. SAT-1 is released before the
    // carrier's parking burn, while the carrier is still circular, so it needs
    // no burn of its own; SAT-2 and SAT-3 are released one parking orbit apart
    // and circularize at that apoapsis. Before each release the carrier turns so
    // the separation push is perpendicular to the velocity (SeparationAttitude).
    public static class DeploymentSequence
    {
        public const double AlignLead      = 60.0;  // s reserved to point before a burn starts
        public const double SeparationWait = 5.0;   // s between release and switching vessel
        public const double SwitchMargin   = 15.0;  // s for the vessel switch to settle
        public const double ReleaseMargin  = 30.0;  // s spare before SAT-1's release
        public const double OrientTime     = 60.0;  // s reserved to orient before each release

        // carrierBurn: parking burn duration; satelliteBurns: SAT-2 and SAT-3 circularization durations (s).
        public static IReadOnlyList<SequenceStep> Build(double carrierBurn, IReadOnlyList<double> satelliteBurns)
        {
            if (satelliteBurns.Count != 2)
                throw new ArgumentException("Need the circularization burns of SAT-2 and SAT-3.", nameof(satelliteBurns));

            var steps = new List<SequenceStep>
            {
                new SequenceStep(StepKind.WarpToApoapsis,
                                 seconds: carrierBurn / 2 + AlignLead + SeparationWait + ReleaseMargin + OrientTime),
                new SequenceStep(StepKind.OrientForRelease, satellite: 0),
                new SequenceStep(StepKind.Release, satellite: 0),
                new SequenceStep(StepKind.Wait, seconds: SeparationWait),
                new SequenceStep(StepKind.CreateParkingNode),
                new SequenceStep(StepKind.ExecuteNode),
            };

            for (int k = 1; k <= 2; ++k)
            {
                steps.Add(new SequenceStep(StepKind.WarpToApoapsis,
                          seconds: satelliteBurns[k - 1] / 2 + AlignLead + SeparationWait + SwitchMargin + OrientTime));
                steps.Add(new SequenceStep(StepKind.OrientForRelease, satellite: k));
                steps.Add(new SequenceStep(StepKind.Release, satellite: k));
                steps.Add(new SequenceStep(StepKind.Wait, seconds: SeparationWait));
                steps.Add(new SequenceStep(StepKind.SwitchToSatellite, satellite: k));
                steps.Add(new SequenceStep(StepKind.CreateCircularizeNode, satellite: k));
                steps.Add(new SequenceStep(StepKind.ExecuteNode, satellite: k));
                steps.Add(new SequenceStep(StepKind.SwitchToCarrier));
            }
            return steps;
        }
    }
}

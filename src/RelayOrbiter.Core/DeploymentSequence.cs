using System;
using System.Collections.Generic;

namespace RelayOrbiter.Core
{
    public enum StepKind
    {
        WarpToRelease,          // warp until Seconds before the carrier's next release point
        OrientForRelease,       // turn the carrier so Satellite's separation push is perpendicular to the velocity
        Release,                // fire Satellite's separator
        Wait,                   // let the vessels separate for Seconds
        ClearSatellite,         // carrier turns away; wait (up to Seconds) until Satellite is clear
        SwitchToSatellite,
        SwitchToCarrier,
        CreateParkingNode,      // carrier: parking-orbit burn at the next apoapsis
        CreateCircularizeNode,  // active vessel: circularize at the next apoapsis
        ExecuteNode,            // fly the active vessel's next node
        CreateTransferNode,           // carrier: first Hohmann burn towards the target orbit
        CreateTargetCircularizeNode,  // carrier: circularize at the target orbit
        CreatePlaneChangeNode,        // carrier: rotate the orbit plane at the next node
        CreateMoonPlaneNode,          // carrier: rotate into the destination moon's orbital plane (skipped when in it)
        CreateEjectionNode,           // carrier: leave for the destination moon at the next window
        CreateCourseCorrectionNode,   // carrier: fine-tune the encounter's periapsis (skipped when good)
        WarpToSoi,                    // warp until the carrier enters the destination's sphere of influence
        WarpToMidcourse,              // interplanetary: warp to halfway, for a second correction
        CreateCaptureNode,            // carrier: circularize at the periapsis around the destination
    }

    // What to do before the deployment to reach the target orbit.
    public sealed class TransferSteps
    {
        public TransferSteps(bool transfer, bool planeChange, bool planeChangeFirst, bool moon = false,
                             bool interplanetary = false)
        {
            Transfer         = transfer;
            PlaneChange      = planeChange;
            PlaneChangeFirst = planeChangeFirst;
            Moon             = moon;
            Interplanetary   = interplanetary;
        }

        // Travel first, in legs: to a sibling of the current body (Interplanetary), then
        // (or only) to a moon of the body the carrier is around by then (Moon).
        public bool Moon             { get; }
        public bool Interplanetary   { get; }
        public int  Legs => (Moon ? 1 : 0) + (Interplanetary ? 1 : 0);

        public bool Transfer         { get; }
        public bool PlaneChange      { get; }
        public bool PlaneChangeFirst { get; }  // lowering: change the plane while still high (slower)
    }

    public sealed class SequenceStep
    {
        public SequenceStep(StepKind kind, int satellite = -1, double seconds = 0.0, int leg = 0)
        {
            Kind      = kind;
            Satellite = satellite;
            Seconds   = seconds;
            Leg       = leg;
        }

        public int      Leg       { get; }  // travel steps: which leg (0 first)

        public StepKind Kind      { get; }
        public int      Satellite { get; }  // 0-based, -1 when not about a satellite
        public double   Seconds   { get; }  // WarpToRelease lead, Wait or ClearSatellite duration
    }

    // Steps for deploying N satellites. SAT-1 is released before the carrier's
    // parking burn, while the carrier is still circular, so it needs no burn of
    // its own; SAT-2..SAT-N are released one parking orbit apart and circularize
    // at the release point (apoapsis for an inner parking orbit, periapsis for an
    // outer one). Before each release the carrier turns so
    // the separation push is perpendicular to the velocity (SeparationAttitude);
    // after releasing SAT-2 onwards it turns away and waits until the satellite is
    // clear, so the satellite can turn for its burn without hitting the carrier.
    public static class DeploymentSequence
    {
        public const double AlignLead      = 60.0;  // s reserved to point before a burn starts
        public const double SeparationWait = 5.0;   // s between release and switching vessel
        public const double SwitchMargin   = 15.0;  // s for the vessel switch to settle
        public const double ReleaseMargin  = 30.0;  // s spare before SAT-1's release
        public const double OrientTime     = 60.0;  // s reserved to orient before each release
        public const double ClearTime      = 45.0;  // s reserved to get clear of SAT-2 / SAT-3

        // carrierBurn: parking burn duration; satelliteBurns: circularization durations (s) of
        // SAT-2..SAT-N, so there are satelliteBurns.Count + 1 satellites.
        public static IReadOnlyList<SequenceStep> Build(double carrierBurn, IReadOnlyList<double> satelliteBurns,
                                                        TransferSteps? prelude = null)
        {
            if (satelliteBurns.Count < 1)
                throw new ArgumentException("Need the circularization burns of SAT-2 onwards.", nameof(satelliteBurns));

            var steps = new List<SequenceStep>();
            if (prelude != null)
            {
                void Burn(StepKind create, int leg = 0) =>
                    steps.AddRange(new[] { new SequenceStep(create, leg: leg), new SequenceStep(StepKind.ExecuteNode, leg: leg) });
                if (prelude.Interplanetary)
                {
                    Burn(StepKind.CreateEjectionNode);
                    Burn(StepKind.CreateCourseCorrectionNode);
                    steps.Add(new SequenceStep(StepKind.WarpToMidcourse));
                    Burn(StepKind.CreateCourseCorrectionNode);
                    steps.Add(new SequenceStep(StepKind.WarpToSoi));
                    Burn(StepKind.CreateCaptureNode);
                }
                if (prelude.Moon)
                {
                    int leg = prelude.Interplanetary ? 1 : 0;
                    Burn(StepKind.CreateMoonPlaneNode, leg);
                    Burn(StepKind.CreateEjectionNode, leg);
                    Burn(StepKind.CreateCourseCorrectionNode, leg);
                    steps.Add(new SequenceStep(StepKind.WarpToSoi, leg: leg));
                    Burn(StepKind.CreateCaptureNode, leg);
                }
                if (prelude.PlaneChange && prelude.PlaneChangeFirst) Burn(StepKind.CreatePlaneChangeNode);
                if (prelude.Transfer)
                {
                    Burn(StepKind.CreateTransferNode);
                    Burn(StepKind.CreateTargetCircularizeNode);
                }
                if (prelude.PlaneChange && !prelude.PlaneChangeFirst) Burn(StepKind.CreatePlaneChangeNode);
            }

            steps.AddRange(new[]
            {
                new SequenceStep(StepKind.WarpToRelease,
                                 seconds: carrierBurn / 2 + AlignLead + SeparationWait + ReleaseMargin + OrientTime),
                new SequenceStep(StepKind.OrientForRelease, satellite: 0),
                new SequenceStep(StepKind.Release, satellite: 0),
                new SequenceStep(StepKind.Wait, seconds: SeparationWait),
                new SequenceStep(StepKind.CreateParkingNode),
                new SequenceStep(StepKind.ExecuteNode),
            });

            for (int k = 1; k <= satelliteBurns.Count; ++k)
            {
                steps.Add(new SequenceStep(StepKind.WarpToRelease,
                          seconds: satelliteBurns[k - 1] / 2 + AlignLead + ClearTime + SwitchMargin + OrientTime));
                steps.Add(new SequenceStep(StepKind.OrientForRelease, satellite: k));
                steps.Add(new SequenceStep(StepKind.Release, satellite: k));
                steps.Add(new SequenceStep(StepKind.ClearSatellite, satellite: k, seconds: ClearTime));
                steps.Add(new SequenceStep(StepKind.SwitchToSatellite, satellite: k));
                steps.Add(new SequenceStep(StepKind.CreateCircularizeNode, satellite: k));
                steps.Add(new SequenceStep(StepKind.ExecuteNode, satellite: k));
                steps.Add(new SequenceStep(StepKind.SwitchToCarrier));
            }
            return steps;
        }
    }
}

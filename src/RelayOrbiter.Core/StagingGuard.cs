using System.Collections.Generic;

namespace RelayOrbiter.Core
{
    public enum StagingDecision
    {
        Stage,                  // safe: activate the next stage
        NothingLeft,            // no stage left to activate
        WouldReleaseSatellite,  // the next stage holds a satellite (or its separator)
        TooManyStages,          // already lit MaxStagesPerBurn stages of engines without thrust
    }

    // Whether the carrier may stage during one of its burns (a spent stage), without
    // ever releasing a satellite that is still on board.
    public static class StagingGuard
    {
        // Stages that light engines yet give no thrust: something is wrong past this.
        // Stages without engines (fairings, decouplers) don't count: a carrier may have
        // several between a spent stage and its next engine.
        public const int MaxStagesPerBurn = 4;

        // nextStage: the stage number that would activate; nextStageParts: its parts;
        // protectedParts: every part of the satellites still on board, separators included.
        // releasedParts: every part the stage's decouplers would cut loose (a decoupler
        // can sit in a carrier stage and still hold the satellites on its far side).
        public static StagingDecision Decide(int nextStage, IEnumerable<uint> nextStageParts,
                                             ISet<uint> protectedParts, int stagedThisBurn,
                                             IEnumerable<uint>? releasedParts = null)
        {
            if (nextStage < 0) return StagingDecision.NothingLeft;
            if (stagedThisBurn >= MaxStagesPerBurn) return StagingDecision.TooManyStages;
            foreach (uint id in nextStageParts)
                if (protectedParts.Contains(id)) return StagingDecision.WouldReleaseSatellite;
            if (releasedParts != null)
                foreach (uint id in releasedParts)
                    if (protectedParts.Contains(id)) return StagingDecision.WouldReleaseSatellite;
            return StagingDecision.Stage;
        }
    }
}

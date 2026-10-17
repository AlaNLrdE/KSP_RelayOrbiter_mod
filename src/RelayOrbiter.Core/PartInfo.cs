namespace RelayOrbiter.Core
{
    public enum SeparatorKind { None, Decoupler, DockingPort }

    // KSP-independent view of one part of a vessel, enough to find satellites
    // and estimate burns. Mass in tonnes (with resources), thrust in kN (vacuum),
    // Isp in seconds (vacuum).
    public sealed class PartInfo
    {
        public PartInfo(uint id, uint? parentId, string title,
                        SeparatorKind separator = SeparatorKind.None,
                        bool decouplesFromParent = false, uint? decoupledChildId = null,
                        bool isCommand = false, double mass = 0.0,
                        double thrust = 0.0, double isp = 0.0, bool engineActive = false)
        {
            Id                  = id;
            ParentId            = parentId;
            Title               = title;
            Separator           = separator;
            DecouplesFromParent = decouplesFromParent;
            DecoupledChildId    = decoupledChildId;
            IsCommand           = isCommand;
            Mass                = mass;
            Thrust              = thrust;
            Isp                 = isp;
            EngineActive        = engineActive;
        }

        public uint          Id                  { get; }
        public uint?         ParentId            { get; }  // null for the vessel's root part
        public string        Title               { get; }
        public SeparatorKind Separator           { get; }
        // Decouplers, and docking ports joined in the editor: true when the explosive
        // node / docking face points at the parent (this part leaves with the
        // satellite); otherwise DecoupledChildId is the part that leaves.
        // Ports docked in flight set neither: the outer port of the pair leaves.
        public bool          DecouplesFromParent { get; }
        public uint?         DecoupledChildId    { get; }
        public bool          IsCommand           { get; }  // probe core or crewed command part
        public double        Mass                { get; }
        public double        Thrust              { get; }  // 0 for non-engines
        public double        Isp                 { get; }
        public bool          EngineActive        { get; }  // engine already staged/ignited
    }
}

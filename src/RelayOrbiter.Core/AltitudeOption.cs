namespace RelayOrbiter.Core
{
    public enum AltitudeSource { Synchronous, Child, Midpoint, Custom }

    public enum AltitudeStatus
    {
        Ok,
        BelowSurface,      // altitude <= 0
        InsideAtmosphere,  // altitude < atmosphere height
        ExceedsSoi,        // altitude > SOI radius - body radius
    }

    // One candidate target altitude for the constellation's circular orbit.
    public sealed class AltitudeOption
    {
        public AltitudeOption(AltitudeSource source, string name, double altitude, AltitudeStatus status)
        {
            Source   = source;
            Name     = name;
            Altitude = altitude;
            Status   = status;
        }

        public AltitudeSource Source   { get; }
        public string         Name     { get; }  // child name, "A+B" for midpoints, else ""
        public double         Altitude { get; }  // metres above the surface
        public AltitudeStatus Status   { get; }
        public bool           IsValid  => Status == AltitudeStatus.Ok;
    }
}

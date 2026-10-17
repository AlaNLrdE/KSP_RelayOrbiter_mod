namespace RelayOrbiter.Core
{
    // Physical data for one celestial body. Distances in metres, times in seconds.
    public sealed class CelestialBody
    {
        public CelestialBody(string name, double radius, double mu, double atm, double rotPeriod,
                             double? soi, double? orbitalRadius, string? parent)
        {
            Name          = name;
            Radius        = radius;
            Mu            = mu;
            Atm           = atm;
            RotPeriod     = rotPeriod;
            Soi           = soi;
            OrbitalRadius = orbitalRadius;
            Parent        = parent;
        }

        public string  Name          { get; }
        public double  Radius        { get; }
        public double  Mu            { get; }
        public double  Atm           { get; }
        public double  RotPeriod     { get; }
        public double? Soi           { get; }  // null for Kerbol (no SOI limit)
        public double? OrbitalRadius { get; }  // null for Kerbol
        public string? Parent        { get; }  // null for Kerbol
    }
}

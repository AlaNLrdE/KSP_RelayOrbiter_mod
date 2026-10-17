namespace RelayOrbiter.Core
{
    // Value object returned by IConstellationCalculator.Calculate().
    // All distances in metres, times in seconds, velocities in m/s.
    public sealed class OrbitalResult
    {
        public string  BodyName     { get; set; } = "";
        public double  HTarget      { get; set; }  // target circular orbit altitude
        public double  RC           { get; set; }  // circular orbit radius
        public double  TC           { get; set; }  // circular orbit period
        public double  VC           { get; set; }  // circular orbit velocity
        public double  TE           { get; set; }  // elliptical parking orbit period
        public double  AE           { get; set; }  // elliptical orbit semi-major axis
        public double  RApo         { get; set; }  // apoapsis radius
        public double  HApo         { get; set; }  // apoapsis altitude
        public double  RPeri        { get; set; }  // periapsis radius
        public double  HPeri        { get; set; }  // periapsis altitude
        public double  VApo         { get; set; }  // carrier speed at apoapsis
        public double  VPeri        { get; set; }  // carrier speed at periapsis
        public bool    SurfaceOk    { get; set; }  // periapsis clears the surface
        public bool    AtmOk        { get; set; }  // periapsis clears the atmosphere
        public double  MinHSurface  { get; set; }  // minimum HTarget for periapsis > 0
        public double? MinHAtm      { get; set; }  // minimum HTarget for periapsis > atm

        public int          Satellites  { get; set; } = 3;
        public ParkingOrbit Parking     { get; set; } = ParkingOrbit.Inner;
        public double       SpacingDeg  { get; set; }  // 360° / Satellites
        public bool         SoiOk       { get; set; }  // parking orbit stays inside the SOI
        public double       CarrierDv   { get; set; }  // prograde Δv into the parking orbit (negative = retrograde)
        public double       SatelliteDv { get; set; }  // prograde Δv each satellite needs to circularize
    }
}

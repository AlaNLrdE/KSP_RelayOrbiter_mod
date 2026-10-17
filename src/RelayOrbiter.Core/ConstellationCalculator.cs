using System;

namespace RelayOrbiter.Core
{
    // (DIP) Depends on IOrbitalMechanics, not the concrete class.
    public sealed class ConstellationCalculator : IConstellationCalculator
    {
        private readonly IOrbitalMechanics _mechanics;

        public ConstellationCalculator(IOrbitalMechanics mechanics)
        {
            _mechanics = mechanics ?? throw new ArgumentNullException(nameof(mechanics));
        }

        public ParkingOrbit Recommend(CelestialBody body, double hTarget, int satellites)
        {
            var inner = Calculate(body, hTarget, satellites, ParkingOrbit.Inner);
            if (inner.SurfaceOk && inner.AtmOk) return ParkingOrbit.Inner;
            return Calculate(body, hTarget, satellites, ParkingOrbit.Outer).SoiOk
                ? ParkingOrbit.Outer : ParkingOrbit.Inner;
        }

        public OrbitalResult Calculate(CelestialBody body, double hTarget,
                                       int satellites = 3, ParkingOrbit parking = ParkingOrbit.Inner)
        {
            double ratio = Resonance.PeriodRatio(satellites, parking);
            var r = new OrbitalResult
            {
                BodyName   = body.Name,
                HTarget    = hTarget,
                Satellites = satellites,
                Parking    = parking,
                SpacingDeg = Resonance.SpacingDeg(satellites),
            };

            // Circular orbit
            r.RC = body.Radius + hTarget;
            r.TC = _mechanics.OrbitalPeriod(r.RC, body.Mu);
            r.VC = _mechanics.VisViva(r.RC, r.RC, body.Mu);

            // Elliptical parking orbit: T_e = ratio · T_c  =>  a_e = r_c · ratio^(2/3)
            r.TE = r.TC * ratio;
            r.AE = r.RC * Math.Pow(ratio, 2.0 / 3.0);

            // One apsis is on the target orbit (where the satellites are released);
            // the other follows from the semi-major axis.
            double other = 2.0 * r.AE - r.RC;
            if (parking == ParkingOrbit.Inner)
            {
                r.RApo = r.RC;   r.RPeri = other;
            }
            else
            {
                r.RPeri = r.RC;  r.RApo = other;
            }
            r.HApo  = r.RApo  - body.Radius;
            r.HPeri = r.RPeri - body.Radius;

            r.VApo  = _mechanics.VisViva(r.RApo,  r.AE, body.Mu);
            r.VPeri = _mechanics.VisViva(r.RPeri, r.AE, body.Mu);

            double vRelease = parking == ParkingOrbit.Inner ? r.VApo : r.VPeri;
            r.CarrierDv   = vRelease - r.VC;
            r.SatelliteDv = r.VC - vRelease;

            r.SurfaceOk = r.HPeri > 0.0;
            r.AtmOk     = body.Atm <= 0.0 || r.HPeri > body.Atm;
            r.SoiOk     = !body.Soi.HasValue || r.RApo <= body.Soi.Value;

            if (parking == ParkingOrbit.Inner)
            {
                // r_peri = r_c (2 ratio^(2/3) − 1), so r_c = r_peri / factor
                double factor = 2.0 * Math.Pow(ratio, 2.0 / 3.0) - 1.0;
                r.MinHSurface = body.Radius / factor - body.Radius;
                if (body.Atm > 0.0)
                    r.MinHAtm = (body.Radius + body.Atm) / factor - body.Radius;
            }
            else
            {
                // The periapsis is the target orbit itself.
                r.MinHSurface = 0.0;
                if (body.Atm > 0.0) r.MinHAtm = body.Atm;
            }

            return r;
        }
    }
}

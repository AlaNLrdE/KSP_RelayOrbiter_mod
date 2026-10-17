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

        public OrbitalResult Calculate(CelestialBody body, double hTarget)
        {
            var r = new OrbitalResult
            {
                BodyName = body.Name,
                HTarget  = hTarget,
            };

            // Circular orbit
            r.RC = body.Radius + hTarget;
            r.TC = _mechanics.OrbitalPeriod(r.RC, body.Mu);
            r.VC = _mechanics.VisViva(r.RC, r.RC, body.Mu);

            // Elliptical parking orbit: T_e = (2/3) T_c  =>  a_e = r_c (2/3)^(2/3)
            r.TE = r.TC * (2.0 / 3.0);
            r.AE = r.RC * Math.Pow(2.0 / 3.0, 2.0 / 3.0);

            // Apoapsis is the circular orbit; periapsis from the semi-major axis
            r.RApo  = r.RC;
            r.HApo  = hTarget;
            r.RPeri = 2.0 * r.AE - r.RC;
            r.HPeri = r.RPeri - body.Radius;

            r.VApo  = _mechanics.VisViva(r.RApo,  r.AE, body.Mu);
            r.VPeri = _mechanics.VisViva(r.RPeri, r.AE, body.Mu);

            r.SurfaceOk = r.HPeri > 0.0;
            r.AtmOk     = body.Atm <= 0.0 || r.HPeri > body.Atm;

            // r_peri = r_c (2 (2/3)^(2/3) − 1), so r_c = r_peri / factor
            double factor = 2.0 * Math.Pow(2.0 / 3.0, 2.0 / 3.0) - 1.0;
            r.MinHSurface = body.Radius / factor - body.Radius;
            if (body.Atm > 0.0)
                r.MinHAtm = (body.Radius + body.Atm) / factor - body.Radius;

            return r;
        }
    }
}

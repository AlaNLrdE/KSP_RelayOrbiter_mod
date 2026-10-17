using System;
using System.Collections.Generic;
using System.Linq;

namespace RelayOrbiter.Core
{
    // Hardcodes all 17 stock KSP bodies.
    // Adding a modded body only requires editing this table.
    public sealed class KspBodyDatabase : IBodyDatabase
    {
        private static readonly CelestialBody[] Bodies =
        {
            //                name      radius         mu            atm        rot_period     soi              orbital_radius     parent
            new CelestialBody("Kerbol", 261_600_000.0, 1.1723328e18,       0.0,     432_000.0, null,            null,              null),
            new CelestialBody("Moho",       250_000.0, 1.6860938e11,       0.0,   1_210_000.0,     9_646_663.0,   5_263_138_304.0, "Kerbol"),
            new CelestialBody("Eve",        700_000.0, 8.1717302e12,  90_000.0,      80_500.0,    85_109_365.0,   9_832_684_544.0, "Kerbol"),
            new CelestialBody("Gilly",       13_000.0, 8_289_450.0,        0.0,      28_255.0,       126_123.0,      31_500_000.0, "Eve"),
            new CelestialBody("Kerbin",     600_000.0, 3.5316000e12,  70_000.0,    21_549.425,    84_159_286.0,  13_599_840_256.0, "Kerbol"),
            new CelestialBody("Mun",        200_000.0, 6.5138398e10,       0.0,    138_984.38,     2_429_559.0,      12_000_000.0, "Kerbin"),
            new CelestialBody("Minmus",      60_000.0, 1.7658000e9,        0.0,  1_077_311.08,     2_247_428.0,      47_000_000.0, "Kerbin"),
            new CelestialBody("Duna",       320_000.0, 3.0136321e11,  50_000.0,    65_517.859,    47_921_949.0,  20_726_155_264.0, "Kerbol"),
            new CelestialBody("Ike",        130_000.0, 1.8568369e10,       0.0,    65_517.862,     1_049_598.0,       3_200_000.0, "Duna"),
            new CelestialBody("Dres",       138_000.0, 2.1484489e10,       0.0,      34_800.0,    32_832_840.0,  40_839_348_203.0, "Kerbol"),
            new CelestialBody("Jool",     6_000_000.0, 2.8252800e14, 200_000.0,      36_000.0, 2_455_985_200.0,  68_773_560_320.0, "Kerbol"),
            new CelestialBody("Laythe",     500_000.0, 1.9620000e12,  50_000.0,    52_980.879,     3_723_645.0,      27_184_000.0, "Jool"),
            new CelestialBody("Vall",       300_000.0, 2.0748150e11,       0.0,    105_962.09,     2_406_401.0,      43_152_000.0, "Jool"),
            new CelestialBody("Tylo",       600_000.0, 2.8252800e12,       0.0,    211_926.36,    10_856_518.0,      68_500_000.0, "Jool"),
            new CelestialBody("Bop",         65_000.0, 2.4868349e9,        0.0,     544_507.4,     1_221_060.0,     128_500_000.0, "Jool"),
            new CelestialBody("Pol",         44_000.0, 7.2170208e8,        0.0,    901_902.62,     1_042_138.0,     179_890_000.0, "Jool"),
            new CelestialBody("Eeloo",      210_000.0, 7.4410815e10,       0.0,      19_460.0,   119_082_940.0, 113_549_713_200.0, "Kerbol"),
        };

        public CelestialBody? Find(string name) =>
            Bodies.FirstOrDefault(b => b.Name == name);

        public IReadOnlyList<CelestialBody> All() => Bodies;

        public IReadOnlyList<CelestialBody> ChildrenOf(string parentName) =>
            Bodies.Where(b => string.Equals(b.Parent, parentName, StringComparison.Ordinal))
                  .OrderBy(b => b.OrbitalRadius!.Value)
                  .ToArray();
    }
}

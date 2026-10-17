using System;
using System.Collections.Generic;
using System.Linq;
using RelayOrbiter.Core;
using Body    = RelayOrbiter.Core.CelestialBody;
using KspBody = global::CelestialBody;

namespace RelayOrbiter.Plugin
{
    // IBodyDatabase backed by the bodies of the running game, so planet packs
    // (Kopernicus, OPM...) are picked up automatically. Snapshot taken once:
    // bodies don't change during a game session.
    internal sealed class GameBodyDatabase : IBodyDatabase
    {
        private readonly Body[] _bodies;

        public GameBodyDatabase(IEnumerable<KspBody> gameBodies)
        {
            _bodies = gameBodies.Select(Convert).ToArray();
        }

        public Body? Find(string name) => _bodies.FirstOrDefault(b => b.Name == name);

        public IReadOnlyList<Body> All() => _bodies;

        public IReadOnlyList<Body> ChildrenOf(string parentName) =>
            _bodies.Where(b => string.Equals(b.Parent, parentName, StringComparison.Ordinal))
                   .OrderBy(b => b.OrbitalRadius ?? 0.0)
                   .ToArray();

        // Name shown to the player, not the internal bodyName. Used as the body's
        // identifier everywhere in the plugin. Stock KSP calls its star "The Sun"
        // (internal "Sun"); like the CLI and the wiki, show it as Kerbol. Other
        // stars (Kopernicus systems, other languages) keep the game's name.
        public static string NameOf(KspBody b)
        {
            string name = b.displayName.LocalizeRemoveGender();
            return b.bodyName == "Sun" && name == "The Sun" ? "Kerbol" : name;
        }

        private static Body Convert(KspBody b)
        {
            // A star has no orbit driver, and its referenceBody is itself.
            bool isStar = b.orbitDriver == null || b.referenceBody == null || b.referenceBody == b;
            return BodyFactory.FromGame(
                NameOf(b), b.Radius, b.gravParameter,
                b.atmosphere, b.atmosphereDepth,
                b.rotationPeriod, b.sphereOfInfluence,
                isStar ? (double?)null : b.orbitDriver!.orbit.semiMajorAxis,
                isStar ? null : NameOf(b.referenceBody!));
        }
    }
}

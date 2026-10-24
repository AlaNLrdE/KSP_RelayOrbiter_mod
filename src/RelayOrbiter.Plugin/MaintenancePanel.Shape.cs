using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RelayOrbiter.Core;
using UnityEngine;
using Body = RelayOrbiter.Core.CelestialBody;

namespace RelayOrbiter.Plugin
{
    // Shapes other than a ring (Walker Delta and Star, Molniya, Tundra, Draim's
    // tetrahedron, Trojans): pick the shape and its parameters, see where each
    // satellite goes, name them by slot, and correct one or all of them into the shape.
    internal sealed partial class MaintenancePanel
    {
        private static readonly ShapeKind[] Kinds =
        {
            ShapeKind.Ring, ShapeKind.WalkerDelta, ShapeKind.WalkerStar, ShapeKind.Molniya,
            ShapeKind.Tundra, ShapeKind.Tetrahedral, ShapeKind.Trojans,
        };
        private static readonly string[] KindNames =
            { "Ring", "Walker Δ", "Walker ★", "Molniya", "Tundra", "Tetrahedron", "Trojans" };

        private ShapeKind _shape = ShapeKind.Ring;
        private int _planes = 3, _phasingF = 1, _moonIndex;
        private string _shapeIncText = "55", _periapsisKmText = "";
        private ShapeAssignment? _shapeFit;
        private float _shapeFitAt = -10f;
        private string _shapeFitKey = "";

        // True when a shape other than a ring is selected.
        private bool DrawShapeSelector()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Shape", _header, GUILayout.Width(80f));
            int k = GUILayout.SelectionGrid(Array.IndexOf(Kinds, _shape), KindNames, 4);
            GUILayout.EndHorizontal();
            if (Kinds[k] != _shape)
            {
                _shape = Kinds[k];
                _shapeIncText = _shape switch
                {
                    ShapeKind.WalkerDelta => "55",
                    ShapeKind.WalkerStar  => "88",
                    ShapeKind.Molniya     => "63.4",
                    ShapeKind.Tundra      => "63.4",
                    _                     => _shapeIncText,
                };
                _shapeFitAt = -10f;
            }
            return _shape != ShapeKind.Ring;
        }

        // From the members' names: "… WD55-3-1" brings the shape and its parameters back.
        private void SuggestShape(List<Vessel> members)
        {
            foreach (var v in members)
            {
                var name = ConstellationName.Parse(v.vesselName);
                var kind = name == null ? null : ConstellationShapes.KindFromTag(name.Tag);
                if (kind == null) continue;
                _shape = kind.Value;
                var walker = ConstellationShapes.WalkerFromTag(name!.Tag);
                if (walker.HasValue)
                {
                    _shapeIncText = walker.Value.inclination.ToString("F0", CultureInfo.InvariantCulture);
                    _planes = walker.Value.planes;
                    _phasingF = walker.Value.phasing;
                }
                Debug.Log($"[RelayOrbiter] Shape from the names: {_shape} ({name.Tag}).");
                return;
            }
            _shape = ShapeKind.Ring;
        }

        private void DrawShaped(Body body, Vessel vessel, List<Vessel> candidates, List<Vessel> members)
        {
            int active = members.IndexOf(vessel);
            int? reference = ReferenceIndex(members);
            var spec = DrawShapeParameters(body, vessel, members, reference, active);
            if (spec == null) return;
            if (spec.Total != _slots) _slots = spec.Total;
            var slots = ConstellationShapes.Slots(spec);
            double now = Planetarium.GetUniversalTime();
            var moon = spec.Kind == ShapeKind.Trojans ? Moon(vessel) : null;

            // The fit is costly with many satellites: once a second is plenty for the display.
            if (members.Count > 0 && members.Count <= slots.Count)
            {
                string key = $"{spec.Code()}|{spec.Total}|{spec.Sma:F0}|{reference}|{string.Join(",", members.Select(v => v.persistentId))}";
                if (key != _shapeFitKey || Time.realtimeSinceStartup - _shapeFitAt > 1f)
                {
                    _shapeFit = FitShape(spec, members, reference, null, moon, now);
                    _shapeFitKey = key;
                    _shapeFitAt = Time.realtimeSinceStartup;
                }
            }
            else _shapeFit = null;
            var fit = _shapeFit;

            GUILayout.Label("Satellites (tick the members)   slot · plane · off by · off its plane", _header);
            BeginList(candidates.Count, ref _listScroll);
            foreach (var v in candidates)
            {
                int i = members.IndexOf(v);
                string info = fit != null && i >= 0 && i < fit.SlotOf.Length
                    ? $"  #{fit.SlotOf[i] + 1} · P{fit.SlotFor(i).Plane + 1} · {fit.PhaseErrorsDeg[i]:+0.0;-0.0}° · {fit.PlaneErrorsDeg[i]:F1}°"
                    : "";
                string mark = v == vessel ? " (active)" : i >= 0 && i == reference ? " (reference)" : "";
                bool on = GUILayout.Toggle(i >= 0, Short(v.vesselName) + mark + info);
                if (on != (i >= 0))
                {
                    if (on) _selected.Add(v.persistentId); else _selected.Remove(v.persistentId);
                    _shapeFitAt = -10f;
                }
            }
            EndList(candidates.Count);
            if (members.Count > slots.Count) { Warn($"{members.Count} satellites don't fit in {slots.Count} slots."); return; }
            if (members.Count < 1 || fit == null) { Warn("Tick the satellites of the constellation."); return; }
            if (spec.Kind != ShapeKind.Trojans &&
                GUILayout.Button("Reference: " + (reference.HasValue
                    ? Short(members[reference.Value].vesselName) + " (stays put)" : "best fit")))
                NextReference(members);

            // The target orbit and its cost.
            double mu = body.Mu;
            var s0 = slots[0];
            double period = _mechanics.OrbitalPeriod(spec.Sma, mu);
            Row(s0.Elliptical ? "Orbit" : "Altitude",
                s0.Elliptical ? $"{Km(s0.PeriapsisRadius - body.Radius)} × {Km(s0.ApoapsisRadius - body.Radius)}, e {s0.Eccentricity:F3}"
                              : Km(spec.Sma - body.Radius));
            Row("Period", _formatTime(period));
            if (spec.Kind != ShapeKind.Trojans)
                Row("Reference node / phase", $"Ω0 {fit.Lan0Deg:F1}°, θ0 {fit.Theta0Deg:F1}°");
            double planeDv = Enumerable.Range(0, members.Count)
                .Sum(i => fit.PlaneErrorsDeg[i] < PlaneToleranceDeg ? 0.0
                          : PlaneGrouping.PlaneChangeDv(members[i].orbit.orbitalSpeed, fit.PlaneErrorsDeg[i]));
            Row("Plane changes (Δv, at the current speeds)", Ms(planeDv), planeDv < 500.0 * members.Count ? _ok : _warn);
            if (fit.PlaneErrorsDeg.Any(e => e > 30.0))
                GUILayout.Label("Some satellites are far from their plane: a plane change that big costs a lot. " +
                                "Satellites of different planes are usually launched separately, each one close to its " +
                                "own plane.", _warn);

            DrawShapeNames(body, members, fit, spec);

            NodeExecutor.AutoActivateEngines = GUILayout.Toggle(NodeExecutor.AutoActivateEngines,
                "Activate the satellites' engines when none is running before a burn");
            Warp.Enabled = GUILayout.Toggle(Warp.Enabled, "Use time warp (off: you warp, burns still start on time)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Phasing orbits (at least)", GUILayout.Width(LabelW));
            if (GUILayout.Button("−", GUILayout.Width(24f))) _minOrbits--;
            _minOrbits = Mathf.Clamp(_minOrbits, 1, StationKeeping.MaxPhasingOrbits);
            GUILayout.Label(_minOrbits.ToString(CultureInfo.InvariantCulture), GUILayout.Width(24f));
            if (GUILayout.Button("+", GUILayout.Width(24f))) _minOrbits++;
            _minOrbits = Mathf.Clamp(_minOrbits, 1, StationKeeping.MaxPhasingOrbits);
            GUILayout.EndHorizontal();

            GUILayout.Label("Correct", _header);
            if (active >= 0 && reference != active &&
                GUILayout.Button($"Put {Short(vessel.vesselName)} into its slot"))
            {
                var single = FitShape(spec, members, reference, active, moon, now);
                var state = StartShape(body, members, single, period, reference, moon);
                for (int i = 0; i < members.Count; ++i)
                    if (i != active) state.Stage[i] = CorrectionStage.Done;
                _runner = new MaintenanceRunner(state, false, _transfer, _phasing, _planner, _formatTime);
                return;
            }
            if (GUILayout.Button("Put every satellite into the shape"))
            {
                var state = StartShape(body, members, FitShape(spec, members, reference, null, moon, now), period,
                                       reference, moon);
                _runner = new MaintenanceRunner(state, false, _transfer, _phasing, _planner, _formatTime);
                return;
            }
            GUILayout.Label("Each satellite: into its plane (after raising, when it goes up), transfer, " +
                            (s0.Elliptical ? "raise the apoapsis where its periapsis must be, " : "") +
                            "phasing, period trim. They work in parallel, switching satellites as burns come due.", _ok);
        }

        // The shape's parameters; null while they are invalid.
        private ShapeSpec? DrawShapeParameters(Body body, Vessel vessel, List<Vessel> members, int? reference, int active)
        {
            double mu = body.Mu;
            double inc = 0.0;
            if (_shape != ShapeKind.Tetrahedral && _shape != ShapeKind.Trojans)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Inclination [°]", GUILayout.Width(LabelW));
                _shapeIncText = GUILayout.TextField(_shapeIncText, GUILayout.Width(80f));
                GUILayout.EndHorizontal();
                if (!double.TryParse(_shapeIncText, NumberStyles.Float, CultureInfo.InvariantCulture, out inc) ||
                    inc < 0.0 || inc > 180.0)
                {
                    Warn("Enter an inclination between 0 and 180°.");
                    return null;
                }
            }

            switch (_shape)
            {
                case ShapeKind.WalkerDelta:
                case ShapeKind.WalkerStar:
                {
                    var divisors = Enumerable.Range(1, _slots).Where(d => _slots % d == 0).ToList();
                    int di = Math.Max(0, divisors.IndexOf(_planes));
                    if (!divisors.Contains(_planes)) _planes = divisors[Math.Min(divisors.Count - 1, 1)];
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Planes", GUILayout.Width(LabelW));
                    if (GUILayout.Button("−", GUILayout.Width(24f))) _planes = divisors[Math.Max(0, di - 1)];
                    GUILayout.Label(_planes.ToString(CultureInfo.InvariantCulture), GUILayout.Width(24f));
                    if (GUILayout.Button("+", GUILayout.Width(24f))) _planes = divisors[Math.Min(divisors.Count - 1, di + 1)];
                    GUILayout.Label($"{_slots / _planes} per plane");
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Phasing F (between planes)", GUILayout.Width(LabelW));
                    if (GUILayout.Button("−", GUILayout.Width(24f))) _phasingF--;
                    _phasingF = ((_phasingF % _planes) + _planes) % _planes;
                    GUILayout.Label(_phasingF.ToString(CultureInfo.InvariantCulture), GUILayout.Width(24f));
                    if (GUILayout.Button("+", GUILayout.Width(24f))) _phasingF++;
                    _phasingF = ((_phasingF % _planes) + _planes) % _planes;
                    GUILayout.Label($"{_phasingF * 360.0 / _slots:0.#}° per plane");
                    GUILayout.EndHorizontal();
                    if (!DrawFinalAltitude(body, members, reference, active)) return null;
                    double sma = TargetSma(body, members, reference, -1);
                    return _shape == ShapeKind.WalkerDelta
                        ? ConstellationShapes.WalkerDelta(_slots, _planes, _phasingF, inc, sma)
                        : ConstellationShapes.WalkerStar(_slots, _planes, _phasingF, inc, sma);
                }
                case ShapeKind.Molniya:
                case ShapeKind.Tundra:
                {
                    double minPe = PhasingPlanner.MinRadius(body) - body.Radius;
                    if (_periapsisKmText.Length == 0)
                        _periapsisKmText = (Math.Ceiling(minPe / 1000.0) + 50).ToString("F0", CultureInfo.InvariantCulture);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Periapsis altitude [km]", GUILayout.Width(LabelW));
                    _periapsisKmText = GUILayout.TextField(_periapsisKmText, GUILayout.Width(80f));
                    GUILayout.EndHorizontal();
                    if (!double.TryParse(_periapsisKmText, NumberStyles.Float, CultureInfo.InvariantCulture, out double km) ||
                        km * 1000.0 < minPe)
                    {
                        Warn($"Enter a periapsis altitude of at least {Km(minPe)}.");
                        return null;
                    }
                    var spec = ConstellationShapes.Elliptical(_shape, _slots, mu, body.RotPeriod, body.Radius + km * 1000.0, inc);
                    double ra = spec.Sma * (1.0 + spec.Eccentricity);
                    if (body.Soi.HasValue && ra > body.Soi.Value)
                    {
                        Warn($"The apoapsis ({Km(ra - body.Radius)}) leaves {body.Name}'s SOI: this body rotates too slowly " +
                             "for this shape.");
                        return null;
                    }
                    if (spec.Eccentricity < 0.05)
                        Warn("With this periapsis the orbit is almost circular: the shape loses its point.");
                    return spec;
                }
                case ShapeKind.Tetrahedral:
                {
                    var spec = ConstellationShapes.Tetrahedral(mu, body.RotPeriod);
                    var rp = spec.Sma * (1.0 - spec.Eccentricity);
                    if (rp < PhasingPlanner.MinRadius(body) || body.Soi.HasValue && spec.Sma * (1 + spec.Eccentricity) > body.Soi.Value)
                    {
                        Warn($"Draim's orbits don't fit around {body.Name} (periapsis {Km(rp - body.Radius)}).");
                        return null;
                    }
                    GUILayout.Label("Four satellites on Draim's elliptical orbits (i 31.3°, e 0.263, period ~1.1 " +
                                    "rotations): together they see the whole surface.", _ok);
                    return spec;
                }
                default:  // Trojans
                {
                    var moons = vessel.mainBody.orbitingBodies;
                    if (moons == null || moons.Count == 0) { Warn($"{body.Name} has no moon."); return null; }
                    _moonIndex = Mathf.Clamp(_moonIndex, 0, moons.Count - 1);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("In the orbit of", GUILayout.Width(LabelW));
                    if (GUILayout.Button("◀", GUILayout.Width(28f))) _moonIndex = (_moonIndex + moons.Count - 1) % moons.Count;
                    GUILayout.Label(GameBodyDatabase.NameOf(moons[_moonIndex]));
                    if (GUILayout.Button("▶", GUILayout.Width(28f))) _moonIndex = (_moonIndex + 1) % moons.Count;
                    GUILayout.EndHorizontal();
                    var m = moons[_moonIndex].orbit;
                    GUILayout.Label("Two slots, 60° ahead of and behind the moon (three: and opposite it). Set Slots to 2 or 3.", _ok);
                    return ConstellationShapes.Trojans(Mathf.Clamp(_slots, 2, 3), m.semiMajorAxis, m.eccentricity,
                                                       m.inclination, m.argumentOfPeriapsis);
                }
            }
        }

        private CelestialBody? Moon(Vessel vessel)
        {
            var moons = vessel.mainBody.orbitingBodies;
            return moons == null || moons.Count == 0 ? null : moons[Mathf.Clamp(_moonIndex, 0, moons.Count - 1)];
        }

        private static ShapeAssignment FitShape(ShapeSpec spec, List<Vessel> members, int? reference, int? exclude,
                                               CelestialBody? moon, double now)
        {
            var shapeMembers = members.Select(v => new ShapeMember(v.orbit.inclination, v.orbit.LAN,
                                                                   MeanLongitude.ArgLat(v.orbit, now))).ToList();
            return moon != null
                ? ShapeFit.Fit(spec, shapeMembers, null, exclude, moon.orbit.LAN, MeanLongitude.ArgLat(moon.orbit, now))
                : ShapeFit.Fit(spec, shapeMembers, reference, exclude);
        }

        private MaintenanceState StartShape(Body body, List<Vessel> members, ShapeAssignment fit, double period,
                                            int? reference, CelestialBody? moon)
        {
            var state = MaintenanceState.StartShaped(members.Select(v => v.persistentId).ToList(), fit,
                                                     Planetarium.GetUniversalTime(), period,
                                                     moon != null ? null : reference, _minOrbits,
                                                     PhasingPlanner.MinRadius(body), MaxRadius(body), moon?.bodyName);
            Debug.Log($"[RelayOrbiter] Shape {_shape}: Ω0 {fit.Lan0Deg:F2}°, θ0 {fit.Theta0Deg:F2}°; " +
                      string.Join(", ", members.Select((v, i) => $"{v.vesselName} → slot {fit.SlotOf[i] + 1}")));
            return state;
        }

        // Names in our scheme by slot, with the shape in the tag: "Kerbin Orion 5/12 - SYNC WD55-3-1".
        private void DrawShapeNames(Body body, List<Vessel> members, ShapeAssignment fit, ShapeSpec spec)
        {
            var memberIds = new HashSet<uint>(members.Select(v => v.persistentId));
            var others = FlightGlobals.Vessels.Where(v => v != null && !memberIds.Contains(v.persistentId))
                                              .Select(v => v.vesselName);
            string callsign = PlaneGrouping.ChooseCallsign(body.Name, members.Select(v => v.vesselName).ToList(), others);
            string altitude = ConstellationNaming.OrbitTag(spec.Sma - body.Radius, _mechanics.SyncOrbitAltitude(body));
            string tag = (altitude + " " + spec.Code()).Trim();
            var names = members.Select((v, i) => $"{body.Name} {callsign} {fit.SlotOf[i] + 1}/{spec.Total} - {tag}").ToList();
            int changes = members.Where((v, i) => v.vesselName != names[i]).Count();
            if (changes == 0) return;
            if (GUILayout.Button($"Name them by slot ({names[0]} …)"))
                for (int i = 0; i < members.Count; ++i) VesselRename.Apply(members[i], names[i]);
        }
    }
}

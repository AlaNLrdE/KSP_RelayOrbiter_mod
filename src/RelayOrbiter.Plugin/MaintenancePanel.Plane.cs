using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RelayOrbiter.Core;
using UnityEngine;
using Body = RelayOrbiter.Core.CelestialBody;

namespace RelayOrbiter.Plugin
{
    // "Group by plane": the relays sharing the active satellite's orbital plane
    // (within a tolerance) become one constellation, renamed in our scheme, ready to
    // be spaced evenly by "Group by name".
    internal sealed partial class MaintenancePanel
    {
        private bool _byPlane;
        private Vector2 _namesScroll;
        private string _toleranceText = PlaneGrouping.DefaultToleranceDeg.ToString("F0", CultureInfo.InvariantCulture);
        private double _planeTolerance = double.NaN;     // tolerance the selection was made with
        private uint _planeFor;                          // active vessel the selection was made for
        private readonly HashSet<uint> _planeSelected = new HashSet<uint>();

        private void DrawPlaneGroup(Body body, Vessel vessel, List<Vessel> candidates)
        {
            GUILayout.Label($"Relays in the plane of {Short(vessel.vesselName)}", _header);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Tolerance [°] (relative inclination)", GUILayout.Width(LabelW));
            _toleranceText = GUILayout.TextField(_toleranceText, GUILayout.Width(60f));
            GUILayout.EndHorizontal();
            if (!double.TryParse(_toleranceText, NumberStyles.Float, CultureInfo.InvariantCulture, out double tolerance) ||
                tolerance < 0.0 || tolerance > 90.0)
            {
                Warn("Enter a tolerance between 0 and 90°.");
                return;
            }

            var planes = candidates.Select(v => new OrbitPlane(v.orbit.inclination, v.orbit.LAN)).ToList();
            int seed = candidates.IndexOf(vessel);
            if (seed < 0) { Warn("The active vessel can't be a member (debris, or not orbiting)."); return; }
            // The button is drawn every frame (IMGUI needs the same controls each pass).
            bool again = GUILayout.Button("Suggest again");
            if (again || _planeFor != vessel.persistentId || tolerance != _planeTolerance)
                SuggestPlane(vessel, candidates, planes, seed, tolerance);

            GUILayout.Label("Tick the members   plane difference · altitude", _header);
            BeginList(candidates.Count, ref _planeScroll);
            foreach (var v in candidates)
            {
                int i = candidates.IndexOf(v);
                bool ticked = _planeSelected.Contains(v.persistentId);
                double rel = PlaneGrouping.RelativeInclinationDeg(planes[seed], planes[i]);
                string mark = v == vessel ? " (active)" : "";
                bool on = GUILayout.Toggle(ticked, $"{Short(v.vesselName)}{mark}  {rel:F1}° · {Km(v.orbit.semiMajorAxis - body.Radius)}");
                if (on != ticked)
                {
                    if (on) _planeSelected.Add(v.persistentId); else _planeSelected.Remove(v.persistentId);
                }
            }
            EndList(candidates.Count);

            var members = candidates.Where(v => _planeSelected.Contains(v.persistentId)).ToList();
            if (!members.Contains(vessel)) { Warn("Tick the active vessel too: the names start from it."); return; }
            if (members.Count < StationKeeping.MinSlots) { Warn("Tick at least two satellites."); return; }
            if (members.Count > StationKeeping.MaxSlots) { Warn($"At most {StationKeeping.MaxSlots} satellites."); return; }

            var names = NewNames(body, vessel, members);
            GUILayout.Label("New names", _header);
            BeginList(members.Count, ref _namesScroll);
            for (int i = 0; i < members.Count; ++i)
                GUILayout.Label(members[i].vesselName == names[i]
                    ? $"  {names[i]} (unchanged)"
                    : $"  {Short(members[i].vesselName)} → {names[i]}");
            EndList(members.Count);
            double spread = members.Max(a => members.Max(b =>
                PlaneGrouping.RelativeInclinationDeg(new OrbitPlane(a.orbit.inclination, a.orbit.LAN),
                                                     new OrbitPlane(b.orbit.inclination, b.orbit.LAN))));
            Row("Largest plane difference", $"{spread:F2}°", spread <= 1.0 ? _ok : _warn);
            if (spread > 1.0)
                GUILayout.Label("Spacing them evenly doesn't change their planes: satellites in different planes " +
                                "drift apart in places, and their names show the mean inclination.", _warn);

            if (GUILayout.Button("Rename them and form the constellation"))
            {
                for (int i = 0; i < members.Count; ++i) VesselRename.Apply(members[i], names[i]);
                _byPlane = false;
                Suggest(vessel, Candidates(vessel));  // now from our names: the new group, N slots
            }
        }

        private void SuggestPlane(Vessel vessel, List<Vessel> candidates, List<OrbitPlane> planes, int seed,
                                  double tolerance)
        {
            _planeFor = vessel.persistentId;
            _planeTolerance = tolerance;
            _planeSelected.Clear();
            foreach (int i in PlaneGrouping.SamePlane(planes, seed, tolerance)) _planeSelected.Add(candidates[i].persistentId);
            Debug.Log($"[RelayOrbiter] Plane of {vessel.vesselName} (i {vessel.orbit.inclination:F2}°, " +
                      $"Ω {vessel.orbit.LAN:F2}°), within {tolerance:F1}°: {_planeSelected.Count} vessel(s).");
        }

        // Names in our scheme, in the members' order: the active vessel is 1/N (or the
        // member already named 1/N), the others follow it around the orbit.
        private IReadOnlyList<string> NewNames(Body body, Vessel vessel, List<Vessel> members)
        {
            var memberIds = new HashSet<uint>(members.Select(v => v.persistentId));
            var others = FlightGlobals.Vessels.Where(v => v != null && !memberIds.Contains(v.persistentId))
                                              .Select(v => v.vesselName);
            string callsign = PlaneGrouping.ChooseCallsign(body.Name, members.Select(v => v.vesselName).ToList(), others);
            // The median: one member far off (still to be moved) doesn't skew the tag.
            var smas = members.Select(v => v.orbit.semiMajorAxis).OrderBy(a => a).ToList();
            double median = smas.Count % 2 == 1 ? smas[smas.Count / 2]
                                                : (smas[smas.Count / 2 - 1] + smas[smas.Count / 2]) / 2.0;
            string tag = ConstellationNaming.OrbitTag(median - body.Radius, _mechanics.SyncOrbitAltitude(body));
            double inclination = PlaneGrouping.MeanInclinationDeg(
                members.Select(v => new OrbitPlane(v.orbit.inclination, v.orbit.LAN)));
            double now = Planetarium.GetUniversalTime();
            // Already numbered with this callsign: keep the numbering, start from 1/N.
            int seed = members.FindIndex(v => ConstellationName.Parse(v.vesselName) is { } p &&
                                              p.Callsign == callsign && p.Body == body.Name && p.Index == 1);
            return PlaneGrouping.Names(body.Name, callsign, members.Select(v => MeanLongitude.At(v.orbit, now)).ToList(),
                                       seed >= 0 ? seed : members.IndexOf(vessel), tag, inclination);
        }
    }
}

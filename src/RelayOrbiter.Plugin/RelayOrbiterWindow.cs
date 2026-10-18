using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RelayOrbiter.Core;
using UnityEngine;
using Body = RelayOrbiter.Core.CelestialBody;

namespace RelayOrbiter.Plugin
{
    // IMGUI window (flight only): target altitude around the body the active vessel
    // orbits, the deployment plan, and the Maneuvers and Automation tabs.
    // All orbital logic lives in RelayOrbiter.Core; this class only draws state.
    internal sealed class RelayOrbiterWindow
    {
        private const int   WindowId = 0x52454C41;  // "RELA"
        private const float Width    = 440f;
        private const float LabelW   = 230f;

        private readonly IBodyDatabase            _db;
        private readonly IAltitudePlanner         _planner;
        private readonly IConstellationCalculator _calculator;
        private readonly Func<double, string>     _formatTime;
        private readonly ConstellationSettings    _settings;
        private readonly ManeuversPanel           _maneuvers;
        private readonly AutomationPanel          _automation;
        private readonly Action                   _onClose;

        private Rect    _rect = new Rect(240f, 120f, Width, 200f);
        private Body?   _body;  // the body the active vessel orbits
        private AltitudeSource _source = AltitudeSource.Synchronous;
        private int     _child, _midA, _midB = 1;
        private string  _customKm = "1000";
        private int     _tab;  // 0 = results, 1 = maneuvers, 2 = automation

        private GUIStyle? _ok, _warn, _header;

        public RelayOrbiterWindow(IBodyDatabase db, IAltitudePlanner planner,
                                  IConstellationCalculator calculator,
                                  Func<double, string> formatTime, ConstellationSettings settings,
                                  ManeuversPanel maneuvers, AutomationPanel automation, Action onClose)
        {
            _settings   = settings;
            _db         = db;
            _planner    = planner;
            _calculator = calculator;
            _formatTime = formatTime;
            _maneuvers  = maneuvers;
            _automation = automation;
            _onClose    = onClose;
        }

        public bool Visible { get; set; }

        // Runs the automation every frame, even while the window is hidden.
        public void Tick()  => _automation.Tick();
        public void Abort() => _automation.Abort();

        public Rect Rect => _rect;
        public Vector2 Position { get => _rect.position; set => _rect.position = value; }
        public int Tab { get => _tab; set => _tab = Mathf.Clamp(value, 0, 2); }

        public void Draw()
        {
            if (_ok == null) CreateStyles();
            _rect = GUILayout.Window(WindowId, _rect, DrawContents, "Relay Orbiter", GUILayout.Width(Width));
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private void DrawContents(int id)
        {
            if (GUI.Button(new Rect(_rect.width - 22f, 3f, 18f, 16f), "×")) _onClose();

            if (!FollowActiveVessel())
            {
                GUILayout.Label("No active vessel.", _warn);
                GUI.DragWindow();
                return;
            }
            var body = _body!;

            DrawSatelliteCount();
            var option = DrawAltitudeSelector(body);
            GUILayout.Space(6f);

            if (_automation.IsRunning || _automation.IsLaunching) _tab = 2;
            _tab = GUILayout.Toolbar(_tab, new[] { "Results", "Maneuvers", "Automation" });

            if (_tab == 1)
                _maneuvers.Draw(body, option != null && option.IsValid ? option.Altitude : (double?)null);
            else if (_tab == 2)
                _automation.Draw(body, option != null && option.IsValid ? option.Altitude : (double?)null,
                                 option == null ? "Enter a valid target altitude."
                                 : option.IsValid ? null : StatusText(body, option));
            else if (option == null) { }
            else if (!option.IsValid) GUILayout.Label(StatusText(body, option), _warn);
            else
            {
                int n = _settings.Satellites;
                var parking = _calculator.Recommend(body, option.Altitude, n);
                DrawResult(body, _calculator.Calculate(body, option.Altitude, n, parking));
            }

            GUI.DragWindow();
        }

        private void DrawSatelliteCount()
        {
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && !_automation.IsRunning && !_automation.IsLaunching;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Satellites", _header, GUILayout.Width(80f));
            if (GUILayout.Button("−", GUILayout.Width(24f))) _settings.Satellites--;
            GUILayout.Label(_settings.Satellites.ToString(CultureInfo.InvariantCulture), GUILayout.Width(24f));
            if (GUILayout.Button("+", GUILayout.Width(24f))) _settings.Satellites++;
            GUILayout.Label($"{Resonance.SpacingDeg(_settings.Satellites):F0}° apart");
            GUILayout.EndHorizontal();
            GUI.enabled = wasEnabled;
        }

        // Track the body the active vessel orbits (it changes with the SOI).
        private bool FollowActiveVessel()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return false;
            string name = GameBodyDatabase.NameOf(vessel.mainBody);
            if (_body == null || _body.Name != name)
            {
                _body = _db.Find(name);
                _child = 0; _midA = 0; _midB = 1;
            }
            return _body != null;
        }

        private AltitudeOption? DrawAltitudeSelector(Body body)
        {
            GUILayout.Label($"Orbiting {body.Name}{AtmHint(body)} — target altitude", _header);

            var kids = _db.ChildrenOf(body.Name);
            var sources = new List<AltitudeSource> { AltitudeSource.Synchronous };
            if (kids.Count >= 1) sources.Add(AltitudeSource.Child);
            if (kids.Count >= 2) sources.Add(AltitudeSource.Midpoint);
            sources.Add(AltitudeSource.Custom);
            if (!sources.Contains(_source)) _source = AltitudeSource.Synchronous;

            string childWord = body.Parent == null ? "planet" : "moon";
            var labels = sources.Select(s => SourceLabel(s, childWord)).ToArray();
            _source = sources[GUILayout.Toolbar(sources.IndexOf(_source), labels)];

            switch (_source)
            {
                case AltitudeSource.Synchronous:
                    var sync = _planner.Synchronous(body);
                    Row("Synchronous orbit", Km(sync.Altitude));
                    return sync;

                case AltitudeSource.Child:
                {
                    var options = _planner.Children(body);
                    _child = Mathf.Clamp(_child, 0, options.Count - 1);
                    _child = GUILayout.SelectionGrid(_child, options.Select(o => o.Name).ToArray(), 3);
                    Row($"{options[_child].Name}'s orbital altitude", Km(options[_child].Altitude));
                    return options[_child];
                }

                case AltitudeSource.Midpoint:
                {
                    var names = kids.Select(k => k.Name).ToArray();
                    _midA = Mathf.Clamp(_midA, 0, kids.Count - 1);
                    _midB = Mathf.Clamp(_midB, 0, kids.Count - 1);
                    GUILayout.Label($"First {childWord}");
                    _midA = GUILayout.SelectionGrid(_midA, names, 3);
                    GUILayout.Label($"Second {childWord}");
                    _midB = GUILayout.SelectionGrid(_midB, names, 3);
                    if (_midA == _midB)
                    {
                        GUILayout.Label($"Choose two different {childWord}s.", _warn);
                        return null;
                    }
                    var mid = _planner.Midpoint(body, kids[_midA], kids[_midB]);
                    Row($"Midpoint {kids[_midA].Name} – {kids[_midB].Name}", Km(mid.Altitude));
                    return mid;
                }

                default:
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Altitude [km]", GUILayout.Width(LabelW));
                    _customKm = GUILayout.TextField(_customKm, GUILayout.Width(120f));
                    GUILayout.EndHorizontal();
                    if (!double.TryParse(_customKm, NumberStyles.Float, CultureInfo.InvariantCulture, out double km))
                    {
                        GUILayout.Label("Enter a number.", _warn);
                        return null;
                    }
                    return _planner.Custom(body, km * 1000.0);
                }
            }
        }

        private void DrawResult(Body body, OrbitalResult r)
        {
            bool inner = r.Parking == ParkingOrbit.Inner;
            string point = inner ? "apoapsis" : "periapsis";
            string other = inner ? "periapsis" : "apoapsis";

            GUILayout.Label($"Final orbit: {r.Satellites} satellites, {r.SpacingDeg:F0}° apart", _header);
            Row("Altitude",         Km(r.HTarget));
            Row("Orbital velocity", Ms(r.VC));
            Row("Orbital period",   _formatTime(r.TC));

            GUILayout.Label($"Parking orbit (carrier, {(inner ? "inner" : "outer")}: released at {point})", _header);
            Row("Apoapsis altitude",  Km(r.HApo),
                inner || r.SoiOk ? GUI.skin.label : _warn);
            string periNote = !r.SurfaceOk ? "  BELOW SURFACE" : !r.AtmOk ? "  INSIDE ATMOSPHERE" : "";
            Row("Periapsis altitude", Km(r.HPeri) + periNote, r.SurfaceOk && r.AtmOk ? _ok : _warn);
            Row("Orbital period",     _formatTime(r.TE));
            Row("Carrier burn",       $"{Ms(Math.Abs(r.CarrierDv))} {ReleasePoint.Direction(r.CarrierDv)} at {point}");
            Row("Each satellite",     $"{Ms(Math.Abs(r.SatelliteDv))} {ReleasePoint.Direction(r.SatelliteDv)} at {point}");

            if (!r.SurfaceOk)
                GUILayout.Label($"Periapsis is underground. Minimum target altitude: {Km(r.MinHSurface)}", _warn);
            else if (!r.AtmOk && r.MinHAtm.HasValue)
                GUILayout.Label($"Periapsis is inside the atmosphere. Minimum target altitude: {Km(r.MinHAtm.Value)}", _warn);
            if (!r.SoiOk)
                GUILayout.Label($"The parking orbit's apoapsis leaves {body.Name}'s SOI. " +
                                "Choose a lower altitude or more satellites.", _warn);

            GUILayout.Label("Deployment procedure", _header);
            string te = _formatTime(r.TE);
            GUILayout.Label(
                $"1. Circularize the carrier at {Km(r.HTarget)} and release SAT-1 (it stays there).\n" +
                $"2. Burn {ReleasePoint.Direction(r.CarrierDv)} {Ms(Math.Abs(r.CarrierDv))}: the {other} goes to " +
                $"{Km(inner ? r.HPeri : r.HApo)}.\n" +
                $"3. Every parking orbit ({te}), at {point}, release the next satellite; it burns " +
                $"{ReleasePoint.Direction(r.SatelliteDv)} {Ms(Math.Abs(r.SatelliteDv))} to circularize.\n" +
                $"Result: {r.Satellites} satellites, {r.SpacingDeg:F0}° apart, after {r.Satellites - 1} parking orbits.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string AtmHint(Body body) =>
            body.Atm > 0.0 ? $" (atmosphere to {Km(body.Atm)})" : "";

        private static string SourceLabel(AltitudeSource s, string childWord) => s switch
        {
            AltitudeSource.Synchronous => "Synchronous",
            AltitudeSource.Child       => $"At a {childWord}",
            AltitudeSource.Midpoint    => "Midpoint",
            _                          => "Custom",
        };

        private static string StatusText(Body body, AltitudeOption o) => o.Status switch
        {
            AltitudeStatus.BelowSurface     => $"{Km(o.Altitude)} is below the surface.",
            AltitudeStatus.InsideAtmosphere => $"{Km(o.Altitude)} is inside the atmosphere{AtmHint(body)}.",
            AltitudeStatus.ExceedsSoi       =>
                $"{Km(o.Altitude)} is beyond {body.Name}'s sphere of influence " +
                $"(max {Km((body.Soi ?? 0.0) - body.Radius)}). Choose a lower altitude.",
            _ => "",
        };

        private void Row(string label, string value, GUIStyle? style = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelW));
            GUILayout.Label(value, style ?? GUI.skin.label);
            GUILayout.EndHorizontal();
        }

        private static string Km(double metres) =>
            (metres / 1000.0).ToString("N2", CultureInfo.InvariantCulture) + " km";

        private static string Ms(double v) =>
            v.ToString("N1", CultureInfo.InvariantCulture) + " m/s";

        // Shared with every panel, from the skin.
        private void CreateStyles()
        {
            _ok     = RelayOrbiterSkin.Ok;
            _warn   = RelayOrbiterSkin.Warn;
            _header = RelayOrbiterSkin.Header;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RelayOrbiter.Core;
using UnityEngine;
using Body = RelayOrbiter.Core.CelestialBody;

namespace RelayOrbiter.Plugin
{
    // IMGUI window: pick a body and a target altitude, show the deployment plan.
    // All orbital logic lives in RelayOrbiter.Core; this class only draws state.
    internal sealed class RelayOrbiterWindow
    {
        private const int   WindowId = 0x52454C41;  // "RELA"
        private const float Width    = 460f;
        private const float LabelW   = 230f;

        private readonly IBodyDatabase            _db;
        private readonly IAltitudePlanner         _planner;
        private readonly IConstellationCalculator _calculator;
        private readonly Func<double, string>     _formatTime;
        private readonly List<(Body body, int depth)> _bodyTree;

        private Rect    _rect = new Rect(240f, 120f, Width, 200f);
        private Vector2 _bodyScroll;
        private Body    _body;
        private AltitudeSource _source = AltitudeSource.Synchronous;
        private int     _child, _midA, _midB = 1;
        private string  _customKm = "1000";

        private GUIStyle? _ok, _warn, _header;

        public RelayOrbiterWindow(IBodyDatabase db, IAltitudePlanner planner,
                                  IConstellationCalculator calculator,
                                  Func<double, string> formatTime, string? initialBody)
        {
            _db         = db;
            _planner    = planner;
            _calculator = calculator;
            _formatTime = formatTime;
            _bodyTree   = BuildTree();
            _body       = (initialBody != null ? _db.Find(initialBody) : null) ?? _bodyTree[0].body;
        }

        public bool Visible { get; set; }
        public Rect Rect => _rect;

        public void Draw()
        {
            if (_ok == null) CreateStyles();
            _rect = GUILayout.Window(WindowId, _rect, DrawContents, "Relay Orbiter — 3-satellite constellation",
                                     GUILayout.Width(Width));
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private void DrawContents(int id)
        {
            DrawBodySelector();
            GUILayout.Space(6f);

            var option = DrawAltitudeSelector();
            GUILayout.Space(6f);

            if (option == null) { }
            else if (!option.IsValid) GUILayout.Label(StatusText(option), _warn);
            else                      DrawResult(_calculator.Calculate(_body, option.Altitude));

            GUI.DragWindow();
        }

        private void DrawBodySelector()
        {
            GUILayout.Label("Body", _header);
            _bodyScroll = GUILayout.BeginScrollView(_bodyScroll, GUILayout.Height(130f));
            foreach (var (body, depth) in _bodyTree)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(16f * depth);
                bool selected = body.Name == _body.Name;
                if (GUILayout.Toggle(selected, body.Name, GUI.skin.button) && !selected)
                    SelectBody(body);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private AltitudeOption? DrawAltitudeSelector()
        {
            GUILayout.Label($"Target altitude above {_body.Name}{AtmHint()}", _header);

            var kids = _db.ChildrenOf(_body.Name);
            var sources = new List<AltitudeSource> { AltitudeSource.Synchronous };
            if (kids.Count >= 1) sources.Add(AltitudeSource.Child);
            if (kids.Count >= 2) sources.Add(AltitudeSource.Midpoint);
            sources.Add(AltitudeSource.Custom);
            if (!sources.Contains(_source)) _source = AltitudeSource.Synchronous;

            string childWord = _body.Parent == null ? "planet" : "moon";
            var labels = sources.Select(s => SourceLabel(s, childWord)).ToArray();
            _source = sources[GUILayout.Toolbar(sources.IndexOf(_source), labels)];

            switch (_source)
            {
                case AltitudeSource.Synchronous:
                    var sync = _planner.Synchronous(_body);
                    Row("Synchronous orbit", Km(sync.Altitude));
                    return sync;

                case AltitudeSource.Child:
                {
                    var options = _planner.Children(_body);
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
                    var mid = _planner.Midpoint(_body, kids[_midA], kids[_midB]);
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
                    return _planner.Custom(_body, km * 1000.0);
                }
            }
        }

        private void DrawResult(OrbitalResult r)
        {
            GUILayout.Label("Circular orbit (final satellite orbit)", _header);
            Row("Altitude",         Km(r.HTarget));
            Row("Orbital velocity", Ms(r.VC));
            Row("Orbital period",   _formatTime(r.TC));

            GUILayout.Label("Elliptical parking orbit (carrier)", _header);
            Row("Apoapsis altitude", Km(r.HApo));
            string periNote = !r.SurfaceOk ? "  BELOW SURFACE" : !r.AtmOk ? "  INSIDE ATMOSPHERE" : "";
            Row("Periapsis altitude", Km(r.HPeri) + periNote, r.SurfaceOk && r.AtmOk ? _ok : _warn);
            Row("Orbital period",    _formatTime(r.TE));
            Row("Speed at apoapsis",  Ms(r.VApo));
            Row("Speed at periapsis", Ms(r.VPeri));
            Row("Circularize burn at apoapsis", Ms(r.VC - r.VApo));

            if (!r.SurfaceOk)
                GUILayout.Label($"Periapsis is underground. Minimum target altitude: {Km(r.MinHSurface)}", _warn);
            else if (!r.AtmOk && r.MinHAtm.HasValue)
                GUILayout.Label($"Periapsis is inside the atmosphere. Minimum target altitude: {Km(r.MinHAtm.Value)}", _warn);

            GUILayout.Label("Deployment procedure", _header);
            string te = _formatTime(r.TE);
            GUILayout.Label(
                $"1. Circularize the carrier at {Km(r.HTarget)}.\n" +
                $"2. At apoapsis, burn retrograde to lower periapsis to {Km(r.HPeri)}.\n" +
                $"3. At apoapsis: release SAT-1; it circularizes ({Ms(r.VC - r.VApo)} prograde).\n" +
                $"4. Wait one parking orbit ({te}); release SAT-2 at apoapsis.\n" +
                $"5. Wait one more orbit ({te}); release SAT-3 at apoapsis.\n" +
                "Result: 3 satellites, 120° apart.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SelectBody(Body body)
        {
            _body = body;
            _child = 0; _midA = 0; _midB = 1;
        }

        private List<(Body, int)> BuildTree()
        {
            var tree = new List<(Body, int)>();
            void Add(Body b, int depth)
            {
                tree.Add((b, depth));
                foreach (var kid in _db.ChildrenOf(b.Name)) Add(kid, depth + 1);
            }
            foreach (var root in _db.All().Where(b => b.Parent == null)) Add(root, 0);
            return tree;
        }

        private string AtmHint() =>
            _body.Atm > 0.0 ? $"  (atmosphere ends at {Km(_body.Atm)})" : "";

        private static string SourceLabel(AltitudeSource s, string childWord) => s switch
        {
            AltitudeSource.Synchronous => "Synchronous",
            AltitudeSource.Child       => $"At a {childWord}",
            AltitudeSource.Midpoint    => "Midpoint",
            _                          => "Custom",
        };

        private string StatusText(AltitudeOption o) => o.Status switch
        {
            AltitudeStatus.BelowSurface     => $"{Km(o.Altitude)} is below the surface.",
            AltitudeStatus.InsideAtmosphere => $"{Km(o.Altitude)} is inside the atmosphere{AtmHint()}.",
            AltitudeStatus.ExceedsSoi       =>
                $"{Km(o.Altitude)} is beyond {_body.Name}'s sphere of influence " +
                $"(max {Km((_body.Soi ?? 0.0) - _body.Radius)}). Choose a lower altitude.",
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

        private void CreateStyles()
        {
            _ok     = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(0.55f, 0.95f, 0.55f) } };
            _warn   = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(1.00f, 0.75f, 0.30f) }, wordWrap = true };
            _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        }
    }
}

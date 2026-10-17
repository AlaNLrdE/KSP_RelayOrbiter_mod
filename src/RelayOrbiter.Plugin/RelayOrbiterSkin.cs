using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // MechJeb-like look: Unity's built-in dark skin (what MechJeb's default skin is
    // based on), made more compact. Call Get() from inside OnGUI: GUI.skin is only
    // Unity's built-in skin there.
    internal static class RelayOrbiterSkin
    {
        public const int FontSize = 12;

        private static GUISkin? _skin;

        public static GUISkin Get()
        {
            if (_skin != null) return _skin;

            var s = Object.Instantiate(GUI.skin);
            s.name = "RelayOrbiter";
            foreach (var style in new[] { s.label, s.button, s.toggle, s.textField, s.box, s.window })
                style.fontSize = FontSize;

            s.window.fontStyle = FontStyle.Bold;
            s.window.padding   = new RectOffset(8, 8, 22, 8);
            s.label.padding    = new RectOffset(2, 2, 1, 1);
            s.label.margin     = new RectOffset(2, 2, 1, 1);
            s.button.padding   = new RectOffset(6, 6, 3, 3);
            s.button.margin    = new RectOffset(2, 2, 2, 2);
            s.toggle.margin    = new RectOffset(2, 2, 2, 2);
            s.textField.padding = new RectOffset(4, 4, 2, 2);
            return _skin = s;
        }
    }
}

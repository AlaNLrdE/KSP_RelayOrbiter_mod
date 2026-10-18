using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Opaque, high-contrast dark skin, built in code from flat 1-px-border textures on
    // top of Unity's built-in skin (for its toggle check marks). Call Get() from inside
    // OnGUI: GUI.skin is only Unity's built-in skin there.
    internal static class RelayOrbiterSkin
    {
        public const int FontSize = 13;

        private static readonly Color Background = Hex(0x1E2126);
        private static readonly Color Border     = Hex(0x3A3F47);
        private static readonly Color Control    = Hex(0x2D323A);
        private static readonly Color ControlEdge = Hex(0x4A515C);
        private static readonly Color Hover      = Hex(0x3A414B);
        private static readonly Color Accent     = Hex(0x2F6FD6);
        private static readonly Color AccentEdge = Hex(0x5A93F0);
        private static readonly Color Field      = Hex(0x15181C);
        private static readonly Color Text       = Hex(0xE6E8EB);
        private static readonly Color Dim        = Hex(0x8B929C);

        private static GUISkin? _skin;

        public static GUIStyle Header { get; private set; } = GUIStyle.none;
        public static GUIStyle Ok     { get; private set; } = GUIStyle.none;
        public static GUIStyle Warn   { get; private set; } = GUIStyle.none;

        public static GUISkin Get()
        {
            if (_skin != null) return _skin;

            var s = Object.Instantiate(GUI.skin);
            s.name = "RelayOrbiter";
            foreach (var style in new[] { s.label, s.button, s.toggle, s.textField, s.box, s.window })
            {
                style.fontSize = FontSize;
                SetText(style, Text);
            }

            s.window.normal.background   = Tex(Background, Border);
            s.window.onNormal.background = Tex(Background, AccentEdge);
            s.window.border    = new RectOffset(1, 1, 1, 1);
            s.window.padding   = new RectOffset(10, 10, 24, 10);
            s.window.fontStyle = FontStyle.Bold;

            s.label.padding  = new RectOffset(2, 2, 2, 2);
            s.label.margin   = new RectOffset(2, 2, 1, 1);
            s.label.wordWrap = false;

            // Buttons double as tabs and toolbars: the selected one ("on") is the accent colour.
            s.button.normal.background   = Tex(Control, ControlEdge);
            s.button.hover.background    = Tex(Hover, ControlEdge);
            s.button.active.background   = Tex(Accent, AccentEdge);
            s.button.onNormal.background = Tex(Accent, AccentEdge);
            s.button.onHover.background  = Tex(Accent, AccentEdge);
            s.button.onActive.background = Tex(Accent, AccentEdge);
            s.button.border  = new RectOffset(1, 1, 1, 1);
            s.button.padding = new RectOffset(8, 8, 4, 4);
            s.button.margin  = new RectOffset(2, 2, 2, 2);
            SetText(s.button, Text);
            s.button.onNormal.textColor = Color.white;

            s.textField.normal.background  = Tex(Field, ControlEdge);
            s.textField.hover.background   = Tex(Field, AccentEdge);
            s.textField.focused.background = Tex(Field, AccentEdge);
            s.textField.border  = new RectOffset(1, 1, 1, 1);
            s.textField.padding = new RectOffset(5, 5, 3, 3);

            s.toggle.margin = new RectOffset(2, 2, 3, 3);

            Header = new GUIStyle(s.label) { fontStyle = FontStyle.Bold, normal = { textColor = Hex(0x8CC4FF) } };
            Ok     = new GUIStyle(s.label) { wordWrap = true, normal = { textColor = Hex(0x7FE08A) } };
            Warn   = new GUIStyle(s.label) { wordWrap = true, normal = { textColor = Hex(0xFFB347) } };
            s.box.normal.textColor = Dim;
            return _skin = s;
        }

        private static void SetText(GUIStyle style, Color c)
        {
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = c;
            style.onNormal.textColor = style.onHover.textColor = style.onActive.textColor = style.onFocused.textColor = c;
        }

        // A flat texture with a 1-pixel border, stretched by the style's 1-pixel border.
        private static Texture2D Tex(Color fill, Color border)
        {
            const int size = 4;
            var t = new Texture2D(size, size, TextureFormat.ARGB32, false)
            {
                filterMode = FilterMode.Point,
                hideFlags  = HideFlags.HideAndDontSave,
            };
            for (int y = 0; y < size; ++y)
            for (int x = 0; x < size; ++x)
                t.SetPixel(x, y, x == 0 || y == 0 || x == size - 1 || y == size - 1 ? border : fill);
            t.Apply();
            return t;
        }

        private static Color Hex(int rgb) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }
}

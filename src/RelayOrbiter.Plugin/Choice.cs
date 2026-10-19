using System;
using System.Linq;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // A row of mutually exclusive buttons that never gets cut: next to its label when
    // everything fits on one line, otherwise under it, in as many columns as fit.
    internal static class Choice
    {
        // Width the tab content has (the window's, less its padding and scroll bar).
        public const float ContentWidth = 404f;

        public static int Draw(string? label, int value, string[] options, float labelWidth = 230f,
                               float width = ContentWidth)
        {
            var button = GUI.skin.button;
            float widest = options.Max(o => button.CalcSize(new GUIContent(o)).x) + button.margin.horizontal;
            float all = widest * options.Length;
            if (label != null && labelWidth + all <= width)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(label, GUILayout.Width(labelWidth));
                value = GUILayout.Toolbar(value, options);
                GUILayout.EndHorizontal();
                return value;
            }
            if (label != null) GUILayout.Label(label);
            int columns = Math.Max(1, Math.Min(options.Length, (int)(width / widest)));
            return GUILayout.SelectionGrid(value, options, columns);
        }
    }
}

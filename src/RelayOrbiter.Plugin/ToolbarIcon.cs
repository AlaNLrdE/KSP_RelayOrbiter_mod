using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Draws the 38x38 toolbar icon in code: a planet, an orbit ring and three
    // satellites 120° apart. Avoids shipping and loading a texture file.
    internal static class ToolbarIcon
    {
        private const int Size = 38;

        public static Texture2D Create()
        {
            var tex = new Texture2D(Size, Size, TextureFormat.ARGB32, false);
            var pixels = new Color[Size * Size];
            var planet    = new Color(0.35f, 0.65f, 1.00f, 1f);
            var ring      = new Color(0.80f, 0.80f, 0.80f, 0.8f);
            var satellite = Color.white;
            const float c = (Size - 1) / 2f;
            const float orbitR = 13f;

            for (int y = 0; y < Size; ++y)
            for (int x = 0; x < Size; ++x)
            {
                float dx = x - c, dy = y - c;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                var col = new Color(0f, 0f, 0f, 0f);
                if (d <= 5.5f)                     col = planet;
                else if (Mathf.Abs(d - orbitR) < 0.7f) col = ring;
                pixels[y * Size + x] = col;
            }

            for (int i = 0; i < 3; ++i)
            {
                float a = Mathf.PI / 2f + i * 2f * Mathf.PI / 3f;
                float sx = c + orbitR * Mathf.Cos(a), sy = c + orbitR * Mathf.Sin(a);
                for (int y = 0; y < Size; ++y)
                for (int x = 0; x < Size; ++x)
                    if ((x - sx) * (x - sx) + (y - sy) * (y - sy) <= 3.2f * 3.2f)
                        pixels[y * Size + x] = satellite;
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}

using UnityEngine;

namespace QuestSaber
{
    /// <summary>Shared UI helpers (deterministic, with no dependency on imported sprites).</summary>
    public static class BsUi
    {
        private static Sprite _white;
        private static Sprite _circle;
        private static Sprite _rounded;

        // ---- Hand menu color palette: dark, slightly bluish glass + green accent ----
        /// <summary>Base surface of the panel.</summary>
        public static readonly Color PanelColor = new Color(0.055f, 0.065f, 0.105f, 0.96f);
        /// <summary>Bright border of the panel/card.</summary>
        public static readonly Color PanelEdge = new Color(0.42f, 0.86f, 1f, 0.42f);
        /// <summary>Background bar of the title row.</summary>
        public static readonly Color TitleBarColor = new Color(0.38f, 1f, 0.55f, 0.14f);
        /// <summary>Base surface of normal rows (subtle, so they stand out from the panel).</summary>
        public static readonly Color RowColor = new Color(1f, 1f, 1f, 0.045f);
        /// <summary>Base surface of the control fields (-/+/◄/►) when they are NOT targeted.</summary>
        public static readonly Color ChipColor = new Color(1f, 1f, 1f, 0.10f);
        /// <summary>Control field under the laser (rich green).</summary>
        public static readonly Color ChipHiColor = new Color(0.38f, 1f, 0.55f, 0.85f);
        /// <summary>Accent color for icons/glyphs.</summary>
        public static readonly Color AccentColor = new Color(0.38f, 1f, 0.55f, 1f);
        /// <summary>Label text of the control cards (muted).</summary>
        public static readonly Color LabelColor = new Color(0.72f, 0.80f, 0.92f, 1f);
        /// <summary>Value text (bright).</summary>
        public static readonly Color ValueColor = new Color(1f, 1f, 1f, 1f);
        /// <summary>Row highlight (under the laser) - deliberately neutral-transparent.</summary>
        public static readonly Color CursorColor = new Color(1f, 1f, 1f, 0.16f);

        public static Sprite WhiteSprite()
        {
            if (_white != null) return _white;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                name = "BsUiWhite",
            };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            _white = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 100f);
            return _white;
        }

        public static Sprite CircleSprite()
        {
            if (_circle != null) return _circle;
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "BsUiCircle" };
            var pixels = new Color[size * size];
            float radius = size * 0.5f - 1f;
            var center = new Vector2(size * 0.5f, size * 0.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float alpha = Mathf.Clamp01(radius - dist);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            _circle = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            return _circle;
        }

        public static Sprite RoundedSprite()
        {
            if (_rounded != null) return _rounded;
            const int size = 64;
            const float radius = 18f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "BsUiRounded" };
            var pixels = new Color[size * size];
            var half = new Vector2(size * 0.5f, size * 0.5f);
            var inner = new Vector2(half.x - radius, half.y - radius);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new Vector2(Mathf.Abs(x + 0.5f - half.x), Mathf.Abs(y + 0.5f - half.y)) - inner;
                    float outside = Vector2.Max(p, Vector2.zero).magnitude;
                    float inside = Mathf.Min(Mathf.Max(p.x, p.y), 0f);
                    float dist = outside + inside - radius;
                    // The central overlap lies at dist <= -radius (clamped at the bottom); the
                    // corner SDF yields <= 0 there. Anti-aliasing over 1 px.
                    float alpha = Mathf.Clamp01(0.5f - dist);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            _rounded = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f,
                0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            return _rounded;
        }

        public static Font Font()
        {
            try { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch { return null; }
        }
    }
}

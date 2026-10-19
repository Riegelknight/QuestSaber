using System.Collections.Generic;
using UnityEngine;

namespace QuestSaber
{
    public sealed class GripCurvedLabel : MonoBehaviour
    {
        [Tooltip("Font for the text (empty = BsUi-LegacyRuntime).")]
        public Font font;
        public int fontSize = 64;
        [Tooltip("Cap height in meters.")]
        public float capHeight = 0.008f;
        [Tooltip("Distance to the grip axis in meters (0 = flat). Set at runtime from " +
                 "GameConfig.GripLabelRadiusWorld so the text lies on the measured grip surface.")]
        public float curveRadius = GameConfig.GripLabelRadiusWorld;
        [Tooltip("Maximum arc around the grip; long names are otherwise scaled down.")]
        public float maxArcDegrees = 150f;
        public Color color = Color.white;
        [Tooltip("Fineness of the curvature (strips per glyph).")]
        [Range(1, 24)] public int subdivisions = 8;
        [Tooltip("Draw order (0 = light edge, 1 = engraving in front of it).")]
        public int sortOrder = 0;

        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private Material _material;
        private Mesh _mesh;
        private string _text = "";
        private bool _onFontRebuild;   // recursion guard (textureRebuilt -> Rebuild)

        public string Text { get { return _text; } }

        private void OnEnable()
        {
            Font.textureRebuilt += OnFontTextureRebuilt;
        }

        private void OnDisable()
        {
            Font.textureRebuilt -= OnFontTextureRebuilt;
        }

        private void OnFontTextureRebuilt(Font rebuilt)
        {
            if (_onFontRebuild) return;
            Font f = font != null ? font : BsUi.Font();
            if (rebuilt == null || f == null || rebuilt != f) return;
            _onFontRebuild = true;
            try
            {
                RefreshAtlas();                  // material follows the new atlas texture
                if (_text.Length > 0) Rebuild(); // metrics are now valid -> build mesh
            }
            finally
            {
                _onFontRebuild = false;
            }
        }

        private void EnsureRefs()
        {
            if (_filter == null) _filter = GetComponent<MeshFilter>();
            if (_filter == null) _filter = gameObject.AddComponent<MeshFilter>();
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
            if (_renderer == null) _renderer = gameObject.AddComponent<MeshRenderer>();
            if (_mesh == null)
            {
                _mesh = new Mesh();
                _mesh.name = "GripLabelMesh";
                _mesh.MarkDynamic();
            }
            if (_filter.sharedMesh != _mesh) _filter.sharedMesh = _mesh;
            if (_material == null) _material = MakeFontMaterial();
            if (_renderer.sharedMaterial != _material) _renderer.sharedMaterial = _material;
        }

        private Material MakeFontMaterial()
        {
            // Copy the dynamic font's material: it already contains the built-in
            // "GUI/Text Shader" (alpha atlas, vertex color) and is thus safe in the build.
            Font f = font != null ? font : BsUi.Font();
            Material src = f != null ? f.material : null;
            Shader sh = src != null ? src.shader : Shader.Find("GUI/Text Shader");
            if (sh == null) sh = Shader.Find("Unlit/Transparent");
            var mat = new Material(sh);
            mat.name = "GripLabelFont";
            if (src != null)
            {
                mat.CopyPropertiesFromMaterial(src);
                mat.mainTexture = src.mainTexture;
            }
            mat.renderQueue = 3000 + Mathf.Clamp(sortOrder, 0, 10);
            return mat;
        }

        /// <summary>Sets the text and rebuilds the mesh only on change.</summary>
        public void SetText(string value)
        {
            if (value == null) value = "";
            if (_text == value)
            {
                RefreshAtlas();
                return;
            }
            _text = value;
            Rebuild();
        }

        /// <summary>Rebuilds the mesh (e.g. after changing color/size in the editor).</summary>
        public void Rebuild()
        {
            EnsureRefs();
            Font f = font != null ? font : BsUi.Font();
            font = f;
            if (f == null || _mesh == null) return;
            f.RequestCharactersInTexture(_text, fontSize, FontStyle.Normal);
            RefreshAtlas();
            GripLabelMesh.Build(_mesh, f, _text, fontSize, capHeight, curveRadius,
                maxArcDegrees, color, subdivisions);
        }

        private void RefreshAtlas()
        {
            Font f = font != null ? font : BsUi.Font();
            if (_material != null && f != null && f.material != null)
            {
                Texture tex = f.material.mainTexture;
                if (tex != null && _material.mainTexture != tex) _material.mainTexture = tex;
            }
        }
    }

    /// <summary>Builds the curved text mesh (subdivided glyphs, meter units).</summary>
    internal static class GripLabelMesh
    {
        private struct Glyph
        {
            public float x0, x1, y0, y1;
            public Vector2 uvBL, uvBR, uvTL, uvTR;
        }

        public static void Build(Mesh mesh, Font font, string text, int fontSize,
            float capHeight, float curveRadius, float maxArcDegrees, Color color, int subdivisions)
        {
            mesh.Clear();
            if (font == null || string.IsNullOrEmpty(text)) return;
            // Add reference 'H' for the cap height to the atlas (the text does not contain it).
            font.RequestCharactersInTexture("H", fontSize, FontStyle.Normal);

            var glyphs = new List<Glyph>();
            float pen = 0f;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (char c in text)
            {
                CharacterInfo ci;
                if (!font.GetCharacterInfo(c, out ci, fontSize, FontStyle.Normal)) continue;
                // TIGHT ink bounds: `minY/maxY` return the em box (incl. ascender/descender)
                // and make the letters unnecessarily small; `vert` is the tight glyph box.
#pragma warning disable 0618
                Rect v = ci.vert;
#pragma warning restore 0618
                var g = new Glyph();
                g.x0 = pen + v.xMin;
                g.x1 = pen + v.xMax;
                g.y0 = Mathf.Min(v.yMin, v.yMax);
                g.y1 = Mathf.Max(v.yMin, v.yMax);
                g.uvBL = ci.uvBottomLeft;
                g.uvBR = ci.uvBottomRight;
                g.uvTL = ci.uvTopLeft;
                g.uvTR = ci.uvTopRight;
                glyphs.Add(g);
                if (g.x0 < minX) minX = g.x0;
                if (g.x1 > maxX) maxX = g.x1;
                if (g.y0 < minY) minY = g.y0;
                if (g.y1 > maxY) maxY = g.y1;
                pen += ci.advance;
            }
            if (glyphs.Count == 0) return;

            float rawH = Mathf.Max(0.0001f, maxY - minY);

            // Reference CAP HEIGHT from 'H' (tight box, without descenders). Without it the
            // ascender/descender union of the word (e.g. 'g' with a descender) would scale the letters
            // too small - the capitals would no longer be `capHeight` tall.
            float capUnits = rawH;
#pragma warning disable 0618
            CharacterInfo hci;
            if (font.GetCharacterInfo('H', out hci, fontSize, FontStyle.Normal))
            {
                float hh = Mathf.Abs(hci.vert.height);
                if (hh > 1f) capUnits = hh;
            }
#pragma warning restore 0618

            bool curved = curveRadius > 0.001f;
            float cap = Mathf.Max(0.0005f, capHeight);
            // The curvature wraps the TEXT HEIGHT around the grip. So that the ends do not
            // slip onto the back, the height is limited to the allowed arc.
            if (curved && maxArcDegrees > 1f)
            {
                float maxCap = maxArcDegrees * Mathf.Deg2Rad * curveRadius;
                if (maxCap < cap) cap = maxCap;
            }
            float scale = cap / capUnits;                    // font units -> meters
            float R = curved ? curveRadius / scale : 1f;     // wrap radius in font units
            float cx = (minX + maxX) * 0.5f;
            float cy = (minY + maxY) * 0.5f;

            int sub = Mathf.Clamp(subdivisions, 1, 24);
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color>();
            var tris = new List<int>();

            for (int gi = 0; gi < glyphs.Count; gi++)
            {
                Glyph g = glyphs[gi];
                int rows = curved ? sub : 1;   // strips along the text height (wrap direction)
                int baseIdx = verts.Count;
                for (int r = 0; r <= rows; r++)
                {
                    float fy = (float)r / rows;
                    float y = Mathf.Lerp(g.y0, g.y1, fy);
                    for (int cc = 0; cc <= 1; cc++)
                    {
                        float x = (cc == 0) ? g.x0 : g.x1;
                        Vector2 uv = Vector2.Lerp(
                            Vector2.Lerp(g.uvBL, g.uvBR, cc),
                            Vector2.Lerp(g.uvTL, g.uvTR, cc), fy);
                        Vector3 p = curved
                            ? Curved(x - cx, y - cy, R)
                            : new Vector3(x - cx, y - cy, 0f);
                        verts.Add(p * scale);
                        uvs.Add(uv);
                        colors.Add(color);
                    }
                }
                for (int r = 0; r < rows; r++)
                {
                    int i0 = baseIdx + r * 2;
                    int i1 = i0 + 1;
                    int i2 = i0 + 2;
                    int i3 = i2 + 1;
                    tris.Add(i0); tris.Add(i2); tris.Add(i1);
                    tris.Add(i1); tris.Add(i2); tris.Add(i3);
                }
            }

            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
        }

        private static Vector3 Curved(float a, float b, float R)
        {
            // The READING DIRECTION (a) stays straight; the TEXT HEIGHT (b) is wrapped around the
            // local X axis (after the root rotation: the grip axis). Curvature toward +Z
            // (toward the axis) so that the text lies exactly on the cylinder.
            float phi = b / R;
            return new Vector3(a, R * Mathf.Sin(phi), R * (1f - Mathf.Cos(phi)));
        }
    }
}

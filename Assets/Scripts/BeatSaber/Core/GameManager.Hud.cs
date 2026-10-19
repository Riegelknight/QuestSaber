// GameManager: grip label, HUD and menu construction.
using System;
using UnityEngine;

namespace QuestSaber
{
    public sealed partial class GameManager
    {
        // ======================================================================
        // Build UI
        // ======================================================================
        private void BuildUi()
        {
            var hudGo = new GameObject("FloorHUD");
            _hud = hudGo.AddComponent<HudDisplay>();
            if (_cam != null) _hud.Build(_cam);
            // Score display belongs to the ROOM, not to the player: rigidly attached to the game world root
            // => no billboard/head tracking. `_gameRoot` stands at Y 0 = the
            // floor reported by the headset, so the HUD begins at floor level.
            hudGo.transform.SetParent(_gameRoot, false);
            // CLEARLY above the floor plane: at 0.08 the lower edge of the 45-degree inclined
            // panel lay practically ON the floor (and the combo row still ran below it) -
            // reported as "display half in the floor". 0.30 m (x room scaling) lets
            // the whole disc stand free; at ScaleMin 0.5 there is still ~11 cm of clearance.
            hudGo.transform.localPosition = new Vector3(0f, 0.30f, 0.7f);
            // Visual: inclined 45 degrees backward (top edge away from the player) - like a
            // raised shelf, so the indicator standing on the floor is comfortably readable.
            hudGo.transform.localRotation = Quaternion.Euler(45f, 0f, 0f);

            var anchor = _leftAnchor != null ? _leftAnchor : transform;
            var menuGo = new GameObject("HandMenu");
            _menu = menuGo.AddComponent<HandMenu>();
            _menu.Build(anchor, _cam);

            // 2) GRIP labels LAST and ENCAPSULATED: pure cosmetics. An error here is
            //    caught - menu/HUD/game continue anyway.
            try
            {
                if (_cam != null)
                {
                    // ENGRAVED on the grip (not floating, no billboard). Fallback without a
                    // saber: label at the controller anchor with a fixed hand position.
                    bool onNameGrip = _saberL != null && _saberL.LabelAnchor != null;
                    bool onBestGrip = _saberR != null && _saberR.LabelAnchor != null;
                    Transform nameParent = onNameGrip ? _saberL.LabelAnchor
                        : (_leftAnchor != null ? _leftAnchor : transform);
                    Transform bestParent = onBestGrip ? _saberR.LabelAnchor
                        : (_rightAnchor != null ? _rightAnchor : transform);
                    _labelName = CreateLabel(nameParent, true, onNameGrip);
                    _labelBest = CreateLabel(bestParent, false, onBestGrip);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Grip label construction failed (game continues): "
                                 + ex.Message);
            }
        }

        // ---- Grip label: engraved (no billboard, no floating) ----
        // Dark engraving IN FRONT, bright light edge behind it - this makes the text appear
        // recessed into the grip instead of printed/floating.
        private static readonly Color EngraveNameDark = new Color(0.12f, 0.02f, 0.02f, 0.95f);
        private static readonly Color EngraveNameLight = new Color(1f, 0.55f, 0.5f, 0.45f);
        private static readonly Color EngraveBestDark = new Color(0.02f, 0.05f, 0.16f, 0.95f);
        private static readonly Color EngraveBestLight = new Color(0.55f, 0.78f, 1f, 0.45f);

        private const int GripLabelFontSize = 64;
        private const float GripLabelCapHeight = 0.0275f;
        // Compression across the reading direction (the font is pulled slightly narrower so
        // that e.g. "Riegholm" fits on the short grip).
        private const float GripLabelWidthScale = 0.72f;
        // Wrap radius = the grip's measured surface radius (GameConfig) so the text lies ON
        // the handle instead of floating above it. Keep in sync with the label
        // anchor in SaberView.Build - both come from GameConfig now.
        private const float GripCurveRadius = GameConfig.GripLabelRadiusWorld;
        private const float GripLabelMaxArcDegrees = 150f;

        private GripCurvedLabel CreateLabel(Transform parent, bool left, bool onGrip)
        {
            Color dark = left ? EngraveNameDark : EngraveBestDark;
            Color light = left ? EngraveNameLight : EngraveBestLight;

            Transform root;
            if (onGrip)
            {
                // On the grip, the origin lies exactly on the grip surface; position/scaling
                // come from the (axis-aligned) label anchor of the saber. The root rotates the
                // reading direction onto the grip axis (90 degrees left / 270 degrees right, mirrored) and
                // compresses slightly across. The glyph height wraps the mesh around the grip
                // (`curved: true`) - this makes the text lie on the curvature instead of flat.
                root = new GameObject("GripLabel").transform;
                root.SetParent(parent, false);
                root.localPosition = Vector3.zero;
                root.localRotation = Quaternion.Euler(0f, 0f, left ? 90f : 270f);
                root.localScale = new Vector3(GripLabelWidthScale, 1f, 1f);
                // Light edge first: slightly lower/offset, so that the dark engraving lies in front
                // and only the edge shows through.
                MakeLabelLayer(root, "LayerLight", light,
                    new Vector3(0.0006f, -0.0006f, 0.0005f), curved: true, sortOrder: 0);
            }
            else
            {
                // Fallback without a saber: at the controller anchor, fixed hand position, flat.
                root = new GameObject("HandLabel").transform;
                root.SetParent(parent, false);
                root.localPosition = new Vector3(left ? -0.02f : 0.02f, 0.06f, 0.05f);
                root.localRotation = Quaternion.identity;
                root.localScale = Vector3.one;
                MakeLabelLayer(root, "LayerLight", light,
                    new Vector3(0.0006f, -0.0006f, 0.0002f), curved: false, sortOrder: 0);
            }
            return MakeLabelLayer(root, "LayerDark", dark, Vector3.zero, curved: onGrip, sortOrder: 1);
        }

        private static GripCurvedLabel MakeLabelLayer(Transform parent, string name, Color color,
            Vector3 localPos, bool curved, int sortOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            var label = go.AddComponent<GripCurvedLabel>();
            label.font = BsUi.Font();
            label.fontSize = GripLabelFontSize;
            label.capHeight = GripLabelCapHeight;
            label.curveRadius = curved ? GripCurveRadius : 0f;
            label.maxArcDegrees = GripLabelMaxArcDegrees;
            label.color = color;
            label.sortOrder = sortOrder;
            return label;
        }

        /// <summary>Writes a text into ALL layers (engraving + light edge) of the label.</summary>
        private static void SetLabelText(GripCurvedLabel main, string value)
        {
            if (main == null) return;
            main.SetText(value);
            var root = main.transform.parent;
            if (root == null) return;
            var layers = root.GetComponentsInChildren<GripCurvedLabel>(true);
            for (int i = 0; i < layers.Length; i++)
                if (layers[i] != main) layers[i].SetText(value);
        }

        /// <summary>
        /// Updates the engraved grip labels: on the left the name of the active player,
        /// on the right their best score for the CURRENT song. Runs every frame with text comparison
        /// and thus also takes effect immediately after a record (without a song change) - no
        /// "dirty" flag, no billboard/turning toward the head anymore.
        /// </summary>
        private void UpdateLabels()
        {
            if (_labelName == null || _labelBest == null) return;

            string name = _player != null ? Shorten(_player.Name, 12) : "";
            if (name != _labelNameText)
            {
                _labelNameText = name;
                SetLabelText(_labelName, name);
                // Diagnostic: only on an actual change, so the log shows what the
                // engraved grip text reads after e.g. deleting a player. Truncated names are
                // reported too (a shortened name is not a rendering bug).
                Debug.Log($"[QuestSaber] Grip name label = '{name}' " +
                          $"(player='{(_player != null ? _player.Name : "-")}'" +
                          $"{(_player != null && _player.Name != name ? ", shortened" : "")}).");
            }

            string best = "";
            if (_player != null && _current != null)
            {
                int b = _player.GetBest(_current.BestKey);
                if (b > 0) best = b.ToString("N0");
            }
            if (best != _labelBestText)
            {
                _labelBestText = best;
                SetLabelText(_labelBest, best);
            }
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

    }
}

using UnityEngine;
using UnityEngine.UI;

namespace QuestSaber
{
    /// <summary>
    /// Floor display (round, windowless, scale ~0.0008 per specification):
    /// current score, multiplier x1-x8, combo and progress to the NEXT
    /// multiplier tier (hits within the tier / hits required).
    /// Attached to the game-world root (BeatSaberWorld) and thus oriented to the ORIENTATION OF THE
    /// ROOM (fixed position/rotation), not to the orientation of the player:
    /// It no longer follows the head and stays in place on the room X/Z reset (key A).
    /// </summary>
    public sealed class HudDisplay : MonoBehaviour
    {
        private Canvas _canvas;
        private RectTransform _panel;
        private Text _scoreText;
        private Text _comboText;

        public void Build(Camera cam)
        {
            name = "FloorHUD";
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            // Canvas unit: scale ~0.0008 => 1 canvas unit = 0.0008 m
            transform.localScale = new Vector3(0.0008f, 0.0008f, 0.0008f);

            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.AddComponent<RectTransform>();
            _panel.sizeDelta = new Vector2(900f, 320f);

            var img = panelGo.AddComponent<Image>();
            img.sprite = BsUi.CircleSprite();
            img.color = new Color(0f, 0f, 0f, 0.55f);

            var font = BsUi.Font();

            _scoreText = CreateText("Score", panelGo.transform, font, new Vector2(860f, 150f),
                new Vector2(0f, 75f), 110, "SCORE 0000000", Color.white);
            _comboText = CreateText("Combo", panelGo.transform, font, new Vector2(860f, 140f),
                new Vector2(0f, -80f), 56, "COMBO 0  x1\n[░░░░░░░░░░]", new Color(1f, 0.85f, 0.4f));
        }

        private static Text CreateText(
            string name, Transform parent, Font font, Vector2 size, Vector2 anchoredPos,
            int fontSize, string content, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = size;
            rt.anchoredPosition = anchoredPos;

            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = content;
            text.color = color;
            return text;
        }

        public void Show(bool visible) => _canvas.enabled = visible;

        public void SetScore(int score, int combo, int multiplier, float progress)
        {
            if (_scoreText == null || _comboText == null) return;
            _scoreText.text = "SCORE " + score.ToString("N0");

            int progressDots = Mathf.Clamp(Mathf.RoundToInt(progress * 10f), 0, 10);
            string dots = new string('█', progressDots) + new string('░', 10 - progressDots);
            _comboText.text = $"COMBO {combo}  x{multiplier}\n[{dots}]";
        }
    }
}

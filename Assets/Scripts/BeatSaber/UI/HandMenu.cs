using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace QuestSaber
{
    /// <summary>
    /// Hand menu (fixed to the wrist, follows the controller movement):
    /// a row list with a cursor; the OTHER hand points at the panel with its lightsaber
    /// (pointer beam). Confirmation is done with the index trigger
    /// (the side grip is reserved for the Easter Egg).
    /// The menu itself is display only - the logic (what rows do) lives in the GameManager.
    /// For long lists (song list) there is a scroll window; paging is done with the joystick.
    /// </summary>
    public sealed class HandMenu : MonoBehaviour
    {
        public struct Row
        {
            public string Text;
            public int Id;          // -1 = not selectable
            public bool Selectable;
            public float Height;    // 0 = default row height
            public bool Title;      // title row (larger, bold)
            public Color? Background; // e.g. blue bar behind the active player
            public int FontSize;    // 0 = default (48); smaller for long value rows
            public bool MultiControl; // control card with fields (-/+ or ◄/►)
            public string Value;    // value (control card centered or right-aligned with RightValue)
            public bool Cycle;      // control card with cycle arrows ◄/► instead of -/+
            public bool Button;     // stored as a button (subtle gray, like the control cards)
            public bool RightValue; // text left-aligned, value right-aligned (e.g. player scores)
            public bool Card;       // subtle gray background (like the control cards)
            public bool Deletable;  // song list: row may be deleted via the "X" (never the demo song)
        }

        private const float TitleHeight = 96f;
        private const float ControlRowHeight = 96f; // two-line control card (label + value/fields)
        private const int SongListViewport = 7;   // visible song rows in the list submenu

        // Control card: fixed positions of the fields left/right (panel-local relative to the row center)
        private const float ControlChipX = 160f;
        private const float ControlChipY = -16f;
        private static readonly Vector2 ControlChipSize = new Vector2(74f, 50f);

        public bool Visible { get; private set; }
        public int Cursor { get; private set; }

        /// <summary>Hit X of the laser point in the panel (local, left negative / right positive).</summary>
        public float LastPointerX => _lastPointerX;

        private Canvas _canvas;
        private RectTransform _panel;
        private Image _cursorHighlight;
        private Image _controlHighlight;   // highlight of the active control field (-/+ or ◄/►)
        private readonly List<GameObject> _rowObjects = new();
        private readonly List<Row> _rows = new();
        private readonly List<Text> _rowValueTexts = new(); // value text per row (null = no value display)

        // Modal confirmation dialog (e.g. delete player): its OWN overlay canvas that
        // lies CENTERED over the still visible panel. The background is only slightly darkened.
        private Transform _confirmRoot;
        private Image _confirmScrim;
        private Image _confirmCard;
        private Text _confirmTitle;
        private Text _confirmDetail;
        private Image _confirmYesBg, _confirmNoBg;
        private Text _confirmYesText, _confirmNoText;
        private bool _confirmVisible;
        public bool ConfirmVisible => _confirmVisible;
        /// <summary>0 = Yes, 1 = No - the selection in the dialog last highlighted by the laser.</summary>
        public int ConfirmChoice { get; private set; } = 1;
        private const float ConfirmW = 660f;
        private const float ConfirmH = 320f;
        private static readonly Vector2 ConfirmNoPos = new Vector2(-165f, -95f);
        private static readonly Vector2 ConfirmYesPos = new Vector2(165f, -95f);
        private static readonly Vector2 ConfirmBtnSize = new Vector2(280f, 76f);

        // Layout per (global) row index - only visible rows are filled.
        // Deliberately NOT limited to 64: player management is (unlike the song list)
        // not scrollable; with many profiles it could otherwise overflow the fixed length.
        // RefreshGeometry grows the buffers as needed.
        private float[] _rowCenterY = new float[64];
        private float[] _rowHeights = new float[64];
        private LineRenderer _laser;
        private Image _reticle;
        private Transform _handAnchor;
        private Camera _cam;
        private float _rowHeight = 72f;
        private float _lastPointerX;    // local hit X (for (-)/(+) rows)
        // Laser hit (panel-local). Updated EVERY frame when the laser hits the
        // panel. In the song list it re-determines the row under the laser - even
        // when the list moves away under the resting laser (joystick scroll).
        private Vector2 _lastPointerLocal;
        private bool _hasPointerLocal;

        // Marquee of the song names (only the song list submenu): only the row under the laser
        // scrolls through, so that a long name remains readable despite the clipped window width.
        // The text is clipped to the LEFT part of the row (SongTextWidth, shifted left by
        // SongTextClipX) to leave room at the far right for the per-row delete "X".
        private const float SongTextWidth = 470f;   // visible text width (= mask width)
        private const float SongTextClipX = -40f;   // clip container shifted left -> room for the X
        private const float MarqueeSpeedPx = 70f;   // scroll speed
        private float _marqueeTimer;

        // Per-row delete "X" in the song list shown at the far right of the row under
        // the laser. DIM/subtle red while only the row is hovered, BRIGHT red as soon as the
        // laser points straight at the X. Drawn from two crossed bars (no font glyph needed).
        private const float SongDeleteSize = 52f;
        private const float SongDeleteVisualRadius = 16f; // bars are 41.6 px long, ±45° -> ±15 px visual extent
        private const float SongDeleteCenterX = 242f;
        private static readonly Color SongDeleteDim = new Color(0.85f, 0.30f, 0.30f, 0.40f);
        private static readonly Color SongDeleteHot = new Color(1.00f, 0.16f, 0.16f, 1.00f);
        private RectTransform _songDeleteIcon;
        private Image _songDeleteBarA, _songDeleteBarB;
        private bool _pointerOnSongDelete;

        /// <summary>True while the laser of the menu hand points straight at the delete "X"
        /// of the hovered song row. The GameManager then opens the "Delete song?" dialog
        /// instead of selecting the song (see OnMenuAction/HandleMenuInput).</summary>
        public bool PointerOnSongDeleteIcon => _pointerOnSongDelete;
        private sealed class SongTextRef
        {
            public int Index;
            public Text Text;
            public float Overflow;
            public bool Measured;
        }
        private readonly List<SongTextRef> _songTextRefs = new();

        // Scroll state (only the song list submenu)
        private bool _scrollable;
        // Visible rows of the scroll window. Derived (if a target height is passed)
        // from the height of the main menu, so that the song list is EXACTLY as tall.
        private int _viewportRows = SongListViewport;
        private float _targetPanelHeight; // 0 = natural height (no padding)
        // First content row index shown at DISPLAY position 0. In the song list
        // cyclic (modulo song count) - the list has no end, the songs repeat.
        private int _scrollOffset;
        // Row under the laser in the song list (-1 = none). The song list deliberately has
        // NO persistent selection: only the row currently targeted is highlighted in green.
        private int _hoverRow = -1;

        // ---- Position/orientation of the panel ----
        // The panel is ALWAYS above the controller; the distance between the controller and the
        // panel's BOTTOM EDGE is fixed (a little clearance in between). It faces the headset but is
        // tilted slightly backward, and sits farther away due to the forward offset.
        private const float MenuBottomClearance = 0.06f; // m clearance between controller and bottom edge
        private const float MenuForwardOffset = 0.10f;   // m farther away (+ view direction)
        private const float MenuTiltDeg = -10f;           // tilt: 20° minus 30° = 350° (-10°)

        public void Build(Transform handAnchor, Camera cam)
        {
            _handAnchor = handAnchor;
            _cam = cam;
            name = "HandMenu";
            transform.SetParent(handAnchor, false);

            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            transform.localScale = new Vector3(0.0004f, 0.0004f, 0.0004f);
            _canvas.enabled = false;
            Visible = false;

            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.AddComponent<RectTransform>();
            var img = panelGo.AddComponent<Image>();
            img.sprite = BsUi.RoundedSprite();
            img.type = Image.Type.Sliced;
            img.color = BsUi.PanelColor;
            var panelOutline = panelGo.AddComponent<Outline>();
            panelOutline.effectColor = BsUi.PanelEdge;
            panelOutline.effectDistance = new Vector2(3f, 3f);

            _cursorHighlight = new GameObject("Cursor").AddComponent<Image>();
            _cursorHighlight.transform.SetParent(_panel, false);
            _cursorHighlight.sprite = BsUi.RoundedSprite();
            _cursorHighlight.type = Image.Type.Sliced;
            _cursorHighlight.color = BsUi.CursorColor;
            _cursorHighlight.rectTransform.sizeDelta = new Vector2(560f, _rowHeight - 10f);

            // Highlight of the control field of a control card currently targeted.
            _controlHighlight = new GameObject("ControlField").AddComponent<Image>();
            _controlHighlight.transform.SetParent(_panel, false);
            _controlHighlight.sprite = BsUi.RoundedSprite();
            _controlHighlight.type = Image.Type.Sliced;
            _controlHighlight.color = BsUi.ChipHiColor;
            _controlHighlight.rectTransform.sizeDelta = ControlChipSize + new Vector2(4f, 4f);
            _controlHighlight.enabled = false;

            // Per-row delete "X" (song list): a container with two crossed bars, hidden until
            // the row under the laser is deletable (see RefreshSongDeleteIcon).
            var delGo = new GameObject("SongDeleteX");
            delGo.transform.SetParent(_panel, false);
            _songDeleteIcon = delGo.AddComponent<RectTransform>();
            CenterAnchors(_songDeleteIcon);
            _songDeleteIcon.sizeDelta = new Vector2(SongDeleteSize, SongDeleteSize);
            _songDeleteBarA = MakeDeleteBar(delGo.transform, 45f);
            _songDeleteBarB = MakeDeleteBar(delGo.transform, -45f);
            delGo.SetActive(false);

            BuildConfirmUi();

            // Pointer laser: points from the other hand to the menu panel
            var laserGo = new GameObject("PointerLaser");
            laserGo.transform.SetParent(transform, false);
            _laser = laserGo.AddComponent<LineRenderer>();
            _laser.positionCount = 2;
            _laser.startWidth = 0.004f;
            _laser.endWidth = 0.0015f;
            _laser.useWorldSpace = true;
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            _laser.material = new Material(sh);
            _laser.startColor = _laser.endColor = new Color(1f, 1f, 1f, 0.9f);
            _laser.enabled = false;

            // Reticle at the laser end point (standard MR look: laser + crosshair)
            _reticle = new GameObject("LaserReticle").AddComponent<Image>();
            _reticle.transform.SetParent(_panel, false);
            _reticle.sprite = BsUi.CircleSprite();
            _reticle.color = new Color(1f, 1f, 1f, 0.95f);
            _reticle.rectTransform.sizeDelta = new Vector2(40f, 40f);
            _reticle.enabled = false;
        }

        /// <summary>Creates a text as a child (center-anchored, fixed size/position).</summary>
        private static Text MakeText(Transform parent, string text, int fontSize, TextAnchor align,
            Color color, Vector2 size, Vector2 pos)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = BsUi.Font();
            t.fontSize = fontSize;
            t.alignment = align;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.color = color;
            t.text = text;
            t.rectTransform.sizeDelta = size;
            CenterAnchors(t.rectTransform);
            t.rectTransform.anchoredPosition = pos;
            return t;
        }

        private static Image MakeRounded(Transform parent, Color color, Vector2 size, Vector2 pos)
        {
            var go = new GameObject("Rounded");
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = BsUi.RoundedSprite();
            img.type = Image.Type.Sliced;
            img.color = color;
            img.rectTransform.sizeDelta = size;
            CenterAnchors(img.rectTransform);
            img.rectTransform.anchoredPosition = pos;
            return img;
        }

        /// <summary>
        /// Centers the anchors of a UI element (midpoint = center of the PARENT element,
        /// pivot centered). This guarantees that <c>anchoredPosition</c> is relative to the
        /// parent center - exactly the space that the laser hit also checks.
        /// </summary>
        private static void CenterAnchors(RectTransform rt)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        /// <summary>One bar of the delete "X" (two of them crossed at ±45°).</summary>
        private static Image MakeDeleteBar(Transform parent, float angle)
        {
            var go = new GameObject("Bar");
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = BsUi.WhiteSprite();
            img.raycastTarget = false;
            CenterAnchors(img.rectTransform);
            img.rectTransform.sizeDelta = new Vector2(SongDeleteSize * 0.80f, 5f);
            img.rectTransform.localRotation = Quaternion.Euler(0f, 0f, angle);
            return img;
        }

        private static Image CreateButton(Transform parent, string label, Vector2 pos, Vector2 size,
            Color bg, out Text text)
        {
            var go = new GameObject("Btn_" + label);
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = BsUi.RoundedSprite();
            image.type = Image.Type.Sliced;
            image.color = bg;
            CenterAnchors(image.rectTransform);
            image.rectTransform.sizeDelta = size;
            image.rectTransform.anchoredPosition = pos;

            var tgo = new GameObject("Label");
            tgo.transform.SetParent(go.transform, false);
            text = tgo.AddComponent<Text>();
            text.font = BsUi.Font();
            text.fontSize = 44;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = label;
            text.color = Color.white;
            text.rectTransform.sizeDelta = size;
            CenterAnchors(text.rectTransform);
            return image;
        }

        /// <summary>
        /// Builds the modal confirmation dialog as its OWN overlay canvas (hidden).
        /// A separate canvas with an overriding sort key guarantees that the dialog
        /// lies VISIBLY above everything, independent of the panel hierarchy.
        /// </summary>
        private void BuildConfirmUi()
        {
            var rootGo = new GameObject("ConfirmPopup");
            rootGo.transform.SetParent(transform, false);

            // Its own canvas: forces display above the panel (sort key).
            // The canvas creates the RectTransform itself; configure it afterward.
            var popupCanvas = rootGo.AddComponent<Canvas>();
            popupCanvas.overrideSorting = true;
            popupCanvas.sortingOrder = 100;
            var rootRt = rootGo.GetComponent<RectTransform>();
            CenterAnchors(rootRt);
            rootRt.sizeDelta = new Vector2(ConfirmW, ConfirmH);
            rootRt.anchoredPosition = Vector2.zero;

            _confirmRoot = rootGo.transform;

            // Slight darkening of the panel (the window underneath remains recognizable).
            var scrimGo = new GameObject("Scrim");
            scrimGo.transform.SetParent(_confirmRoot, false);
            _confirmScrim = scrimGo.AddComponent<Image>();
            _confirmScrim.sprite = BsUi.WhiteSprite();
            _confirmScrim.color = new Color(0.01f, 0.01f, 0.03f, 0.55f);
            CenterAnchors(_confirmScrim.rectTransform);
            _confirmScrim.rectTransform.sizeDelta = new Vector2(ConfirmW, ConfirmH);

            // Card
            _confirmCard = MakeRounded(_confirmRoot, BsUi.PanelColor, new Vector2(ConfirmW, ConfirmH), Vector2.zero);
            var cardOutline = _confirmCard.gameObject.AddComponent<Outline>();
            cardOutline.effectColor = BsUi.PanelEdge;
            cardOutline.effectDistance = new Vector2(3f, 3f);

            _confirmTitle = MakeText(_confirmRoot, "", 50, TextAnchor.MiddleCenter, Color.white,
                new Vector2(ConfirmW - 40f, 60f), new Vector2(0f, 100f));
            _confirmDetail = MakeText(_confirmRoot, "", 40, TextAnchor.MiddleCenter, BsUi.LabelColor,
                new Vector2(ConfirmW - 60f, 56f), new Vector2(0f, 40f));

            // Exactly TWO options: green "No" (safe) and red "Yes" (delete).
            _confirmNoBg = CreateButton(_confirmRoot, "No", ConfirmNoPos, ConfirmBtnSize,
                new Color(0.2f, 0.85f, 0.4f, 0.22f), out _confirmNoText);
            _confirmYesBg = CreateButton(_confirmRoot, "Yes", ConfirmYesPos, ConfirmBtnSize,
                new Color(0.9f, 0.3f, 0.3f, 0.22f), out _confirmYesText);

            rootGo.SetActive(false);
        }

        /// <summary>Shows the modal confirmation dialog centered over the panel.</summary>
        public void ShowConfirm(string title, string detail = null)
        {
            _confirmVisible = true;
            ConfirmChoice = 1; // default: No (safe)
            if (_confirmTitle != null) _confirmTitle.text = title;
            if (_confirmDetail != null)
            {
                _confirmDetail.text = detail ?? "";
                _confirmDetail.gameObject.SetActive(!string.IsNullOrEmpty(detail));
            }
            if (_confirmRoot != null)
            {
                if (_confirmScrim != null)
                    _confirmScrim.rectTransform.sizeDelta = _panel.sizeDelta + new Vector2(60f, 60f);
                PlaceConfirmOverPanel(); // immediately centered over the panel (not only in LateUpdate)
                _confirmRoot.gameObject.SetActive(true);
                _confirmRoot.SetAsLastSibling();
            }
            if (_cursorHighlight != null) _cursorHighlight.enabled = false;
            if (_controlHighlight != null) _controlHighlight.enabled = false;
            RefreshSongDeleteIcon();
            UpdateConfirmVisual();
            Debug.Log($"[QuestSaber] HandMenu.ShowConfirm '{title}' visible={_confirmVisible} root={_confirmRoot != null}.");
        }

        /// <summary>Positions the dialog in the center of the panel (world position).</summary>
        private void PlaceConfirmOverPanel()
        {
            if (_confirmRoot == null || _panel == null) return;
            Vector3 center = _panel.TransformPoint(new Vector3(0f, -_panel.sizeDelta.y * 0.5f, 0f));
            _confirmRoot.position = center;
            _confirmRoot.rotation = transform.rotation;
        }

        /// <summary>Hides the confirmation dialog (the panel underneath becomes usable again).</summary>
        public void HideConfirm()
        {
            _confirmVisible = false;
            if (_confirmRoot != null) _confirmRoot.gameObject.SetActive(false);
            RefreshCursorPosition(); // resume the row highlight
            UpdateControlHighlight();
        }

        /// <summary>
        /// Hit finder for the dialog: highlights the selection under the laser. Checked against
        /// the ACTUALLY drawn buttons (world position -> panel-local), so that display
        /// and hit testing can never diverge.
        /// </summary>
        private void UpdateConfirmPointer(Vector2 local)
        {
            if (!_confirmVisible || _confirmYesBg == null || _confirmNoBg == null) return;
            if (IsPointerOn(local, _confirmYesBg)) ConfirmChoice = 0;                 // Yes
            else if (IsPointerOn(local, _confirmNoBg)) ConfirmChoice = 1;             // No
            UpdateConfirmVisual(); // On confirm the last selection hit remains
        }

        /// <summary>True if the laser point (panel-local) lies over the button rect.</summary>
        private bool IsPointerOn(Vector2 local, Image button)
        {
            if (button == null) return false;
            // Both elements hang under the same panel with identical scaling -
            // the rect size in their own units therefore corresponds to panel-local units.
            Vector3 center = _panel.InverseTransformPoint(button.rectTransform.position);
            Vector2 half = button.rectTransform.rect.size * 0.5f;
            return Mathf.Abs(local.x - center.x) <= half.x
                && Mathf.Abs(local.y - center.y) <= half.y;
        }

        private void UpdateConfirmVisual()
        {
            if (_confirmYesBg == null || _confirmNoBg == null) return;
            // "Yes" is red (delete), "No" is green (safe) - each brightly highlighted.
            _confirmYesBg.color = ConfirmChoice == 0 ? new Color(0.95f, 0.35f, 0.35f, 0.95f)
                                                     : new Color(0.9f, 0.3f, 0.3f, 0.22f);
            _confirmNoBg.color = ConfirmChoice == 1 ? new Color(0.2f, 0.95f, 0.45f, 0.95f)
                                                    : new Color(0.2f, 0.85f, 0.4f, 0.22f);
            if (_confirmYesText != null) _confirmYesText.color = ConfirmChoice == 0 ? Color.black : Color.white;
            if (_confirmNoText != null) _confirmNoText.color = ConfirmChoice == 1 ? Color.black : Color.white;
        }

        /// <summary>
        /// Show the row list. With scrollable=true the list is drawn as an ENDLESS, cyclic
        /// window of SongListViewport rows (the songs repeat); there
        /// is NO persistent selection, only the row under the laser is highlighted.
        /// `scrollToRow` (e.g. the current song) only centers the window but selects nothing.
        /// Paging is done exclusively with the joystick (ScrollList).
        /// </summary>
        public void Show(List<Row> rows, bool scrollable = false, int scrollToRow = -1,
            float targetPanelHeight = 0f)
        {
            Cursor = 0;
            _hoverRow = -1;
            _marqueeTimer = 0f;
            _hasPointerLocal = false;
            _pointerOnSongDelete = false;
            _targetPanelHeight = scrollable ? targetPanelHeight : 0f;

            // Fast path: if the row structure is identical (only values have changed,
            // e.g. "scale ±" or player scores), ONLY the value texts are set -
            // no destroying/recreating of all rows. That was the stutter on every click.
            if (!scrollable && TryUpdateValuesInPlace(rows))
            {
                _canvas.enabled = true;
                Visible = true;
                if (_laser != null)
                {
                    _laser.gameObject.SetActive(true);
                    _laser.enabled = false;
                }
                return;
            }

            _rows.Clear();
            _rows.AddRange(rows);

            _scrollable = scrollable;
            if (_scrollable)
            {
                // Visible rows: standard viewport or - if a target height is passed - as
                // many rows as fit into that height (so that the song list is EXACTLY as tall
                // as the main menu and shows more songs).
                _viewportRows = SongListViewport;
                if (_targetPanelHeight > 0f)
                {
                    int fit = Mathf.FloorToInt((_targetPanelHeight - 16f) / _rowHeight);
                    _viewportRows = Mathf.Max(1, fit);
                }
                // Song list: center the window on the passed song. The current song is
                // only highlighted via the background, NOT selected (no cursor on it).
                _scrollOffset = _rows.Count > 0 && scrollToRow >= 0
                    ? Mod(scrollToRow - _viewportRows / 2, _rows.Count)
                    : 0;
            }
            else
            {
                _scrollOffset = 0;
                _viewportRows = SongListViewport;
            }

            RefreshGeometry();
            _canvas.enabled = true;
            Visible = true;
            // Show the laser only after the first pointer match (it hits the panel only
            // while the hand points there) - never let it flash with an old position.
            if (_laser != null)
            {
                _laser.gameObject.SetActive(true);
                _laser.enabled = false;
            }
        }

        private bool TryUpdateValuesInPlace(List<Row> rows)
        {
            if (rows == null || rows.Count != _rows.Count || _rowValueTexts.Count != _rows.Count)
                return false;
            for (int i = 0; i < rows.Count; i++)
            {
                Row a = rows[i], b = _rows[i];
                if (a.Id != b.Id || a.Selectable != b.Selectable || a.MultiControl != b.MultiControl
                    || a.RightValue != b.RightValue || a.Button != b.Button || a.Title != b.Title
                    || a.Card != b.Card || a.FontSize != b.FontSize || a.Cycle != b.Cycle
                    || a.Deletable != b.Deletable
                    || a.Text != b.Text || a.Background != b.Background)
                    return false;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                _rows[i] = rows[i];
                var t = _rowValueTexts[i];
                if (t != null) t.text = rows[i].Value ?? "";
            }
            RefreshCursorPosition();
            UpdateControlHighlight();
            return true;
        }

        public void Hide()
        {
            _canvas.enabled = false;
            Visible = false;
            _hasPointerLocal = false;
            _pointerOnSongDelete = false;
            if (_songDeleteIcon != null) _songDeleteIcon.gameObject.SetActive(false);
            if (_laser != null)
            {
                _laser.enabled = false;
                _laser.gameObject.SetActive(false);
            }
            if (_reticle != null) _reticle.enabled = false;
            HideConfirm();
        }

        /// <summary>
        /// Hard safeguard: the laser beam (and the crosshair) exist EXCLUSIVELY
        /// while the menu is open. The LineRenderer does NOT lie in the canvas, so it does
        /// not fade automatically with <c>_canvas.enabled</c> - therefore its state is forced
        /// every frame here.
        /// </summary>
        private void Update()
        {
            UpdateMarquee();
            bool menuOpen = Visible && _canvas != null && _canvas.enabled;
            if (_laser != null && !menuOpen)
            {
                if (_laser.enabled) _laser.enabled = false;
                if (_laser.gameObject.activeSelf) _laser.gameObject.SetActive(false);
            }
            if (_reticle != null && !menuOpen && _reticle.enabled) _reticle.enabled = false;
        }

        /// <summary>
        /// Marquee of the song names: only the row under the laser scrolls through as soon as the
        /// name is wider than the row. Hold briefly -> scroll through to the left -> hold briefly.
        /// All other rows return to position 0.
        /// </summary>
        private void UpdateMarquee()
        {
            if (!_scrollable || _songTextRefs.Count == 0) return;
            _marqueeTimer += Time.unscaledDeltaTime;
            for (int i = 0; i < _songTextRefs.Count; i++)
            {
                var r = _songTextRefs[i];
                if (r.Text == null) continue;
                if (!r.Measured)
                {
                    r.Overflow = Mathf.Max(0f, r.Text.preferredWidth - SongTextWidth);
                    r.Measured = true;
                }
                float x = 0f;
                if (r.Index == _hoverRow && r.Overflow > 1f)
                {
                    const float hold = 0.7f; // hold at the start/end (s)
                    float scrollTime = r.Overflow / MarqueeSpeedPx;
                    float cycle = hold + scrollTime + hold;
                    float tau = _marqueeTimer % cycle;
                    if (tau < hold) x = 0f;
                    else if (tau < hold + scrollTime) x = (tau - hold) * MarqueeSpeedPx;
                    else x = r.Overflow;
                }
                r.Text.rectTransform.anchoredPosition = new Vector2(-x, 0f);
            }
        }

        public void SetAnchor(Transform anchor)
        {
            _handAnchor = anchor;
            if (anchor != null) transform.SetParent(anchor, false);
        }

        public bool ScrollList(int delta)
        {
            if (!Visible || !_scrollable || delta == 0) return false;
            ScrollBy(delta);
            return true;
        }

        /// <summary>Id of the currently targeted row (song list: row under the laser; -1 = none).</summary>
        public int CurrentRowId()
        {
            if (_scrollable)
            {
                return _hoverRow >= 0 && _hoverRow < _rows.Count ? _rows[_hoverRow].Id : -1;
            }
            int selectableSeen = 0;
            foreach (var r in _rows)
            {
                if (!r.Selectable) continue;
                if (selectableSeen == Cursor) return r.Id;
                selectableSeen++;
            }
            return -1;
        }

        private static int Mod(int a, int n) => ((a % n) + n) % n;

        /// <summary>Index of the currently selected row (or -1).</summary>
        private int CurrentRowIndex()
        {
            int selectableSeen = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!_rows[i].Selectable) continue;
                if (selectableSeen == Cursor) return i;
                selectableSeen++;
            }
            return -1;
        }

        /// <summary>
        /// For control cards, additionally highlights the control field currently targeted (left = -/◄,
        /// right = +/►) above the row highlight.
        /// </summary>
        private void UpdateControlHighlight()
        {
            if (_controlHighlight == null) return;
            if (_confirmVisible || !Visible)
            {
                _controlHighlight.enabled = false;
                return;
            }
            int idx = CurrentRowIndex();
            if (idx < 0 || !_rows[idx].MultiControl)
            {
                _controlHighlight.enabled = false;
                return;
            }
            var rt = _controlHighlight.rectTransform;
            rt.sizeDelta = ControlChipSize + new Vector2(8f, 8f);
            float x = _lastPointerX < 0f ? -ControlChipX : ControlChipX;
            rt.anchoredPosition = new Vector2(x, _rowCenterY[idx] + ControlChipY + _panel.sizeDelta.y * 0.5f);
            _controlHighlight.enabled = true;
        }

        /// <summary>Row height of a row by its kind (also usable externally, e.g. to
        /// calculate the total height of the main menu and pad the song list to it).</summary>
        public static float HeightOf(Row r)
        {
            if (r.Height > 0f) return r.Height;
            if (r.MultiControl) return ControlRowHeight;
            if (r.Button) return 84f;
            return r.Title ? TitleHeight : 72f; // 72 = default row height (_rowHeight)
        }

        private float RowHeightFor(int index) => HeightOf(_rows[index]);

        private void RefreshGeometry()
        {
            // Remove old row objects (including children).
            foreach (var o in _rowObjects) if (o != null) Destroy(o);
            _rowObjects.Clear();
            _songTextRefs.Clear();
            _rowValueTexts.Clear();
            for (int i = 0; i < _rows.Count; i++) _rowValueTexts.Add(null);
            // Adapt the buffers to the row count (non-scrollable lists like
            // player management can have arbitrarily many rows - no index error).
            if (_rowCenterY.Length < _rows.Count)
            {
                int n = Mathf.Max(_rows.Count, 64);
                _rowCenterY = new float[n];
                _rowHeights = new float[n];
            }
            for (int i = 0; i < _rowCenterY.Length; i++)
            {
                _rowCenterY[i] = float.MinValue;
                _rowHeights[i] = 0f;
            }

            // Visible rows: song list = cyclic window of length `_viewportRows`
            // (display position p -> content (offset + p) modulo song count).
            var visible = new List<int>();
            if (_scrollable && _rows.Count > 0)
            {
                int n = Mathf.Min(_viewportRows, _rows.Count);
                for (int p = 0; p < n; p++) visible.Add(Mod(_scrollOffset + p, _rows.Count));
            }
            else
            {
                for (int i = 0; i < _rows.Count; i++) visible.Add(i);
            }

            // Precompute the panel height: the top edge lies at the anchor (no empty space at the top).
            float totalH = 0f;
            foreach (int i in visible) totalH += RowHeightFor(i);
            // Target height (e.g. height of the main menu): distribute the remaining space evenly
            // top/bottom as margin, so that the panel is EXACTLY this tall.
            float extra = 0f;
            if (_scrollable && _targetPanelHeight > 0f)
            {
                float natural = totalH + 16f;
                if (natural < _targetPanelHeight) extra = _targetPanelHeight - natural;
            }
            float half = (totalH + 16f + extra) * 0.5f; // +8 margin top, +8 bottom (+ possible padding)
            float y = -8f - extra * 0.5f; // top edge of the first row

            foreach (int i in visible)
            {
                bool isTitle = _rows[i].Title;
                float h = RowHeightFor(i);
                float centerY = y - h * 0.5f;
                _rowCenterY[i] = centerY;
                _rowHeights[i] = h;
                // Panel pivot is at the top (0.5/1): offset children by half,
                // so that their world position stays exactly centerY (layout conversion).
                Vector2 childPos = new Vector2(0f, centerY + half);

                var rowGo = new GameObject("Row" + i);
                rowGo.transform.SetParent(_panel, false);
                var rt = rowGo.AddComponent<RectTransform>();
                CenterAnchors(rt);
                rt.sizeDelta = new Vector2(560f, h);
                rt.anchoredPosition = childPos;
                _rowObjects.Add(rowGo);

                // Optional row background (e.g. blue bar for the active player)
                if (_rows[i].Background.HasValue)
                    MakeRounded(rowGo.transform, _rows[i].Background.Value, new Vector2(556f, h - 4f), Vector2.zero);

                if (_rows[i].MultiControl) _rowValueTexts[i] = BuildControlRow(rowGo.transform, _rows[i], h);
                else if (_rows[i].Button) BuildButtonRow(rowGo.transform, _rows[i], h);
                else if (_rows[i].RightValue) _rowValueTexts[i] = BuildRightValueRow(rowGo.transform, _rows[i], h);
                else if (_scrollable) BuildSongRow(rowGo, i, h); // clipped + marquee
                else BuildTextRow(rowGo.transform, _rows[i], isTitle, h);

                y -= h;
            }

            _panel.sizeDelta = new Vector2(580f, totalH + 16f + extra);
            _panel.pivot = new Vector2(0.5f, 1f); // top edge at the anchor -> no empty area at the top
            // Cursor last (semi-transparent, stays readable), control field highlight above it.
            if (_cursorHighlight != null) _cursorHighlight.transform.SetAsLastSibling();
            if (_controlHighlight != null) _controlHighlight.transform.SetAsLastSibling();
            if (_songDeleteIcon != null) _songDeleteIcon.SetAsLastSibling();
            RefreshCursorPosition();
            UpdateControlHighlight();
        }

        /// <summary>Normal text row (also title).</summary>
        private void BuildTextRow(Transform row, Row r, bool isTitle, float h)
        {
            if (isTitle)
                MakeRounded(row, BsUi.TitleBarColor, new Vector2(556f, h - 6f), Vector2.zero);

            var text = MakeText(row, r.Text,
                isTitle ? 56 : (r.FontSize > 0 ? r.FontSize : 48),
                isTitle ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft,
                RowTextColor(r, isTitle),
                new Vector2(540f, h), new Vector2(8f, 0f));
            text.fontStyle = isTitle ? FontStyle.Bold : FontStyle.Normal;
        }

        /// <summary>
        /// Button: subtle gray background like the control cards (audio offset),
        /// so that the row is already readable as a button BEFORE hovering, without standing out.
        /// </summary>
        private void BuildButtonRow(Transform row, Row r, float h)
        {
            MakeRounded(row, BsUi.RowColor, new Vector2(548f, h - 6f), Vector2.zero);

            var text = MakeText(row, r.Text, r.FontSize > 0 ? r.FontSize : 46,
                TextAnchor.MiddleCenter,
                r.Selectable ? Color.white : new Color(0.6f, 0.62f, 0.7f),
                new Vector2(520f, h - 8f), Vector2.zero);
            text.fontStyle = FontStyle.Bold;
        }

        /// <summary>
        /// Name left-aligned, value (e.g. score or on/off status) right-aligned.
        /// With <see cref="Row.Card"/> additionally given a subtle gray background.
        /// </summary>
        private Text BuildRightValueRow(Transform row, Row r, float h)
        {
            if (r.Card)
                MakeRounded(row, BsUi.RowColor, new Vector2(548f, h - 6f), Vector2.zero);

            MakeText(row, r.Text, r.FontSize > 0 ? r.FontSize : 46, TextAnchor.MiddleLeft,
                RowTextColor(r, false), new Vector2(360f, h), new Vector2(-95f, 0f));
            return MakeText(row, r.Value ?? "", r.FontSize > 0 ? r.FontSize : 46, TextAnchor.MiddleRight,
                r.Selectable ? Color.white : new Color(0.68f, 0.70f, 0.78f),
                new Vector2(180f, h), new Vector2(180f, 0f));
        }

        /// <summary>
        /// Song row of the song list: the text is clipped to the LEFT part of the row
        /// (RectMask2D on a left-shifted child) so that long names do not run over the window
        /// edge AND do not reach the delete "X" at the far right. Scrolls through as a
        /// marquee if needed (see <see cref="UpdateMarquee"/>).
        /// </summary>
        private void BuildSongRow(GameObject rowGo, int contentIndex, float h)
        {
            // Clip container (mask) shifted left -> the right part of the row stays free for the X.
            var clipGo = new GameObject("Clip");
            clipGo.transform.SetParent(rowGo.transform, false);
            var clipRt = clipGo.AddComponent<RectTransform>();
            CenterAnchors(clipRt);
            clipRt.sizeDelta = new Vector2(SongTextWidth, h);
            clipRt.anchoredPosition = new Vector2(SongTextClipX, 0f);
            clipGo.AddComponent<RectMask2D>(); // clips overflowing names at the clip edge

            var t = MakeText(clipGo.transform, _rows[contentIndex].Text,
                _rows[contentIndex].FontSize > 0 ? _rows[contentIndex].FontSize : 48,
                TextAnchor.MiddleLeft, RowTextColor(_rows[contentIndex], false),
                new Vector2(SongTextWidth, h), Vector2.zero);
            _songTextRefs.Add(new SongTextRef { Index = contentIndex, Text = t, Overflow = 0f, Measured = false });
        }

        private static Color RowTextColor(Row r, bool isTitle)
        {
            if (r.Selectable && !isTitle && !string.IsNullOrEmpty(r.Text) && r.Text.Contains("★"))
                return new Color(1f, 0.9f, 0.4f); // top scorer in gold
            return r.Selectable ? Color.white : new Color(0.68f, 0.70f, 0.78f);
        }

        /// <summary>
        /// Control card: name CENTERED, below it a visible control field left/right each
        /// (-/◄ or +/►) and the current value centered.
        /// </summary>
        private Text BuildControlRow(Transform row, Row r, float h)
        {
            MakeRounded(row, BsUi.RowColor, new Vector2(556f, h - 6f), Vector2.zero);

            MakeText(row, r.Text, 30, TextAnchor.MiddleCenter, BsUi.LabelColor,
                new Vector2(520f, 30f), new Vector2(0f, 22f));
            var value = MakeText(row, r.Value ?? "", 44, TextAnchor.MiddleCenter, BsUi.ValueColor,
                new Vector2(230f, 46f), new Vector2(0f, ControlChipY));

            string leftGlyph = r.Cycle ? "◄" : "−";
            string rightGlyph = r.Cycle ? "►" : "+";
            MakeChip(row, leftGlyph, new Vector2(-ControlChipX, ControlChipY));
            MakeChip(row, rightGlyph, new Vector2(ControlChipX, ControlChipY));
            return value;
        }

        private static void MakeChip(Transform row, string glyph, Vector2 pos)
        {
            var chip = MakeRounded(row, BsUi.ChipColor, ControlChipSize, pos);
            MakeText(chip.transform, glyph, 40, TextAnchor.MiddleCenter, BsUi.AccentColor,
                ControlChipSize, Vector2.zero);
        }

        private void RefreshCursorPosition()
        {
            if (_cursorHighlight == null) return;
            if (_rows.Count == 0)
            {
                _cursorHighlight.enabled = false;
                RefreshSongDeleteIcon();
                return;
            }

            // Song list: NO persistent selection. Only the row under the laser is
            // highlighted in green; if the laser points at no song, no row is marked either.
            if (_scrollable)
            {
                bool show = !_confirmVisible && _hoverRow >= 0
                    && _hoverRow < _rowHeights.Length && _rowHeights[_hoverRow] > 0f;
                _cursorHighlight.enabled = show;
                if (show)
                {
                    _cursorHighlight.rectTransform.sizeDelta =
                        new Vector2(560f, Mathf.Max(24f, _rowHeights[_hoverRow] - 10f));
                    _cursorHighlight.rectTransform.anchoredPosition =
                        new Vector2(0f, _rowCenterY[_hoverRow] + _panel.sizeDelta.y * 0.5f);
                }
                RefreshSongDeleteIcon();
                return;
            }

            _cursorHighlight.enabled = !_confirmVisible;
            RefreshSongDeleteIcon(); // not a song list -> hides the X
            int selectableSeen = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!_rows[i].Selectable) continue;
                if (selectableSeen == Cursor)
                {
                    if (i >= _rowCenterY.Length) return;
                    _cursorHighlight.rectTransform.sizeDelta =
                        new Vector2(560f, Mathf.Max(24f, _rowHeights[i] - 10f));
                    _cursorHighlight.rectTransform.anchoredPosition =
                        new Vector2(0f, _rowCenterY[i] + _panel.sizeDelta.y * 0.5f);
                    return;
                }
                selectableSeen++;
            }
        }

        /// <summary>
        /// Moves the song list window by `dir` entries, CYCLICALLY (no end; the songs
        /// repeat). Afterward the row under the (resting) laser is re-determined.
        /// </summary>
        private void ScrollBy(int dir)
        {
            if (!_scrollable || _rows.Count <= 1 || dir == 0) return;
            _scrollOffset = Mod(_scrollOffset + dir, _rows.Count);
            _marqueeTimer = 0f; // restart the marquee after paging
            RefreshGeometry();
            if (_hasPointerLocal) RowHitScan(_lastPointerLocal);
            else if (_hoverRow != -1) { _hoverRow = -1; RefreshCursorPosition(); }
        }

        /// <summary>
        /// Pointer laser: cast a beam from the other hand onto the menu panel; a hit
        /// moves the cursor directly to the indicated row ("point-to-navigate" navigation).
        /// Called every frame by the GameManager while the menu is open.
        /// </summary>
        public void UpdatePointer(Vector3 origin, Vector3 direction)
        {
            if (_laser == null) return;
            if (!Visible || _panel == null)
            {
                SetLaserVisible(false);
                if (_reticle != null) _reticle.enabled = false;
                return;
            }

            Vector3 dirN = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
            Vector3 end = origin + dirN * 2f;
            var ray = new Ray(origin, dirN);

            // Show the beam only when it really hits the panel (reference: SuperSaber
            // cursor-laser) - no beam permanently pointing into the void.
            bool laserOn = false;

            var plane = new Plane(transform.forward, transform.position);
            if (plane.Raycast(ray, out float enter) && enter >= 0f)
            {
                Vector3 hit = ray.GetPoint(enter);
                Vector3 local = _panel.InverseTransformPoint(hit);

                // Panel extends from the anchor (0) downward (-sizeDelta.y). The hit area follows.
                bool inPanel = Mathf.Abs(local.x) <= 340f
                               && local.y <= 8f
                               && local.y >= -_panel.sizeDelta.y;
                if (inPanel)
                {
                    laserOn = true;
                    _lastPointerX = local.x; // for (-)/(+) rows: left = minus, right = plus
                    _lastPointerLocal = local;
                    _hasPointerLocal = true;
                    end = hit;
                    if (_reticle != null)
                    {
                        _reticle.enabled = true;
                        _reticle.rectTransform.position = hit;
                    }

                    if (_confirmVisible)
                    {
                        // Modal dialog: highlight only Yes/No; the menu behind it is frozen.
                        _pointerOnSongDelete = false;
                        UpdateConfirmPointer(local);
                    }
                    else
                    {
                        // Song list: determine the row under the laser (NO persistent selection).
                        // Other menus: set the selection cursor to the row.
                        RowHitScan(local);
                        _pointerOnSongDelete = HitSongDeleteIcon(local);
                        UpdateControlHighlight();
                        RefreshSongDeleteIcon();
                    }
                }
                else
                {
                    _hasPointerLocal = false;
                    _pointerOnSongDelete = false;
                    ClearHover();
                    if (_reticle != null) _reticle.enabled = false;
                }
            }
            else
            {
                _hasPointerLocal = false;
                _pointerOnSongDelete = false;
                ClearHover();
                if (_reticle != null) _reticle.enabled = false;
            }

            SetLaserVisible(laserOn);
            if (laserOn)
            {
                _laser.SetPosition(0, origin);
                _laser.SetPosition(1, end);
            }
        }

        /// <summary>
        /// Show the laser only when it really hits the menu panel - and only with the
        /// menu open (reference: SuperSaber `cursor-laser.js`).
        /// </summary>
        private void SetLaserVisible(bool on)
        {
            if (_laser == null) return;
            bool show = on && Visible && _canvas != null && _canvas.enabled;
            if (_laser.gameObject.activeSelf != show) _laser.gameObject.SetActive(show);
            _laser.enabled = show;
        }

        /// <summary>
        /// Song list: determine the row under the laser (no persistent selection). Other
        /// menus: set the selection cursor to the row under the laser.
        /// </summary>
        private void RowHitScan(Vector2 local)
        {
            if (_scrollable)
            {
                int hover = HitRowAt(local);
                if (hover != _hoverRow)
                {
                    _hoverRow = hover;
                    _marqueeTimer = 0f; // restart the marquee
                    RefreshCursorPosition();
                }
                return;
            }

            int selectableSeen = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                bool inY = i < _rowCenterY.Length
                    && Mathf.Abs(local.y - _rowCenterY[i]) <= _rowHeights[i] * 0.5f;
                if (inY)
                {
                    if (_rows[i].Selectable && selectableSeen != Cursor)
                    {
                        Cursor = selectableSeen;
                        RefreshCursorPosition();
                    }
                    break;
                }
                if (_rows[i].Selectable) selectableSeen++;
            }
        }

        /// <summary>Content row index under the laser in the song list (cyclic window), otherwise -1.</summary>
        private int HitRowAt(Vector2 local)
        {
            if (_rows.Count == 0) return -1;
            int n = Mathf.Min(_viewportRows, _rows.Count);
            for (int p = 0; p < n; p++)
            {
                int i = Mod(_scrollOffset + p, _rows.Count);
                if (i < _rowHeights.Length && _rowHeights[i] > 0f
                    && Mathf.Abs(local.y - _rowCenterY[i]) <= _rowHeights[i] * 0.5f)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Song list: remove the green hover highlight (laser points at no song).</summary>
        private void ClearHover()
        {
            if (_scrollable && _hoverRow != -1)
            {
                _hoverRow = -1;
                _marqueeTimer = 0f;
                _pointerOnSongDelete = false;
                RefreshCursorPosition();
            }
        }

        /// <summary>
        /// Song list: places the delete "X" at the far right of the row under the laser (shown
        /// only for deletable rows) and colors it DIM / BRIGHT red depending on whether the
        /// laser points straight at it. Hidden while the confirmation dialog is open or when
        /// no deletable row is hovered.
        /// </summary>
        private void RefreshSongDeleteIcon()
        {
            if (_songDeleteIcon == null) return;
            bool show = _scrollable && !_confirmVisible && _hoverRow >= 0
                        && _hoverRow < _rows.Count && _rows[_hoverRow].Deletable
                        && _hoverRow < _rowHeights.Length && _rowHeights[_hoverRow] > 0f;
            if (_songDeleteIcon.gameObject.activeSelf != show) _songDeleteIcon.gameObject.SetActive(show);
            if (!show) return;
            _songDeleteIcon.anchoredPosition =
                new Vector2(SongDeleteCenterX, _rowCenterY[_hoverRow] + _panel.sizeDelta.y * 0.5f);
            Color c = _pointerOnSongDelete ? SongDeleteHot : SongDeleteDim;
            if (_songDeleteBarA != null) _songDeleteBarA.color = c;
            if (_songDeleteBarB != null) _songDeleteBarB.color = c;
        }

        /// <summary>True if the laser point (panel-local) lies on the delete "X" of the
        /// hovered song row (same geometry as the drawn icon -> display and hit never diverge).</summary>
        private bool HitSongDeleteIcon(Vector2 local)
        {
            if (!_scrollable || _confirmVisible || _hoverRow < 0 || _hoverRow >= _rows.Count
                || !_rows[_hoverRow].Deletable
                || _hoverRow >= _rowHeights.Length || _rowHeights[_hoverRow] <= 0f) return false;
            // The icon is DRAWN at anchoredPosition.y = rowCenterY + panelH/2, which is the
            // panel-local y rowCenterY (measured from the top pivot) - exactly `local.y`.
            // The drawn X bars have an extent of ~±15 px from center (41.6 px at 45 deg).
            return Mathf.Abs(local.x - SongDeleteCenterX) <= SongDeleteVisualRadius
                && Mathf.Abs(local.y - _rowCenterY[_hoverRow]) <= SongDeleteVisualRadius;
        }

        private void LateUpdate()
        {
            if (!Visible || _handAnchor == null || _cam == null) return;

            // Billboards toward the headset (NOT toward the other hand): the panel must
            // not rotate along with the pointer hand. Only slightly tilted backward.
            Vector3 toHeadset = _handAnchor.position - _cam.transform.position;
            if (toHeadset.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.LookRotation(toHeadset.normalized, Vector3.up)
                                 * Quaternion.Euler(MenuTiltDeg, 0f, 0f);

            // Fixed bottom edge: the panel's BOTTOM EDGE lies `MenuBottomClearance` above the
            // controller (farther away by `MenuForwardOffset` in view direction). The pivot is the
            // top edge, so add the full panel height along the panel's "up" axis.
            float panelH = (_panel != null ? _panel.sizeDelta.y : 0f) * Mathf.Abs(transform.lossyScale.y);
            Vector3 bottom = _handAnchor.position
                             + Vector3.up * MenuBottomClearance
                             + _cam.transform.forward * MenuForwardOffset;
            transform.position = bottom + transform.up * panelH;

            // Dialog (its own overlay canvas) follows the panel and stays centered above it.
            if (_confirmVisible) PlaceConfirmOverPanel();
        }
    }
}

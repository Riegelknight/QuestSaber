using System.Collections.Generic;
using UnityEngine;

namespace QuestSaber
{
    /// <summary>An arc (slider): Bézier band between head and tail point.</summary>
    [DefaultExecutionOrder(-100)]
    public sealed class ArcView : MonoBehaviour
    {
        private LineRenderer _line;
        private float _njs;
        private float _headTime;   // head beat in seconds
        private float _length;     // arc length in movement direction (meters, base units)
        private bool _moving;
        private MaterialPropertyBlock _mpb; // texture scroll + interaction intensity (ArcShader)

        // Arc geometry in local space (fixed after construction) - basis of the saber attraction:
        // only the head point/head control point are replaced by the saber position.
        private Vector3 _headPoint;    // head point p0 (unstretched)
        private Vector3 _headTangent;  // head control point p1 - p0
        private Vector3 _tailControl;  // control point at the end p2
        private Vector3 _tailPoint;    // tail point p3
        private int _curveCount;
        private float _lastFadeLen = -1f;
        private float _naturalLength;  // |tail - head| in the unstretched state (basis of the stretch limit)

        // Saber attraction (original `SliderController.SetSaberAttraction`): If the
        // HEAD NOTE is sliced, the saber becomes the new start of the arc - the arc
        // then sticks to the saber and changes live with its movement (smoothed).
        private bool _attached;
        private SaberView _attachSaber;
        private Vector3 _attachPointLocal;
        private bool _hasAttachPoint;

        // Arc interaction (original `SliderInteractionManager`): the arc glows brighter
        // when the saber blade follows it, and fades out when it leaves it.
        private float _intensity = 1f;
        private bool _interacted;
        private float _interactionParam;
        private static Color _arcBaseRed, _arcBaseBlue;
        private static bool _arcBaseColorsCached;

        /// <summary>Color type of the arc: 0 = red (left saber), 1 = blue (right).</summary>
        public int ColorType { get; private set; }

        /// <summary>The data model of this arc (for linking head note -> arc
        /// during the saber attraction).</summary>
        public ArcModel Model { get; private set; }

        /// <summary>True as long as the arc sticks to the saber (the head note was sliced).</summary>
        public bool IsAttached => _attached;

        private static readonly Stack<ArcView> _free = new();
        private static Material _redMat, _blueMat;
        private static Gradient _fadeGradient; // reused (end fade per curve length)
        private GradientColorKey[] _fadeColorKeys; // reused (no GC when updating)
        private GradientAlphaKey[] _fadeAlphaKeys;

        /// <summary>Gets a (pooled) arc, builds the curve and activates it.</summary>
        public static ArcView Get(ArcModel arc, float headTime, float tailTime, float njs, Transform root)
        {
            ArcView view = _free.Count > 0 ? _free.Pop() : CreateNew(root);
            view.Build(arc, headTime, tailTime, njs);
            return view;
        }

        public static void Return(ArcView view)
        {
            if (view == null) return;
            view._moving = false;
            view._attached = false;
            view._attachSaber = null;
            view._hasAttachPoint = false;
            view.gameObject.SetActive(false);
            _free.Push(view);
        }

        /// <summary>Collects all active arcs (song change). Pooled ones are inactive.</summary>
        public static void ReturnAll(Transform root)
        {
            if (root == null) return;
            var active = root.GetComponentsInChildren<ArcView>(); // active only
            for (int i = 0; i < active.Length; i++) Return(active[i]);
        }

        private static ArcView CreateNew(Transform root)
        {
            var go = new GameObject("Arc");
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;          // curve lies in the local room root
            line.textureMode = LineTextureMode.Stretch;
            line.numCapVertices = 2;
            line.numCornerVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.widthMultiplier = GameConfig.ArcWidth;
            line.positionCount = 0;
            var view = go.AddComponent<ArcView>();
            view._line = line;
            if (root != null) go.transform.SetParent(root, false);
            return view;
        }

        private void Build(ArcModel arc, float headTime, float tailTime, float njs)
        {
            _headTime = headTime;
            _njs = njs;
            _moving = true;
            gameObject.SetActive(true);

            ColorType = arc._headColorType;
            Model = arc;
            _attached = false;
            _attachSaber = null;
            _hasAttachPoint = false;
            _intensity = 1f;
            _interacted = false;
            _interactionParam = 0f;
            _line.sharedMaterial = arc._headColorType == 0 ? RedMat : BlueMat;

            // 1:1 ArcViewer arc constructor (ArcManager.cs):
            Vector3 hp = GameConfig.GridPosition(arc._headLineIndex, arc._headLineLayer);
            Vector3 tp = GameConfig.GridPosition(arc._tailLineIndex, arc._tailLineLayer);
            int headDir = Mathf.Clamp((int)arc._headCutDirection, 0, 8);
            int tailDir = Mathf.Clamp((int)arc._tailCutDirection, 0, 8);
            Vector2 headDirVec = headDir == 8 ? Vector2.zero
                : GameConfig.DirectionVector(GameConfig.NoteAngleDegrees(headDir));
            Vector2 tailDirVec = tailDir == 8 ? Vector2.zero
                : -GameConfig.DirectionVector(GameConfig.NoteAngleDegrees(tailDir));
            Vector2 headControl = new Vector2(hp.x, hp.y)
                + headDirVec * (arc._headControlPointLengthMultiplier * GameConfig.ArcControlOffset);
            Vector2 tailControl = new Vector2(tp.x, tp.y)
                + tailDirVec * (arc._tailControlPointLengthMultiplier * GameConfig.ArcControlOffset);

            // Connection to a note (ArcViewer ObjectManager `halfNoteOffset` = 0.225):
            // If a note sits at an arc end, both the endpoint AND the control point are
            // shifted by ArcAttachOffset in the arc direction - the arc thus ends at the
            // note edge instead of its center. Dot ends (direction 8) have no
            // direction vector => no offset (as in the reference).
            if (arc._headAttached)
            {
                Vector2 off = headDirVec * GameConfig.ArcAttachOffset;
                hp.x += off.x; hp.y += off.y;
                headControl += off;
            }
            if (arc._tailAttached)
            {
                Vector2 off = tailDirVec * GameConfig.ArcAttachOffset;
                tp.x += off.x; tp.y += off.y;
                tailControl += off;
            }

            float duration = Mathf.Max(0f, tailTime - headTime);
            _length = Mathf.Max(0.01f, duration * njs);
            int count = Mathf.Clamp(
                Mathf.RoundToInt(GameConfig.ArcSegmentsPerSecond * duration) + 1,
                GameConfig.ArcMinPoints, GameConfig.ArcMaxPoints);
            _line.positionCount = count;

            Vector3 p0 = new Vector3(hp.x, hp.y, 0f);
            Vector3 p1 = new Vector3(headControl.x, headControl.y, 0f);
            Vector3 p2 = new Vector3(tailControl.x, tailControl.y, _length);
            Vector3 p3 = new Vector3(tp.x, tp.y, _length);
            // Remember geometry - basis of the saber attraction: if the head note is sliced,
            // only p0 (and thus p1) moves to the sword position, the rest stays.
            _headPoint = p0;
            _headTangent = p1 - p0;
            _tailControl = p2;
            _tailPoint = p3;
            _naturalLength = (p3 - p0).magnitude;
            _curveCount = count;
            _lastFadeLen = -1f;
            // Set the curve and measure the REAL curve length in the process (basis of the
            // fixed end fade, ArcViewer `GetCurveLength`).
            RebuildCurve(_headPoint, _headPoint + _headTangent, _tailControl, _tailPoint);

            // Set the position IMMEDIATELY: the arc is still activated in THIS frame, but its
            // `Update` (order -100) already ran BEFORE GameManager (0). Without this line
            // it would appear for one frame at the OLD position from the pool
            // (reported "arc flashes at the top" bug). The time is the same as in Update.
            float z = GameConfig.JumpZ(_headTime, GameManager.VisualTime, _njs);
            transform.localPosition = new Vector3(0f, GameConfig.FieldHeightOffsetMeters, z);
        }

        private void Update()
        {
            if (!_moving) return;
            float z = GameConfig.JumpZ(_headTime, GameManager.VisualTime, _njs);
            transform.localPosition = new Vector3(0f, GameConfig.FieldHeightOffsetMeters, z);
            // Note: the saber attraction (if active) is NOT updated here, but
            // in `GameManager.LateUpdate` (`UpdateArcInteraction`) - there the sword pose
            // is already current (here, order -100, it would be one frame old).
            // ArcShader texture scroll (ArcViewer `arcAnimationSpeed`): the band "flows".
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            _mpb.SetFloat("_TextureOffset",
                Mathf.Repeat(_headTime * 0.1f + Time.time * GameConfig.ArcAnimationSpeed, 1f));
            // Arc interaction: followed = brighter, left = fade out (original `SliderIntensityEffect`).
            float target = _interactionParam > 0.001f
                ? 1f + GameConfig.ArcInteractIntensityBoost * _interactionParam
                : (_interacted ? GameConfig.ArcInteractFadedIntensity : 1f);
            _intensity = Mathf.MoveTowards(_intensity, target, Time.deltaTime * GameConfig.ArcInteractFadeSpeed);
            ApplyInteractionColor(_intensity);
            _line.SetPropertyBlock(_mpb);
            // End: the arc disappears as soon as its end passes the "missed" line too.
            if (z <= GameConfig.NoteMissZ - _length)
            {
                Return(this);
            }
        }

        // ---------- Curve/material helpers ----------

        /// <summary>Sets the interaction level (0..1) of this frame (from the GameManager).</summary>
        public void SetInteraction(float param)
        {
            _interactionParam = param;
            if (param > 0.001f) _interacted = true;
        }

        // ---------- Saber attraction (original `SliderController.SetSaberAttraction`) ----------

        /// <summary>
        /// Attaches the arc to this saber: the saber becomes the new START of the arc.
        /// Called by the GameManager as soon as the HEAD NOTE of this arc has been
        /// validly sliced. From then on the arc head follows the sword movement (smoothed).
        /// </summary>
        public void AttachToSaber(SaberView saber)
        {
            if (!_moving || saber == null) return;
            _attachSaber = saber;
            _attached = true;
            // Start AT THE PREVIOUS ARC HEAD (not on the sword): the smoothing then pulls the
            // head onto the blade position within a few frames. Setting it immediately to
            // the sword would visibly jump (the blade lies next to the note at the cut).
            _attachPointLocal = _headPoint;
            _hasAttachPoint = true;
        }

        /// <summary>Releases the attraction again - the arc returns to its original shape.</summary>
        private void ReleaseAttach()
        {
            if (!_attached) return;
            _attached = false;
            _attachSaber = null;
            _hasAttachPoint = false;
            RebuildCurve(_headPoint, _headPoint + _headTangent, _tailControl, _tailPoint);
        }

        /// <summary>
        /// Follows the arc head to the sword position. Point on the blade:
        /// <see cref="GameConfig.ArcAttachPointNormalizedPosition"/> (original
        /// `kSaberAttractPointNormalizedPosition` = 0.7 = 70 % from base to tip).
        /// Smoothed frame-rate-independently (replacement for
        /// `_closeSmoothedSaberInteractionPos`/FixedUpdateVector3SmoothValue).
        /// Called by the GameManager in LATEUPDATE (there the sword pose is fresh and
        /// identical to the collision measurement; in `Update` (order -100) it would be one frame old).
        /// </summary>
        public void UpdateAttachedCurve()
        {
            if (!_attached) return;

            SaberView saber = _attachSaber;
            if (saber == null || saber.Retracted || !saber.HasCurrentPose)
            {
                ReleaseAttach();
                return;
            }

            Vector3 world = Vector3.Lerp(saber.CurrentBasePosition, saber.CurrentTipPosition,
                GameConfig.ArcAttachPointNormalizedPosition);
            Vector3 target = transform.InverseTransformPoint(world);

            if (!_hasAttachPoint)
            {
                _attachPointLocal = target;
                _hasAttachPoint = true;
            }
            else
            {
                float k = 1f - Mathf.Exp(-GameConfig.ArcAttachSmoothSpeed * Time.deltaTime);
                _attachPointLocal = Vector3.Lerp(_attachPointLocal, target, k);
            }

            // "Leash": the attraction point may never move farther than the NATURAL arc length
            // (+ some stretch) away from the arc end. Instead of distorting the arc by meters
            // or even letting go (visible jump!), it is clamped onto this sphere around the end.
            // IMPORTANT: relative to the length, not absolute - a fixed value would already be
            // exceeded for long arcs at rest (distance = arc length).
            float maxLen = _naturalLength + GameConfig.ArcAttachMaxStretch;
            Vector3 rel = _attachPointLocal - _tailPoint;
            float dist = rel.magnitude;
            if (dist > maxLen && dist > 1e-5f)
                _attachPointLocal = _tailPoint + rel * (maxLen / dist);

            RebuildCurve(_attachPointLocal, _attachPointLocal + _headTangent, _tailControl, _tailPoint);
        }

        /// <summary>
        /// Interaction level 0..1 from the distance of the saber blade (base->tip) to the arc curve
        /// (original `SliderInteractionManager.saberInteractionParam`): 1 = exactly on the band,
        /// 0 = outside the capture radius. Computationally cheap (stride samples, allocation-free).
        /// </summary>
        public float EvaluateInteraction(Vector3 saberBase, Vector3 saberTip, float radius)
        {
            if (!_moving || _line == null || _line.positionCount == 0 || radius <= 0f) return 0f;
            int count = _line.positionCount;
            int stride = Mathf.Max(1, count / 48);
            float best = float.MaxValue;
            for (int i = 0; i < count; i += stride)
                best = Mathf.Min(best, DistancePointSegment(saberBase, saberTip,
                    transform.TransformPoint(_line.GetPosition(i))));
            best = Mathf.Min(best, DistancePointSegment(saberBase, saberTip,
                transform.TransformPoint(_line.GetPosition(count - 1))));
            if (best >= radius) return 0f;
            return 1f - (best / radius);
        }

        private void ApplyInteractionColor(float intensity)
        {
            if (!_arcBaseColorsCached)
            {
                _arcBaseRed = RedMat.HasProperty("_BaseColor")
                    ? RedMat.GetColor("_BaseColor") : new Color(0.784f, 0.078f, 0.078f);
                _arcBaseBlue = BlueMat.HasProperty("_BaseColor")
                    ? BlueMat.GetColor("_BaseColor") : new Color(0.157f, 0.557f, 0.824f);
                _arcBaseColorsCached = true;
            }
            Color baseColor = ColorType == 0 ? _arcBaseRed : _arcBaseBlue;
            float k = Mathf.Max(intensity, 0f);
            var c = new Color(baseColor.r * k, baseColor.g * k, baseColor.b * k, baseColor.a);
            _mpb.SetColor("_BaseColor", c);
            _mpb.SetColor("_Color", c);
        }

        /// <summary>Distance point <-> segment (a..b) - for the blade-arc proximity.</summary>
        private static float DistancePointSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float ab2 = ab.sqrMagnitude;
            if (ab2 < 1e-8f) return Vector3.Distance(p, a);
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab2);
            return Vector3.Distance(p, a + ab * t);
        }

        private static Vector3 CubicBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }

        /// <summary>
        /// Resets the curve points - both during construction and every frame during the
        /// saber attraction - and measures the real curve length for the fixed end fade.
        /// Allocation-free.
        /// </summary>
        private void RebuildCurve(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
        {
            int count = _curveCount;
            if (_line.positionCount != count) _line.positionCount = count;
            Vector3 prev = CubicBezier(p0, p1, p2, p3, 0f);
            _line.SetPosition(0, prev);
            float curveLength = 0f;
            for (int i = 1; i < count; i++)
            {
                float t = (float)i / (count - 1);
                Vector3 p = CubicBezier(p0, p1, p2, p3, QuadInOut(t));
                _line.SetPosition(i, p);
                curveLength += Vector3.Distance(prev, p);
                prev = p;
            }
            ApplyEndFade(curveLength);
        }

        private static float QuadInOut(float x)
        {
            return x < 0.5f ? 2f * x * x : 1f - Mathf.Pow(-2f * x + 2f, 2f) / 2f;
        }

        /// <summary>
        /// End fade as a FIXED DISTANCE (ArcViewer `ArcHandler.SetGradient`): start/end
        /// are divided by the curve length so that long arcs do NOT fade out proportionally
        /// longer. A reused gradient (no GC per arc).
        /// </summary>
        private void ApplyEndFade(float curveLength)
        {
            float len = Mathf.Max(0.0001f, curveLength);
            // During the attraction the length changes every frame - only re-set on a relevant
            // change (1 %). Pure cosmetics, the effort is otherwise not worth it.
            if (_lastFadeLen > 0f && Mathf.Abs(len - _lastFadeLen) < _lastFadeLen * 0.01f) return;
            _lastFadeLen = len;
            // Limited to 0.49: the alpha keys must remain sorted in time.
            float fadeEnd = Mathf.Clamp(GameConfig.ArcEndFadeMeters / len, 0f, 0.49f);
            float fadeStart = Mathf.Clamp(GameConfig.ArcEndFadeStartMeters / len, 0f, fadeEnd);
            if (_fadeGradient == null) _fadeGradient = new Gradient();
            if (_fadeColorKeys == null)
            {
                _fadeColorKeys = new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f),
                };
                _fadeAlphaKeys = new GradientAlphaKey[4];
            }
            _fadeAlphaKeys[0] = new GradientAlphaKey(0f, fadeStart);
            _fadeAlphaKeys[1] = new GradientAlphaKey(1f, fadeEnd);
            _fadeAlphaKeys[2] = new GradientAlphaKey(1f, 1f - fadeEnd);
            _fadeAlphaKeys[3] = new GradientAlphaKey(0f, 1f - fadeStart);
            _fadeGradient.SetKeys(_fadeColorKeys, _fadeAlphaKeys);
            _line.colorGradient = _fadeGradient;
        }

        private static Material RedMat => _redMat != null ? _redMat : (_redMat = BuildMaterial(new Color(0.784f, 0.078f, 0.078f)));
        private static Material BlueMat => _blueMat != null ? _blueMat : (_blueMat = BuildMaterial(new Color(0.157f, 0.557f, 0.824f)));

        private static Material BuildMaterial(Color color)
        {
            // 1:1 ArcViewer: `Arc.mat` (ArcShader) + `Arc.png`. Fallback: Unlit.
            var baseMat = Resources.Load<Material>("QuestSaberDemo/Materials/Arc");
            Material mat;
            if (baseMat != null)
            {
                mat = new Material(baseMat) { name = "Arc" };
            }
            else
            {
                var shader = Shader.Find("Sprites/Default");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                mat = new Material(shader) { name = "Arc" };
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            var tex = Resources.Load<Texture2D>("QuestSaberDemo/Textures/Arc");
            if (tex != null)
            {
                if (mat.HasProperty("_Texture")) mat.SetTexture("_Texture", tex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            }
            mat.renderQueue = 3000;
            mat.enableInstancing = true;
            return mat;
        }
    }
}

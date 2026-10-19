using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace QuestSaber
{
    /// <summary>Saber/hand side: left = red, right = blue.</summary>
    public enum SaberSide { Left = 0, Right = 1 }

    /// <summary>
    /// Saber: geometry (blade + grip), pose history for hit detection,
    /// blade direction history for preswing/follow-through (like OpenSaberPlus `LightSaber.gd`),
    /// retract/extend and trail.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class SaberView : MonoBehaviour
    {
        public SaberSide Side { get; private set; }
        public Material CrossSectionMaterial { get; private set; }
        public Transform Tip { get; private set; }
        public Transform BaseTrans { get; private set; }
        /// <summary>Room root in which the blade direction is measured (like the notes).</summary>
        public Transform Room { get; set; }

        /// <summary>Anchor point for the grip label text.</summary>
        public Transform LabelAnchor { get; private set; }

        /// <summary>Blade radius and ends in root coordinates (from the real blade mesh bounds).</summary>
        public float BladeClashRadiusBase { get; private set; }
        public Vector3 BladeClashBaseLocal { get; private set; }
        public Vector3 BladeClashTipLocal { get; private set; }

        private MeshRenderer _bladeRenderer;
        private Transform _extendPivot;
        private SaberTrail _trail;

        private Vector3 _baseScale = new Vector3(0.75f, 0.5f, 0.75f);
        private float _scaleMultiplier = 1f;

        public void SetScaleMultiplier(float s)
        {
            _scaleMultiplier = Mathf.Max(0.01f, s);
            transform.localScale = _baseScale * _scaleMultiplier;
        }

        /// <summary>
        /// Applies (or re-applies) the saber mount from GameConfig. Called once from Build and
        /// again whenever the player nudges something on the in-headset calibration page
        /// because the mount has to follow the virtual controller: the root offset is
        /// the measured aim-&gt;grip vector PLUS the (baked) controller model offset, so the saber
        /// hilt keeps sitting centred in the controller grip.
        ///
        /// Direction: the blade leaves the root at elevation "pitch" above the anchor's forward
        /// axis and azimuth "yaw" - 0/0 = straight ahead ("genau nach vorne"), +90 pitch = up.
        /// The lateral offset, yaw and the model offset are MIRRORED per side (+left / -right),
        /// exactly like the virtual controller models they have to match.
        ///
        /// The player's fine adjustment is a plain 3-axis POSITION delta in the same anchor
        /// frame (SaberAlignOffset: X lateral/mirrored, Y up/down, Z forward/back) plus the three
        /// angle deltas - together the six degrees of freedom of the "Saber X/Y/Z + pitch/yaw/roll"
        /// rows. Nothing here is computed along the blade axis any more: "grip 5 mm lower" is a Y
        /// nudge the player can reason about instead of a slide along a 47.7 deg tilted axis.
        /// </summary>
        public void ApplyMount()
        {
            float sgn = Side == SaberSide.Left ? 1f : -1f;

            float pitch = GameConfig.SaberMountPitchUpDeg + GameConfig.SaberAlignPitchDeg;
            float yaw = (GameConfig.SaberMountYawDeg + GameConfig.SaberAlignYawDeg) * sgn;
            float roll = GameConfig.SaberMountRollDeg + GameConfig.SaberAlignRollDeg;

            Vector3 o = GameConfig.SaberMountLocalOffset;
            Vector3 m = GameConfig.ControllerModelLocalOffset;
            Vector3 d = GameConfig.SaberAlignOffset;
            Vector3 pos = new Vector3((o.x + m.x) * sgn, o.y + m.y, o.z + m.z);
            pos += new Vector3(d.x * sgn, d.y, d.z);

            transform.localPosition = pos;
            // The X rotation is 90 - pitch because the saber model's blade runs along ITS +Y
            // (SaberView root frame): Euler.x = 90 - pitch puts that axis `pitch` degrees above
            // the anchor's forward axis.
            transform.localRotation = Quaternion.Euler(90f - pitch, yaw, roll);
        }

        // ---- Retract/extend ----
        public bool Retracted { get; private set; }
        private float _retractTarget = 1f;
        private const float RetractBladeScale = 0.02f;
        private const float RetractLerpSpeed = 14f;

        // ---- Pose history (LateUpdate, i.e. exactly the drawn pose) ----
        private Vector3 _prevBasePos, _prevTipPos;
        private Vector3 _curBasePos, _curTipPos;
        private float _prevSampleTime, _curSampleTime;
        private bool _hasPrevPose, _hasCurPose;

        public Vector3 PreviousBasePosition => _prevBasePos;
        public Vector3 PreviousTipPosition => _prevTipPos;
        public Vector3 CurrentBasePosition => _curBasePos;
        public Vector3 CurrentTipPosition => _curTipPos;
        public bool HasCurrentPose => _hasCurPose;
        public bool HasPreviousPose => _hasPrevPose && _hasCurPose;
        /// <summary>Score-clock distance of the two poses (&lt;= 0 if invalid).</summary>
        public float SampleTimeDelta => _curSampleTime - _prevSampleTime;

        // ---- Swing evaluation: blade direction history ----
        // 1:1 OpenSaberPlus (`LightSaber.gd`): preswing/follow-through = largest change of the
        // BLADE DIRECTION in a time window (±200 ms) around the hit - plane- and
        // direction-independent, without saturation (therefore straight == diagonal).
        private const int DirHistorySize = 64;   // ~1 s at 72 Hz
        private readonly float[] _dirTime = new float[DirHistorySize];
        private readonly Vector3[] _dirVec = new Vector3[DirHistorySize];
        // Room-local blade positions parallel to the direction - for the CENTER evaluation
        // (closest XY pass of the blade at the note center over the measurement window).
        private readonly Vector3[] _dirBase = new Vector3[DirHistorySize];
        private readonly Vector3[] _dirTip = new Vector3[DirHistorySize];
        private int _dirCount;   // valid entries
        private int _dirWrite;   // next write index

        /// <summary>Room-local blade direction of the current pose.</summary>
        public Vector3 CurrentBladeDirectionLocal { get; private set; }

        /// <summary>
        /// Largest change (degrees) of the blade direction in the score-clock window
        /// [<paramref name="fromTime"/>, <paramref name="toTime"/>] relative to
        /// <paramref name="reference"/>. 1:1 OpenSaberPlus `get_preswing_angle`
        /// (window BEFORE the hit) or `get_followthrough_angle` (after it).
        /// </summary>
        public float MaxBladeDeviation(Vector3 reference, float fromTime, float toTime)
        {
            float max = 0f;
            for (int i = 0; i < _dirCount; i++)
            {
                int idx = _dirWrite - 1 - i;
                if (idx < 0) idx += DirHistorySize;
                float t = _dirTime[idx];
                if (t < fromTime || t > toTime) continue;
                float angle = Vector3.Angle(reference, _dirVec[idx]);
                if (angle > max) max = angle;
            }
            return max;
        }

        /// <summary>Records the blade direction + positions of the current pose on the score clock.</summary>
        private void RecordBladeDirection()
        {
            Vector3 d = _curTipPos - _curBasePos;
            if (d.sqrMagnitude < 1e-10f) return;
            d = Room != null ? Room.InverseTransformDirection(d) : d;
            CurrentBladeDirectionLocal = d.normalized;

            _dirTime[_dirWrite] = _curSampleTime;
            _dirVec[_dirWrite] = CurrentBladeDirectionLocal;
            if (Room != null)
            {
                _dirBase[_dirWrite] = Room.InverseTransformPoint(_curBasePos);
                _dirTip[_dirWrite] = Room.InverseTransformPoint(_curTipPos);
            }
            else
            {
                _dirBase[_dirWrite] = _curBasePos;
                _dirTip[_dirWrite] = _curTipPos;
            }
            _dirWrite = (_dirWrite + 1) % DirHistorySize;
            if (_dirCount < DirHistorySize) _dirCount++;
        }

        private void ClearBladeHistory()
        {
            _dirCount = 0;
            _dirWrite = 0;
        }

        /// <summary>
        /// Closest XY pass of the blade LINE at the note (<paramref name="centerLocalXY"/>,
        /// room-local, screen plane only) in the score-clock window [<paramref name="fromTime"/>,
        /// <paramref name="toTime"/>]. Returns the smallest 2D distance (base units). This makes
        /// the CENTER evaluation independent of the evaluation time: it measures how close the
        /// blade REALLY came to the note center - not where it stood in a single frame.
        /// </summary>
        public float MinBladeCenterDeviation(Vector2 centerLocalXY, float fromTime, float toTime)
        {
            float min = float.MaxValue;
            for (int i = 0; i < _dirCount; i++)
            {
                int idx = _dirWrite - 1 - i;
                if (idx < 0) idx += DirHistorySize;
                float t = _dirTime[idx];
                if (t < fromTime || t > toTime) continue;
                Vector2 pa = new Vector2(_dirBase[idx].x, _dirBase[idx].y);
                Vector2 pb = new Vector2(_dirTip[idx].x, _dirTip[idx].y);
                Vector2 ab = pb - pa;
                float k = ab.sqrMagnitude > 1e-8f
                    ? Mathf.Clamp01(Vector2.Dot(centerLocalXY - pa, ab) / ab.sqrMagnitude)
                    : 0f;
                float dev = Vector2.Distance(centerLocalXY, pa + ab * k);
                if (dev < min) min = dev;
            }
            return min == float.MaxValue ? 0f : min;
        }

        // Smoothed TIP velocity (world, 5-frame average) for the cut plane/check.
        private readonly Vector3[] _tipVelSamples = new Vector3[5];
        private int _tipVelIdx;
        private Vector3 _tipVelocity;
        public Vector3 TipVelocity => _tipVelocity;

        public Vector3 GetBladeDirection()
        {
            if (Tip == null || BaseTrans == null) return transform.up;
            Vector3 d = Tip.position - BaseTrans.position;
            return d.sqrMagnitude > 0.0001f ? d.normalized : transform.up;
        }

        // ======================================================================
        // Construction
        // ======================================================================

        public static SaberView Build(SaberSide side, Transform anchor, Material crossSectionMaterial, Color glowColor)
        {
            var go = new GameObject(side == SaberSide.Left ? "SaberLeft" : "SaberRight");
            go.layer = GameConfig.SaberLayer;
            go.transform.SetParent(anchor, false);

            // Geometry per SuperSaber: blade points forward (+Z), grip centered on the controller.
            // The mount itself lives in ApplyMount() because it has to be re-applied at runtime
            // (the in-headset calibration page changes pitch/yaw/roll and the grip position
            // without rebuilding the saber). All values there are MEASURED from the Quest 3
            // Touch Plus model; the blade points FORWARD instead of along the handle.
            // Revert saber scale to original (visual controller size only, not sabers)
            go.transform.localScale = new Vector3(0.75f, 0.5f, 0.75f);

            var view = go.AddComponent<SaberView>();
            view.Side = side;
            view._baseScale = go.transform.localScale;
            view.ApplyMount();

            // grip label anchor: axis-parallel, with the reciprocal of the saber scale.
            // The local Z is NOT a free value any more: it must place the anchor exactly on the
            // grip surface (GameConfig.GripLabelAnchorZ), otherwise the engraved text floats
            // above the handle. See GameConfig for the measured grip radius.
            var labelGo = new GameObject("GripLabelAnchor");
            labelGo.transform.SetParent(go.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0f, GameConfig.GripLabelAnchorZ);
            labelGo.transform.localRotation = Quaternion.identity;
            labelGo.transform.localScale = new Vector3(1f / go.transform.localScale.x, 1f / go.transform.localScale.y, 1f / go.transform.localScale.z);
            view.LabelAnchor = labelGo.transform;

            // ArcViewer saber model (blade + handle).
            Mesh bladeMesh = null, handleMesh = null;
            var saberModel = Resources.Load<GameObject>("QuestSaberDemo/Models/Saber");
            if (saberModel != null)
            {
                foreach (var mf in saberModel.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.name == "Blade") bladeMesh = mf.sharedMesh;
                    else if (mf.name == "Handle") handleMesh = mf.sharedMesh;
                }
            }
            Material saberBladeMat = MakeSaberMaterial("QuestSaberDemo/Materials/SaberBlade", glowColor, "SaberBlade");
            Material saberHandleMat = MakeSaberMaterial("QuestSaberDemo/Materials/SaberHandle", glowColor, "SaberHandle");

            // Grip.
            var grip = new GameObject("Grip");
            grip.layer = GameConfig.SaberLayer;
            grip.transform.SetParent(go.transform, false);
            grip.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            grip.transform.localScale = new Vector3(1.25f, 1.25f, 2.5f);
            if (handleMesh != null)
            {
                grip.AddComponent<MeshFilter>().sharedMesh = handleMesh;
                var gripMr = grip.AddComponent<MeshRenderer>();
                gripMr.sharedMaterial = saberHandleMat;
                gripMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            // Close the top of the grip.
            var gripCap = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            gripCap.name = "GripCap";
            gripCap.layer = GameConfig.SaberLayer;
            Destroy(gripCap.GetComponent<Collider>());
            gripCap.transform.SetParent(go.transform, false);
            gripCap.transform.localPosition = new Vector3(0f, 0.19f, 0f);
            gripCap.transform.localScale = new Vector3(0.05f, 0.03f, 0.05f);
            var capMr = gripCap.GetComponent<MeshRenderer>();
            if (capMr != null)
            {
                capMr.sharedMaterial = MakeFlatMaterial(new Color(0.10f, 0.10f, 0.11f), "GripCap");
                capMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            // blade at the extend pivot (base end).
            var pivot = new GameObject("BladePivot");
            pivot.transform.SetParent(go.transform, false);
            pivot.transform.localPosition = new Vector3(0f, 0.2f, 0f);
            view._extendPivot = pivot.transform;

            var blade = new GameObject("Blade");
            blade.layer = GameConfig.SaberLayer;
            blade.transform.SetParent(pivot.transform, false);
            blade.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            blade.transform.localScale = new Vector3(1.125f, 1.125f, 2.0f);
            blade.transform.localPosition = new Vector3(0f, -0.26f, 0f);
            if (bladeMesh != null)
            {
                blade.AddComponent<MeshFilter>().sharedMesh = bladeMesh;
                var bladeMr = blade.AddComponent<MeshRenderer>();
                bladeMr.sharedMaterial = saberBladeMat;
                bladeMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                view._bladeRenderer = bladeMr;
            }

            // collision geometry for saber↔saber/wall from the blade mesh.
            if (bladeMesh != null)
            {
                var b = bladeMesh.bounds;
                float half = (b.extents.x + b.extents.y) * 0.5f;
                view.BladeClashRadiusBase = Mathf.Max(0.004f, half * 1.125f);
                view.BladeClashBaseLocal = new Vector3(0f, b.min.z * 2f - 0.06f, 0f);
                view.BladeClashTipLocal = new Vector3(0f, b.max.z * 2f - 0.06f, 0f);
            }
            else
            {
                view.BladeClashRadiusBase = GameConfig.SaberBladeRadius;
                view.BladeClashBaseLocal = new Vector3(0f, 0.1f, 0f);
                view.BladeClashTipLocal = new Vector3(0f, 2.1f, 0f);
            }

            // saber tip and base (root-local): base 0.1 m, tip 2.1 (= 0.05 and 1.05 m world, respectively).
            var tipGo = new GameObject("Tip");
            tipGo.transform.SetParent(go.transform, false);
            tipGo.transform.localPosition = new Vector3(0f, 2.1f, 0f);
            var baseGo = new GameObject("Base");
            baseGo.transform.SetParent(go.transform, false);
            baseGo.transform.localPosition = new Vector3(0f, 0.1f, 0f);
            view.Tip = tipGo.transform;
            view.BaseTrans = baseGo.transform;

            var trailGo = new GameObject("Trail");
            trailGo.transform.SetParent(go.transform, false);
            view._trail = SaberTrail.Attach(trailGo, baseGo.transform, tipGo.transform, MakeSaberTrailMaterial(glowColor));

            view.CrossSectionMaterial = crossSectionMaterial;
            return view;
        }

        // ======================================================================
        // Frame core
        // ======================================================================

        private void Update()
        {
            if (!enabled) return;
            AnimateRetract();
        }

        private void LateUpdate()
        {
            if (!enabled) return;
            if (Retracted || Tip == null || BaseTrans == null)
            {
                InvalidatePose();
                ClearBladeHistory();
                return;
            }

            CapturePose();
            UpdateVelocity();
            RecordBladeDirection();
        }

        private void CapturePose()
        {
            _prevBasePos = _curBasePos;
            _prevTipPos = _curTipPos;
            _prevSampleTime = _curSampleTime;
            _hasPrevPose = _hasCurPose;

            _curBasePos = BaseTrans.position;
            _curTipPos = Tip.position;
            _curSampleTime = GameManager.ScoreTime;
            _hasCurPose = true;
        }

        private void InvalidatePose()
        {
            _hasPrevPose = false;
            _hasCurPose = false;
            ClearTipVelocity();
        }

        /// <summary>Tip velocity of the current pose (world, 5-frame average).</summary>
        private void UpdateVelocity()
        {
            if (!_hasPrevPose) return;
            float dt = SampleTimeDelta > 0.0001f ? SampleTimeDelta : Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            PushTipSample((_curTipPos - _prevTipPos) / dt);
        }

        private void PushTipSample(Vector3 v)
        {
            _tipVelSamples[_tipVelIdx] = v;
            _tipVelIdx = (_tipVelIdx + 1) % _tipVelSamples.Length;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < _tipVelSamples.Length; i++) sum += _tipVelSamples[i];
            _tipVelocity = sum / _tipVelSamples.Length;
        }

        private void ClearTipVelocity()
        {
            Array.Clear(_tipVelSamples, 0, _tipVelSamples.Length);
            _tipVelIdx = 0;
            _tipVelocity = Vector3.zero;
        }

        // ======================================================================
        // Retract/extend, trail
        // ======================================================================

        public void SetRetracted(bool retracted)
        {
            if (Retracted == retracted) return;
            Retracted = retracted;
            _retractTarget = retracted ? RetractBladeScale : 1f;
            if (retracted)
            {
                InvalidatePose();
                ClearBladeHistory();
            }
            if (_trail != null)
            {
                _trail.Clear();
                _trail.SetVisible(!retracted);
            }
        }

        public void SetTrailActive(bool active)
        {
            if (_trail == null) return;
            _trail.Clear();
            _trail.SetVisible(active && !Retracted);
        }

        private void AnimateRetract()
        {
            if (_extendPivot == null) return;
            if (!Mathf.Approximately(_extendPivot.localScale.y, _retractTarget))
            {
                float s = Mathf.MoveTowards(_extendPivot.localScale.y, _retractTarget,
                    Time.unscaledDeltaTime * RetractLerpSpeed);
                var ls = _extendPivot.localScale;
                ls.y = s;
                _extendPivot.localScale = ls;
            }
            if (_bladeRenderer != null)
                _bladeRenderer.enabled = !(Retracted && _extendPivot.localScale.y <= RetractBladeScale + 0.001f);
        }

        // ======================================================================
        // Materials
        // ======================================================================

        private static Material MakeFlatMaterial(Color color, string name)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var mat = new Material(shader) { name = name };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            return mat;
        }

        private static Material MakeSaberMaterial(string resourcePath, Color color, string name)
        {
            var baseMat = Resources.Load<Material>(resourcePath);
            Material mat;
            if (baseMat != null) mat = new Material(baseMat) { name = name };
            else
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                mat = new Material(shader) { name = name };
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            return mat;
        }

        private static Material MakeSaberTrailMaterial(Color color)
        {
            var baseMat = Resources.Load<Material>("QuestSaberDemo/Materials/SaberTrail");
            Material mat;
            if (baseMat != null) mat = new Material(baseMat) { name = "SaberTrail" };
            else
            {
                var shader = Shader.Find("Sprites/Default");
                mat = new Material(shader) { name = "SaberTrail" };
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Brightness")) mat.SetFloat("_Brightness", 1f);
            var tex = Resources.Load<Texture2D>("QuestSaberDemo/Textures/DefaultTrail");
            if (tex != null)
            {
                if (mat.HasProperty("_TrailTexture")) mat.SetTexture("_TrailTexture", tex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            }
            return mat;
        }
    }

    [DefaultExecutionOrder(-500)]
    public sealed class XrPoseAnchorFollower : MonoBehaviour
    {
        public XRNode Node;

        private readonly List<InputDevice> _devices = new(2);

        private void Update()
        {
            _devices.Clear();
            InputDevices.GetDevicesAtXRNode(Node, _devices);
            for (int i = 0; i < _devices.Count; i++)
            {
                var d = _devices[i];
                if (!d.isValid) continue;
                // The XR device pose is in TRACKING space, i.e. relative to the XR
                // Origin - exactly like the local transform of the XRI controller objects
                // (children of "Camera Offset"). Writing it as a WORLD pose displaced the
                // sabers by the rig's world offset. Set the LOCAL pose instead.
                if (d.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos))
                    transform.localPosition = pos;
                if (d.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rot))
                    transform.localRotation = rot;
                break;
            }
        }
    }
}

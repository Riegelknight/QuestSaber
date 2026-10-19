using UnityEngine;
using UnityEngine.XR;

namespace QuestSaber
{
    public sealed class ControllerDebugView : MonoBehaviour
    {
        private static readonly ControllerDebugView[] Views = new ControllerDebugView[2];
        private static bool _visible;

        /// <summary>Session visibility (default OFF, not persisted).</summary>
        public static bool Visible => _visible;

        private SaberSide _side;

        // Set when the real Meta model was instantiated; its world size is logged from
        // OnEnable (the view is created hidden, so a coroutine could not be started in
        // Build() - coroutines need an active GameObject).
        private GameObject _modelInstance;
        // Parent transform of the model. The model inherits the anchor (aim) pose, which is the
        // pose the Meta model is authored in (and the pose the saber mount is built on);
        // GameConfig.ControllerModelLocal* apply on top of it. The old "raw grip pose" mode is
        // gone - it rotated the model by the aim<->grip angle and pushed it into the wrist.
        private Transform _modelRoot;
        private bool _sizeLogged;
        private bool _poseLogged;

        // Shared debug materials (created once, then used by both hands).
        private static Material _matX, _matY, _matZ, _matOrigin, _matProxy;

        private static readonly Color ColorX = new Color(1.00f, 0.15f, 0.15f);
        private static readonly Color ColorY = new Color(0.25f, 1.00f, 0.25f);
        private static readonly Color ColorZ = new Color(0.30f, 0.55f, 1.00f);
        private static readonly Color ColorOrigin = new Color(1.00f, 0.85f, 0.10f);
        private static readonly Color ColorProxy = new Color(0.85f, 0.85f, 0.90f);

        /// <summary>
        /// Attaches the debug view to a controller anchor (idempotent per side).
        /// The call happens AFTER hiding the template models; the
        /// marker component additionally protects the debug objects from the
        /// repeated hiding (HideControllerModelsRoutine).
        /// </summary>
        public static ControllerDebugView Attach(SaberSide side, Transform anchor)
        {
            if (anchor == null) return null;
            var existing = Views[(int)side];
            if (existing != null) return existing;

            var go = new GameObject("ControllerDebug");
            go.transform.SetParent(anchor, false);
            var view = go.AddComponent<ControllerDebugView>();
            view._side = side;
            view.Build();
            Views[(int)side] = view;
            go.SetActive(_visible);
            return view;
        }

        /// <summary>Toggles both debug controllers on/off (session, not persistent).</summary>
        public static void SetVisible(bool visible)
        {
            _visible = visible;
            for (int i = 0; i < Views.Length; i++)
                if (Views[i] != null) Views[i].gameObject.SetActive(visible);
        }

        public static void Toggle() => SetVisible(!_visible);

        // ---------- Construction ----------

        private void Build()
        {
            BuildOriginGizmo();
            BuildModel();
        }

        /// <summary>
        /// The debug view is created hidden (visible only after "Controllers & sabers") and
        /// coroutines need an active GameObject - so the size diagnostic starts here, on the
        /// first activation. Logged once per side.
        /// </summary>
        private void OnEnable()
        {
            if (_modelRoot != null && !_poseLogged)
            {
                _poseLogged = true;
                StartCoroutine(LogGripPoseRoutine());
            }
            if (_sizeLogged || _modelInstance == null) return;
            _sizeLogged = true;
            StartCoroutine(LogModelSizeRoutine(_modelInstance));
        }

        private static void DestroySafe(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        /// <summary>
        /// Origin sphere + axis cross at the RAW tracking origin. Stays in place during
        /// every later calibration - only this way can one see where the pose
        /// points and how far the model/saber deviates from it.
        /// </summary>
        private void BuildOriginGizmo()
        {
            var origin = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            origin.name = "Origin";
            DestroySafe(origin.GetComponent<Collider>());
            origin.transform.SetParent(transform, false);
            origin.transform.localScale = Vector3.one * 0.013f;
            SetMaterial(origin, OriginMat());

            AddAxis("AxisX", Vector3.right, XMat());
            AddAxis("AxisY", Vector3.up, YMat());
            AddAxis("AxisZ", Vector3.forward, ZMat());
        }

        private void AddAxis(string axisName, Vector3 direction, Material mat)
        {
            const float length = 0.10f;
            var axis = GameObject.CreatePrimitive(PrimitiveType.Cylinder); // cylinder lies along Y
            axis.name = axisName;
            DestroySafe(axis.GetComponent<Collider>());
            axis.transform.SetParent(transform, false);
            // Standard cylinder: height 2, radius 0.5 -> scale to length/width.
            axis.transform.localScale = new Vector3(0.0035f, length * 0.5f, 0.0035f);
            axis.transform.localRotation = Quaternion.FromToRotation(Vector3.up, direction);
            axis.transform.localPosition = direction * (length * 0.5f); // origin = start of axis
            SetMaterial(axis, mat);
        }

        private void BuildModel()
        {
            var modelRoot = new GameObject("Model");
            modelRoot.transform.SetParent(transform, false);
            _modelRoot = modelRoot.transform;

            GameObject prefab = null;
            string source = null;
            string direct = _side == SaberSide.Left
                ? "QuestControllers/LeftControllerModel"
                : "QuestControllers/RightControllerModel";
            prefab = Resources.Load<GameObject>(direct);
            source = direct;

#if UNITY_EDITOR
            if (prefab == null)
            {
                string fbxPath = _side == SaberSide.Left
                    ? "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/MetaQuestTouchPlus_Left.fbx"
                    : "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/MetaQuestTouchPlus_Right.fbx";
                prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                if (prefab != null) source = fbxPath;
            }
#endif

            if (prefab != null)
            {
                var inst = Instantiate(prefab, modelRoot.transform, false);
                inst.name = "ControllerModel";
                // Safety: do not let any colliders from the model into the scene.
                foreach (var c in inst.GetComponentsInChildren<Collider>(true)) DestroySafe(c);
                Debug.Log($"[QuestSaber] Controller-Debug {_side}: model loaded ({source}).");
                // The model belongs on the anchor (aim) pose - see GameConfig. The fine
                // tunables are applied on top; the anchor carries the model from then on.
                ApplyModelTransform();
                _modelInstance = inst; // size is logged from OnEnable (view starts hidden)
                return;
            }

            Debug.LogWarning("[QuestSaber] Controller-Debug: no controller model found - " +
                             "using proxy. Expected: Assets/Resources/" + direct);
            BuildProxy(modelRoot.transform);
        }

        /// <summary>
        /// Diagnostic: logs the REAL world size of the virtual controller in mm
        /// plus the scale chain up to the rig root, right after the model was built.
        /// The real Quest 3 Touch Plus controller is 126 x 67 x 43 mm - so if the log says
        /// e.g. "size=95mm" the chain scales it down and the cause of "looks too small" is
        /// visible in the log instead of guessed. Never throws.
        /// </summary>
        private System.Collections.IEnumerator LogModelSizeRoutine(GameObject inst)
        {
            // one frame so renderers/meshes are bound
            yield return null;

            try
            {
                if (inst == null) yield break;

                Bounds b = default;
                bool any = false;
                foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
                {
                    if (!any) { b = r.bounds; any = true; }
                    else b.Encapsulate(r.bounds);
                }

                // scale chain: model -> anchor -> camera offset -> rig root
                string chain = "";
                int guard = 0;
                for (Transform t = transform; t != null && guard < 8; t = t.parent, guard++)
                {
                    Vector3 s = t.localScale;
                    chain += t.name + "[" + s.x.ToString("F3") + "] ";
                }

                Debug.Log("[QuestSaber] Controller-Debug " + _side + " size: " +
                          (any
                              ? "worldBounds=" + (b.size * 1000f).ToString("F0") + "mm " +
                                "largest=" + (Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) * 1000f).ToString("F0") + "mm "
                              : "<no renderer> ") +
                          "| lossyScale=" + transform.lossyScale.ToString("F4") +
                          "| chain=" + chain.Trim() +
                          "| expected=126x67x43mm");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Controller size log failed: " + ex.Message);
            }
        }

        // ---------- In-headset alignment ----------

        /// <summary>Per-side sign for the MIRRORED values (lateral offset, yaw), exactly like the
        /// measurements of the real controllers: +1 left, -1 right.</summary>
        private float SideSign => _side == SaberSide.Left ? 1f : -1f;

        /// <summary>Model offset in the anchor (aim) frame: the baked value, X mirrored per side.
        /// There is no in-headset delta any more - the models are finished (see GameConfig).</summary>
        private Vector3 AlignedOffset()
        {
            Vector3 o = GameConfig.ControllerModelLocalOffset;
            return new Vector3(o.x * SideSign, o.y, o.z);
        }

        /// <summary>Baked model rotation in the anchor (aim) frame (no delta any more).</summary>
        private Vector3 AlignedEuler() => GameConfig.ControllerModelLocalEuler;

        private void ApplyModelTransform()
        {
            if (_modelRoot == null) return;
            _modelRoot.localPosition = AlignedOffset();
            _modelRoot.localRotation = Quaternion.Euler(AlignedEuler());
            _modelRoot.localScale = Vector3.one * GameConfig.ControllerModelScale;
        }

        /// <summary>Re-applies both virtual controller models and logs the wizard state. The
        /// log is the delivery channel: the saber values the player ends up with become the
        /// defaults in GameConfig, so the next build is already correct.</summary>
        public static void RefreshAlignment()
        {
            for (int i = 0; i < Views.Length; i++)
                if (Views[i] != null) Views[i].ApplyModelTransform();

            Debug.Log("[QuestSaber] Controller model (baked, no rows): offset=" +
                      GameConfig.ControllerModelLocalOffset.ToString("F3") + " m (X mirrored), euler=" +
                      GameConfig.ControllerModelLocalEuler.ToString("F1") + " deg, scale=" +
                      GameConfig.ControllerModelScale.ToString("F2") + ".");
            Debug.Log("[QuestSaber] Saber alignment (6 DOF): offset=" +
                      GameConfig.SaberAlignOffset.ToString("F3") + " m (X mirrored), pitch=" +
                      GameConfig.SaberMountPitchUpDeg + " + " + GameConfig.SaberAlignPitchDeg +
                      " deg, yaw=" + GameConfig.SaberMountYawDeg + " + " + GameConfig.SaberAlignYawDeg +
                      " deg (mirrored), roll=" + GameConfig.SaberMountRollDeg + " + " +
                      GameConfig.SaberAlignRollDeg + " deg.");
        }

        /// <summary>
        /// Diagnostic only (the model is driven by the anchor pose, see ApplyModelTransform):
        /// logs the aim anchor pose vs. the raw device (grip) pose vs. the model pose, a moment
        /// after the view becomes visible. This settles "do the virtual controllers match the
        /// physical ones?" with numbers in the log.
        /// </summary>
        private System.Collections.IEnumerator LogGripPoseRoutine()
        {
            yield return new WaitForSecondsRealtime(1.0f);
            try
            {
                var device = InputDevices.GetDeviceAtXRNode(
                    _side == SaberSide.Left ? XRNode.LeftHand : XRNode.RightHand);
                Vector3 dp = Vector3.zero;
                Quaternion dr = Quaternion.identity;
                bool hasDevice = device.isValid
                    && device.TryGetFeatureValue(CommonUsages.devicePosition, out dp)
                    && device.TryGetFeatureValue(CommonUsages.deviceRotation, out dr);

                // The device pose is in TRACKING space -> convert to world exactly like
                // LateUpdate does, otherwise the deltas below would be meaningless.
                Transform tracking = transform.parent != null ? transform.parent.parent : null;
                Vector3 devicePos = hasDevice
                    ? (tracking != null ? tracking.TransformPoint(dp) : dp)
                    : Vector3.zero;

                Vector3 anchorPos = transform.position;
                Quaternion anchorRot = transform.rotation;
                Vector3 modelPos = _modelRoot != null ? _modelRoot.position : anchorPos;
                string delta = hasDevice
                    ? (anchorPos - devicePos).magnitude.ToString("F4")
                    : "n/a";

                // Also log the ROTATIONS and the aim->grip offset/rotation expressed in
                // the ANCHOR's local frame. The world-space delta alone mixes the fixed pose
                // offset with whatever the hand happened to do, which is why "3 cm off" could not
                // be pinned down from the log. The local values are hand-independent constants, so
                // the next report contains the exact numbers the model has to be shifted by.
                string localInfo = "n/a";
                string rotInfo = "n/a";
                if (hasDevice)
                {
                    Vector3 localOff = transform.InverseTransformPoint(devicePos);
                    localInfo = localOff.ToString("F4");
                    float gripAngle = Quaternion.Angle(anchorRot, tracking != null
                        ? tracking.rotation * dr
                        : dr);
                    rotInfo = "aim->grip " + gripAngle.ToString("F1") + "deg";
                }

                Debug.Log("[QuestSaber] Controller-Debug " + _side + " pose: " +
                          "anchor(AIM)=" + anchorPos.ToString("F3") +
                          (hasDevice ? " device(GRIP)world=" + devicePos.ToString("F3") : " device=n/a") +
                          " anchor->device delta=" + delta +
                          " model=" + modelPos.ToString("F3") +
                          " model->device=" + (hasDevice ? (modelPos - devicePos).magnitude.ToString("F4") : "n/a") +
                          " scale=" + GameConfig.ControllerModelScale.ToString("F3"));

                // Local-frame values (hand-independent) + rotations, for the model alignment.
                Debug.Log("[QuestSaber] Controller-Debug " + _side + " local: " +
                          "anchor->gripLocal=" + localInfo + " m | " + rotInfo +
                          " | anchorEuler=" + anchorRot.eulerAngles.ToString("F1") +
                          (hasDevice
                              ? " gripEuler=" + (tracking != null ? tracking.rotation * dr : dr).eulerAngles.ToString("F1")
                              : "") +
                          " | modelLocal=" + (modelPos - anchorPos).magnitude.ToString("F4") + " m");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Controller grip pose log failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Minimal substitute representation (only if the SDK assets are missing): grip
        /// cylinder + head block, gray - together with the axis cross and saber this is
        /// enough for position/rotation checking.
        /// </summary>
        private static void BuildProxy(Transform parent)
        {
            var mat = ProxyMat();

            var grip = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            grip.name = "Grip";
            DestroySafe(grip.GetComponent<Collider>());
            grip.transform.SetParent(parent, false);
            grip.transform.localScale = new Vector3(0.045f, 0.07f, 0.045f); // 14 cm high
            SetMaterial(grip, mat);

            var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
            head.name = "Head";
            DestroySafe(head.GetComponent<Collider>());
            head.transform.SetParent(parent, false);
            head.transform.localPosition = new Vector3(0f, 0.09f, -0.03f);
            head.transform.localScale = new Vector3(0.06f, 0.035f, 0.09f);
            SetMaterial(head, mat);
        }

        // ---------- Material helpers (unlit, no shader dependency of the scene) ----------

        private static void SetMaterial(GameObject go, Material mat)
        {
            var r = go.GetComponent<Renderer>();
            if (r != null && mat != null) r.sharedMaterial = mat;
        }

        private static Material MakeUnlit(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            return mat;
        }

        private static Material XMat()
        {
            if (_matX == null) _matX = MakeUnlit(ColorX);
            return _matX;
        }

        private static Material YMat()
        {
            if (_matY == null) _matY = MakeUnlit(ColorY);
            return _matY;
        }

        private static Material ZMat()
        {
            if (_matZ == null) _matZ = MakeUnlit(ColorZ);
            return _matZ;
        }

        private static Material OriginMat()
        {
            if (_matOrigin == null) _matOrigin = MakeUnlit(ColorOrigin);
            return _matOrigin;
        }

        private static Material ProxyMat()
        {
            if (_matProxy == null) _matProxy = MakeUnlit(ColorProxy);
            return _matProxy;
        }
    }
}

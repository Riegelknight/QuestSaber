using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace QuestSaber.Environment
{
    public enum BackgroundMode
    {
        /// <summary>Video see-through on - the real room is visible.</summary>
        Passthrough = 0,
        /// <summary>Song environment, calm default lighting.</summary>
        Default = 1,
        /// <summary>Song environment with lightshow.</summary>
        Animated = 2
    }

    /// <summary>
    /// Controls the background environment. LateUpdate (order 1) runs AFTER the GameManager
    /// (default 0) and reads the room pose/scale in THIS frame - one frame
    /// in sync with the drawn geometry.
    /// </summary>
    [DefaultExecutionOrder(1)]
    public sealed class EnvironmentDirector : MonoBehaviour
    {
        public BackgroundMode Mode { get; private set; } = BackgroundMode.Passthrough;

        // ArcViewer default values for the fog (EnvironmentManager.EnvironmentParameters).
        private const float FogAttenuation = 1000f;
        private const float FogOffset = 0f;
        private const float FogHeight = 25f;
        private const float FogStartY = -80f;

        // ArcViewer quality level 1 (512 px height): reduces the cost of the second render pass.
        private const int BloomfogQuality = 1;

        private const float CaptureCamHeight = 2.0f;

        /// <summary>Layer of the laser glow quads (ArcViewer "LightGlow") - the ONLY
        /// layer that the bloomfog capture photographs (like ArcViewer: capture mask
        /// 2048). Structure/notes/walls would otherwise flood the fog texture.</summary>
        private const int LightGlowLayer = 11;

        private Transform _root;
        private readonly List<GameObject> _loadedObjects = new List<GameObject>();
        private string _loadedEnvironment;
        private string _loadingEnvironment;
        private string _desiredEnvironment = EnvironmentParametersTable.DefaultEnvironmentName;
        private Coroutine _loadRoutine;

        private Volume _postVolume;
        private Camera _camera;
        private Camera _captureCamera;
        private RenderTexture _captureTexture;
        private ReflectionProbe _reflectionProbe;

        // The probe hangs on the room root and re-renders when its pose/scale
        // has changed substantially (not every frame).
        private Vector3 _probeAnchorPos;
        private Quaternion _probeAnchorRot;
        private Vector3 _probeAnchorScale;
        private bool _probeAnchorValid;
        private float _nextProbeRenderTime;

        // Trackers for the ROOM-local shader globals (_RoomScale/_RoomAnchorPos): set
        // only on change (no unnecessary SetGlobal calls per frame).
        private float _roomScaleGlobals = 1f;
        private float _roomYGlobals;

        // Layers that the reflection probe captures: structure (0) + lasers (10) + glow (11).
        // Notes/walls/HUD deliberately NOT (static stage -> stable, no flicker).
        private const int ReflectionCullingMask = (1 << 0) | (1 << 10) | (1 << 11);

        private void Awake()
        {
            var rootGo = new GameObject("[ArcViewer Environment]");
            rootGo.transform.SetParent(transform, false);
            _root = rootGo.transform;

            // Pre-initialize globals so that no frame samples with a null matrix
            // (shader default would be 0 -> NaN UVs). Replaces every frame of UpdateCaptureMatrix.
            Shader.SetGlobalMatrix("_BloomfogCaptureVP", Matrix4x4.identity);
            Shader.SetGlobalMatrix("_BloomfogCaptureRP", Matrix4x4.identity);

            Bloomfog.Quality = BloomfogQuality;
            Shader.SetGlobalTexture("_BloomfogTex", Texture2D.blackTexture);
            Shader.SetGlobalFloat("_CustomFogAttenuation", 1f / FogAttenuation);
            Shader.SetGlobalFloat("_CustomFogOffset", FogOffset);
            Shader.SetGlobalFloat("_CustomFogHeightFogHeight", FogHeight);
            Shader.SetGlobalFloat("_CustomFogHeightFogStartY", FogStartY);

            // Pre-initialize the room metric neutrally (scale 1, room Y 0 = authored
            // ArcViewer look); LateUpdate keeps it up to date from the first frame.
            Shader.SetGlobalFloat("_RoomScale", 1f);
            Shader.SetGlobalVector("_RoomAnchorPos", Vector3.zero);

            EnsurePostFx();
            EnsureReflectionProbe(); // Beat Saber look: baked reflection probes (dump)
        }

        private void OnDestroy()
        {
            if (_captureTexture != null) _captureTexture.Release();
        }

        private void Update()
        {
            // Camera transparent + environment visible is always wrong: as soon as the real
            // room comes through (mode Passthrough OR Meta see-through switch on), the
            // environment must disappear - regardless of WHO set the state.
            bool seeThrough = Mode == BackgroundMode.Passthrough || MetaSeeThrough.Enabled;
            if (!seeThrough) return;

            // (a) Stage root must never be active.
            if (_root != null && _root.gameObject.activeSelf) _root.gameObject.SetActive(false);

            // (b) All renderers under the director (including hypothetical ones created
            //     outside of `_root`) are off - also covers the freshly loaded
            //     scene of a subsequently finished load operation.
            SetEnvironmentRenderers(false);

            // (c) Bloomfog capture + reflection probe + fake reflection strength off.
            SetBloomfogActive(false);
            try
            {
                if (_reflectionProbe != null && _reflectionProbe.enabled)
                {
                    _reflectionProbe.enabled = false;
                    Shader.SetGlobalFloat("_ProbeReflStrength", 0f);
                }
            }
            catch
            {
                // The probe must never block the safety net.
            }

            // (d) Keep the main camera transparent (Meta requirement: SolidColor alpha 0,
            //     allowHDR off) - also against later opaque resets.
            EnforceTransparentCamera();
        }

        // ==================================================================
        // Room synchronization (LateUpdate, after GameManager - DefaultExecutionOrder 1)
        // ==================================================================
        // The stage is a child of the room root: it inherits position/rotation/scale
        // automatically. Here only the EFFECTS are updated that the transform alone
        // cannot carry along: the shaders' fog globals (room metric) and the
        // world->capture VP of the capture. The probe re-renders on room change.
        // ==================================================================
        private void LateUpdate()
        {
            if (_root == null) return;
            Vector3 pos = _root.position;

            // ROOM-local fog metric: factor AND floor height of the room (fog band/-attenuation
            // grow/shrink with the stage; at scale 1 and room Y 0 = authored
            // ArcViewer behavior).
            float scale = _root.lossyScale.x;
            if (Mathf.Abs(scale - _roomScaleGlobals) > 0.0001f
                || Mathf.Abs(pos.y - _roomYGlobals) > 0.0001f)
            {
                _roomScaleGlobals = scale;
                _roomYGlobals = pos.y;
                Shader.SetGlobalFloat("_RoomScale", scale);
                Shader.SetGlobalVector("_RoomAnchorPos", pos);
            }

            // Capture VP only when the capture is active (otherwise the last
            // matrix would have remained; on shutdown SetBloomfogActive sets black).
            if (_captureCamera != null && _captureCamera.enabled)
                UpdateCaptureMatrix();

            TrackReflectionProbe();
        }

        private void EnforceTransparentCamera()
        {
            FindCamera();
            if (_camera == null) return;
            if (_camera.clearFlags == CameraClearFlags.SolidColor
                && _camera.backgroundColor.a < 0.01f
                && !_camera.allowHDR)
            {
                return;
            }

            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _camera.allowHDR = false;
        }

        // ==================================================================
        // Public API (from the GameManager - signatures unchanged)
        // ==================================================================

        public void ApplyMode(BackgroundMode mode)
        {
            Mode = mode;
            bool opaque = mode != BackgroundMode.Passthrough;

            SetPostProcessing(opaque);
            SetCameraClear(opaque);
            if (_root != null) _root.gameObject.SetActive(opaque);
            SetEnvironmentRenderers(opaque);
            bool lit = mode != BackgroundMode.Passthrough;
            SetBloomfogActive(lit);

            // Reflection probe (real neon reflection on the floor), as in the original.
            // The floor shader samples `unity_SpecCube0`; `_ProbeReflStrength` toggles on/off.
            try
            {
                if (_reflectionProbe != null) _reflectionProbe.enabled = lit;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Reflection probe: " + ex.Message);
            }
            Shader.SetGlobalFloat("_ProbeReflStrength", lit ? 1f : 0f);

            if (!opaque)
            {
                EnvironmentLightManager.StaticLights = true;
                return;
            }

            // Never start a coroutine on an inactive object - Unity refuses it and
            // only prints a red error ("game object ... is inactive"). Then just refresh the
            // light parameters; the load is retried once the object is active again.
            if (isActiveAndEnabled
                && _loadedEnvironment != _desiredEnvironment
                && _loadingEnvironment != _desiredEnvironment
                && _loadRoutine == null)
            {
                _loadingEnvironment = _desiredEnvironment;
                _loadRoutine = StartCoroutine(SwitchEnvironmentRoutine(_desiredEnvironment));
            }
            else
                EnvironmentLightManager.UpdateLightParameters();
        }

        /// <summary>Selects the environment (map name); loads it on demand.</summary>
        public void SetEnvironment(string environmentName, Func<float, float> toSeconds)
        {
            _desiredEnvironment = EnvironmentParametersTable.Normalize(environmentName);
            EnvironmentClock.SetConverter(toSeconds);
            EnvironmentLightManager.CurrentParameters = EnvironmentParametersTable.Get(_desiredEnvironment);
            EnvironmentColorManager.SetPalette(EnvironmentColorTable.Get(_desiredEnvironment));

            if (Mode == BackgroundMode.Passthrough) return;
            if (!isActiveAndEnabled) return;   // No coroutine on an inactive object
            if (_loadedEnvironment == _desiredEnvironment) return;
            if (_loadingEnvironment == _desiredEnvironment) return;
            if (_loadRoutine != null) return;
            _loadingEnvironment = _desiredEnvironment;
            _loadRoutine = StartCoroutine(SwitchEnvironmentRoutine(_desiredEnvironment));
        }

        public void AttachToRoom(Transform roomRoot)
        {
            if (_root == null || roomRoot == null) return;
            _root.SetParent(roomRoot, false);
            _root.localPosition = Vector3.zero;
            _root.localRotation = Quaternion.identity;
            _root.localScale = Vector3.one;
        }

        /// <summary>
        /// Keeps the reflection probe at the room pose (it hangs as a child on the room root and
        /// inherits pose + scale by itself) and re-renders it when the pose OR
        /// scale has changed substantially. Since the stage remains static relative to the probe
        /// (self-reflection), ONE render per substantial change is correct -
        /// physically turning the player does NOT move the stage (no re-render),
        /// room rotation/offset in the menu, room follow or live scaling however do.
        /// </summary>
        private void TrackReflectionProbe()
        {
            if (_reflectionProbe == null || !_reflectionProbe.enabled || _root == null) return;
            Vector3 pos = _root.position;
            Quaternion rot = _root.rotation;
            Vector3 scale = _root.lossyScale;
            bool changed = !_probeAnchorValid
                || (pos - _probeAnchorPos).sqrMagnitude > 0.05f * 0.05f
                || Quaternion.Angle(_probeAnchorRot, rot) > 0.5f
                || (scale - _probeAnchorScale).sqrMagnitude > 0.001f * 0.001f;
            if (!changed) return;
            if (Time.unscaledTime < _nextProbeRenderTime)
            {
                // Room follow (stage moves every frame): throttle active. Update pose trackers
                // only on the actual render, so that after the
                // throttle expires it is guaranteed to render once more.
                _probeAnchorValid = false;
                return;
            }
            _probeAnchorPos = pos;
            _probeAnchorRot = rot;
            _probeAnchorScale = scale;
            _probeAnchorValid = true;
            // 0.35 s instead of 0.15 s: with "room follows player" the stage moves every
            // frame, so the cubemap (6 faces) would be re-rendered ~7x per second - a visible
            // frame pacing spike on Quest. The reflections are low-res and blurry, so a slower
            // cadence is not noticeable but is measurably smoother.
            _nextProbeRenderTime = Time.unscaledTime + 0.35f;
            try
            {
                _reflectionProbe.RenderProbe();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Reflection probe: render failed: " + ex.Message);
            }
        }

        /// <summary>Every frame from the GameManager.</summary>
        public void Tick(float songTime, float songBeat)
        {
            EnvironmentClock.CurrentTime = songTime;
            EnvironmentClock.CurrentBeat = songBeat;
            if (Mode == BackgroundMode.Passthrough) return;

            if (Mode == BackgroundMode.Animated) EnvironmentLightManager.UpdateLights();
            if (_camera != null) _camera.backgroundColor = EnvironmentLightManager.CurrentBackground;
        }

        /// <summary>
        /// Load the map's lightshow. Maps without light events get the auto lightshow
        /// so that the environment in mode "Animated" is always alive.
        /// </summary>
        public void LoadLightshow(BeatmapChart chart, SongMeta meta, Func<float, float> toSeconds)
        {
            EnvironmentClock.SetConverter(toSeconds);

            EnvironmentColorManager.SetPalette(EnvironmentColorTable.Get(_desiredEnvironment));
            var palette = EnvironmentColorManager.CurrentColors;
            if (meta != null && meta.HasEnvironmentColors)
            {
                palette.LightColor1 = meta.EnvColorLeft;
                palette.LightColor2 = meta.EnvColorRight;
                palette.BoostLightColor1 = meta.EnvColorLeftBoost;
                palette.BoostLightColor2 = meta.EnvColorRightBoost;
            }

            EnvironmentLightManager.ResetAll();

            var types = new List<LightEventType>();
            var beats = new List<float>();
            var values = new List<int>();
            var floats = new List<float>();
            var hasColor = new List<bool>();
            var colors = new List<Color>();
            var extras = new List<LightEventExtras>();
            bool animated = Mode == BackgroundMode.Animated;

            // Environment-specific lightshow behaviour (back-laser flip on Dragons/FitBeat)
            // has to be known BEFORE the events are read - see EnvironmentLightManager.
            EnvironmentLightManager.ApplyEnvironment(_desiredEnvironment);

            int mapEventCount = 0;
            if (animated && chart != null && chart.LightEvents != null)
            {
                foreach (var e in chart.LightEvents)
                {
                    types.Add((LightEventType)e._type);
                    beats.Add(e._time);
                    values.Add(e._value);
                    floats.Add(e._floatValue);
                    hasColor.Add(e._hasCustomColor);
                    colors.Add(e._customColor);
                    extras.Add(e.Extras);
                }
                mapEventCount = chart.LightEvents.Count;
            }

            var boostBeats = new List<float>();
            var boostVals = new List<bool>();
            if (animated && chart != null && chart.BoostEvents != null)
            {
                foreach (var b in chart.BoostEvents)
                {
                    boostBeats.Add(b._time);
                    boostVals.Add(b._boost);
                }
            }

            bool autoShow = false;
            if (animated && types.Count == 0 && boostBeats.Count == 0)
            {
                float total = 0f;
                if (chart != null && chart._notes != null)
                    foreach (var n in chart._notes)
                        if (n._time > total) total = n._time;
                AutoLightshow.Populate(types, beats, values, floats, hasColor, colors,
                                       boostBeats, boostVals, total + 32f);
                autoShow = true;
            }

            EnvironmentLightManager.LoadEvents(types, beats, values, floats, hasColor, colors,
                                               extras, boostBeats, boostVals);

            // One conclusive line for "animated shows nothing": mode, how many events
            // came from the MAP (0 + auto=True means the map has no lightshow at all), how
            // many boost events there are - and how many of the events carry custom extras
            // (lightID/nameFilter/easing/ring parameters), because those are what the
            // ArcViewer driver adds on top of the vanilla lightshow.
            int extraCount = 0;
            int lightIdCount = 0;
            for (int i = 0; i < extras.Count; i++)
            {
                if (extras[i] == null) continue;
                extraCount++;
                if (extras[i]._lightID != null && extras[i]._lightID.Length > 0) lightIdCount++;
            }
            Debug.Log($"[QuestSaber] Lightshow: mode={Mode}, map events={mapEventCount}, " +
                      $"boost={boostBeats.Count}, auto-show={autoShow}, extras={extraCount} " +
                      $"(lightID={lightIdCount}), flipBackLasers={EnvironmentLightManager.FlipBackLasers}, " +
                      $"environment={_desiredEnvironment}.");

            if (Mode == BackgroundMode.Default) EnvironmentLightManager.StaticLights = true;
            EnvironmentLightManager.UpdateLightParameters();
        }

        // ==================================================================
        // Bloomfog capture camera
        // ==================================================================
        private void EnsureCaptureCamera()
        {
            if (_captureCamera != null) return;

            var go = new GameObject("[Bloomfog Capture]");
            // Child of the stage root at local height CaptureCamHeight: the capture is
            // ROOM-FIXED (same pose) and scales with the room (height = 2.0 x roomScale).
            go.transform.SetParent(_root, false);
            go.transform.localPosition = new Vector3(0f, CaptureCamHeight, 0f);
            go.transform.localRotation = Quaternion.identity;
            _captureCamera = go.AddComponent<Camera>();
            _captureCamera.enabled = false;
            _captureCamera.clearFlags = CameraClearFlags.SolidColor;
            _captureCamera.backgroundColor = Color.black;
            _captureCamera.allowHDR = true;
            _captureCamera.allowMSAA = false;
            _captureCamera.depthTextureMode = DepthTextureMode.None;
            // ArcViewer 1:1: the capture photographs ONLY the glow layer (the large
            // soft laser glow quads). Without a mask the ENTIRE scene (notes,
            // walls, structure) ended up in the fog texture - cost without benefit and wrong look.
            _captureCamera.cullingMask = 1 << LightGlowLayer;

            var data = _captureCamera.GetUniversalAdditionalCameraData();
            if (data != null)
            {
                data.allowXRRendering = false;   // mono, helper camera
                data.renderPostProcessing = false;
            }
        }

        // ==================================================================
        // Reflection probe - Beat Saber look (dump: `BakedReflectionProbe`).
        // Realtime probe, but ViaScripting + rendered ONCE per environment load
        // (128 px, only the static stage) - the performant VR path instead of SSR. The
        // floor shader samples `unity_SpecCube0` (URP GlobalIllumination.hlsl).
        // ==================================================================
        private void EnsureReflectionProbe()
        {
            if (_reflectionProbe != null) return;
            try
            {
                var go = new GameObject("[Reflection Probe]");
                // Child of the stage root: pose AND size follow the room (size 160x80x160
                // and center (0,12,0) are local, scaled with roomScale).
                go.transform.SetParent(_root, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                var probe = go.AddComponent<ReflectionProbe>();
                probe.mode = ReflectionProbeMode.Realtime;
                probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
                probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
                probe.resolution = 128;
                probe.cullingMask = ReflectionCullingMask;
                probe.size = new Vector3(160f, 80f, 160f);
                probe.center = new Vector3(0f, 12f, 0f);
                probe.nearClipPlane = 0.1f;
                probe.farClipPlane = 400f;
                probe.intensity = 1f;
                probe.enabled = false;
                _reflectionProbe = probe;
            }
            catch (Exception ex)
            {
                // No game break: without a probe the floor keeps the fake bloomfog gloss.
                _reflectionProbe = null;
                Debug.LogWarning("[QuestSaber] Reflection probe could not be created: " + ex.Message);
            }
        }

        /// <summary>Renders the reflection probe once (after the environment load).</summary>
        private void RefreshReflectionProbe()
        {
            if (_reflectionProbe == null || !_reflectionProbe.enabled) return;
            try
            {
                if (_root != null)
                {
                    // The probe hangs as a child on the room root and is thus already at the
                    // stage pose/scale. Set trackers so that TrackReflectionProbe does not
                    // immediately trigger the freshly rendered reflection again.
                    _probeAnchorPos = _root.position;
                    _probeAnchorRot = _root.rotation;
                    _probeAnchorScale = _root.lossyScale;
                    _probeAnchorValid = true;
                }
                _reflectionProbe.RenderProbe();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Reflection probe: render failed: " + ex.Message);
            }
        }

        private void SetBloomfogActive(bool on)
        {
            if (!on)
            {
                Bloomfog.CaptureCamera = null;
                if (_captureCamera != null) _captureCamera.enabled = false;
                // Set the shader neutral immediately (otherwise the last fog texture would remain).
                Shader.SetGlobalTexture("_BloomfogTex", Texture2D.blackTexture);
                return;
            }

            EnsureCaptureCamera();
            FindCamera();
            if (_captureTexture == null && _camera != null)
            {
                // Size like ArcViewer: reference height x aspect ratio of the main camera.
                int refHeight = new[] { 256, 512, 1024, 2048, 4096 }[Mathf.Clamp(BloomfogQuality, 0, 4)];
                int width = Mathf.Max(16, Mathf.RoundToInt(refHeight * _camera.aspect));
                _captureTexture = new RenderTexture(width, refHeight, 0, RenderTextureFormat.DefaultHDR)
                {
                    name = "BloomfogCapture",
                    useMipMap = false,
                    autoGenerateMips = false
                };
                _captureTexture.Create();
            }
            if (_captureCamera != null)
            {
                _captureCamera.targetTexture = _captureTexture;
                _captureCamera.enabled = true;
            }
            Bloomfog.CaptureCamera = _captureCamera;
        }

        /// <summary>Every frame (LateUpdate): update the VP of the ROOM-fixed capture.
        /// The camera itself is already correct as a child of the room root - only the
        /// aspect ratio (from the main camera) and the world->capture VP matrix must
        /// be fresh per frame.</summary>
        private void UpdateCaptureMatrix()
        {
            if (_captureCamera == null || _camera == null || _root == null) return;
            _captureCamera.aspect = _camera.aspect;

            // VP of the (room-fixed) capture: world -> capture NDC for the shader UVs. The
            // shaders project world points with `_BloomfogCaptureVP` into the camera view space -
            // this way fog and fake reflections remain STAGE-FIXED: room rotation/offset
            // (joystick/menu) rotates EVERYTHING, head/body movement of the player rotates
            // NOTHING. Capture height and stage scale together (both under the room root).
            var vp = _captureCamera.projectionMatrix * _captureCamera.worldToCameraMatrix;
            Shader.SetGlobalMatrix("_BloomfogCaptureVP", vp);
            // Rotation world->capture (normal projection for the reflection offset).
            Shader.SetGlobalMatrix("_BloomfogCaptureRP",
                Matrix4x4.TRS(Vector3.zero, _root.rotation, Vector3.one));
        }

        /// <summary>Toggles ALL renderers under the EnvironmentDirector (passthrough safety).</summary>
        private void SetEnvironmentRenderers(bool visible)
        {
            var renderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                renderers[i].enabled = visible;
            }
        }

        // ==================================================================
        // Load/unload scenes
        // ==================================================================
        private IEnumerator SwitchEnvironmentRoutine(string environmentName)
        {
            DestroyLoadedObjects();

            var load = SceneManager.LoadSceneAsync(environmentName, LoadSceneMode.Additive);
            if (load == null)
            {
                Debug.LogWarning($"[QuestSaber] Environment scene '{environmentName}' not loadable " +
                                 "(missing from the Build Settings?).");
                _loadingEnvironment = null;
                _loadRoutine = null;
                yield break;
            }

            while (!load.isDone) yield return null;

            var scene = SceneManager.GetSceneByName(environmentName);
            if (scene.IsValid() && scene.isLoaded)
            {
                if (_desiredEnvironment != environmentName)
                {
                    SceneManager.UnloadSceneAsync(scene);
                    _loadingEnvironment = null;
                    _loadRoutine = null;
                    TryStartPendingLoad();
                    yield break;
                }

                var managerScene = gameObject.scene;
                foreach (var go in scene.GetRootGameObjects())
                {
                    SceneManager.MoveGameObjectToScene(go, managerScene);
                    // worldPositionStays = FALSE is the DIRECTION fix: the stage hangs as a
                    // child on the room root (`_root` in `_gameRoot`, identity) and inherits
                    // its position + yaw + scale; with "false" the authored
                    // local transform is preserved. With "true" the authored WORLD rotation
                    // would be frozen - then the background would NOT rotate with the notes
                    // (reported direction discrepancy).
                    go.transform.SetParent(_root, false);
                    _loadedObjects.Add(go);
                }
                SceneManager.UnloadSceneAsync(scene);
            }

            _loadedEnvironment = environmentName;
            if (_root != null) _root.gameObject.SetActive(Mode != BackgroundMode.Passthrough);
            LogHierarchyOrphans();
            EnvironmentLightManager.UpdateLightParameters();
            RefreshReflectionProbe();
            Debug.Log($"[QuestSaber] Environment loaded: {environmentName}.");
            _loadingEnvironment = null;
            _loadRoutine = null;
        }

        /// <summary>Starts the currently desired environment load operation, if allowed.
        /// Also called after a DISCARDED (outdated) load operation - there it pulls in
        /// the later requested name that SetEnvironment had only noted.</summary>
        private void TryStartPendingLoad()
        {
            if (Mode == BackgroundMode.Passthrough) return;
            if (!isActiveAndEnabled) return;   // No coroutine on an inactive object
            if (_loadedEnvironment == _desiredEnvironment) return;
            if (_loadingEnvironment == _desiredEnvironment) return;
            if (_loadRoutine != null) return;
            _loadingEnvironment = _desiredEnvironment;
            _loadRoutine = StartCoroutine(SwitchEnvironmentRoutine(_desiredEnvironment));
        }

        private void LogHierarchyOrphans()
        {
            if (_root == null) return;
            int orphans = 0;
            string examples = "";
            for (int i = 0; i < _loadedObjects.Count; i++)
            {
                if (_loadedObjects[i] == null) continue;
                foreach (var renderer in _loadedObjects[i].GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || renderer.transform.IsChildOf(_root)) continue;
                    if (orphans < 3) examples += (orphans > 0 ? " | " : "") + TransformPath(renderer.transform);
                    orphans++;
                }
            }
            if (orphans == 0)
                Debug.Log($"[QuestSaber] Hierarchy ok: all {_loadedObjects.Count} loaded root(s) " +
                          "are under the room anchor.");
            else
                Debug.LogWarning($"[QuestSaber] {orphans} renderers outside the room anchor " +
                                 $"(do NOT rotate with it); examples: {examples}");
        }

        // Small helper: hierarchy path of a transform for diagnostics.
        private static string TransformPath(Transform t)
        {
            if (t == null) return "<null>";
            var parts = new System.Collections.Generic.List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private void DestroyLoadedObjects()
        {
            for (int i = 0; i < _loadedObjects.Count; i++)
            {
                if (_loadedObjects[i] != null) Destroy(_loadedObjects[i]);
            }
            _loadedObjects.Clear();
            _loadedEnvironment = null;

            for (int i = 0; i < EnvironmentParametersTable.Supported.Length; i++)
            {
                var orphan = SceneManager.GetSceneByName(EnvironmentParametersTable.Supported[i]);
                if (orphan.IsValid() && orphan.isLoaded) SceneManager.UnloadSceneAsync(orphan);
            }
        }

        // ==================================================================
        // Camera + post-processing (bloom + grading) - only in Default/Animated
        // ==================================================================
        private void EnsurePostFx()
        {
            if (_postVolume != null) return;

            var go = new GameObject("[Environment PostFX]");
            go.transform.SetParent(transform, false);
            _postVolume = go.AddComponent<Volume>();
            _postVolume.isGlobal = true;
            _postVolume.priority = 100f;
            _postVolume.weight = 0f;

            // PostProcessProfile 1:1 from ArcViewer (Assets/Rendering/PostProcessProfile.asset):
            // Bloom threshold 1.0 (ONLY the overdriven lasers/glows glow - the HDR boost
            // comes from EnvironmentLightManager.LightEmission 6.5), intensity 1.5,
            // very tight scatter 0.1 (small hard halos), 4 instead of 6 iterations.
            // Tonemapping remains ABSENT (ArcViewer: None) - HDR values roll off unclamped.
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(1.5f);
            bloom.threshold.Override(1.0f);
            bloom.scatter.Override(0.1f);
            bloom.maxIterations.Override(4); // ArcViewer: compact blur chain (URP 14: maxIterations instead of skipIterations)
            bloom.highQualityFiltering.Override(false);

            // Color grading like ArcViewer: dark shadows, slightly pulled highlights,
            // minimal gamma lift - makes the neon colors "pop".
            var smh = profile.Add<ShadowsMidtonesHighlights>(true);
            smh.shadows.Override(new Vector4(1f, 1f, 1f, -0.3937f));
            smh.midtones.Override(new Vector4(1f, 1f, 1f, 0f));
            smh.highlights.Override(new Vector4(1f, 1f, 1f, -0.1575f));
            smh.shadowsStart.Override(0f);
            smh.shadowsEnd.Override(0.15f);
            var lgg = profile.Add<LiftGammaGain>(true);
            lgg.gamma.Override(new Vector4(1f, 1f, 1f, 0.0591f));

            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.25f);
            vignette.smoothness.Override(0.4f);
            _postVolume.profile = profile;
        }

        private void FindCamera()
        {
            if (_camera != null) return;
            _camera = Camera.main;
            if (_camera == null) _camera = FindAnyObjectByType<Camera>(FindObjectsInactive.Include);
        }

        private void SetPostProcessing(bool on)
        {
            if (_postVolume != null) _postVolume.weight = on ? 1f : 0f;
            FindCamera();
            if (_camera == null) return;
            var data = _camera.GetUniversalAdditionalCameraData();
            if (data != null) data.renderPostProcessing = on;
        }

        // Passthrough needs a transparent background (Meta requirement, see MetaSeeThrough).
        private void SetCameraClear(bool opaque)
        {
            FindCamera();
            if (_camera == null) return;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = opaque
                ? EnvironmentLightManager.CurrentBackground
                : new Color(0f, 0f, 0f, 0f);

            // HDR follows the background mode. The ArcViewer look (Bloomfog, neon, emissive
            // materials) is authored for an HDR pipeline - the original MrSaber renderer had
            // m_SupportsHDR = 1 - so the opaque environment renders with HDR again. Passthrough
            // stays LDR: there the camera must keep a real ALPHA channel (HDR buffers are
            // R11G11B10/RGBA16F and Meta needs a solid alpha 0 background), and it also saves
            // bandwidth exactly where the environment is not visible.
            _camera.allowHDR = opaque;
        }
    }
}

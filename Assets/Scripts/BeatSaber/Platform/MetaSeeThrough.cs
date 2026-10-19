// ==============================================================================
//  MetaSeeThrough - Enables Meta Quest passthrough (Mixed Reality) via the
//  AR Foundation camera management of the MR template and keeps the camera background
//  matching the background mode.
// ==============================================================================
//  Note when changing:
//   - The actual passthrough is created in the Meta OpenXR provider: As soon as the
//     AR camera subsystem runs, `MetaOpenXRCameraSubsystem` creates a
//     passthrough composition layer and removes it again when stopping.
//     It is toggled via `ARCameraManager.enabled` - exactly the lever that
//     the template also uses in `ARFeatureController.TogglePassthrough`.
//   - DEVELOPER SWITCH (menu "Passthrough"): `Enabled` (default true). All
//     automatic calls (init/resume/focus/watchdog) ALWAYS apply the
//     current enabled state - off stays off.
//   - Guardian/boundary and foveation are NOT here, but in the
//     platform performance layer MetaPerformance.cs (boundary suppression,
//     120 Hz frame rate, foveation).
//   - Self-initializing ([RuntimeInitializeOnLoadMethod], its own
//     DontDestroyOnLoad GameObject) - no inspector wiring needed.
//  Context: architecture and version states see PROJECT_NOTES.md (project root);
//  SDK versions: ProjectInfo.cs.
// ==============================================================================
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace QuestSaber
{
    /// <summary>
    /// Enables Meta Quest passthrough ("Video SeeThrough") for the game in
    /// Beat Saber style and at the same time controls the camera background.
    ///
    /// Technique: Passthrough is NOT drawn via its own layer, but
    /// as a composition layer of the Meta OpenXR provider. The provider creates/removes
    /// it when the AR camera subsystem starts/stops. Therefore
    /// <c>ARCameraManager.enabled</c> is the switch for "passthrough on/off" - the same
    /// lever that the MR template uses in <c>ARFeatureController.TogglePassthrough</c>.
    ///
    /// The camera background is additionally set to match:
    ///   - Passthrough ON  -> SolidColor with alpha 0 (the layer shows through),
    ///   - Passthrough OFF -> opaque black (the ArcViewer environment stands on a ground).
    ///
    /// The state is additionally
    ///   - re-enforced after resume and on focus return (system menu),
    ///   - periodically (watchdog)
    /// re-enforced again.
    ///
    /// No inspector wiring needed: Initializes itself via
    /// [RuntimeInitializeOnLoadMethod] (its own DontDestroyOnLoad GameObject).
    /// </summary>
    public static class MetaSeeThrough
    {
        /// <summary>Maximum wait time until the AR camera is ready.</summary>
        public const float EnableTimeoutSeconds = 10f;

        internal const float EnableRetryIntervalSeconds = 0.5f;

        /// <summary>Interval of the watchdog that enforces the passthrough state.</summary>
        internal const float WatchdogIntervalSeconds = 10f;

        /// <summary>
        /// Developer switch (menu "Passthrough", persistently derived in
        /// <see cref="SaveData.BackgroundMode"/>): true = video see-through
        /// active (the real room is visible), false = off (the app renders normally on
        /// a black background). Default true = previous behavior.
        /// </summary>
        public static bool Enabled { get; private set; } = true;

        /// <summary>
        /// true while the passthrough composition layer of the Meta provider really exists
        /// (AR camera subsystem enabled). This is what the OS looks at: outside passthrough it
        /// REFUSES to hide the guardian boundary (see
        /// <see cref="MetaPerformance.RequestGuardianSuppression"/>).
        /// </summary>
        public static bool LayerActive { get; private set; }

        // Cached references to the template components (Main Camera in the
        // "MR Interaction Setup" prefab). They become invalid on scene change;
        // Unity's "fake null" catches that and triggers a new search.
        private static ARCameraManager _cameraManager;
        private static ARCameraBackground _cameraBackground;

        /// <summary>
        /// Sets the developer switch and applies it immediately. All automatic
        /// calls (init/watchdog/resume/focus) afterwards also apply ONLY
        /// this state - so an "off" is not turned back on by the watchdog.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            ApplySeethrough(enabled);
            Debug.Log($"[Meta] Passthrough {(enabled ? "ON" : "OFF")} (developer switch).");
        }

        /// <summary>
        /// Sets the passthrough state: choose a matching camera background and
        /// switch the AR camera subsystem (and thus the passthrough layer) on/off.
        /// </summary>
        public static void ApplySeethrough(bool enabled)
        {
            // Camera state is part of the passthrough chain: on = fully transparent
            // (compositor shows the camera image), off = opaque black (the
            // ArcViewer environment/default background becomes visible).
            ApplyCameraState(enabled);
            ApplyArPassthrough(enabled);
        }

        /// <summary>
        /// Switches the passthrough composition layer via the AR camera subsystem.
        /// If the template camera is not there yet (editor/early start), nothing
        /// happens - the init routine tries again.
        /// </summary>
        public static void ApplyArPassthrough(bool enabled)
        {
            var manager = CameraManager();
            if (manager != null && manager.enabled != enabled)
            {
                try
                {
                    manager.enabled = enabled;
                    Debug.Log($"[Meta] AR camera (passthrough layer) {(enabled ? "ON" : "OFF")}.");
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning("[Meta] AR camera could not be switched: " + ex.Message);
                }
            }

            // The layer exists exactly while the AR camera subsystem runs - the OS checks
            // this (not our developer switch) before it allows hiding the boundary.
            LayerActive = manager != null && manager.enabled;

            // The template's background component would set the clear flags; in
            // Meta passthrough the provider reports mode "None" (the layer sits
            // separately), so it has no effect - we toggle it as well for
            // cleanliness, so that it can no longer cause resets in opaque mode.
            var background = CameraBackground();
            if (background != null && background.enabled != enabled)
                background.enabled = enabled;
        }

        /// <summary>
        /// Sets the camera background to match the background mode. Passthrough
        /// (see-through ON) requires SolidColor with alpha 0; with see-through
        /// switched off, opaque black is drawn so that the virtual environment
        /// (modes Default/animated) stands on a ground.
        /// </summary>
        public static void ApplyCameraState(bool seethrough)
        {
            var cam = Camera.main;
            if (cam == null) return;

            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = seethrough ? new Color(0f, 0f, 0f, 0f) : new Color(0f, 0f, 0f, 1f);
            // HDR only outside passthrough: in see-through it does nothing (the
            // compositor shows the camera image); in Default/animated it provides the
            // bloom glow of the emissive environment.
            cam.allowHDR = !seethrough;
        }

        /// <summary>
        /// Enables the AR camera subsystem and thus the passthrough composition
        /// layer. Fixed foveation and guardian boundary lie in
        /// <see cref="MetaPerformance"/> (platform performance layer).
        /// </summary>
        private static ARCameraManager CameraManager()
        {
            if (_cameraManager == null)
            {
                _cameraManager = Object.FindAnyObjectByType<ARCameraManager>(
                    FindObjectsInactive.Include);
            }
            return _cameraManager;
        }

        private static ARCameraBackground CameraBackground()
        {
            if (_cameraBackground == null)
            {
                _cameraBackground = Object.FindAnyObjectByType<ARCameraBackground>(
                    FindObjectsInactive.Include);
            }
            return _cameraBackground;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInit()
        {
            var go = new GameObject("[Meta] SeeThrough");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<MetaSeeThroughBehaviour>();
        }
    }

    internal sealed class MetaSeeThroughBehaviour : MonoBehaviour
    {
        private void Start()
        {
            StartCoroutine(InitRoutine());
            StartCoroutine(WatchdogRoutine());
        }

        private IEnumerator InitRoutine()
        {
            // 1. Prepare camera (transparent when ON, opaque when OFF).
            MetaSeeThrough.ApplyCameraState(MetaSeeThrough.Enabled);
            if (Camera.main == null)
                Debug.LogWarning("[Meta] SeeThrough: No Camera.main found - camera background not adjusted.");

            // Developer switch off (e.g. on a save that stored "off"):
            // do not turn it on at all - the GameManager sets the value again after loading
            // the save, so here we just avoid an unnecessary on.
            if (!MetaSeeThrough.Enabled) yield break;

            // 2. Enable AR camera until it is there (the camera is created at runtime).
            float elapsed = 0f;
            while (elapsed < MetaSeeThrough.EnableTimeoutSeconds)
            {
                // Switched off in the meantime? The GameManager applies the stored
                // background mode only after loading the save (Default/animated -> OFF).
                // Without this check the loop would turn passthrough back on after that
                // switch-off anyway -> camera transparent WHILE the environment
                // is visible ("stage/rings in passthrough"). So check on every iteration.
                if (!MetaSeeThrough.Enabled) yield break;

                MetaSeeThrough.ApplyArPassthrough(true);
                if (Camera.main != null)
                {
                    Debug.Log("[Meta] SeeThrough enabled (passthrough layer of the AR camera).");
                    yield break;
                }

                elapsed += MetaSeeThrough.EnableRetryIntervalSeconds;
                yield return new WaitForSeconds(MetaSeeThrough.EnableRetryIntervalSeconds);
            }
        }

        /// <summary>
        /// Keeps the passthrough state in place permanently - even if the runtime
        /// briefly resets it on system menu transitions.
        /// </summary>
        private IEnumerator WatchdogRoutine()
        {
            var wait = new WaitForSeconds(MetaSeeThrough.WatchdogIntervalSeconds);
            while (true)
            {
                yield return wait;
                // Enforce the current developer state (on OR off).
                MetaSeeThrough.ApplySeethrough(MetaSeeThrough.Enabled);
            }
        }

        private void OnApplicationPause(bool pause)
        {
            // On resume, re-apply the current developer state.
            if (!pause)
            {
                MetaSeeThrough.ApplySeethrough(MetaSeeThrough.Enabled);
                Debug.Log($"[Meta] SeeThrough re-applied after resume ({(MetaSeeThrough.Enabled ? "ON" : "OFF")}).");
            }
        }

        private void OnApplicationFocus(bool focus)
        {
            // Additionally catch the case when, e.g. via the system menu, the background
            // is toggled and one returns (focus return without a preceding pause).
            if (focus)
            {
                MetaSeeThrough.ApplySeethrough(MetaSeeThrough.Enabled);
            }
        }
    }
}

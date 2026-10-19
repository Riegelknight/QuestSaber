// ==============================================================================
//  MetaExtras - Quest 3 / Horizon OS extras that MetaPerformance does not cover:
//    1. Eye buffer sharpening (OVRPlugin, CAS-like)
//    2. Local dimming          (LCD contrast; OVRPlugin - no OVRManager needed)
//    3. Environment depth occlusion (AROcclusionManager + ARShaderOcclusion)
//  Everything is defensive: an unsupported API is logged and stays inert, so a
//  device without support behaves exactly as before.
// ==============================================================================
//  Occlusion: the OpenXR feature "Meta Quest: Occlusion", the AROcclusionManager
//  and the ARShaderOcclusion component are already part of this project (MR
//  Interaction Setup prefab). The depth map is only REQUESTED when the arena
//  shaders consume it, because it costs GPU time - see
//  GameConfig.EnableEnvironmentOcclusion. This class owns the switch and writes
//  the state into the log, so a device log proves whether depth arrives.
// ==============================================================================
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace QuestSaber
{
    /// <summary>Quest 3 quality extras and the environment-depth plumbing.</summary>
    public static class MetaExtras
    {
        /// <summary>Delay before the first OVRPlugin use (session warm-up).</summary>
        internal const float FirstAttemptDelaySeconds = 2f;
        /// <summary>Retry interval while OVRPlugin is not initialized yet.</summary>
        internal const float RetrySeconds = 0.5f;
        /// <summary>Give up after this long; the extras then stay off.</summary>
        internal const float InitTimeoutSeconds = 15f;
        /// <summary>Wait before the occlusion state is read back from the device.</summary>
        internal const float OcclusionVerifySeconds = 2.5f;

        private static bool _qualityApplied;

        /// <summary>
        /// Eye buffer sharpening + local dimming. Called once, as soon as
        /// OVRPlugin reports itself initialized. Idempotent.
        /// </summary>
        public static void ApplyQualityExtras()
        {
            if (_qualityApplied) return;
            _qualityApplied = true;

            ApplyEyeBufferSharpening();
            ApplyLocalDimming();
        }

        /// <summary>Quest 3 sharpening of the eye buffer (cheap perceived sharpness).</summary>
        private static void ApplyEyeBufferSharpening()
        {
            if (!GameConfig.EnableEyeBufferSharpening) return;

            try
            {
                const OVRPlugin.LayerSharpenType type = OVRPlugin.LayerSharpenType.Normal;
                bool ok = OVRPlugin.SetEyeBufferSharpenType(type);
                Debug.Log("[Meta] Eye buffer sharpening: " + (ok ? "applied" : "rejected by runtime") +
                          " (type=" + type + ")");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Meta] Eye buffer sharpening failed: " + ex.Message);
            }
        }

        /// <summary>Quest 3 local dimming (better blacks; no OVRManager instance required).</summary>
        private static void ApplyLocalDimming()
        {
            if (!GameConfig.EnableLocalDimming) return;

            try
            {
                bool supported = OVRPlugin.localDimmingSupported;
                if (!supported)
                {
                    Debug.Log("[Meta] Local dimming: not supported on this device - skipped.");
                    return;
                }

                OVRPlugin.localDimming = true;
                Debug.Log("[Meta] Local dimming: requested=true, effective=" + OVRPlugin.localDimming);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Meta] Local dimming failed: " + ex.Message);
            }
        }

        /// <summary>Finds the occlusion manager of the scene (may be inactive).</summary>
        public static AROcclusionManager FindOcclusionManager()
        {
            try
            {
                return UnityEngine.Object.FindAnyObjectByType<AROcclusionManager>(FindObjectsInactive.Include);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Applies <see cref="GameConfig.EnableEnvironmentOcclusion"/>. While the flag is
        /// off the depth map is explicitly NOT requested (it would cost GPU time without
        /// a visible effect, because the arena shaders do not consume the keywords yet).
        /// </summary>
        public static bool ApplyOcclusion()
        {
            var manager = FindOcclusionManager();
            if (manager == null)
            {
                Debug.Log("[Meta] Occlusion: no AROcclusionManager in the scene - nothing to do.");
                return false;
            }

            if (!GameConfig.EnableEnvironmentOcclusion)
            {
                try { manager.enabled = false; } catch { }
                Debug.Log("[Meta] Occlusion: OFF by configuration (GameConfig.EnableEnvironmentOcclusion=false). " +
                          "OpenXR feature, manager and ARShaderOcclusion are ready; the arena shaders still " +
                          "have to consume XR_HARD_OCCLUSION/XR_SOFT_OCCLUSION.");
                return false;
            }

            try
            {
                manager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Medium;
                manager.requestedHumanStencilMode = HumanSegmentationStencilMode.Disabled;
                manager.requestedHumanDepthMode = HumanSegmentationDepthMode.Disabled;
                manager.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.PreferEnvironmentOcclusion;
                manager.enabled = true;

                var shaderOcclusion = manager.GetComponent<ARShaderOcclusion>();
                if (shaderOcclusion == null)
                    shaderOcclusion = manager.gameObject.AddComponent<ARShaderOcclusion>();
                shaderOcclusion.enabled = true;
                shaderOcclusion.occlusionShaderMode = AROcclusionShaderMode.SoftOcclusion;

                Debug.Log("[Meta] Occlusion: requested (depth=" + manager.requestedEnvironmentDepthMode +
                          ", mode=" + shaderOcclusion.occlusionShaderMode + ")");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Meta] Occlusion setup failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Reads the occlusion state back from the device a moment after enabling it.
        /// This is the line that proves (in a device log) whether environment depth
        /// really arrives - the prerequisite for the shader support.
        /// </summary>
        public static void LogOcclusionState()
        {
            var manager = FindOcclusionManager();
            if (manager == null) return;

            try
            {
                Debug.Log("[Meta] Occlusion state: enabled=" + manager.enabled +
                          ", currentDepth=" + manager.currentEnvironmentDepthMode +
                          ", preference=" + manager.currentOcclusionPreferenceMode +
                          ", temporalSmoothing=" + manager.environmentDepthTemporalSmoothingEnabled);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Meta] Occlusion state could not be read: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Self-initializing driver for <see cref="MetaExtras"/>. Waits for OVRPlugin
    /// (like <see cref="MetaPerformance"/>), then applies the extras once.
    /// </summary>
    internal sealed class MetaExtrasBehaviour : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInit()
        {
            try
            {
                var go = new GameObject("[Meta] Extras");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<MetaExtrasBehaviour>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Meta] Extras init failed: " + ex.Message);
            }
        }

        private IEnumerator Start()
        {
            if (Application.isEditor)
            {
                Debug.Log("[Meta] Extras: editor - sharpening/local dimming/occlusion are device-only.");
                yield break;
            }

            yield return new WaitForSecondsRealtime(MetaExtras.FirstAttemptDelaySeconds);

            float waited = MetaExtras.FirstAttemptDelaySeconds;
            while (!OVRPlugin.initialized && waited < MetaExtras.InitTimeoutSeconds)
            {
                yield return new WaitForSecondsRealtime(MetaExtras.RetrySeconds);
                waited += MetaExtras.RetrySeconds;
            }

            if (!OVRPlugin.initialized)
            {
                Debug.LogWarning("[Meta] Extras: OVRPlugin was not initialized within " +
                                 MetaExtras.InitTimeoutSeconds + " s - sharpening/local dimming skipped.");
            }
            else
            {
                MetaExtras.ApplyQualityExtras();
            }

            bool occlusionRequested = MetaExtras.ApplyOcclusion();
            if (occlusionRequested)
            {
                yield return new WaitForSecondsRealtime(MetaExtras.OcclusionVerifySeconds);
                MetaExtras.LogOcclusionState();
            }
        }
    }
}

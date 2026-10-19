using UnityEngine;

namespace QuestSaber
{
    public static class ProjectInfo
    {
        // ---------- Unity / Render Pipelines (see ProjectVersion.txt, manifest.json) ----------
        public const string UnityVersion = "6000.6.5f1";       // Unity 6.6
        public const string UrpVersion = "17.6.0";             // com.unity.render-pipelines.universal

        // ---------- XR stack (see manifest.json) ----------
        public const string XrManagementVersion = "4.7.0";      // com.unity.xr.management
        public const string OpenXrVersion = "1.18.0";           // com.unity.xr.openxr
        public const string XrInteractionToolkitVersion = "3.6.1"; // com.unity.xr.interaction.toolkit
        public const string XrHandsVersion = "1.9.0";           // com.unity.xr.hands
        public const string InputSystemVersion = "1.20.0";      // com.unity.inputsystem

        // ---------- Meta XR SDK (com.meta.xr.sdk.all) ----------
        /// <summary>Version of the Meta XR all-in-one package from manifest.json.</summary>
        public const string MetaSdkVersion = "207.0.0";
        /// <summary>Meta OpenXR provider (AR Foundation / passthrough) from manifest.json.</summary>
        public const string MetaOpenXrVersion = "2.6.1";
        /// <summary>AR Foundation (passthrough camera, AR camera management).</summary>
        public const string ArFoundationVersion = "6.6.2";

        public const string DeviceName = "Meta Quest 3";

        // ---------- Build stamp ----------
        /// <summary>
        /// Human-readable build stamp, taken from the RUNNING app: Application.version is
        /// ProjectSettings.bundleVersion, so the log can
        /// never claim to belong to a different APK than the one that produced it. That went wrong
        /// once - the log copied off the headset did not belong to the APK it was compared
        /// against, because this was a hardcoded string that was simply forgotten during a build.
        /// </summary>
        public static string BuildStamp => "QuestSaber " + Application.version + " (log-time stamp)";

        /// <summary>
        /// Logs all version states at game start (called in GameBootstrap.Boot).
        /// </summary>
        public static void LogVersions()
        {
            Debug.Log("[QuestSaber] Build: " + BuildStamp);
            DebugLogFile.Mark("build: " + BuildStamp);
            Debug.Log(
                $"[QuestSaber] Versions - Unity {UnityVersion} | URP {UrpVersion} | " +
                $"XRI {XrInteractionToolkitVersion} | OpenXR {OpenXrVersion} | " +
                $"XR Management {XrManagementVersion} | XR Hands {XrHandsVersion} | " +
                $"Input System {InputSystemVersion} | " +
                $"Meta XR {MetaSdkVersion} (meta-openxr {MetaOpenXrVersion}, ARFoundation {ArFoundationVersion}) | " +
                $"Device {DeviceName}");

            // Runtime hardware: decisive for the passthrough question.
            Debug.Log($"[QuestSaber] Hardware (Unity) - Model {SystemInfo.deviceModel} | " +
                      $"Name {SystemInfo.deviceName} | OS {SystemInfo.operatingSystem} | " +
                      $"GPU {SystemInfo.graphicsDeviceName}");
        }
    }
}

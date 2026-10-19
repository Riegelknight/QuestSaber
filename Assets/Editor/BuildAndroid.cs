// ==============================================================================
//  BuildAndroid - headless / in-editor Android (Quest 3) build helper.
// ==============================================================================
//  Why: A full player build is the strongest verification available without a
//  headset. It exercises IL2CPP/AOT, shader stripping/variants, scene loading,
//  the manifest post-processor, the keystore path and asset bundling - i.e. every
//  step that runtime tests would otherwise catch only on device.
//
//  1) In-Editor:  menu "QuestSaber > Build Android APK" (no editor shutdown).
//  2) Headless (CI):
//       Unity.exe -batchmode -nographics -quit -buildTarget Android ^
//         -projectPath "Y:\Unity\QuestSaber" ^
//         -executeMethod QuestSaber.EditorTools.BuildAndroid.PerformBuild ^
//         -logFile "<log>"
//
//  Output: <project>/Builds/QuestSaber-<version>.apk - EXACTLY ONE APK, always the
//          newest version, so the file to install is never ambiguous.
//
//  Build logs belong to Unity and must NOT end up in Builds\ (that folder exists only
//  to hand the APK to the player): pass -logFile "<project>/Logs/build.log".
//
//  After a successful build the APK is ALSO pushed into the connected headset's
//  Download folder (Tools/PushApkToQuest.ps1 over MTP - no ADB, no developer mode,
//  nothing on the headset is ever deleted: a taken name simply gets -2, -3, ...).
//  No headset connected is NOT a build failure (exit code 0 stays 0).
//
//  Headless exit code: 0 = success, 1 = failure.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using QuestSaber.Environment;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace QuestSaber.EditorTools
{
    public static class BuildAndroid
    {
        private const string OutputDir = "Builds";

        /// <summary>
        /// Single, version-stamped APK name. Builds\ must contain exactly the one APK the
        /// player has to install - no second copy, no build log.
        /// </summary>
        private static string ApkPath()
        {
            return Path.Combine(OutputDir, $"QuestSaber-{PlayerSettings.bundleVersion}.apk");
        }

        /// <summary>
        /// Interactive entry point. Builds an APK and reports to the Console but
        /// never calls EditorApplication.Exit, so the open Editor stays alive.
        /// </summary>
        [MenuItem("QuestSaber/Build Android APK", priority = 100)]
        public static void BuildFromMenu()
        {
            Build(exitWhenDone: false);
        }

        /// <summary>
        /// Headless entry point for <c>-executeMethod</c>. Exits the Unity process
        /// with a proper exit code so CI can detect failure.
        /// </summary>
        public static void PerformBuild()
        {
            Build(exitWhenDone: true);
        }

        private static void Build(bool exitWhenDone)
        {
            try
            {
                // Force a plain APK (not an App Bundle) so the path below is valid.
                EditorUserBuildSettings.buildAppBundle = false;

                // Force the classic ACTIVITY application entry point. Unity 6's
                // GameActivity entry point cannot show the Quest system keyboard overlay at all -
                // TouchScreenKeyboard reports "status=Visible" forever while Horizon OS never
                // draws the keyboard (Unity bug, reproduced on Quest 2/3 with Unity 6.5 and
                // exactly our manifest). The Meta XR SDK "prefers" GameActivity (a store rule),
                // but this build is installed manually and a working keyboard is mandatory.
                // Enforced here so a stray editor setting cannot silently break name input again;
                // Assets/Plugins/Android/AndroidManifest.xml carries the matching activity name.
                if (PlayerSettings.Android.applicationEntry != AndroidApplicationEntry.Activity)
                {
                    PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;
                    Debug.Log("[Build] Application entry point enforced to Activity (system keyboard).");
                }
                Debug.Log("[Build] ApplicationEntry=" + PlayerSettings.Android.applicationEntry +
                          " (must be Activity, see the keyboard fix note above)");

                // Refuse to build when a "Fix All"/"Apply All" in the Meta XR Project
                // Validation has silently rewritten critical settings.
                VerifyCriticalProjectState();

                Directory.CreateDirectory(OutputDir);
                string apk = ApkPath();

                var options = new BuildPlayerOptions
                {
                    scenes = GetEnabledScenes(),
                    locationPathName = apk,
                    target = BuildTarget.Android,
                    options = BuildOptions.None
                };

                // A rebuild of the SAME version writes to exactly the same file name, so Unity
                // would overwrite the APK of the previous build before ArchiveOlderApks() can
                // see it (that method runs after the build and must skip the build target).
                // Move the previous file aside FIRST.
                ProtectBuildTargetApk(apk);

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                Debug.Log($"[Build] Result={summary.result} size={summary.totalSize} " +
                          $"errors={summary.totalErrors} warnings={summary.totalWarnings} " +
                          $"time={summary.totalTime}");

                if (summary.result == BuildResult.Succeeded)
                {
                    Debug.Log("[Build] OK -> " + Path.GetFullPath(apk));
                    ArchiveOlderApks(apk);   // Builds\ must hold exactly this one APK
                    // Headless/CI switch: with QUESTSABER_NO_PUSH=1 the APK is only built
                    // and NOTHING is written to a connected headset.
                    // (System.Environment is spelled out: "Environment" alone would resolve
                    //  to the QuestSaber.Environment namespace imported above.)
                    if (System.Environment.GetEnvironmentVariable("QUESTSABER_NO_PUSH") == "1")
                        Debug.Log("[Build] APK push skipped (QUESTSABER_NO_PUSH=1) - " +
                                  "nothing was changed on the headset.");
                    else
                        DeliverApk(apk);   // push to the headset Download folder (best effort)
                    if (exitWhenDone) EditorApplication.Exit(0);
                }
                else
                {
                    Debug.LogError("[Build] FAILED -> " + summary.result);
                    if (exitWhenDone) EditorApplication.Exit(1);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Build] Exception: " + ex);
                if (exitWhenDone) EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// Pre-build guard against the Meta XR Project Validation "Fix All"/"Apply All" button.
        /// That button silently rewrites player settings, the manifest and the OpenXR settings to
        /// Meta's *store* defaults - and every one of those is a DELIBERATE deviation in this
        /// project:
        ///   - androidApplicationEntry: Activity -> GameActivity => the Quest system keyboard can
        ///     never be shown again (exactly the bug),
        ///   - the manifest activity/theme then no longer match that entry point,
        ///   - Application SpaceWarp enabled (never tested in this project),
        ///   - telemetry project_guid filled in, splash switched to passthrough, Quest 1 dropped
        ///     from the supported device list, extra permission/BOUNDARY_VISIBILITY entries.
        /// The build does NOT silently repair those files (that would also undo future
        /// intentional changes) - it FAILS LOUDLY and names the exact recovery command.
        /// </summary>
        private static void VerifyCriticalProjectState()
        {
            var problems = new List<string>();
            var ok = new List<string>();

            // ---- Assets/Plugins/Android/AndroidManifest.xml --------------------------------
            string manifestPath = Path.Combine(Application.dataPath, "Plugins", "Android", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                problems.Add("AndroidManifest.xml is missing: " + manifestPath);
            }
            else
            {
                // Strip XML comments first: the manifest documents the GameActivity/AppCompat
                // topic inside a comment, and a guard must never fire on its own documentation.
                string m = System.Text.RegularExpressions.Regex.Replace(
                    File.ReadAllText(manifestPath), @"<!--.*?-->", string.Empty,
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                if (m.Contains("UnityPlayerGameActivity"))
                    problems.Add("AndroidManifest.xml: UnityPlayerGameActivity is the launcher activity again " +
                                 "- GameActivity cannot show the system keyboard.");
                else if (!m.Contains("android:name=\"com.unity3d.player.UnityPlayerActivity\""))
                    problems.Add("AndroidManifest.xml: the launcher activity is not UnityPlayerActivity.");
                else ok.Add("launcher activity UnityPlayerActivity");

                if (m.Contains("Theme.AppCompat"))
                    problems.Add("AndroidManifest.xml: the activity theme is a Theme.AppCompat - that theme only " +
                                 "fits the GameActivity entry point.");
                else if (!m.Contains("Theme.Black.NoTitleBar.Fullscreen"))
                    problems.Add("AndroidManifest.xml: the classic fullscreen activity theme is missing.");
                else ok.Add("classic fullscreen theme");

                if (m.Contains("passthrough-contextual"))
                    problems.Add("AndroidManifest.xml: com.oculus.ossplash.background was switched to " +
                                 "\"passthrough-contextual\".");
                else ok.Add("black splash background");

                bool telemetryValue = false;
                foreach (string line in m.Split('\n'))
                    if (line.Contains("com.oculus.telemetry.project_guid") && line.Contains("android:value"))
                        telemetryValue = true;
                if (telemetryValue)
                    problems.Add("AndroidManifest.xml: the telemetry project_guid carries a value again.");
                else ok.Add("telemetry project_guid removed");

                if (m.Contains("com.oculus.supportedDevices") && !m.Contains("android:value=\"quest|"))
                    problems.Add("AndroidManifest.xml: com.oculus.supportedDevices no longer starts with \"quest\" " +
                                 "(Quest 1 dropped from the device list).");
                else ok.Add("supportedDevices list");

                if (m.Contains("com.oculus.permission.BOUNDARY_VISIBILITY"))
                    problems.Add("AndroidManifest.xml: com.oculus.permission.BOUNDARY_VISIBILITY was added " +
                                 "(guardian visibility is controlled in code).");

                // Boundaryless is what lets the game run without a drawn play area and
                // without the "moved too far away" popup. Losing this line silently would
                // turn the game back into a boundary-limited app.
                if (!m.Contains("com.oculus.feature.BOUNDARYLESS_APP"))
                    problems.Add("AndroidManifest.xml: com.oculus.feature.BOUNDARYLESS_APP is missing - " +
                                 "Horizon OS would demand a play area and show the boundary again.");
                else ok.Add("boundaryless app");
            }

            // ---- Assets/XR/Settings/OpenXRPackageSettings.asset ----------------------------
            string openXrPath = Path.Combine(Application.dataPath, "XR", "Settings", "OpenXRPackageSettings.asset");
            if (!File.Exists(openXrPath))
            {
                problems.Add("OpenXRPackageSettings.asset is missing: " + openXrPath);
            }
            else
            {
                string[] lines = File.ReadAllLines(openXrPath);
                int nameIndex = Array.FindIndex(lines, l => l.Trim() == "m_Name: SpaceWarpFeature Android");
                if (nameIndex < 0)
                {
                    problems.Add("OpenXRPackageSettings.asset: the SpaceWarpFeature Android block is gone.");
                }
                else
                {
                    string state = null;
                    for (int j = nameIndex + 1; j < lines.Length; j++)
                    {
                        string t = lines[j].Trim();
                        if (t.StartsWith("m_Name:")) break;              // next feature block
                        if (!t.StartsWith("m_enabled:")) continue;
                        state = t.Substring("m_enabled:".Length).Trim();
                        break;
                    }
                    if (state != "0")
                        problems.Add("OpenXRPackageSettings.asset: SpaceWarpFeature Android enabled=" +
                                     (state ?? "?") + " - Application SpaceWarp was never tested here.");
                    else ok.Add("Application SpaceWarp disabled");
                }

                // ---- Ungenutzte MR-/Raum-Features (Android) --------------------------------
                // Meta's manifest module turns EVERY enabled AR Foundation feature into a
                // permission (Plane/BoundingBox/Raycast -> USE_SCENE + USE_ANCHOR_API,
                // Anchor -> additionally IMPORT_EXPORT_IOT_MAP_DATA, hand tracking input ->
                // HAND_TRACKING). With those declared Horizon OS treats QuestSaber as a
                // room-tracking app: it demands a space setup, re-establishes the app space
                // and steals focus mid-run - the tracking jumps reported in play.
                // The game only needs the camera (passthrough) and the AR session.
                VerifyRoomFeaturesDisabled(lines, problems, ok);
            }

            // Dieselben Meta-Berechtigungen duerfen auch nicht direkt im Quell-Manifest stehen
            // (eine "Fix All"-Runde kann sie dort eintragen statt in die OpenXR-Settings).
            if (File.Exists(manifestPath))
            {
                string raw = File.ReadAllText(manifestPath);
                foreach (string permission in ForbiddenManifestPermissions)
                    if (raw.Contains(permission))
                        problems.Add("AndroidManifest.xml: " + permission + " is declared again - " +
                                     "Horizon OS then treats the app as a room-tracking app.");
            }

            // Not fatal: Meta's validator drops this resource asset in, it just means it ran.
            if (File.Exists(Path.Combine(Application.dataPath, "Resources", "OVROverlayCanvasSettings.asset")))
                Debug.LogWarning("[Build] Assets/Resources/OVROverlayCanvasSettings.asset exists - created by " +
                                 "Meta's project validation, not part of the tested build.");

            if (problems.Count > 0)
            {
                foreach (string p in problems) Debug.LogError("[Build] PROJECT STATE: " + p);
                throw new InvalidOperationException(
                    "[Build] Build aborted: " + problems.Count + " critical project setting(s) no longer match " +
                    "the configuration this app was tested with - most likely Meta XR Project Validation " +
                    "\"Fix All\"/\"Apply All\" was pressed. Recover with:\n" +
                    "    git checkout -- ProjectSettings/ProjectSettings.asset " +
                    "Assets/XR/Settings/OpenXRPackageSettings.asset Assets/Plugins/Android/AndroidManifest.xml\n" +
                    "  and remove Assets/Resources/OVROverlayCanvasSettings.asset.");
            }

            Debug.Log("[Build] Project state verified against the tested configuration: " +
                      string.Join(", ", ok) + ".");
        }

        /// <summary>
        /// AR Foundation OpenXR features (Android) that MUST stay off: each one adds the
        /// Meta permissions that turn the app into a "space aware" room-tracking app.
        /// </summary>
        private static readonly string[] ForbiddenAndroidXrFeatures =
        {
            "com.unity.openxr.feature.arfoundation-meta-plane",
            "com.unity.openxr.feature.arfoundation-meta-anchor",
            "com.unity.openxr.feature.arfoundation-meta-raycast",
            "com.unity.openxr.feature.arfoundation-meta-bounding-boxes",
            "com.unity.openxr.feature.arfoundation-meta-occlusion",
            "com.unity.openxr.feature.input.handtrackingsubsystem",
            "com.unity.openxr.feature.input.metahandtrackingaim",
        };

        /// <summary>Meta permissions that must never end up in the manifest.</summary>
        private static readonly string[] ForbiddenManifestPermissions =
        {
            "com.oculus.permission.USE_SCENE",
            "com.oculus.permission.USE_ANCHOR_API",
            "com.oculus.permission.IMPORT_EXPORT_IOT_MAP_DATA",
            "com.oculus.permission.HAND_TRACKING",
        };

        /// <summary>
        /// Fails the build when a room/MR feature is enabled again in the OpenXR settings.
        /// The settings asset keeps ONE YAML DOCUMENT PER FEATURE AND PER BUILD TARGET GROUP
        /// (the same feature exists as "... Android" and "... Standalone"), so a name-only
        /// search would check the wrong block - the feature id decides.
        /// </summary>
        private static void VerifyRoomFeaturesDisabled(string[] lines, List<string> problems, List<string> ok)
        {
            int before = problems.Count;
            var found = new Dictionary<string, bool>();   // featureId -> enabled
            string name = null, featureId = null;
            bool? enabled = null;

            for (int i = 0; i <= lines.Length; i++)
            {
                bool blockEnd = i == lines.Length || lines[i].StartsWith("--- ", StringComparison.Ordinal);
                if (blockEnd)
                {
                    if (name != null &&
                        name.EndsWith(" Android", StringComparison.Ordinal) &&
                        featureId != null &&
                        Array.IndexOf(ForbiddenAndroidXrFeatures, featureId) >= 0)
                        found[featureId] = enabled ?? true;   // no flag found -> treat as ON
                    name = null;
                    featureId = null;
                    enabled = null;
                    continue;
                }

                string t = lines[i].Trim();
                if (t.StartsWith("m_Name:", StringComparison.Ordinal))
                    name = t.Substring("m_Name:".Length).Trim();
                else if (t.StartsWith("featureIdInternal:", StringComparison.Ordinal))
                    featureId = t.Substring("featureIdInternal:".Length).Trim();
                else if (t.StartsWith("m_enabled:", StringComparison.Ordinal))
                    enabled = t.Substring("m_enabled:".Length).Trim() == "1";
            }

            foreach (string id in ForbiddenAndroidXrFeatures)
            {
                bool on;
                if (!found.TryGetValue(id, out on))
                    problems.Add("OpenXRPackageSettings.asset: the Android feature block " + id +
                                 " is gone - Meta's \"Fix All\" rewrote the OpenXR settings.");
                else if (on)
                    problems.Add("OpenXRPackageSettings.asset: Android feature " + id +
                                 " is ENABLED - Horizon OS then demands a space setup and " +
                                 "rebuilds the app space during play.");
            }
            if (problems.Count == before) ok.Add("room/MR OpenXR features disabled");
        }

        /// <summary>
        /// Keeps Builds\ unambiguous: after a successful build it holds EXACTLY ONE APK - the
        /// new one. Every older <c>QuestSaber-*.apk</c> next to it is MOVED (never deleted) into
        /// <c>Builds\archive\</c>, so the file the player has to install is always obvious and
        /// no build is lost. Never throws - housekeeping must not fail a good build.
        /// </summary>
        private static void ArchiveOlderApks(string newApk)
        {
            try
            {
                string full = Path.GetFullPath(newApk);
                foreach (string existing in Directory.GetFiles(OutputDir, "QuestSaber-*.apk"))
                {
                    if (string.Equals(Path.GetFullPath(existing), full,
                                      StringComparison.OrdinalIgnoreCase)) continue;
                    MoveIntoArchive(existing);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Build] Could not archive an older APK: " + ex.Message);
            }
        }

        /// <summary>
        /// A rebuild of the SAME version produces the same file name, so the APK of the previous
        /// build sits exactly on the build target and Unity overwrites it - invisible for
        /// <see cref="ArchiveOlderApks"/> (which runs after the build). Called BEFORE the build
        /// so the previous file is moved into the archive first and no build is ever lost.
        /// Never throws.
        /// </summary>
        private static void ProtectBuildTargetApk(string apk)
        {
            try
            {
                if (File.Exists(apk)) MoveIntoArchive(apk);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Build] Could not archive the APK at the build target: " +
                                 ex.Message);
            }
        }

        /// <summary>
        /// Moves an APK into <c>Builds\archive\</c> without ever deleting anything: if the name
        /// is already taken, a counter is appended (QuestSaber-1.0-2.apk, ...).
        /// </summary>
        private static void MoveIntoArchive(string apk)
        {
            string archive = Path.Combine(OutputDir, "archive");
            Directory.CreateDirectory(archive);

            string name = Path.GetFileNameWithoutExtension(apk);
            string target = Path.Combine(archive, Path.GetFileName(apk));
            for (int i = 2; File.Exists(target); i++)
                target = Path.Combine(archive, name + "-" + i + Path.GetExtension(apk));

            File.Move(apk, target);
            Debug.Log("[Build] Older APK archived: " + Path.GetFileName(apk) +
                      " -> Builds\\archive\\" + Path.GetFileName(target));
        }

        /// <summary>
        /// Runs after a successful build: pushes the APK into the connected headset's
        /// Download folder over MTP. Never throws - a missing headset or a failed copy
        /// must not turn a good build into a failed one.
        /// </summary>
        private static void DeliverApk(string apk)
        {
            try
            {
                string script = Path.Combine(Application.dataPath, "..", "Tools", "PushApkToQuest.ps1");
                if (!File.Exists(script))
                {
                    Debug.LogWarning("[Build] Push script missing: " + script);
                    return;
                }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.GetFullPath(script)}\" " +
                                $"-ApkPath \"{Path.GetFullPath(apk)}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    // Read to end BEFORE WaitForExit: redirected pipes deadlock otherwise.
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15 * 60 * 1000))
                    {
                        Debug.LogWarning("[Build] APK push to the headset timed out (15 min).");
                        try { p.Kill(); } catch { /* already gone */ }
                        return;
                    }

                    foreach (string line in stdout.Split('\n'))
                        if (line.Trim().Length > 0) Debug.Log("[Build] " + line.TrimEnd('\r'));

                    if (p.ExitCode == 0)
                        Debug.Log("[Build] APK delivered to the headset Download folder.");
                    else if (p.ExitCode == 1)
                        Debug.Log("[Build] No headset connected - APK is in Builds\\ only (exit 1).");
                    else
                    {
                        Debug.LogWarning($"[Build] APK push failed (exit {p.ExitCode}).");
                        if (stderr.Trim().Length > 0) Debug.LogWarning("[Build] " + stderr.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                // Delivery is best-effort: the APK in Builds\ is always the fallback.
                Debug.LogWarning("[Build] APK delivery skipped: " + ex.Message);
            }
        }

        private static string[] GetEnabledScenes()
        {
            var list = new List<string>();
            var enabled = new HashSet<string>();
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled) continue;
                list.Add(s.path);
                enabled.Add(Path.GetFileNameWithoutExtension(s.path));
            }

            if (list.Count == 0)
                throw new InvalidOperationException("[Build] No enabled scenes in Build Settings.");

            // The ArcViewer environments are loaded BY NAME at runtime
            // (EnvironmentDirector -> SceneManager.LoadSceneAsync(name, Additive)), which only
            // works for scenes that are in the build list. They were missing once: the result was
            // "no backgrounds at all, in BOTH Default and Animated", with just a warning in the
            // on-device log. Fail the build instead of shipping that again.
            var missing = new List<string>();
            foreach (var name in EnvironmentParametersTable.Supported)
                if (!enabled.Contains(name)) missing.Add(name);

            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "[Build] Environment scene(s) missing from Build Settings: " + string.Join(", ", missing) +
                    " - the ArcViewer backgrounds would not load (see EnvironmentParametersTable.Supported).");

            Debug.Log($"[Build] Scenes: {list.Count} enabled, all {EnvironmentParametersTable.Supported.Length} " +
                      "environment scenes present.");
            return list.ToArray();
        }
    }
}

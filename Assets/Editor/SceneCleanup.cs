// ==============================================================================
//  SceneCleanup - removes the Meta "Mixed Reality" template leftovers from
//  SampleScene (skybox/backdrop, XRI affordance samples, coaching/tutorial UI).
// ==============================================================================
//  Why: the project was set up from Meta's MR template, and its SampleScene still
//  contains the template's own content right next to the game rig:
//
//    * "Environment"        -> MRTemplateAssets/Models/Environment.fbx with the
//                              template's "FadeCube" material: the VR skybox /
//                              backdrop cube. The template fades it out via
//                              FadeMaterial.FadeSkybox() - but NOTHING calls that
//                              here, so it stays fully opaque. It sits in front of
//                              the video see-through layer (the game's
//                              EnvironmentDirector only hides its own renderers)
//                              and swallows distant geometry, so notes only became
//                              visible once they were close.
//    * XRI sample objects   -> "* Affordance", "Tap Tooltip", "Snap Volume",
//                              "Spatial Panel Manipulator" (XR Interaction Toolkit
//                              demo content).
//    * Template UI          -> "Coaching UI", "Tutorial Player", "Permissions
//                              Manager", "Tooltip Worldspace", hand menu.
//
//  QuestSaber builds its whole world at runtime (GameManager), so NONE of this
//  belongs to the game. The objects are DEACTIVATED (not destroyed) so that no
//  serialized reference inside the MR rig dangles - the change is reversible and
//  is written back into the scene by Unity itself (no hand-edited YAML).
//
//  Kept: the XR rig ("MR Interaction Setup"), scene lighting and everything
//  created at runtime.
//
//  Usage:
//    In-Editor: menu "QuestSaber > Clean up MR template leftovers"
//    Headless : -executeMethod QuestSaber.EditorTools.SceneCleanup.PerformCleanup
// ==============================================================================
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuestSaber.EditorTools
{
    public static class SceneCleanup
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";
        private const string TemplateAssetsRoot = "Assets/MRTemplateAssets/";
        private const string KeepPrefabPath = "Assets/MRTemplateAssets/Prefabs/MR Interaction Setup.prefab";

        // Plain scene objects (no prefab instance) that are template samples.
        private static readonly string[] LeftoverNames =
        {
            "Environment",
            "Outline Affordance",
            "BlendShape Affordance",
            "Color Affordance",
            "Interaction Affordance",
            "Audio Affordance",
            "Tap Tooltip",
            "Snap Volume",
            "Spatial Panel Manipulator"
        };

        [MenuItem("QuestSaber/Clean up MR template leftovers", priority = 110)]
        public static void CleanFromMenu()
        {
            Clean(exitWhenDone: false);
        }

        /// <summary>Headless entry point for <c>-executeMethod</c>.</summary>
        public static void PerformCleanup()
        {
            Clean(exitWhenDone: true);
        }

        private static void Clean(bool exitWhenDone)
        {
            try
            {
                // Reuse the scene if the Editor already has it open (no disruptive
                // close/reopen of whatever the user is working on).
                Scene scene = SceneManager.GetSceneByPath(ScenePath);
                if (!scene.IsValid() || !scene.isLoaded)
                    scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

                var all = UnityEngine.Object.FindObjectsByType<Transform>(
                    FindObjectsInactive.Include);

                // The XR rig is the ONLY template object the game needs; protect its whole
                // subtree (including any nested template prefabs such as controller models).
                Transform rigRoot = FindRigRoot(all);
                if (rigRoot == null)
                    Debug.LogWarning("[SceneCleanup] MR Interaction Setup rig not found - " +
                                     "no subtree protection active!");

                var turnedOff = new List<string>();

                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    if (rigRoot != null && t.IsChildOf(rigRoot)) continue;

                    var go = t.gameObject;
                    if (go == null || !go.activeSelf) continue;
                    if (!IsLeftover(go)) continue;

                    go.SetActive(false);
                    turnedOff.Add(Describe(go));
                }

                EditorSceneManager.MarkSceneDirty(scene);
                bool saved = EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();

                Debug.Log($"[SceneCleanup] deactivated {turnedOff.Count} object(s), saved={saved}");
                foreach (var s in turnedOff) Debug.Log("[SceneCleanup]   OFF  " + s);
                if (rigRoot != null) Debug.Log("[SceneCleanup]   KEEP " + Describe(rigRoot.gameObject));
            }
            catch (Exception ex)
            {
                Debug.LogError("[SceneCleanup] Exception: " + ex);
                if (exitWhenDone) EditorApplication.Exit(1);
                return;
            }

            if (exitWhenDone) EditorApplication.Exit(0);
        }

        /// <summary>The MR template's XR rig - the only template object the game needs.</summary>
        private static Transform FindRigRoot(Transform[] all)
        {
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null || t.parent != null) continue;
                if (PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject) == KeepPrefabPath)
                    return t;
            }
            return null;
        }

        private static bool IsLeftover(GameObject go)
        {
            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);

            // Meta template prefab instance (skybox/backdrop, coaching UI, tutorial,
            // hand menu, ...); the rig subtree was already excluded by the caller.
            if (!string.IsNullOrEmpty(prefabPath))
                return prefabPath.StartsWith(TemplateAssetsRoot, StringComparison.Ordinal);

            // Plain scene object: only the known sample names (so game objects that
            // happen to share a name are never touched - the game builds its world
            // at runtime anyway).
            return IsLeftoverName(go.name);
        }

        private static bool IsLeftoverName(string name)
        {
            string normalized = Normalize(name);
            for (int i = 0; i < LeftoverNames.Length; i++)
                if (Normalize(LeftoverNames[i]) == normalized) return true;
            return false;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("  ", " ").Trim();
        }

        private static string Describe(GameObject go)
        {
            string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            return string.IsNullOrEmpty(path)
                ? $"scene '{go.name}'"
                : $"prefab '{go.name}' ({path})";
        }
    }
}

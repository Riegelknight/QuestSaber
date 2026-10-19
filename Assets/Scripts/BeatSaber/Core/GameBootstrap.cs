using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuestSaber
{
    /// <summary>
    /// Starts the Beat Saber game automatically in the SampleScene (no scene YAML intervention needed):
    /// creates the GameManager; the existing passthrough chain (MetaSeeThrough) remains untouched.
    /// </summary>
    public static class GameBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            try
            {
                string scene = SceneManager.GetActiveScene() != null
                    ? SceneManager.GetActiveScene().name
                    : "";
                if (string.IsNullOrEmpty(scene) || !scene.StartsWith("SampleScene", StringComparison.Ordinal))
                {
                    Debug.Log("[QuestSaber] No SampleScene -> boot skipped.");
                    return;
                }

                ProjectInfo.LogVersions();

                var existing = GameObject.Find("[QuestSaberGame]");
                if (existing != null) return;

                var go = new GameObject("[QuestSaberGame]");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<GameManager>();
                Debug.Log("[QuestSaber] Started.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[QuestSaber] Boot failed: " + ex);
            }
        }
    }
}

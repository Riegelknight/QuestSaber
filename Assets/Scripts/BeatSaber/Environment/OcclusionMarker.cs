// ==============================================================================
//  OcclusionMarker - no-op replacement for ArcViewer's occlusion script.
// ==============================================================================
//  Used by ArcViewer's LaserDataHandler (GPU laser buffer for the
//  DiffusePlatformShader). MrSaber's DiffusePlatformShader no longer uses these
//  buffers - the file exists only so that the GUID-faithful
//  scene reference binds cleanly (otherwise "missing script ..." warning).
//  Fields 1:1 as in the original, so that scene values bind.
// ==============================================================================
using UnityEngine;

namespace QuestSaber.Environment
{
    public class OcclusionMarker : MonoBehaviour
    {
        [System.NonSerialized] public bool Dirty = true;
        [SerializeField] public float intensity = 1f;
        [SerializeField] public float brightnessCap = 0f;
        [SerializeField] public float falloff = 10f;
        [SerializeField] public float falloffSteepness = 1f;

        private void OnEnable()
        {
            // After activation the LaserDataHandler must recompute the marker.
            Dirty = true;
        }
    }
}

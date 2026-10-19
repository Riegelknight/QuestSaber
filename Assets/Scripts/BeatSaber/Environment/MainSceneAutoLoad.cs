// ==============================================================================
//  MainSceneAutoLoad - no-op replacement for ArcViewer's utility script.
// ==============================================================================
//  The adopted environment SCENES carry this script on the scene root object
//  (ArcViewer uses it to load the main scene when playing an environment directly). In
//  MrSaber this is not necessary - the file exists only so that the GUID-faithful
//  scene reference binds cleanly (otherwise "missing script ..." warning).
// ==============================================================================
using UnityEngine;

namespace QuestSaber.Environment
{
    public class MainSceneAutoLoad : MonoBehaviour
    {
    }
}

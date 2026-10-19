// ==============================================================================
//  OfflineBuildAssemblyFilter - keeps network-capable tooling out of the player.
// ==============================================================================
//  QuestSaber must run 100% offline (see PROJECT_NOTES.md section 11). A normal
//  build pulls in editor/AI tooling assemblies whose runtime halves can open
//  sockets or phone home.
//
//  IMPORTANT LIMIT: the Meta AI Agent Bridge (meta.xr.ai.agentbridge) and the
//  Immersive Debugger DevAgent / Voice SDK (Wit.ai) assemblies can NOT be
//  removed here - the Meta core runtime assembly "Oculus.VR" references them, so
//  excluding them would leave an unresolved reference and break IL2CPP. They are
//  neutralised through settings instead (DevAgentSettings.enabled = 0,
//  ImmersiveDebuggerSettings.immersiveDebuggerEnabled = 0) and are runtime-inert.
//
//  Only assemblies that NO shipped player assembly references are excluded here.
//  Names are matched without the ".dll" extension.
// ==============================================================================
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace QuestSaber.EditorTools
{
    /// <summary>Excludes unused, socket-capable tooling assemblies from the player build.</summary>
    public sealed class OfflineBuildAssemblyFilter : IFilterBuildAssemblies
    {
        public int callbackOrder => 0;

        /// <summary>
        /// Verified against the reference graph of the shipped player:
        ///  - Unity.AI.MCP.Runtime is referenced only by Unity.AI.MCP.Editor -> it ships a
        ///    Model Context Protocol server runtime that nothing in the game uses.
        ///  - Unity.AI.Tracing is referenced only by Unity.AI.Assistant.Runtime, which is
        ///    itself not part of the player build.
        /// </summary>
        private static readonly HashSet<string> Excluded = new HashSet<string>
        {
            "Unity.AI.MCP.Runtime",
            "Unity.AI.Tracing",
        };

        public string[] OnFilterAssemblies(BuildOptions buildOptions, string[] assemblies)
        {
            var kept = new List<string>(assemblies.Length);
            foreach (string assembly in assemblies)
            {
                string name = Path.GetFileNameWithoutExtension(assembly);
                if (Excluded.Contains(name))
                {
                    Debug.Log("[QuestSaber] Offline build filter: excluded assembly '" + name + "'.");
                    continue;
                }
                kept.Add(assembly);
            }
            return kept.ToArray();
        }
    }
}

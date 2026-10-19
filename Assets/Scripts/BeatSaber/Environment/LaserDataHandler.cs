// ==============================================================================
//  LaserDataHandler - GPU laser/occlusion buffer for the environment shaders.
// ==============================================================================
//  Port of ArcViewer's LaserDataHandler (GUID-faithful, hence the same file).
//  Collects every frame the light emitters (LightHandler) and occlusion markers
//  of the loaded environment and writes them into shader globals:
//    _LaserColors/_LaserOrigins/_LaserDirections/_LaserFalloffInfos
//    _OcclusionColors/_OcclusionOrigins/_OcclusionDirections/_OcclusionFalloffInfos
//  These arrays are read by the DiffusePlatformShader for the soft environment
//  lighting through the laser beams.
//
//  IMPORTANT (rotation): WORLD positions of the transforms are passed.
//  Since the whole environment (including the light emitters) hangs under the room anchor
//  (_root), position and direction rotate automatically with the room -
//  the shader computes in the same world coordinates. So NO
//  additional conversion is required, as long as nothing falls out of the hierarchy.
//
//  The serialized fields (lasers/occlusionMarkers) come from the
//  ArcViewer scenes and bind to our LightHandler/OcclusionMarker.
// ==============================================================================
using System;
using UnityEngine;

namespace QuestSaber.Environment
{
    public class LaserDataHandler : MonoBehaviour
    {
        public const int MaxLasers = 15;
        public const int MaxOcclusions = 3;

        [SerializeField] private LightHandler[] lasers;
        [SerializeField] private OcclusionMarker[] occlusionMarkers;

        private Vector4[] laserColors;
        private Vector4[] laserOrigins;
        private Vector4[] laserDirections;
        private Vector4[] laserFalloffInfos;

        private Vector4[] occlusionColors;
        private Vector4[] occlusionOrigins;
        private Vector4[] occlusionDirections;
        private Vector4[] occlusionFalloffInfos;

        private void UpdateLaserBuffer(bool forceUpdate = false)
        {
            // ArcViewer has a settings coupling here; we keep it fixed.
            const float brightnessMult = 1f;

            if (laserColors == null || laserColors.Length != MaxLasers)
            {
                laserColors = new Vector4[MaxLasers];
                laserOrigins = new Vector4[MaxLasers];
                laserDirections = new Vector4[MaxLasers];
                laserFalloffInfos = new Vector4[MaxLasers];

                if (lasers != null && lasers.Length > MaxLasers)
                    Debug.Log($"[QuestSaber] {lasers.Length} light emitters present - only " +
                              $"the first {MaxLasers} are taken into the shader buffer.");
            }

            if (lasers == null) return; // no scene binding -> everything stays dark

            bool dirty = false;
            for (int i = 0; i < MaxLasers; i++)
            {
                if (i >= lasers.Length)
                {
                    if (laserColors[i] != Vector4.zero)
                    {
                        laserColors[i] = Vector4.zero;
                        dirty = true;
                    }
                    continue;
                }

                LightHandler lightHandler = lasers[i];
                if (lightHandler == null || !lightHandler.enabled || !lightHandler.gameObject.activeInHierarchy)
                {
                    if (laserColors[i] != Vector4.zero)
                    {
                        laserColors[i] = Vector4.zero;
                        dirty = true;
                    }
                    continue;
                }

                if (!lightHandler.Dirty && !forceUpdate) continue;

                Transform lightTransform = lightHandler.transform;
                laserColors[i] = lightHandler.CurrentColor * lightHandler.diffuseMult * brightnessMult;
                laserOrigins[i] = lightTransform.position;
                laserDirections[i] = lightTransform.up;

                laserFalloffInfos[i] = new Vector4
                {
                    x = Mathf.Abs(lightTransform.lossyScale.y) / 2f, // half length
                    y = lightHandler.diffuseBrightnessCap,
                    z = lightHandler.diffuseFalloff,
                    w = lightHandler.diffuseFalloffSteepness
                };

                lightHandler.Dirty = false;
                dirty = true;
            }

            if (dirty)
            {
                Shader.SetGlobalVectorArray("_LaserColors", laserColors);
                Shader.SetGlobalVectorArray("_LaserOrigins", laserOrigins);
                Shader.SetGlobalVectorArray("_LaserDirections", laserDirections);
                Shader.SetGlobalVectorArray("_LaserFalloffInfos", laserFalloffInfos);
            }
        }

        private void UpdateOcclusionBuffer(bool forceUpdate = false)
        {
            if (occlusionColors == null || occlusionColors.Length != MaxOcclusions)
            {
                occlusionColors = new Vector4[MaxOcclusions];
                occlusionOrigins = new Vector4[MaxOcclusions];
                occlusionDirections = new Vector4[MaxOcclusions];
                occlusionFalloffInfos = new Vector4[MaxOcclusions];

                if (occlusionMarkers != null && occlusionMarkers.Length > MaxOcclusions)
                    Debug.Log($"[QuestSaber] {occlusionMarkers.Length} occlusion markers present - only " +
                              $"the first {MaxOcclusions} are taken over.");
            }

            if (occlusionMarkers == null) return;

            bool dirty = false;
            for (int i = 0; i < MaxOcclusions; i++)
            {
                if (i >= occlusionMarkers.Length)
                {
                    if (occlusionColors[i] != Vector4.zero)
                    {
                        occlusionColors[i] = Vector4.zero;
                        dirty = true;
                    }
                    continue;
                }

                OcclusionMarker marker = occlusionMarkers[i];
                if (marker == null || !marker.enabled || !marker.gameObject.activeInHierarchy)
                {
                    if (occlusionColors[i] != Vector4.zero)
                    {
                        occlusionColors[i] = Vector4.zero;
                        dirty = true;
                    }
                    continue;
                }

                if (!marker.Dirty && !forceUpdate) continue;

                Transform occlusionTransform = marker.transform;
                occlusionColors[i] = Color.white * marker.intensity;
                occlusionOrigins[i] = occlusionTransform.position;
                occlusionDirections[i] = occlusionTransform.up;

                occlusionFalloffInfos[i] = new Vector4
                {
                    x = Mathf.Abs(occlusionTransform.lossyScale.y) / 2f,
                    y = marker.brightnessCap,
                    z = marker.falloff,
                    w = marker.falloffSteepness
                };

                marker.Dirty = false;
                dirty = true;
            }

            if (dirty)
            {
                Shader.SetGlobalVectorArray("_OcclusionColors", occlusionColors);
                Shader.SetGlobalVectorArray("_OcclusionOrigins", occlusionOrigins);
                Shader.SetGlobalVectorArray("_OcclusionDirections", occlusionDirections);
                Shader.SetGlobalVectorArray("_OcclusionFalloffInfos", occlusionFalloffInfos);
            }
        }

        private void LateUpdate()
        {
            UpdateLaserBuffer();
            UpdateOcclusionBuffer();
        }

        private void OnEnable()
        {
            UpdateLaserBuffer(true);
            UpdateOcclusionBuffer(true);
            Debug.Log($"[QuestSaber] LaserDataHandler active: {lasers?.Length ?? 0} lasers, " +
                      $"{occlusionMarkers?.Length ?? 0} occlusion markers bound.");
        }
    }
}
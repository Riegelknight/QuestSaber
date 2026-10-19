// ==============================================================================
//  LightHandler - light emitter of the ArcViewer environment (port, GUID-faithful).
// ==============================================================================
//  File name/GUID remain identical to ArcViewer (LightHandler.cs.meta), so that
//  the adopted environment prefabs keep their script reference.
//  Fields (type/id/meshRenderer/glowRenderer/...) are 1:1 the serialized names.
//
//  Sets the light color every frame from the EnvironmentLightManager - including the
//  Chroma lightID logic: with lightID the manager's colour is only valid for the
//  emitters the event addresses, so each handler searches the event list for the event
//  that addresses ITS id (exactly ArcViewer LightHandler). Without lightID (vanilla)
//  the first branch is taken for every emitter and nothing differs from before.
// ==============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestSaber.Environment
{
    public class LightHandler : MonoBehaviour
    {
        public Color CurrentColor = Color.blue;
        [NonSerialized] public bool Dirty = true;

        [Header("Components")]
        [SerializeField] private MeshRenderer meshRenderer;
        [SerializeField] private MeshRenderer glowRenderer;

        [Header("Parameters")]
        [SerializeField] public LightEventType type;
        [SerializeField] public int id;
        [SerializeField] public float emissionMult = 1f;
        [SerializeField] public float glowMult = 1f;

        [Space]
        [SerializeField] public float diffuseMult = 1f;
        [SerializeField] public float diffuseBrightnessCap = 0f;
        [SerializeField] public float diffuseFalloff = 10f;
        [SerializeField] public float diffuseFalloffSteepness = 1f;

        private MaterialPropertyBlock laserProperties;
        private MaterialPropertyBlock glowProperties;

        private Color _appliedLaser;
        private Color _appliedGlow;
        private bool _applied;

        // Chroma lightID search caches (ArcViewer). Reset whenever the event list changes
        // (new song / other mode) so stale indices can never pick the wrong event.
        private List<LightEvent> _lastEventList;
        private int lastCheckIndex = -1;
        private int nextCheckIndex = -1;
        private int lastEventIndex = -1;
        private int nextEventIndex = -1;

        private void SetCurrentColor(Color newColor)
        {
            if (CurrentColor == newColor) return;
            CurrentColor = newColor;
            Dirty = true;
        }

        private void UpdateLight(LightingPropertyEventArgs eventArgs)
        {
            if (eventArgs.type != type) return;

            // New event list (song/mode change): the cached search indices belong to the old
            // list and must never be reused.
            if (!ReferenceEquals(_lastEventList, eventArgs.eventList))
            {
                _lastEventList = eventArgs.eventList;
                ResetCheckIndices();
            }

            bool useThisEvent = eventArgs.lightEvent == null || eventArgs.lightEvent.AffectsID(id);
            bool useNextEvent = eventArgs.nextEvent == null || eventArgs.nextEvent.AffectsID(id);

            Color laser;
            Color glow;
            if (useThisEvent && useNextEvent)
            {
                // The current AND the next event address this emitter, so the colour the
                // manager computed applies as is. This is ALWAYS the case in vanilla
                // lightshows (no lightID) - see AffectsID.
                SetCurrentColor(eventArgs.eventColor);
                laser = eventArgs.laserColor;
                glow = eventArgs.glowColor;
            }
            else
            {
                // Chroma lightID: one of the two does not address this emitter, so the colour
                // has to be recomputed from the neighbouring events that DO address it.
                LightEvent lightEvent;
                LightEvent nextEvent;
                if (useThisEvent)
                {
                    lightEvent = eventArgs.lightEvent;
                    nextEvent = GetNextEvent(eventArgs, eventArgs.eventIndex);
                }
                else
                {
                    int eventIndex = GetLastEventIndex(eventArgs);
                    lightEvent = eventIndex >= 0 && eventIndex < eventArgs.eventList.Count
                        ? eventArgs.eventList[eventIndex]
                        : null;
                    nextEvent = GetNextEvent(eventArgs, eventIndex);
                }

                SetCurrentColor(EnvironmentLightManager.GetEventColor(lightEvent, nextEvent));
                laser = EnvironmentLightManager.GetLaserColor(CurrentColor);
                glow = EnvironmentLightManager.GetLaserGlowColor(CurrentColor);
            }

            if (_applied && _appliedLaser == laser && _appliedGlow == glow) return;
            _appliedLaser = laser;
            _appliedGlow = glow;
            _applied = true;
            SetProperties(laser, glow);
        }

        /// <summary>Last event before the current one that addresses this emitter (-1 = none).</summary>
        private int GetLastEventIndex(LightingPropertyEventArgs eventArgs)
        {
            List<LightEvent> lightEvents = eventArgs.eventList;
            if (lightEvents == null || lightEvents.Count == 0) return -1;

            int startIndex = Mathf.Clamp(eventArgs.eventIndex - 1, 0, lightEvents.Count - 1);
            if (startIndex == lastCheckIndex) return lastEventIndex;

            if (startIndex < lastCheckIndex)
            {
                if (lastCheckIndex >= 0 && lastEventIndex < 0) return -1;
                if (lastEventIndex >= 0 && startIndex > lastEventIndex) return lastEventIndex;
            }

            // Never loop back past a spot where we already know there is no event.
            int minIndex = 0;
            if (startIndex > lastCheckIndex)
                minIndex = lastEventIndex >= 0 ? lastEventIndex : Mathf.Max(lastCheckIndex, 0);
            lastCheckIndex = startIndex;

            for (int i = startIndex; i >= minIndex; i--)
            {
                if (lightEvents[i].AffectsID(id))
                {
                    lastEventIndex = i;
                    return i;
                }
            }
            lastEventIndex = -1;
            return -1;
        }

        /// <summary>Next event after `eventIndex` that addresses this emitter (null = none).</summary>
        private LightEvent GetNextEvent(LightingPropertyEventArgs eventArgs, int eventIndex)
        {
            List<LightEvent> lightEvents = eventArgs.eventList;
            if (lightEvents == null || lightEvents.Count == 0) return null;

            int startIndex = Mathf.Clamp(eventIndex + 1, 0, lightEvents.Count - 1);
            if (startIndex == nextCheckIndex)
                return nextEventIndex >= 0 && nextEventIndex < lightEvents.Count ? lightEvents[nextEventIndex] : null;

            if (startIndex > nextCheckIndex)
            {
                if (nextCheckIndex >= 0 && nextEventIndex < 0) return null;
                if (nextEventIndex >= 0 && startIndex < nextEventIndex) return lightEvents[nextEventIndex];
            }

            int maxIndex = lightEvents.Count - 1;
            if (startIndex < nextCheckIndex)
                maxIndex = nextEventIndex >= 0 ? nextEventIndex : nextCheckIndex;
            nextCheckIndex = startIndex;

            for (int i = startIndex; i <= maxIndex; i++)
            {
                if (lightEvents[i].AffectsID(id))
                {
                    nextEventIndex = i;
                    return lightEvents[i];
                }
            }
            nextEventIndex = -1;
            return null;
        }

        private void ResetCheckIndices()
        {
            lastCheckIndex = -1;
            nextCheckIndex = -1;
            lastEventIndex = -1;
            nextEventIndex = -1;
        }

        private void SetProperties(Color laserColor, Color glowColor)
        {
            laserProperties.SetColor("_LaserColor", laserColor);
            glowProperties.SetColor("_LaserColor", glowColor);
            UpdateProperties();
        }

        private void UpdateProperties()
        {
            if (meshRenderer != null) meshRenderer.SetPropertyBlock(laserProperties);
            if (glowRenderer != null) glowRenderer.SetPropertyBlock(glowProperties);
        }

        private void Awake()
        {
            laserProperties = new MaterialPropertyBlock();
            glowProperties = new MaterialPropertyBlock();
            laserProperties.SetFloat("_ColorMult", emissionMult);
            glowProperties.SetFloat("_ColorMult", glowMult);
        }

        private void OnEnable()
        {
            EnvironmentLightManager.OnLightPropertiesChanged += UpdateLight;
            Dirty = true;
            _applied = false; // re-apply after re-enable (renderer block may be gone)
            _lastEventList = null;
            ResetCheckIndices();
        }

        private void OnDisable()
        {
            EnvironmentLightManager.OnLightPropertiesChanged -= UpdateLight;
        }
    }
}

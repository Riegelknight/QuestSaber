// ==============================================================================
//  EnvironmentLightManager - lightshow evaluation (port from ArcViewer LightManager.cs).
// ==============================================================================
//  Taken 1:1 from ArcViewer (Assets/__Scripts/Previewer/MapControl/Lightshow/
//  LightManager.cs): computes the light color of the event types per frame
//  (0 back lasers, 1 rings, 2 left lasers, 3 right lasers, 4 center lights),
//  the laser rotation speed (12/13) and the color boost. The color values
//  are sent to all LightHandlers via a static event (as in the original).
//
//  Adjustments: time source = EnvironmentClock; color set = EnvironmentColorManager;
//  static list instead of a scene component. `StaticLights = true` shows the
//  calm default lighting (mode "Default"), `false` plays the song lightshow
//  (mode "Animated").
//  Context: architecture see PROJECT_NOTES.md (project root).
// ==============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestSaber.Environment
{
    public static class EnvironmentLightManager
    {
        public const float FlashIntensity = 1.2f;

        public static float LightEmission = 6.5f;

        /// <summary>Current environment parameters (rings/lasers) - default environment.</summary>
        public static EnvironmentLightParameters CurrentParameters = new EnvironmentLightParameters();

        private static bool _staticLights = true;
        public static bool StaticLights
        {
            get => _staticLights;
            set
            {
                if (_staticLights == value) return;
                _staticLights = value;
            }
        }

        private static bool _boostActive;
        public static bool BoostActive
        {
            get => _boostActive && !StaticLights;
            set => _boostActive = value;
        }

        /// <summary>
        /// Some environments have their back/bottom lasers wired the other way round: red
        /// and blue of back-laser events are swapped there. 1:1 ArcViewer
        /// (`LightManager.backLaserFlipEnvironments`), set per environment in
        /// <see cref="ApplyEnvironment"/> BEFORE the events are read.
        /// </summary>
        public static bool FlipBackLasers { get; private set; }

        private static readonly string[] backLaserFlipEnvironments =
        {
            "DragonsEnvironment",
            "FitBeatEnvironment"
        };

        /// <summary>Environment-specific lightshow behaviour (ArcViewer sets this on every
        /// environment change). Called by the EnvironmentDirector before LoadEvents.</summary>
        public static void ApplyEnvironment(string environmentName)
        {
            bool flip = false;
            if (!string.IsNullOrEmpty(environmentName))
            {
                foreach (string name in backLaserFlipEnvironments)
                {
                    if (name == environmentName) { flip = true; break; }
                }
            }
            FlipBackLasers = flip;
        }

        public static event Action<LightingPropertyEventArgs> OnLightPropertiesChanged;
        public static event Action<LaserSpeedEvent, LightEventType> OnLaserRotationsChanged;

        private static EnvironmentColorPalette Colors => EnvironmentColorManager.CurrentColors;
        private static Color LightColor1 => BoostActive ? Colors.BoostLightColor1 : Colors.LightColor1;
        private static Color LightColor2 => BoostActive ? Colors.BoostLightColor2 : Colors.LightColor2;
        private static Color WhiteLightColor => BoostActive ? Colors.BoostWhiteLightColor : Colors.WhiteLightColor;

        public static readonly MapElementList<LightEvent> BackLaserEvents = new MapElementList<LightEvent>();
        public static readonly MapElementList<LightEvent> RingEvents = new MapElementList<LightEvent>();
        public static readonly MapElementList<LightEvent> LeftLaserEvents = new MapElementList<LightEvent>();
        public static readonly MapElementList<LightEvent> RightLaserEvents = new MapElementList<LightEvent>();
        public static readonly MapElementList<LightEvent> CenterLightEvents = new MapElementList<LightEvent>();
        public static readonly MapElementList<LaserSpeedEvent> LeftLaserSpeedEvents = new MapElementList<LaserSpeedEvent>();
        public static readonly MapElementList<LaserSpeedEvent> RightLaserSpeedEvents = new MapElementList<LaserSpeedEvent>();
        public static readonly MapElementList<BoostEvent> BoostEvents = new MapElementList<BoostEvent>();

        private static readonly LightingPropertyEventArgs[] lightPropertyEventArgs =
        {
            new LightingPropertyEventArgs { type = LightEventType.BackLasers },
            new LightingPropertyEventArgs { type = LightEventType.Rings },
            new LightingPropertyEventArgs { type = LightEventType.LeftRotatingLasers },
            new LightingPropertyEventArgs { type = LightEventType.RightRotatingLasers },
            new LightingPropertyEventArgs { type = LightEventType.CenterLights }
        };

        private static readonly MapElementList<BoostEvent>.CheckInRangeDelegate boostInRange = e => e.Time <= EnvironmentClock.CurrentTime;
        private static readonly MapElementList<LightEvent>.CheckInRangeDelegate lightInRange = e => e.Time <= EnvironmentClock.CurrentTime;
        private static readonly MapElementList<LaserSpeedEvent>.CheckInRangeDelegate laserSpeedInRange = e => e.Time <= EnvironmentClock.CurrentTime;

        /// <summary>Event list of the calm default layout (no events at all) - the args
        /// carry a list for the Chroma handlers, so it must never be null.</summary>
        private static readonly List<LightEvent> emptyEventList = new List<LightEvent>();

        /// <summary>Called every frame by the EnvironmentDirector (mode "Animated").</summary>
        public static void UpdateLights()
        {
            if (StaticLights) return;

            int lastBoostEvent = BoostEvents.GetLastIndex(EnvironmentClock.CurrentTime, boostInRange);
            BoostActive = lastBoostEvent >= 0 && BoostEvents[lastBoostEvent].Value;

            EnvironmentRingManager.UpdateRings();

            UpdateLightEventType(LightEventType.BackLasers, BackLaserEvents);
            UpdateLightEventType(LightEventType.Rings, RingEvents);
            UpdateLightEventType(LightEventType.LeftRotatingLasers, LeftLaserEvents);
            UpdateLightEventType(LightEventType.RightRotatingLasers, RightLaserEvents);
            UpdateLightEventType(LightEventType.CenterLights, CenterLightEvents);

            UpdateLaserSpeedEventType(LightEventType.LeftRotationSpeed, LeftLaserSpeedEvents);
            UpdateLaserSpeedEventType(LightEventType.RightRotationSpeed, RightLaserSpeedEvents);

            ApplyGlobalTint();
        }

        private static void UpdateLightEventType(LightEventType type, MapElementList<LightEvent> events)
        {
            int lastIndex = events.GetLastIndex(EnvironmentClock.CurrentTime, lightInRange);
            bool foundEvent = lastIndex >= 0;
            LightEvent currentEvent = foundEvent ? events[lastIndex] : null;
            bool hasNextEvent = lastIndex + 1 < events.Count;
            LightEvent nextEvent = hasNextEvent ? events[lastIndex + 1] : null;
            UpdateLightEvent(type, currentEvent, nextEvent, events, lastIndex);
        }

        private static void UpdateLightEvent(LightEventType type, LightEvent lightEvent, LightEvent nextEvent,
            List<LightEvent> events, int eventIndex)
        {
            Color eventColor = GetEventColor(lightEvent, nextEvent);
            LightingPropertyEventArgs eventArgs = lightPropertyEventArgs[(int)type];
            eventArgs.type = type;
            eventArgs.eventList = events;
            eventArgs.lightEvent = lightEvent;
            eventArgs.nextEvent = nextEvent;
            eventArgs.eventIndex = eventIndex;
            eventArgs.eventColor = eventColor;
            eventArgs.laserColor = GetLaserColor(eventColor);
            eventArgs.glowColor = GetLaserGlowColor(eventColor);
            OnLightPropertiesChanged?.Invoke(eventArgs);
        }

        private static void UpdateLaserSpeedEventType(LightEventType type, MapElementList<LaserSpeedEvent> events)
        {
            int lastIndex = events.GetLastIndex(EnvironmentClock.CurrentTime, laserSpeedInRange);
            LaserSpeedEvent lastEvent = lastIndex >= 0 ? events[lastIndex] : null;
            OnLaserRotationsChanged?.Invoke(lastEvent, type);
        }

        public static Color GetLaserColor(Color baseColor)
        {
            baseColor.a *= LightEmission;
            return baseColor;
        }

        public static Color GetLaserGlowColor(Color baseColor) => baseColor;

        public static Color GetEventColor(LightEvent lightEvent, LightEvent nextEvent)
        {
            if (lightEvent == null) return Color.clear;
            switch (lightEvent.Value)
            {
                case LightEventValue.RedOn:
                case LightEventValue.RedTransition:
                case LightEventValue.BlueOn:
                case LightEventValue.BlueTransition:
                case LightEventValue.WhiteOn:
                case LightEventValue.WhiteTransition:
                case LightEventValue.Off:
                    return GetStandardEventColor(lightEvent, nextEvent);
                case LightEventValue.RedFlash:
                case LightEventValue.WhiteFlash:
                case LightEventValue.BlueFlash:
                    return GetFlashColor(lightEvent);
                case LightEventValue.RedFade:
                case LightEventValue.BlueFade:
                case LightEventValue.WhiteFade:
                    return GetFadeColor(lightEvent);
                default:
                    return Color.clear;
            }
        }

        private static Color GetEventBaseColor(LightEvent lightEvent)
        {
            Color baseColor;
            // Chroma: per-event color takes precedence over the red/blue/white semantics
            // (exception "Off" is always transparent - see GetEventColor).
            if (lightEvent.HasCustomColor && lightEvent.Value != LightEventValue.Off)
            {
                baseColor = lightEvent.CustomColor;
            }
            else switch (lightEvent.Value)
            {
                case LightEventValue.RedOn:
                case LightEventValue.RedTransition:
                case LightEventValue.RedFlash:
                case LightEventValue.RedFade:
                    baseColor = LightColor1; break;
                case LightEventValue.BlueOn:
                case LightEventValue.BlueTransition:
                case LightEventValue.BlueFlash:
                case LightEventValue.BlueFade:
                    baseColor = LightColor2; break;
                case LightEventValue.WhiteOn:
                case LightEventValue.WhiteTransition:
                case LightEventValue.WhiteFlash:
                case LightEventValue.WhiteFade:
                    baseColor = WhiteLightColor; break;
                default:
                    baseColor = Color.clear; break;
            }
            baseColor.a *= lightEvent.FloatValue;
            return baseColor;
        }

        private static Color GetStandardEventColor(LightEvent lightEvent, LightEvent nextEvent)
        {
            Color baseColor = GetEventBaseColor(lightEvent);
            if (nextEvent != null && nextEvent.IsTransition)
            {
                Color transitionColor = GetEventBaseColor(nextEvent);
                if (lightEvent.Value == LightEventValue.Off)
                {
                    baseColor = transitionColor;
                    baseColor.a = 0f;
                }
                float transitionTime = nextEvent.Time - lightEvent.Time;
                float t = transitionTime > 0f
                    ? Mathf.Clamp01((EnvironmentClock.CurrentTime - lightEvent.Time) / transitionTime)
                    : 1f;
                // Chroma: the transition curve is per event (`customData.easing`) and may
                // run through HSV instead of RGB (ArcViewer).
                t = Easings.EasingFromType(lightEvent.TransitionEasing, t);
                baseColor = lightEvent.HsvLerp
                    ? Easings.LerpHsv(baseColor, transitionColor, t)
                    : Color.LerpUnclamped(baseColor, transitionColor, t);
            }
            return baseColor;
        }

        private static Color GetFlashColor(LightEvent lightEvent)
        {
            const float fadeTime = 0.6f;
            Color baseColor = GetEventBaseColor(lightEvent);
            float timeDifference = EnvironmentClock.CurrentTime - lightEvent.Time;
            if (timeDifference < fadeTime)
            {
                float t = timeDifference / fadeTime;
                baseColor.a *= Mathf.Lerp(FlashIntensity, 1f, Easings.Cubic.Out(t));
            }
            return baseColor;
        }

        private static Color GetFadeColor(LightEvent lightEvent)
        {
            const float fadeTime = 1.5f;
            Color baseColor = GetEventBaseColor(lightEvent);
            float timeDifference = EnvironmentClock.CurrentTime - lightEvent.Time;
            if (timeDifference >= fadeTime) baseColor.a = 0f;
            else
            {
                float t = timeDifference / fadeTime;
                baseColor.a *= Mathf.Lerp(FlashIntensity, 0f, Easings.Expo.Out(t));
            }
            return baseColor;
        }

        /// <summary>Calm default lighting (mode "Default" or maps without a lightshow).</summary>
        public static void SetStaticLayout()
        {
            var back = new LightEvent(0f, LightEventType.BackLasers, LightEventValue.BlueOn, 1f);
            var rings = new LightEvent(0f, LightEventType.Rings, LightEventValue.BlueOn, 1f);
            var left = new LightEvent(0f, LightEventType.LeftRotatingLasers, LightEventValue.RedOn, 1f);
            var right = new LightEvent(0f, LightEventType.RightRotatingLasers, LightEventValue.RedOn, 1f);
            var center = new LightEvent(0f, LightEventType.CenterLights, LightEventValue.BlueOn, 1f);

            UpdateLightEvent(LightEventType.BackLasers, back, null, emptyEventList, -1);
            UpdateLightEvent(LightEventType.Rings, rings, null, emptyEventList, -1);
            UpdateLightEvent(LightEventType.LeftRotatingLasers, left, null, emptyEventList, -1);
            UpdateLightEvent(LightEventType.RightRotatingLasers, right, null, emptyEventList, -1);
            UpdateLightEvent(LightEventType.CenterLights, center, null, emptyEventList, -1);

            OnLaserRotationsChanged?.Invoke(null, LightEventType.LeftRotationSpeed);
            OnLaserRotationsChanged?.Invoke(null, LightEventType.RightRotationSpeed);
            EnvironmentRingManager.SetStaticRings();
            ApplyGlobalTint();
        }

        /// <summary>
        /// Sets the GLOBAL colors (shader globals for background/reflections of the
        /// bloomfog skybox and the environment shaders): ring + back laser color, plus
        /// subtle fog values.
        /// </summary>
        private static void ApplyGlobalTint()
        {
            Color rings = lightPropertyEventArgs[(int)LightEventType.Rings].eventColor;
            Color back = lightPropertyEventArgs[(int)LightEventType.BackLasers].eventColor;
            // Fallback (e.g. maps without light events): base palette so that the background
            // is still colored (not black).
            if (rings.a <= 0.001f && back.a <= 0.001f)
            {
                Color c1 = Colors.LightColor1, c2 = Colors.LightColor2;
                rings = new Color(c1.r, c1.g, c1.b, 0.35f);
                back = new Color(c2.r, c2.g, c2.b, 0.35f);
            }

            float strength = BoostActive ? 0.9f : 0.6f;

            // Background color (camera clear) - dark mix of ring + back laser color.
            CurrentBackground = new Color(
                (rings.r * rings.a + back.r * back.a) * 0.5f * strength,
                (rings.g * rings.a + back.g * back.a) * 0.5f * strength,
                (rings.b * rings.a + back.b * back.a) * 0.5f * strength,
                1f);
        }

        /// <summary>Current background color (camera clear) for the EnvironmentDirector.</summary>
        public static Color CurrentBackground = new Color(0.015f, 0.015f, 0.02f, 1f);

        public static void UpdateLightParameters()
        {
            if (StaticLights) SetStaticLayout();
            else UpdateLights();
        }

        // ---------- Setup per song ----------

        public static void ClearEvents()
        {
            BackLaserEvents.Clear();
            RingEvents.Clear();
            LeftLaserEvents.Clear();
            RightLaserEvents.Clear();
            CenterLightEvents.Clear();
            LeftLaserSpeedEvents.Clear();
            RightLaserSpeedEvents.Clear();
            BoostEvents.Clear();
            EnvironmentRingManager.ClearEvents();
        }

        /// <summary>
        /// Fills the event lists from the parsed song data (Beat Saber types).
        /// `extras` carries the Chroma/Heck extras of each event (index aligned, entries may be
        /// null) - lightID, nameFilter, easing/HSV, ring/laser parameters; see
        /// <see cref="LightEventExtras"/>.
        /// </summary>
        public static void LoadEvents(List<LightEventType> types, List<float> beats,
            List<int> values, List<float> floatValues, List<bool> hasCustomColor, List<Color> customColors,
            List<LightEventExtras> extras,
            List<float> boostBeats, List<bool> boostValues)
        {
            ClearEvents();

            for (int i = 0; i < types.Count && i < beats.Count; i++)
            {
                var le = new LightEvent(beats[i], types[i], (LightEventValue)values[i],
                    i < floatValues.Count ? floatValues[i] : 1f);
                if (extras != null && i < extras.Count) le.Extras = extras[i];
                le.ApplyExtras();
                if (hasCustomColor != null && i < hasCustomColor.Count && hasCustomColor[i]
                    && customColors != null && i < customColors.Count)
                {
                    le.HasCustomColor = true;
                    le.CustomColor = customColors[i];
                }
                switch (types[i])
                {
                    case LightEventType.BackLasers:
                        // Dragons/FitBeat: their back lasers are wired the other way round,
                        // so the event's red/blue is swapped (ArcViewer FlipBackLasers).
                        if (FlipBackLasers) le.MirrorColor();
                        BackLaserEvents.Add(le);
                        break;
                    case LightEventType.Rings: RingEvents.Add(le); break;
                    case LightEventType.LeftRotatingLasers: LeftLaserEvents.Add(le); break;
                    case LightEventType.RightRotatingLasers: RightLaserEvents.Add(le); break;
                    case LightEventType.CenterLights: CenterLightEvents.Add(le); break;
                    case LightEventType.LeftRotationSpeed: LeftLaserSpeedEvents.Add(new LaserSpeedEvent(le)); break;
                    case LightEventType.RightRotationSpeed: RightLaserSpeedEvents.Add(new LaserSpeedEvent(le)); break;
                    case LightEventType.RingSpin:
                    {
                        var spin = new RingRotationEvent(le);
                        spin.SetDefaultSpeedAndProp(false);
                        EnvironmentRingManager.AllRingRotationEvents.Add(spin);

                        // Chroma nameFilter: a spin may address ONLY the small or ONLY the big
                        // rings. Without a filter it moves both (ArcViewer AddLightEvent).
                        string filter = le.Extras != null ? le.Extras._nameFilter : null;
                        EnvironmentLightParameters env = CurrentParameters;
                        if (string.IsNullOrEmpty(filter) || ContainsName(env.SmallRingNameFilters, filter))
                        {
                            var small = new RingRotationEvent(spin);
                            EnvironmentRingManager.SmallRingRotationEvents.Add(small);
                        }
                        if (string.IsNullOrEmpty(filter) || ContainsName(env.BigRingNameFilters, filter))
                        {
                            var big = new RingRotationEvent(spin);
                            big.SetDefaultSpeedAndProp(true);
                            EnvironmentRingManager.BigRingRotationEvents.Add(big);
                        }
                        break;
                    }
                    case LightEventType.RingZoom:
                        EnvironmentRingManager.RingZoomEvents.Add(new RingZoomEvent(le));
                        break;
                }
            }

            if (boostBeats != null && boostValues != null)
            {
                for (int i = 0; i < boostBeats.Count && i < boostValues.Count; i++)
                    BoostEvents.Add(new BoostEvent(boostBeats[i], boostValues[i]));
            }

            BackLaserEvents.SortElementsByBeat();
            RingEvents.SortElementsByBeat();
            LeftLaserEvents.SortElementsByBeat();
            RightLaserEvents.SortElementsByBeat();
            CenterLightEvents.SortElementsByBeat();
            LeftLaserSpeedEvents.SortElementsByBeat();
            RightLaserSpeedEvents.SortElementsByBeat();
            BoostEvents.SortElementsByBeat();
            EnvironmentRingManager.AllRingRotationEvents.SortElementsByBeat();
            EnvironmentRingManager.SmallRingRotationEvents.SortElementsByBeat();
            EnvironmentRingManager.BigRingRotationEvents.SortElementsByBeat();
            EnvironmentRingManager.RingZoomEvents.SortElementsByBeat();

            PopulateLaserRotationEventData();
            EnvironmentRingManager.PopulateRingEventData();

            // Like ArcViewer: EVERY base event (also ring spin 8/9, laser speed 12/13)
            // turns the animation on - not only the color events 0-4. Otherwise
            // maps that only have ring/laser events would remain wrongly static.
            bool hasEvents = types.Count > 0;
            StaticLights = !hasEvents;
            if (StaticLights) UpdateLightParameters();
        }

        private static void PopulateLaserRotationEventData()
        {
            int laserCount = (int)CurrentParameters.RotatingLaserCount;
            for (int i = 0; i < LeftLaserSpeedEvents.Count; i++)
            {
                LaserSpeedEvent previous = i > 0 ? LeftLaserSpeedEvents[i - 1] : null;
                LeftLaserSpeedEvents[i].PopulateRotationData(laserCount, previous, CurrentParameters);
            }
            for (int i = 0; i < RightLaserSpeedEvents.Count; i++)
            {
                LaserSpeedEvent previous = i > 0 ? RightLaserSpeedEvents[i - 1] : null;
                RightLaserSpeedEvents[i].PopulateRotationData(laserCount, previous, CurrentParameters);
            }
        }

        public static void ResetAll()
        {
            ClearEvents();
            StaticLights = true;
            _boostActive = false;
        }

        /// <summary>true when `name` is one of the (rarely used) Chroma ring name filters.</summary>
        private static bool ContainsName(string[] names, string name)
        {
            if (names == null || name == null) return false;
            for (int i = 0; i < names.Length; i++)
                if (names[i] == name) return true;
            return false;
        }
    }

    public class LightingPropertyEventArgs
    {
        public LightEventType type;

        /// <summary>Event list of this type + the index of the current event: the Chroma
        /// handlers use them to find the event that actually addresses their lightID
        /// (ArcViewer LightingPropertyEventArgs).</summary>
        public List<LightEvent> eventList;
        public LightEvent lightEvent;
        public LightEvent nextEvent;
        public int eventIndex;

        public Color eventColor;
        public Color laserColor;
        public Color glowColor;
    }
}

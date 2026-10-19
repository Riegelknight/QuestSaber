// ==============================================================================
//  EnvironmentRingManager - ring spin/zoom (port from ArcViewer RingManager.cs).
// ==============================================================================
//  Taken 1:1 from ArcViewer (Assets/__Scripts/Previewer/MapControl/Lightshow/
//  RingManager.cs). Controls the ring rotation (event type 8) and the ring zoom
//  (type 9) of the default environment. Time source = EnvironmentClock, parameters =
//  EnvironmentLightManager.CurrentParameters.
//  Context: architecture see PROJECT_NOTES.md (project root).
// ==============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestSaber.Environment
{
    public static class EnvironmentRingManager
    {
        public static MapElementList<RingRotationEvent> AllRingRotationEvents = new MapElementList<RingRotationEvent>();
        public static MapElementList<RingRotationEvent> SmallRingRotationEvents = new MapElementList<RingRotationEvent>();
        public static MapElementList<RingRotationEvent> BigRingRotationEvents = new MapElementList<RingRotationEvent>();
        public static MapElementList<RingZoomEvent> RingZoomEvents = new MapElementList<RingZoomEvent>();

        private static readonly List<RingRotationEvent> emptyRingRotationEvents = new List<RingRotationEvent>();
        private static readonly RingRotationEventArgs ringRotationEventArgs = new RingRotationEventArgs();
        private static readonly RingRotationEventArgs staticRingRotationEventArgs = new RingRotationEventArgs
        {
            events = emptyRingRotationEvents,
            currentEventIndex = -1,
            affectSmallRings = true,
            affectBigRings = true
        };
        private static readonly MapElementList<RingRotationEvent>.CheckInRangeDelegate ringRotationEventInRange = RingRotationEventInRange;
        private static readonly MapElementList<RingZoomEvent>.CheckInRangeDelegate ringZoomEventInRange = RingZoomEventInRange;

        public static event Action<RingRotationEventArgs> OnRingRotationsChanged;
        public static event Action<float> OnRingZoomPositionChanged;

        private static EnvironmentLightParameters P => EnvironmentLightManager.CurrentParameters;

        public static float DefaultSmallRingRotationSpeed => P.SmallRingRotationSpeed;
        public static float DefaultSmallRingRotationAmount => P.SmallRingRotationAmount;
        public static float DefaultSmallRingMaxStep => P.SmallRingMaxStep;
        public static float DefaultSmallRingProp => P.SmallRingRotationProp;
        public static float SmallRingStartAngle => P.SmallRingStartAngle;
        public static float SmallRingStartStep => P.SmallRingStartStep;

        public static float DefaultBigRingRotationSpeed => P.BigRingRotationSpeed;
        public static float DefaultBigRingRotationAmount => P.BigRingRotationAmount;
        public static float DefaultBigRingMaxStep => P.BigRingMaxStep;
        public static float DefaultBigRingProp => P.BigRingRotationProp;
        public static float BigRingStartAngle => P.BigRingStartAngle;
        public static float BigRingStartStep => P.BigRingStartStep;

        public static float DefaultZoomSpeed => P.ZoomSpeed;
        public static float DefaultCloseZoomStep => P.CloseZoomStep;
        public static float DefaultFarZoomStep => P.FarZoomStep;
        public static bool StartRingZoomParity => P.StartRingZoomParity;
        public static float StartRingZoomStep => StartRingZoomParity ? DefaultFarZoomStep : DefaultCloseZoomStep;

        public static void UpdateRings()
        {
            UpdateRingZoom();
            UpdateRingRotations();
        }

        private static void UpdateRingRotations()
        {
            int lastIndex = AllRingRotationEvents.GetLastIndex(EnvironmentClock.CurrentTime, ringRotationEventInRange);

            RingRotationEventArgs eventArgs = ringRotationEventArgs;
            eventArgs.events = AllRingRotationEvents;
            eventArgs.currentEventIndex = lastIndex;
            eventArgs.affectSmallRings = false;
            eventArgs.affectBigRings = false;
            OnRingRotationsChanged?.Invoke(eventArgs);

            eventArgs.events = SmallRingRotationEvents;
            eventArgs.currentEventIndex = SmallRingRotationEvents.GetLastIndex(EnvironmentClock.CurrentTime, ringRotationEventInRange);
            eventArgs.affectSmallRings = true;
            OnRingRotationsChanged?.Invoke(eventArgs);

            eventArgs.events = BigRingRotationEvents;
            eventArgs.currentEventIndex = BigRingRotationEvents.GetLastIndex(EnvironmentClock.CurrentTime, ringRotationEventInRange);
            eventArgs.affectSmallRings = false;
            eventArgs.affectBigRings = true;
            OnRingRotationsChanged?.Invoke(eventArgs);
        }

        private static void UpdateRingZoom()
        {
            if (RingZoomEvents.Count == 0)
            {
                OnRingZoomPositionChanged?.Invoke(StartRingZoomStep);
                return;
            }

            int lastIndex = RingZoomEvents.GetLastIndex(EnvironmentClock.CurrentTime, ringZoomEventInRange);
            if (lastIndex < 0)
            {
                OnRingZoomPositionChanged?.Invoke(StartRingZoomStep);
                return;
            }

            RingZoomEvent current = RingZoomEvents[lastIndex];
            OnRingZoomPositionChanged?.Invoke(current.GetRingZoomStep(EnvironmentClock.CurrentTime));
        }

        public static void SetStaticRings()
        {
            OnRingRotationsChanged?.Invoke(staticRingRotationEventArgs);
            OnRingZoomPositionChanged?.Invoke(StartRingZoomStep);
        }

        private static bool RingRotationEventInRange(RingRotationEvent e) => e.Time <= EnvironmentClock.CurrentTime;
        private static bool RingZoomEventInRange(RingZoomEvent e) => e.Time <= EnvironmentClock.CurrentTime;

        public static void ClearEvents()
        {
            AllRingRotationEvents.Clear();
            SmallRingRotationEvents.Clear();
            BigRingRotationEvents.Clear();
            RingZoomEvents.Clear();
        }

        public static void PopulateRingEventData()
        {
            SetRingRotationParity();
            PopulateRingRotationEvents(SmallRingRotationEvents, DefaultSmallRingRotationAmount, DefaultSmallRingMaxStep, SmallRingStartAngle, SmallRingStartStep);
            PopulateRingRotationEvents(BigRingRotationEvents, DefaultBigRingRotationAmount, DefaultBigRingMaxStep, BigRingStartAngle, BigRingStartStep);
            PopulateRingZoomEvents();
        }

        private static void SetRingRotationParity()
        {
            for (int i = 0; i < AllRingRotationEvents.Count; i++)
            {
                RingRotationEvent current = AllRingRotationEvents[i];
                if (i == 0) current.Parity = true;
                else current.Parity = !AllRingRotationEvents[i - 1].Parity;
            }
        }

        private static void PopulateRingRotationEvents(MapElementList<RingRotationEvent> events,
            float rotationAmount, float maxStep, float startRotation, float startStep)
        {
            for (int i = 0; i < events.Count; i++)
            {
                RingRotationEvent current = events[i];
                if (i == 0)
                {
                    current.StartAngle = startRotation;
                    current.StartStep = startStep;
                    current.InitializeValues(startRotation, rotationAmount, maxStep);
                }
                else
                {
                    RingRotationEvent previous = events[i - 1];
                    float timeDifference = current.Time - previous.Time;
                    float rotationProgress = GetRingEventProgress(timeDifference, previous.Speed);
                    current.StartAngle = previous.GetEventAngle(rotationProgress);
                    current.StartStep = previous.GetEventStepAngle(rotationProgress);
                    current.InitializeValues(previous.TargetAngle, rotationAmount, maxStep);
                }
            }
        }

        private static void PopulateRingZoomEvents()
        {
            for (int i = 0; i < RingZoomEvents.Count; i++)
            {
                RingZoomEvent current = RingZoomEvents[i];
                if (i == 0)
                {
                    current.IsFarParity = !StartRingZoomParity;
                    current.StartStep = StartRingZoomStep;
                }
                else
                {
                    RingZoomEvent previous = RingZoomEvents[i - 1];
                    current.IsFarParity = !previous.IsFarParity;
                    current.StartStep = previous.GetRingZoomStep(current.Time);
                }
                if (!current.CustomStep)
                    current.Step = current.IsFarParity ? DefaultFarZoomStep : DefaultCloseZoomStep;
            }
        }

        public static float GetRingEventProgress(float timeDifference, float speed)
        {
            return 1f - Mathf.Pow(2f, -(timeDifference * speed * 2f));
        }
    }

    public class RingRotationEvent : MapElement
    {
        public const float FixedDeltaTime = 1f / 60f;

        public float Rotation;
        public float Speed;
        public float Prop = 1f;
        public float Step;

        public float TargetAngle;
        public float StartAngle;
        public float StartStep;

        public bool Parity;

        public bool RotateClockwise;

        // Chroma parameters (customData): when set they win over the environment defaults
        // (ArcViewer RingRotationEvent.Custom* fields).
        public bool CustomDirection;
        public bool CustomRotation;
        public bool CustomSpeed;
        public bool CustomProp;
        public bool CustomStep;

        public RingRotationEvent() { }

        public RingRotationEvent(LightEvent src)
        {
            Beat = src.Beat;

            if (src.Extras != null)
            {
                if (src.Extras._direction != 0)
                {
                    RotateClockwise = src.Extras._direction == 1;
                    CustomDirection = true;
                }
                if (!float.IsNaN(src.Extras._rotation)) { Rotation = src.Extras._rotation; CustomRotation = true; }
                if (!float.IsNaN(src.Extras._speed)) { Speed = src.Extras._speed; CustomSpeed = true; }
                if (!float.IsNaN(src.Extras._prop)) { Prop = src.Extras._prop; CustomProp = true; }
                if (!float.IsNaN(src.Extras._step)) { Step = src.Extras._step; CustomStep = true; }
            }

            if (!CustomDirection) RotateClockwise = UnityEngine.Random.value >= 0.5f;
        }

        public RingRotationEvent(RingRotationEvent original)
        {
            Time = original.Time;
            Rotation = original.Rotation;
            Speed = original.Speed;
            Prop = original.Prop;
            Step = original.Step;
            TargetAngle = original.TargetAngle;
            StartAngle = original.StartAngle;
            StartStep = original.StartStep;
            Parity = original.Parity;
            RotateClockwise = original.RotateClockwise;

            CustomDirection = original.CustomDirection;
            CustomRotation = original.CustomRotation;
            CustomSpeed = original.CustomSpeed;
            CustomProp = original.CustomProp;
            CustomStep = original.CustomStep;
        }

        public void SetDefaultSpeedAndProp(bool bigRing)
        {
            if (!CustomSpeed)
                Speed = bigRing ? EnvironmentRingManager.DefaultBigRingRotationSpeed : EnvironmentRingManager.DefaultSmallRingRotationSpeed;
            if (!CustomProp)
                Prop = bigRing ? EnvironmentRingManager.DefaultBigRingProp : EnvironmentRingManager.DefaultSmallRingProp;
        }

        public void InitializeValues(float startRotation, float defaultRotationAmount, float defaultMaxStep)
        {
            if (!CustomRotation) Rotation = defaultRotationAmount;
            if (!CustomStep) Step = UnityEngine.Random.Range(-defaultMaxStep, defaultMaxStep);
            float angleChange = RotateClockwise ? -Rotation : Rotation;
            TargetAngle = startRotation + angleChange;
        }

        public float StartInfluenceTime(int ringIndex)
        {
            return Time + (FixedDeltaTime * ringIndex / (Prop == 0f ? 0.0001f : Prop));
        }

        public float GetRingAngle(float currentTime, int ringIndex)
        {
            float ringInfluenceTime = StartInfluenceTime(ringIndex);
            float timeDifference = Mathf.Max(currentTime - ringInfluenceTime, 0f);
            float rotationProgress = EnvironmentRingManager.GetRingEventProgress(timeDifference, Speed);
            float angle = GetEventAngle(rotationProgress);
            float step = GetEventStepAngle(rotationProgress);
            return angle + (step * ringIndex);
        }

        public float GetEventAngle(float rotationProgress) => Mathf.Lerp(StartAngle, TargetAngle, rotationProgress);
        public float GetEventStepAngle(float rotationProgress) => Mathf.Lerp(StartStep, Step, rotationProgress);
    }

    public class RingZoomEvent : LightEvent
    {
        public float Speed;
        public float Step;
        public float StartStep;
        public bool IsFarParity;
        public bool CustomStep = false;

        public RingZoomEvent() { }

        public RingZoomEvent(LightEvent src)
        {
            Beat = src.Beat;
            Type = src.Type;
            Value = src.Value;
            FloatValue = src.FloatValue;
            Extras = src.Extras;

            // Chroma: `speed` (zoom speed) and `step` (distance per ring) override the
            // environment defaults; a custom step is kept when the parity pass runs.
            Speed = src.Extras != null && !float.IsNaN(src.Extras._speed)
                ? src.Extras._speed
                : EnvironmentRingManager.DefaultZoomSpeed;

            if (src.Extras != null && !float.IsNaN(src.Extras._step))
            {
                Step = src.Extras._step;
                CustomStep = true;
            }
        }

        public float GetRingZoomStep(float currentTime)
        {
            float timeDifference = currentTime - Time;
            float zoomProgress = EnvironmentRingManager.GetRingEventProgress(timeDifference, Speed);
            return Mathf.Lerp(StartStep, Step, zoomProgress);
        }
    }

    public class RingRotationEventArgs
    {
        public List<RingRotationEvent> events;
        public int currentEventIndex;
        public bool affectSmallRings;
        public bool affectBigRings;
    }
}

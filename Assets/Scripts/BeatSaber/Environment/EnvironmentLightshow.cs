// ==============================================================================
//  EnvironmentLightshow - lightshow data model (port from ArcViewer-main).
// ==============================================================================
//  Taken 1:1 from ArcViewer (Assets/__Scripts/Previewer/MapControl/Lightshow
//  + Maps/MapElement.cs) so that the song lightshows (v2/v3/v4) are evaluated
//  exactly like in the reference viewer: event colors (On/Flash/Fade/Transition),
//  ring spin/zoom, laser rotation speed and color boost.
//
//  Adjustments compared to ArcViewer:
//   - Time source is EnvironmentClock (fed from the score clock by the GameManager)
//     instead of TimeManager.
//   - Colors come from EnvironmentColorManager: the red/blue default plus the
//     per-environment and map-specific color sets.
//   - The complete lightshow hangs off the EnvironmentLightManager class.
//  Context: architecture see PROJECT_NOTES.md (project root).
// ==============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace QuestSaber.Environment
{
    /// <summary>
    /// Time source of the lightshow: seconds (score clock) AND beats of the running song.
    /// The GameManager sets <see cref="ToSeconds"/> per song (BPM curve) and writes
    /// <see cref="CurrentTime"/>/<see cref="CurrentBeat"/> every frame.
    /// </summary>
    public static class EnvironmentClock
    {
        /// <summary>Song time in seconds (score clock of the GameManager).</summary>
        public static float CurrentTime;

        /// <summary>Current beat (derived from <see cref="ToSeconds"/>).</summary>
        public static float CurrentBeat;

        /// <summary>Beat->seconds of the current map (BPM curve), set by the GameManager.</summary>
        public static Func<float, float> ToSeconds = b => b;

        /// <summary>Sets the conversion for the current song (ToSeconds + inverse).</summary>
        public static void SetConverter(Func<float, float> toSeconds)
        {
            ToSeconds = toSeconds ?? (b => b);
        }
    }

    /// <summary>Base of all time-based lightshow elements (beat + seconds).</summary>
    public class MapElement
    {
        private float _beat;
        public float Beat
        {
            get => _beat;
            set { _beat = value; _time = EnvironmentClock.ToSeconds(value); }
        }

        private float _time;
        public float Time
        {
            get => _time;
            set { _time = value; _beat = EnvironmentClock.CurrentBeat; }
        }
    }

    /// <summary>
    /// Sorted event list with incremental search (port from ArcViewer MapElementList):
    /// GetLastIndex only moves forward and finds the last element "in range".
    /// </summary>
    public class MapElementList<T> : IEnumerable<T> where T : MapElement
    {
        public bool IsSorted { get; protected set; }
        protected List<T> Elements;
        protected int lastStartIndex;
        protected float lastStartTime;

        public MapElementList()
        {
            Elements = new List<T>();
            IsSorted = true;
            lastStartIndex = 0;
            lastStartTime = 0f;
        }

        public int Count => Elements.Count;
        public T Last => Elements.Count > 0 ? Elements[Elements.Count - 1] : null;
        public delegate bool CheckInRangeDelegate(T element);

        public static implicit operator List<T>(MapElementList<T> elementList) => elementList.Elements;

        public void Add(T item)
        {
            IsSorted = Count == 0 || (IsSorted && item.Time >= Last.Time);
            Elements.Add(item);
        }

        public void Clear()
        {
            Elements.Clear();
            lastStartIndex = 0;
            IsSorted = true;
        }

        public T this[int i] { get => Elements[i]; }

        IEnumerator IEnumerable.GetEnumerator() => Elements.GetEnumerator();
        public IEnumerator<T> GetEnumerator() => Elements.GetEnumerator();

        public void SortElementsByBeat()
        {
            if (!IsSorted) { Elements.Sort((x, y) => x.Time.CompareTo(y.Time)); IsSorted = true; }
        }

        /// <summary>Index of the last element that satisfies the range check (-1 = none).</summary>
        public int GetLastIndex(float currentTime, CheckInRangeDelegate checkMethod)
        {
            if (!IsSorted) SortElementsByBeat();
            if (Elements.Count == 0) return -1;

            if (currentTime < lastStartTime) lastStartIndex = 0;
            lastStartTime = currentTime;

            for (int i = lastStartIndex; i < Elements.Count; i++)
            {
                if (!checkMethod(Elements[i])) return i - 1;
                lastStartIndex = i;
            }
            return lastStartIndex;
        }
    }

    /// <summary>Lightshow event types (values MUST match the map prefabs).</summary>
    public enum LightEventType
    {
        BackLasers = 0,
        Rings = 1,
        LeftRotatingLasers = 2,
        RightRotatingLasers = 3,
        CenterLights = 4,
        LeftSideExtra = 6,
        RightSideExtra = 7,
        RingSpin = 8,
        RingZoom = 9,
        BillieLeftLasers = 10,
        BillieRightLasers = 11,
        LeftRotationSpeed = 12,
        RightRotationSpeed = 13,
        InterscopeHydraulicsDown = 16,
        InterscopeHydraulicsUp = 17,
        GagaLeftTowerHeight = 18,
        GagaRightTowerHeight = 19
    }

    /// <summary>Event values (colors) - order = Beat Saber map convention.</summary>
    public enum LightEventValue
    {
        Off,
        BlueOn,
        BlueFlash,
        BlueFade,
        BlueTransition,
        RedOn,
        RedFlash,
        RedFade,
        RedTransition,
        WhiteOn,
        WhiteFlash,
        WhiteFade,
        WhiteTransition
    }

    /// <summary>A base light event (from the map).</summary>
    public class LightEvent : MapElement
    {
        public LightEventType Type;
        public LightEventValue Value;
        public float FloatValue = 1f;

        /// <summary>Chroma: per-event color (`customData.color`); overrides red/blue/white.</summary>
        public bool HasCustomColor = false;
        public Color CustomColor = Color.white;

        /// <summary>Chroma lightID: the emitters this event addresses. Null/empty = all
        /// emitters of the type (vanilla). See <see cref="AffectsID"/>.</summary>
        public HashSet<int> LightIDs;

        /// <summary>Chroma transition curve (`customData.easing`) - linear by default.</summary>
        public Easings.EasingType TransitionEasing = Easings.EasingType.Linear;

        /// <summary>Chroma `lerpType: "HSV"` - transition through hue/sat/value.</summary>
        public bool HsvLerp = false;

        /// <summary>Extras this event was built from (ring/laser parameters). Null = vanilla.</summary>
        public LightEventExtras Extras;

        public LightEvent() { }

        public LightEvent(float beat, LightEventType type, LightEventValue value, float floatValue)
        {
            Beat = beat;
            Type = type;
            Value = value;
            FloatValue = floatValue;
        }

        public bool IsTransition => Value == LightEventValue.RedTransition
                                 || Value == LightEventValue.BlueTransition
                                 || Value == LightEventValue.WhiteTransition;

        /// <summary>
        /// Does this event address the emitter with this id? Without lightID every event
        /// affects every emitter - exactly ArcViewer `LightEvent.AffectsID`.
        /// </summary>
        public bool AffectsID(int id)
        {
            if ((LightIDs?.Count ?? 0) == 0) return true;
            return LightIDs.Contains(id);
        }

        /// <summary>
        /// Swaps red/blue of the event (white/off stay). Some environments (Dragons,
        /// FitBeat) have their back/bottom lasers wired the other way round - ArcViewer
        /// `LightEvent.MirrorColor`.
        /// </summary>
        public void MirrorColor()
        {
            switch (Value)
            {
                case LightEventValue.BlueOn: Value = LightEventValue.RedOn; break;
                case LightEventValue.BlueFlash: Value = LightEventValue.RedFlash; break;
                case LightEventValue.BlueFade: Value = LightEventValue.RedFade; break;
                case LightEventValue.BlueTransition: Value = LightEventValue.RedTransition; break;
                case LightEventValue.RedOn: Value = LightEventValue.BlueOn; break;
                case LightEventValue.RedFlash: Value = LightEventValue.BlueFlash; break;
                case LightEventValue.RedFade: Value = LightEventValue.BlueFade; break;
                case LightEventValue.RedTransition: Value = LightEventValue.BlueTransition; break;
            }
        }

        /// <summary>Takes over the Chroma/Heck extras (lightID, easing, HSV lerp).</summary>
        public void ApplyExtras()
        {
            if (Extras == null) return;
            if (Extras._lightID != null && Extras._lightID.Length > 0)
                LightIDs = new HashSet<int>(Extras._lightID);
            TransitionEasing = Easings.EasingTypeFromString(Extras._easing);
            HsvLerp = Extras._hsvLerp;
        }
    }

    /// <summary>Laser rotation speed event (type 12/13).</summary>
    public class LaserSpeedEvent : LightEvent
    {
        public const float RotationSpeedMult = 20f;

        public float RotationSpeed;
        public bool LockRotation = false;
        public int Direction = -1;
        public List<LaserRotationData> RotationValues = new List<LaserRotationData>();

        public struct LaserRotationData
        {
            public float startPosition;
            public bool direction;
        }

        public LaserSpeedEvent() { }

        public LaserSpeedEvent(LightEvent src)
        {
            Beat = src.Beat;
            Type = src.Type;
            Value = src.Value;
            FloatValue = src.FloatValue;
            Extras = src.Extras;

            // Chroma: `speed` (in rotations/s) replaces the event value; direction and
            // lockRotation exist only as customData (ArcViewer LaserSpeedEvent).
            float speed = src.Extras != null && !float.IsNaN(src.Extras._speed)
                ? src.Extras._speed
                : (int)Value;
            RotationSpeed = speed * RotationSpeedMult;

            if (src.Extras != null)
            {
                LockRotation = src.Extras._lockRotation;
                Direction = src.Extras._direction;
            }
        }

        public void PopulateRotationData(int laserCount, LaserSpeedEvent previous, EnvironmentLightParameters env)
        {
            RotationValues.Clear();
            bool randomize = env == null || env.RandomizeRotatingLasers;
            float startAngle = UnityEngine.Random.Range(0f, 360f);
            bool unifiedDirection = UnityEngine.Random.value >= 0.5f;

            for (int i = 0; i < laserCount; i++)
            {
                var newData = new LaserRotationData();
                if ((int)Value > 0)
                {
                    if (Direction < 0)
                        newData.direction = randomize ? UnityEngine.Random.value >= 0.5f : unifiedDirection;
                    else newData.direction = Direction == 1;

                    if (randomize) newData.startPosition = UnityEngine.Random.Range(0f, 360f);
                    else
                    {
                        float step = (env != null ? env.RotatingLaserStep : 0f) * (newData.direction ? 1 : -1);
                        newData.startPosition = (startAngle + (step * i)) % 360f;
                    }
                }
                else newData.startPosition = 0f;

                if (newData.startPosition < 0) newData.startPosition += 360f;
                if (LockRotation) newData.startPosition = previous?.GetLaserRotation(Time, i) ?? 0f;
                RotationValues.Add(newData);
            }
        }

        public float GetLaserRotation(float currentTime, int laserID)
        {
            if (laserID >= RotationValues.Count) return 0f;
            float timeDifference = currentTime - Time;
            LaserRotationData data = RotationValues[laserID];
            float rotationAmount = RotationSpeed * timeDifference;
            if (data.direction) rotationAmount = -rotationAmount;
            return (data.startPosition + rotationAmount) % 360;
        }

        public float GetLaserRotationWithStep(float currentTime, bool direction, float step, int laserID)
        {
            if (RotationValues.Count == 0) return 0f;
            float timeDifference = currentTime - Time;
            LaserRotationData data = RotationValues[0];
            float rotationAmount = RotationSpeed * timeDifference;
            if (direction) rotationAmount = -rotationAmount;
            float startPosition = data.startPosition + (step * laserID);
            return (startPosition + rotationAmount) % 360;
        }
    }

    /// <summary>Color boost event.</summary>
    public class BoostEvent : MapElement
    {
        public bool Value;
        public BoostEvent() { }
        public BoostEvent(float beat, bool value) { Beat = beat; Value = value; }
    }

    /// <summary>Color set of the lightshow (default = ArcViewer `ColorManager.DefaultColors`).</summary>
    public class EnvironmentColorPalette
    {
        public Color LightColor1 = new Color(0.85f, 0.085f, 0.085f, 1f);
        public Color LightColor2 = new Color(0.1882353f, 0.675294f, 1f, 1f);
        public Color WhiteLightColor = new Color(1f, 1f, 1f, 1f);
        public Color BoostLightColor1 = new Color(0.85f, 0.085f, 0.085f, 1f);
        public Color BoostLightColor2 = new Color(0.1882353f, 0.675294f, 1f, 1f);
        public Color BoostWhiteLightColor = new Color(1f, 1f, 1f, 1f);
    }

    /// <summary>Holds the current color set (red/blue default or per-environment/map colors).</summary>
    public static class EnvironmentColorManager
    {
        public static EnvironmentColorPalette CurrentColors = new EnvironmentColorPalette();

        public static void SetPalette(EnvironmentColorPalette palette)
        {
            CurrentColors = palette ?? new EnvironmentColorPalette();
        }
    }

    /// <summary>
    /// Easings - 1:1 port of ArcViewer `Utilities/Easings.cs` (EasingType + EasingFromType +
    /// EasingTypeFromString). Needed for Chroma transitions (`customData.easing`), which
    /// otherwise always ran linear.
    /// </summary>
    public static class Easings
    {
        public static float Linear(float x) => x;

        public static float Step(float x) => Mathf.Floor(x);

        public static class Sine
        {
            public static float In(float x) => 1f - Mathf.Cos((x * Mathf.PI) / 2f);
            public static float Out(float x) => Mathf.Sin((x * Mathf.PI) / 2f);
            public static float InOut(float x) => -(Mathf.Cos(Mathf.PI * x) - 1f) / 2f;
        }

        public static class Quad
        {
            public static float In(float x) => x * x;
            public static float Out(float x) => 1f - (1f - x) * (1f - x);
            public static float InOut(float x) => x < 0.5f ? 2f * x * x : 1f - Mathf.Pow(-2f * x + 2f, 2f) / 2f;
        }

        public static class Cubic
        {
            public static float In(float x) => x * x * x;
            public static float Out(float x) => 1f - Mathf.Pow(1f - x, 3f);
            public static float InOut(float x) => x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) / 2f;
        }

        public static class Quart
        {
            public static float In(float x) => x * x * x * x;
            public static float Out(float x) => 1f - Mathf.Pow(1f - x, 4f);
            public static float InOut(float x) => x < 0.5f ? 8f * x * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 4f) / 2f;
        }

        public static class Quint
        {
            public static float In(float x) => x * x * x * x * x;
            public static float Out(float x) => 1f - Mathf.Pow(1f - x, 5f);
            public static float InOut(float x) => x < 0.5f ? 16f * x * x * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 5f) / 2f;
        }

        public static class Expo
        {
            public static float In(float x) => x == 0f ? 0f : Mathf.Pow(2f, 10f * x - 10f);
            public static float Out(float x) => x >= 1f ? 1f : 1f - Mathf.Pow(2f, -10f * x);
            public static float InOut(float x) => x == 0f ? 0f
                : x == 1f ? 1f
                : x < 0.5f ? Mathf.Pow(2f, 20f * x - 10f) / 2f
                : (2f - Mathf.Pow(2f, -20f * x + 10f)) / 2f;
        }

        public static class Circ
        {
            public static float In(float x) => 1f - Mathf.Sqrt(1f - Mathf.Pow(x, 2f));
            public static float Out(float x) => Mathf.Sqrt(1f - Mathf.Pow(x - 1f, 2f));
            public static float InOut(float x) => x < 0.5f
                ? (1f - Mathf.Sqrt(1f - Mathf.Pow(2f * x, 2f))) / 2f
                : (Mathf.Sqrt(1f - Mathf.Pow(-2f * x + 2f, 2f)) + 1f) / 2f;
        }

        public static class Back
        {
            private const float C1 = 1.70158f;
            private const float C2 = C1 * 1.525f;
            private const float C3 = C1 + 1f;

            public static float In(float x) => C3 * x * x * x - C1 * x * x;
            public static float Out(float x) => 1f + C3 * Mathf.Pow(x - 1f, 3f) + C1 * Mathf.Pow(x - 1f, 2f);
            public static float InOut(float x) => x < 0.5f
                ? (Mathf.Pow(2f * x, 2f) * ((C2 + 1f) * 2f * x - C2)) / 2f
                : (Mathf.Pow(2f * x - 2f, 2f) * ((C2 + 1f) * (x * 2f - 2f) + C2) + 2f) / 2f;
        }

        public static class Elastic
        {
            private const float C4 = (2f * Mathf.PI) / 3f;
            private const float C5 = (2f * Mathf.PI) / 4.5f;

            public static float In(float x) => x == 0f ? 0f
                : x == 1f ? 1f
                : -Mathf.Pow(2f, 10f * x - 10f) * Mathf.Sin((x * 10f - 10.75f) * C4);

            public static float Out(float x) => x == 0f ? 0f
                : x == 1f ? 1f
                : Mathf.Pow(2f, -10f * x) * Mathf.Sin((x * 10f - 0.75f) * C4) + 1f;

            public static float InOut(float x) => x == 0f ? 0f
                : x == 1f ? 1f
                : x < 0.5f ? -(Mathf.Pow(2f, 20f * x - 10f) * Mathf.Sin((20f * x - 11.125f) * C5)) / 2f
                : (Mathf.Pow(2f, -20f * x + 10f) * Mathf.Sin((20f * x - 11.125f) * C5)) / 2f + 1f;
        }

        public static class Bounce
        {
            private const float N1 = 7.5625f;
            private const float D1 = 2.75f;

            public static float In(float x) => 1f - Out(1f - x);

            public static float Out(float x)
            {
                if (x < 1f / D1) return N1 * x * x;
                if (x < 2f / D1) return N1 * (x -= 1.5f / D1) * x + 0.75f;
                if (x < 2.5f / D1) return N1 * (x -= 2.25f / D1) * x + 0.9375f;
                return N1 * (x -= 2.625f / D1) * x + 0.984375f;
            }

            public static float InOut(float x) => x < 0.5f
                ? (1f - Out(1f - 2f * x)) / 2f
                : (1f + Out(2f * x - 1f)) / 2f;
        }

        /// <summary>Bit flags exactly like ArcViewer (family in the low, direction in the high bits).</summary>
        [Flags]
        public enum EasingType
        {
            Linear = 0b_0000_0000,
            Sine = 0b_0000_0001,
            Quad = 0b_0000_0010,
            Cubic = 0b_0000_0011,
            Quart = 0b_0000_0100,
            Quint = 0b_0000_0101,
            Expo = 0b_0000_0110,
            Circ = 0b_0000_0111,
            Back = 0b_0000_1001,
            Elastic = 0b_0000_1010,
            Bounce = 0b_0000_1011,
            Step = 0b_0000_1100,

            In = 0b_1000_0000,
            Out = 0b_0100_0000,
            InOut = 0b_1100_0000,
        }

        public static float EasingFromType(EasingType easingType, float x)
        {
            switch (easingType)
            {
                case EasingType.Sine | EasingType.In: return Sine.In(x);
                case EasingType.Sine | EasingType.Out: return Sine.Out(x);
                case EasingType.Sine | EasingType.InOut: return Sine.InOut(x);
                case EasingType.Quad | EasingType.In: return Quad.In(x);
                case EasingType.Quad | EasingType.Out: return Quad.Out(x);
                case EasingType.Quad | EasingType.InOut: return Quad.InOut(x);
                case EasingType.Cubic | EasingType.In: return Cubic.In(x);
                case EasingType.Cubic | EasingType.Out: return Cubic.Out(x);
                case EasingType.Cubic | EasingType.InOut: return Cubic.InOut(x);
                case EasingType.Quart | EasingType.In: return Quart.In(x);
                case EasingType.Quart | EasingType.Out: return Quart.Out(x);
                case EasingType.Quart | EasingType.InOut: return Quart.InOut(x);
                case EasingType.Quint | EasingType.In: return Quint.In(x);
                case EasingType.Quint | EasingType.Out: return Quint.Out(x);
                case EasingType.Quint | EasingType.InOut: return Quint.InOut(x);
                case EasingType.Expo | EasingType.In: return Expo.In(x);
                case EasingType.Expo | EasingType.Out: return Expo.Out(x);
                case EasingType.Expo | EasingType.InOut: return Expo.InOut(x);
                case EasingType.Circ | EasingType.In: return Circ.In(x);
                case EasingType.Circ | EasingType.Out: return Circ.Out(x);
                case EasingType.Circ | EasingType.InOut: return Circ.InOut(x);
                case EasingType.Back | EasingType.In: return Back.In(x);
                case EasingType.Back | EasingType.Out: return Back.Out(x);
                case EasingType.Back | EasingType.InOut: return Back.InOut(x);
                case EasingType.Elastic | EasingType.In: return Elastic.In(x);
                case EasingType.Elastic | EasingType.Out: return Elastic.Out(x);
                case EasingType.Elastic | EasingType.InOut: return Elastic.InOut(x);
                case EasingType.Bounce | EasingType.In: return Bounce.In(x);
                case EasingType.Bounce | EasingType.Out: return Bounce.Out(x);
                case EasingType.Bounce | EasingType.InOut: return Bounce.InOut(x);
                case EasingType.Step: return Step(x);
                case EasingType.Linear:
                default: return Linear(x);
            }
        }

        /// <summary>Easing name of a map (`customData.easing`) -> type. Unknown = linear.</summary>
        public static EasingType EasingTypeFromString(string easingType)
        {
            if (string.IsNullOrEmpty(easingType)) return EasingType.Linear;
            switch (easingType)
            {
                case "easeInSine": return EasingType.Sine | EasingType.In;
                case "easeOutSine": return EasingType.Sine | EasingType.Out;
                case "easeInOutSine": return EasingType.Sine | EasingType.InOut;
                case "easeInQuad": return EasingType.Quad | EasingType.In;
                case "easeOutQuad": return EasingType.Quad | EasingType.Out;
                case "easeInOutQuad": return EasingType.Quad | EasingType.InOut;
                case "easeInCubic": return EasingType.Cubic | EasingType.In;
                case "easeOutCubic": return EasingType.Cubic | EasingType.Out;
                case "easeInOutCubic": return EasingType.Cubic | EasingType.InOut;
                case "easeInQuart": return EasingType.Quart | EasingType.In;
                case "easeOutQuart": return EasingType.Quart | EasingType.Out;
                case "easeInOutQuart": return EasingType.Quart | EasingType.InOut;
                case "easeInQuint": return EasingType.Quint | EasingType.In;
                case "easeOutQuint": return EasingType.Quint | EasingType.Out;
                case "easeInOutQuint": return EasingType.Quint | EasingType.InOut;
                case "easeInExpo": return EasingType.Expo | EasingType.In;
                case "easeOutExpo": return EasingType.Expo | EasingType.Out;
                case "easeInOutExpo": return EasingType.Expo | EasingType.InOut;
                case "easeInCirc": return EasingType.Circ | EasingType.In;
                case "easeOutCirc": return EasingType.Circ | EasingType.Out;
                case "easeInOutCirc": return EasingType.Circ | EasingType.InOut;
                case "easeInBack": return EasingType.Back | EasingType.In;
                case "easeOutBack": return EasingType.Back | EasingType.Out;
                case "easeInOutBack": return EasingType.Back | EasingType.InOut;
                case "easeInElastic": return EasingType.Elastic | EasingType.In;
                case "easeOutElastic": return EasingType.Elastic | EasingType.Out;
                case "easeInOutElastic": return EasingType.Elastic | EasingType.InOut;
                case "easeInBounce": return EasingType.Bounce | EasingType.In;
                case "easeOutBounce": return EasingType.Bounce | EasingType.Out;
                case "easeInOutBounce": return EasingType.Bounce | EasingType.InOut;
                case "easeStep": return EasingType.Step;
                case "easeLinear":
                default: return EasingType.Linear;
            }
        }

        /// <summary>
        /// Chroma `lerpType: "HSV"`: interpolate through hue/saturation/value instead of
        /// RGB (ArcViewer `ColorExtensions.LerpHSV`).
        /// </summary>
        public static Color LerpHsv(Color a, Color b, float t)
        {
            Color.RGBToHSV(a, out float ha, out float sa, out float va);
            Color.RGBToHSV(b, out float hb, out float sb, out float vb);

            // Shortest way around the colour wheel (ArcViewer does exactly this).
            float dh = hb - ha;
            if (dh > 0.5f) dh -= 1f;
            else if (dh < -0.5f) dh += 1f;

            float h = (ha + dh * t) % 1f;
            if (h < 0f) h += 1f;

            Color rgb = Color.HSVToRGB(h, Mathf.LerpUnclamped(sa, sb, t), Mathf.LerpUnclamped(va, vb, t));
            rgb.a = Mathf.LerpUnclamped(a.a, b.a, t);
            return rgb;
        }
    }

    /// <summary>Environment parameters (port from ArcViewer's EnvironmentLightParameters).</summary>
    [Serializable]
    public class EnvironmentLightParameters
    {
        public float RotatingLaserCount = 4;
        public bool RandomizeRotatingLasers = true;
        public float RotatingLaserStep = 0f;

        public float SmallRingRotationSpeed = 2f;
        public float SmallRingRotationAmount = 90f;
        public float SmallRingRotationProp = 1f;
        public float SmallRingMaxStep = 5f;
        public float SmallRingStartAngle = -45f;
        public float SmallRingStartStep = 3f;

        public float BigRingRotationSpeed = 2f;
        public float BigRingRotationAmount = 45f;
        public float BigRingRotationProp = 1f;
        public float BigRingMaxStep = 5f;
        public float BigRingStartAngle = -45f;
        public float BigRingStartStep = 0f;

        public float ZoomSpeed = 2f;
        public float CloseZoomStep = 2f;
        public float FarZoomStep = 5f;
        public bool StartRingZoomParity = true;

        /// <summary>Ring spin (type 8) with this `nameFilter` moves only the small rings.
        /// Values 1:1 from ArcViewer's EnvironmentLightParameters (BSMG standard).</summary>
        public string[] SmallRingNameFilters = { "SmallTrackLaneRings" };

        /// <summary>Ring spin (type 8) with this `nameFilter` moves only the big rings.</summary>
        public string[] BigRingNameFilters = { "BigTrackLaneRings" };
    }
}

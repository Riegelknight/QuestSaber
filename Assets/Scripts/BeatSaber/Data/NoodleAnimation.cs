using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace QuestSaber
{
    /// <summary>
    /// Easing functions for Noodle/Heck animations.
    /// Source: https://heck.aeroluna.dev/animation/tracks-and-points/
    /// Names follow easings.net; additionally "easeLinear" and "easeStep".
    ///
    /// The input t usually runs from 0..1 (segment progress). The formulas
    /// are deliberately the 1:1 standard Penner equations from easings.net, so that the
    /// motion feels exactly like in the original/ArcViewer.
    ///
    /// Note: old maps sometimes encode the easing as a number - that is NOT implemented.
    /// In a point like [data..., time, easingIndex] a numeric token is taken as Time
    /// (see NoodlePointDefinition.AddRaw); only string names are matched as easings.
    /// </summary>
    public static class NoodleEasing
    {
        private const float Pi = Mathf.PI;
        private static readonly HashSet<string> Warned = new();

        /// <summary>Spline identifier: "splineCatmullRom" (position/rotation only).</summary>
        public const string SplineCatmullRom = "splineCatmullRom";

        /// <summary>Evaluates an easing name. Unknown/null = Linear (t).</summary>
        public static float Evaluate(string name, float t)
        {
            if (string.IsNullOrEmpty(name)) return t;

            switch (name)
            {
                // ---- Base ----
                case "easeLinear": return t;
                case "easeStep": return t >= 1f ? 1f : 0f;

                // ---- Sine ----
                case "easeInSine": return 1f - Mathf.Cos(t * Pi / 2f);
                case "easeOutSine": return Mathf.Sin(t * Pi / 2f);
                case "easeInOutSine": return -(Mathf.Cos(Pi * t) - 1f) / 2f;

                // ---- Quad ----
                case "easeInQuad": return t * t;
                case "easeOutQuad": return 1f - (1f - t) * (1f - t);
                case "easeInOutQuad": return t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f;

                // ---- Cubic ----
                case "easeInCubic": return t * t * t;
                case "easeOutCubic": return 1f - Mathf.Pow(1f - t, 3f);
                case "easeInOutCubic": return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;

                // ---- Quart ----
                case "easeInQuart": return t * t * t * t;
                case "easeOutQuart": return 1f - Mathf.Pow(1f - t, 4f);
                case "easeInOutQuart": return t < 0.5f ? 8f * t * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 4f) / 2f;

                // ---- Quint ----
                case "easeInQuint": return t * t * t * t * t;
                case "easeOutQuint": return 1f - Mathf.Pow(1f - t, 5f);
                case "easeInOutQuint": return t < 0.5f ? 16f * t * t * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 5f) / 2f;

                // ---- Expo ----
                case "easeInExpo": return t <= 0f ? 0f : Mathf.Pow(2f, 10f * t - 10f);
                case "easeOutExpo": return t >= 1f ? 1f : 1f - Mathf.Pow(2f, -10f * t);
                case "easeInOutExpo":
                    if (t <= 0f) return 0f;
                    if (t >= 1f) return 1f;
                    return t < 0.5f ? Mathf.Pow(2f, 20f * t - 10f) / 2f
                                    : (2f - Mathf.Pow(2f, -20f * t + 10f)) / 2f;

                // ---- Circ ----
                case "easeInCirc": return 1f - Mathf.Sqrt(1f - Mathf.Min(1f, t * t));
                case "easeOutCirc": return Mathf.Sqrt(1f - Mathf.Pow(t - 1f, 2f));
                case "easeInOutCirc":
                    return t < 0.5f
                        ? (1f - Mathf.Sqrt(1f - Mathf.Pow(2f * t, 2f))) / 2f
                        : (Mathf.Sqrt(1f - Mathf.Pow(-2f * t + 2f, 2f)) + 1f) / 2f;

                // ---- Back ----
                case "easeInBack": { const float c1 = 1.70158f; const float c3 = c1 + 1f; return c3 * t * t * t - c1 * t * t; }
                case "easeOutBack": { const float c1 = 1.70158f; const float c3 = c1 + 1f; return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f); }
                case "easeInOutBack":
                {
                    const float c1 = 1.70158f; const float c2 = c1 * 1.525f;
                    return t < 0.5f
                        ? (Mathf.Pow(2f * t, 2f) * ((c2 + 1f) * 2f * t - c2)) / 2f
                        : (Mathf.Pow(2f * t - 2f, 2f) * ((c2 + 1f) * (t * 2f - 2f) + c2) + 2f) / 2f;
                }

                // ---- Elastic ----
                case "easeInElastic":
                {
                    const float c4 = (2f * Pi) / 3f;
                    if (t <= 0f) return 0f;
                    if (t >= 1f) return 1f;
                    return -Mathf.Pow(2f, 10f * t - 10f) * Mathf.Sin((t * 10f - 10.75f) * c4);
                }
                case "easeOutElastic":
                {
                    const float c4 = (2f * Pi) / 3f;
                    if (t <= 0f) return 0f;
                    if (t >= 1f) return 1f;
                    return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t * 10f - 0.75f) * c4) + 1f;
                }
                case "easeInOutElastic":
                {
                    const float c5 = (2f * Pi) / 4.5f;
                    if (t <= 0f) return 0f;
                    if (t >= 1f) return 1f;
                    return t < 0.5f
                        ? -(Mathf.Pow(2f, 20f * t - 10f) * Mathf.Sin((20f * t - 11.125f) * c5)) / 2f
                        : (Mathf.Pow(2f, -20f * t + 10f) * Mathf.Sin((20f * t - 11.125f) * c5)) / 2f + 1f;
                }

                // ---- Bounce ----
                case "easeInBounce": return 1f - BounceOut(1f - t);
                case "easeOutBounce": return BounceOut(t);
                case "easeInOutBounce":
                    return t < 0.5f
                        ? (1f - BounceOut(1f - 2f * t)) / 2f
                        : (1f + BounceOut(2f * t - 1f)) / 2f;

                default:
                    // One-time warning per unknown name (avoids log spam in maps
                    // with many points); listing the map helps when reporting missing easings.
                    if (Warned.Add(name))
                        Debug.LogWarning($"[Noodle] Unknown easing '{name}' - using Linear.");
                    return t;
            }
        }

        /// <summary>easings.net classic (bounce-out), base for In/InOut.</summary>
        private static float BounceOut(float t)
        {
            const float n1 = 7.5625f;
            const float d1 = 2.75f;
            if (t < 1f / d1) return n1 * t * t;
            if (t < 2f / d1) { t -= 1.5f / d1; return n1 * t * t + 0.75f; }
            if (t < 2.5f / d1) { t -= 2.25f / d1; return n1 * t * t + 0.9375f; }
            t -= 2.625f / d1; return n1 * t * t + 0.984375f;
        }

        /// <summary>True if the name marks a spline interpolation.</summary>
        public static bool IsSpline(string name) =>
            !string.IsNullOrEmpty(name) && name.Equals(SplineCatmullRom, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A single control point of a Noodle point definition: <c>[data..., time, easing?, spline?]</c>.
    /// <see cref="Data"/> contains only the payload (without time/easing), <see cref="Easing"/>
    /// applies to the segment FROM the previous point TO this point (Heck semantics).
    /// </summary>
    public sealed class NoodlePoint
    {
        public float[] Data;      // payload (dimension per property: 1/3/4)
        public float Time;        // 0..1 within the definition
        public string Easing;     // segment easing (applied to this point)
        public bool Spline;       // splineCatmullRom active for this segment
    }

    /// <summary>
    /// Noodle/Heck point definition: sequence of <see cref="NoodlePoint"/> plus evaluation.
    /// Supports Vec3 (position/scale), float (dissolve/interactable), color (RGBA) and
    /// quaternion rotation (Euler input). Time outside the definition bounds clamps
    /// to the first/last point (Heck behavior).
    /// </summary>
    public sealed class NoodlePointDefinition
    {
        public readonly List<NoodlePoint> Points = new();
        /// <summary>Expected payload dimension (3 = vector, 1 = scalar, 4 = color).</summary>
        public int Dimension { get; private set; }

        public bool IsEmpty => Points.Count == 0;

        public NoodlePointDefinition(int dimension) { Dimension = dimension; }

        /// <summary>
        /// Adds a control point from raw JSON values. The order is
        /// <c>[data..., time, easing?, spline?]</c>; after the payload the rest is
        /// scanned tolerantly (number = time, string = easing/spline, array = modifier
        /// is ignored). If time is entirely missing, 0 applies.
        /// </summary>
        public void AddRaw(IReadOnlyList<object> raw)
        {
            if (raw == null || raw.Count == 0) return;

            int dim = Dimension;
            if (raw.Count < dim) dim = raw.Count; // defensive: do not discard too-short points

            var p = new NoodlePoint { Data = new float[dim] };
            for (int i = 0; i < dim; i++) p.Data[i] = ToFloat(raw[i]);

            for (int i = dim; i < raw.Count; i++)
            {
                var token = raw[i];
                if (token is float f) p.Time = f;
                else if (token is int n) p.Time = n;
                else if (token is string s)
                {
                    if (NoodleEasing.IsSpline(s)) p.Spline = true;
                    else p.Easing = s;
                }
                // Arrays = modifier (opAdd/opMul ...) - not applied in this stage.
            }

            Points.Add(p);
        }

        /// <summary>Sorts the points by time (Heck requires ascending time).</summary>
        public void SortByTime() => Points.Sort((a, b) => a.Time.CompareTo(b.Time));

        private static float ToFloat(object o)
        {
            switch (o)
            {
                case float f: return f;
                case int n: return n;
                case double dd: return (float)dd;
                case bool b: return b ? 1f : 0f;
                case string s: return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0f;
                default: return 0f;
            }
        }
    }
}

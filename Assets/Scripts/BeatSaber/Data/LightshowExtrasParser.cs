// ==============================================================================
//  LightshowExtrasParser - Chroma/Heck extras of the lightshow events.
// ==============================================================================
//  Why this is a separate pass: the events themselves are read with Unity's
//  JsonUtility (BeatmapParser), and JsonUtility cannot express BOTH shapes of
//  `lightID` that exist in the wild (`"lightID": 7` and `"lightID": [1,2,3]`);
//  a mismatching declaration silently drops the field. So the extras are read
//  from the SAME json with Newtonsoft (already used for Noodle data), index
//  aligned with the parsed event list.
//
//  Reference: ArcViewer `BeatmapCustomBasicEventData` (Assets/__Scripts/
//  BeatmapData/...) - lightID (which light emitters an event addresses),
//  nameFilter (ring spins: only the small or the big rings), easing/lerpType
//  (transition curve), and the Chroma ring/laser parameters speed/rotation/
//  prop/step/direction/lockRotation.
//
//  Defensive by design: any error yields "no extras" (null entries), which is
//  exactly the vanilla behaviour - a broken customData must never cost the map
//  its lightshow. Context: architecture see PROJECT_NOTES.md (project root).
// ==============================================================================
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace QuestSaber
{
    public static class LightshowExtrasParser
    {
        /// <summary>
        /// Extras of every event, index aligned with the parsed event list of the chart.
        /// Entries are null for plain vanilla events. Never returns null.
        /// </summary>
        public static List<LightEventExtras> Parse(string json, int version)
        {
            var result = new List<LightEventExtras>();
            if (string.IsNullOrEmpty(json)) return result;
            try
            {
                JObject root = JObject.Parse(json);
                bool v2 = version == 2;
                JArray events = Field(root, v2 ? "_events" : "basicBeatmapEvents") as JArray
                                ?? Field(root, "_events") as JArray
                                ?? Field(root, "basicBeatmapEvents") as JArray;
                if (events == null) return result;

                foreach (var token in events)
                    result.Add(ParseEvent(token as JObject));
            }
            catch (System.Exception)
            {
                // No extras at all - the lightshow still plays (vanilla semantics).
                result.Clear();
            }
            return result;
        }

        private static LightEventExtras ParseEvent(JObject ev)
        {
            if (ev == null) return null;
            JObject cd = Field(ev, "_customData") as JObject ?? Field(ev, "customData") as JObject;
            if (cd == null) return null;

            var extras = new LightEventExtras();
            bool any = false;

            extras._lightID = ReadIntArray(cd, "lightID");
            any |= extras._lightID != null && extras._lightID.Length > 0;

            extras._nameFilter = ReadString(cd, "nameFilter", ref any);
            extras._easing = ReadString(cd, "easing", ref any);

            var lerp = Field(cd, "lerpType") ?? Field(cd, "_lerpType");
            if (lerp != null && lerp.Type == JTokenType.String)
            {
                extras._hsvLerp = string.Equals(lerp.ToString(), "HSV", StringComparison.OrdinalIgnoreCase);
                any = true;
            }

            if (TryFloat(cd, "speed", out float speed)) { extras._speed = speed; any = true; }
            if (TryFloat(cd, "rotation", out float rotation)) { extras._rotation = rotation; any = true; }
            if (TryFloat(cd, "prop", out float prop)) { extras._prop = prop; any = true; }
            if (TryFloat(cd, "step", out float step)) { extras._step = step; any = true; }
            if (TryFloat(cd, "direction", out float direction))
            {
                extras._direction = Math.Sign(direction);
                any = true;
            }

            var lockRotation = Field(cd, "lockRotation") ?? Field(cd, "_lockRotation");
            if (lockRotation != null && lockRotation.Type == JTokenType.Boolean)
            {
                extras._lockRotation = lockRotation.Value<bool>();
                any = true;
            }

            return any ? extras : null;
        }

        /// <summary>`"lightID": 7` AND `"lightID": [1,2,3]` are both valid.</summary>
        private static int[] ReadIntArray(JObject o, string name)
        {
            var t = Field(o, name) ?? Field(o, "_" + name);
            if (t == null) return null;

            if (t.Type == JTokenType.Array)
            {
                var list = new List<int>();
                foreach (var item in (JArray)t)
                {
                    if (TryInt(item, out int v)) list.Add(v);
                }
                return list.Count > 0 ? list.ToArray() : null;
            }

            if (TryInt(t, out int single)) return new[] { single };
            return null;
        }

        private static string ReadString(JObject o, string name, ref bool any)
        {
            // Both spellings exist (v2 uses the underscore) - Field is case insensitive,
            // so both are covered by trying the two names.
            var t = Field(o, name) ?? Field(o, "_" + name);
            if (t != null && t.Type == JTokenType.String)
            {
                any = true;
                string s = t.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            return null;
        }

        private static bool TryFloat(JObject o, string name, out float value)
        {
            value = 0f;
            var t = Field(o, name) ?? Field(o, "_" + name);
            if (t == null) return false;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
            {
                value = t.Value<float>();
                return true;
            }
            return false;
        }

        private static bool TryInt(JToken t, out int value)
        {
            value = 0;
            if (t == null) return false;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            {
                value = t.Value<int>();
                return true;
            }
            return false;
        }

        private static JToken Field(JObject o, string name)
        {
            if (o == null || string.IsNullOrEmpty(name)) return null;
            return o.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var v) ? v : null;
        }
    }
}

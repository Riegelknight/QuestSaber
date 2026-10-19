using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace QuestSaber
{
    /// <summary>
    /// Noodle/Heck animation properties.
    /// Source: https://heck.aeroluna.dev/animation/properties/
    /// </summary>
    public enum NoodleProperty
    {
        OffsetPosition,
        OffsetWorldRotation,
        LocalRotation,
        Scale,
        Dissolve,
        DissolveArrow,
        Interactable,
        DefinitePosition,
        Color,
        Time,
    }

    /// <summary>Names + dimensions of the animation properties (v3/v4 and v2 notation).</summary>
    public static class NoodlePropertyInfo
    {
        public struct Names { public string V3; public string V2; }

        public static readonly NoodleProperty[] All =
        {
            NoodleProperty.OffsetPosition, NoodleProperty.OffsetWorldRotation, NoodleProperty.LocalRotation,
            NoodleProperty.Scale, NoodleProperty.Dissolve, NoodleProperty.DissolveArrow,
            NoodleProperty.Interactable, NoodleProperty.DefinitePosition, NoodleProperty.Color, NoodleProperty.Time,
        };

        public static Names Of(NoodleProperty p)
        {
            switch (p)
            {
                case NoodleProperty.OffsetPosition: return new Names { V3 = "offsetPosition", V2 = "_position" };
                case NoodleProperty.OffsetWorldRotation: return new Names { V3 = "offsetWorldRotation", V2 = "_rotation" };
                case NoodleProperty.LocalRotation: return new Names { V3 = "localRotation", V2 = "_localRotation" };
                case NoodleProperty.Scale: return new Names { V3 = "scale", V2 = "_scale" };
                case NoodleProperty.Dissolve: return new Names { V3 = "dissolve", V2 = "_dissolve" };
                case NoodleProperty.DissolveArrow: return new Names { V3 = "dissolveArrow", V2 = "_dissolveArrow" };
                case NoodleProperty.Interactable: return new Names { V3 = "interactable", V2 = "_interactable" };
                case NoodleProperty.DefinitePosition: return new Names { V3 = "definitePosition", V2 = "_definitePosition" };
                case NoodleProperty.Color: return new Names { V3 = "color", V2 = "_color" };
                case NoodleProperty.Time: return new Names { V3 = "time", V2 = "_time" };
                default: return new Names { V3 = "", V2 = "" };
            }
        }

        /// <summary>Number of payload values per point (3 = vector, 1 = scalar, 4 = color).</summary>
        public static int Dimension(NoodleProperty p)
        {
            switch (p)
            {
                case NoodleProperty.OffsetPosition:
                case NoodleProperty.OffsetWorldRotation:
                case NoodleProperty.LocalRotation:
                case NoodleProperty.Scale:
                case NoodleProperty.DefinitePosition:
                    return 3;
                case NoodleProperty.Color:
                    return 4;
                default:
                    return 1;
            }
        }
    }

    /// <summary>Set of path animations (point definitions) per property.</summary>
    public sealed class NoodlePropertySet
    {
        public readonly Dictionary<NoodleProperty, NoodlePointDefinition> Paths = new();

        public bool Any => Paths.Count > 0;

        public void Set(NoodleProperty p, NoodlePointDefinition d)
        {
            if (d != null && !d.IsEmpty) Paths[p] = d;
        }
    }

    /// <summary>Noodle data of ONE beatmap object (note/wall/arc/chain).</summary>
    public sealed class NoodleObjectData
    {
        /// <summary>Heck tracks ("track": string or string[]).</summary>
        public string[] Tracks;

        /// <summary>Object-specific path animation ("animation" in the customData).</summary>
        public readonly NoodlePropertySet Animation = new();

        // ---- Object flags (Noodle/Chroma) ----
        public bool Fake;
        public bool Uninteractable;
        public bool DisableNoteGravity;
        public bool DisableNoteLook;
        public bool DisableBadCutDirection;
        public bool DisableBadCutSpeed;
        public bool DisableBadCutSaberType;
        public bool DisableDebris;
        public string Link;
        public bool? SpawnEffect;                 // Chroma: force/hide the spawn effect
        public float? NoteJumpMovementSpeed;      // per-object NJS
        public float? NoteJumpStartBeatOffset;    // per-object spawn offset

        // Obstacle: "size": [w, h, l]
        public Vector3? Size;
        // Chroma: static object color ("color")
        public bool HasColor;
        public Color Color = Color.white;

        public bool HasAnything =>
            Tracks != null || Animation.Any || Fake || Uninteractable || DisableNoteGravity || DisableNoteLook ||
            DisableBadCutDirection || DisableBadCutSpeed || DisableBadCutSaberType || DisableDebris ||
            Link != null || SpawnEffect.HasValue || NoteJumpMovementSpeed.HasValue ||
            NoteJumpStartBeatOffset.HasValue || Size.HasValue || HasColor;
    }

    /// <summary>An AnimateTrack/AssignPathAnimation event (custom event of the map).</summary>
    public sealed class NoodleTrackEvent
    {
        public float Beat;
        public bool IsPathAnimation;   // true = AssignPathAnimation, false = AnimateTrack
        public string[] Tracks;
        public float Duration;         // beats
        public string Easing = "easeLinear";
        public int Repeat;             // AnimateTrack: repetitions
        public readonly NoodlePropertySet Properties = new();
    }

    /// <summary>AssignTrackParent event: attach child tracks to a parent track.</summary>
    public sealed class NoodleParentEvent
    {
        public float Beat;
        public string ParentTrack;
        public string[] ChildrenTracks;
        public bool WorldPositionStays;
        public Vector3? OffsetPosition;
    }

    /// <summary>AssignPlayerToTrack event: attach the player object to a track.</summary>
    public sealed class NoodlePlayerEvent
    {
        public float Beat;
        public string Track;
        public string Target = "Root";   // Root|Head|LeftHand|RightHand
    }

    /// <summary>Chart-wide Noodle data (tracks, points, events, fake objects).</summary>
    public sealed class NoodleChartData
    {
        public bool HasAnything;
        /// <summary>True if the map defines chart-wide `pointDefinitions`.</summary>
        public bool HasPointDefinitions;
        public readonly List<NoodleTrackEvent> TrackEvents = new();
        public readonly List<NoodleParentEvent> ParentEvents = new();
        public readonly List<NoodlePlayerEvent> PlayerEvents = new();

        /// <summary>Fake objects (separate arrays in the customData). They stay out of the
        /// real note/wall lists so that they can never affect the score; they are currently
        /// only parsed (not spawned/scored).</summary>
        public readonly List<NoteModel> FakeNotes = new();
        public readonly List<ObstacleModel> FakeObstacles = new();
    }

    /// <summary>Result of a Noodle parse: chart data + object data per raw array
    /// (index-aligned with the order in the JSON so the caller can map them 1:1).</summary>
    public sealed class NoodleParseResult
    {
        public readonly NoodleChartData Chart = new();
        public NoodleObjectData[] Notes;
        public NoodleObjectData[] Bombs;
        public NoodleObjectData[] Obstacles;
        public NoodleObjectData[] Sliders;
        public NoodleObjectData[] BurstSliders;
    }

    /// <summary>
    /// Reads Noodle/Heck/Chroma data directly from the raw JSON (Newtonsoft), because
    /// Unity's JsonUtility cannot process dynamic keys and nested arrays
    /// (point definitions). Defensive: unreadable JSON is caught up front, and every
    /// object/event is parsed in its own try/catch so one malformed entry is skipped
    /// instead of aborting the whole chart parse (returns null/empty on failure).
    /// </summary>
    public static class NoodleParser
    {
        public static NoodleParseResult ParseChart(string json, int version)
        {
            RawPointDefinition.Clear();   // do not keep state from previous songs
            var result = new NoodleParseResult();
            JObject root;
            try { root = JObject.Parse(json); }
            catch (Exception ex) { Debug.LogWarning("[Noodle] JSON not readable: " + ex.Message); return result; }

            bool v2 = version <= 2;

            // ---- Point definitions (chart-wide) ----
            var customData = Field(root, v2 ? "_customData" : "customData") as JObject;
            var pointDefs = Field(customData, v2 ? "_pointDefinitions" : "pointDefinitions") as JObject;
            if (pointDefs != null)
            {
                // The dimension of a point definition depends on the target property;
                // therefore only remember the raw token and parse it on resolve (with cache).
                foreach (var kv in pointDefs) RawPointDefinition.Store(kv.Key, kv.Value);
                result.Chart.HasPointDefinitions = pointDefs.Count > 0;
            }

            // ---- Object arrays ----
            result.Notes = ParseObjectArray(Field(root, v2 ? "_notes" : "colorNotes") as JArray, v2);
            // v2 has no separate bomb list (bombs are `_type == 3` inside `_notes`), so pass
            // null on purpose; the real v3/v4 underscore fallback is the `_bombNotes` line below.
            result.Bombs = ParseObjectArray(v2 ? null : Field(root, "bombNotes") as JArray, v2);
            result.Obstacles = ParseObjectArray(Field(root, v2 ? "_obstacles" : "obstacles") as JArray, v2);
            result.Sliders = ParseObjectArray(Field(root, v2 ? "_sliders" : "sliders") as JArray, v2);
            result.BurstSliders = ParseObjectArray(Field(root, v2 ? "_chains" : "burstSliders") as JArray, v2);

            // v3/v4 use "_colorNotes"/"_bombNotes" (underscore variants) as an alternative notation
            if (result.Notes == null) result.Notes = ParseObjectArray(Field(root, "_colorNotes") as JArray, v2);
            if (result.Bombs == null) result.Bombs = ParseObjectArray(Field(root, "_bombNotes") as JArray, v2);

            // ---- Fake objects (separate arrays in customData) ----
            ParseFakeNotes(result.Chart.FakeNotes, Field(customData, v2 ? "_fakeColorNotes" : "fakeColorNotes") as JArray, false);
            ParseFakeNotes(result.Chart.FakeNotes, Field(customData, v2 ? "_fakeBombNotes" : "fakeBombNotes") as JArray, true);
            ParseFakeObstacles(result.Chart.FakeObstacles, Field(customData, v2 ? "_fakeObstacles" : "fakeObstacles") as JArray);

            // ---- Custom Events (AnimateTrack / AssignPathAnimation / Parent / Player) ----
            var events = Field(customData, v2 ? "_customEvents" : "customEvents") as JArray
                         ?? Field(customData, "_customEvents") as JArray;
            if (events != null)
            {
                foreach (var ev in events)
                {
                    // One malformed event must not abort the whole chart parse.
                    try { ParseCustomEvent(result.Chart, ev as JObject, v2); }
                    catch (Exception ex) { Debug.LogWarning("[Noodle] Custom event skipped: " + ex.Message); }
                }
            }

            result.Chart.HasAnything =
                result.Chart.HasPointDefinitions || result.Chart.TrackEvents.Count > 0 ||
                result.Chart.ParentEvents.Count > 0 || result.Chart.PlayerEvents.Count > 0 ||
                result.Chart.FakeNotes.Count > 0 || result.Chart.FakeObstacles.Count > 0 ||
                AnyObjectData(result.Notes) || AnyObjectData(result.Bombs) || AnyObjectData(result.Obstacles) ||
                AnyObjectData(result.Sliders) || AnyObjectData(result.BurstSliders);

            return result;
        }

        private static bool AnyObjectData(NoodleObjectData[] arr)
        {
            if (arr == null) return false;
            for (int i = 0; i < arr.Length; i++) if (arr[i] != null && arr[i].HasAnything) return true;
            return false;
        }

        // ---------- Object arrays ----------

        private static NoodleObjectData[] ParseObjectArray(JArray arr, bool v2)
        {
            if (arr == null) return null;
            var outArr = new NoodleObjectData[arr.Count];
            for (int i = 0; i < arr.Count; i++)
            {
                // Skip a single malformed object instead of aborting the whole parse
                // (e.g. "size": [1, 2, "x"] throws in the unguarded Value<float>() below).
                try { outArr[i] = ParseObject(arr[i] as JObject, v2); }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Noodle] Object " + i + " skipped: " + ex.Message);
                    outArr[i] = null;
                }
            }
            return outArr;
        }

        private static NoodleObjectData ParseObject(JObject obj, bool v2)
        {
            if (obj == null) return null;
            var cd = Field(obj, v2 ? "_customData" : "customData") as JObject;
            if (cd == null) return null;

            var d = new NoodleObjectData();

            // ---- Tracks ----
            var track = Field(cd, "track") ?? Field(cd, "_track");
            if (track is JArray ta)
            {
                d.Tracks = new string[ta.Count];
                for (int i = 0; i < ta.Count; i++) d.Tracks[i] = ta[i]?.ToString();
            }
            else if (track != null && track.Type == JTokenType.String)
            {
                d.Tracks = new[] { track.ToString() };
            }

            // ---- Track-specific/individual path animation ----
            var anim = Field(cd, v2 ? "_animation" : "animation") as JObject;
            if (anim != null) ParseAnimation(anim, d.Animation);

            // ---- Flags ----
            d.Uninteractable = Bool(cd, "uninteractable") || (v2 && !BoolOr(cd, "_interactable", true));
            d.DisableNoteGravity = Bool(cd, "disableNoteGravity") || Bool(cd, "_disableNoteGravity");
            d.DisableNoteLook = Bool(cd, "disableNoteLook") || Bool(cd, "_disableNoteLook");
            d.DisableBadCutDirection = Bool(cd, "disableBadCutDirection") || Bool(cd, "_disableBadCutDirection");
            d.DisableBadCutSpeed = Bool(cd, "disableBadCutSpeed") || Bool(cd, "_disableBadCutSpeed");
            d.DisableBadCutSaberType = Bool(cd, "disableBadCutSaberType") || Bool(cd, "_disableBadCutSaberType");
            d.DisableDebris = Bool(cd, "disableDebris") || Bool(cd, "_disableDebris");
            d.Fake = Bool(cd, "_fake") || Bool(cd, "fake");
            d.Link = (Field(cd, "link") ?? Field(cd, "_link"))?.ToString();

            var spawn = Field(cd, "spawnEffect") ?? Field(cd, "_spawnEffect");
            if (spawn != null && spawn.Type == JTokenType.Boolean) d.SpawnEffect = spawn.Value<bool>();

            var njs = Float(cd, "noteJumpMovementSpeed", "_noteJumpMovementSpeed");
            if (njs.HasValue) d.NoteJumpMovementSpeed = njs;
            var njo = Float(cd, "noteJumpStartBeatOffset", "_noteJumpStartBeatOffset");
            if (njo.HasValue) d.NoteJumpStartBeatOffset = njo;

            var size = Field(cd, "size") ?? Field(cd, "_size");
            if (size is JArray sa && sa.Count >= 3)
            {
                float sx = 0f, sy = 0f, sz = 0f;
                if (TryFloat(sa[0], out sx) && TryFloat(sa[1], out sy) && TryFloat(sa[2], out sz))
                    d.Size = new Vector3(sx, sy, sz);
            }

            // ---- Chroma: static color ----
            var color = Field(cd, "color") ?? Field(cd, "_color");
            if (color is JArray ca && ca.Count >= 3)
            {
                float cr = 0f, cg = 0f, cb = 0f, caVal = 1f;
                if (TryFloat(ca[0], out cr) && TryFloat(ca[1], out cg) && TryFloat(ca[2], out cb))
                {
                    if (ca.Count >= 4) TryFloat(ca[3], out caVal);
                    d.HasColor = true;
                    d.Color = new Color(cr, cg, cb, caVal);
                }
            }

            return d.HasAnything ? d : null;
        }

        /// <summary>Parses an "animation" object (property -> point definition).</summary>
        private static void ParseAnimation(JObject anim, NoodlePropertySet target)
        {
            foreach (var prop in NoodlePropertyInfo.All)
            {
                var names = NoodlePropertyInfo.Of(prop);
                var token = Field(anim, names.V3) ?? Field(anim, names.V2);
                if (token == null) continue;
                var def = ParsePointDefinition(token, NoodlePropertyInfo.Dimension(prop));
                target.Set(prop, def);
            }
        }

        // ---------- Point definitions ----------

        /// <summary>
        /// Parses a point definition. Supports: null, reference by name (string),
        /// single-point shorthand and list form. Nested modifier arrays are
        /// (in this stage) discarded.
        /// </summary>
        public static NoodlePointDefinition ParsePointDefinition(JToken token, int dimension)
        {
            if (token == null || token.Type == JTokenType.Null) return null;

            // Reference to a chart-wide point definition
            if (token.Type == JTokenType.String)
                return RawPointDefinition.Resolve(token.ToString(), dimension);

            if (token.Type != JTokenType.Array) return null;
            var arr = (JArray)token;
            if (arr.Count == 0) return null;

            var def = new NoodlePointDefinition(dimension);

            if (arr[0].Type == JTokenType.Array)
            {
                // List of points
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JArray p) def.AddRaw(ToPointValues(p));
                    // Base references ("baseHeadPosition" ...) are stage 2 and are skipped.
                }
            }
            else if (arr[0].Type == JTokenType.String)
            {
                // Base reference as the whole definition ("baseHeadPosition.x") - stage 2
                return null;
            }
            else
            {
                // Single-point shorthand: [245, 23, 54] == [[245, 23, 54, 0]]
                def.AddRaw(ToPointValues(arr));
            }

            if (def.IsEmpty) return null;
            def.SortByTime();
            return def;
        }

        /// <summary>Converts a point (JSON array) into a flat value list.
        /// Nested arrays (modifiers) are skipped.</summary>
        private static object[] ToPointValues(JArray p)
        {
            var list = new List<object>(p.Count);
            for (int i = 0; i < p.Count; i++)
            {
                var t = p[i];
                switch (t.Type)
                {
                    case JTokenType.Float: case JTokenType.Integer: list.Add(t.Value<float>()); break;
                    case JTokenType.Boolean: list.Add(t.Value<bool>() ? 1f : 0f); break;
                    case JTokenType.String: list.Add(t.ToString()); break;
                    default: break; // nested arrays/objects = modifier -> ignore for now
                }
            }
            return list.ToArray();
        }

        // ---------- Fake arrays ----------

        private static void ParseFakeNotes(List<NoteModel> target, JArray arr, bool bomb)
        {
            if (arr == null) return;
            foreach (var t in arr)
            {
                if (!(t is JObject o)) continue;
                target.Add(new NoteModel
                {
                    _time = Float(o, "b", "_time") ?? 0f,
                    _lineIndex = (int)(Float(o, "x", "_lineIndex") ?? 0f),
                    _lineLayer = (int)(Float(o, "y", "_lineLayer") ?? 0f),
                    _type = bomb ? 3 : (int)(Float(o, "c", "_type") ?? 1f),
                    _cutDirection = (int)(Float(o, "d", "_cutDirection") ?? 8f),
                });
            }
        }

        private static void ParseFakeObstacles(List<ObstacleModel> target, JArray arr)
        {
            if (arr == null) return;
            foreach (var t in arr)
            {
                if (!(t is JObject o)) continue;
                target.Add(new ObstacleModel
                {
                    _time = Float(o, "b", "_time") ?? 0f,
                    _lineIndex = (int)(Float(o, "x", "_lineIndex") ?? 0f),
                    _lineLayer = (int)(Float(o, "y", "_lineLayer") ?? 0f),
                    _duration = Float(o, "d", "_duration") ?? 1f,
                    _width = (int)(Float(o, "w", "_width") ?? 1f),
                    _height = (int)(Float(o, "h", "_height") ?? 1f),
                });
            }
        }

        // ---------- Custom Events ----------

        private static void ParseCustomEvent(NoodleChartData chart, JObject ev, bool v2)
        {
            if (ev == null) return;
            string type = (Field(ev, "t") ?? Field(ev, "_type"))?.ToString();
            float beat = Float(ev, "b", "_time") ?? 0f;
            var data = (Field(ev, "d") ?? Field(ev, "_data")) as JObject;
            if (type == null || data == null) return;

            switch (type)
            {
                case "AnimateTrack":
                case "AssignPathAnimation":
                {
                    var e = new NoodleTrackEvent
                    {
                        Beat = beat,
                        IsPathAnimation = type == "AssignPathAnimation",
                        Tracks = ReadTracks(data),
                        Duration = Float(data, "duration", "_duration") ?? 0f,
                        Easing = (Field(data, "easing") ?? Field(data, "_easing"))?.ToString() ?? "easeLinear",
                        Repeat = (int)(Float(data, "repeat", "_repeat") ?? 0f),
                    };
                    foreach (var prop in NoodlePropertyInfo.All)
                    {
                        var names = NoodlePropertyInfo.Of(prop);
                        var tok = Field(data, names.V3) ?? Field(data, names.V2);
                        if (tok == null) continue;
                        e.Properties.Set(prop, ParsePointDefinition(tok, NoodlePropertyInfo.Dimension(prop)));
                    }
                    if (e.Tracks != null && e.Properties.Any) chart.TrackEvents.Add(e);
                    break;
                }
                case "AssignTrackParent":
                {
                    var e = new NoodleParentEvent
                    {
                        Beat = beat,
                        ParentTrack = (Field(data, "parentTrack") ?? Field(data, "_parentTrack"))?.ToString(),
                        ChildrenTracks = ReadTracks(data, "childrenTracks"),
                        WorldPositionStays = Bool(data, "worldPositionStays") || Bool(data, "_worldPositionStays"),
                    };
                    var off = Field(data, "offsetPosition") as JArray;
                    if (off != null && off.Count >= 3)
                    {
                        float ox = 0f, oy = 0f, oz = 0f;
                        if (TryFloat(off[0], out ox) && TryFloat(off[1], out oy) && TryFloat(off[2], out oz))
                            e.OffsetPosition = new Vector3(ox, oy, oz);
                    }
                    if (!string.IsNullOrEmpty(e.ParentTrack) && e.ChildrenTracks != null)
                        chart.ParentEvents.Add(e);
                    break;
                }
                case "AssignPlayerToTrack":
                {
                    var e = new NoodlePlayerEvent
                    {
                        Beat = beat,
                        Track = (Field(data, "track") ?? Field(data, "_track"))?.ToString(),
                        Target = (Field(data, "target") ?? Field(data, "_target"))?.ToString() ?? "Root",
                    };
                    if (!string.IsNullOrEmpty(e.Track)) chart.PlayerEvents.Add(e);
                    break;
                }
            }
        }

        private static string[] ReadTracks(JObject data, string key = "track")
        {
            var t = Field(data, key) ?? Field(data, "_" + key);
            if (t is JArray a)
            {
                var arr = new string[a.Count];
                for (int i = 0; i < a.Count; i++) arr[i] = a[i]?.ToString();
                return arr;
            }
            if (t != null && t.Type == JTokenType.String) return new[] { t.ToString() };
            return null;
        }

        // ---------- JSON helpers ----------

        private static bool TryFloat(JToken t, out float val)
        {
            if (t != null)
            {
                if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
                {
                    val = t.Value<float>();
                    return true;
                }
                if (t.Type == JTokenType.String &&
                    float.TryParse(t.ToString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out val))
                {
                    return true;
                }
            }
            val = 0f;
            return false;
        }

        private static JToken Field(JObject o, string name)
        {
            if (o == null || name == null) return null;
            return o.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var v) ? v : null;
        }

        private static bool Bool(JObject o, string name)
        {
            var t = Field(o, name);
            return t != null && t.Type == JTokenType.Boolean && t.Value<bool>();
        }

        private static bool BoolOr(JObject o, string name, bool fallback)
        {
            var t = Field(o, name);
            return t != null && t.Type == JTokenType.Boolean ? t.Value<bool>() : fallback;
        }

        private static float? Float(JObject o, string a, string b)
        {
            var t = Field(o, a) ?? Field(o, b);
            if (t == null) return null;
            if (TryFloat(t, out float val)) return val;
            return null;
        }
    }

    /// <summary>
    /// Cache for chart-wide point definitions whose dimension is only known on
    /// resolve (it depends on the target property). Instances are parsed
    /// per (name, dimension) and reused.
    /// </summary>
    internal static class RawPointDefinition
    {
        private static readonly Dictionary<string, JToken> Raw = new();
        private static readonly Dictionary<(string, int), NoodlePointDefinition> Cache = new();

        public static void Clear() { Raw.Clear(); Cache.Clear(); }
        public static void Store(string name, JToken token) => Raw[name] = token;

        public static NoodlePointDefinition Resolve(string name, int dimension)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var key = (name, dimension);
            if (Cache.TryGetValue(key, out var cached)) return cached;
            if (!Raw.TryGetValue(name, out var token)) return null;
            var def = NoodleParser.ParsePointDefinition(token, dimension);
            Cache[key] = def;
            return def;
        }
    }
}

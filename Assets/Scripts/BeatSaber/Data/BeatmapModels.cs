using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestSaber
{
    /// <summary>A note in the BeatSaver v2 format.</summary>
    [Serializable]
    public sealed class NoteModel
    {
        public float _time;        // in beats (conversion via BeatmapParser.BuildBeatToSeconds)
        public int _lineIndex;     // 0..3 (column)
        public int _lineLayer;     // 0..2 (row)
        public int _type;          // 0 = red/left, 1 = blue/right, 3 = bomb
        public int _cutDirection;  // 0..8 arrow direction

        /// <summary>v3/v4: angle offset "a" in degrees, added to the arrow direction
        /// (ArcViewer `CalculateObjectAngle(d, a)`). v2 does not know the field -> 0.</summary>
        public float _angleOffset;

        /// <summary>Set by the parser: this note is the head of a v3/v4 chain
        /// (burstSlider). It is scored as a chain head (max. 85 instead of 115: no
        /// follow-through) and starts the chain links. Not persisted.</summary>
        [NonSerialized] public bool _isChainHead;

        /// <summary>Linked at runtime: if this note is the HEAD NOTE of an arc
        /// (slider), this points to its model. Then, when the note is cut, the
        /// saber moves to the new start of the arc (saber attraction, original
        /// `SliderController.SetSaberAttraction`). Not persisted.</summary>
        [NonSerialized] public ArcModel _arcHeadOf;

        /// <summary>Noodle data of this note (tracks, path animation, flags). Null = none.</summary>
        [NonSerialized] public NoodleObjectData Noodle;

        /// <summary>Noodle Extensions custom data (v2 `_customData` / v3/v4 `customData`).
        /// Position/rotation/flip/scale change the spawn behavior.</summary>
        public NoteCustomData _customData;
    }

    /// <summary>
    /// Noodle Extensions custom data of a note (excerpt: position/rotation/scale/flip).
    /// v3/v4 use unprefixed names, v2 the "_" variant - both are available as fields;
    /// the properties choose the present source. Missing fields are null/false.
    /// </summary>
    [Serializable]
    public sealed class NoteCustomData
    {
        // ---- v3/v4 ----
        public float[] coordinates;      // [x, y] (Noodle grid, fractional)
        public float[] worldRotation;    // [x, y, z, w] Quaternion
        public float[] localRotation;    // [x, y, z, w] Quaternion
        public float[] scale;            // [x, y, z]
        public float[] flip;             // [flipLineIndex, flipJump]
        // ---- v2 ----
        public float[] _position;        // [x, y]
        public float[] _rotation;        // [x, y, z, w]
        public float[] _localRotation;   // [x, y, z, w]
        public float[] _scale;           // [x, y, z]
        public float[] _flip;

        public float[] Position => coordinates ?? _position;
        public float[] WorldRotation => worldRotation ?? _rotation;
        public float[] LocalRotation => localRotation ?? _localRotation;
        public float[] Scale => scale ?? _scale;
        public float[] FlipArray => flip ?? _flip;
        public bool Flip => HasFlip;

        public bool HasPosition => Position != null && Position.Length >= 2;
        public bool HasWorldRotation => WorldRotation != null && WorldRotation.Length >= 4;
        public bool HasLocalRotation => LocalRotation != null && LocalRotation.Length >= 4;
        public bool HasScale => Scale != null && Scale.Length >= 3;
        public bool HasFlip => FlipArray != null && FlipArray.Length > 0;
    }

    /// <summary>An obstacle (_obstacles) in the v2 format.</summary>
    [Serializable]
    public sealed class ObstacleModel
    {
        public float _time;        // in beats
        public int _lineIndex;     // start column
        public int _type;          // 0 = full wall, 1 = crouch wall
        public float _duration;    // in beats
        public int _width;         // in columns
        /// <summary>Lower row of the obstacle in grid heights (0 = floor). v3/v4 from
        /// "y"; for v2 (0) it is derived from _type (crouch wall = 2).</summary>
        public int _lineLayer;
        /// <summary>Height of the obstacle in grid heights (v3/v4 from "h"). 0 = derive from _type.</summary>
        public int _height;

        /// <summary>Noodle data of this wall (tracks, "size", path animation). Null = none.</summary>
        [NonSerialized] public NoodleObjectData Noodle;
    }

    /// <summary>A BPM event (v2: _BPMChanges / v3+v4: bpmEvents) for the beat->time conversion.</summary>
    [Serializable]
    public sealed class BeatBpmPoint
    {
        public float Beat;   // beat time of the change (v2: _time, v3/v4: b)
        public float Bpm;    // BPM from this point on (v2: _BPM, v3/v4: m)
    }

    /// <summary>Beatmap chart (v2/v3/v4 .dat, uniformly as v2 models).</summary>
    [Serializable]
    public sealed class BeatmapChart
    {
        public List<NoteModel> _notes = new();
        public List<ObstacleModel> _obstacles = new();

        /// <summary>BPM progression of the map (empty = constant info BPM). Filled by the parser
        /// and determines the exact beat->seconds conversion (otherwise all notes shift
        /// after a BPM change -> timing error -> always "MISS").</summary>
        public List<BeatBpmPoint> BpmEvents = new();

        // Arcs (v2.1 "_sliders" / v3+v4 "sliders") and chains (v2.1 "_chains" /
        // v3+v4 "burstSliders") are read, spawned and scored.
        [NonSerialized] public List<ArcModel> Sliders = new();
        [NonSerialized] public List<ChainModel> Chains = new();

        /// <summary>Lightshow base events (v2 "_events" / v3+v4 "basicBeatmapEvents"):
        /// control colors/rings/lasers of the environment (mode "Animated").</summary>
        [NonSerialized] public List<LightEventModel> LightEvents = new();
        /// <summary>Color boost events (v4 "colorBoostBeatmapEvents" / v2 _customData).</summary>
        [NonSerialized] public List<ColorBoostModel> BoostEvents = new();

        /// <summary>Chart-wide Noodle data (tracks, point definitions, events, fake objects).
        /// Null if the map contains no Noodle data.</summary>
        [NonSerialized] public NoodleChartData Noodle;
    }

    /// <summary>A lightshow event (beat, type, value, float value).</summary>
    public sealed class LightEventModel
    {
        public float _time;         // beat time (v2 _time / v3+v4 b)
        public int _type;           // LightEventType (v2 _type / v3+v4 t)
        public int _value;          // LightEventValue (v2 _value / v3+v4 i)
        public float _floatValue = 1f; // brightness (v2 _floatValue / v3+v4 f)
        // Chroma: per-event color (`customData.color`/`_customData._color`).
        public bool _hasCustomColor;
        public Color _customColor = Color.white;

        /// <summary>Chroma/Heck extras of this event (lightID, nameFilter, easing, HSV lerp,
        /// ring/laser parameters). Null = plain vanilla event. Read by
        /// <see cref="LightshowExtrasParser"/> in a second (Newtonsoft) pass - see there.
        /// Reference: ArcViewer `BeatmapCustomBasicEventData`.</summary>
        [NonSerialized] public LightEventExtras Extras;
    }

    /// <summary>
    /// Chroma/Heck extras of ONE lightshow event, names as in the map format (BSMG):
    /// `lightID`, `nameFilter`, `easing`, `lerpType`, `speed`, `rotation`, `prop`,
    /// `step`, `direction`, `lockRotation`.
    /// Values the map did not set stay neutral (NaN for numbers, null for arrays and
    /// strings), so the ArcViewer/Beat Saber defaults apply unchanged.
    /// </summary>
    public sealed class LightEventExtras
    {
        /// <summary>Which light emitters the event addresses (Chroma lightID). Null or
        /// empty = every emitter of that type (vanilla behaviour).</summary>
        public int[] _lightID;

        /// <summary>Ring spin (type 8): only the rings with this name move
        /// ("SmallTrackLaneRings"/"BigTrackLaneRings"). Null = all rings.</summary>
        public string _nameFilter;

        /// <summary>Transition easing ("easeOutCubic", ...). Null = linear.</summary>
        public string _easing;

        /// <summary>Transition interpolation in HSV instead of RGB (Chroma `lerpType`).</summary>
        public bool _hsvLerp;

        public float _speed = float.NaN;      // ring spin/zoom + laser rotation speed
        public float _rotation = float.NaN;   // ring spin: rotation amount
        public float _prop = float.NaN;       // ring spin: how much later each ring starts
        public float _step = float.NaN;       // ring spin/zoom: per-ring step
        public int _direction;                // 0 = unset, 1 = clockwise, -1 = counter clockwise
        public bool _lockRotation;            // laser: keep the current angle
    }

    /// <summary>A color boost event (beat + on/off).</summary>
    public sealed class ColorBoostModel
    {
        public float _time;         // beat (v3/v4 b)
        public bool _boost;         // v3/v4 o
    }

    /// <summary>An arc (v2.1 "_sliders" / v3+v4 "sliders"): connects head and tail point.
    /// The head/tail notes exist separately as colorNotes and are scored normally; the
    /// arc itself is purely visual (ArcViewer `ArcManager`).</summary>
    [Serializable]
    public sealed class ArcModel
    {
        public float _headTime;                 // b
        public int _headLineIndex;              // x
        public int _headLineLayer;              // y
        public int _headColorType;              // c (0=red/left, 1=blue/right)
        public float _headCutDirection;         // d (0..8)
        public float _headControlPointLengthMultiplier; // mu (v2.1: _headControlPointLengthMultiplier)
        public float _tailTime;                 // tb
        public int _tailLineIndex;              // tx
        public int _tailLineLayer;              // ty
        public float _tailCutDirection;         // tc (v3/v4) / _tailCutDirection (v2.1)
        public float _tailControlPointLengthMultiplier; // tmu

        /// <summary>Set at runtime (not persisted): a note sits at the head or tail
        /// (same time + grid cell). Then ArcView shifts the point by
        /// <see cref="GameConfig.ArcAttachOffset"/> in the arc direction (ArcViewer
        /// ObjectManager `halfNoteOffset`) so the arc ends at the note edge.</summary>
        [System.NonSerialized] public bool _headAttached;
        [System.NonSerialized] public bool _tailAttached;

        /// <summary>Head note linked at runtime (same time + grid cell). Target of the
        /// saber attraction: if it is cut, the saber moves to the new start of the
        /// arc. Not persisted.</summary>
        [System.NonSerialized] public NoteModel _headNote;

        /// <summary>Noodle data of this arc (tracks, path animation). Null = none.</summary>
        [System.NonSerialized] public NoodleObjectData Noodle;
    }

    /// <summary>A chain (v2.1 "_chains" / v3+v4 "burstSliders"): head note (real colorNote)
    /// plus generated links between head and tail point (ArcViewer `ChainManager`).</summary>
    [Serializable]
    public sealed class ChainModel
    {
        public float _time;             // b = head beat
        public int _lineIndex;          // x
        public int _lineLayer;          // y
        public int _colorType;          // c
        public float _cutDirection;     // d
        public float _tailTime;         // tb (v3/v4; v2.1 has no tail point)
        public int _tailLineIndex;      // tx
        public int _tailLineLayer;      // ty
        public float _sliceCount;       // sc (number of segments; head + sc-1 links)
        public float _squishAmount;     // s (0/missing = 1)

        /// <summary>Head note linked by the parser (v3/v4: exists as colorNote).
        /// Not persisted.</summary>
        [NonSerialized] public NoteModel _headNote;

        /// <summary>Noodle data of this chain (tracks, path animation). Null = none.</summary>
        [NonSerialized] public NoodleObjectData Noodle;
    }

    /// <summary>A playable difficulty of a song (only Standard characteristic).</summary>
    public sealed class SongDifficulty
    {
        public string Name = "";               // "Easy".."ExpertPlus"
        public int Rank;                       // 1..5 (classic Beat Saber scheme)
        public string BeatmapFileName = "";    // .dat in the song folder
        public float NoteJumpMovementSpeed = 0f;   // NJS of this difficulty (0 = map specifies nothing)
        public float NoteJumpStartBeatOffset = 0f; // spawn offset in beats
        public string Key;                     // SongKey + "|" + Name (highscore key)

        /// <summary>Visible short label (read from the info).</summary>
        public string DisplayName => string.IsNullOrEmpty(Name) ? "Normal" : Name;
    }

    /// <summary>Data from info.json (v2).</summary>
    public sealed class SongMeta
    {
        public string FolderName = "";
        public string FolderPath = "";             // absolute song folder path (for MapPath per difficulty)
        public string SongName = "";
        public string SongAuthorName = "";
        public string SongFileName = "";       // OGG/MP3 in the song folder
        public string MapFileName = "";        // .dat of the CURRENTLY selected difficulty
        public float BeatsPerMinute = 120f;
        /// <summary>Environment name of the map (info.json `_environmentName` or v3/v4
        /// `environmentNames`). Determines the ArcViewer background environment; fallback
        /// DefaultEnvironment.</summary>
        public string EnvironmentName = "DefaultEnvironment";

        /// <summary>Map-specific environment colors (`_envColorLeft/Right` + boost from the info
        /// or difficulty _customData). If set, they override the default palette
        /// of the environment (as in the original/ArcViewer).</summary>
        public bool HasEnvironmentColors;
        public Color EnvColorLeft = new Color(0.85f, 0.085f, 0.085f);
        public Color EnvColorRight = new Color(0.1882353f, 0.675294f, 1f);
        public Color EnvColorLeftBoost = new Color(0.85f, 0.085f, 0.085f);
        public Color EnvColorRightBoost = new Color(0.1882353f, 0.675294f, 1f);
        public bool IsDemo = false;
        public string AudioPath = "";          // absolute path of the audio file (no demo)
        public string MapPath = "";            // absolute path of the .dat of the selected difficulty

        // NJS / spawn behavior (applies to the selected difficulty; 0 = map specifies nothing)
        public float NoteJumpMovementSpeed = 0f;   // jump speed of the notes in m/s
        public float NoteJumpStartBeatOffset = 0f; // spawn offset in beats

        /// <summary>All playable Standard difficulties (ascending by rank).</summary>
        public List<SongDifficulty> Difficulties = new();
        /// <summary>Index of the currently selected difficulty (is persisted).</summary>
        public int SelectedDifficulty;
        public int LegacyDefaultIndex;

        /// <summary>The currently selected difficulty (null if none present).</summary>
        public SongDifficulty CurrentDifficulty =>
            Difficulties != null && SelectedDifficulty >= 0 && SelectedDifficulty < Difficulties.Count
                ? Difficulties[SelectedDifficulty]
                : null;

        /// <summary>Unique highscore key incl. difficulty ("Song|Difficulty").</summary>
        public string BestKey
        {
            get
            {
                var d = CurrentDifficulty;
                return d != null ? d.Key : Key;
            }
        }

        /// <summary>Unique key (for best scores).</summary>
        public string Key => IsDemo ? DemoKey : FolderName;
        public const string DemoKey = "__demo__";

        /// <summary>Adapts MapFileName/MapPath and NJS to the selected difficulty (call before LoadSong/ParseChart).</summary>
        public void ApplySelectedDifficulty()
        {
            var d = CurrentDifficulty;
            if (d == null) return;
            MapFileName = d.BeatmapFileName;
            MapPath = string.IsNullOrEmpty(FolderPath) ? d.BeatmapFileName
                : BeatmapParser.ResolveMapFile(FolderPath, d.BeatmapFileName);
            NoteJumpMovementSpeed = d.NoteJumpMovementSpeed;
            NoteJumpStartBeatOffset = d.NoteJumpStartBeatOffset;
        }
    }

    /// <summary>Entry of a best score per song (Key = "Song|Difficulty").</summary>
    [Serializable]
    public sealed class ScoreRecord
    {
        public string Key;
        public int Score;
    }

    /// <summary>Persisted difficulty choice per song (global, not per player).</summary>
    [Serializable]
    public sealed class SongDiffChoice
    {
        public string SongKey;
        public string Difficulty;
    }

    /// <summary>Player profile with best scores per song and own room scaling.</summary>
    [Serializable]
    public sealed class PlayerProfile
    {
        public string Name;
        public float HeightCm = 175f;
        /// <summary>Room scaling (1 = reference size). Is saved per player and
        /// scales the entire game (notes/walls/HUD/sabers).</summary>
        public float Scale = 1f;
        /// <summary>"Run comfort" (Run and Play, personal, persistent): own
        /// multiplier on the game speed computed from room speed x note speed factor.
        /// Default 1.0 = notes exactly static; range
        /// 0.2...3.0 (GameConfig.RunComfort*).</summary>
        public float RunComfort = GameConfig.RunComfortDefault;
        public List<ScoreRecord> BestScores = new();

        public int GetBest(string songKey)
        {
            foreach (var r in BestScores)
            {
                if (r.Key == songKey) return r.Score;
            }
            return 0;
        }

        public void SetBest(string songKey, int score)
        {
            foreach (var r in BestScores)
            {
                if (r.Key == songKey)
                {
                    if (score > r.Score) r.Score = score;
                    return;
                }
            }
            BestScores.Add(new ScoreRecord { Key = songKey, Score = score });
        }
    }

    /// <summary>Persistence container (JSON).</summary>
    [Serializable]
    public sealed class SaveData
    {
        public List<PlayerProfile> Players = new();
        public string SelectedPlayer = "";
        // Audio offset (menu "Audio Offset"): shifts EXCLUSIVELY the audible music
        // relative to the notes. STARTS at 0 - the 85 ms previously calibrated on the PICO 4 is now baked
        // into the note clock (GameConfig.AudioLatencyBaseMs = 275). The game is in sync at
        // AudioOffset == AudioLatencyBaseMs (the base shift AudioOffsetBaseMs cancels
        // out); 0 = no hard offset at all anymore. On other devices/outputs
        // (e.g. Bluetooth) adjustable in the menu.
        public float SoundDelayMs = 0f;
        /// <summary>One-time migration: the former hard PICO calibration value (-85 ms) is
        /// baked into <see cref="AudioLatencyBaseMs"/>. On the first start after the rework,
        /// a stored offset is therefore pulled to 0 once.</summary>
        public bool SoundDelayBakedIn = false;
        public bool PassthroughDisabled = false;
        public int BackgroundMode = 0;
        /// <summary>"Room follows player" (menu, global, persistent): true = the virtual room
        /// shifts with the forward/backward movement of the player (depth follow).
        /// false = the room stays spatially fixed.</summary>
        public bool RoomFollowsPlayer = false;
        /// <summary>"Run and Play" (menu, global, persistent): true = game AND
        /// audio speed are coupled to the physical forward movement;
        /// "Room follows player" is forcibly enabled and locked. See
        /// GameConfig/`RunAndPlay`.</summary>
        public bool RunAndPlay = false;
        /// <summary>Vertical offset of the note/mine field and floor-free walls in meters
        /// ("field height" in the menu, global, persistent, ±1.5 m in 0.05 m steps). 0 = pure
        /// ArcViewer grid. Also affects the hit/score position (note box = display;
        /// ground-based walls remain unchanged).</summary>
        public float FieldHeightMeters = 0f;
        /// <summary>Selected difficulty per song (stays active after restart/switch).</summary>
        public List<SongDiffChoice> DifficultyChoices = new();
    }
}

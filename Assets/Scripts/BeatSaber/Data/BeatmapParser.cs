using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

// The JSON DTO classes below are filled by Unity's JsonUtility/reflection; the
// compiler sees no assignment and would report CS0649 for every field. This is
// normal and harmless for serialization DTOs - so it is deliberately silenced here.
#pragma warning disable 0649

namespace QuestSaber
{
    /// <summary>
    /// Reads real BeatSaver v2 maps: info.json is tried FIRST (info.dat is the fallback)
    /// for the song metadata + Standard difficulty, and the .dat for the notes/mines/walls.
    /// All JSON is deserialized via Unity's JsonUtility (see the FromJson&lt;T&gt; helper)
    /// into the minimal DTO wrapper classes below.
    /// </summary>
    public static class BeatmapParser
    {
        // ---------- NJS/_customData (v2.0: directly on the entry, v2.1: in _customData) ----------
        [Serializable]
        private sealed class DifficultyCustomData
        {
            public float _noteJumpMovementSpeed = 0f;
            public float _noteJumpStartBeatOffset = 0f;
            // Environment colors are NOT read here (the JSON shape varies: array vs.
            // object) - for that the more robust raw text scan `ApplyRawEnvColors`.
        }

        [Serializable]
        private sealed class ChartCustomData
        {
            public float _noteJumpMovementSpeed = 0f;
            public float _noteJumpStartBeatOffset = 0f;
            // Map-specific environment colors (some maps put them in the difficulty).
            public float[] _envColorLeft;
            public float[] _envColorRight;
            public float[] _envColorLeftBoost;
            public float[] _envColorRightBoost;
        }

        // ---------- info.json ----------
        [Serializable]
        private sealed class InfoJson
        {
            [Serializable]
            public sealed class DifficultySet
            {
                [Serializable]
                public sealed class Difficulty
                {
                    public string _difficulty;
                    public string _beatmapFilename;

                    // v2.0.0: NJS/offset are DIRECTLY on the difficulty entry,
                    // v2.1.0: they are in _customData - both are read.
                    public float _noteJumpMovementSpeed = 0f;
                    public float _noteJumpStartBeatOffset = 0f;
                    public DifficultyCustomData _customData;
                }

                public string _beatmapCharacteristicName;
                public List<Difficulty> _difficultyBeatmaps = new();
            }

            public string _songName = "";
            public string _songSubName = "";
            public string _songAuthorName = "";
            public float _beatsPerMinute = 120f;
            public string _songFilename = "";
            // Environment: v2 `_environmentName`, v3/v4 `environmentNames` (list).
            public string _environmentName = "";
            public List<string> environmentNames;
            // Environment colors: v2/v3 in `_customData._envColor*`, v4 also as hex
            // (`environmentColor0/1/...`).
            public InfoCustomData _customData;
            public string environmentColor0;
            public string environmentColor1;
            public string environmentColor0Boost;
            public string environmentColor1Boost;
            public List<DifficultySet> _difficultyBeatmapSets = new();
        }

        [Serializable]
        private sealed class InfoCustomData
        {
            public float[] _envColorLeft;
            public float[] _envColorRight;
            public float[] _envColorLeftBoost;
            public float[] _envColorRightBoost;
        }

        // ---------- .dat (v2 legacy: long field names) ----------
        [Serializable]
        private sealed class ChartJsonV2
        {
            public string _version = "";
            public List<NoteModel> _notes = new();
            public List<ObstacleModel> _obstacles = new();

            // Some maps carry NJS/offset at the beginning of the difficulty .dat:
            public ChartCustomData _customData = null;
            // Arcs/chains (v2.1) are read into the chart and played (see ParseChartCore).
            public List<ArcModel> _sliders = null;
            public List<ChainModel> _chains = null;
            // BPM changes of the map (time in beats)
            public List<V2BpmChange> _BPMChanges = null;
            // Lightshow events (v2: _events)
            public List<V2Event> _events = null;
        }

        // v2 BPM changes (time in beats + new BPM)
        [Serializable]
        private sealed class V2BpmChange
        {
            public float _time;
            public float _BPM;
        }

        // ---------- .dat (v3/v4: short field names) ----------
        // v3/v4 notes: b=beat, x=column, y=row, c=color type (0=red/left, 1=blue/right), d=cut direction, a=angle offset
        [Serializable]
        private sealed class V3Note
        {
            public float b;
            public int x;
            public int y;
            public int c;
            public int d;
            public float a;
            // Noodle Extensions (v3/v4): position/rotation/flip/scale/per-note NJS.
            public NoteCustomData customData;
        }

        // v3/v4 bombs: separate list (no more type field)
        [Serializable]
        private sealed class V3Bomb
        {
            public float b;
            public int x;
            public int y;
        }

        // v3/v4 walls: y=0 => full wall, y=2 => crouch wall (v2: _type 0/1)
        [Serializable]
        private sealed class V3Obstacle
        {
            public float b;
            public int x;
            public int y;
            public float d;
            public int w;
            public int h;
        }

        // v3/v4 BPM events (short field names, without underscore prefix): b = beat, m = BPM
        [Serializable]
        private sealed class V3BpmEvent
        {
            public float b;
            public float m;
        }

        // v3/v4 arc (sliders): b/x/y/c/d = head, mu = head control point multiplier,
        // tb/tx/ty/tc = tail, tmu = tail control point multiplier, m = arc mid anchor mode.
        [Serializable]
        private sealed class V3Slider
        {
            public float b;
            public int x;
            public int y;
            public int c;
            public int d;
            public float mu;
            public float tb;
            public int tx;
            public int ty;
            public int tc;
            public float tmu;
            public int m;
        }

        // v3/v4 chain (burstSliders): b/x/y/c/d = head, tb = tail beat, tx/ty = tail position,
        // sc = segment count (head + sc-1 links), s = squish.
        [Serializable]
        private sealed class V3BurstSlider
        {
            public float b;
            public int x;
            public int y;
            public int c;
            public int d;
            public float tb;
            public int tx;
            public int ty;
            public int sc;
            public float s;
        }

        // v2 lightshow events (long field names)
        [Serializable]
        private sealed class V2Event
        {
            public float _time;
            public int _type;
            public int _value;
            public float _floatValue = 1f;
            public V2EventCustomData _customData;
        }

        [Serializable]
        private sealed class V2EventCustomData
        {
            public float[] _color;
            public float[] color;
        }

        // v3/v4 lightshow events (short field names): b=beat, et=type, i=value, f=float
        [Serializable]
        private sealed class V3Event
        {
            public float b;
            // The event TYPE is called "et" in every v3/v4 map (BSMG format), e.g.
            // {"b":4.0,"et":1,"i":1,"f":0.5}. The DTO used to declare "t" only, which no map
            // writes: JsonUtility then left it at 0 for EVERY event, so all v3 lightshows were
            // played as BackLasers (type 0) - no rings, no ring spin/zoom, i.e. "animated
            // environment shows no animations" (reported on the headset). Both spellings
            // are declared now; "et" wins (see ApplyV3Events).
            public int et;
            public int t;
            public int i;
            public float f = 1f;
            public V3EventCustomData customData;
        }

        [Serializable]
        private sealed class V3EventCustomData
        {
            public float[] color;
        }

        // v3/v4 color boost events: b=beat, o=on/off
        [Serializable]
        private sealed class V3BoostEvent
        {
            public float b;
            public bool o;
        }

        [Serializable]
        private sealed class ChartJsonV3
        {
            public string _version = "";
            public string version = "";
            public List<V3Note> _colorNotes = null;
            public List<V3Bomb> _bombNotes = null;
            public List<V3Obstacle> _obstacles = null;
            // Field spelling varies in the wild: "_colorNotes" (editor exports) and
            // "colorNotes" (official v3.0.0). JsonUtility binds only what is present, so both
            // spellings are declared and the non-empty one wins in ParseChartCore - otherwise a
            // chart parses to ZERO notes while the audio plays fine.
            public List<V3Note> colorNotes = null;
            public List<V3Bomb> bombNotes = null;
            public List<V3Obstacle> obstacles = null;
            public ChartCustomData customData = null;
            public List<V3Slider> sliders = null;
            public List<V3BurstSlider> burstSliders = null;
            public List<V3Slider> _sliders = null;
            public List<V3BurstSlider> _burstSliders = null;
            public ChartCustomData _customData = null;
            public List<V3BpmEvent> bpmEvents = null;
            public List<V3Event> basicBeatmapEvents = null;
            public List<V3BoostEvent> colorBoostBeatmapEvents = null;
        }

        /// <summary>First list that actually has entries (key spelling may differ per map).</summary>
        private static List<T> FirstList<T>(List<T> a, List<T> b)
        {
            if (a != null && a.Count > 0) return a;
            if (b != null && b.Count > 0) return b;
            return a ?? b;
        }

        /// <summary>
        /// Scans ALL song folders (GameConfig.SongsRootCandidates: the visible
        /// QuestSaberSongs folder at the storage root first, then the legacy
        /// Download/QuestSaberSongs, the app-own "Songs" and the SAF inbox
        /// "SongsImported"), merges them WITHOUT duplicates and falls back to the demo song.
        /// Prefers the "Standard" characteristic + highest difficulty.
        /// </summary>
        public static List<SongMeta> LoadSongList()
        {
            var result = new List<SongMeta>();
            foreach (var root in GameConfig.SongsRootCandidates)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    ScanSongRoot(root, result);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[QuestSaber] Song scan '{root}' failed: {ex.Message}");
                }
            }

            // The same song can exist in more than one root - an older install imported
            // it into the app-own SongsImported folder, while a fresh import (or the player)
            // put it into the visible QuestSaberSongs folder (at the storage root).
            // Without this the song appeared TWICE in the menu. The roots are scanned in
            // priority order (visible
            // folder FIRST), so the first occurrence wins and the menu lists each song once.
            var unique = new List<SongMeta>(result.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in result)
            {
                string key = string.IsNullOrEmpty(song.FolderName) ? song.SongName : song.FolderName;
                if (string.IsNullOrEmpty(key) || seen.Add(key)) unique.Add(song);
                else Debug.Log("[QuestSaber] Duplicate song skipped: '" + key +
                               "' (already found in a higher-priority folder)");
            }
            result = unique;

            if (result.Count == 0)
            {
                Debug.Log("[QuestSaber] No songs found -> demo song as fallback.");
                result.Add(CreateDemoMeta());
            }

            return result
                .OrderBy(s => s.SongName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Scans ONE song root folder: direct subfolders are songs;
        /// if a folder itself has no info file, one level deeper is searched
        /// (collection folders like "CustomLevels").</summary>
        private static void ScanSongRoot(string root, List<SongMeta> result)
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                try
                {
                    var meta = ParseSongFolder(Path.GetFileName(dir), dir);
                    if (meta != null)
                    {
                        result.Add(meta);
                        continue;
                    }

                    // No info.json/info.dat here: search one level deeper.
                    foreach (var sub in Directory.GetDirectories(dir))
                    {
                        var subMeta = ParseSongFolder(Path.GetFileName(sub), sub);
                        if (subMeta != null) result.Add(subMeta);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[QuestSaber] Song folder '{dir}' skipped: {ex.Message}");
                }
            }
        }

        /// <summary>A song folder with info.json + .dat + audio file.</summary>
        public static SongMeta ParseSongFolder(string folderName, string folderPath)
        {
            // info.json is tried FIRST; info.dat (the classic BeatSaver v2 name) is the fallback.
            string infoPath = Path.Combine(folderPath, "info.json");
            if (!File.Exists(infoPath)) infoPath = Path.Combine(folderPath, "info.dat");
            if (!File.Exists(infoPath))
            {
                var cand = Directory.GetFiles(folderPath)
                    .FirstOrDefault(f => f.EndsWith("info.dat", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith("info.json", StringComparison.OrdinalIgnoreCase));
                if (cand != null) infoPath = cand;
            }
            if (!File.Exists(infoPath)) return null;

            string json = File.ReadAllText(infoPath);
            var info = FromJson<InfoJson>(json);
            if (info == null) throw new InvalidDataException("info.json/info.dat unreadable");

            string audioName = string.IsNullOrEmpty(info._songFilename) ? "song.ogg" : info._songFilename;
            string audioPath = FindAudioFile(folderPath, audioName);

            return BuildMetaFromInfo(folderName, folderPath, info, false, audioName, audioPath, json);
        }

        private static SongMeta BuildMetaFromInfo(string folderName, string folderPath, InfoJson info,
            bool isDemo, string audioName, string audioPath, string rawInfoJson = null)
        {
            // ONLY the Standard characteristic is playable. Special modes like OneSaber
            // ("one-handed"), 360°/90° or lightshow stay completely disabled -
            // songs without a Standard set are skipped.
            var set = info._difficultyBeatmapSets?.FirstOrDefault(s => string.Equals(
                s._beatmapCharacteristicName, "Standard", StringComparison.OrdinalIgnoreCase));
            if (set == null) return null; // no Standard set: skip song

            var diffs = set._difficultyBeatmaps ?? new List<InfoJson.DifficultySet.Difficulty>();
            var ordered = diffs
                .Where(d => !string.IsNullOrEmpty(d._beatmapFilename))
                .OrderBy(d => DifficultyRank(d._difficulty))
                .ToList();
            if (ordered.Count == 0) throw new InvalidDataException("No beatmap files");

            // NJS/spawn offset per difficulty: v2.0 directly on the entry, v2.1 in _customData
            var difficultyList = new List<SongDifficulty>();
            foreach (var d in ordered)
            {
                float njs = d._noteJumpMovementSpeed;
                float beatOffset = d._noteJumpStartBeatOffset;
                if (d._customData != null)
                {
                    if (d._customData._noteJumpMovementSpeed > 0f) njs = d._customData._noteJumpMovementSpeed;
                    if (d._customData._noteJumpStartBeatOffset != 0f) beatOffset = d._customData._noteJumpStartBeatOffset;
                }
                string diffName = string.IsNullOrEmpty(d._difficulty) ? "Normal" : d._difficulty;
                difficultyList.Add(new SongDifficulty
                {
                    Name = diffName,
                    Rank = DifficultyRank(diffName),
                    BeatmapFileName = MapFileNameFor(isDemo, d._beatmapFilename),
                    NoteJumpMovementSpeed = njs,
                    NoteJumpStartBeatOffset = beatOffset,
                });
            }

            int legacyIndex = difficultyList.Count - 1;
            int bestRank = -1;
            for (int i = 0; i < difficultyList.Count; i++)
            {
                bool inFolderName = folderName.IndexOf(difficultyList[i].Name, StringComparison.OrdinalIgnoreCase) >= 0;
                if (inFolderName && difficultyList[i].Rank > bestRank)
                {
                    legacyIndex = i;
                    bestRank = difficultyList[i].Rank;
                }
            }
            // Highscore key: the demo uses DemoKey (not the folder name) so that the
            // old "__demo__" entries still match it (see MigrateLegacyScores).
            string keyBase = isDemo ? SongMeta.DemoKey : folderName;
            for (int i = 0; i < difficultyList.Count; i++)
                difficultyList[i].Key = keyBase + "|" + difficultyList[i].Name;

            var meta = new SongMeta
            {
                FolderName = folderName,
                FolderPath = folderPath,
                SongName = string.IsNullOrEmpty(info._songName) ? folderName : info._songName,
                SongAuthorName = info._songAuthorName ?? "",
                SongFileName = audioName,
                BeatsPerMinute = info._beatsPerMinute > 0 ? info._beatsPerMinute : 120f,
                EnvironmentName = ResolveEnvironmentName(info),
                IsDemo = isDemo,
                AudioPath = audioPath,
                Difficulties = difficultyList,
                LegacyDefaultIndex = legacyIndex,
                SelectedDifficulty = legacyIndex,
            };
            meta.ApplySelectedDifficulty();
            ApplyInfoEnvColors(meta, info);
            // Raw text scan LAST: reads `_envColor*` in BOTH notations
            // (array `[r,g,b]` AND object `{r,g,b}` - ChroMapper writes the latter, e.g.
            // "Radiant"/374e3) and thereby overrides the DTO values.
            ApplyRawEnvColors(meta, rawInfoJson);
            return meta;
        }

        /// <summary>Environment name of the map: v3/v4 `environmentNames` (first entry) or
        /// v2 `_environmentName`; fallback DefaultEnvironment.</summary>
        private static string ResolveEnvironmentName(InfoJson info)
        {
            if (info == null) return "DefaultEnvironment";
            if (info.environmentNames != null && info.environmentNames.Count > 0
                && !string.IsNullOrEmpty(info.environmentNames[0]))
            {
                return info.environmentNames[0];
            }
            if (!string.IsNullOrEmpty(info._environmentName)) return info._environmentName;
            return "DefaultEnvironment";
        }

        private static string MapFileNameFor(bool isDemo, string beatmapFileName)
        {
            if (!isDemo) return beatmapFileName;
            return GameConfig.DemoMapResourcePrefix + Path.GetFileNameWithoutExtension(beatmapFileName);
        }

        private static readonly HashSet<string> MissingFileReports = new();

        /// <summary>
        /// Absolute path of a difficulty file. The name inside info.dat is taken literally -
        /// Android's shared storage is case sensitive, so a map whose file was renamed (BeatSaver,
        /// a file manager, or an older import) would load an empty chart: the audio plays, but not
        /// a single note appears. Reported on the headset ("song comes, but no notes").
        /// Therefore the exact name is tried first, then a case-insensitive scan of the whole song
        /// folder (v4 maps may reference "Sub/File.dat"), and the outcome is logged.
        /// </summary>
        public static string ResolveMapFile(string folderPath, string fileName)
        {
            if (string.IsNullOrEmpty(folderPath)) return fileName ?? "";
            try
            {
                string direct = Path.Combine(folderPath, fileName ?? "");
                if (File.Exists(direct)) return direct;

                string root = folderPath.Replace('\\', '/').TrimEnd('/');
                string want = (fileName ?? "").Replace('\\', '/');
                string[] files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);

                foreach (var f in files)
                {
                    string rel = f.Replace('\\', '/');
                    if (rel.StartsWith(root + "/", StringComparison.Ordinal)) rel = rel.Substring(root.Length + 1);
                    if (string.Equals(rel, want, StringComparison.OrdinalIgnoreCase))
                    {
                        WarnOnce("[QuestSaber] Beatmap file name mismatch: info.dat says '" + fileName +
                                 "', the folder has '" + rel + "' - using the file on disk.");
                        return f;
                    }
                }

                // Nothing matched: list what IS there - that line solves such a report.
                var names = new List<string>();
                for (int i = 0; i < files.Length && i < 24; i++) names.Add(Path.GetFileName(files[i]));
                WarnOnce("[QuestSaber] Beatmap file MISSING: '" + fileName + "' in " + folderPath +
                         " (" + files.Length + " file(s) present: " + string.Join(", ", names) + ")");
                return direct;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Beatmap file lookup failed: " + ex.Message);
                return Path.Combine(folderPath, fileName ?? "");
            }
        }

        /// <summary>One warning per path - a song played twice must not flood the log.</summary>
        private static void WarnOnce(string message)
        {
            if (MissingFileReports.Add(message)) Debug.LogWarning(message);
        }

        private static readonly Dictionary<string, BeatmapChart> ChartCache = new();
        private const int ChartCacheMax = 128;

        /// <summary>Loads the .dat of a song into NoteModel/ObstacleModel lists (v2, v3 and v4)
        /// - with chart cache: already parsed songs/difficulties stay in memory.
        /// For the built-in song, <see cref="SongMeta.MapPath"/> is the Resources name.</summary>
        public static BeatmapChart ParseChart(SongMeta meta)
        {
            if (meta == null) return new BeatmapChart();

            string cacheKey = meta.MapPath;
            if (!string.IsNullOrEmpty(cacheKey) && ChartCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var chart = ParseChartCore(meta);

            if (ChartCache.Count >= ChartCacheMax) ChartCache.Clear();
            if (!string.IsNullOrEmpty(cacheKey)) ChartCache[cacheKey] = chart;
            return chart;
        }

        /// <summary>Actual parsing without cache (v2/v3/v4 into uniform v2 models).</summary>
        private static BeatmapChart ParseChartCore(SongMeta meta)
        {
            var chart = new BeatmapChart();

            string mapJson;
            try
            {
                mapJson = meta.IsDemo
                    ? ReadResourceText(meta.MapPath)
                    : File.ReadAllText(meta.MapPath);
            }
            catch (Exception ex)
            {
                Debug.LogError("[QuestSaber] Beatmap file unreadable: '" + meta.MapPath + "' (" +
                               ex.GetType().Name + ": " + ex.Message + ") - the song has no notes.");
                return chart; // Song still starts, but the log now says why.
            }
            int version = DetectMapVersion(mapJson);

            // Noodle/Heck/Chroma data (Newtonsoft). JsonUtility cannot read dynamic
            // keys and no jagged arrays (point definitions) - therefore a separate,
            // purely defensive pass that simply yields nothing on errors.
            var noodle = NoodleParser.ParseChart(mapJson, version);

            // Chroma extras of the lightshow events (lightID/nameFilter/easing/...), same
            // reason: JsonUtility cannot read `lightID` in BOTH shapes (number and array).
            var lightshowExtras = LightshowExtrasParser.Parse(mapJson, version);

            if (version == 2)
            {
                // v2 legacy: _notes with _type (0=red, 1=blue, 3=bomb) and _cutDirection 0..8
                var parsed = FromJson<ChartJsonV2>(mapJson);
                if (parsed == null) return chart;

                chart._notes = parsed._notes ?? new List<NoteModel>();
                chart._obstacles = parsed._obstacles ?? new List<ObstacleModel>();

                // Read arcs/chains (v2.1). Both are spawned/scored; the v2.1 chains additionally
                // need the same head-link pass as v3/v4, otherwise GameManager.Spawn skips them
                // (`_headNote == null`) and v2.1 chains never spawn.
                chart.Sliders = parsed._sliders ?? new List<ArcModel>();
                chart.Chains = parsed._chains ?? new List<ChainModel>();
                LinkChainHeadNotes(chart);

                // Some maps carry NJS/spawn offset at the beginning of the difficulty .dat
                // (instead of in the info): this overrides the info values.
                if (parsed._customData != null && parsed._customData._noteJumpMovementSpeed > 0f)
                {
                    meta.NoteJumpMovementSpeed = parsed._customData._noteJumpMovementSpeed;
                    meta.NoteJumpStartBeatOffset = parsed._customData._noteJumpStartBeatOffset;
                }
                ApplyChartEnvColors(meta, parsed._customData);

                // BPM progression (v2: _BPMChanges): determines the exact beat->time conversion
                if (parsed._BPMChanges != null && parsed._BPMChanges.Count > 0)
                {
                    chart.BpmEvents = parsed._BPMChanges
                        .Where(c => c._BPM > 0f)
                        .Select(c => new BeatBpmPoint { Beat = c._time, Bpm = c._BPM })
                        .ToList();
                }
                // Put Noodle data onto the models index-aligned (v2: model == DTO).
                AttachNoodle(parsed._notes, noodle.Notes);
                AttachNoodle(parsed._obstacles, noodle.Obstacles);
                AttachNoodle(chart.Sliders, noodle.Sliders);
                AttachNoodle(chart.Chains, noodle.BurstSliders);

                ApplyV2Events(chart, parsed._events, lightshowExtras);
                return chart;
            }

            if (version == 3 || version == 4)
            {
                // v3 and v4 differ only in the key spelling ("_colorNotes" vs "colorNotes").
                // One tolerant parse: every list is accepted with or without the underscore, so a
                // chart can never end up EMPTY just because an editor spelled the keys differently
                // (symptom: the audio plays, but not a single note appears).
                var v3 = FromJson<ChartJsonV3>(mapJson);
                if (v3 == null) return chart;

                ApplyV3Chart(chart, meta,
                    FirstList(v3._colorNotes, v3.colorNotes),
                    FirstList(v3._bombNotes, v3.bombNotes),
                    FirstList(v3._obstacles, v3.obstacles),
                    v3._customData ?? v3.customData, noodle);
                chart.BpmEvents = ToBpmEvents(v3.bpmEvents);
                ApplyV3Sliders(chart, FirstList(v3.sliders, v3._sliders),
                    FirstList(v3.burstSliders, v3._burstSliders), noodle);
                ApplyV3Events(chart, v3.basicBeatmapEvents, v3.colorBoostBeatmapEvents,
                    lightshowExtras);
                return chart;
            }

            var fallback = FromJson<ChartJsonV2>(mapJson);
            if (fallback == null) return chart;
            chart._notes = fallback._notes ?? new List<NoteModel>();
            chart._obstacles = fallback._obstacles ?? new List<ObstacleModel>();
            return chart;
        }

        /// <summary>Detects the beatmap format from the version field or the field names.</summary>
        private static int DetectMapVersion(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return 0;

            // v2/v3 editor exports: "_version". Official v3.0.0+ and v4: "version" (no "_").
            int major = MajorOf(json, "\"_version\"\\s*:\\s*\"(\\d+)");
            if (major != 0) return major;
            major = MajorOf(json, "\"version\"\\s*:\\s*\"(\\d+)");
            if (major != 0) return major;

            // Without a version field: detect from the field names
            if (json.Contains("\"_notes\"")) return 2;
            if (json.Contains("\"_colorNotes\"")) return 3;
            if (json.Contains("\"colorNotes\"")) return 4;
            return 0;
        }

        /// <summary>Version number of the first matching key, or 0 if absent/out of range.</summary>
        private static int MajorOf(string json, string pattern)
        {
            var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int major)
                && major >= 2 && major <= 4)
            {
                return major;
            }
            return 0;
        }

        /// <summary>Fills the chart from v3/v4 data (short field names) - processable just like v2.</summary>
        private static void ApplyV3Chart(
            BeatmapChart chart, SongMeta meta,
            List<V3Note> colorNotes, List<V3Bomb> bombNotes, List<V3Obstacle> obstacles,
            ChartCustomData customData, NoodleParseResult noodle)
        {
            var notes = new List<NoteModel>();
            if (colorNotes != null)
            {
                for (int i = 0; i < colorNotes.Count; i++)
                {
                    var n = colorNotes[i];
                    notes.Add(new NoteModel
                    {
                        _time = n.b,
                        _lineIndex = n.x,
                        _lineLayer = n.y,
                        _type = n.c,          // 0 = red/left, 1 = blue/right
                        _cutDirection = n.d,  // 0..8 (8 = Dot)
                        _angleOffset = n.a,   // v3/v4: additional angle offset in degrees
                        _customData = n.customData, // Noodle (v3/v4)
                        Noodle = At(noodle?.Notes, i),
                    });
                }
            }
            if (bombNotes != null)
            {
                for (int i = 0; i < bombNotes.Count; i++)
                {
                    var bomb = bombNotes[i];
                    notes.Add(new NoteModel
                    {
                        _time = bomb.b,
                        _lineIndex = bomb.x,
                        _lineLayer = bomb.y,
                        _type = 3,           // bomb (v2 convention)
                        _cutDirection = 8,   // arbitrary
                        Noodle = At(noodle?.Bombs, i),
                    });
                }
            }
            notes.Sort((a, b) => a._time.CompareTo(b._time));
            chart._notes = notes;

            var walls = new List<ObstacleModel>();
            if (obstacles != null)
            {
                for (int i = 0; i < obstacles.Count; i++)
                {
                    var o = obstacles[i];
                    walls.Add(new ObstacleModel
                    {
                        _time = o.b,
                        _lineIndex = o.x,
                        _type = o.y >= 2 ? 1 : 0, // y=2 => crouch wall
                        _duration = o.d,
                        _width = Mathf.Max(1, o.w),
                        // Take the vertical position from the map (bottom edge/height in grid heights).
                        _lineLayer = Mathf.Max(0, o.y),
                        _height = Mathf.Max(0, o.h),
                        Noodle = At(noodle?.Obstacles, i),
                    });
                }
            }
            chart._obstacles = walls;

            // NJS can (as with v2) also be at the beginning of the difficulty .dat
            if (customData != null && customData._noteJumpMovementSpeed > 0f)
            {
                meta.NoteJumpMovementSpeed = customData._noteJumpMovementSpeed;
                meta.NoteJumpStartBeatOffset = customData._noteJumpStartBeatOffset;
            }
            ApplyChartEnvColors(meta, customData);
        }

        /// <summary>
        /// Reads v3/v4 arcs (sliders) and chains (burstSliders) into the uniform
        /// ArcModel/ChainModel and links the chain head note: in v3/v4 the head is a
        /// real colorNote at the same beat/cell/color (ArcViewer `NoteManager.CheckChainHead`).
        /// If it is missing (broken map), the head stays unlinked - the chain is then not
        /// spawned (no crash).
        /// </summary>
        private static void ApplyV3Sliders(
            BeatmapChart chart, List<V3Slider> sliders, List<V3BurstSlider> burstSliders,
            NoodleParseResult noodle)
        {
            if (sliders != null)
            {
                chart.Sliders = new List<ArcModel>(sliders.Count);
                for (int i = 0; i < sliders.Count; i++)
                {
                    var s = sliders[i];
                    chart.Sliders.Add(new ArcModel
                    {
                        _headTime = s.b,
                        _headLineIndex = s.x,
                        _headLineLayer = s.y,
                        _headColorType = s.c,
                        _headCutDirection = s.d,
                        _headControlPointLengthMultiplier = s.mu,
                        _tailTime = s.tb,
                        _tailLineIndex = s.tx,
                        _tailLineLayer = s.ty,
                        _tailCutDirection = s.tc,
                        _tailControlPointLengthMultiplier = s.tmu,
                        Noodle = At(noodle?.Sliders, i),
                    });
                }
            }

            if (burstSliders != null)
            {
                chart.Chains = new List<ChainModel>(burstSliders.Count);
                for (int i = 0; i < burstSliders.Count; i++)
                {
                    var b = burstSliders[i];
                    chart.Chains.Add(new ChainModel
                    {
                        _time = b.b,
                        _lineIndex = b.x,
                        _lineLayer = b.y,
                        _colorType = b.c,
                        _cutDirection = b.d,
                        _tailTime = b.tb,
                        _tailLineIndex = b.tx,
                        _tailLineLayer = b.ty,
                        _sliceCount = b.sc,
                        _squishAmount = b.s,
                        Noodle = At(noodle?.BurstSliders, i),
                    });
                }
            }

            LinkChainHeadNotes(chart);
        }

        /// <summary>
        /// Marks the head note of every chain: the head exists as a separate colorNote at the
        /// same beat/cell/color (ArcViewer `NoteManager.CheckChainHead`). v3/v4 and v2.1
        /// ("_chains") both need this pass - without it ChainModel._headNote stays null and
        /// GameManager.Spawn skips the chain (so v2.1 chains never spawned).
        /// </summary>
        private static void LinkChainHeadNotes(BeatmapChart chart)
        {
            if (chart == null || chart._notes == null || chart.Chains == null) return;
            bool addedSyntheticHead = false;
            foreach (var chain in chart.Chains)
            {
                NoteModel head = null;
                foreach (var note in chart._notes)
                {
                    if (note._isChainHead) continue;
                    if (Mathf.Abs(note._time - chain._time) > 0.001f) continue;
                    if (note._lineIndex != chain._lineIndex || note._lineLayer != chain._lineLayer) continue;
                    if (note._type != chain._colorType) continue;
                    head = note;
                    break;
                }
                if (head != null)
                {
                    head._isChainHead = true;
                    chain._headNote = head;
                }
                else
                {
                    head = new NoteModel
                    {
                        _time = chain._time,
                        _lineIndex = chain._lineIndex,
                        _lineLayer = chain._lineLayer,
                        _type = chain._colorType,
                        _cutDirection = (int)chain._cutDirection,
                        _isChainHead = true,
                    };
                    chart._notes.Add(head);
                    chain._headNote = head;
                    addedSyntheticHead = true;
                }
            }
            if (addedSyntheticHead)
            {
                chart._notes.Sort((a, b) => a._time.CompareTo(b._time));
            }
        }

        /// <summary>Null-safe index access to a Noodle object array.</summary>
        private static NoodleObjectData At(NoodleObjectData[] arr, int i) =>
            arr != null && i >= 0 && i < arr.Length ? arr[i] : null;

        // Put Noodle data onto a model list index-aligned (v2 path, where model == DTO).
        private static void AttachNoodle(List<NoteModel> models, NoodleObjectData[] data)
        {
            if (models == null || data == null) return;
            int n = Mathf.Min(models.Count, data.Length);
            for (int i = 0; i < n; i++) models[i].Noodle = data[i];
        }

        private static void AttachNoodle(List<ObstacleModel> models, NoodleObjectData[] data)
        {
            if (models == null || data == null) return;
            int n = Mathf.Min(models.Count, data.Length);
            for (int i = 0; i < n; i++) models[i].Noodle = data[i];
        }

        private static void AttachNoodle(List<ArcModel> models, NoodleObjectData[] data)
        {
            if (models == null || data == null) return;
            int n = Mathf.Min(models.Count, data.Length);
            for (int i = 0; i < n; i++) models[i].Noodle = data[i];
        }

        private static void AttachNoodle(List<ChainModel> models, NoodleObjectData[] data)
        {
            if (models == null || data == null) return;
            int n = Mathf.Min(models.Count, data.Length);
            for (int i = 0; i < n; i++) models[i].Noodle = data[i];
        }

        /// <summary>
        /// Converts a v2 time (beats) into game time seconds. This is only the case for
        /// ONE constant BPM (no events in <see cref="BuildBeatToSeconds"/>); the
        /// beat times of the maps are beats, not seconds.
        /// </summary>
        public static float BeatsToSeconds(float beats, float bpm)
        {
            float safeBpm = bpm > 0 ? bpm : 60f;
            return beats * (60f / safeBpm);
        }

        /// <summary>Transfers lightshow events (v2) into uniform models.</summary>
        private static void ApplyV2Events(BeatmapChart chart, List<V2Event> events,
            List<LightEventExtras> extras)
        {
            if (events == null) return;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                var model = new LightEventModel
                {
                    _time = e._time,
                    _type = e._type,
                    _value = e._value,
                    _floatValue = e._floatValue,
                    Extras = ExtrasAt(extras, i),
                };
                float[] col = null;
                if (e._customData != null)
                    col = e._customData.color ?? e._customData._color;
                if (TryColor(col, out Color c)) { model._hasCustomColor = true; model._customColor = c; }
                chart.LightEvents.Add(model);
            }
        }

        /// <summary>Transfers lightshow events (v3/v4) + color boost into uniform models.</summary>
        private static void ApplyV3Events(BeatmapChart chart,
            List<V3Event> events, List<V3BoostEvent> boostEvents,
            List<LightEventExtras> extras)
        {
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    // "et" is the real field name; "t" is only a safety net for a hypothetical
                    // map that used the old (wrong) spelling - et==0 is a VALID type (BackLasers),
                    // so it is only preferred when it is non-zero or "t" is absent.
                    int type = (e.et != 0 || e.t == 0) ? e.et : e.t;
                    var model = new LightEventModel
                    {
                        _time = e.b,
                        _type = type,
                        _value = e.i,
                        _floatValue = e.f,
                        Extras = ExtrasAt(extras, i),
                    };
                    if (e.customData != null && TryColor(e.customData.color, out Color c))
                    {
                        model._hasCustomColor = true;
                        model._customColor = c;
                    }
                    chart.LightEvents.Add(model);
                }
            }
            if (boostEvents != null)
            {
                foreach (var b in boostEvents)
                {
                    chart.BoostEvents.Add(new ColorBoostModel { _time = b.b, _boost = b.o });
                }
            }
        }

        /// <summary>Extras of event `index` (null when the map has none / the list is short).</summary>
        private static LightEventExtras ExtrasAt(List<LightEventExtras> extras, int index)
        {
            return extras != null && index >= 0 && index < extras.Count ? extras[index] : null;
        }

        /// <summary>Chroma/env color array [r,g,b(,a)] -> Color.</summary>
        private static bool TryColor(float[] a, out Color c)
        {
            c = Color.white;
            if (a == null || a.Length < 3) return false;
            c = new Color(a[0], a[1], a[2], a.Length > 3 ? a[3] : 1f);
            return true;
        }

        /// <summary>Reads environment colors robustly from the RAW info JSON - accepts the
        /// array form (`[r,g,b]`) AND the object form (`{r,g,b}`, ChroMapper style, e.g.
        /// "Radiant"/374e3). Takes the first occurrence per key (maps set the colors
        /// identically per difficulty).</summary>
        private static void ApplyRawEnvColors(SongMeta meta, string json)
        {
            if (meta == null || string.IsNullOrEmpty(json)) return;
            if (TryRawColor(json, "_envColorLeft", out var cl)) { meta.EnvColorLeft = cl; meta.HasEnvironmentColors = true; }
            if (TryRawColor(json, "_envColorRight", out var cr)) { meta.EnvColorRight = cr; meta.HasEnvironmentColors = true; }
            if (TryRawColor(json, "_envColorLeftBoost", out var lb)) { meta.EnvColorLeftBoost = lb; meta.HasEnvironmentColors = true; }
            if (TryRawColor(json, "_envColorRightBoost", out var rb)) { meta.EnvColorRightBoost = rb; meta.HasEnvironmentColors = true; }
        }

        private static bool TryRawColor(string json, string key, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(json)) return false;
            string k = "\"" + key + "\""; // quotes: does NOT match "_envColorLeftBoost"
            int i = json.IndexOf(k, StringComparison.Ordinal);
            if (i < 0) return false;
            int j = i + k.Length;
            int arr = json.IndexOf('[', j);
            int obj = json.IndexOf('{', j);
            if (arr < 0 && obj < 0) return false;
            bool useArray = arr >= 0 && (obj < 0 || arr < obj);
            string tail = json.Substring(j);
            Match m = useArray
                ? Regex.Match(tail, @"^\s*:\s*\[\s*(-?[0-9.eE+]+)\s*,\s*(-?[0-9.eE+]+)\s*,\s*(-?[0-9.eE+]+)")
                : Regex.Match(tail, @"^\s*:\s*\{\s*""r""\s*:\s*(-?[0-9.eE+]+)\s*,\s*""g""\s*:\s*(-?[0-9.eE+]+)\s*,\s*""b""\s*:\s*(-?[0-9.eE+]+)");
            if (!m.Success) return false;
            if (!float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) return false;
            if (!float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float g)) return false;
            if (!float.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float b)) return false;
            c = new Color(r, g, b, 1f);
            return true;
        }

        private static bool TryHex(string hex, out Color c)
        {
            c = Color.white;
            return !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out c);
        }

        /// <summary>Map environment colors from the info (v2/v3 `_customData` or v4 hex).</summary>
        private static void ApplyInfoEnvColors(SongMeta meta, InfoJson info)
        {
            if (info == null) return;
            var cd = info._customData;
            if (cd != null)
            {
                if (TryColor(cd._envColorLeft, out var cl)) { meta.EnvColorLeft = cl; meta.HasEnvironmentColors = true; }
                if (TryColor(cd._envColorRight, out var cr)) { meta.EnvColorRight = cr; meta.HasEnvironmentColors = true; }
                if (TryColor(cd._envColorLeftBoost, out var lb)) { meta.EnvColorLeftBoost = lb; meta.HasEnvironmentColors = true; }
                if (TryColor(cd._envColorRightBoost, out var rb)) { meta.EnvColorRightBoost = rb; meta.HasEnvironmentColors = true; }
            }
            // v4: hex strings (`environmentColor0/1` + boost).
            if (TryHex(info.environmentColor0, out var h0)) { meta.EnvColorLeft = h0; meta.HasEnvironmentColors = true; }
            if (TryHex(info.environmentColor1, out var h1)) { meta.EnvColorRight = h1; meta.HasEnvironmentColors = true; }
            if (TryHex(info.environmentColor0Boost, out var hb0)) { meta.EnvColorLeftBoost = hb0; meta.HasEnvironmentColors = true; }
            if (TryHex(info.environmentColor1Boost, out var hb1)) { meta.EnvColorRightBoost = hb1; meta.HasEnvironmentColors = true; }
        }

        /// <summary>Map environment colors from the difficulty _customData (override the info).</summary>
        private static void ApplyChartEnvColors(SongMeta meta, ChartCustomData cd)
        {
            if (meta == null || cd == null) return;
            if (TryColor(cd._envColorLeft, out var cl)) { meta.EnvColorLeft = cl; meta.HasEnvironmentColors = true; }
            if (TryColor(cd._envColorRight, out var cr)) { meta.EnvColorRight = cr; meta.HasEnvironmentColors = true; }
            if (TryColor(cd._envColorLeftBoost, out var lb)) { meta.EnvColorLeftBoost = lb; meta.HasEnvironmentColors = true; }
            if (TryColor(cd._envColorRightBoost, out var rb)) { meta.EnvColorRightBoost = rb; meta.HasEnvironmentColors = true; }
        }

        private static List<BeatBpmPoint> ToBpmEvents(List<V3BpmEvent> events)
        {
            var result = new List<BeatBpmPoint>();
            if (events == null) return result;
            foreach (var e in events)
            {
                if (e.m > 0f) result.Add(new BeatBpmPoint { Beat = e.b, Bpm = e.m });
            }
            return result;
        }

        public static Func<float, float> BuildBeatToSeconds(
            List<BeatBpmPoint> events, float defaultBpm)
        {
            float baseBpm = defaultBpm > 0 ? defaultBpm : 60f;
            var pts = new List<BeatBpmPoint>();
            if (events != null)
            {
                foreach (var p in events)
                {
                    if (p.Bpm > 0f && p.Beat > 0f) pts.Add(p);
                }
            }
            pts.Sort((a, b) => a.Beat.CompareTo(b.Beat));
            if (pts.Count == 0)
            {
                return beats => BeatsToSeconds(beats, baseBpm); // constant (previous behavior)
            }

            // Precompute the cumulative seconds at each change point
            float spb = 60f / baseBpm;
            var cum = new float[pts.Count];
            float prevBeat = 0f;
            float prevSpb = spb;
            for (int i = 0; i < pts.Count; i++)
            {
                cum[i] = (i > 0 ? cum[i - 1] : 0f) + (pts[i].Beat - prevBeat) * prevSpb;
                prevBeat = pts[i].Beat;
                prevSpb = 60f / pts[i].Bpm;
            }

            return beats =>
            {
                if (beats <= pts[0].Beat) return beats * spb;
                // Find the segment with a beat <= beats (binary, list is sorted)
                int lo = 0, hi = pts.Count - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (pts[mid].Beat <= beats) lo = mid;
                    else hi = mid - 1;
                }
                return cum[lo] + (beats - pts[lo].Beat) * (60f / pts[lo].Bpm);
            };
        }

        public static SongMeta CreateDemoMeta()
        {
            var asset = Resources.Load<TextAsset>(GameConfig.DemoInfoResource);
            if (asset != null)
            {
                var info = FromJson<InfoJson>(asset.text);
                if (info != null)
                {
                    var meta = BuildMetaFromInfo(GameConfig.DemoFolderName, "", info, true,
                        GameConfig.DemoTrackFileName, "", asset.text);
                    // null = info without "Standard" characteristic (then the song is not playable).
                    if (meta != null) return meta;
                }
                Debug.LogWarning("[QuestSaber] Demo info.json unreadable/without Standard difficulties"
                                 + " -> emergency meta (only 'Expert').");
            }
            else
            {
                Debug.LogError($"[QuestSaber] Demo info missing in Resources: {GameConfig.DemoInfoResource}");
            }

            // Emergency meta: a single difficulty so the game starts without a crash
            // (the map is then loaded as usual - if that is missing too, the chart stays empty).
            var fallback = new SongMeta
            {
                FolderName = GameConfig.DemoFolderName,
                FolderPath = "",
                SongName = GameConfig.DemoSongName,
                SongAuthorName = GameConfig.DemoSongAuthor,
                SongFileName = GameConfig.DemoTrackFileName,
                BeatsPerMinute = GameConfig.DemoBpm,
                IsDemo = true,
                AudioPath = "",
            };
            fallback.Difficulties.Add(new SongDifficulty
            {
                Name = GameConfig.DemoDifficultyName,
                Rank = DifficultyRank(GameConfig.DemoDifficultyName),
                BeatmapFileName = GameConfig.DemoMapResourcePrefix + GameConfig.DemoDifficultyName,
                Key = SongMeta.DemoKey + "|" + GameConfig.DemoDifficultyName,
            });
            fallback.LegacyDefaultIndex = 0;
            fallback.SelectedDifficulty = 0;
            fallback.ApplySelectedDifficulty();
            return fallback;
        }

        // ---------- Helper functions ----------

        private static int DifficultyRank(string difficulty)
        {
            switch ((difficulty ?? "").ToLowerInvariant())
            {
                case "easy": return 1;
                case "normal": return 2;
                case "hard": return 3;
                case "expert": return 4;
                case "expertplus": return 5;
                default: return 0;
            }
        }

        private static string FindAudioFile(string folderPath, string preferredName)
        {
            // Prefer exact, then first audio file (.egg = OGG content, BeatSaver standard)
            if (!string.IsNullOrEmpty(preferredName))
            {
                string exact = Path.Combine(folderPath, preferredName);
                if (File.Exists(exact)) return exact;
            }

            var candidates = Directory.GetFiles(folderPath, "*.*")
                .Where(f => f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".egg", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return candidates.Count > 0 ? candidates[0] : null;
        }

        /// <summary>Loads a text asset (JSON) from Resources - built-in song.</summary>
        private static string ReadResourceText(string resourceName)
        {
            var ta = Resources.Load<TextAsset>(resourceName);
            if (ta == null) throw new FileNotFoundException("Resource missing in Resources: " + resourceName);
            return ta.text;
        }

        private static T FromJson<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default;
            try { return JsonUtility.FromJson<T>(json); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestSaber] JSON error: {ex.Message}");
                return default;
            }
        }
    }
}

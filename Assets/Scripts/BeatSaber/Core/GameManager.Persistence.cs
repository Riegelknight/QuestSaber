// GameManager: Saving and loading (save, highscores).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace QuestSaber
{
    public sealed partial class GameManager
    {
        // ======================================================================
        // Persistence
        // ======================================================================
        private void LoadSave()
        {
            try
            {
                if (File.Exists(GameConfig.ProfilesPath))
                {
                    var json = File.ReadAllText(GameConfig.ProfilesPath);
                    _save = JsonUtility.FromJson<SaveData>(json) ?? new SaveData();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Loading save failed: " + ex.Message);
            }

            _save.Players ??= new List<PlayerProfile>();
            _save.DifficultyChoices ??= new List<SongDiffChoice>();

            foreach (var p in _save.Players)
            {
                bool oldHeight = p.HeightCm >= 100f && p.HeightCm <= 250f;
                if (oldHeight && Mathf.Approximately(p.Scale, 1f))
                    p.Scale = GameConfig.HeightCmToScale(p.HeightCm);
                if (oldHeight) p.HeightCm = 0f;
                p.Scale = Mathf.Clamp(p.Scale, GameConfig.ScaleMin, GameConfig.ScaleMax);
                // Run-and-Play "run speed": old saves without the field have 0 -> default 1 (real
                // lock), otherwise clamp to the valid range.
                if (p.RunComfort <= 0.01f) p.RunComfort = GameConfig.RunComfortDefault;
                p.RunComfort = Mathf.Clamp(p.RunComfort, GameConfig.RunComfortMin, GameConfig.RunComfortMax);
            }
            ApplyFieldHeight();
            if (_save.PassthroughDisabled && _save.BackgroundMode == 0)
                _save.BackgroundMode = (int)Environment.BackgroundMode.Default;
            _save.BackgroundMode = Mathf.Clamp(_save.BackgroundMode, 0, 2);
            _save.PassthroughDisabled = false;
            // Run and Play forces room following even after a restart.
            if (_save.RunAndPlay) _save.RoomFollowsPlayer = true;
        }

        /// <summary>Apply the saved difficulty choice per song to the songs (after the song scan).</summary>
        private void RestoreDifficultyChoices()
        {
            foreach (var choice in _save.DifficultyChoices)
            {
                var song = _songs.FirstOrDefault(s => s.Key == choice.SongKey);
                if (song == null) continue;
                for (int i = 0; i < song.Difficulties.Count; i++)
                {
                    if (string.Equals(song.Difficulties[i].Name, choice.Difficulty,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        song.SelectedDifficulty = i;
                        break;
                    }
                }
            }
        }

        /// <summary>Save the current difficulty choice of the song globally (stays active after a restart).</summary>
        private void SaveDifficultyChoice()
        {
            if (_current == null || _current.CurrentDifficulty == null) return;
            _save.DifficultyChoices.RemoveAll(c => c.SongKey == _current.Key);
            _save.DifficultyChoices.Add(new SongDiffChoice
            {
                SongKey = _current.Key,
                Difficulty = _current.CurrentDifficulty.Name
            });
            Save();
        }

        private void MigrateLegacyScores()
        {
            bool changed = false;
            foreach (var p in _save.Players)
            {
                for (int i = 0; i < p.BestScores.Count; i++)
                {
                    var r = p.BestScores[i];
                    if (r.Key.Contains('|')) continue;

                    string newKey = null;
                    if (r.Key == SongMeta.DemoKey)
                    {
                        newKey = SongMeta.DemoKey + "|" + GameConfig.DemoDifficultyName;
                    }
                    else
                    {
                        var song = _songs.FirstOrDefault(s => s.Key == r.Key);
                        if (song != null && song.Difficulties.Count > 0)
                        {
                            var d = song.Difficulties[song.LegacyDefaultIndex];
                            newKey = song.Key + "|" + d.Name;
                        }
                    }
                    if (newKey != null)
                    {
                        p.BestScores[i] = new ScoreRecord { Key = newKey, Score = r.Score };
                        changed = true;
                    }
                }
            }
            if (changed) Save();
        }

        /// <summary>
        /// One-time migration: the former hard audio calibration value -85 ms is
        /// now baked into the note clock (<see cref="GameConfig.AudioLatencyBaseMs"/> =
        /// 275 ms) and the slider starts at 0. A saved old value would
        /// compensate twice - therefore it is pulled to 0 once (only as long as the migration
        /// has not yet run, afterwards a fine correction is preserved).
        /// </summary>
        private void MigrateAudioOffset()
        {
            if (_save.SoundDelayBakedIn) return;
            _save.SoundDelayBakedIn = true;
            _save.SoundDelayMs = 0f;
            if (_audio != null) _audio.SetAudioOffset(0f);
            Save();
        }

        private void EnsurePlayer()
        {
            if (_save.Players.Count == 0)
            {
                _save.Players.Add(new PlayerProfile { Name = "Player 1" });
            }
            PlayerProfile selected = null;
            foreach (var p in _save.Players)
            {
                if (p.Name == _save.SelectedPlayer) { selected = p; break; }
            }
            if (selected == null)
            {
                selected = _save.Players[0];
                _save.SelectedPlayer = selected.Name;
            }
            _player = selected;
            ApplyRoomScale();
            Save();
        }

        /// <summary>
        /// Request persistence (values are in the in-memory state IMMEDIATELY). The actual
        /// file write is batched (max. 1x per second, see SaveThrottleSeconds) -
        /// this avoids main-thread disk I/O on every record cut. Pending changes
        /// are reliably flushed at song end (OnSongEnded) and on quit.
        /// </summary>
        private void Save()
        {
            _savePending = true;
            if (Time.unscaledTime - _lastSaveTime < SaveThrottleSeconds) return;
            FlushSave();
        }

        /// <summary>Write save data to disk NOW (regardless of the throttle).</summary>
        private void FlushSave()
        {
            _savePending = false;
            _lastSaveTime = Time.unscaledTime;
            try
            {
                if (!_saveDirReady)
                {
                    Directory.CreateDirectory(GameConfig.SaveRootPath);
                    _saveDirReady = true;
                }
                File.WriteAllText(GameConfig.ProfilesPath, JsonUtility.ToJson(_save, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Writing save failed: " + ex.Message);
            }
        }

    }
}

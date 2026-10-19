// GameManager: Song flow, scheduler, spawning of notes, walls, arcs.
using System.Collections.Generic;
using UnityEngine;

namespace QuestSaber
{
    public sealed partial class GameManager
    {
        // ======================================================================
        // Gameplay flow: song, scheduler, spawn
        // ======================================================================
        /// <summary>Shuffles the song list after reading (Fisher-Yates) - random order at start.</summary>
        private void ShuffleSongsAtStart()
        {
            for (int i = _songs.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                var tmp = _songs[i];
                _songs[i] = _songs[j];
                _songs[j] = tmp;
            }
            _songIndex = -1;
        }

        private void LoadSong(int index)
        {
            if (_songs.Count == 0) return;
            if (_savePending) FlushSave(); // guaranteed: save pending (batched) changes before song change
            _songIndex = ((index % _songs.Count) + _songs.Count) % _songs.Count;
            _current = _songs[_songIndex];

            // Reset state
            _score = 0; _combo = 0; _multTier = 0; _comboProgress = 0; _songEnded = false;
            _noteCursor = 0; _wallCursor = 0; _arcCursor = 0; _chainCursor = 0;
            _runAndPlay.Reset(); // Run-and-Play: fresh measurement from the song's first frame
            _noteSchedule.Clear(); _wallSchedule.Clear(); _arcSchedule.Clear(); _chainSchedule.Clear();

            foreach (var w in _activeWalls) if (w != null) Destroy(w.gameObject);
            _activeWalls.Clear();
            NotePool.ReturnAll(); // Dispose of old notes (including sliced ones) immediately - otherwise they fly into the new song
            _activeNotes.Clear();
            ArcView.ReturnAll(_gameRoot);   // likewise do not carry arcs/chains into the new song
            ChainView.ReturnAll(_gameRoot);
            _activeArcs.Clear();
            _activeChains.Clear();

            _current.ApplySelectedDifficulty();
            _chart = BeatmapParser.ParseChart(_current);
            // Exact beat->time conversion (BPM curve of the map; without events = info BPM)
            _toSeconds = BeatmapParser.BuildBeatToSeconds(
                _chart.BpmEvents, _current != null ? _current.BeatsPerMinute : 120f);
            // Pass the map's lightshow to the environment (only "Animated" mode).
            if (_environment != null)
            {
                _environment.SetEnvironment(_current?.EnvironmentName, _toSeconds);
                _environment.LoadLightshow(_chart, _current, _toSeconds);
            }
            ComputeJumpParameters();
            BuildSchedule();

            // Conclusive line for "song plays, but no notes": name, difficulty, map file, counts.
            if (_current != null)
            {
                string diffName = _current.CurrentDifficulty != null ? _current.CurrentDifficulty.Name : "-";
                DebugLogFile.Mark("song load: '" + _current.SongName + "' diff=" + diffName +
                                  " map='" + _current.MapFileName + "' notes=" + _chart._notes.Count +
                                  " obstacles=" + _chart._obstacles.Count +
                                  " scheduled=" + _noteSchedule.Count);
            }

            Phase = GamePhase.Boot;
            if (_audioLoad != null) StopCoroutine(_audioLoad);
            _audioLoad = StartCoroutine(_audio.PlaySong(_current, () =>
            {
                _audioLoad = null;
                Phase = GamePhase.Playing;
                _songEnded = false;
                UpdateHud();
            }));
        }

        private void ComputeJumpParameters()
        {
            float bpm = _current != null && _current.BeatsPerMinute > 0 ? _current.BeatsPerMinute : 120f;
            float spb = 60f / bpm;
            float njs = _current != null ? _current.NoteJumpMovementSpeed : 0f;
            if (njs <= 0.01f)
            {
                int rank = _current != null && _current.CurrentDifficulty != null
                    ? _current.CurrentDifficulty.Rank : 2;
                njs = GameConfig.DefaultNjsForRank(rank);
            }
            float offset = _current != null ? _current.NoteJumpStartBeatOffset : 0f;

            float hjd = GameConfig.HalfJumpDurationDefault;
            while (njs * 2f * spb * hjd > GameConfig.MaxJumpDistance) hjd *= 0.5f;
            hjd = Mathf.Max(hjd + offset, GameConfig.MinHalfJumpDuration);

            _njs = njs;
            _reactionTime = spb * hjd;              // ArcViewer: 60/BPM x HJD
            _halfJumpDistance = njs * _reactionTime;
            _spawnLeadTime = GameConfig.SpawnLeadTime(_reactionTime);
        }

        private void BuildSchedule()
        {
            if (_chart == null) return;
            var toSeconds = _toSeconds ?? (beats => BeatmapParser.BeatsToSeconds(beats, 120f));

            // Precompute pattern-dependent position effects once (stack/flip).
            var byBeat = new Dictionary<float, List<NoteModel>>();
            foreach (var note in _chart._notes)
            {
                if (!byBeat.TryGetValue(note._time, out var group))
                {
                    group = new List<NoteModel>(4);
                    byBeat[note._time] = group;
                }
                group.Add(note);
            }
            var effects = new Dictionary<NoteModel, (float startYRel, float flipStartX, float flipYHeight)>(_chart._notes.Count);
            BuildNoteEffects(byBeat, effects);

            foreach (var note in _chart._notes)
            {
                float t = toSeconds(note._time);
                if (t < 0f) continue;
                // Spawn time = beat time minus full jump duration (2 x reactionTime);
                // notes right at the song start spawn correspondingly closer.
                effects.TryGetValue(note, out var fx);
                _noteSchedule.Add((Mathf.Max(0f, t - _spawnLeadTime), note,
                    fx.startYRel, fx.flipStartX, fx.flipYHeight));
            }
            _noteSchedule.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            foreach (var obs in _chart._obstacles)
            {
                float t = toSeconds(obs._time);
                if (t < 0f) continue;
                // Resolve duration via the BPM curve (switches in the middle of the wall -> exact)
                float dur = toSeconds(obs._time + obs._duration) - t;
                _wallSchedule.Add((Mathf.Max(0f, t - _spawnLeadTime), obs, dur));
            }
            _wallSchedule.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            // Arcs (sliders): purely visual, spawn like notes/walls 2x reactionTime before the head.
            // Attachment detection (ArcViewer ObjectManager): For each arc end, check whether
            // a note with the SAME time and the SAME grid cell exists. Only then does
            // ArcView shift the point by ArcAttachOffset (arc ends at the note edge).
            var noteAt = new Dictionary<long, NoteModel>(_chart._notes.Count);
            foreach (var n in _chart._notes)
                noteAt[NoteGridKey(n._time, n._lineIndex, n._lineLayer)] = n;
            foreach (var arc in _chart.Sliders)
            {
                float t = toSeconds(arc._headTime);
                if (t < 0f) continue;
                // Link head note (double role): It determines the attachment offset AND
                // the saber attraction - if it is cut, the saber becomes the new
                // start of the arc (original `SliderController.SetSaberAttraction`).
                if (noteAt.TryGetValue(
                        NoteGridKey(arc._headTime, arc._headLineIndex, arc._headLineLayer),
                        out var headNote))
                {
                    arc._headAttached = true;
                    arc._headNote = headNote;
                    headNote._arcHeadOf = arc;
                }
                else
                {
                    arc._headAttached = false;
                }
                arc._tailAttached = noteAt.ContainsKey(
                    NoteGridKey(arc._tailTime, arc._tailLineIndex, arc._tailLineLayer));
                _arcSchedule.Add((Mathf.Max(0f, t - _spawnLeadTime), arc));
            }
            _arcSchedule.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            // Chains (burstSliders): visible links between head and tail.
            foreach (var chain in _chart.Chains)
            {
                float t = toSeconds(chain._time);
                if (t < 0f) continue;
                _chainSchedule.Add((Mathf.Max(0f, t - _spawnLeadTime), chain));
            }
            _chainSchedule.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        }

        private static long NoteGridKey(float beat, int line, int layer)
            => (long)Mathf.Round(beat * 1000f) * 10000L + line * 100L + layer;

        /// <summary>
        /// Computes the pattern-dependent position effects per note (1:1 ArcViewer
        /// ObjectManager/NOTE-MANAGER):
        ///  - STACK: Only when several notes lie in the SAME column at the same time
        ///    does the note get an offset start height (`StackIndex x StackSpacing`
        ///    below the container plane) and slide into its row. Normal notes
        ///    (column occupied once) start on their target row - no effect.
        ///  - FLIP: Exactly two color notes at the same time and crossed (red right of
        ///    blue) => start X = position of the respective other note and the vertical
        ///    over/under arc from ArcViewer (`FlipYOver`/`FlipYUnder`).
        /// </summary>
        private void BuildNoteEffects(
            Dictionary<float, List<NoteModel>> byBeat,
            Dictionary<NoteModel, (float startYRel, float flipStartX, float flipYHeight)> result)
        {
            foreach (var group in byBeat.Values)
            {
                // Column occupancy at the same time (for "is this a stack?").
                int[] perColumn = new int[4];
                foreach (var n in group)
                    perColumn[Mathf.Clamp(n._lineIndex, 0, 3)]++;

                foreach (var n in group)
                {
                    int row = Mathf.Clamp(n._lineLayer, 0, GameConfig.RowHeights.Length - 1);
                    // Start height RELATIVE to the target row (field-height independent).
                    float startYRel = 0f;
                    if (perColumn[Mathf.Clamp(n._lineIndex, 0, 3)] > 1)
                    {
                        int index = StackIndex(n, group);
                        // Stack animation: start one grid unit below the lowest note row
                        // and slide into your own target row.
                        float staggeredRel = GameConfig.RowHeights[0]
                                             + index * GameConfig.StackSpacing - GameConfig.StackSpacing
                                             - GameConfig.RowHeights[row];
                        // Never start above your own target row (safety net for
                        // unusual patterns).
                        startYRel = Mathf.Min(0f, staggeredRel);
                    }
                    result[n] = (startYRel, 0f, 0f);
                }

                // Side flip: exactly two color notes on this beat (bombs do not count).
                var colorNotes = new List<NoteModel>(2);
                foreach (var n in group)
                    if (n._type == 0 || n._type == 1) colorNotes.Add(n);
                if (colorNotes.Count != 2) continue;

                // Deterministic order: left before right (ArcViewer uses the
                // file order; the result is identical for equal orders).
                colorNotes.Sort((a, b) => a._lineIndex.CompareTo(b._lineIndex));
                NoteModel first = colorNotes[0], second = colorNotes[1];

                float f0 = 0f, f1 = 0f;
                if (first._type == second._type)
                {
                    // same color -> no crossing
                }
                else if ((first._type == 0 && first._lineIndex <= second._lineIndex)
                      || (first._type == 1 && first._lineIndex >= second._lineIndex))
                {
                    // no crossing (red left / blue right)
                }
                else if (first._type == 0)
                {
                    if (first._lineLayer < second._lineLayer) { f0 = GameConfig.FlipYUnder; f1 = GameConfig.FlipYOver; }
                    else { f0 = GameConfig.FlipYOver; f1 = GameConfig.FlipYUnder; }
                }
                else
                {
                    if (first._lineLayer > second._lineLayer) { f0 = GameConfig.FlipYOver; f1 = GameConfig.FlipYUnder; }
                    else { f0 = GameConfig.FlipYUnder; f1 = GameConfig.FlipYOver; }
                }

                if (Mathf.Approximately(f0, 0f) && Mathf.Approximately(f1, 0f)) continue;

                // Start X = position of the other note (ArcViewer `FlipStartX`).
                float x0 = GameConfig.GridPosition(first._lineIndex, first._lineLayer).x;
                float x1 = GameConfig.GridPosition(second._lineIndex, second._lineLayer).x;
                var e0 = result[first];
                var e1 = result[second];
                e0.flipStartX = x1; e0.flipYHeight = f0;
                e1.flipStartX = x0; e1.flipYHeight = f1;
                result[first] = e0;
                result[second] = e1;
            }
        }

        /// <summary>
        /// Stack index of a note: 0 if no notes lie directly below it (same
        /// column, lower row, same time); otherwise recursively 1 + largest index below.
        /// Source: ArcViewer `NoteManager.GetStartY`.
        /// </summary>
        private static int StackIndex(NoteModel note, List<NoteModel> sameBeat)
        {
            if (note._lineLayer <= 0) return 0;
            int best = -1;
            foreach (var other in sameBeat)
            {
                if (ReferenceEquals(other, note)) continue;
                if (other._lineIndex != note._lineIndex || other._lineLayer >= note._lineLayer) continue;
                int v = StackIndex(other, sameBeat);
                if (v > best) best = v;
            }
            return best < 0 ? 0 : best + 1;
        }

        private float _roomScale = 1f;

        /// <summary>
        /// Applies the ONE room scaling to all game objects: room root (notes,
        /// walls, scoreboard, score texts) + lightsabers + saber labels.
        /// (Takes effect live, because everything is a child of the scaled root.)
        ///
        /// IMPORTANT (invariant): All game objects use EXCLUSIVELY local
        /// base coordinates under the room root. Therefore world = scaling x base
        /// for position AND size - grid, gap between the note blocks, spawn distance
        /// and block size thus ALWAYS scale together.
        ///
        /// IMPORTANT (restart behavior): The value is ALWAYS assigned ABSOLUTELY
        /// (`localScale = base x slider`), never multiplicatively onto a stored
        /// factor - after every start this produces exactly the same state. The slider
        /// is read exclusively from the player profile (EnsurePlayer -> ApplyRoomScale).
        ///
        /// NOTE: The vertical fine adjustment of the notes/mines/floorless walls is the
        /// separate menu row "field height" (GameConfig.FieldHeightOffsetMeters) - NOT part
        /// of this scaling. The base position comes from the ArcViewer grid
        /// (GameConfig.GridPosition); the room root is driven here (and in ApplyRoomAnchor).
        /// </summary>
        private void ApplyRoomScale()
        {
            _roomScale = _player != null
                ? Mathf.Clamp(_player.Scale, GameConfig.ScaleMin, GameConfig.ScaleMax)
                : 1f;
            if (_gameRoot != null)
                _gameRoot.localScale = Vector3.one * _roomScale;
            if (_saberL != null) _saberL.SetScaleMultiplier(_roomScale);
            if (_saberR != null) _saberR.SetScaleMultiplier(_roomScale);
            // Note: The grip labels are children of the label anchor of the saber grips and
            // scale automatically with the room scaling (the anchor neutralizes the
            // base scaling of the saber -> world factor = _roomScale). Do NOT additionally
            // set localScale, otherwise the text would be scaled twice.
        }

        private void GameplayUpdate()
        {
            // Run and Play: run speed -> state. Standstill/backward pauses
            // (audio + note timeline stand still), forward couples the playback rate.
            RunAndPlayUpdate();

            // Three clocks (PROJECT_NOTES section 4): score clock = fixed reference (hitboxes/saber
            // check). Visual clock = score + note offset (spawn/visibility of the notes).
            float scoreTime = _audio.ScoreTime;
            float visualTime = _audio.VisualTime;

            // Prune consumed notes BEFORE spawning: otherwise a pooled NoteView can
            // be reused in the spawn step while its old (consumed)
            // list entry still exists -> double entry. See PruneActiveNotes.
            PruneActiveNotes();

            // Spawn according to the VISUAL clock (the notes should appear at the correct display
            // time); the lead time is orders of magnitude larger than the offset.
            while (_noteCursor < _noteSchedule.Count && _noteSchedule[_noteCursor].spawn <= visualTime)
            {
                SpawnNote(_noteSchedule[_noteCursor].note,
                    _noteSchedule[_noteCursor].startYRel,
                    _noteSchedule[_noteCursor].flipStartX,
                    _noteSchedule[_noteCursor].flipYHeight);
                _noteCursor++;
            }

            while (_wallCursor < _wallSchedule.Count && _wallSchedule[_wallCursor].spawn <= visualTime)
            {
                SpawnWall(_wallSchedule[_wallCursor].obs, _wallSchedule[_wallCursor].durSec);
                _wallCursor++;
            }

            while (_arcCursor < _arcSchedule.Count && _arcSchedule[_arcCursor].spawn <= visualTime)
            {
                SpawnArc(_arcSchedule[_arcCursor].arc);
                _arcCursor++;
            }

            while (_chainCursor < _chainSchedule.Count && _chainSchedule[_chainCursor].spawn <= visualTime)
            {
                SpawnChain(_chainSchedule[_chainCursor].chain);
                _chainCursor++;
            }

            // Remove expired arcs/chains (which have returned to the pool themselves) from the lists.
            for (int i = _activeArcs.Count - 1; i >= 0; i--)
                if (_activeArcs[i] == null || !_activeArcs[i].gameObject.activeSelf) _activeArcs.RemoveAt(i);
            for (int i = _activeChains.Count - 1; i >= 0; i--)
                if (_activeChains[i] == null || !_activeChains[i].gameObject.activeSelf) _activeChains.RemoveAt(i);

            // Hits are NOT checked here, but in LateUpdate AFTER
            // ApplyRoomAnchor (GeometricHitUpdate): only then are the room root AND
            // controller anchors at the world pose that is drawn in THIS frame.
            // A test against the root pose of the previous frame was the cause of the
            // "visually hit, but MISS / touched from the side, but 110" errors.

            // Walls: clean up + head collision (a head hit resets combo AND multiplier)
            Vector3 head = _cam != null ? _cam.transform.position : Vector3.zero;
            bool headInWall = false;
            for (int i = _activeWalls.Count - 1; i >= 0; i--)
            {
                var w = _activeWalls[i];
                if (w == null) { _activeWalls.RemoveAt(i); continue; }
                if (w.Consumed) { _activeWalls.RemoveAt(i); Destroy(w.gameObject); continue; }
                if (w.IsHeadInside(head))
                {
                    headInWall = true;
                    if (!w.HeadAlreadyHit)
                    {
                        w.HeadAlreadyHit = true;
                        // Wall hit: combo AND multiplier are reset (x1).
                        BreakCombo(true);
                    }
                }
            }
            // (3) Head in wall: music immediately dull (< 400 Hz); immediately back on leaving.
            if (_audio != null) _audio.SetWallMuffle(headInWall);

            // Light, sustained haptic feedback on blade contact (saber-on-saber
            // or saber-on-wall) - throttled per hand, allocation-free.
            UpdateContactHaptics();

            // Endless: seamlessly next song. (a) physical file end is the hardest
            // switch; (b) the fixed SCORE clock has reached the overlap (last measures
            // + 0.4 s). Both are independent of audio/note offset.
            if (_audio.ClipDuration > 0 && !_songEnded
                && (!_save.RunAndPlay || _runAndPlay.State == RunPlayState.Forward)
                && (_audio.AudioFinished || scoreTime >= _audio.ClipDuration - 0.4f))
            {
                _songEnded = true;
                OnSongEnded();
            }
        }

        private void SpawnNote(NoteModel note, float startYRel, float flipStartX, float flipYHeight)
        {
            // Exact hit time of the note (map BPM curve) - identical to BuildSchedule;
            // the hit moment (point distribution) is determined from it.
            float hitTime = _toSeconds != null ? _toSeconds(note._time)
                : BeatmapParser.BeatsToSeconds(note._time, _current != null ? _current.BeatsPerMinute : 120f);

            // Chart-faithful position: ArcViewer grid (column + vanilla row, GameConfig.
            // GridPosition); NoteView adds the "field height" offset every frame (live).
            // The hit/score check reads the same transform position (one plane).
            var pos = GameConfig.GridPosition(note._lineIndex, note._lineLayer);

            // Noodle Extensions: free position (`coordinates`/`_position`) overrides the
            // grid (ArcViewer `CalculateObjectPosition`: x offset by +2, unit 0.6 m).
            var noodle = note._customData;
            if (noodle != null && noodle.HasPosition)
            {
                float cx = noodle.Position[0];
                float cy = noodle.Position[1];
                pos.x = GameConfig.GridLeftX + (cx + 2f) * GameConfig.LaneWidth;
                pos.y = GameConfig.RowHeights[0] + cy * GameConfig.LaneWidth;
                // Pattern effects (stack/flip) do not apply to freely positioned notes.
                startYRel = 0f;
                flipStartX = pos.x;
                flipYHeight = 0f;
            }
            if (noodle != null && noodle.Flip) pos.x = -pos.x; // mirror at the X center

            // Stack start is stored relative to the target row (field-height independent).
            float startY = pos.y + startYRel;
            // Z from the VISUAL clock (continuous NJS flight, speed from the song data):
            // start value at the spawn moment; from then on NoteView.Update follows.
            pos.z = GameConfig.JumpZ(hitTime, VisualTime, _njs);
            var view = NotePool.Get(note, pos, hitTime, _njs, _reactionTime, _halfJumpDistance,
                startY, flipStartX, flipYHeight);
            _activeNotes.Add(view);

            // Reference 1:1 (BeatSaberClone BoxSpawner): spawn particles at the note position -
            // red for `_type 0`, blue for `_type 1`. Mines (type 3) are not note blocks and
            // deliberately remain without an effect.
            if (note._type != 3)
                Fx.PlayNoteSpawn(view.transform.position, note._type == 0, _roomScale);

            view.OnPassedMiss = null;
            view.OnPassedMiss += OnNoteMissed;
            view.OnComboBreak = null;
            view.OnComboBreak += OnNoteComboBreak;
        }

        /// <summary>
        /// Spawns a wall from the ArcViewer grid (wall container 0.1 m, grid unit
        /// 0.6 m) with time-based Z position. The length in movement direction is
        /// duration x NJS (ArcViewer WallManager) - this makes the player's crossing time
        /// match the map duration exactly, and the front edge aligns with the notes.
        /// </summary>
        private void SpawnWall(ObstacleModel obs, float durationSeconds)
        {
            // Exact hit time (BPM curve) - consistent with BuildSchedule
            float hitTime = _toSeconds != null ? _toSeconds(obs._time)
                : BeatmapParser.BeatsToSeconds(obs._time, _current != null ? _current.BeatsPerMinute : 120f);

            // Width = columns x 0.6; left edge on the column grid
            // (ArcViewer WallManager.CalculateWallPosition + wall constructor).
            float widthCols = Mathf.Max(1f, obs._width);
            float width = widthCols * GameConfig.LaneWidth;
            float x = GameConfig.GridLeftX + obs._lineIndex * GameConfig.LaneWidth
                      + (width - GameConfig.LaneWidth) * 0.5f;

            // Vertical position from the map (raw values). The "field height" shifts ALL walls
            // up (floor-standing ones are extended down to the floor); the ENTIRE field is raised/lowered
            // by the left stick.
            GameConfig.ObstacleBounds(obs, out float bottom, out float height);
            float y = bottom + height * 0.5f;

            float length = Mathf.Max(durationSeconds * _njs, GameConfig.MinWallSize);
            float front = GameConfig.JumpZ(hitTime, VisualTime, _njs)
                          - GameConfig.NoteSize * 0.5f; // front edge = note front face
            var pos = new Vector3(x, y, front + length * 0.5f);

            var wall = WallView.Create(obs, pos, _gameRoot, width, height, length,
                hitTime, _njs, bottom);
            _activeWalls.Add(wall);
        }

        /// <summary>Spawns an arc (v3/v4 slider). The arc is purely visual; the
        /// head/tail notes exist as colorNotes and are scored normally.</summary>
        private void SpawnArc(ArcModel arc)
        {
            float headTime = _toSeconds != null ? _toSeconds(arc._headTime)
                : BeatmapParser.BeatsToSeconds(arc._headTime, _current != null ? _current.BeatsPerMinute : 120f);
            float tailTime = _toSeconds != null ? _toSeconds(arc._tailTime)
                : BeatmapParser.BeatsToSeconds(arc._tailTime, _current != null ? _current.BeatsPerMinute : 120f);
            _activeArcs.Add(ArcView.Get(arc, headTime, tailTime, _njs, _gameRoot));
        }

        /// <summary>Spawns a chain (v3/v4 burstSlider): visible links between head
        /// and tail point. The head note itself runs along as a normal (marked) note.</summary>
        private void SpawnChain(ChainModel chain)
        {
            float headTime = _toSeconds != null ? _toSeconds(chain._time)
                : BeatmapParser.BeatsToSeconds(chain._time, _current != null ? _current.BeatsPerMinute : 120f);
            float tailTime = _toSeconds != null ? _toSeconds(chain._tailTime)
                : BeatmapParser.BeatsToSeconds(chain._tailTime, _current != null ? _current.BeatsPerMinute : 120f);
            _activeChains.Add(ChainView.Get(chain, headTime, tailTime, _njs, _gameRoot));
        }

    }
}

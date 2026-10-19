// GameManager: Points, combo, slice display.
using System;
using System.Collections;
using UnityEngine;

namespace QuestSaber
{
    public sealed partial class GameManager
    {
        // ======================================================================
        // Points / Combo
        // ======================================================================
        /// <summary>
        /// Scores a valid cut around the MOMENT OF BLADE CONTACT (not, as before,
        /// at the score-zone moment when the note reaches Z=0): The first contact is the
        /// actual cut - the blade is then at the note, pre-swing lies before it,
        /// follow-through after it. If instead you wait until the score zone, the blade has long
        /// passed the note: center approx. 0 and follow-through approx. 0, although the cut was perfect
        /// (device log: often 85 instead of 115, `center=0`).
        /// The reference direction is the blade direction AT THE CUT MOMENT (captured at contact);
        /// pre-swing/follow-through = largest change of the blade direction in the +/-<see cref="GameConfig.SwingMeasureWindow"/>
        /// window around it (OpenSaberPlus `LightSaber.gd`) - plane- and direction-independent,
        /// so that straight and diagonal cuts of equal quality count the same.
        /// </summary>
        private IEnumerator ScoreAtCutAsync(
            SaberView saber, float cutTime, Vector3 reference, float preAngle,
            Vector3 noteLocal, Vector3 notePos, bool chainHead, int cutDirection)
        {
            if (_current == null) yield break;

            // Follow-through = largest deviation in the window AFTER the cut (OpenSaberPlus
            // `followthrough_time`). Wait on the score clock, with real-time safeguard
            // against pause/song end.
            float postEnd = cutTime + GameConfig.SwingMeasureWindow;
            float realCap = Time.unscaledTime + GameConfig.SwingMeasureWindow * 3f + 0.25f;
            while (saber != null && saber.HasCurrentPose && ScoreTime < postEnd
                   && Time.unscaledTime < realCap)
                yield return null;
            float postAngle = saber != null && saber.HasCurrentPose
                ? saber.MaxBladeDeviation(reference, cutTime, postEnd)
                : 0f;

            // Center: nearest XY pass of the blade at the note center over the whole
            // measurement window - not a single frame (which depended on the hit zone).
            Vector2 noteCenter = new Vector2(noteLocal.x, noteLocal.y);
            float centerScore = saber != null && saber.HasCurrentPose
                ? GameConfig.CenterMax * (1f - Mathf.Clamp01(
                    saber.MinBladeCenterDeviation(noteCenter,
                        cutTime - GameConfig.SwingMeasureWindow, postEnd) / GameConfig.CenterDistanceMax))
                : 0f;

            AwardSlice(notePos, preAngle, postAngle, centerScore, chainHead, cutDirection);
        }

        /// <summary>
        /// Points for a valid note cut (0..115, original Beat Saber distribution):
        /// pre-swing 70 (full from 100°), follow-through 30 (full from 60°), center 15 (full in the
        /// note center) - linear in between, without a deadzone. Pre-swing/follow-through are measured
        /// as direction change, as in OpenSaberPlus (direction-independent).
        /// Chain head: no follow-through.
        /// The base points are multiplied by the combo multiplier.
        /// </summary>
        private void AwardSlice(Vector3 notePos, float preAngle, float postAngle, float centerScore, bool chainHead, int cutDirection)
        {
            int prePart = Mathf.RoundToInt(
                GameConfig.PreSwingMax * Mathf.Clamp01(preAngle / GameConfig.PreSwingFullAngle));
            int centerPart = Mathf.RoundToInt(Mathf.Clamp(centerScore, 0f, GameConfig.CenterMax));
            int postPart = chainHead ? 0 : Mathf.RoundToInt(
                GameConfig.FollowThroughMax * Mathf.Clamp01(postAngle / GameConfig.FollowThroughFullAngle));
            int baseScore = prePart + postPart + centerPart;

            if (GameConfig.LogHitDiagnostics)
                Debug.Log($"[Score] dir={cutDirection} chainHead={chainHead} pre={prePart} post={postPart} " +
                          $"center={centerPart} total={baseScore} preDeg={preAngle:0.0} postDeg={postAngle:0.0}");

            int multiplier = AdvanceCombo();

            _score += (long)baseScore * multiplier;
            UpdateHud();
            TrySaveBestImmediately();

            // Reference 1:1: let the cut score (0..115) rise at the note position
            ShowSliceScore(notePos, baseScore);
        }

        /// <summary>
        /// Advances the combo and one multiplier tier step for a single scoring hit
        /// (exactly like the original/ArcViewer, ScoringUtils.ManuallyAdvanceCombo):
        /// the tier rises as soon as the progress reaches the hit count of the current tier;
        /// the note/link that reaches the tier already counts with the NEW tier.
        /// Returns the multiplier to apply to the hit. Shared by notes and chain links.
        /// </summary>
        private int AdvanceCombo()
        {
            _combo++;
            if (_multTier < GameConfig.ComboMultipliers.Length - 1)
            {
                _comboProgress++;
                if (_comboProgress >= GameConfig.HitsNeededForComboIncrease[_multTier])
                {
                    _multTier++;
                    _comboProgress = 0;
                }
            }
            return GameConfig.ComboMultipliers[_multTier];
        }

        // ---------- Floating score (reference: InGameUiView.DisplaySliceScore) ----------

        private void ShowSliceScore(Vector3 notePosition, int score, bool isMiss = false)
        {
            if (_cam == null) return;
            var tmp = GetScoreText();
            // Compute LOCALLY in the room root: The root scales position and size together,
            // the offsets added here are base units (no separate x scaling).
            Vector3 localNote = _gameRoot != null ? _gameRoot.InverseTransformPoint(notePosition) : notePosition;
            Vector3 pos = localNote
                + Vector3.forward * ScoreSpawnForwardOffset
                + Vector3.down * ScoreSpawnDownwardOffset;
            tmp.transform.localPosition = pos;

            // Billboard to the player (yaw only): front faces away from the camera.
            // worldPos is transformed via the room root - this also works with a
            // rotated room (`_roomYawDeg`). The world rotation is set
            // directly and thereby replaces the root rotation of the text.
            Vector3 worldPos = _gameRoot != null ? _gameRoot.TransformPoint(pos) : pos;
            Vector3 away = worldPos - _cam.transform.position;
            away.y = 0f;
            tmp.transform.rotation = away.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(away.normalized)
                : Quaternion.identity;
            StartCoroutine(FloatScoreRoutine(tmp, score, isMiss));
        }

        private TMPro.TextMeshPro GetScoreText()
        {
            if (_scoreTextPool.Count > 0)
            {
                var pooled = _scoreTextPool.Pop();
                pooled.gameObject.SetActive(true);
                return pooled;
            }
            var go = new GameObject("SliceScore");
            if (_gameRoot != null) go.transform.SetParent(_gameRoot, false);
            var tmp = go.AddComponent<TMPro.TextMeshPro>();
            tmp.font = TMPro.TMP_Settings.defaultFontAsset;
            tmp.fontSize = 1f;
            tmp.alignment = TMPro.TextAlignmentOptions.Center;
            tmp.rectTransform.sizeDelta = new Vector2(4f, 2f);
            tmp.color = Color.white;
            return tmp;
        }

        private IEnumerator FloatScoreRoutine(TMPro.TextMeshPro tmp, int score, bool isMiss)
        {
            tmp.text = isMiss ? "MISS" : score.ToString();
            Vector3 start = tmp.transform.localPosition; // local in the room root (scales along)
            Color c = tmp.color;
            float t = 0f;
            while (t < ScoreDisplayDuration)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / ScoreDisplayDuration);
                // Rising: Ease.OutQuad, fading: Ease.InQuad - like the reference tweens.
                // Local base units: The room root scales the distance automatically.
                tmp.transform.localPosition = start + Vector3.forward
                    * (ScoreFloatDistance * (1f - (1f - k) * (1f - k)));
                c.a = 1f - k * k;
                tmp.color = c;
                yield return null;
            }
            tmp.gameObject.SetActive(false);
            _scoreTextPool.Push(tmp);
        }

        /// <summary>Save the best score immediately once it is beaten (not only at song end).</summary>
        private void TrySaveBestImmediately()
        {
            if (_player == null || _current == null) return;
            int best = _player.GetBest(_current.BestKey);
            if (_score > best)
            {
                _player.SetBest(_current.BestKey, (int)Math.Min(_score, int.MaxValue));
                Save();
            }
        }

        private void OnNoteMissed(NoteView note)
        {
            if (note != null && note.Kind == NoteKind.Bomb) return; // a dodged bomb is NOT a mistake
            if (note != null) ShowSliceScore(note.transform.position, 0, true);
            BreakCombo();
        }

        private void OnNoteComboBreak(NoteView note) => BreakCombo();

        private void BreakCombo(bool fullReset = false)
        {
            if (_combo == 0 && _multTier == 0 && _comboProgress == 0) return; // nothing to lose
            _combo = 0;
            _comboProgress = 0;
            if (fullReset)
            {
                _multTier = 0;
            }
            else
            {
                // Missed note/mistake: original behavior - losing the combo halves the
                // multiplier (x8 -> x4 -> x2 -> x1); afterwards the tier's progress
                // starts over (ArcViewer: comboMult--, comboProgress = 0).
                if (_multTier > 0) _multTier--;
            }
            UpdateHud();
        }

        private void UpdateHud()
        {
            if (_hud == null) return;
            // Progress = hits WITHIN the current tier (ArcViewer: comboProgress /
            // HitsNeededForComboIncrease[tier]); x8 is always full.
            float progress = 1f;
            if (_multTier < GameConfig.ComboMultipliers.Length - 1)
            {
                int needed = GameConfig.HitsNeededForComboIncrease[_multTier];
                progress = needed > 0 ? Mathf.Clamp01(_comboProgress / (float)needed) : 1f;
            }
            _hud.SetScore((int)Math.Min(_score, int.MaxValue), _combo,
                GameConfig.ComboMultipliers[_multTier], progress);
        }

        private void OnSongEnded()
        {
            if (_player != null && _current != null && _score > _player.GetBest(_current.BestKey))
            {
                _player.SetBest(_current.BestKey, (int)Math.Min(_score, int.MaxValue));
                Save(); // mark as "pending" - the write happens in the LoadSong flush
            }
            // Continue seamlessly: next song (with only one it comes back immediately)
            LoadSong(_songIndex + 1);
        }

    }
}

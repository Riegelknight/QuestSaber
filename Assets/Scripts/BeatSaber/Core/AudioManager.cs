using System;
using System.Collections;
using UnityEngine;

namespace QuestSaber
{
    /// <summary>
    /// Plays the current song (demo from Resources, real songs from the headset via
    /// UnityWebRequest). Provides the FIXED score clock (fixed reference for hitboxes/sabers);
    /// the visual clock is identical to it (the former note offset is folded into the
    /// score clock), and the audio layer offset shifts
    /// only the music playback. Plus small sound effects (without offset).
    /// </summary>
    public sealed class AudioManager : MonoBehaviour
    {
        private AudioSource _music;
        // DSP delay cell of the music (audio offset) - sits with the source on the
        // child object [Music], so that only the music is delayed.
        private AudioDelayFilter _delay;

        // Three separate effect sources, because the two uses of the slice clip are
        // SHAPED differently (low-pass + pitch apply per source):
        //   _sfx    - UI click, unfiltered (original sound)
        //   _hit    - saber<->note: muffled, deeper, quiet (direct hit feel)
        //   _toggle - saber retract/extend: FRONT SECTION, sped up, more muffled
        // Every effect source sits on its OWN child object: an AudioLowPassFilter
        // affects ALL AudioSources of the same GameObject - without separation the
        // muffled slice sound would also filter the music and UI click.
        private AudioSource _sfx;
        private AudioSource _hit;
        private AudioSource _toggle;
        // (3) Music low-pass on head<->wall contact; (4) sizzle source.
        private AudioLowPassFilter _wallMuffle;
        private AudioSource _sizzle;

        /// <summary>Current audio offset in seconds (menu "Audio Offset", -300...+300).
        /// Shifts ONLY the audible music - the note clock NEVER moves
        /// (the base delay of the delay line compensates the headroom downward).
        /// Default = 0: there is NO hard offset anymore - the 85 ms previously calibrated on the PICO 4
        /// are inside the note clock (<see cref="GameConfig.AudioLatencyBaseMs"/>).
        /// The game is in sync at offset == AudioLatencyBaseMs.</summary>
        public float AudioOffsetSeconds { get; private set; }

        /// <summary>Sets the audio offset (seconds; +/-300 ms in the menu, 5 ms steps).
        /// Affects ONLY the audible music (AudioDelayFilter.SetOffset);
        /// the score clock carries the constant base compensation (see ScoreTime).
        /// No seek, no clip intervention, no note movement.</summary>
        public void SetAudioOffset(float seconds)
        {
            if (seconds > GameConfig.AudioOffsetMaxMs / 1000f) seconds = GameConfig.AudioOffsetMaxMs / 1000f;
            if (seconds < GameConfig.AudioOffsetMinMs / 1000f) seconds = GameConfig.AudioOffsetMinMs / 1000f;
            AudioOffsetSeconds = seconds;
            if (_delay != null)
                _delay.SetOffset(seconds);
        }

        private bool _paused;
        // Run-and-Play: its own pause (standstill/backward) and playback rate. Both
        // states are COMBINED with the menu pause (ApplyPlaybackState) - no
        // layer overrides the other.
        private bool _runPaused;
        private float _runRate = 1f;

        // ---- smooth song clock (see AdvanceSmoothClock) ----
        private float _smoothAudioTime;
        private int _clockFrame = -1;
        private bool _clockReady;

        // Overrun of the audio clock: if the file runs out naturally, the clock keeps counting.
        // Otherwise it would stand still at "length - delay" and the song-end switch would
        // never trigger with a positive delay (endless hang).
        private bool _clockPastEnd;
        private float _clockPastEndBase;
        private float _clockPastEndWall;

        private void ResetEndlessClock()
        {
            _clockPastEnd = false;
        }

        private void Awake()
        {
            // Music on its OWN child object [Music]: the source, wall low-pass
            // and the audio offset delay filter live there. An audio filter only affects AudioSources
            // of the SAME GameObject - this way only the music is delayed/
            // filtered, the SFX children (their own objects) sound immediately unchanged.
            var musicGo = new GameObject("Music");
            musicGo.transform.SetParent(transform, false);
            _music = musicGo.AddComponent<AudioSource>();
            _music.playOnAwake = false;
            _music.loop = false;
            _music.spatialBlend = 0f;

            // Audio offset: sample-accurate delay of only the music (both directions).
            _delay = musicGo.AddComponent<AudioDelayFilter>();
            _delay.SetOffset(AudioOffsetSeconds);

            _sfx = MakeSfxSource("SfxUi");        // UI click: unfiltered
            _hit = MakeSfxSource("SfxHit");       // note hit: muffled
            _toggle = MakeSfxSource("SfxToggle"); // toggle click: muffled + fast

            AddLowpass(_hit, GameConfig.HitSfxLowpassHz, GameConfig.HitSfxLowpassQ);
            AddLowpass(_toggle, GameConfig.SaberToggleLowpassHz, GameConfig.SaberToggleLowpassQ);

            // (3) Music low-pass (on the [Music] object): head in wall = muffled.
            _wallMuffle = musicGo.AddComponent<AudioLowPassFilter>();
            _wallMuffle.cutoffFrequency = GameConfig.WallMuffleCutoffHz;
            _wallMuffle.enabled = false;

            // (4) Wall sizzle: spatial, quiet continuous noise at the contact point (the
            // slice clip loops with a high-pass = "electric" crackling).
            _sizzle = MakeSfxSource("SfxSizzle");
            _sizzle.spatialBlend = 1f;
            _sizzle.loop = true;
            _sizzle.volume = 0f;
            _sizzle.minDistance = 0.15f;
            _sizzle.maxDistance = 3f;
            var sizzleHp = _sizzle.gameObject.AddComponent<AudioHighPassFilter>();
            sizzleHp.cutoffFrequency = 1200f;
        }

        // ---------- (3) head<->wall low-pass, (4) sizzle, (8) stereo focus ----------

        /// <summary>(3) Immediately switch the music to muffled while the head is stuck in a wall.</summary>
        public void SetWallMuffle(bool on)
        {
            if (_wallMuffle != null) _wallMuffle.enabled = on;
        }

        /// <summary>(4) Wall sizzle on/off; the source sits at the contact point (spatial).</summary>
        public void SetWallSizzle(bool on, Vector3 worldPos)
        {
            if (_sizzle == null) return;
            if (on)
            {
                EnsureSfxCached();
                if (_sliceSfx != null && _sizzle.clip != _sliceSfx) _sizzle.clip = _sliceSfx;
                _sizzle.transform.position = worldPos;
                if (!_sizzle.isPlaying) _sizzle.Play();
                _sizzle.volume = GameConfig.WallSizzleVolume;
            }
            else
            {
                _sizzle.volume = 0f;
            }
        }

        /// <summary>
        /// Effect source on its own child object, non-spatial, source volume 1
        /// (the effect volumes are set individually in GameConfig - ArcViewer also plays
        /// hit sounds NOT spatially, volume there 0.5).
        /// </summary>
        private AudioSource MakeSfxSource(string childName)
        {
            var child = new GameObject(childName);
            child.transform.SetParent(transform, false);
            var src = child.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.volume = 1f;
            return src;
        }

        /// <summary>Attach a low-pass to an effect source (muffled sound). The
        /// cutoff frequency takes effect directly - `cutoffFrequency` is enough (a `customCutoff`
        /// switch does not exist in this Unity API).</summary>
        private static void AddLowpass(AudioSource src, float cutoffHz, float q)
        {
            var filter = src.gameObject.AddComponent<AudioLowPassFilter>();
            filter.cutoffFrequency = cutoffHz;
            filter.lowpassResonanceQ = q;
            filter.enabled = true;
        }

        /// <summary>Loads and starts a song (audio layer with its offset).</summary>
        public IEnumerator PlaySong(SongMeta meta, Action onLoaded)
        {
            _music.Stop();
            _music.clip = null;
            ResetEndlessClock();
            _paused = false;
            _runPaused = false;
            _runRate = 1f;
            _music.pitch = 1f;
            if (meta == null)
            {
                onLoaded?.Invoke();
                yield break;
            }

            if (meta.IsDemo)
            {
                var clip = Resources.Load<AudioClip>(GameConfig.DemoTrackResource);
                if (clip != null)
                {
                    _music.clip = clip;
                    BeginMusic();
                }
                else
                {
                    Debug.LogError("[QuestSaber] Demo audio missing!");
                }
            }
            else if (!string.IsNullOrEmpty(meta.AudioPath))
            {
                AudioClip loaded = null;
                using (var uwr = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(
                           ToFileUri(meta.AudioPath), UnityEngine.AudioType.OGGVORBIS))
                {
                    yield return uwr.SendWebRequest();
                    if (uwr.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                    {
                        loaded = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(uwr);
                        _music.clip = loaded;
                        BeginMusic();
                    }
                    else
                    {
                        Debug.LogError($"[QuestSaber] Failed to load audio: {uwr.error} ({meta.AudioPath})");
                    }
                }
            }

            onLoaded?.Invoke();
        }

        /// <summary>Starts the music from position 0 (no seek/offset anymore).</summary>
        private void BeginMusic()
        {
            // New song: re-anchor the smooth clock to the fresh audio position.
            _clockReady = false;
            _clockFrame = -1;
            ApplyPlaybackState();
        }

        /// <summary>
        /// Applies the combined pause (menu AND run-and-play) and sets the
        /// playback rate. Central point so that the layers do not override each other:
        /// `PauseSong`/`ResumeSong` (menu) and `SetRunPlayPaused`/`SetRunPlayRate`
        /// (run coupling) only change their own state and call THIS method.
        /// </summary>
        private void ApplyPlaybackState()
        {
            if (_music == null) return;
            _music.pitch = _runRate;
            if (_paused || _runPaused)
            {
                _music.Pause();
                return;
            }
            _music.Play();
        }

        /// <summary>Run-and-Play: standstill/backward pauses the song (score clock freezes
        /// -> notes/timeline stand still). Independent of the menu pause.</summary>
        public void SetRunPlayPaused(bool paused)
        {
            if (_runPaused == paused) return;
            _runPaused = paused;
            ApplyPlaybackState();
        }

        /// <summary>Run-and-Play: sets the playback rate coupled to the running speed
        /// (AudioSource.pitch). Is clamped to the GameConfig limits.</summary>
        public void SetRunPlayRate(float rate)
        {
            rate = Mathf.Clamp(rate, GameConfig.RunPlayMinRate, GameConfig.RunPlayMaxRate);
            if (Mathf.Approximately(rate, _runRate)) return;
            _runRate = rate;
            if (_music != null) _music.pitch = rate;
        }

        /// <summary>Ends the run coupling: rate back to 1, clears the run pause
        /// (the menu pause remains untouched and still applies via ApplyPlaybackState).</summary>
        public void ClearRunPlay()
        {
            _runPaused = false;
            _runRate = 1f;
            ApplyPlaybackState();
        }

        public void StopSong()
        {
            _music.Stop();
            _paused = false;
            _runPaused = false;
            _runRate = 1f;
            _music.pitch = 1f;
            _clockReady = false;
            ResetEndlessClock();
        }

        public void PauseSong()
        {
            _paused = true;
            ApplyPlaybackState();
        }

        public void ResumeSong()
        {
            _paused = false;
            ApplyPlaybackState();
        }

        /// <summary>
        /// Builds a valid file:// URL from a local path. Path components are
        /// URL-escaped (spaces, &amp; ...), otherwise loading fails on Android for
        /// folder names like "Runaway (U &amp; I) - Tpenny68" or "Dance Monkey.egg".
        /// </summary>
        private static string ToFileUri(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string norm = path.Replace('\\', '/');
            var parts = norm.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = Uri.EscapeDataString(parts[i]);
            }
            return "file://" + string.Join("/", parts);
        }

        /// <summary>
        /// Smooths the song clock. <c>AudioSource.time</c> is updated by the AUDIO
        /// thread and jumps in buffer-sized steps (~20-40 ms on Quest). Driving the note
        /// positions straight from it made the notes advance in visible steps even at a
        /// steady 120 fps - "120 fps / 120 Hz" in the menu, yet the notes stuttered. The
        /// clock is therefore integrated per FRAME and only GENTLY re-synced to the real
        /// audio position, so the motion is smooth AND stays locked to the music.
        /// Runs at most once per frame, before any reader (see <see cref="AudioTime"/>).
        /// </summary>
        private void AdvanceSmoothClock()
        {
            if (_music == null) return;
            int frame = Time.frameCount;
            if (frame == _clockFrame) return;   // at most once per frame
            _clockFrame = frame;

            if (_music.clip == null) { _smoothAudioTime = 0f; _clockReady = false; return; }

            bool rolling = !_paused && !_runPaused && _music.isPlaying;
            if (!_clockReady || !rolling)
            {
                _smoothAudioTime = _music.time;   // (re)anchor while stopped/paused/at start
                _clockReady = true;
                return;
            }

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f || dt > 0.25f) dt = 0.008f;   // guard against a stalled main loop

            // Advance at the real playback rate (Run-and-Play changes it).
            _smoothAudioTime += dt * _runRate;

            // Gentle drift correction toward the true audio position - spreads the
            // buffer-sized step over several frames instead of showing it as a jump.
            float err = _music.time - _smoothAudioTime;
            if (Mathf.Abs(err) > 0.25f)
                _smoothAudioTime = _music.time;               // seek / real desync: snap
            else
                _smoothAudioTime += err * Mathf.Clamp01(dt * 3f); // ~0.3 s time constant
        }

        /// <summary>
        /// Audio position of the file in seconds. If playback runs out NATURALLY, the clock
        /// keeps running from there (overrun): otherwise <c>_music.time</c> would stay at
        /// <c>clip.length</c> - the play clock shortened by the offset would never reach the
        /// end, and from about 0.4 s delay the song would no longer change. During a pause
        /// (menu) the file does not end, so the clock freezes there as usual.
        /// The position is read from the SMOOTH clock (see AdvanceSmoothClock) so the
        /// notes move fluidly instead of in audio-buffer steps.
        /// </summary>
        public float AudioTime
        {
            get
            {
                if (_music == null || _music.clip == null) return 0f;
                AdvanceSmoothClock();
                float t = _smoothAudioTime;
                if (!AudioFinished) { _clockPastEnd = false; return t; }
                if (!_clockPastEnd)
                {
                    _clockPastEnd = true;
                    _clockPastEndBase = t;
                    _clockPastEndWall = Time.unscaledTime;
                }
                return _clockPastEndBase + (Time.unscaledTime - _clockPastEndWall);
            }
        }

        /// <summary>True when the file has physically finished playing (end switch).
        /// During menu OR run pause NEVER "finished": otherwise the overrun clock
        /// (unscaledTime) would keep running and the score clock would keep counting during standstill.</summary>
        public bool AudioFinished => _music != null && _music.clip != null
            && !_paused && !_runPaused
            && !_music.isPlaying
            && _music.time >= _music.clip.length - 0.02f;

        public float ScoreTime
        {
            get
            {
                float totalSec = (GameConfig.AudioLatencyBaseMs + GameConfig.AudioOffsetBaseMs) / 1000f;
                return AudioTime - totalSec;
            }
        }

        public float VisualTime => ScoreTime;

        public float ClipDuration => _music.clip != null ? _music.clip.length : 0f;

        // Load SFX clips once and keep them in memory (performance): Resources.Load per
        // call would be a dictionary lookup + renewed clip handling in the hot path for
        // EVERY slice and every UI click. The clips are small and stay unchanged anyway -
        // loading once is enough (see PROJECT_NOTES.md section 10).
        private static AudioClip _sliceSfx, _uiSfx;
        private static bool _sfxCached;

        private static void EnsureSfxCached()
        {
            if (_sfxCached) return;
            _sfxCached = true;
            _sliceSfx = Resources.Load<AudioClip>(GameConfig.SliceSfxResource);
            _uiSfx = Resources.Load<AudioClip>(GameConfig.UISfxResource);
        }

        /// <summary>
        /// Saber <-> note: the slice sound, LOW-PASS filtered (muffled), deeper in tone and
        /// QUIET - the "direct muffled noise" on a hit. Pitch, cutoff frequency and
        /// volume are in `GameConfig` (`HitSfxPitch`/`HitSfxLowpassHz`/`HitSfxVolume`).
        /// `HitSfxVolume` = 0 deliberately mutes the sound, without the call disappearing
        /// from the hit path (no code noise, no `PlayOneShot` on mute).
        /// </summary>
        public void PlayHitSfx()
        {
            if (GameConfig.HitSfxVolume > 0f)
            {
                EnsureSfxCached();
                if (_sliceSfx != null)
                {
                    _hit.pitch = GameConfig.HitSfxPitch;
                    _hit.PlayOneShot(_sliceSfx, GameConfig.HitSfxVolume);
                }
            }
        }

        /// <summary>
        /// Easter egg toggle (saber retract AND extend): the same slice sound, but
        /// trimmed at the front, sped up and filtered more muffled - this makes it sound directly
        /// like a toggle click instead of a hit. Only `Play()` can set a start offset
        /// (`timeSamples`); `Slice.ogg` is in memory as "Decompress On Load"
        /// (`loadType: 0` in the .meta), so the sample offset is reliable - with
        /// streaming clips it would not work. Everything tunable via `GameConfig`.
        /// </summary>
        public void PlaySaberToggleSfx()
        {
            if (GameConfig.SaberToggleSfxVolume > 0f)
            {
                EnsureSfxCached();
                var clip = _sliceSfx;
                if (clip != null)
                {
                    int skip = Mathf.Clamp(
                        Mathf.RoundToInt(GameConfig.SaberToggleSfxSkipMs * clip.frequency / 1000f),
                        0, Mathf.Max(0, clip.samples - 1));
                    _toggle.clip = clip;
                    _toggle.volume = GameConfig.SaberToggleSfxVolume;
                    _toggle.pitch = GameConfig.SaberToggleSfxPitch;
                    _toggle.timeSamples = skip;
                    _toggle.Play();
                }
            }
        }

        public void PlayUiSfx()
        {
            EnsureSfxCached();
            if (_uiSfx != null) _sfx.PlayOneShot(_uiSfx, GameConfig.UISfxVolume);
        }
    }
}

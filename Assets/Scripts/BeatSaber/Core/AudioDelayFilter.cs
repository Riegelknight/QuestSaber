using UnityEngine;

namespace QuestSaber
{
    /// <summary>
    /// Delays the audio signal of its GameObject by a configurable number
    /// of milliseconds (audio offset).
    /// </summary>
    public sealed class AudioDelayFilter : MonoBehaviour
    {
        /// <summary>Slew rate: max. shift of the read pointer per audio callback
        /// (seconds). Unity calls OnAudioFilterRead roughly every 20 ms (~50/s) -> 0.02 s per
        /// callback ~ 1 s drift per second - a change is audible within a fraction of a
        /// second, without a click.</summary>
        private const float MaxSlewSecondsPerCallback = 0.02f;

        /// <summary>Buffer headroom above the largest offset setting (seconds).</summary>
        private const float BufferHeadroomSeconds = 0.2f;

        private float[] _ring;             // interleaved ring buffer (frames x channels)
        private int _channels;
        private int _sampleRate;
        private int _ringFrames;
        private int _writeFrame;           // write pointer (frame index in the ring)
        private float _delaySamples;       // current read distance (fractional, samples)
        private float _targetSeconds;      // target delay (main thread -> audio thread)

        public void SetOffset(float offsetSeconds)
        {
            float baseDelay = GameConfig.AudioOffsetBaseMs / 1000f;
            float delay = baseDelay + offsetSeconds;
            if (delay < 0f) delay = 0f;
            float maxDelay = (GameConfig.AudioOffsetBaseMs + GameConfig.AudioOffsetMaxMs) / 1000f
                             + BufferHeadroomSeconds;
            if (delay > maxDelay) delay = maxDelay;
            _targetSeconds = delay;
            if (Mathf.Abs(delay - baseDelay) > 0.0005f)
                Debug.Log($"[QuestSaber] Audio offset = {offsetSeconds * 1000f:0} ms " +
                          $"(music delay {delay * 1000f:0} ms; notes untouched)");
        }

        private void OnEnable()
        {
            // Read the sample rate from the audio system (usually 48 kHz); the channel
            // count is only provided by the callback - the buffer is then created once.
            _sampleRate = AudioSettings.outputSampleRate;
            if (_sampleRate <= 0) _sampleRate = 48000;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            int sampleRate = _sampleRate;

            // Create the buffer once based on the real channel count (no Unity API needed).
            if (_ring == null || _channels != channels || _ringFrames == 0)
            {
                _channels = channels;
                int maxFrames = Mathf.CeilToInt(
                    (GameConfig.AudioOffsetBaseMs + GameConfig.AudioOffsetMaxMs) / 1000f
                    * sampleRate + BufferHeadroomSeconds * sampleRate);
                if (maxFrames < 64) maxFrames = 64;
                _ringFrames = maxFrames;
                _ring = new float[maxFrames * channels];
                if (_writeFrame >= maxFrames) _writeFrame = 0;
            }

            int ch = channels;
            int ring = _ringFrames;
            int write = _writeFrame;
            int frameCount = data.Length / ch;

            // The ring is ALWAYS written along (cheap copying) - that way it is already
            // "warm" when switching from pass-through to delay, and no gap/garbage
            // occurs at the transition.
            for (int f = 0; f < frameCount; f++)
            {
                int w = (write + f) % ring;
                int baseW = w * ch;
                for (int c = 0; c < ch; c++)
                    _ring[baseW + c] = data[f * ch + c];
            }

            float delay = _delaySamples;
            float target = _targetSeconds * sampleRate;

            // Neutral pass-through mode: only write (ring stays warm), output unchanged.
            if (delay < 0.5f && target < 0.5f)
            {
                _writeFrame = (write + frameCount) % ring;
                _delaySamples = 0f;
                return;
            }

            // Target approach (slew) - click-free tracking during menu changes.
            float maxStep = MaxSlewSecondsPerCallback * sampleRate;
            if (target - delay > maxStep) delay += maxStep;
            else if (delay - target > maxStep) delay -= maxStep;
            else delay = target;

            // Safety clamp: the read distance always stays >= 1 frame below the
            // ring size (otherwise the fractional read pointer could be overtaken).
            int maxDelaySamples = ring - 2;
            if (delay > maxDelaySamples) delay = maxDelaySamples;

            // Read back delayed: read position = write - delay (fractional -> linear
            // lerp of the two neighboring frames). delay >= 0 => the read pointer is never
            // ahead of the write pointer; offsets <= 0.3 s are far below the ring size, so the
            // samples have already been written.
            int delayInt = (int)delay;
            float frac = delay - delayInt;
            for (int f = 0; f < frameCount; f++)
            {
                int w = (write + f) % ring;
                int readIdx0 = w - delayInt;
                int readIdx1 = readIdx0 - 1;
                if (readIdx0 < 0) readIdx0 += ring;
                if (readIdx1 < 0) readIdx1 += ring;
                int base0 = readIdx0 * ch;
                int base1 = readIdx1 * ch;
                for (int c = 0; c < ch; c++)
                {
                    float s0 = _ring[base0 + c];
                    float s1 = _ring[base1 + c];
                    data[f * ch + c] = s0 + (s1 - s0) * frac;
                }
            }

            _writeFrame = (write + frameCount) % ring;
            _delaySamples = delay;
        }
    }
}

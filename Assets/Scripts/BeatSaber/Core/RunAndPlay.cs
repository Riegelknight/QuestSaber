using UnityEngine;

namespace QuestSaber
{
    /// <summary>Movement state of the player in Run-and-Play mode.</summary>
    public enum RunPlayState
    {
        /// <summary>Absolute standstill: song paused, notes freeze.</summary>
        Still,
        /// <summary>Forward: audio + note timeline run in sync with the running speed.</summary>
        Forward,
        /// <summary>Backward: gameplay stops immediately, the field is dragged along.</summary>
        Backward
    }

    /// <summary>
    /// Buffers the forward speed of the room and derives from it the
    /// Run-and-Play state and the smoothed speed. Pure logic,
    /// no Unity dependency except <see cref="Mathf"/>; allocation-free.
    /// </summary>
    public sealed class RunAndPlay
    {
        // Ring buffer: per frame (forward delta [m], dt [s]). 256 slots are plenty for
        // a 0.3 s window even at very high frame rates (60 Hz division = ~18).
        private const int Capacity = 256;
        private readonly float[] _delta = new float[Capacity];
        private readonly float[] _dt = new float[Capacity];
        private int _head;   // next write slot
        private int _tail;   // oldest entry
        private int _count;
        private float _deltaSum;
        private float _dtSum;

        // Last raw frame (for immediate backward detection).
        private float _rawSpeed;

        /// <summary>Smoothed forward speed (m/s, signed; + = forward).</summary>
        public float SmoothedSpeed => _dtSum > 1e-6f ? _deltaSum / _dtSum : 0f;

        public RunPlayState State
        {
            get
            {
                if (_rawSpeed < -GameConfig.RunPlayStandstillSpeed) return RunPlayState.Backward;
                return SmoothedSpeed > GameConfig.RunPlayStandstillSpeed
                    ? RunPlayState.Forward
                    : RunPlayState.Still;
            }
        }

        /// <summary>Feed in one frame: signed forward delta (meters) and dt (seconds).</summary>
        public void Push(float signedForwardDelta, float dt)
        {
            if (dt <= 1e-6f) return;
            _rawSpeed = signedForwardDelta / dt;

            // When the ring is FULL, _head == _tail: the slot at _head is the oldest entry.
            // Evict it BEFORE overwriting it, so the sums drop the oldest sample (and not the
            // newly written one). The pointers then advance together and _count stays Capacity.
            if (_count == Capacity)
            {
                _deltaSum -= _delta[_head];
                _dtSum -= _dt[_head];
                _tail = (_tail + 1) % Capacity;
            }
            else
            {
                _count++;
            }

            _delta[_head] = signedForwardDelta;
            _dt[_head] = dt;
            _deltaSum += signedForwardDelta;
            _dtSum += dt;
            _head = (_head + 1) % Capacity;

            // Keep within the window: as long as the oldest entry lies outside the window,
            // remove it - but keep at least ONE entry (otherwise dtSum = 0).
            while (_count > 1 && _dtSum - _dt[_tail] > GameConfig.RunPlaySpeedWindow)
            {
                _deltaSum -= _delta[_tail];
                _dtSum -= _dt[_tail];
                _tail = (_tail + 1) % Capacity;
                _count--;
            }
        }

        /// <summary>Discard the buffer and the last frame (song change, menu, mode change).</summary>
        public void Reset()
        {
            _head = _tail = _count = 0;
            _deltaSum = 0f;
            _dtSum = 0f;
            _rawSpeed = 0f;
        }

        public static float ComputePlayRate(float speed, float njs, float roomScale, float comfort)
        {
            float denom = Mathf.Max(0.01f, njs * Mathf.Max(0.01f, roomScale));
            return speed * comfort / denom;
        }
    }
}

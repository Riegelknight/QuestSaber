// ==============================================================================
//  MetaHaptics - amplitude-envelope haptics for Quest 3 / Touch Plus controllers.
//  A rectangular impulse (SendHapticImpulse) feels like a short "buzz"; the
//  amplitude envelope plays a hard attack followed by a fast decay instead, which
//  feels like a precise "tap" on a cut.
//  STRICTLY additive: whenever the controller does not report the required sample
//  rate (or anything throws), TryPlayEnvelope() returns false and the caller falls
//  back to the proven impulse path - the worst case is"unchanged haptics".
// ==============================================================================
using System;
using UnityEngine;

namespace QuestSaber
{
    /// <summary>Amplitude-envelope haptics with impulse fallback.</summary>
    internal static class MetaHaptics
    {
        /// <summary>Reused sample buffer (the hot path stays allocation-free).</summary>
        private static readonly float[] _samples = new float[GameConfig.HapticEnvelopeSamples];
        private static bool _pathReported;
        private static bool _envelopeUsable = true;

        /// <summary>
        /// Plays <paramref name="amplitude"/> over <paramref name="duration"/> as a
        /// percussive envelope. Returns false if the caller must use the impulse path.
        /// </summary>
        internal static bool TryPlayEnvelope(SaberSide side, float amplitude, float duration)
        {
            if (!_envelopeUsable || !GameConfig.UseAmplitudeEnvelopeHaptics) return false;
            if (amplitude <= 0f || duration <= 0f) return false;

            try
            {
                var controller = ToOvr(side);
                float sampleRate = OVRInput.GetControllerSampleRateHz(controller);
                if (sampleRate < GameConfig.HapticEnvelopeMinSampleRateHz)
                {
                    // Not a failure - just the documented capability gate. Log once so a
                    // device log tells whether the envelope path is actually available
                    // (on a Quest 3 a sample rate of 0 would mean "not reported").
                    if (!_pathReported)
                    {
                        _pathReported = true;
                        Debug.Log("[Meta] Haptics: amplitude envelope not offered (sampleRate=" +
                                  sampleRate + " Hz, required=" + GameConfig.HapticEnvelopeMinSampleRateHz +
                                  " Hz) - impulse path stays in charge.");
                    }
                    return false;
                }

                int count = GameConfig.HapticEnvelopeSamples;
                float last = count - 1;
                for (int i = 0; i < count; i++)
                {
                    float t = last > 0f ? i / last : 0f;
                    _samples[i] = amplitude * Mathf.Pow(1f - t, GameConfig.HapticEnvelopeDecayExponent);
                }

                OVRInput.SetControllerHapticsAmplitudeEnvelope(
                    new OVRInput.HapticsAmplitudeEnvelopeVibration
                    {
                        SamplesCount = count,
                        Samples = _samples,
                        Duration = duration
                    }, controller);

                if (!_pathReported)
                {
                    _pathReported = true;
                    Debug.Log("[Meta] Haptics: amplitude envelope active (sampleRate=" + sampleRate +
                              " Hz, " + count + " samples over " + duration + " s).");
                }
                return true;
            }
            catch (Exception ex)
            {
                // Never retry after a hard API failure: stay on the impulse path.
                _envelopeUsable = false;
                Debug.LogWarning("[Meta] Haptics: amplitude envelope unavailable (" + ex.Message +
                                 ") - impulse path stays in charge.");
                return false;
            }
        }

        private static OVRInput.Controller ToOvr(SaberSide side)
        {
            return side == SaberSide.Left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
        }
    }
}

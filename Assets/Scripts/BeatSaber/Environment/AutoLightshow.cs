// ==============================================================================
//  AutoLightshow - generates a default lightshow for maps WITHOUT their own
//  lightshow files (most custom maps have no _lightshowEvents).
// ==============================================================================
//  Model SuperSaber: its songs have a fixed event table generated per song.
//  Here we build a tasteful 16/32-beat pattern from the existing palette
//  colors (EnvironmentColorTable + map env colors):
//   - Rings/tunnel:      color change red/blue every 8 beats
//   - Back lasers:       antiphase every 8 beats (moving transition)
//   - Lasers left/right: flash every 4 beats, sides offset
//   - Center:            flash on 16th accent
//   - Laser spin:        new direction/stop mix every 16 beats
//   - Boost:             every 2nd phrase (16 beats on, 16 off) -> twister pulse
//
//  Everything in BEATS - the conversion to seconds is handled by EnvironmentClock.
//  Context: architecture see PROJECT_NOTES.md (project root).
// ==============================================================================
using System.Collections.Generic;
using UnityEngine;

namespace QuestSaber.Environment
{
    public static class AutoLightshow
    {
        /// <summary>Fills the parallel lists in the style of the map parser.</summary>
        public static void Populate(List<LightEventType> types, List<float> beats,
                                    List<int> values, List<float> floats,
                                    List<bool> hasColor, List<Color> colors,
                                    List<float> boostBeats, List<bool> boostVals,
                                    float totalBeats)
        {
            const int kRed  = (int)LightEventValue.RedOn;
            const int kBlue = (int)LightEventValue.BlueOn;
            const int kRedF  = (int)LightEventValue.RedFlash;
            const int kBlueF = (int)LightEventValue.BlueFlash;
            const int kOff   = (int)LightEventValue.Off;

            float end = Mathf.CeilToInt(Mathf.Max(totalBeats, 64f) / 16f) * 16f + 32f;
            for (float b = 0f; b < end; b += 4f)
            {
                bool half = (b % 8f) >= 4f;          // phase position within 8
                bool sect = ((b % 16f) >= 8f);       // phase position within 16
                int main = sect ? kRed : kBlue;
                int anti = sect ? kBlue : kRed;

                // Tunnel/rings: clear color every 8 beats.
                if (b % 8f < 0.01f)
                    Add(types, beats, values, floats, hasColor, colors,
                        LightEventType.Rings, b, main);

                // Back lasers: antiphase to the rings, soft transition.
                if (b % 8f < 0.01f)
                    Add(types, beats, values, floats, hasColor, colors,
                        LightEventType.BackLasers, b, anti);

                // Laser fans: flash quarter offset left/right.
                Add(types, beats, values, floats, hasColor, colors,
                    LightEventType.LeftRotatingLasers, b, half ? kBlueF : kRedF);
                Add(types, beats, values, floats, hasColor, colors,
                    LightEventType.RightRotatingLasers, b + 2f, half ? kRedF : kBlueF);

                // Center: only on 16th accent.
                if (b % 16f < 0.01f)
                    Add(types, beats, values, floats, hasColor, colors,
                        LightEventType.CenterLights, b, kRedF);

                // Laser spin: new mix every 16 beats (direction/stop).
                if (b % 16f < 0.01f)
                {
                    int spin = ((b / 16f) % 3f) < 0.5f ? kOff : (sect ? kRed : kBlue);
                    Add(types, beats, values, floats, hasColor, colors,
                        LightEventType.LeftRotationSpeed, b, spin);
                    Add(types, beats, values, floats, hasColor, colors,
                        LightEventType.RightRotationSpeed, b, spin == kOff ? kBlue : kOff);
                }

                // Boost: 16 beats on, 16 beats off -> twister pushes.
                if (b % 32f < 0.01f)
                {
                    boostBeats.Add(b);        boostVals.Add(true);
                    boostBeats.Add(b + 16f);  boostVals.Add(false);
                }
            }
        }

        private static void Add(List<LightEventType> types, List<float> beats,
            List<int> values, List<float> floats, List<bool> hasColor,
            List<Color> colors, LightEventType type, float beat, int value)
        {
            types.Add(type);
            beats.Add(beat);
            values.Add(value);
            floats.Add(1f);
            hasColor.Add(false);
            colors.Add(Color.clear);
        }
    }
}

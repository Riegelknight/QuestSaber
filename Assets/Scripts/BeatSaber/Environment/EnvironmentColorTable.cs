// ==============================================================================
//  EnvironmentColorTable - default color sets per ArcViewer environment.
// ==============================================================================
//  Values 1:1 from ArcViewer's ColorManager (`GetEnvironmentColors`): each environment
//  has its own default light colors (red/blue only in most standard environments).
//  Map-specific colors (`_envColor*`) override these defaults at runtime.
//  Context: architecture see PROJECT_NOTES.md (project root).
// ==============================================================================
using UnityEngine;

namespace QuestSaber.Environment
{
    public static class EnvironmentColorTable
    {
        public static EnvironmentColorPalette Get(string environmentName)
        {
            switch (environmentName)
            {
                case "OriginsEnvironment": return Origins();
                case "KDAEnvironment": return Kda();
                case "TimbalandEnvironment": return Timbaland();
                case "FitBeatEnvironment": return FitBeat();
                case "LinkinParkEnvironment": return LinkinPark();
                case "KaleidoscopeEnvironment": return Kaleidoscope();
                default: return Default(); // Default/Triangle/Nice/BigMirror/Dragons/Monstercat/Panic
            }
        }

        private static EnvironmentColorPalette Default() => new EnvironmentColorPalette
        {
            LightColor1 = new Color(0.85f, 0.085f, 0.085f),
            LightColor2 = new Color(0.1882353f, 0.675294f, 1f),
            BoostLightColor1 = new Color(0.85f, 0.085f, 0.085f),
            BoostLightColor2 = new Color(0.1882353f, 0.675294f, 1f),
        };

        private static EnvironmentColorPalette Origins() => new EnvironmentColorPalette
        {
            LightColor1 = new Color(0.4910995f, 0.6862745f, 0.7f),
            LightColor2 = new Color(0.03844783f, 0.6862745f, 0.9056604f),
            BoostLightColor1 = new Color(0.4910995f, 0.6862745f, 0.7f),
            BoostLightColor2 = new Color(0.03844783f, 0.6862745f, 0.9056604f),
        };

        private static EnvironmentColorPalette Kda() => new EnvironmentColorPalette
        {
            LightColor1 = new Color(1f, 0.3960785f, 0.2431373f),
            LightColor2 = new Color(0.7607844f, 0.1254902f, 0.8666667f),
            BoostLightColor1 = new Color(1f, 0.3960785f, 0.2431373f),
            BoostLightColor2 = new Color(0.7607844f, 0.1254902f, 0.8666667f),
        };

        private static EnvironmentColorPalette Timbaland() => new EnvironmentColorPalette
        {
            LightColor1 = new Color(0.1f, 0.5517647f, 1f),
            LightColor2 = new Color(0.1f, 0.5517647f, 1f),
            BoostLightColor1 = new Color(0.1f, 0.5517647f, 1f),
            BoostLightColor2 = new Color(0.1f, 0.5517647f, 1f),
        };

        private static EnvironmentColorPalette FitBeat() => new EnvironmentColorPalette
        {
            LightColor1 = new Color(0.8f, 0.5594772f, 0.5594772f),
            LightColor2 = new Color(0.5594772f, 0.5594772f, 0.8f),
            BoostLightColor1 = new Color(0.8f, 0.5594772f, 0.5594772f),
            BoostLightColor2 = new Color(0.5594772f, 0.5594772f, 0.8f),
        };

        private static EnvironmentColorPalette LinkinPark() => new EnvironmentColorPalette
        {
            LightColor1 = new Color(0.7529412f, 0.672753f, 0.5925647f),
            LightColor2 = new Color(0.6241197f, 0.6890281f, 0.709f),
            BoostLightColor1 = new Color(0.922f, 0.5957885f, 0.255394f),
            BoostLightColor2 = new Color(0.282353f, 0.4586275f, 0.6235294f),
        };

        private static EnvironmentColorPalette Kaleidoscope() => new EnvironmentColorPalette
        {
            LightColor1 = new Color(0.65882355f, 0.1254902f, 0.1254902f),
            LightColor2 = new Color(0.47058824f, 0.47058824f, 0.47058824f),
            BoostLightColor1 = new Color(0.50196081f, 0f, 0f),
            BoostLightColor2 = new Color(0.49244517f, 0f, 0.53725493f),
        };
    }
}

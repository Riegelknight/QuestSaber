// ==============================================================================
//  EnvironmentParametersTable - environment parameters per ArcViewer environment.
// ==============================================================================
//  Values 1:1 from ArcViewer's EnvironmentManager (`EnvironmentParameters` array in
//  Assets/_Scenes/Previewer.unity): ring rotation/zoom and laser count. Set on
//  environment change in EnvironmentLightManager.CurrentParameters.
//  Context: architecture see PROJECT_NOTES.md (project root).
// ==============================================================================
using System.Collections.Generic;

namespace QuestSaber.Environment
{
    public static class EnvironmentParametersTable
    {
        public const string DefaultEnvironmentName = "DefaultEnvironment";

        /// <summary>All supported environment scenes (Build Settings).</summary>
        public static readonly string[] Supported =
        {
            "DefaultEnvironment",
            "NiceEnvironment",
            "TriangleEnvironment",
            "KDAEnvironment",
            "MonstercatEnvironment",
            "BigMirrorEnvironment",
            "OriginsEnvironment",
            "DragonsEnvironment",
            "PanicEnvironment",
            "FitBeatEnvironment",
            "KaleidoscopeEnvironment",
            "TimbalandEnvironment",
            "LinkinParkEnvironment",
        };

        private static readonly Dictionary<string, EnvironmentLightParameters> Table = Build();

        /// <summary>
        /// Map environments that this app has no scene for, but whose FAMILY it does have
        /// Beat Saber added a second stage for several packs; those names are common
        /// in custom maps, and without a mapping every one of them silently fell back to
        /// DefaultEnvironment - which is why the player only ever saw ONE background
        /// ("es wird nur ein Hintergrund gerendert").
        ///
        /// Only genuine family members are listed here (the name itself says "2") plus the two
        /// Monstercat stages - everything else (GreenDay, Billie, EDM, TheSecond, GlassDesert,
        /// Weave, ...) has no equivalent scene and is reported + played as Default, see
        /// <see cref="Normalize"/>.
        /// </summary>
        private static readonly Dictionary<string, string> FamilyAliases = new Dictionary<string, string>
        {
            { "Dragons2Environment", "DragonsEnvironment" },          // Imagine Dragons 2
            { "Panic2Environment", "PanicEnvironment" },              // Panic! at the Disco 2
            { "LinkinPark2Environment", "LinkinParkEnvironment" },    // Linkin Park 2
            { "Monstercat2Environment", "MonstercatEnvironment" },    // Monstercat 2
            { "CrabRaveEnvironment", "MonstercatEnvironment" },       // Monstercat label
        };

        /// <summary>Environment names already reported to the log (one line per name).</summary>
        private static readonly HashSet<string> ReportedNames = new HashSet<string>();

        /// <summary>Returns the environment's parameters (fallback: default).</summary>
        public static EnvironmentLightParameters Get(string environmentName)
        {
            if (!string.IsNullOrEmpty(environmentName) && Table.TryGetValue(environmentName, out var p))
                return p;
            if (!string.IsNullOrEmpty(environmentName)
                && FamilyAliases.TryGetValue(environmentName, out var alias)
                && Table.TryGetValue(alias, out var ap))
            {
                return ap;
            }
            return Table[DefaultEnvironmentName];
        }

        /// <summary>Returns a valid environment scene name or falls back to default.
        /// Names of known families are mapped to their first stage, everything else is logged
        /// ONCE per name so the game log shows exactly which maps have no matching background
        /// </summary>
        public static string Normalize(string environmentName)
        {
            if (string.IsNullOrEmpty(environmentName)) return DefaultEnvironmentName;
            if (Table.ContainsKey(environmentName)) return environmentName;

            if (FamilyAliases.TryGetValue(environmentName, out var alias))
            {
                if (ReportedNames.Add(environmentName))
                {
                    UnityEngine.Debug.Log("[QuestSaber] Environment '" + environmentName +
                                          "' has no scene of its own in this app - using the same family: " +
                                          alias + ".");
                }
                return alias;
            }

            if (ReportedNames.Add(environmentName))
            {
                UnityEngine.Debug.LogWarning("[QuestSaber] Environment '" + environmentName +
                                             "' is not available in this app (this map was lit for another " +
                                             "stage) - using " + DefaultEnvironmentName + ". Available: " +
                                             string.Join(", ", Supported));
            }
            return DefaultEnvironmentName;
        }

        private static Dictionary<string, EnvironmentLightParameters> Build()
        {
            var t = new Dictionary<string, EnvironmentLightParameters>
            {
                // name,                laser, rnd, step, sSpd, sAmt, sProp, sMax, sAng, sStep, bSpd, bAmt, bProp, bMax, bAng, bStep, zSpd, zClose, zFar, zParity
                { "DefaultEnvironment",     P(4,  true,  0, 2, 90, 1f,    5, -45, 3, 2, 45, 1f,    5, -45, 0, 2, 2,     5,    true) },
                { "NiceEnvironment",        P(4,  true,  0, 2, 90, 1f,    5, -45, 3, 2, 45, 1f,    5, -45, 0, 2, 1,     5,    true) },
                { "TriangleEnvironment",    P(7,  true,  0, 2, 90, 1f,    7,  45, 4, 2, 45, 1f,    5, -45, 0, 2, 5,     10,   true) },
                { "KDAEnvironment",         P(7,  true,  0, 0,  0, 0f,    0,   0, 0, 0,  0, 0f,    0,   0, 0, 0, 0,     0,    true) },
                { "MonstercatEnvironment",  P(5,  true,  0, 2, 90, 1f,    5, -45, 3, 2, 45, 1f,    5, -45, 0, 2, 7,     7,    true) },
                { "BigMirrorEnvironment",   P(4,  true,  0, 2, 90, 1f,    5, -45, 3, 2, 45, 1f,    5, -45, 0, 2, 2,     5,    true) },
                { "OriginsEnvironment",     P(5,  true,  0, 2,  0, 1f,    0,   0, 0, 2, 90, 1f,    3, -45, 0, 0, 5,     5,    true) },
                { "DragonsEnvironment",     P(5,  true,  0, 3, 90, 0.5f, 15,  90, 5, 2, 45, 1f,    5, -45, 0, 4, 0.25f, 1.75f,true) },
                { "PanicEnvironment",       P(7,  true,  0, 2, 90, 1f,   20,   0, 5, 2, 45, 1f,    5, -45, 0, 2, 0.25f, 1.5f, true) },
                { "FitBeatEnvironment",     P(8,  false,25, 3, 90, 0.5f, 15,  90, 5, 2, 90, 1f,    5,  45, 0, 4, 0.25f, 1.75f,true) },
                { "KaleidoscopeEnvironment",P(0,  true,  0, 6, 90, 0.75f,18,   0,45, 6, 90, 0.75f,10,   0,45, 4, 1,     3,    true) },
                { "TimbalandEnvironment",   P(10, false,25, 2, 45, 1f,    3,   0, 0, 2,  0, 1f,    0,   0, 0, 2, 2,     4,    true) },
                { "LinkinParkEnvironment",  P(18, true,  5, 2, 90, 1f,    5, -45, 3, 2, 45, 1f,    5, -45, 0, 2, 2,     5,    true) },
            };
            return t;
        }

        private static EnvironmentLightParameters P(
            int laserCount, bool randomize, float step,
            float sSpeed, float sAmount, float sProp, float sMaxStep, float sStartAngle, float sStartStep,
            float bSpeed, float bAmount, float bProp, float bMaxStep, float bStartAngle, float bStartStep,
            float zoomSpeed, float closeStep, float farStep, bool zoomParity)
        {
            return new EnvironmentLightParameters
            {
                RotatingLaserCount = laserCount,
                RandomizeRotatingLasers = randomize,
                RotatingLaserStep = step,
                SmallRingRotationSpeed = sSpeed,
                SmallRingRotationAmount = sAmount,
                SmallRingRotationProp = sProp,
                SmallRingMaxStep = sMaxStep,
                SmallRingStartAngle = sStartAngle,
                SmallRingStartStep = sStartStep,
                BigRingRotationSpeed = bSpeed,
                BigRingRotationAmount = bAmount,
                BigRingRotationProp = bProp,
                BigRingMaxStep = bMaxStep,
                BigRingStartAngle = bStartAngle,
                BigRingStartStep = bStartStep,
                ZoomSpeed = zoomSpeed,
                CloseZoomStep = closeStep,
                FarZoomStep = farStep,
                StartRingZoomParity = zoomParity,
            };
        }
    }
}

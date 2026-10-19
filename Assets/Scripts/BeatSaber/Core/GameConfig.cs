using UnityEngine;

namespace QuestSaber
{
    /// <summary>
    /// Central, tunable game constants (speeds, grid, settings).
    /// Grid, offsets and jump timing come 1:1 from the reference viewer ArcViewer.
    /// </summary>
    public static class GameConfig
    {
        // ---------- Layers (see ProjectSettings/TagManager.asset) ----------
        public const int NoteLayer = 7;
        public const int SaberLayer = 8;
        public const int WallLayer = 9;

        // ---------- Disposal behind the player ----------
        /// <summary>Wall disposal line behind the cutting plane (base units): as soon as a
        /// wall's back edge leaves this value behind, the wall is collected (see WallView).
        /// Notes/arcs/chains are collected at the separate <see cref="NoteMissZ"/> line, where
        /// the "missed" message is produced. Deliberately only the back of the scene, no
        /// position offset.</summary>
        public const float DestroyZ = -5f;

        /// <summary>Disposal line for note-like objects (notes/arcs/chains, base units):
        /// behind this Z they are collected and the "missed" message is produced.</summary>
        public const float NoteMissZ = -2f;

        // ---------- Grid (basis from ArcViewer: ObjectManager + scene containers) ----------
        // The row heights are ABSOLUTE local Y values. There is NO container offset
        // and no coupling to the head-set height.
        /// <summary>Left edge of the grid in X (ArcViewer ObjectManager.GridBottomLeft).</summary>
        public const float GridLeftX = -0.9f;
        /// <summary>Column spacing: 4 columns of 0.6 m (x = -0.9..0.9).</summary>
        public const float LaneWidth = 0.6f;
        /// <summary>Wall height per grid unit (ArcViewer ObjectManager.WallHScale).</summary>
        public const float WallHScale = 0.6f;
        /// <summary>Vertical offset of the wall container (ArcViewer scene: container "Walls"
        /// y = 0.1). The wall center is formed from grid row × WallHScale + height/2; this
        /// offset is added on top of that (WallManager.UpdateVisual).</summary>
        public const float WallContainerOffsetY = 0.1f;
        /// <summary>Smallest wall size (ArcViewer WallManager.MinWallSize, meters).</summary>
        public const float MinWallSize = 0.06f;
        public static readonly float[] RowHeights = { 0.85f, 1.40f, 1.95f };

        // ---------- "Field height" (vertical offset for everything except the floor) ----------
        // Adjustable via the menu row "Field height" (persistent in SaveData.FieldHeightMeters).
        // Raises notes/mines AND all walls above the playfield floor (notes in NoteView, walls in
        // WallView; every frame -> live). The floor stays; floor-standing walls are extended downward
        // to the floor. The ENTIRE playfield (incl. walls), by contrast, is raised/lowered by the left
        // stick (GameManager._floorOffsetMeters).
        /// <summary>Vertical offset of all objects above the playfield floor (meters).</summary>
        public static float FieldHeightOffsetMeters = 0f;
        /// <summary>Step size of the menu row "Field height" (meters).</summary>
        public const float FieldHeightStepMeters = 0.05f;
        /// <summary>Lower/upper limit of the "field height" (meters).</summary>
        public const float FieldHeightMinMeters = -1.5f;
        public const float FieldHeightMaxMeters = 1.5f;

        // ---------- Jump/spawn timing (1:1 from ArcViewer: JumpManager/BeatmapManager) ----------
        /// <summary>Default half-jump duration in beats (ArcViewer BeatmapManager.DefaultHJD).</summary>
        public const float HalfJumpDurationDefault = 4f;
        /// <summary>Limit of the jump distance (meters): The default HJD is halved until
        /// the distance lies below it (ArcViewer: `GetJumpDistance > 35.998`).</summary>
        public const float MaxJumpDistance = 35.998f;
        /// <summary>Lower limit of the half-jump duration after the map offset (beats).</summary>
        public const float MinHalfJumpDuration = 0.25f;

        // ---------- Note movement EFFECTS from the pattern (ArcViewer/SuperSaber) ----------
        // NOT every note gets an effect: Normal notes fly straight. Only when the
        // pattern dictates it (multiple notes of the same time/column = stack; crossed
        // doubles = red right/blue left) does the note change its position during approach.
        // 1:1 ArcViewer (ObjectManager.GetStartY/GetFlipYHeights + NoteManager.UpdateVisual).
        /// <summary>Height spacing of stacked notes on spawn (ArcViewer `StartYSpacing`).</summary>
        public const float StackSpacing = 0.6f;
        /// <summary>Side flip: vertical offset of the "upper" note (ArcViewer `flipYHeightOver`).</summary>
        public const float FlipYOver = 0.45f;
        /// <summary>Side flip: vertical offset of the "lower" note (ArcViewer `flipYHeightUnder`).</summary>
        public const float FlipYUnder = -0.15f;

        /// <summary>
        /// Spawn parabola of the stack animation (ArcViewer `JumpManager.SpawnParabola`): The
        /// note starts at <paramref name="baseHeight"/> (below its row) and reaches
        /// <paramref name="targetHeight"/> exactly at the beat, when t (Z position) runs to 0.
        /// For baseHeight == targetHeight the result is constantly the target height - normal
        /// notes therefore fly straight (no effect).
        /// </summary>
        public static float SpawnParabola(float targetHeight, float baseHeight, float halfJumpDistance, float t)
        {
            float d = halfJumpDistance > 0.0001f ? halfJumpDistance : 0.0001f;
            float range = targetHeight - baseHeight;
            return -(range / (d * d)) * (t * t) + targetHeight;
        }

        /// <summary>Default NJS per difficulty rank (1..5) when the map specifies no NJS
        /// - ArcViewer BeatmapManager: Easy/Normal/Hard 10, Expert 12, Expert+ 16.</summary>
        public static float DefaultNjsForRank(int rank)
        {
            switch (rank)
            {
                case 4: return 12f;
                case 5: return 16f;
                default: return 10f;
            }
        }

        /// <summary>
        /// Z position of the note/wall reference plane (the note centre; only WallView
        /// derives the wall front edge from it) at song time - CONTINUOUS
        /// flight with the map tempo (NJS from the song data). At the beat Z is exactly 0;
        /// before the beat positive (far away), after it negative (behind the player).
        /// There is deliberately NO fast approach phase anymore (no MoveZ/MoveTime):
        /// The notes fly uniformly, determined solely by the map's NJS/HJD.
        /// </summary>
        public static float JumpZ(float hitTime, float songTime, float njs)
        {
            return (hitTime - songTime) * njs;
        }

        /// <summary>Time between spawn and beat (seconds): the full jump duration
        /// (2 × ReactionTime), so the note arrives exactly at the beat position.</summary>
        public static float SpawnLeadTime(float reactionTime) => 2f * reactionTime;

        // ---------- Room scaling (ONLY size setting) ----------
        /// <summary>Lower/upper limit of the room scaling (1 = reference size).</summary>
        public const float ScaleMin = 0.5f;
        public const float ScaleMax = 2f;
        /// <summary>Step size in the menu ("Scaling").</summary>
        public const float ScaleStep = 0.05f;
        /// <summary>Reference size for migrating old saves: 1.75 m corresponds to scaling 1.0.</summary>
        public const float ScaleBaseHeightCm = 175f;

        public static float HeightCmToScale(float heightCm)
        {
            if (heightCm <= 1f) return 1f;
            return Mathf.Clamp(heightCm / ScaleBaseHeightCm, ScaleMin, ScaleMax);
        }

        // ---------- Room orientation ----------
        // Right stick left/right = room yaw angle; left stick up/down =
        // session floor offset. Both only as long as the song list is NOT open.
        // The floor offset is SESSION-only: starts at 0 on every game start, is NOT
        // saved and NOT touched by button A (A only sets room X/Z under the head
        // and the rotation to the view direction - the height stays, see RecenterRoomOnHead).
        /// <summary>Rotation speed of the room at full stick deflection (degrees/second).</summary>
        public const float RoomYawDegPerSecond = 90f;
        /// <summary>Raise/lower speed of the session floor offset at full
        /// stick deflection (meters/second).</summary>
        public const float FloorOffsetMetersPerSecond = 1.2f;
        /// <summary>Deadzone of the right stick for the room rotation.</summary>
        public const float RoomAdjustDeadzone = 0.15f;
        /// <summary>Deadzone of the LEFT stick for the floor offset - deliberately LARGER so
        /// that an accidental/light deflection does not shift anything.</summary>
        public const float FloorOffsetDeadzone = 0.35f;

        // ---------- "Run and Play" (run/tempo coupling) ----------
        // Optional mode: The game AND audio speed is coupled to the physical
        // forward movement of the room. "Room follows player" is then forcibly
        // on and locked. Formula (see GameManager/`RunAndPlay`):
        //     playRate = v_buffer × songFactor × comfort
        //     songFactor = 1 / (_roomScale × NJS)
        // With comfort = 1 the notes are EXACTLY static in the real room (the player runs
        // toward them), for every song and every room scaling; the factor falls with the
        // note speed (faster songs = lower multiplier).
        /// <summary>Deadzone of the forward speed (m/s): below it counts as "standstill"
        /// and the song pauses completely (notes freeze).</summary>
        public const float RunPlayStandstillSpeed = 0.12f;
        /// <summary>Buffer window (seconds) for the smoothed average of the
        /// room speed (concept: 0.3 s) - prevents abrupt tempo changes.</summary>
        public const float RunPlaySpeedWindow = 0.3f;
        public const float RunComfortMin = 0.2f;
        public const float RunComfortMax = 3.0f;
        public const float RunComfortDefault = 1.0f;
        /// <summary>Step size of the menu slider "Run tempo" (0.1 steps).</summary>
        public const float RunComfortStep = 0.1f;
        /// <summary>Limits of the actual playback rate (AudioSource.pitch). The
        /// lower limit keeps the sound barely audible; the upper limit is the Unity pitch limit.</summary>
        public const float RunPlayMinRate = 0.05f;
        public const float RunPlayMaxRate = 3.0f;
        /// <summary>Deadzone (meters) for the room follow in Run-and-Play mode: below it
        /// the room also "freezes", so standstill is really still.</summary>
        public const float RunPlayDeadzoneMeters = 0.002f;

        // ---------- Cutting settings ----------
        // Direction rule according to BeatSaberClone (`BoxView.CheckSliceDirection`) and
        // OpenSaberPlus (`BeepCube.cut`): The angle between the smoothed
        // saber tip velocity (`SaberView.TipVelocity`, 5-frame average at the
        // TIP - during wrist swings the root barely moves) and the
        // TARGET DIRECTION must stay below `CutAllowedAngle`, otherwise bad cut. Target direction = the visibly
        // displayed arrow direction of the note (local -Y; the note carries the
        // display rotation `NoteView.TargetRotationZ` - the score layer reads exactly
        // the same rotation as the eye, the 45° diagonals result from that by
        // themselves). LIKE OPEN-SABER-PLUS the DEPTH IS PROJECTED AWAY (only the local x/y
        // of the movement counts): the component INTO the note must not be penalized.
        // Dot notes (8/9): every direction valid. Movement below `CutMinSpeed` =
        // touch without cut (note stays alive). Color (hand) is checked separately.
        // The direction decides the POINTS only: EVERY contact still smashes the note
        // visually (see NoteView.TrySlice) - a wrong direction is a bad cut = "MISS"
        // (combo break, no points), never a surviving note.
        /// <summary>Maximum angle (degrees) between saber velocity and
        /// target direction, from which a swing counts as WRONG (bad cut = miss).</summary>
        /// <remarks>
        /// <b>60°.</b> The swing has to run substantially along the arrow.
        /// A CARDINAL swing on a DIAGONAL note (45° off) still counts as a cut; a
        /// perpendicular or opposite swing (&gt; 60°) is a miss.
        /// History: the value was 45° until an early device log showed a wave of bad cuts
        /// at 46-90°, which led to the lax 90° half-plane rule ("Beat Saber `dot &gt; 0`",
        /// our former behaviour). That wave was NOT a wrong threshold but the WRONG SABER
        /// MOUNT of that time: the anchor/model twist rotated the measured tip velocity by
        /// ~38° (see PROJECT_NOTES §3 "Controller pose: AIM vs GRIP"). With the mount
        /// corrected the measurement is correct again, and a device report
        /// showed the consequence of the lax rule: "notes can be hit from sides
        /// where the arrow does not point" - because within ±90° the score is direction
        /// INDEPENDENT (it rates the swing arc, PROJECT_NOTES §7.7) and thus paid full
        /// points for a swing 80° beside the arrow. The strict rule is therefore back.
        /// Every hit logs its angle and this limit (`[Hit] ... angle=... limit=...`,
        /// <see cref="LogHitDiagnostics"/>), so the value can be re-tuned from device data.
        /// </remarks>
        public const float CutAllowedAngle = 60f;
        /// <summary>Lower limit of the saber TIP speed (m/s, world) below which a contact
        /// counts as "touch without cut": the note stays alive and no points are given -
        /// deliberately NOT a bad cut. Value from Super Saber
        /// (<c>saber-controls.strokeMinSpeed</c> = 0.002 m/ms = 2.0 m/s), measured the same
        /// way (tip distance over a 5-frame window), so the two are directly comparable.
        /// A merely resting hand still reports 0.04-0.13 m/s of tremor, so the former
        /// 0.15 m/s sat exactly on the noise: light touches randomly exceeded it and were
        /// consumed as wrong-direction bad cuts (no points, combo break). A real swing is
        /// far above this (device median ~18 m/s).</summary>
        public const float CutMinSpeed = 2.0f;
        public const float SlicePieceSpeed = 2.2f;
        /// <summary>Own rotation (tumbling) of the cut halves in rad/s (slightly random).</summary>
        public const float SlicePieceSpin = 6f;
        public const float DestroyDelayTime = 0.3f;   // Lifetime of the cut halves (seconds)
        public const float NoteSize = 0.5f;

        public const float SaberBladeRadius = 0.017f;
        public const float HitBoxPadding = SaberBladeRadius;
        public const float HitGateForward = 1.7f;

        // ---------- Saber mount (relative to the controller anchor) ----------
        // The saber root sits ON the controller anchor. The anchor is the rig's controller
        // anchor, which the XRI position action drives with a Vector3Fallback composite whose
        // FIRST binding wins: <XRController>{Side}/pointerPosition -> OpenXR "Pointer" = AIM
        // pose. That is exactly the frame Meta's Touch Plus model is authored in: measured with
        // Assets/Editor/ControllerModelMeasure.cs, the model ORIGIN is the aim point and its
        // body extends back/down - so anchor-local and model-local values are the same frame.
        //
        // Every value below is MEASURED from that model instead of guessed, which is what makes
        // the blade lie along the grip and the saber grip sit in the hand:
        //   * the controller's long axis (= grip axis) points 37.7 deg up from the aim forward
        //     direction and ~10 deg sideways  -> SaberMountPitchUpDeg / SaberMountYawDeg.
        //   * the hand (grip pose = devicePosition) lies 0.0509 m from the aim origin along that
        //     same long axis (constant magnitude, see Game-Logs/spiel_debug_log.txt)
        //     -> SaberMountLocalOffset puts the saber root into the hand.
        //   * yaw and the lateral offset are MIRRORED per side (+left / -right), like the models.
        //
        // Headset report: the blade is NOT aligned with the controller's long axis any
        // more, it points FORWARD - which is also what the mirroring test below shows:
        // yaw = +10.2 turned BOTH blades INWARD (it followed the handle, and the handle leans
        // toward the body), which is exactly the reported "nach innen gedreht".
        //   * SaberMountYawDeg 10.2 -> 0: blades parallel to the aim forward direction.
        //   * SaberMountPitchUpDeg 37.7 -> 47.7: "ca. 10 Grad weiter nach oben".
        //   * the saber root additionally follows the controller MODEL offset
        //     (ControllerModelLocalOffset, see SaberView.ApplyMount), so the hilt keeps sitting
        //     centred in the (newly aligned) virtual controller grip - "mittig im Griff".
        // The CONTROLLER MODEL is DONE as of 2026-10-18 ("so wie die Controller aktuell sind,
        // sind sie perfekt"): its offset/rotation/scale are plain baked constants now and the
        // calibration page no longer touches them. What stays tunable in the headset are the
        // SABERS, in all six degrees of freedom: "Saber X / Y / Z" (position in the anchor frame)
        // plus "Saber pitch / yaw / roll" (direction), see GameConfig.SaberAlign* below.
        //
        // Sign convention of the blade direction (see SaberView.ApplyMount): the blade leaves the
        // root at elevation = PitchUpDeg above the anchor's forward axis and azimuth = YawDeg
        // (mirrored per side), i.e. 0 / 0 = straight forward, +90 = straight up.
        public static float SaberMountPitchUpDeg = 47.7f;
        public static float SaberMountYawDeg = 0f;
        /// <summary>Baked from the headset run of 2026-10-17 (the player turned the blades
        /// roll +1 deg, then reported the remaining error as done).</summary>
        public static float SaberMountRollDeg = 1f;
        /// <summary>Saber root offset in the anchor (aim) frame, in meters. Default = the
        /// aim->grip vector (the aim origin is in front of the hand), so the saber grip lands
        /// where the controller is held. X is mirrored per side.
        /// The controller model's offset (ControllerModelLocalOffset + AlignOffset) is
        /// added on top of this at mount time (SaberView.ApplyMount), so the saber automatically
        /// follows the aligned virtual controller instead of drifting out of its grip.</summary>
        public static Vector3 SaberMountLocalOffset = new Vector3(-0.0071f, -0.0311f, -0.0397f);

        // ---------- Virtual controller model (debug view: "Controllers & sabers") ----------
        // The Meta Touch Plus model is authored for the AIM pose of the anchor: measured
        // (bounds + vertex cloud, Assets/Editor/ControllerModelMeasure.cs) its origin IS the aim
        // point, the body extends ~72 mm back / ~73 mm down from it, and the real aim->grip
        // distance (0.0509 m) lands inside the handle. The model is therefore simply a child of
        // the anchor at identity - exactly like the Meta SDK places it on its OVR hand anchors.
        // (Driving it from devicePosition/deviceRotation instead rotated it by the aim<->grip
        // angle AND pushed it back into the wrist - it looked twisted and too small.
        // The "Model pose" row that switched between AIM / AIM+GRIP / raw GRIP is GONE: AIM is
        // the only pose that matches the Meta model AND the anchor the saber is mounted on, so
        // the switch only ever produced wrong-looking comparisons.)
        /// <summary>Offset of the model in the anchor (aim) frame, in meters. X is mirrored per
        /// side (like every other per-side value here) - see ControllerDebugView.AlignedOffset.
        /// The values the player dialled in inside the headset are baked in here
        /// ("abnullen"), so the calibration rows read 0 and only carry the fine deltas.
        /// Round 1: x -10, y +5, z +5 ("damit klappt es"). Round 2 (2026-10-17): a further
        /// +2 / -5 / -4 mm - i.e. the whole model 5 mm further down, which is what the player
        /// was after with "die Controller sitzen noch zu hoch".</summary>
        public static Vector3 ControllerModelLocalOffset = new Vector3(-0.008f, 0.000f, 0.001f);
        /// <summary>Rotation of the model in the anchor (aim) frame, in degrees. Yaw is mirrored
        /// per side. Baked from the same headset calibration (yaw -2.5 deg).</summary>
        public static Vector3 ControllerModelLocalEuler = new Vector3(0f, -2.5f, 0f);
        /// <summary>Uniform scale of the model (1 = SDK import scale as-is). The measured
        /// model is 126 x 65 x 52 mm, i.e. the real controller's size, so 1 was the value - but
        /// the headset check found it "ca. 5 Prozent groesser" (1.05), and the check of
        /// 2026-10-17 pulled it back to 0.99. The "Model scale" row is GONE, so this is a
        /// plain constant again - re-check it in the headset before changing it.</summary>
        public static float ControllerModelScale = 0.99f;

        // ---------- In-headset alignment ----------
        // The virtual controller MODELS are finished (headset check of 2026-10-18: "so wie die
        // Controller aktuell sind, sind sie perfekt"). Their alignment - found in the headset in
        // two rounds - is BAKED into the constants above and there is no longer a menu row that
        // could move them: the in-headset calibration page only adjusts the SABERS (below).
        // Removed in this round, all of them levers for the model or for one axis twice:
        // "Model X / Y / Z", "Model yaw" (the model's yaw and the blade's yaw were two rows on
        // the same axis - the model variant is baked at -2.5 deg in ControllerModelLocalEuler)
        // and "Saber grip" (a slide along the blade axis, replaced by "Saber X / Y / Z").

        // ---------- In-headset saber alignment: 6 degrees of freedom ----------
        // Six DELTAS on top of the measured mount above, all starting from the baked values:
        // three for the POSITION of the saber root in the anchor (aim) frame and three for the
        // DIRECTION of the blade. Sign convention exactly like every other per-side value in
        // this file: X and yaw are MIRRORED (+left / -right), Y and Z are not.
        /// <summary>Fine position of the saber root in the anchor (aim) frame, in meters (delta
        /// on top of <see cref="SaberMountLocalOffset"/>). X = lateral (mirrored per side),
        /// Y = up/down, Z = forward/back, so "move the grip down" is a plain Y nudge now instead
        /// of a slide along the tilted blade axis - that is what the "Saber X / Y / Z" rows are
        /// for. Baked: the old "Saber grip" slide of -5 mm along the blade (mount angle 47.7 deg)
        /// converts to exactly down 3.70 mm + back 3.37 mm, so this is the same pose as before -
        /// the rows only carry the fine deltas on top and read ~-4 / -3 mm.</summary>
        public static Vector3 SaberAlignOffset = new Vector3(0f, -0.0037f, -0.0034f);
        /// <summary>Tilt of the blade further up/down, in degrees (delta).</summary>
        public static float SaberAlignPitchDeg = 0f;
        /// <summary>Turn of the blade left/right, in degrees, mirrored per side (delta).
        /// Positive = further inward on the left hand (see the mirroring note above).</summary>
        public static float SaberAlignYawDeg = 0f;
        /// <summary>Roll of the blade (label orientation), in degrees (delta).</summary>
        public static float SaberAlignRollDeg = 0f;
        /// <summary>Nudge range of the calibration page (clamped, not wrapped): +-50 mm for the
        /// saber position, +-20 deg for the angles. Steps are 1 mm / 1 deg
        /// ("1er-Schritte"), see GameManager.Menu.</summary>
        public const float SaberAlignRangeMeters = 0.05f;
        public const float AlignRangeDegrees = 20f;

        // ---------- Engraved grip label (player name / best score on the grip) ----------
        // The label is a text mesh that WRAPS around the saber grip. For it to lie ON the
        // grip surface, the wrap radius must equal the grip's real surface radius - it used to
        // be a guessed constant (0.01875 m), which left the text visibly floating ~1.9 mm
        // above the handle.
        // Measured with Assets/Editor/SaberGripMeasure.cs (editor tool, not shipped):
        //   ARC "Handle" mesh = 40 x 40 x 160 mm prism, grip localScale (1.25,1.25,2.5),
        //   grip rotation 270 deg about X (mesh Z -> saber root Y), root localScale (0.75,0.5,0.75)
        //   => every grip vertex lies at radius 22.5 mm in root-local space
        //   => world grip radius = 22.5 mm * 0.75 = 16.875 mm.
        // Changing the saber scales or the handle model means re-running that tool.
        public const float SaberRootScaleZ = 0.75f;          // SaberView root localScale.z
        public const float GripSurfaceRadiusWorld = 0.016875f; // measured (see above)
        /// <summary>Tiny lift above the surface. It must stay above the label's own light-edge
        /// offset (0.5 mm inward, see CreateLabel) - otherwise the bright edge would sink into
        /// the grip and the engraving would lose its edge. 0.7 mm keeps both layers just outside
        /// the handle while looking flush (it used to float 1.875 mm above it).</summary>
        public const float GripLabelLiftWorld = 0.0007f;
        /// <summary>Radius the label must wrap at (world meters) - grip surface + lift.</summary>
        public const float GripLabelRadiusWorld = GripSurfaceRadiusWorld + GripLabelLiftWorld;
        /// <summary>Label anchor offset along the saber root's -Z, in ROOT-LOCAL units
        /// (the anchor is scaled by SaberRootScaleZ on its way to world space).</summary>
        public const float GripLabelAnchorZ = -GripLabelRadiusWorld / SaberRootScaleZ;
        // Diagnostics: [Hit]/[Miss]/[Ignore] lines in logcat (per hit).
        // Logging is OFF by default: every line is an auto-flushed synchronous file write on the MAIN
        // thread, which shows up as frame hitches (see the "hitches=" field in the log
        // heartbeat). Turn it on only while chasing a specific hit problem.
        /// <summary>Per-hit diagnostics ([Hit]/[Ignore]/[Score] with the measured direction
        /// angle). ON for the direction-tuning round (the MrSaber reference also runs
        /// with it ON); switch off again once the angle data has been confirmed.</summary>
        public static readonly bool LogHitDiagnostics = true;

        // ---------- Controller haptics (vibration feedback) ----------
        // Relative intensities: light < notes < mines. All values tunable centrally here.
        // Note: amplitudes deliberately well above the perception threshold - small values
        // (< ~0.2) are barely felt on the Quest controllers. Saber↔Note is the
        // most important feedback (every cut) and therefore designed strongest (0.95).
        /// <summary>LIGHT: saber↔saber and saber↔wall (during contact).</summary>
        public const float HapticLightAmplitude = 0.35f;
        /// <summary>NOTE BLOCKS: clearly stronger than "light" (hit primary feedback).</summary>
        public const float HapticNoteAmplitude = 0.95f;
        public const float HapticNoteDuration = 0.14f;
        /// <summary>MINES (bombs): strong + short after-vibration.</summary>
        public const float HapticMineAmplitude = 1.0f;
        public const float HapticMineDuration = 0.20f;
        public const float HapticMineAfterDelay = 0.10f;    // delay until the after-vibration
        public const float HapticMineAfterAmplitude = 0.60f;
        public const float HapticMineAfterDuration = 0.10f;
        /// <summary>Contact vibration (saber↔saber/wall): pulse duration and minimum interval between two pulses.</summary>
        public const float HapticContactPulseDuration = 0.10f;
        public const float HapticContactInterval = 0.06f;

        // ---------- Quest 3: haptics as an amplitude envelope (see MetaHaptics.cs) ----------
        // A rectangular impulse (SendHapticImpulse) feels like a short "buzz"; the
        // Quest 3 controllers can play an amplitude ENVELOPE instead - a hard attack
        // followed by a fast decay feels like a precise "tap" for a cut. Only used
        // when the controller reports the required sample rate; otherwise the
        // existing impulse path stays in charge (see MetaHaptics.TryPlayEnvelope).
        /// <summary>Use the amplitude-envelope haptics where the controller supports it.</summary>
        public static readonly bool UseAmplitudeEnvelopeHaptics = true;
        /// <summary>Envelope samples, played evenly over the impulse duration.</summary>
        public const int HapticEnvelopeSamples = 12;
        /// <summary>Decay exponent of the envelope (1 = linear, &gt; 1 = more percussive).</summary>
        public const float HapticEnvelopeDecayExponent = 2.2f;
        /// <summary>Minimum controller sample rate (Hz) required for the envelope path.</summary>
        public const float HapticEnvelopeMinSampleRateHz = 200f;

        /// <summary>Base compensation of the score/note clock relative to the audio clock (ms).
        /// It delays the NOTES relative to the music (counterpart to the audio offset, which
        /// shifts the MUSIC). The game is in sync when
        /// <c>AudioOffset(ms) == AudioLatencyBaseMs</c> - both sides shift their
        /// source by the same amount, <see cref="AudioOffsetBaseMs"/> (both together)
        /// cancels out in the process. The "Audio offset" slider therefore starts at 0, and the
        /// 85 ms calibrated on the PICO 4 are stored here (no longer as a hard offset):
        /// earlier base 190 + offset -85 => 190 - (-85) = 275.
        /// (The 85 ms are an empirical value measured on the old hardware; on the
        /// Quest 3 readjust as needed via the "Audio offset" slider.)</summary>
        public const float AudioLatencyBaseMs = 275f;

        public const float AudioOffsetMinMs = -300f;
        /// <summary>Largest possible audio offset (ms).</summary>
        public const float AudioOffsetMaxMs = 300f;
        /// <summary>Step size of the menu row per (-)/(+) trigger (ms).</summary>
        public const float AudioOffsetStepMs = 5f;
        public const float AudioOffsetBaseMs = 300f;

        public const int SparkCount = 18;
        /// <summary>Emission cone (degrees) around the swing direction.</summary>
        public const float SparkSpreadAngle = 22f;
        /// <summary>Initial speed of the sparks (m/s, × room scaling).</summary>
        public const float SparkSpeedMin = 1.4f, SparkSpeedMax = 2.6f;
        /// <summary>Lifetime of the sparks (s).</summary>
        public const float SparkLifetimeMin = 0.2f, SparkLifetimeMax = 0.4f;
        /// <summary>Particle size (m) - small, bright streaks.</summary>
        public const float SparkSizeMin = 0.006f, SparkSizeMax = 0.010f;
        /// <summary>Gravity of the sparks (1 = earth gravity; deliberately almost weightless).</summary>
        public const float SparkGravity = 0.15f;
        /// <summary>HDR boost of the spark color (so the URP bloom makes them glow).</summary>
        public const float SparkColorBoost = 2.5f;
        public const float SparkForwardBias = 0.42f;

        public const float WallSparkRate = 180f;
        public const float ContactSparkLifetimeMin = 0.25f, ContactSparkLifetimeMax = 0.55f;
        /// <summary>Initial speed of the contact sparks (m/s, × room scaling).</summary>
        public const float ContactSparkSpeedMin = 1.2f, ContactSparkSpeedMax = 2.6f;
        public const float ContactSparkSizeMin = 0.005f, ContactSparkSizeMax = 0.009f;
        /// <summary>Gravity of the contact sparks (1 = earth): pulls them down after the radial spray.</summary>
        public const float ContactSparkGravity = 1.5f;

        public const float MineColorBoost = 2.2f;
        /// <summary>Spark color RED (HDR: note color × <see cref="SparkColorBoost"/>) -
        /// overdriven past 1.0 so the URP bloom makes the streaks glow.</summary>
        public static readonly Color SparkColorRed = new Color(0.7843f, 0.0784f, 0.0784f, 1f) * SparkColorBoost;
        /// <summary>Spark color BLUE (HDR, as above).</summary>
        public static readonly Color SparkColorBlue = new Color(0.1569f, 0.5569f, 0.8235f, 1f) * SparkColorBoost;
        /// <summary>Mine detonation: warm HDR white as the base tint; the glow stages
        /// (white -> yellow -> orange -> red) are produced by the volume shader in
        /// <c>Fx.CreateMineFireball</c>.</summary>
        public static readonly Color MineFlashColor = new Color(1f, 0.95f, 0.9f, 1f) * MineColorBoost;
        /// <summary>Saber clash (saber on saber): white spark fountain (HDR, bloom).</summary>
        public static readonly Color ClashSparkColor = Color.white * SparkColorBoost;

        // Point distribution = original Beat Saber scheme: 70 pre-swing + 30 follow-through + 15 center
        // = 115 base points per note. But MEASUREMENT happens as in OpenSaberPlus (`LightSaber.gd`,
        // ±200 ms window of the BLADE DIRECTION) instead of via SuperSaber's saturated plane angles
        // - that is why straight and diagonal cuts of the same quality count the same.
        public const float PreSwingMax = 70f;         // Pre-swing
        public const float FollowThroughMax = 30f;    // Follow-through
        public const float CenterMax = 15f;           // Center (exactly through the note center)
        /// <summary>Pre-swing: full points from 100° change of the blade direction
        /// (OpenSaberPlus `TARGET_PRESWING_ANGLE`).</summary>
        public const float PreSwingFullAngle = 100f;
        /// <summary>Follow-through: full points from 60° (OpenSaberPlus `TARGET_FOLLOWTHROUGH_ANGLE`).</summary>
        public const float FollowThroughFullAngle = 60f;
        /// <summary>Measurement window before/after the CUT MOMENT (blade contact) in which the greatest
        /// change of the blade direction is counted (OpenSaberPlus `preswing_time`/
        /// `followthrough_time` = 200 ms). Measured relative to the blade at the cut moment.</summary>
        public const float SwingMeasureWindow = 0.2f;
        /// <summary>Distance note center ↔ blade line (base units) at which the
        /// center component becomes 0 (before that linearly full -> 0). Ratio 0.3 × note size
        /// (Beat Saber `cutDistanceToCenter`).</summary>
        public const float CenterDistanceMax = NoteSize * 0.3f;

        /// <summary>Link of a chain: fixed base value 20 (× combo multiplier), without
        /// angle/center rating (ArcViewer `ScoringUtils.MaxChainLinkScore`).</summary>
        public const float ChainLinkScore = 20f;

        // ---------- Arcs & Chains - visuals (1:1 ArcViewer ArcManager/ChainManager) ----------
        /// <summary>Width of the arc band in base units (ArcViewer `arcwidth` 0.5 / 2).</summary>
        public const float ArcWidth = 0.25f;
        /// <summary>Points per second of arc length (ArcViewer `arcdensity` = 60), limited to
        /// sensible upper/lower limits.</summary>
        public const int ArcSegmentsPerSecond = 60;
        public const int ArcMinPoints = 30;
        public const int ArcMaxPoints = 300;
        /// <summary>Distance of the Bézier control points from the head/end point at multiplier 1
        /// (ArcViewer `defaultControlOffset` = 2.5).</summary>
        public const float ArcControlOffset = 2.5f;
        public const float ArcEndFadeStartMeters = 0f;
        public const float ArcEndFadeMeters = 0.35f;
        /// <summary>Scroll speed of the arc texture (ArcViewer `arcAnimationSpeed` = 0.75).</summary>
        public const float ArcAnimationSpeed = 0.75f;
        /// <summary>FALLBACK edge length of a chain link (only when the reference mesh
        /// ChainLink.fbx is missing). With the reference mesh (0.50 × 0.09 × 0.50, globalScale 1)
        /// the hitbox is taken from the renderer's world bounds - exactly ArcViewer.</summary>
        public const float ChainLinkSize = 0.22f;
        /// <summary>Distance by which an arc end point is shifted toward the adjacent note center
        /// when a note/bomb sits there (ArcViewer `halfNoteOffset` = 0.225).</summary>
        public const float ArcAttachOffset = 0.225f;

        // ---------- Arc interaction (original `SliderInteractionManager`) ----------
        // The arc reacts to the saber blade: if it follows the band, the band glows more
        // (haptics); if it leaves it, the arc fades out. Score is still given
        // ONLY by the head/end note (as in the original).
        /// <summary>Catch radius around the arc curve (meters, × room scaling), within which
        /// the blade counts as "in the arc".</summary>
        public const float ArcInteractRadius = 0.20f;
        /// <summary>Maximum brightness increase of the band at full interaction (1 = +100 %).</summary>
        public const float ArcInteractIntensityBoost = 0.9f;
        /// <summary>Residual brightness of an abandoned arc (fades out to this value).</summary>
        public const float ArcInteractFadedIntensity = 0.15f;
        /// <summary>Fade speed (units/s) for the interaction transition.</summary>
        public const float ArcInteractFadeSpeed = 5f;

        // ---------- Arc-saber attraction (original `SliderController.SetSaberAttraction`) ----------
        // If the HEAD NOTE of an arc is cut, the saber becomes the new START of the
        // arc: The arc then "sticks" to the saber and changes live with its movement.
        // In the original this happens in the vertex shader via `_SaberAttractionPoint`
        // (MaterialPropertyBlock, `SliderShaderHelper.SetSaberAttractionPoint`) - here the
        // curve is instead recomputed CPU-side (same effect, no new shader).

        /// <summary>Point on the saber blade at which the arc sticks (0 = base, 1 = tip).
        /// Original `SliderController.kSaberAttractPointNormalizedPosition` = 0.7.</summary>
        public const float ArcAttachPointNormalizedPosition = 0.7f;
        /// <summary>Smoothing of the attraction point (1/s, framerate-independent) - replacement for
        /// `SliderController._closeSmoothedSaberInteractionPos` (FixedUpdateVector3SmoothValue).
        /// Larger = more directly on the saber, smaller = more lagging.</summary>
        public const float ArcAttachSmoothSpeed = 30f;
        /// <summary>Maximum stretch (base units): The attraction point may move away from the arc end
        /// by at most this amount BEYOND the natural arc length.
        /// Beyond that it is clamped to this "leash" (no letting go ⇒ no visible jump,
        /// but also no meter-long distortion).</summary>
        public const float ArcAttachMaxStretch = 1f;

        // ---------- Multiplier (1:1 like original/ArcViewer, ScoringUtils.cs) ----------
        // Tiers x1, x2, x4, x8; each tier requires HitsNeededForComboIncrease hits
        // (x1->x2 after 2, x2->x4 after 4, x4->x8 after 8 hits). The note that
        // reaches a tier already counts with the NEW tier. A mistake halves the tier
        // (x8->x4->x2->x1, int/2) and resets the progress - it does NOT jump to x1
        // (exception: wall head hit, a deliberate own rule).
        public static readonly int[] ComboMultipliers = { 1, 2, 4, 8 };
        public static readonly int[] HitsNeededForComboIncrease = { 2, 4, 8 };

        // ---------- MR tolerance (only the 4 permitted core features) ----------
        // (2) Invisible hitbox of the notes - 1:1 the reference COLLISION BOX.
        //     BeatSaber web replays `beat.js toBigBox` and OpenSaberPlus
        //     `BeepCube.tscn` (CollisionBig) are identical: 0.80 × 0.50 × 1.00
        //     (dot: 0.80 high; chain: 0.20 high); the references offset it by +0.25 toward the
        //     player side - here deliberately NOT anymore. The score zone lies exactly on the
        //     note (offset 0, position (0, 0)): The box is centered on the note center,
        //     so notes and light effects of the song (both at the beat time at Z=0) stay
        //     perfectly in sync. The player instead stands
        //     PlayerDistanceBehindZone behind the zone.
        //     The point scoring (pre-swing/follow-through + center) is NOT read at the first box contact
        //     (which due to the larger box is often at the edge), but at the moment of
        //     BLADE CONTACT - follow-through/center follow after the measurement window
        //     (see GameManager.ScoreAtCutAsync).
        //     NOTE: The box stays fixed on the note (one layer, no bug-using) -
        //     NO dynamic pulling forward/deforming. It is tested with the blade
        //     SEGMENT (segment/box slab test, see GameManager.TryGeometricHit) - not
        //     with the inflated swing AABB.
        public const float NoteHitboxWidth = 0.8f;
        public const float NoteHitboxHeight = 0.5f;
        public const float NoteHitboxHeightDot = 0.8f;
        public const float NoteHitboxHeightChain = 0.2f;
        public const float NoteHitboxDepth = 1.0f;
        /// <summary>Distance (meters at scaling 1) between player and score zone/plane Z=0.
        /// The player thus stands BEHIND the zone (room Z = -value) instead of on it. Earlier 0.65
        /// (ArcViewer `PlayerCutPlaneDistance`) - reduced to 0.30 because the notes felt
        /// too far away at 0.65 m (no longer comfortably reachable). Scales with the
        /// room scaling (saber and notes grow along with it).</summary>
        public const float PlayerDistanceBehindZone = 0.3f;
        // (3) Music low-pass on head↔wall contact (Hz) - immediately muffled "under water".
        public const float WallMuffleCutoffHz = 400f;
        // (4) Wall sizzle on the saber: random pulse rate (Hz) and amplitude.
        public const float WallSizzleHapticMinHz = 40f;
        public const float WallSizzleHapticMaxHz = 80f;
        public const float WallSizzleHapticAmplitude = 0.6f;
        public const float WallSizzleVolume = 0.18f;
        // CCD: number of interpolated sweep poses between previous frame and current frame
        // (sweep test against fast slipping through).
        public const int SaberSweepSamples = 4;

        public const string DemoFolderName = "Death to Disco";
        public const string DemoSongName = "Death to Disco";
        public const string DemoSongAuthor = "by Freeek";
        public const float DemoBpm = 115f;
        /// <summary>Resource folder of the built-in song; also the prefix of the beatmap JSONs.</summary>
        public const string DemoMapResourcePrefix = "QuestSaberDemo/DeathToDisco/";
        /// <summary>info.json of the built-in song in Resources.</summary>
        public const string DemoInfoResource = DemoMapResourcePrefix + "info";
        /// <summary>Audio of the built-in song in Resources - load name WITHOUT extension.</summary>
        public const string DemoTrackResource = DemoMapResourcePrefix + "DeathToDisco";
        /// <summary>File name of the same audio (display/info only; playback loads
        /// <see cref="DemoTrackResource"/>).</summary>
        public const string DemoTrackFileName = "DeathToDisco.ogg";
        /// <summary>Only for migrating ancient demo high scores (key without difficulty)
        /// and as an emergency difficulty if the demo info is not readable.</summary>
        public const string DemoDifficultyName = "Expert";

        // ---------- Sound effects (reference BeatSaberClone) ----------
        // ONE single slices clip (Slice.ogg) serves two completely differently
        // PLAYED uses: the note hit (direct, muffled, quietly audible) and the
        // switch click of the Easter egg (trimmed at the front, sped up, bright).
        // The tone coloring arises at RUNTIME via low-pass (`AudioLowPassFilter`)
        // + `AudioSource.pitch` + start offset (`timeSamples`) - each its own
        // AudioSource, see `AudioManager`. Everything here is tunable without code changes;
        // a different sound = simply swap the resource path or the numeric values.

        /// <summary>Slice sound of the reference (`Resources/Slice.ogg`) - source of both effects.</summary>
        public const string SliceSfxResource = "QuestSaberDemo/Slice";

        public const float HitSfxVolume = 0.20f;
        /// <summary>Pitch of the cut sound - &lt; 1 makes it lower/heavier.</summary>
        public const float HitSfxPitch = 0.85f;
        /// <summary>Low-pass cutoff frequency in Hz - the smaller, the muffled. Range 10...22000.
        /// 1000 instead of 850: stays muffled, but the sound does not lose its substance.</summary>
        public const float HitSfxLowpassHz = 1000f;
        public const float HitSfxLowpassQ = 1.0f;

        // --- Saber extend/retract (Easter egg): trimmed at the front + sped up, bright ---
        public const float SaberToggleSfxVolume = 0.25f;
        /// <summary>Acceleration - &gt; 1 makes it faster AND higher (feels more direct).</summary>
        public const float SaberToggleSfxPitch = 1.9f;
        /// <summary>How many milliseconds are skipped at the START of the slice sound.</summary>
        public const float SaberToggleSfxSkipMs = 45f;
        /// <summary>Low-pass of the switch click. 4000 instead of 1800 Hz - the click was thereby
        /// "too dark" and sounded clogged; bright enough to read as a click.</summary>
        public const float SaberToggleLowpassHz = 4000f;
        public const float SaberToggleLowpassQ = 1.0f;

        /// <summary>UI click (unchanged).</summary>
        public const string UISfxResource = "QuestSaberDemo/UI_Confirm";
        public const float UISfxVolume = 0.20f;

        // ---------- Particles (reference BeatSaberClone: BoxSpawner + ObjectSlicer) ----------
        // Both effects are statically CACHED and POOLED (like the reference ParticleEffectHandler
        // pooling: capacity 4 / max 8 per effect) - no Resources.Load and no Instantiate
        // allocation per hit/spawn (see PROJECT_NOTES §10).
        /// <summary>Spawn particles of the note blocks: red (left/`_type 0`) or blue.</summary>
        public const string NoteSpawnParticleRedResource = "QuestSaberDemo/Particles/ParticleSpawnL";
        public const string NoteSpawnParticleBlueResource = "QuestSaberDemo/Particles/ParticleSpawnR";
        public static readonly Quaternion ParticleRotation = Quaternion.Euler(-90f, 0f, 0f);
        /// <summary>Upper limit of the pool per effect (reference `ParticleEffectSettings`: 8).
        /// Instances beyond that are destroyed on return.</summary>
        public const int ParticlePoolMaxSize = 8;

        public static readonly Color WallColor = new Color(0.80f, 0.90f, 1.00f, 1f);
        public const float WallOpacity = 0.20f;
        public const float WallLightness = 1f;

        // ---------- Player runtime path ----------
        // QuestSaber never reads the folders of the official Beat Saber. Songs come from:
        //   1) the app-own "Songs" folder (USB / file manager, NO permission),
        //   2) the folder the player picked in the Android system folder chooser (e.g.
        //      Download; the app asks for it automatically while no usable folder is
        //      granted) - QuestSaber moves new songs out of it automatically
        //      (NO permission, no developer mode, fully offline),
        //   3) the bundled demo song.
        // Everything stays inside the app sandbox, so the app never requests a storage
        // permission and never needs developer mode.
        /// <summary>Song source 1 (RECOMMENDED for your own downloads): the app-own external
        /// folder `Android/data/<Package>/files/Songs`. On Android 11+ writable via USB/file manager
        /// WITHOUT special permission - simply copy the song folders in here.
        /// (= Application.persistentDataPath/Songs.)</summary>
        public static string SongsRootPath =>
            System.IO.Path.Combine(Application.persistentDataPath, "Songs");
        /// <summary>Name of the short how-to file that is dropped into the app-own Songs folder
        /// (so the folder is self-explaining in the file manager over USB).</summary>
        public const string SongFolderHintFileName = "_HOW_TO_ADD_SONGS.txt";
        /// <summary>Song source 2 (the app-own FALLBACK): songs moved/copied out of the
        /// permission-free media inbox, out of a legacy SAF folder grant, or out of the shared
        /// Download folder when "All files access" is missing. It stays a scan root so songs
        /// imported by earlier versions are still found. The visible folder at the storage root
        /// (<c>/storage/emulated/0/QuestSaberSongs</c>, see <see cref="SongImportDestination"/>)
        /// is the primary location as soon as the shared storage is readable - the player then
        /// sees the songs exactly where he put them and the game plays them from there.
        /// Fully offline, no permission needed for this folder.</summary>
        public static string ImportedSongsPath =>
            System.IO.Path.Combine(Application.persistentDataPath, "SongsImported");
        /// <summary>
        /// All song roots, in scan order. The VISIBLE folder sits at the TOP LEVEL of
        /// the shared storage (/storage/emulated/0/QuestSaberSongs) FIRST when the app may read
        /// the shared storage: that is where songs from Download are imported to and played
        /// from, so the player always finds them (before it sat INSIDE Download, which
        /// the Files app's aggregated views listed as a flood of files - see
        /// <see cref="SafSongFolder.EnsureVisibleSongFolder"/>). The legacy location
        /// (Download/QuestSaberSongs) is still scanned until the automatic migration moved its
        /// songs over. Without "All files access" the app-own folders keep working exactly as
        /// before. QuestSaber reads every existing folder and merges them (duplicates are
        /// filtered out, see BeatmapParser.LoadSongList).
        /// </summary>
        public static string[] SongsRootCandidates
        {
            get
            {
                var roots = new System.Collections.Generic.List<string>(4);
                string visible = SafSongFolder.VisibleSongsPath;
                if (!string.IsNullOrEmpty(visible))
                {
                    roots.Add(visible);
                    // Songs of an older install still sit in Download/QuestSaberSongs.
                    // Keep reading from there as a safety net until the java migration moved
                    // them (only added while the shared storage is really readable, so the
                    // scanner never walks into a folder it cannot read).
                    string legacy = LegacyDownloadSongsPath;
                    if (!string.IsNullOrEmpty(legacy) && legacy != visible) roots.Add(legacy);
                }
                roots.Add(SongsRootPath);
                roots.Add(ImportedSongsPath);
                return roots.ToArray();
            }
        }

        /// <summary>
        /// Legacy visible song folder (<c>Download/QuestSaberSongs</c>), or
        /// "" when the shared Download folder is unavailable. Kept as a scan root so songs of an
        /// older install are still found if the automatic move to the storage root did not run
        /// (e.g. no "All files access").
        /// </summary>
        public static string LegacyDownloadSongsPath
        {
            get
            {
                string dl = SafSongFolder.DownloadPath;
                return string.IsNullOrEmpty(dl)
                    ? ""
                    : System.IO.Path.Combine(dl, SafSongFolder.VisibleFolderName);
            }
        }

        /// <summary>
        /// Destination for a song that was found in the shared Download folder. With
        /// "All files access" that is the VISIBLE folder at the storage root
        /// (<c>/storage/emulated/0/QuestSaberSongs</c>, the player sees the songs as
        /// ONE folder, just like on a PC, and the game plays them from there); without it the
        /// app-own SongsImported folder is the fallback used by the permission-free inbox path.
        /// </summary>
        public static string SongImportDestination
        {
            get
            {
                string visible = SafSongFolder.VisibleSongsPath;
                return string.IsNullOrEmpty(visible) ? ImportedSongsPath : visible;
            }
        }
        public static string SaveRootPath =>
            System.IO.Path.Combine(Application.persistentDataPath, "QuestSaberSave");
        public static string ProfilesPath =>
            System.IO.Path.Combine(SaveRootPath, "Profiles.json");

        // ---------- Quest 3 performance (Meta optimizations; see MetaPerformance.cs) ----------
        // The Quest 3 supports exactly 72/80/90/120 Hz - 120 is the maximum
        // (144 Hz does NOT exist here). A higher frame rate is not "free": 120 Hz
        // means ~33 % more GPU work per second than 90 Hz. Therefore there is
        // a one-sided watchdog (downward only) that goes to the fallback rate
        // on a persistently missed target.
        /// <summary>Target frame rate (Hz). 120 = Quest 3 maximum.</summary>
        public const float TargetDisplayRefreshRate = 120f;
        /// <summary>Fallback frame rate of the watchdog (spares battery/temperature).</summary>
        public const float FallbackDisplayRefreshRate = 90f;
        /// <summary>Suppress guardian/boundary visibility app-side (Quest):
        /// the Quest then draws no boundary grid over the passthrough. Requires
        /// `boundaryVisibilitySupport` in the OculusProjectConfig + passthrough. The OS continues
        /// the secured room boundary internally (safety function); the
        /// floor tracking (floor as tracking origin) remains unaffected.</summary>
        public static readonly bool SuppressGuardianBoundary = true;
        /// <summary>Fixed foveated rendering level (Off/Low/Medium/High/HighTop). "High"
        /// saves the most fill rate (important for 120 Hz); the outer image edge becomes
        /// softer in the process. Reduce to Medium/Off if needed.</summary>
        public const OVRManager.FoveatedRenderingLevel FoveationLevel = OVRManager.FoveatedRenderingLevel.High;

        // ---------- Quest 3 quality extras (see MetaExtras.cs) ----------
        /// <summary>Quest 3 local dimming: stronger LCD contrast (better blacks in VR).
        /// Inert on devices that do not report support.</summary>
        public static readonly bool EnableLocalDimming = true;
        /// <summary>Quest 3 eye buffer sharpening (CAS-like). "Normal" costs the
        /// least GPU; a missing OVRPlugin support is logged and ignored.</summary>
        public static readonly bool EnableEyeBufferSharpening = true;
        /// <summary>
        /// Environment depth occlusion (virtual content hidden behind real furniture).
        /// The OpenXR feature "Meta Quest: Occlusion" and the AROcclusionManager are
        /// already in the project - but the depth map only pays off when the ARENA
        /// SHADERS consume it (shader keyword XR_HARD_OCCLUSION / XR_SOFT_OCCLUSION).
        /// QuestSaber's arena shaders are ShaderGraph/CG and do not yet, so requesting
        /// the depth would cost GPU time without a visible effect. Kept OFF until the
        /// depth delivery has been verified in a device log; then flip this to true
        /// together with the shader support.
        /// </summary>
        public static readonly bool EnableEnvironmentOcclusion = false;
        /// <summary>
        /// Pause the run when the app loses focus during play (system menu button,
        /// headset taken off). Without this the song keeps playing and the notes keep
        /// running through while the player is in the system menu.
        /// </summary>
        public static readonly bool AutoPauseOnFocusLoss = true;

        // ---------- Grid helper functions ----------
        /// <summary>
        /// Position of a grid cell (lineIndex 0..3, lineLayer 0..2) in local
        /// base units of the room root. Y is the ABSOLUTE row height (RowHeights); the
        /// adjustable "field height" offset (<see cref="FieldHeightOffsetMeters"/>) is
        /// added every frame in NoteView/WallView (so it also takes effect live). The scaling
        /// is done by the root; Z from the spawn timing.
        /// </summary>
        public static Vector3 GridPosition(int lineIndex, int lineLayer)
        {
            int lane = Mathf.Clamp(lineIndex, 0, 3);
            int row = Mathf.Clamp(lineLayer, 0, RowHeights.Length - 1);
            float x = GridLeftX + lane * LaneWidth;
            float y = RowHeights[row];
            return new Vector3(x, y, 0f);
        }

        /// <summary>Arrow direction as Z degree angle - 1:1 ArcViewer `ObjectManager.DirectionAngles`
        /// (0:180, 1:0, 2:-90, 3:90, 4:-135, 5:135, 6:-45, 7:45, 8:0 = dot).</summary>
        public static float NoteAngleDegrees(int cutDirection)
        {
            switch (Mathf.Clamp(cutDirection, 0, 8))
            {
                case 0: return 180f;
                case 1: return 0f;
                case 2: return -90f;
                case 3: return 90f;
                case 4: return -135f;
                case 5: return 135f;
                case 6: return -45f;
                case 7: return 45f;
                default: return 0f;
            }
        }

        /// <summary>Direction vector of an angle in the XY plane - 1:1 ArcViewer
        /// (`ObjectManager.DirectionVector`: (sin θ, -cos θ)).</summary>
        public static Vector2 DirectionVector(float angleDegrees)
        {
            float r = angleDegrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(r), -Mathf.Cos(r));
        }

        /// <summary>
        /// Vertical extent of an obstacle in base units: bottom edge and height -
        /// 1:1 ArcViewer's wall constructor (grid unit 0.6 m, WallHScale, height limited to
        /// 5 - layer, minimum size MinWallSize). v3/v4 use the map values
        /// (<c>_lineLayer</c>/<c>_height</c>); v2 derives both from <c>_type</c>
        /// (0 = full floor wall 5 units, 1 = floating crouch wall from row 2 with 3).
        /// Returns the RAW values. The "field height" (<see cref="FieldHeightOffsetMeters"/>) shifts
        /// everything upward; floor-standing walls are extended to the floor in WallView.
        /// </summary>
        public static void ObstacleBounds(ObstacleModel obs, out float bottom, out float height)
        {
            float layer, layers;
            if (obs._height >= 1)
            {
                layer = Mathf.Clamp(obs._lineLayer, 0, 4);
                layers = Mathf.Min(obs._height, 5f - layer);
            }
            else
            {
                // v2 fallback: crouch wall = floating from row 2 (height 3), otherwise full wall (5).
                bool crouch = obs._type == 1;
                layer = crouch ? 2f : 0f;
                layers = crouch ? 3f : 5f;
            }
            // Raw values; WallView adds the "field height" offset (only for walls without
            // floor contact) every frame so the change takes effect live.
            bottom = layer * WallHScale;
            height = Mathf.Max(MinWallSize, layers * WallHScale);
        }
    }
}

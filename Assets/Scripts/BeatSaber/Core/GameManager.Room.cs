// GameManager: Room orientation, anchoring, quitting.
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace QuestSaber
{
    public sealed partial class GameManager
    {
        private void RecenterRoomOnHead()
        {
            if (_cam == null) return;

            Vector3 head = _cam.transform.position;

            // 1) Player anchor onto the headset (only X/Z - ApplyRoomAnchor pushes the score
            //    zone by the zone distance in front of it and continues to drive Y via _floorOffsetMeters,
            //    so the height stays exactly as it currently is).
            _roomFollowOffset = new Vector3(head.x, 0f, head.z);

            // 2) Rotate room into the look direction: Euler(0, yaw, 0) maps +Z onto
            //    (sin yaw, 0, cos yaw), thus yaw = atan2(forward.x, forward.z).
            Vector3 look = _cam.transform.forward;
            look.y = 0f;
            if (look.sqrMagnitude > 1e-6f)
                _roomYawDeg = Mathf.Repeat(Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg, 360f);

            // Re-measure depth following from here on, so the first frame after centering
            // ("room follows player", if active) does not jump.
            _lastFollowHead = new Vector3(head.x, 0f, head.z);
            _followInit = true;

            Debug.Log($"[QuestSaber] Player anchor set (X/Z = {head.x:0.00}/{head.z:0.00}, " +
                      $"{GameConfig.PlayerDistanceBehindZone * _roomScale:0.00} m behind the score zone, " +
                      $"height unchanged), look direction {_roomYawDeg:0}°.");
        }

        // ======================================================================
        // Room anchoring
        // ======================================================================

        /// <summary>
        /// Keeps the play space (`_gameRoot`, parent of notes/walls/HUD) anchored. Runs
        /// every frame in <c>LateUpdate</c>. X/Z = room anchor (<see cref="RecenterRoomOnHead"/>;
        /// with active depth following additionally driven by <see cref="UpdateRoomFollow"/>);
        /// Y = session floor offset (left stick, <see cref="UpdateRoomAdjustment"/>).
        /// There is deliberately NO coupling to head height (no origin compensation) - that had
        /// attached the field to head movement and lowered it at start.
        /// Rotation uses the yaw angle (`_roomYawDeg`, right stick; button A = look direction).
        ///
        /// Deliberately NOTHING is written to the XR rig here: the camera and its parent transform
        /// are created at runtime in the AR/OpenXR rig and belong to tracking (see file header).
        /// </summary>
        private void ApplyRoomAnchor()
        {
            if (_gameRoot == null) return;

            // Manual room rotation (yaw/Y only) - 0° = note field points to world +Z.
            Quaternion rot = Quaternion.Euler(0f, _roomYawDeg, 0f);

            // _roomFollowOffset = location of the PLAYER (world X/Z). The room origin (score
            // zone, Z=0) lies in front of it - in room forward direction by the zone distance
            // (scaled with the room). This way the player stands at start and after every recenter
            // exactly at the rear position (room Z = -distance), and the room rotates
            // around the player instead of the zone when turning with the stick.
            Vector3 toZone = rot * Vector3.forward * (GameConfig.PlayerDistanceBehindZone * _roomScale);

            // X/Z = player location + zone distance; Y = session floor offset (left stick).
            Vector3 pos = new Vector3(
                _roomFollowOffset.x + toZone.x, _floorOffsetMeters, _roomFollowOffset.z + toZone.z);
            _gameRoot.SetPositionAndRotation(pos, rot);

            // Background follows automatically: The stage (incl. capture + probe) is attached as
            // a child of `_gameRoot` (setup: AttachToRoom) - it needs NO separate
            // sync call here anymore. In passthrough it stays deactivated
            // (`_root.SetActive`, see EnvironmentDirector.ApplyMode).
        }

        /// <summary>
        /// "Room follows player" (menu switch, depth following): If the option is active, the
        /// play space is moved by the FORWARD/BACKWARD component of head movement (world X/Z, along
        /// the room forward axis). Sideways movement is NOT followed - the
        /// player can move sideways, but the distance to the note plane stays constant along the
        /// forward axis. Runs in <c>LateUpdate</c> BEFORE <see cref="ApplyRoomAnchor"/>;
        /// only real head movement counts (the room shift does not change the head position).
        /// </summary>
        private void UpdateRoomFollow()
        {
            if (_cam == null) return;

            Vector3 head = _cam.transform.position;
            Vector3 headXZ = new Vector3(head.x, 0f, head.z);
            if (!_followInit)
            {
                _lastFollowHead = headXZ;
                _followInit = true;
                return;
            }

            Vector3 delta = headXZ - _lastFollowHead;
            _lastFollowHead = headXZ;

            // Room forward axis (yaw): only its component is followed.
            Vector3 forward = Quaternion.Euler(0f, _roomYawDeg, 0f) * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) return;
            forward.Normalize();
            float signedForward = Vector3.Dot(delta, forward);

            // Run and Play: feed forward speed while playing
            // (without menu). Otherwise discard the buffer - measure fresh after menu/song change.
            if (_save.RunAndPlay && Phase == GamePhase.Playing && _menuSide == 0)
                _runAndPlay.Push(signedForward, Time.deltaTime);
            else
                _runAndPlay.Reset();

            if (!_save.RoomFollowsPlayer || delta.sqrMagnitude <= 1e-8f) return;

            // In Run-and-Play mode a standstill deadzone: below it the room also freezes.
            if (_save.RunAndPlay && Mathf.Abs(signedForward) < GameConfig.RunPlayDeadzoneMeters)
                return;

            // Only the forward/backward component (signed) is followed - when moving
            // backward, the field moves along with the player (distance stays constant).
            _roomFollowOffset += forward * signedForward;
        }

        /// <summary>
        /// Run and Play: translates the buffered movement state into audio pause/rate.
        ///   Still    => song pauses completely (notes stand still).
        ///   Backward => gameplay stops IMMEDIATELY; the field follows via UpdateRoomFollow
        ///               (player-field distance constant, no backward cheating).
        ///   Forward  => rate = v x 1/(roomScale*NJS) x run speed; song runs in sync.
        /// </summary>
        private void RunAndPlayUpdate()
        {
            if (!_save.RunAndPlay) return;

            if (_runAndPlay.State == RunPlayState.Forward)
            {
                float comfort = _player != null
                    ? Mathf.Clamp(_player.RunComfort, GameConfig.RunComfortMin, GameConfig.RunComfortMax)
                    : GameConfig.RunComfortDefault;
                float rate = RunAndPlay.ComputePlayRate(
                    _runAndPlay.SmoothedSpeed, _njs, _roomScale, comfort);
                _audio.SetRunPlayRate(rate);
                _audio.SetRunPlayPaused(false);
            }
            else
            {
                // Standstill OR backward: stop song + note timeline immediately.
                _audio.SetRunPlayPaused(true);
            }
        }

        private void SetRunAndPlay(bool on)
        {
            if (_save.RunAndPlay == on) return;

            if (on)
            {
                _roomFollowBeforeRun = _save.RoomFollowsPlayer;
                _save.RoomFollowsPlayer = true;
            }
            else
            {
                _save.RoomFollowsPlayer = _roomFollowBeforeRun;
                _runAndPlay.Reset();
                _audio.ClearRunPlay();
            }
            _save.RunAndPlay = on;
            Save();
            if (_menuSide != 0) BuildMenuContent();
        }

        /// <summary>
        /// Sets the floor origin to "Floor" (OpenXR) and waits for the runtime's
        /// confirmation.
        ///  1) `XRInputSubsystem.TrySetTrackingOriginMode(Floor)` - Unity/OpenXR.
        /// (The former PICO-native API has been removed - Meta Quest runs via OpenXR.)
        ///
        /// <paramref name="mustSucceed"/> = true (game start): the mode is ALWAYS set
        /// (forces a fresh floor derivation) and retried on failure until
        /// Floor is confirmed ("must not fail").
        /// false: only set if the mode is not yet Floor. CURRENTLY UNUSED - every caller passes
        /// true, so all false-only branches in this method are dead in practice. It is kept on
        /// purpose for a future "recalibrate without forcing" path. Button A does not touch the
        /// tracking origin (it only sets room anchor X/Z under the headset and the rotation to
        /// the look direction, see RecenterRoomOnHead).
        /// </summary>
        private IEnumerator EnsureFloorOrigin(bool mustSucceed)
        {
            const float retryDelaySeconds = 0.5f;
            const int framesPerAttempt = 30;
            int attempts = 0;

            while (true)
            {
                attempts++;
                TrackingOriginModeFlags unityMode = TrackingOriginModeFlags.Unknown;
                bool bestaetigt = false;

                // --- Set mode (OpenXR) ---
                // At GAME START (mustSucceed) ALWAYS set - even if the runtime already
                // reports "Floor". This forces a fresh floor derivation if the origin
                // was previously shifted (e.g. by an XR recenter). With mustSucceed=false
                // only set if the mode is not yet "Floor" (no recalibration).
                foreach (var s in RunningInputSubsystems())
                {
                    if (mustSucceed || s.GetTrackingOriginMode() != TrackingOriginModeFlags.Floor)
                        s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                }

                // --- Only observe until confirmed (do not set again) ---
                for (int i = 0; i < framesPerAttempt && !bestaetigt; i++)
                {
                    foreach (var s in RunningInputSubsystems())
                    {
                        var mode = s.GetTrackingOriginMode();
                        if (mode == TrackingOriginModeFlags.Floor) { bestaetigt = true; unityMode = mode; }
                        else if (unityMode == TrackingOriginModeFlags.Unknown) unityMode = mode;
                    }

                    if (!bestaetigt) yield return null;
                }

                if (bestaetigt)
                {
                    // Successful floor detection: reset the manual stick offset.
                    _floorOffsetMeters = 0f;
                    float headY = _cam != null ? _cam.transform.position.y : float.NaN;
                    Debug.Log($"[QuestSaber] Floor level = Floor confirmed (Unity: {unityMode}) - " +
                              $"head stands {headY:0.000} m above the floor; " +
                              $"stick floor offset set to 0.");
                    yield break;
                }

                if (Application.isEditor || !mustSucceed)
                {
                    Debug.LogWarning($"[QuestSaber] Floor level: Floor not confirmed " +
                                     $"(Unity: {unityMode}) - height stays unchanged.");
                    yield break;
                }

                if (attempts % 10 == 0)
                    Debug.LogWarning($"[QuestSaber] Floor level: Floor not yet confirmed " +
                                     $"(attempt {attempts}) - retrying.");
                yield return new WaitForSeconds(retryDelaySeconds);
            }
        }

        /// <summary>
        /// Fills <see cref="_subsystemScratch"/> with the running XR input subsystems and
        /// returns the same list. Deliberately allocation-free; the list is overwritten by the caller
        /// before the next call and must therefore not be stored.
        /// </summary>
        private List<XRInputSubsystem> RunningInputSubsystems()
        {
            _subsystemScratch.Clear();
            SubsystemManager.GetSubsystems(_subsystemScratch);
            _subsystemScratch.RemoveAll(s => !s.running);
            return _subsystemScratch;
        }

        /// <summary>
        /// Room orientation via joystick (only outside the song list - there the stick pages
        /// through the songs):
        ///  - right stick left/right => room yaw angle (<see cref="_roomYawDeg"/>),
        ///  - left  stick up/down    => session floor offset (<see cref="_floorOffsetMeters"/>).
        /// The floor offset starts at 0 on EVERY game start and is NOT saved;
        /// button A does not change the height (A sets room X/Z under the headset and the rotation
        /// to the look direction). The deadzone is deliberately larger
        /// (<see cref="GameConfig.FloorOffsetDeadzone"/>), so that a slight/accidental
        /// deflection does not shift anything.
        /// </summary>
        private void UpdateRoomAdjustment()
        {
            if (_songListOpen) return;

            float dt = Time.unscaledDeltaTime;

            // Right stick left/right: rotate room.
            float yaw = ApplyStickDeadzone(_input.RightThumbX, GameConfig.RoomAdjustDeadzone);
            if (yaw != 0f)
            {
                _roomYawDeg = Mathf.Repeat(
                    _roomYawDeg + yaw * GameConfig.RoomYawDegPerSecond * dt, 360f);
            }

            // Left stick up/down: session floor offset - only on CLEAR deflection
            // (larger deadzone). Starts at 0, not saved, A does not touch it.
            float floor = ApplyStickDeadzone(_input.LeftThumbY, GameConfig.FloorOffsetDeadzone);
            if (floor != 0f)
                _floorOffsetMeters += floor * GameConfig.FloorOffsetMetersPerSecond * dt;
        }

        /// <summary>
        /// Applies a deadzone to a stick value (-1..1) and scales the rest back
        /// to 0..1, so that movement starts smoothly (without a jump) from the threshold.
        /// </summary>
        private static float ApplyStickDeadzone(float value, float deadzone)
        {
            float mag = Mathf.Abs(value);
            if (mag <= deadzone) return 0f;
            return Mathf.Sign(value) * Mathf.InverseLerp(deadzone, 1f, mag);
        }

        private void Quit()
        {
            if (_savePending) FlushSave(); // save pending (batched) changes before quitting
            Debug.Log("[QuestSaber] Quitting game (Y/B held >= 2 s).");
            _keyboard = null;
            if (_audio != null) _audio.StopSong();
            Application.Quit();
        }
    }
}
// refresh-touch-9

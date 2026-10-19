// GameManager: hand menu, player management, song list.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace QuestSaber
{
    public sealed partial class GameManager
    {
        // ======================================================================
        // Menu
        // ======================================================================
        private void OpenMenu(int side)
        {
            if (_menu == null) return;
            _phaseBeforeMenu = Phase;
            _inPlayersMenu = false;
            // An open OS keyboard (name input) must not survive a new menu opening - otherwise
            // it would hang around with no owner. This is also the escape hatch out of the
            // name input: pressing the menu button again closes the keyboard and shows the menu.
            if (_keyboard != null)
            {
                DismissKeyboard();
                _keyboard = null;
            }
            _awaitingName = false;
            _confirmingDelete = false;
            _confirmingSongDelete = false;
            _pendingSongDeleteIndex = -1;
            _songListOpen = false;
            _menuInputLockUntil = 0f;
            _allowNewPlayer = true; // new menu opening => a "+ New Player" is allowed again
            _pendingDelete = null;
            _keyboardGotText = false;
            _keyboardDead = false;
            _keyboardWaitLogged = false;

            Phase = GamePhase.Menu;
            _menuSide = side;
            AutoRetractSabersForMenu(); // retract both sabers (only the cursor remains visible)

            if (_phaseBeforeMenu == GamePhase.Playing)
            {
                _audio.PauseSong();
                Time.timeScale = 0f;
            }
            // Release the Run-and-Play coupling for the menu duration (rate 1); the menu pause remains.
            if (_save.RunAndPlay) _audio.ClearRunPlay();
            SetSaberTrails(false); // otherwise the trail "paints" in the air with time frozen
            Fx.StopAllContacts(); // let contact sparks (wall/clash) fade out on menu entry

            var anchor = side == -1 ? _leftAnchor : _rightAnchor;
            if (anchor != null) _menu.SetAnchor(anchor);

            BuildMenuContent();
            _audio.PlayUiSfx();
        }

        private void CloseMenu()
        {
            if (_menu != null) _menu.Hide();
            _menuSide = 0;
            _inPlayersMenu = false;
            _awaitingName = false;
            _confirmingDelete = false;
            _confirmingSongDelete = false;
            _pendingSongDeleteIndex = -1;
            _pendingDelete = null;
            DismissKeyboard(); // forcibly close a stuck OS keyboard
            _keyboard = null;
            _keyboardGotText = false;
            _keyboardDead = false;
            _keyboardWaitLogged = false;
            _songListOpen = false;

            RestoreSabersAfterMenu(); // extend again without manual change

            Time.timeScale = 1f;
            SetSaberTrails(true);

            // A different song or difficulty was chosen in the menu:
            // then on close (do not resume, but) reload and play.
            if (_songSelectionQueued)
            {
                _songSelectionQueued = false;
                LoadSong(_songIndex);
                return;
            }

            if (_phaseBeforeMenu == GamePhase.Playing) _audio.ResumeSong();
            Phase = _phaseBeforeMenu == GamePhase.Boot ? GamePhase.Boot : GamePhase.Playing;
        }

        /// <summary>
        /// Let the menu jump from one hand to the other (short press of the other menu button):
        /// it stays open and paused - only the anchor, pointer hand and view change.
        /// </summary>
        private void SwitchMenuSide(int newSide)
        {
            if (_menu == null || newSide == _menuSide) return;
            _menuSide = newSide;
            // Both sabers are already retracted (OpenMenu) - on a side switch only
            // switch the pointer side (laser/cursor).
            var anchor = newSide == -1 ? _leftAnchor : _rightAnchor;
            if (anchor != null) _menu.SetAnchor(anchor);
            _confirmingDelete = false;
            _confirmingSongDelete = false;
            _pendingSongDeleteIndex = -1;
            if (_menu != null) _menu.HideConfirm();
            _pendingDelete = null;
            BuildMenuContent();
            _audio.PlayUiSfx();
        }

        private List<HandMenu.Row> MenuRows()
        {
            var rows = new List<HandMenu.Row>();

            // ---- Scrollable song list submenu (only the song list, without header) ----
            // The CURRENT song gets only the blue background (highlight, NO
            // selection). The green marker comes exclusively from the laser (hover).
            if (_songListOpen)
            {
                for (int i = 0; i < _songs.Count; i++)
                {
                    bool cur = i == _songIndex;
                    // Full name (no truncation): the display clips to the row width
                    // and lets the name scroll under the laser as a marquee.
                    rows.Add(new HandMenu.Row
                    {
                        Text = _songs[i].SongName,
                        Id = IdSongBase + i,
                        Selectable = true,
                        Deletable = !_songs[i].IsDemo, // the bundled demo song cannot be deleted
                        Background = cur ? ActivePlayerBg : (Color?)null
                    });
                }
                return rows;
            }

            // ---- Name input FALLBACK rows ----
            // Normally name input shows NO menu at all: BeginNewPlayer hides the panel and the
            // OS keyboard is the only visible input mask (ENTER/Done on the keyboard creates the
            // player). These rows only appear when the keyboard never showed up - PollKeyboard
            // then brings the panel back after KeyboardDeadAfterSeconds, see there. Without them
            // the name input would be a dead end with no way to cancel.
            // The title/difficulty rows must NOT appear here: both are selectable and were
            // otherwise accidentally hit by the confirm trigger (the song list opened instead of
            // taking the name). "Confirm input" is the first selectable row on purpose, so the
            // default cursor stands on it.
            if (_awaitingName)
            {
                string shown = string.IsNullOrEmpty(_pendingName) ? "…" : Shorten(_pendingName, 16);
                rows.Add(new HandMenu.Row { Text = "New Player", Id = -1, Selectable = false, Title = true });
                rows.Add(new HandMenu.Row { Text = "Name: " + shown, Id = -1, Selectable = false, FontSize = 44 });
                rows.Add(new HandMenu.Row { Text = "Confirm input", Id = IdNew, Selectable = true, Button = true });
                rows.Add(new HandMenu.Row { Text = "Cancel", Id = IdClose, Selectable = true, Button = true });
                return rows;
            }

            // Title (click opens the song list) + difficulty (arrows left/right)
            // - appear directly below each other in EVERY normal view. Both views
            // are available as their own methods, so that the song list can adopt the height of the
            // main menu (MainMenuPanelHeight).
            //
            // Calibration page: while the virtual controllers are shown the menu shows
            // ONLY the calibration rows - the player is aligning controllers, not playing, and the
            // main menu plus eleven nudge rows would be taller than the panel.
            if (ControllerDebugView.Visible) return CalibrationRows();
            return _inPlayersMenu ? PlayersMenuRows() : MainMenuRows();
        }

        /// <summary>Player management: title + difficulty at the top, then player list and actions.</summary>
        private List<HandMenu.Row> PlayersMenuRows()
        {
            var rows = new List<HandMenu.Row> { TitleRow(), DifficultyRow() };

            // Here the highscores per player for THIS song and THIS difficulty.
            int i = 0;
            int bestAll = 0;
            foreach (var p in _save.Players)
                bestAll = Math.Max(bestAll, _current != null ? p.GetBest(_current.BestKey) : 0);
            foreach (var p in _save.Players)
            {
                bool isActive = p.Name == _save.SelectedPlayer;
                string topMark = (p.GetBest(_current != null ? _current.BestKey : "") == bestAll && bestAll > 0) ? " ★" : "";
                int best = _current != null ? p.GetBest(_current.BestKey) : 0;
                rows.Add(new HandMenu.Row
                {
                    Text = $"{Shorten(p.Name, 14)}{topMark}",
                    Value = best.ToString("N0"),
                    Id = 200 + i,
                    Selectable = true,
                    RightValue = true,
                    Background = isActive ? ActivePlayerBg : (Color?)null
                });
                i++;
            }

            // "New Player": disabled during an ongoing name input
            // (double protection); selectable again afterward, so that several players can be
            // created directly one after another.
            rows.Add(_allowNewPlayer
                ? new HandMenu.Row { Text = "New Player", Id = IdNew, Selectable = true, Button = true }
                : new HandMenu.Row { Text = "New Player", Id = -1, Selectable = false, Button = true });
            // Show the delete row even if SelectedPlayer (from an old save)
            // should be empty/null - the name is derived from the first profile
            // if necessary when the confirmation opens (see ShowDeleteConfirm).
            if (_save.Players.Count > 0)
                rows.Add(new HandMenu.Row { Text = "Delete player", Id = IdDelete, Selectable = true, Button = true });
            rows.Add(new HandMenu.Row { Text = "Back", Id = IdBack, Selectable = true, Button = true });
            return rows;
        }

        /// <summary>Height of the main menu panel (for an equally tall song list).</summary>
        private float MainMenuPanelHeight()
        {
            float h = 16f;
            foreach (var r in MainMenuRows()) h += HandMenu.HeightOf(r);
            return h;
        }

        /// <summary>Main menu: title + difficulty, player management and all controls.</summary>
        private List<HandMenu.Row> MainMenuRows()
        {
            var rows = new List<HandMenu.Row> { TitleRow(), DifficultyRow() };

            // ---- Songs from the headset itself ----
            // Fully automatic, so there is NO menu entry for it any more. The game
            // reads the shared Download folder directly ("All files access" is asked at most
            // once per START while it is missing - verified by really reading the
            // folder) and moves the songs into the visible "QuestSaberSongs" folder (at
            // the TOP LEVEL of the shared storage, not inside Download - the Files app's
            // aggregated "Downloads" view listed the folder's files flat and made Download look
            // cluttered), which is also the folder it plays from; .zip archives are unpacked on
            // the way. A fresh scan runs on EVERY app start (and when the app comes back to
            // the front), so a manual "refresh" row is gone as well. Without the grant, songs
            // dropped into the permission-free media inbox still work. Fully offline.

            rows.Add(new HandMenu.Row { Text = "Player management", Id = IdPlayers, Selectable = true, Button = true });
            float sc = _player != null ? Mathf.Clamp(_player.Scale, GameConfig.ScaleMin, GameConfig.ScaleMax) : 1f;
            string scShow = sc.ToString("F2").Replace('.', ',');
            rows.Add(new HandMenu.Row
            {
                Text = "Scale",
                Value = $"{scShow}×",
                Id = IdHeight,
                Selectable = true,
                MultiControl = true
            });
            // Vertical offset of ALL objects (notes/mines/walls) above the floor (persistent).
            // The floor stays; floor-standing walls are extended down to the floor. The left
            // stick, in contrast, raises/lowers the ENTIRE field.
            float fh = _save.FieldHeightMeters;
            string fhShow = fh.ToString("F2").Replace('.', ',');
            rows.Add(new HandMenu.Row
            {
                Text = "Field height",
                Value = $"{fhShow} m",
                Id = IdFieldHeight,
                Selectable = true,
                MultiControl = true
            });
            // Depth-follow: couple the room to forward/backward movement (on/off, persistent).
            // On/off rows are a single interaction surface in the format "Name: Status";
            // a trigger anywhere on the row toggles (NO fields left/right).
            // In Run-and-Play mode the row is LOCKED (the follow-along is mandatory there).
            bool runPlay = _save.RunAndPlay;
            rows.Add(new HandMenu.Row
            {
                Text = "Room follows player",
                Value = runPlay ? "ON (Run&Play)" : (_save.RoomFollowsPlayer ? "ON" : "OFF"),
                Id = IdRoomFollow,
                Selectable = !runPlay,
                Card = true,
                RightValue = true
            });
            // Run and Play: couple game/audio speed to the physical forward movement
            // (global, persistent). ON forces "room follows player".
            rows.Add(new HandMenu.Row
            {
                Text = "Run and Play",
                Value = runPlay ? "ON" : "OFF",
                Id = IdRunPlay,
                Selectable = true,
                Card = true,
                RightValue = true
            });
            // Personal multiplier (only in active mode): 0.2...3.0 in 0.1 steps.
            if (runPlay)
            {
                float rc = _player != null
                    ? Mathf.Clamp(_player.RunComfort, GameConfig.RunComfortMin, GameConfig.RunComfortMax)
                    : GameConfig.RunComfortDefault;
                string rcShow = rc.ToString("F1").Replace('.', ',');
                rows.Add(new HandMenu.Row
                {
                    Text = "Run speed",
                    Value = $"{rcShow}×",
                    Id = IdRunComfort,
                    Selectable = true,
                    MultiControl = true
                });
            }
            rows.Add(new HandMenu.Row
            {
                Text = "Audio-Offset",
                Value = $"{_save.SoundDelayMs:0} ms",
                Id = IdDelay,
                Selectable = true,
                MultiControl = true
            });
            rows.Add(new HandMenu.Row
            {
                Text = "Background",
                Value = BackgroundModeText(_save.BackgroundMode),
                Id = IdPassthrough,
                Selectable = true,
                Card = true,
                RightValue = true
            });
            // Debug (session, not persistent): shows both tracking anchors with axes
            // and the Quest 3 controller model from the SDK. While it is ON the menu switches to
            // the saber calibration page (six degrees of freedom, see CalibrationRows) - the model
            // itself is finished and has no rows any more (see GameConfig).
            rows.Add(new HandMenu.Row
            {
                Text = "Controllers & sabers",
                Value = ControllerDebugView.Visible ? "ON" : "OFF",
                Id = IdControllerDebug,
                Selectable = true,
                Card = true,
                RightValue = true
            });
            // The in-headset calibration rows are NOT built here: with "Controllers & sabers"
            // ON the menu switches to the dedicated calibration page (CalibrationRows), because
            // the main menu plus six nudge rows would be far taller than the panel.
            return rows;
        }

        // ---------- In-headset calibration page ----------
        /// <summary>
        /// Shown INSTEAD of the main menu while the virtual controllers are visible, i.e. exactly
        /// what the player is looking at while adjusting. The first row switches the models
        /// (and with them this page) off again, so the page is never a dead end.
        ///
        /// The page contains ONLY saber rows: the virtual controller MODELS are finished
        /// (2026-10-18, "so wie die Controller aktuell sind, sind sie perfekt"), so every row that
        /// used to move a model is gone - and with it the second yaw lever ("Model yaw" turned the
        /// model while "Saber yaw" turned the blade). What is left is the saber in all SIX degrees
        /// of freedom: "Saber X / Y / Z" (position in the anchor frame: X lateral and mirrored,
        /// Y up/down, Z forward/back) and "Saber pitch / yaw / roll" (direction). The old
        /// "Saber grip" row - a slide along the tilted blade axis - is replaced by X/Y/Z, so
        /// "the grip sits too high" is a plain Y nudge now.
        ///
        /// The nudge rows are MultiControl cards: the LEFT field steps down, the RIGHT field steps
        /// up (HandMenu.LastPointerX, see StepUp) - 1 mm for positions, 1 deg for angles. Values
        /// are CLAMPED (not wrapped), so an overshoot is one click away from being undone. Every
        /// step re-applies both saber mounts and logs the values (ControllerDebugView.
        /// RefreshAlignment), so the numbers the player ends up with can be baked into GameConfig
        /// as the new defaults.
        /// </summary>
        private List<HandMenu.Row> CalibrationRows()
        {
            var rows = new List<HandMenu.Row>
            {
                new HandMenu.Row
                {
                    Text = "Controllers & sabers",
                    Value = "ON",
                    Id = IdControllerDebug,
                    Selectable = true,
                    Card = true,
                    RightValue = true
                },
                NudgeRow("Saber X", SaberOffsetText(0), IdSaberX),
                NudgeRow("Saber Y", SaberOffsetText(1), IdSaberY),
                NudgeRow("Saber Z", SaberOffsetText(2), IdSaberZ),
                NudgeRow("Saber pitch", SaberDegText(GameConfig.SaberAlignPitchDeg), IdSaberPitch),
                NudgeRow("Saber yaw", SaberDegText(GameConfig.SaberAlignYawDeg), IdSaberYaw),
                NudgeRow("Saber roll", SaberDegText(GameConfig.SaberAlignRollDeg), IdSaberRoll)
            };
            return rows;
        }

        /// <summary>A (-)/(+) control card: left field steps down, right field steps up.</summary>
        private static HandMenu.Row NudgeRow(string text, string value, int id)
        {
            return new HandMenu.Row
            {
                Text = text,
                Value = value,
                Id = id,
                Selectable = true,
                MultiControl = true
            };
        }

        private static string SaberDegText(float deg) => deg.ToString("0") + " deg";

        private string SaberOffsetText(int axis)
        {
            Vector3 o = GameConfig.SaberAlignOffset;
            float mm = (axis == 0 ? o.x : axis == 1 ? o.y : o.z) * 1000f;
            return mm.ToString("0") + " mm";
        }

        /// <summary>true = the right field of the row was hit -> step up; false = left field ->
        /// step down. Same convention as Scale / Field height / Audio-Offset.</summary>
        private bool StepUp => _menu != null && _menu.LastPointerX >= 0f;

        private int StepDir => StepUp ? 1 : -1;

        // Nudge steps of the calibration page: 1 mm for the saber position, 1 deg for the angles.
        private const int CalibStepMm = 1;
        private const int CalibStepDeg = 1;

        /// <summary>Steps the saber position by 1 mm on one axis (0=X lateral and mirrored,
        /// 1=Y up/down, 2=Z forward/back), clamped at +-50 mm. Applied by SaberView.ApplyMount.</summary>
        private void NudgeSaberOffset(int axis)
        {
            Vector3 o = GameConfig.SaberAlignOffset;
            int limit = Mathf.RoundToInt(GameConfig.SaberAlignRangeMeters * 1000f);
            int mm = Mathf.Clamp(
                Mathf.RoundToInt((axis == 0 ? o.x : axis == 1 ? o.y : o.z) * 1000f)
                    + StepDir * CalibStepMm, -limit, limit);
            if (axis == 0) o.x = mm * 0.001f;
            else if (axis == 1) o.y = mm * 0.001f;
            else o.z = mm * 0.001f;
            GameConfig.SaberAlignOffset = o;
            RefreshCalibration();
        }

        /// <summary>Steps one saber angle by 1 deg: 0 = pitch (further up/down), 1 = yaw
        /// (left/right, mirrored per side), 2 = roll (label orientation).</summary>
        private void NudgeSaber(int which)
        {
            int limit = Mathf.RoundToInt(GameConfig.AlignRangeDegrees);
            switch (which)
            {
                case 0:
                    GameConfig.SaberAlignPitchDeg = Mathf.Clamp(
                        Mathf.RoundToInt(GameConfig.SaberAlignPitchDeg) + StepDir * CalibStepDeg,
                        -limit, limit);
                    break;
                case 1:
                    GameConfig.SaberAlignYawDeg = Mathf.Clamp(
                        Mathf.RoundToInt(GameConfig.SaberAlignYawDeg) + StepDir * CalibStepDeg,
                        -limit, limit);
                    break;
                default:
                    GameConfig.SaberAlignRollDeg = Mathf.Clamp(
                        Mathf.RoundToInt(GameConfig.SaberAlignRollDeg) + StepDir * CalibStepDeg,
                        -limit, limit);
                    break;
            }
            RefreshCalibration();
        }

        /// <summary>Re-applies both saber mounts and logs the resulting values, then rebuilds
        /// the page.</summary>
        private void RefreshCalibration()
        {
            ControllerDebugView.RefreshAlignment();
            if (_saberL != null) _saberL.ApplyMount();
            if (_saberR != null) _saberR.ApplyMount();
            BuildMenuContent();
        }

        /// <summary>Title row: click opens the scrollable song list submenu.</summary>
        private HandMenu.Row TitleRow()
        {
            string name = _current != null ? Shorten(_current.SongName, 18) : "—";
            return new HandMenu.Row
            {
                Text = name,
                Id = IdSongList,
                Selectable = true,
                Title = true,
                Height = 96f
            };
        }

        private HandMenu.Row DifficultyRow()
        {
            string diff = _current != null && _current.CurrentDifficulty != null
                ? _current.CurrentDifficulty.DisplayName : "—";
            return new HandMenu.Row
            {
                Text = "Difficulty",
                Value = diff,
                Id = IdDiff,
                Selectable = true,
                MultiControl = true,
                Cycle = true
            };
        }

        private void BuildMenuContent()
        {
            if (_menu == null) return;

            // THE cause of "the old submenu flashed up for a second":
            // BeginNewPlayer() hides the panel (the OS keyboard is the input mask), but the
            // players menu then fell through to its own BuildMenuContent() (see the IdNew case),
            // and MenuRows() answers `_awaitingName` with the name/Confirm/Cancel rows - so the
            // panel came straight back. The log proves it: no watchdog line at all (the
            // keyboard always answered within 5 s), just "Name input started" followed by the
            // keyboard taking the XR focus (session state 5 -> 4) about a second later, i.e. the
            // panel was on screen until the overlay covered it.
            // While the name input waits for the keyboard the panel therefore stays hidden -
            // EXCEPT when the watchdog proved the keyboard dead (_keyboardDead), because then
            // Confirm/Cancel would otherwise be unreachable.
            if (_awaitingName && !_keyboardDead && !_menu.Visible)
            {
                if (!_suppressedRebuildLogged)
                {
                    _suppressedRebuildLogged = true;
                    Debug.Log("[QuestSaber] Menu rebuild suppressed while the name input waits for " +
                              "the OS keyboard (a rebuild here would re-show the old name " +
                              "submenu for a moment).");
                }
                return;
            }

            if (_songListOpen)
            {
                // Song list: CENTER the endless window on the current song. The current
                // song is only highlighted via the background (not selected); the green
                // marker appears only where the laser points.
                // Target height = height of the main menu: this makes the list EXACTLY as tall as the
                // main menu and shows correspondingly more songs.
                _menu.Show(MenuRows(), true, _songIndex, MainMenuPanelHeight());
                return;
            }
            _menu.Show(MenuRows());
        }

        /// <summary>
        /// Pointer beam: comes from the other hand's controller (anchor position +
        /// forward direction) and points at the panel; a hit moves the cursor directly
        /// to the shown row. The laser is deliberately NOT aligned to the saber blade -
        /// the saber keeps its own (reference) orientation at the controller anchor.
        /// </summary>
        private void UpdateMenuPointer()
        {
            if (_menu == null || _menuSide == 0) return;
            Transform pointer = _menuSide == -1 ? _rightAnchor : _leftAnchor;
            if (pointer == null) return;
            _menu.UpdatePointer(pointer.position, pointer.forward);
        }

        private void HandleMenuInput()
        {
            UpdateMenuPointer();

            // ---- X in the menu: QUEUE the next song, but leave the menu OPEN ----
            // The song is only (re)played when leaving the menu. X thus jumps
            // one song further as usual, but no longer closes the menu.
            if (_menuSide != 0 && _input.LeftXWasPressedThisFrame
                && !_inPlayersMenu && !_awaitingName && !_songListOpen)
            {
                if (_songs.Count > 0)
                {
                    _songIndex = (_songIndex + 1) % _songs.Count;
                    _current = _songs[_songIndex];
                    _songSelectionQueued = true; // reload + start on close
                    BuildMenuContent();
                    _audio.PlayUiSfx();
                }
                return;
            }

            // A in the menu: place room under headset and rotate to view direction (Y remains)
            if (_input.RightAWasPressedThisFrame) RecenterRoomOnHead();

            bool act = _menuSide == -1
                ? _input.RightMainTriggerWasPressedThisFrame
                : _input.LeftMainTriggerWasPressedThisFrame;

            // ---- Modal delete confirmation: the window remains visible,
            //      the dialog lies centered above it. The trigger only confirms the choice in the
            //      dialog (yes/no); the rows below are frozen. Shared by the player and the
            //      song delete. ----
            if (_confirmingDelete || _confirmingSongDelete)
            {
                // Never freeze the menu: if the dialog is not (or no longer) on screen the
                // confirmation cannot be answered any more - return to the list
                // instead of swallowing every trigger forever ("delete not handled cleanly").
                if (_menu == null || !_menu.ConfirmVisible)
                {
                    Debug.LogWarning("[QuestSaber] Delete dialog not visible - returning to the " +
                                     "list so the menu stays usable.");
                    _confirmingDelete = false;
                    _pendingDelete = null;
                    _confirmingSongDelete = false;
                    _pendingSongDeleteIndex = -1;
                    LockMenuInput();
                    BuildMenuContent();
                    return;
                }
                if (act && Time.unscaledTime >= _menuInputLockUntil)
                {
                    if (_menu.ConfirmChoice == 0)
                    {
                        if (_confirmingSongDelete) ExecuteSongDelete(); // Yes
                        else ExecuteDelete();
                    }
                    else
                    {
                        if (_confirmingSongDelete) CancelSongDelete();  // No
                        else CancelDeletePopup();
                    }
                    _audio.PlayUiSfx();
                }
                PollKeyboard();
                return;
            }

            if (act)
            {
                if (Time.unscaledTime >= _menuInputLockUntil)
                {
                    // While the OS keyboard is open, the trigger belongs to the keyboard:
                    // do NOT trigger the menu. Otherwise, while typing, e.g. "Confirm
                    // input" is also triggered and the name is empty. Confirmation happens via
                    // the keyboard (Done) or the menu row, AFTER the keyboard is closed.
                    if (!(_awaitingName && KeyboardOffen()))
                    {
                        // Song list: pointing at the row's delete "X" opens the
                        // "Delete song?" dialog INSTEAD of selecting the song. Otherwise the
                        // row under the laser is selected as usual.
                        if (_songListOpen && _menu.PointerOnSongDeleteIcon)
                        {
                            ShowSongDeleteConfirm();
                        }
                        else
                        {
                            // The song list is paged ONLY via the joysticks; the trigger
                            // selects exclusively the row under the laser.
                            int id = _menu.CurrentRowId();
                            OnMenuAction(id);
                        }
                        _audio.PlayUiSfx();
                    }
                }
            }

            // Joystick moves the song list up/down by one entry (hold
            // down = repeat). The list is endless: at the end it continues at the front.
            if (_songListOpen && _menu.Visible)
            {
                // Both Joy-Cons may page - regardless of which side
                // the menu is active on. Whichever of the two sticks is deflected more counts.
                float stickL = _input.LeftThumbY;
                float stickR = _input.RightThumbY;
                float stick = Mathf.Abs(stickL) >= Mathf.Abs(stickR) ? stickL : stickR;
                int dir = stick > 0.5f ? -1 : (stick < -0.5f ? 1 : 0);
                if (dir != 0)
                {
                    if (dir != _stickScrollDir)
                    {
                        _stickScrollDir = dir;
                        _stickScrollTimer = 0f;
                        _menu.ScrollList(dir);
                        _audio.PlayUiSfx();
                    }
                    else
                    {
                        _stickScrollTimer += Time.unscaledDeltaTime;
                        if (_stickScrollTimer >= 0.24f)
                        {
                            _stickScrollTimer = 0f;
                            _menu.ScrollList(dir);
                        }
                    }
                }
                else
                {
                    _stickScrollDir = 0;
                    _stickScrollTimer = 0f;
                }
            }

            PollKeyboard();
        }

        private void OnMenuAction(int id)
        {
            // Diagnostic: one line per trigger press. This makes the whole
            // player-management flow readable in the on-device log - previously a failed
            // "New Player" / "Delete" produced NO trace at all. id -1 = no selectable row
            // under the laser (nothing will happen).
            Debug.Log($"[QuestSaber] Menu action id={id} (players={_inPlayersMenu}, " +
                      $"awaitingName={_awaitingName}, confirm={_confirmingDelete}, " +
                      $"songList={_songListOpen}, allowNew={_allowNewPlayer}).");

            if (_songListOpen)
            {
                if (id >= IdSongBase && id < IdSongBase + _songs.Count)
                {
                    int idx = id - IdSongBase;
                    if (idx >= 0 && idx < _songs.Count)
                    {
                        // Every selection - even of the already loaded song - restarts the song
                        // when the menu closes. "No selection" = let it continue.
                        _songIndex = idx;
                        _current = _songs[idx];
                        _songSelectionQueued = true; // reload on closing the menu
                    }
                    _songListOpen = false;
                    LockMenuInput();
                    BuildMenuContent();
                }
                return;
            }

            // Click on the song title => scrollable song list submenu
            // (works in the main AND in the player management menu)
            if (id == IdSongList)
            {
                _songListOpen = true;
                LockMenuInput();
                BuildMenuContent();
                return;
            }

            if (id == IdDiff)
            {
                ChangeDifficulty();
                BuildMenuContent();
                return;
            }

            if (id < 0 && !_awaitingName) return;

            if (_awaitingName)
            {
                if (id == IdNew) TryCommitName();
                else if (id == IdClose) CancelName();
                return;
            }

            // Player row (applies in player management):
            // switch the active player and stay in the current view (blue marker).
            if (id >= 200 && id < 250)
            {
                int idx = id - 200;
                if (idx >= 0 && idx < _save.Players.Count)
                {
                    _save.SelectedPlayer = _save.Players[idx].Name;
                    _player = _save.Players[idx];
                    ApplyRoomScale();
                    _pendingDelete = null;
                    Save();
                    _songSelectionQueued = true; // player switch restarts the current song
                }
                LockMenuInput();
                BuildMenuContent();
                return;
            }

            if (_inPlayersMenu)
            {
                if (id == IdNew)
                {
                    if (_allowNewPlayer)
                    {
                        BeginNewPlayer();
                        LockMenuInput();
                        // Do NOT rebuild the content - the panel must stay hidden so the
                        // OS keyboard is the only input mask (the rebuild here used to bring the
                        // old name submenu back, see BuildMenuContent).
                        return;
                    }
                }
                else if (id == IdDelete)
                {
                    ShowDeleteConfirm(); // opens the modal dialog (yes/no) above the list
                    return;
                }
                else if (id == IdBack)
                {
                    _inPlayersMenu = false;
                    LockMenuInput();
                }
                BuildMenuContent();
                return;
            }

            switch (id)
            {
                case IdPlayers:
                    _inPlayersMenu = true;
                    _pendingDelete = null;
                    LockMenuInput();
                    BuildMenuContent();
                    break;
                // Single (-)/(+) row: left beam = minus, right beam = plus
                case IdHeight:
                    if (_player != null)
                    {
                        bool hPlus = _menu != null && _menu.LastPointerX >= 0f;
                        _player.Scale = Mathf.Clamp(
                            _player.Scale + (hPlus ? GameConfig.ScaleStep : -GameConfig.ScaleStep),
                            GameConfig.ScaleMin, GameConfig.ScaleMax);
                        ApplyRoomScale(); // live: entire room + sabers scale immediately
                        Save();
                    }
                    BuildMenuContent();
                    break;
                // Field height: left beam = minus, right beam = plus (persistent).
                case IdFieldHeight:
                    {
                        bool fPlus = _menu != null && _menu.LastPointerX >= 0f;
                        _save.FieldHeightMeters = Mathf.Clamp(
                            _save.FieldHeightMeters + (fPlus ? GameConfig.FieldHeightStepMeters : -GameConfig.FieldHeightStepMeters),
                            GameConfig.FieldHeightMinMeters, GameConfig.FieldHeightMaxMeters);
                        ApplyFieldHeight();
                        Save();
                        BuildMenuContent();
                    }
                    break;
                // Depth-follow: room follows the player's forward/backward movement (on/off).
                // The whole row is the interaction surface -> a trigger toggles.
                // Locked in Run-and-Play mode (the follow-along is mandatory there).
                case IdRoomFollow:
                    if (_save.RunAndPlay) break;
                    _save.RoomFollowsPlayer = !_save.RoomFollowsPlayer;
                    Save();
                    BuildMenuContent();
                    break;
                // Toggle Run and Play (forces/saves "room follows player").
                case IdRunPlay:
                    SetRunAndPlay(!_save.RunAndPlay);
                    break;
                // Personal run speed multiplier (left beam = -, right = +).
                case IdRunComfort:
                    if (_player != null)
                    {
                        bool cPlus = _menu != null && _menu.LastPointerX >= 0f;
                        _player.RunComfort = Mathf.Clamp(
                            _player.RunComfort + (cPlus ? GameConfig.RunComfortStep : -GameConfig.RunComfortStep),
                            GameConfig.RunComfortMin, GameConfig.RunComfortMax);
                        Save();
                    }
                    BuildMenuContent();
                    break;
                // Audio offset (IdDelay = 108): left beam = minus, right = plus. Positive =
                // music (audible) later, negative = note clock later (compensation; an
                // audio signal cannot be shifted into the future). 0 = the
                // tuned default.
                case IdDelay:
                    {
                        bool dPlus = _menu != null && _menu.LastPointerX >= 0f;
                        _save.SoundDelayMs = Mathf.Clamp(
                            _save.SoundDelayMs + (dPlus ? GameConfig.AudioOffsetStepMs : -GameConfig.AudioOffsetStepMs),
                            GameConfig.AudioOffsetMinMs, GameConfig.AudioOffsetMaxMs);
                        Save();
                        if (_audio != null) _audio.SetAudioOffset(_save.SoundDelayMs / 1000f);
                        BuildMenuContent();
                    }
                    break;
                case IdPassthrough:
                    // Cycle the background option (persisted) and apply
                    // immediately. Whole row = interaction surface -> a trigger toggles.
                    // MetaSeeThrough respects the seethrough state derived from it
                    // also on watchdog/resume/focus (off stays off).
                    _save.BackgroundMode = (_save.BackgroundMode + 1) % 3;
                    ApplyBackgroundSetting();
                    Save();
                    BuildMenuContent();
                    break;
                // Show/hide the virtual controllers (session, NOT persistent): visible
                // tracking origins + Quest 3 model for comparison with the real controller,
                // and while ON the menu shows the saber calibration page.
                case IdControllerDebug:
                    ControllerDebugView.Toggle();
                    BuildMenuContent();
                    break;
                // Saber fine adjustment, 6 DOF (only on the calibration page).
                case IdSaberX:
                    NudgeSaberOffset(0);
                    break;
                case IdSaberY:
                    NudgeSaberOffset(1);
                    break;
                case IdSaberZ:
                    NudgeSaberOffset(2);
                    break;
                case IdSaberPitch:
                    NudgeSaber(0);
                    break;
                case IdSaberYaw:
                    NudgeSaber(1);
                    break;
                case IdSaberRoll:
                    NudgeSaber(2);
                    break;
                // RETIRED (no menu row any more, so unreachable): "Storage access"
                // opened the ONE system settings page that grants "All files access"
                // (MANAGE_EXTERNAL_STORAGE). Kept so a future build can restore the row.
                case IdSongFolder:
                    if (SafSongFolder.IsAndroid)
                    {
                        // The real capability decides, not the permission flag.
                        if (SafSongFolder.CanReadDownload)
                        {
                            PrepareDownloadFolder();
                            _songFolderUsable = true;
                            RequestSongScan("menu", force: true);
                        }
                        else
                        {
                            string note = SafSongFolder.RequestAllFilesAccess();
                            Debug.Log("[QuestSaber] Storage settings page: " +
                                      (string.IsNullOrEmpty(note) ? "opened" : note));
                            DebugLogFile.Mark("storage access menu: " +
                                              (string.IsNullOrEmpty(note) ? "opened" : note));
                        }
                    }
                    else
                    {
                        Debug.Log("[QuestSaber] Storage access is Android-only.");
                    }
                    BuildMenuContent();
                    break;
                // RETIRED (unreachable): "Check new songs" - the scan runs on every
                // app start and on every resume, so a manual refresh row is not needed.
                case IdRescanSongs:
                    LockMenuInput();
                    RequestSongScan("menu", force: true);
                    BuildMenuContent();
                    break;
            }
        }

        /// <summary>
        /// Change difficulty (standard characteristic only; special modes are never loaded).
        /// The choice is persisted globally and applied when the menu closes.
        /// </summary>
        private void ChangeDifficulty()
        {
            if (_current == null || _current.Difficulties.Count <= 1) return;
            bool dPlus = _menu != null && _menu.LastPointerX >= 0f;
            int n = _current.Difficulties.Count;
            _current.SelectedDifficulty = dPlus
                ? (_current.SelectedDifficulty + 1) % n
                : (_current.SelectedDifficulty - 1 + n) % n;
            _current.ApplySelectedDifficulty();
            _songSelectionQueued = true; // on close it is reloaded with the new chart
            SaveDifficultyChoice();
        }

        /// <summary>
        /// Short lock after view changes: prevents a second button press from
        /// immediately triggering a now differently positioned row (e.g. double name input).
        /// Value rows (size/delay/difficulty) are unaffected by it (fast clicking).
        /// </summary>
        private void LockMenuInput() => _menuInputLockUntil = Time.unscaledTime + 0.4f;

        /// <summary>
        /// Applies the saved "field height" (<see cref="SaveData.FieldHeightMeters"/>) as a
        /// vertical offset of ALL objects above the play field floor (NoteView/WallView). The
        /// floor stays; floor-standing walls are extended down to the floor. The hit/score
        /// calculation reads the same position (note box = display).
        /// </summary>
        private void ApplyFieldHeight()
        {
            GameConfig.FieldHeightOffsetMeters = _save != null ? _save.FieldHeightMeters : 0f;
        }

        /// <summary>Short text of a background mode for the menu row.</summary>
        private static string BackgroundModeText(int mode)
        {
            switch ((Environment.BackgroundMode)Mathf.Clamp(mode, 0, 2))
            {
                case Environment.BackgroundMode.Default: return "Default";
                case Environment.BackgroundMode.Animated: return "Animated";
                default: return "Passthrough";
            }
        }

        /// <summary>
        /// Applies the saved audio offset to the music (start + after loading).
        /// See AudioManager.SetAudioOffset / AudioDelayFilter (DSP delay of the music).
        /// </summary>
        private void RefreshAudioOffset()
        {
            if (_audio != null) _audio.SetAudioOffset(_save.SoundDelayMs / 1000f);
        }

        /// <summary>
        /// Applies the persistent background option (passthrough / default / animated):
        /// passthrough = video seethrough on; default/animated = seethrough off, camera
        /// opaque and the ArcViewer default environment visible (animated additionally plays the
        /// song lightshow). Called at start and on every menu toggle;
        /// MetaSeeThrough re-applies the seethrough state also after session/resume/focus
        /// changes.
        /// </summary>
        private void ApplyBackgroundSetting()
        {
            var mode = (Environment.BackgroundMode)Mathf.Clamp(_save.BackgroundMode, 0, 2);
            bool seethrough = mode == Environment.BackgroundMode.Passthrough;

            // Seethrough chain (camera + AR passthrough) - also controls the camera background.
            MetaSeeThrough.SetEnabled(seethrough);

            if (_environment == null) return;
            // First choose the song environment (sets parameters + desired scene),
            // then apply the mode (loads the scene only for default/animated).
            if (mode != Environment.BackgroundMode.Passthrough && _current != null)
                _environment.SetEnvironment(_current.EnvironmentName, _toSeconds);
            _environment.ApplyMode(mode);
            // On mode change, re-submit the lightshow of the current map
            // (default -> calm, animated -> events of the selected difficulty).
            if (_current != null && _toSeconds != null && _chart != null)
                _environment.LoadLightshow(_chart, _current, _toSeconds);
        }

        // ---- Player management (on-screen keyboard) ----

        /// <summary>After this many seconds without a single typed character the OS keyboard is
        /// treated as "not usable": the trigger is released and the fallback input rows come back
        /// (reworded for the hidden-menu name input). Generous, because the
        /// menu is hidden while typing - the user must not be interrupted for merely thinking.
        /// 5 s proved too tight once the root cause of "the keyboard never appears" was
        /// found and fixed (see BeginNewPlayer), so the limit is 15 s again.
        /// Note: the submenu flash the player saw was NOT this watchdog - the
        /// log shows the keyboard always answered well inside 5 s and no watchdog line was
        /// ever written. The flash came from a menu rebuild after BeginNewPlayer (see
        /// BuildMenuContent). The limit is generous again because this is only the dead-keyboard
        /// safety net. While the app is really backgrounded (Application.isFocused false) it is
        /// doubled - note that the overlay keyboard does NOT clear that flag (measured in the
        /// log: "APP: focus = True" for the whole session).</summary>
        private const float KeyboardDeadAfterSeconds = 15f;

        private void BeginNewPlayer()
        {
            // Double protection: lock immediately - even a second call in the same
            // menu opening (e.g. through stuck keyboard events) can no longer open anything.
            if (!_allowNewPlayer) return;
            _allowNewPlayer = false;
            _awaitingName = true;
            _keyboardWasVisible = false;
            _keyboardGotText = false;
            _keyboardDead = false;
            _keyboardWaitLogged = false;
            _suppressedRebuildLogged = false;
            _keyboardOpenedAt = Time.unscaledTime;
            _pendingName = "";
            // Meta keyboard overlay: the system keyboard is the ONLY supported way to type in
            // VR. It needs the Meta "Require System Keyboard" project settings
            // (MetaQuestFeature.enableSystemKeyboard + OVRProjectConfig.requiresSystemKeyboard)
            // - without them Horizon OS never shows it even though Unity returns an object.
            //
            // the actual root cause of "no keyboard ever appears": the ANDROID
            // APPLICATION ENTRY POINT. With Unity 6's GameActivity entry point the keyboard can
            // never be displayed: TouchScreenKeyboard.Open returns status=Visible and stays there
            // forever while Horizon OS never draws the overlay (Unity bug, reported for Quest 2/3
            // with Unity 6.5; TMP/UI Toolkit input fields are equally affected - see
            // PROJECT_NOTES.md). The build therefore enforces the classic ACTIVITY entry point
            // (BuildAndroid.PerformBuild + Assets/Plugins/Android/AndroidManifest.xml). The
            // request below is unchanged and works as documented again with that entry point.
            _keyboard = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.Default, false,
                false, false, false, "Player name");
            _keyboardStatusLog = -1;
            if (_keyboard == null)
            {
                // Do not leave the menu in a dead end: without a keyboard object there is no
                // way to type - release the state immediately and tell the user in the log.
                Debug.LogWarning("[QuestSaber] Name input: TouchScreenKeyboard.Open returned NULL " +
                                 "- no on-screen keyboard available on this device.");
                CancelName();
                return;
            }
            Debug.Log($"[QuestSaber] Name input started - keyboard opened " +
                      $"(status={_keyboard.status}, active={_keyboard.active}).");
            // NO sub-menu: the OS keyboard IS the input mask. The hand menu is hidden while
            // typing; ENTER/Done on the keyboard commits the name (TryCommitName) and brings the
            // player list back. Only if the keyboard never appears does PollKeyboard bring the
            // fallback panel back (Confirm input / Cancel).
            if (_menu != null) _menu.Hide();
        }

        /// <summary>
        /// true as long as the OS keyboard owns the trigger. A keyboard that only
        /// CLAIMS to be open but never delivered anything (the system overlay does not appear)
        /// must not swallow the menu input forever - otherwise name input is a dead end with
        /// no way to cancel. After <see cref="KeyboardDeadAfterSeconds"/> without a single typed
        /// character the input is released again; the log records it (see PollKeyboard).
        /// </summary>
        private bool KeyboardOffen()
        {
            if (_keyboardDead) return false;
            return _keyboard != null &&
                   (_keyboard.active || _keyboard.status == TouchScreenKeyboard.Status.Visible);
        }

        private void CancelName()
        {
            if (!_awaitingName) return;
            DismissKeyboard(); // forcibly close a stuck OS keyboard
            _awaitingName = false;
            _keyboard = null;
            _keyboardGotText = false;
            _keyboardDead = false;
            _keyboardWaitLogged = false;
            _allowNewPlayer = true; // further players possible directly one after another
            LockMenuInput();
            BuildMenuContent();
        }

        /// <summary>Forcibly close the OS keyboard - prevents a "second input prompt" from
        /// getting stuck after committing and allowing further typing.
        /// (Unity 6: 'active' is an instance property; false ends the input.)
        /// Deliberately WITHOUT an "active" check: on Quest/Android 'active' may already report false
        /// while the keyboard is still shown - the call is then harmless.</summary>
        private void DismissKeyboard()
        {
            try
            {
                if (_keyboard != null) _keyboard.active = false;
            }
            catch { /* platform without support: ignore */ }
        }

        private void PollKeyboard()
        {
            if (_keyboard == null) return;

            // Watchdog (reworked): the Quest system keyboard
            // overlay can fail to appear while Unity still reports it as open. Because the menu is
            // hidden during name input, a permanently invisible keyboard would be a dead end - so
            // after a few seconds without a single typed character the FALLBACK panel (Confirm
            // input / Cancel) is brought back and the trigger is released for it again.
            //
            // This is NOT what "the old submenu flashed up" was - the log shows
            // the keyboard always delivered within 5 s and no watchdog line was ever written (that
            // flash came from a menu rebuild, see BuildMenuContent). What stays:
            // (a) the limit is 15 s instead of 5, so a thinking player is not interrupted,
            // (b) while the app is really backgrounded (Application.isFocused false) it is
            // doubled - measured caveat: the overlay keyboard does NOT clear that flag.
            // The wait is logged once per attempt, so the next headset report shows the reason.
            if (_awaitingName && !_keyboardGotText && !_keyboardDead)
            {
                bool focused = Application.isFocused;
                float wait = focused ? KeyboardDeadAfterSeconds : KeyboardDeadAfterSeconds * 2f;
                float waited = Time.unscaledTime - _keyboardOpenedAt;
                if (waited > wait)
                {
                    _keyboardDead = true;
                    Debug.LogWarning("[QuestSaber] OS keyboard reported status=" + _keyboard.status +
                                     " (active=" + _keyboard.active + ", appFocused=" + focused +
                                     ") but nothing was typed for " + waited.ToString("F1") + " s -> showing " +
                                     "the fallback input rows so Cancel/Confirm stay usable. If this appears " +
                                     "and the keyboard overlay itself is broken again - " +
                                     "check the application entry point (must be Activity, NOT GameActivity: " +
                                     "Unity 6 cannot show the system keyboard with GameActivity) and the " +
                                     "oculus.software.overlay_keyboard uses-feature in the merged manifest.");
                    BuildMenuContent();
                }
                else if (!_keyboardWaitLogged)
                {
                    _keyboardWaitLogged = true;
                    Debug.Log("[QuestSaber] Name input: waiting for the OS keyboard (status=" +
                              _keyboard.status + ", active=" + _keyboard.active + ", appFocused=" +
                              focused + ", fallback after " + wait.ToString("F0") + " s).");
                }
            }

            // Diagnostic: log status/text changes (Quest/Android reports "Done"/"LostFocus"
            // sometimes delayed or not at all - this makes the flow traceable in the log).
            int status = (int)_keyboard.status;
            if (status != _keyboardStatusLog)
            {
                _keyboardStatusLog = status;
                Debug.Log($"[QuestSaber] Keyboard status={_keyboard.status}, active={_keyboard.active}, " +
                          $"visible={_keyboardWasVisible}, text='{_keyboard.text}'.");
            }

            // Record text EVERY frame (not only in the "Visible" status): Android/Quest
            // sometimes lags behind on the status. This way the typed name is never lost, and
            // 'text' is often already empty on Done - the buffered value is the fallback.
            string typed = _keyboard.text;
            if (!string.IsNullOrEmpty(typed) && typed != _pendingName)
            {
                bool wasDead = _keyboardDead;
                _pendingName = typed;
                _keyboardWasVisible = true;
                _keyboardGotText = true;  // real input arrived -> the keyboard works
                _keyboardDead = false;    // ...so it owns the trigger again
                // The keyboard turned out to work after the fallback panel had come back (e.g.
                // the user simply needed longer than the watchdog): hide the panel again - the
                // OS keyboard alone is the input mask, see BeginNewPlayer.
                if (wasDead && _awaitingName && _menu != null && _menu.Visible) _menu.Hide();
            }
            if (_keyboard.status == TouchScreenKeyboard.Status.Visible || _keyboard.active)
                _keyboardWasVisible = true;

            switch (_keyboard.status)
            {
                case TouchScreenKeyboard.Status.Done:
                    TryCommitName();
                    return;
                case TouchScreenKeyboard.Status.LostFocus:
                    // Quest/Android sometimes closes the keyboard on confirm with "LostFocus"
                    // instead of "Done". Only treat it as confirmation if the keyboard was really
                    // open before OR text was already typed - this avoids a false commit
                    // directly after opening.
                    if (_keyboardWasVisible || !string.IsNullOrEmpty(_pendingName))
                        TryCommitName();
                    return;
                case TouchScreenKeyboard.Status.Canceled:
                    CancelName();
                    return;
            }

            // No Done/Canceled/LostFocus, but the keyboard was visible and is now
            // closed: treat as confirmation (Quest sometimes closes without a clear status).
            if (_keyboardWasVisible && !_keyboard.active)
                TryCommitName();
        }

        private void TryCommitName()
        {
            // Guard: keyboard Done and trigger on "Confirm input" can land in the same
            // instant - only the first commit counts (otherwise "Player N+1" would be created).
            if (!_awaitingName) return;

            // Read the name BEFORE closing the keyboard: 'text' is often empty on Done status
            // or after hiding, so fall back to the buffered typed text.
            string typed = _keyboard != null && !string.IsNullOrEmpty(_keyboard.text)
                ? _keyboard.text
                : _pendingName;
            string name = (typed ?? "").Trim();

            Debug.Log($"[QuestSaber] Commit name: keyboardText='{(_keyboard != null ? _keyboard.text : "")}', " +
                      $"pending='{_pendingName}', status={(_keyboard != null ? _keyboard.status.ToString() : "-")}.");

            DismissKeyboard(); // forcibly close the keyboard so no second input follows
            _keyboard = null;
            _awaitingName = false;
            _keyboardGotText = false;
            _keyboardDead = false;
            _keyboardWaitLogged = false;
            _allowNewPlayer = true; // further players possible directly one after another

            if (string.IsNullOrEmpty(name)) name = "Player " + (_save.Players.Count + 1);

            foreach (var p in _save.Players)
            {
                if (p.Name == name)
                {
                    _save.SelectedPlayer = p.Name;
                    _player = p;
                    ApplyRoomScale();
                    Save();
                    _songSelectionQueued = true; // active player changed -> restart song
                    _inPlayersMenu = true; // back to the player list (name visible there)
                    LockMenuInput();
                    BuildMenuContent();
                    return;
                }
            }

            var fresh = new PlayerProfile { Name = name };
            _save.Players.Add(fresh);
            _save.SelectedPlayer = name;
            _player = fresh;
            ApplyRoomScale();
            Save();
            _songSelectionQueued = true; // new active player -> restart song
            _inPlayersMenu = true; // back to the player list (new player visible)
            LockMenuInput();
            BuildMenuContent();
        }

        /// <summary>
        /// Opens the delete confirmation as a MODAL dialog CENTERED above player management.
        /// The current window REMAINS visible (it is not replaced by the confirmation rows);
        /// only the dialog catches the trigger. "Yes"/"No" are chosen via the same
        /// laser marker as the rest of the menu. Beforehand, any still open
        /// on-screen keyboard is closed so it does not cover the dialog.
        /// </summary>
        private void ShowDeleteConfirm()
        {
            if (_save.Players.Count == 0)
            {
                // Nothing to delete (the row is hidden in that case, so this is belt+braces).
                Debug.LogWarning("[QuestSaber] Delete: no players to delete.");
                return;
            }
            _pendingDelete = _save.SelectedPlayer;
            // Safety against old/hand-edited saves: SelectedPlayer empty OR naming a player that
            // no longer exists -> fall back to the first existing profile.
            bool exists = false;
            foreach (var p in _save.Players)
                if (p.Name == _pendingDelete) { exists = true; break; }
            if (string.IsNullOrEmpty(_pendingDelete) || !exists)
                _pendingDelete = _save.Players[0].Name;
            _confirmingDelete = true;
            DismissKeyboard(); // no stuck OS keyboard over the dialog
            LockMenuInput();
            Debug.Log($"[QuestSaber] Open delete dialog for '{_pendingDelete}' " +
                      $"(players={_save.Players.Count}, selected='{_save.SelectedPlayer}').");
            if (_menu == null)
            {
                // Without a menu the dialog can never be answered -> do not freeze the input.
                Debug.LogWarning("[QuestSaber] Delete: no HandMenu - aborting confirmation.");
                _confirmingDelete = false;
                _pendingDelete = null;
                return;
            }
            _menu.ShowConfirm("Delete player?", Shorten(_pendingDelete, 18));
        }

        /// <summary>
        /// Cancels the delete confirmation and returns to player management (the window
        /// never disappeared anyway - here only the dialog is hidden again).
        /// </summary>
        private void CancelDeletePopup()
        {
            _confirmingDelete = false;
            _pendingDelete = null;
            if (_menu != null) _menu.HideConfirm();
            LockMenuInput();
        }

        /// <summary>Deletes the player approved in the confirmation; stays in player management afterward.</summary>
        private void ExecuteDelete()
        {
            string name = _pendingDelete;
            _pendingDelete = null;
            _confirmingDelete = false;
            if (_menu != null) _menu.HideConfirm();

            int before = _save.Players.Count;
            if (!string.IsNullOrEmpty(name))
                _save.Players.RemoveAll(p => p.Name == name);
            if (_save.Players.Count == 0)
                _save.Players.Add(new PlayerProfile { Name = "Player 1" });

            // Never leave a dangling selection: if the active player was the deleted one, take
            // the first remaining profile. This is what the grip label reads from, so the
            // engraved name always matches a profile that really exists.
            PlayerProfile next = null;
            foreach (var p in _save.Players)
                if (p.Name == _save.SelectedPlayer) { next = p; break; }
            if (next == null)
            {
                next = _save.Players[0];
                _save.SelectedPlayer = next.Name;
            }
            _player = next;
            ApplyRoomScale();
            Save();
            _songSelectionQueued = true; // deleting the active player restarts the song
            LockMenuInput();
            BuildMenuContent();
            Debug.Log($"[QuestSaber] Deleted player '{name}' (players {before} -> " +
                      $"{_save.Players.Count}); active is now '{_save.SelectedPlayer}'.");
            // Stay in player management (no switch to the main menu).
        }

        // ======================================================================
        // Song delete: the delete "X" in the song list opens the same modal
        // confirmation as the player delete; "Yes" removes the song's folder from disk.
        // ======================================================================

        /// <summary>
        /// Opens the "Delete song?" confirmation for the song whose delete "X" the laser
        /// points at (song list). The list stays visible; only the dialog catches the trigger.
        /// </summary>
        private void ShowSongDeleteConfirm()
        {
            if (!_songListOpen || _menu == null) return;

            int id = _menu.CurrentRowId();
            if (id < IdSongBase || id >= IdSongBase + _songs.Count)
            {
                Debug.LogWarning("[QuestSaber] Delete song: no song under the X (id=" + id + ").");
                return;
            }
            int idx = id - IdSongBase;
            if (idx < 0 || idx >= _songs.Count) return;

            SongMeta song = _songs[idx];
            if (song.IsDemo)
            {
                // The bundled demo song is not deletable (the X is hidden for it, belt+braces).
                Debug.Log("[QuestSaber] Delete song: ignored for the bundled demo song.");
                return;
            }

            _pendingSongDeleteIndex = idx;
            _confirmingSongDelete = true;
            DismissKeyboard(); // no stuck OS keyboard over the dialog
            LockMenuInput();
            Debug.Log($"[QuestSaber] Open song delete dialog for '{song.SongName}' " +
                      $"(folder='{song.FolderPath}', songs={_songs.Count}).");
            _menu.ShowConfirm("Delete song?", Shorten(song.SongName, 20));
        }

        /// <summary>Cancels the song delete confirmation and returns to the (unchanged) song list.</summary>
        private void CancelSongDelete()
        {
            _confirmingSongDelete = false;
            _pendingSongDeleteIndex = -1;
            if (_menu != null) _menu.HideConfirm();
            LockMenuInput();
        }

        /// <summary>
        /// Deletes the song folder approved in the confirmation and rebuilds the (still open)
        /// song list from the in-memory list. Never deletes the bundled demo song and never
        /// deletes anything outside one of the known song roots (safety against a bad save).
        /// The song only reloads when the menu is closed (the current song keeps playing
        /// otherwise).
        /// </summary>
        private void ExecuteSongDelete()
        {
            int idx = _pendingSongDeleteIndex;
            _pendingSongDeleteIndex = -1;
            _confirmingSongDelete = false;
            if (_menu != null) _menu.HideConfirm();

            if (idx < 0 || idx >= _songs.Count)
            {
                BuildMenuContent();
                return;
            }

            SongMeta song = _songs[idx];
            string folder = song.FolderPath;
            string error = null;
            bool deleted = false;

            if (song.IsDemo) error = "bundled demo song";
            else if (string.IsNullOrEmpty(folder)) error = "no folder path";
            else if (!IsInsideSongRoot(folder)) error = "outside the song folders";
            else
            {
                try
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, true);
                        deleted = true;
                    }
                    else error = "folder not found";
                }
                catch (Exception ex) { error = ex.Message; }
            }

            if (deleted)
            {
                bool wasCurrent = idx == _songIndex || (_current != null
                    && !string.IsNullOrEmpty(folder)
                    && string.Equals(folder, _current.FolderPath, StringComparison.OrdinalIgnoreCase));

                _songs.RemoveAt(idx);
                if (_songs.Count == 0)
                {
                    // Never leave the list empty: rescan (falls back to the bundled demo song).
                    _songs = BeatmapParser.LoadSongList();
                }

                if (wasCurrent && _songs.Count > 0)
                {
                    _songIndex = Mathf.Clamp(idx, 0, _songs.Count - 1);
                    _current = _songs[_songIndex];
                    _songSelectionQueued = true; // reload cleanly when the menu closes
                }
                else if (idx < _songIndex)
                {
                    _songIndex--; // keep pointing at the same song
                }
            }

            LockMenuInput();
            BuildMenuContent();

            Debug.Log($"[QuestSaber] Song delete '{song.SongName}': " +
                      (deleted ? "deleted" : "FAILED (" + error + ")") +
                      $", folder='{folder}', songs now={_songs.Count}, index={_songIndex}.");
        }

        /// <summary>Safety: a song folder must lie INSIDE one of the known song roots
        /// (never the root itself, never an arbitrary path).</summary>
        private static bool IsInsideSongRoot(string folder)
        {
            try
            {
                string full = Path.GetFullPath(folder).TrimEnd('\\', '/');
                foreach (var root in GameConfig.SongsRootCandidates)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string r = Path.GetFullPath(root).TrimEnd('\\', '/');
                    if (full.Length <= r.Length) continue;
                    if (full.Equals(r, StringComparison.OrdinalIgnoreCase)) continue;
                    if (full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || full.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

    }
}

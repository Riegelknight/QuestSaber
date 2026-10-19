// GameManager: central orchestrator (songs, scheduler, hits, score, menu, room).
// Split into partial files GameManager.*.cs by responsibility.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace QuestSaber
{
    public enum GamePhase { Boot, Playing, Menu }

    /// <summary>
    /// Central orchestrator: songs + charts, scheduler, note/wall spawn,
    /// scoring system (115-scheme + x1..x8), endless loop, hand menu,
    /// player management, room alignment/quit and template cleanup.
    /// </summary>
    public sealed partial class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        public GamePhase Phase { get; private set; } = GamePhase.Boot;
        public PlayerProfile Player => _player;
        public SaveData Settings => _save;

        /// <summary>
        /// FIXED SCORE clock (seconds) - the reference for hitboxes and sabers. It is
        /// moved by NO menu offset (see PROJECT_NOTES §4, three layers).
        /// </summary>
        public static float ScoreTime => Instance != null && Instance._audio != null
            ? Instance._audio.ScoreTime : 0f;

        /// <summary>
        /// DISPLAY clock (seconds) = score clock + note offset. Only for display
        /// (notes/walls/effects); hit detection and sabers use the score clock.
        /// </summary>
        public static float VisualTime => Instance != null && Instance._audio != null
            ? Instance._audio.VisualTime : 0f;

        // Components
        private AudioManager _audio;
        private ControllerInput _input;
        private HudDisplay _hud;
        private HandMenu _menu;

        // Songs / charts
        private List<SongMeta> _songs = new List<SongMeta>();
        private int _songIndex = -1;
        private SongMeta _current;
        private BeatmapChart _chart;
        // Schedule per note incl. the pattern-dependent position effects (stack/flip),
        // so they do NOT have to be computed per frame (BuildSchedule -> Spawn).
        // startYRel is stored RELATIVE to the target row (field-height independent), so
        // a change of the "field height" also automatically carries already prepared notes.
        private List<(float spawn, NoteModel note, float startYRel, float flipStartX, float flipYHeight)> _noteSchedule
            = new List<(float, NoteModel, float, float, float)>();
        private int _noteCursor;
        private List<(float spawn, ObstacleModel obs, float durSec)> _wallSchedule = new List<(float, ObstacleModel, float)>();
        private int _wallCursor;
        private readonly List<WallView> _activeWalls = new List<WallView>();
        // Active notes for the geometric hit detection (cheap): filled at spawn,
        // thinned out on consumption/pass-through. No physics access needed.
        private readonly List<NoteView> _activeNotes = new List<NoteView>();

        // Arcs (pure visual) and chains (visual + link hits).
        private List<(float spawn, ArcModel arc)> _arcSchedule = new List<(float, ArcModel)>();
        private int _arcCursor;
        private readonly List<ArcView> _activeArcs = new List<ArcView>();
        private List<(float spawn, ChainModel chain)> _chainSchedule = new List<(float, ChainModel)>();
        private int _chainCursor;
        private readonly List<ChainView> _activeChains = new List<ChainView>();

        // Jump/spawn parameters of the current song - from the song data (NJS/HJD/
        // NoteJumpStartBeatOffset). The motion is CONTINUOUS with NJS; spawn time =
        // beat - 2 x ReactionTime (full jump duration). The pattern-dependent position effects
        // (stack/flip) are computed by BuildSchedule per note; NoteView executes them time-based.
        private float _njs;              // map tempo (base units/s)
        private float _reactionTime;     // seconds until half jump distance
        private float _halfJumpDistance; // meters (base units), for the stack parabola
        private float _spawnLeadTime;    // seconds spawn -> beat (2 x ReactionTime)
        // beat->seconds of the current map (respects the BPM curve: _BPMChanges/bpmEvents)
        private System.Func<float, float> _toSeconds;

        // Persistence
        private SaveData _save = new SaveData();
        private PlayerProfile _player;

        // Save performance (see PROJECT_NOTES.md §10): the values are immediately in
        // memory, the file write is batched (max. 1x per second) and explicitly flushed
        // at song end/quit - no disk I/O + JSON serialization on
        // every record slice (TrySaveBestImmediately calls Save() per slice).
        private static bool _saveDirReady;   // Directory.CreateDirectory only 1x per run
        private float _lastSaveTime = float.MinValue;
        private bool _savePending;
        private const float SaveThrottleSeconds = 1f;

        // Score
        private int _combo;
        // Multiplier tier index (0..3 -> x1/x2/x4/x8) + hits within the tier.
        // 1:1 like original/ArcViewer (ScoringUtils.ManuallyAdvanceCombo) - deliberately
        // DECOUPLED from the combo counter, so after a mistake the tier drops only ONE tier
        // (e.g. x8 -> x4) and continues from there with its own progress.
        private int _multTier;
        private int _comboProgress;
        private long _score;

        // Floating Score (reference: InGameUiView.DisplaySliceScore - score/"MISS" at the note position)
        private readonly Stack<TMPro.TextMeshPro> _scoreTextPool = new Stack<TMPro.TextMeshPro>();
        private const float ScoreSpawnForwardOffset = 2.5f;  // reference: _spawnForwardOffset
        private const float ScoreSpawnDownwardOffset = 1f;   // reference: _spawnDownwardOffset
        private const float ScoreFloatDistance = 1.5f;       // reference: _floatForwardDistance
        private const float ScoreDisplayDuration = 1f;       // reference: _sliceScoreDisplayDuration

        // Rig & world
        private Camera _cam;
        private Transform _cameraOffset;
        private Transform _leftAnchor, _rightAnchor;
        // Room anchoring: the room/notes stand FIXED on the
        // world origin during play - head movements do not move the game world. The floor comes
        // via the tracking origin "Floor" (determined at game start), not from a
        // head-height calculation.
        //  - _roomYawDeg: yaw angle of the room. Purely visual and SESSION-RELATED. The right
        //    stick rotates continuously; button A sets it to the current view direction.
        //  - _floorOffsetMeters: SESSION floor offset (left stick up/down). Starts at
        //    0 on EVERY game start and is NOT saved - button A does not change the height.
        //    Larger deadzone against accidental deflection (GameConfig.FloorOffsetDeadzone).
        //  - _roomFollowOffset: world X/Z of the PLAYER anchor (0/0 at start). The room origin
        //    (score zone) lies PlayerDistanceBehindZone in front of it (ApplyRoomAnchor). Button A sets
        //    it to the head X/Z (player at the rear position); with active "room follows player"
        //    depth-follow (menu), head deltas additionally accumulate along the room forward axis.
        //    _lastFollowHead remembers the head position of the previous frame.
        private float _roomYawDeg;
        private float _floorOffsetMeters;
        private Vector3 _roomFollowOffset;
        private Vector3 _lastFollowHead;
        private bool _followInit;

        // Run and Play: buffers the room's forward speed and provides state +
        // smoothed speed (0.3 s). Active only in the Playing state without a menu.
        // `_roomFollowBeforeRun` remembers the "room follows player" value so it can be
        // restored when the mode is switched off (the mode forces/saves it).
        private readonly RunAndPlay _runAndPlay = new RunAndPlay();
        private bool _roomFollowBeforeRun;
        private readonly List<InputDevice> _scratchDevices = new();
        // Reused subsystem list (floor/recenter queries): allocation-free.
        private readonly List<XRInputSubsystem> _subsystemScratch = new(4);
        private Transform _gameRoot;
        private Environment.EnvironmentDirector _environment;
        private SaberView _saberL, _saberR;
        private GripCurvedLabel _labelName, _labelBest;
        private readonly Dictionary<SaberSide, InputDevice> _devices = new Dictionary<SaberSide, InputDevice>();
        // Contact haptics (saber<->saber, saber<->wall): throttle per hand. During
        // contact a light impulse is sent at short intervals (continuous
        // vibration) without sending every frame; when contact ends, the timer is reset.
        private readonly float[] _contactHapticNext = new float[2];
        // Arc interaction: throttle per hand (haptics + tip particles), index = (int)SaberSide.
        private readonly float[] _arcHapticNext = new float[2];
        private readonly float[] _arcParticleNext = new float[2];
        // Saber clash: edge-triggered (only on the transition "no contact -> contact").
        private bool _saberClashing;

        // Menu state
        private int _menuSide; // 0 closed, -1 left, +1 right
        private GamePhase _phaseBeforeMenu;
        private bool _inPlayersMenu;
        private bool _awaitingName;
        private bool _songListOpen;        // scrollable song list submenu (via click on song title)
        private bool _songSelectionQueued; // song/difficulty changed in the menu => reload on close
        private float _menuInputLockUntil; // prevents double triggering after a view change (short lock)
        private bool _allowNewPlayer = true; // prevents double opening of the name input
        private int _stickScrollDir;   // joystick paging in the song list (0 = no stick)
        private float _stickScrollTimer;
        // When the menu opens, BOTH lightsabers automatically retract.
        // As a result, the only "beam" that remains visible is the laser/cursor of the other hand -
        // the menu page no longer glows (no permanently visible weak beam).
        // The cursor appears independently of the saber state. On close, a saber only
        // extends again automatically if its state was NOT changed manually during the menu
        // (grip double-click). Index = (int)SaberSide (Left=0, Right=1).
        private readonly bool[] _menuAutoRetracted = new bool[2];  // per side automatically retracted
        private readonly bool[] _menuManualChanged = new bool[2];  // per side manually changed
        private string _pendingDelete;
        private bool _confirmingDelete;   // modal delete popup active (menu frozen)
        // Song delete: the same modal dialog, but for a song folder.
        private bool _confirmingSongDelete;
        private int _pendingSongDeleteIndex = -1;
        private TouchScreenKeyboard _keyboard;
        private bool _keyboardWasVisible;   // keyboard was visible at least once (no false commit on open)
        private int _keyboardStatusLog = -1; // last logged keyboard status (diagnostic)
        // Watchdog: the Quest system keyboard overlay can fail to appear while Unity
        // still reports "open". Without this the name input would swallow every trigger
        // forever ("New Player" = dead end, no way to Cancel).
        private float _keyboardOpenedAt;    // unscaled time of the last TouchScreenKeyboard.Open
        private bool _keyboardGotText;      // the keyboard really delivered characters at least once
        private bool _keyboardDead;         // keyboard claims to be open but delivered nothing
        // The fallback is only shown while the app still OWNS the input focus; if the
        // overlay keyboard took it (which is what it does while it is on screen) the wait doubles,
        // so a working keyboard is never interrupted. Logged once per wait (diagnostic).
        // Note (measured): Application.isFocused does NOT change while the system keyboard
        // is up - the log shows "APP: focus = True" for the whole session while the XR
        // session state went 5 (focused) -> 4 (visible only) for exactly the two name inputs. So
        // this flag only covers real backgrounding; the 15 s limit is what keeps the overlay safe.
        private bool _keyboardWaitLogged;
        // Reported once when a menu rebuild was suppressed because the name input is
        // waiting for the OS keyboard (proof line in the log that the old submenu can no longer
        // flash up, see BuildMenuContent).
        private bool _suppressedRebuildLogged;
        private string _pendingName = "";   // last typed name (fallback if 'text' is empty on Done)
        private bool _songEnded;
        private Coroutine _audioLoad;
        // Last displayed grip label texts (comparison instead of dirty flag: records and
        // player changes take effect immediately, without waiting for a song change).
        private string _labelNameText = "";
        private string _labelBestText = "";

        // ---- Song folder import (Android system folder chooser / SAF, no permission) ----
        // _songImportStatus is only shown in the menu (e.g. "imported" / "nothing new"),
        // _lastSongScanAt throttles the focus-driven scans. The import itself is idempotent:
        // songs that were moved out once are gone from the picked folder, so a repeated scan
        // only finds new downloads.
        private string _songImportStatus = "";
        private bool _songImportBusy;
        // Did the last java import worker hit the 180 s safety timeout? (A timed out
        // import must not be reported as "nothing new".)
        private bool _lastImportTimedOut;
        private float _lastSongScanAt = -999f;
        // Is the shared Download folder usable (real write probe)? Checked once at start so
        // the menu does not have to ask java on every rebuild.
        private bool _songFolderUsable;

        private const int IdPlayers = 103, IdHeight = 106,
                          IdDelay = 108,
                          IdClose = 109, IdDiff = 110, IdSongList = 120,
                          // Developer switch: passthrough on/off (global, persistent).
                          IdPassthrough = 113,
                          IdVisual = 114,
                          // Vertical offset for notes/mines + floor-free walls (persistent).
                          IdFieldHeight = 111,
                          // Depth-follow: room moves with forward/backward movement (persistent).
                          IdRoomFollow = 112,
                          // Run and Play (global, persistent) + personal run speed control.
                          IdRunPlay = 115, IdRunComfort = 116,
                          // Visual check of the tracking poses + saber calibration (session,
                          // NOT persistent). With "Controllers & sabers" ON the menu switches to
                          // the calibration page, which adjusts the SABER in all six degrees of
                          // freedom: X/Y/Z (position in the anchor frame, X mirrored) and
                          // pitch/yaw/roll (direction). Every row is a DELTA on top of the baked
                          // values in GameConfig and steps in 1 mm / 1 deg.
                          // The rows that moved the virtual CONTROLLER MODEL ("Model X/Y/Z",
                          // "Model yaw") are GONE - the models are finished (2026-10-18) and their
                          // alignment is baked; "Model yaw" was also the second yaw lever next to
                          // "Saber yaw". "Model pose"/"Model scale"/"Saber grip" were removed
                          // earlier or replaced by the X/Y/Z position rows.
                          IdControllerDebug = 117,
                          IdSaberX = 123, IdSaberY = 124, IdSaberZ = 125,
                          IdSaberPitch = 128, IdSaberYaw = 129, IdSaberRoll = 130,
                          // Storage access has NO menu row since the scan runs on every
                          // start and "All files access" is asked at most once per START while
                          // it is missing (asked once per install; verifies the
                          // access by really reading Download). See SafSongFolder.
                          // IdSongFolder / IdRescanSongs are kept only for the (now
                          // unreachable) menu handlers.
                          IdSongFolder = 118, IdRescanSongs = 119,
                          // IdPerformance (121) retired: the live FPS/Hz row
                          // ("Performance") was removed from the menu.
                          IdNew = 250, IdDelete = 251, IdBack = 252,
                          IdSongBase = 300;

        /// <summary>Blue bar behind the active player (player management).</summary>
        private static readonly Color ActivePlayerBg = new Color(0.16f, 0.42f, 0.95f, 0.45f);

        // ======================================================================
        // Lifecycle
        // ======================================================================
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            _input = new ControllerInput();
            _audio = gameObject.AddComponent<AudioManager>();
        }

        private void Start()
        {
            SleepTemplateUi();

            _cam = Camera.main;
            if (_cam == null) _cam = FindAnyObjectByType<Camera>(FindObjectsInactive.Include);
            _cameraOffset = _cam != null ? _cam.transform.parent : null;
            // Resolve anchors robustly - regardless of which rig is active (XRI controller,
            // MRTK hand controller or the template's hands rig). Fallback reads
            // the pose directly from the physical controllers (XRNode).
            _leftAnchor = FindControllerAnchor("Left");
            _rightAnchor = FindControllerAnchor("Right");
            if (_leftAnchor == null) _leftAnchor = CreateXrPoseAnchor("SaberAnchorL", XRNode.LeftHand);
            if (_rightAnchor == null) _rightAnchor = CreateXrPoseAnchor("SaberAnchorR", XRNode.RightHand);

            _gameRoot = new GameObject("QuestSaberWorld").transform;
            // Set scaling base explicitly: the room root starts EVERY run at 1.0.
            // There is NO stored "last used" base factor - always first
            // load (EnsurePlayer -> ApplyRoomScale), then apply once absolutely.
            _gameRoot.localScale = Vector3.one;
            NotePool.SetRoot(_gameRoot);

            // Background environment (ArcViewer "Default"): only built on demand
            // (modes Default/Animated) - no cost is incurred in passthrough mode.
            var envGo = new GameObject("[Environment]");
            envGo.transform.SetParent(transform, false);
            _environment = envGo.AddComponent<Environment.EnvironmentDirector>();
            _environment.AttachToRoom(_gameRoot);

            HideControllerModels();
            StartCoroutine(HideControllerModelsRoutine());
            // Tracking precision: sample the controller/hand poses again before the
            // frame is rendered. Must run before the sabers are built so the very first frame
            // already uses the low-latency path.
            EnsureBeforeRenderPoseUpdates();

            BuildSabers();
            // Diagnostic: WHICH anchor actually drives the sabers (the rig nests
            // several candidates: "Left/Right Controller", "Left/Right Hand").
            Debug.Log($"[QuestSaber] Controller anchors: left={AnchorPath(_leftAnchor)}, " +
                      $"right={AnchorPath(_rightAnchor)}");
            StartCoroutine(AnchorDiagnosticsRoutine());   // Proves the anchor really tracks
            // Debug controllers (tracking visual check/saber calibration; menu row
            // "Controllers & sabers").
            ControllerDebugView.Attach(SaberSide.Left, _leftAnchor);
            ControllerDebugView.Attach(SaberSide.Right, _rightAnchor);
            BuildUi();

            LoadSave();
            EnsurePlayer();
            MigrateAudioOffset(); // migrate legacy offset (-85) once into the base calibration
            RefreshAudioOffset(); // apply saved audio offset to the music
            ApplyBackgroundSetting(); // apply saved background option (passthrough/default/animated)

            // Determine floor level at GAME START (not per song): set the tracking origin
            // "Floor" and repeat until the runtime confirms it.
            StartCoroutine(EnsureFloorOrigin(mustSucceed: true));

            // Make sure the app-own song folders exist (they double as the USB drop
            // folder) and remember the folder the player already picked with the
            // system folder chooser.
            EnsureSongFolders();
            // The song source is the shared Download folder. The game asks for
            // "All files access" so it can read/create folders and write the log there
            // directly; without it the permission-free media inbox still works. The game asks at
            // most once per START (not once per install) and verifies the grant with a real
            // read - see StorageAccessStartupRoutine.
            _songFolderUsable = SafSongFolder.HasAllFilesAccess && SafSongFolder.CanReadDownload;
            StartCoroutine(StorageAccessStartupRoutine());

            // Load songs. No storage permission is involved: QuestSaber only reads its own
            // folders (see GameConfig.SongsRootCandidates) plus the bundled demo song.
            BootSongs();
        }

        /// <summary>
        /// Loads the song list + the first song. Nothing is awaited (no permission request),
        /// so the first launch can never hang.
        /// </summary>
        private void BootSongs()
        {
            _songs = BeatmapParser.LoadSongList();
            if (_songs.Count == 0)
            {
                Debug.LogError("[QuestSaber] No songs found!");
                return;
            }
            RestoreDifficultyChoices();   // apply saved difficulty choice per song
            MigrateLegacyScores();        // migrate old highscores (without difficulty part)
            ShuffleSongsAtStart(); // randomize song order (otherwise always the same order)
            LoadSong(0);

            // Pick up songs that were downloaded into the picked folder (e.g. with the
            // Quest browser into Download) while the app was closed.
            RequestSongScan("boot");
        }

        /// <summary>
        /// Focus changes: losing focus during play is the system menu button or the
        /// headset being taken off - the run has to pause; gaining focus is the moment
        /// to look for songs that finished downloading while the app was not in front.
        /// </summary>
        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
            {
                OnBecameVisibleAgain();
                return;
            }

            PauseRunForSystemUi();
        }

        /// <summary>Same reaction as a focus loss (the device reports both).</summary>
        private void OnApplicationPause(bool isPaused)
        {
            if (isPaused) PauseRunForSystemUi();
        }

        /// <summary>
        /// The player left the app (system menu, headset off, another app). Without this
        /// the song would keep playing and the notes would keep running through while the
        /// player is not looking - destroying the combo and the score of the run.
        /// The normal menu pause path is used (<see cref="OpenMenu"/>), i.e. exactly what
        /// the menu button does, so resuming stays a single, familiar button press.
        /// Deliberately restricted to <see cref="GamePhase.Playing"/>: while the menu is
        /// open the game itself hands focus to Android dialogs (song folder picker, OS
        /// keyboard) and those must never be mistaken for a system menu.
        /// Pages the game opens while PLAYING are announced (<see cref="SafSongFolder"/>.
        /// ExpectSystemUi - the boot-time "All files access" page is exactly this case and
        /// used to pause the run behind the settings page, see the run of 2026-10-17).
        /// </summary>
        private void PauseRunForSystemUi()
        {
            if (!GameConfig.AutoPauseOnFocusLoss) return;
            if (Phase != GamePhase.Playing) return;
            if (SafSongFolder.SystemUiExpected)
            {
                Debug.Log("[QuestSaber] Focus lost while a system page of ours is in front " +
                          "(storage settings/picker): no auto-pause.");
                return;
            }

            try
            {
                Debug.Log("[QuestSaber] Focus lost during play (system menu / headset off): " +
                          "pausing the run and opening the menu.");
                OpenMenu(_menuSide != 0 ? _menuSide : -1);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Auto-pause on focus loss failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The Quest browser is a separate app, so a download finishes while QuestSaber is
        /// not in front. Coming back is therefore the natural moment to look for new songs
        /// in the picked folder (cheap: the actual work runs on a java worker thread and is
        /// throttled).
        /// </summary>
        private void OnBecameVisibleAgain()
        {
            // The app is in front again: whatever system page we opened is done with.
            SafSongFolder.ClearSystemUiExpectation();

            // Returning from the settings page (or the Quest browser):
            // if Download is now usable, point the log there and look for new songs.
            // The grant is required - the read probe alone passes without it.
            if (SafSongFolder.IsAndroid && SafSongFolder.HasAllFilesAccess &&
                SafSongFolder.CanReadDownload)
            {
                if (!_songFolderUsable || !DebugLogFile.PrimaryIsDownload)
                {
                    _songFolderUsable = true;
                    PrepareDownloadFolder();
                }
            }
            RequestSongScan("resume");
        }

        // ======================================================================
        // Song folders: app-own USB folder + player-picked folder (system chooser)
        // ======================================================================

        /// <summary>
        /// Creates the app-own song folders so they are visible in the file manager over
        /// USB, and drops a short how-to file into the plain "Songs" folder. Never throws.
        /// </summary>
        private void EnsureSongFolders()
        {
            TryCreateFolder(GameConfig.SongsRootPath);
            TryCreateFolder(GameConfig.ImportedSongsPath);

            // Permission-free inbox: it lives inside the app's own external media folder,
            // so a file manager (CX) or a USB cable can drop songs in WITHOUT any grant,
            // and the app can still read + unpack + delete them. Created (and hinted)
            // right away, because on some Horizon OS builds this is the ONLY path that
            // works - there is no system folder chooser to confirm.
            string inbox = SafSongFolder.EnsureInbox();
            if (!string.IsNullOrEmpty(inbox))
            {
                Debug.Log("[QuestSaber] Permission-free song inbox: " + inbox);
                TryWriteInboxHint(inbox);
            }
            else if (SafSongFolder.IsAndroid)
            {
                Debug.LogWarning("[QuestSaber] No permission-free song inbox on this device.");
            }

            try
            {
                string hint = System.IO.Path.Combine(
                    GameConfig.SongsRootPath, GameConfig.SongFolderHintFileName);
                if (!System.IO.File.Exists(hint))
                {
                    System.IO.File.WriteAllText(hint,
                        "QuestSaber song folder (no permission needed, fully offline).\r\n" +
                        "\r\n" +
                        "Put every song into its own sub-folder that contains info.dat (or\r\n" +
                        "info.json) plus the audio file and the difficulty files, for example:\r\n" +
                        "\r\n" +
                        "  Songs\\MySong\\info.dat\r\n" +
                        "  Songs\\MySong\\ExpertStandard.dat\r\n" +
                        "  Songs\\MySong\\song.egg\r\n" +
                        "\r\n" +
                        "This folder is read automatically at start.\r\n" +
                        "\r\n" +
                        "MORE COMFORTABLE, no PC needed: download a song in the Quest browser\r\n" +
                        "into the normal \"Download\" folder. At the FIRST start QuestSaber opens\r\n" +
                        "ONE system settings page and asks for \u201cAll files access\u201d - flip that\r\n" +
                        "switch once. From then on\r\n" +
                        "QuestSaber reads the Download folder directly, moves every new song\r\n" +
                        "into its own folder and even unpacks .zip files. It also creates\r\n" +
                        "\r\n" +
                        "  " + SafSongFolder.VisibleFolderName + "\\   (top level, next to Download)\r\n" +
                        "\r\n" +
                        "and MOVES every imported song in there, so you always find your songs\r\n" +
                        "in one clean folder and the Download folder stays tidy.\r\n" +
                        "\r\n" +
                        "IF YOU DO NOT WANT TO GRANT that access (some builds never show the\r\n" +
                        "settings page), use the always-works inbox instead - it needs no\r\n" +
                        "permission and no dialog. Put the .zip or the song folder into\r\n" +
                        "\r\n" +
                        "  Android\\media\\com.questsaber.questsaber\\Songs\\\r\n" +
                        "\r\n" +
                        "with a file manager (e.g. CX File Explorer) or over USB - it is imported\r\n" +
                        "and emptied automatically at every start.\r\n" +
                        "\r\n" +
                        "Debug log (crash/error report). It is written into Download as soon\r\n" +
                        "as Download is writable:\r\n" +
                        "  Download\\spiel_debug_log.txt\r\n" +
                        "While Download is not writable it lives in the app's own media folder\r\n" +
                        "  Android\\media\\com.questsaber.questsaber\\spiel_debug_log.txt\r\n" +
                        "(reachable with a file manager / over USB - unlike Android\\data).\r\n" +
                        "\r\n" +
                        "IMPORTANT: this 'Songs' folder (Android\\data\\...) is DELETED when the app\r\n" +
                        "is uninstalled. Downloaded songs that should survive an uninstall belong in\r\n" +
                        "  " + SafSongFolder.VisibleFolderName + "\\  (top level, next to Download)\r\n" +
                        "or in the media inbox above.\r\n");
                    Debug.Log("[QuestSaber] Wrote " + GameConfig.SongFolderHintFileName);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Could not write the song hint file: " + ex.Message);
            }
        }

        private static void TryCreateFolder(string path)
        {
            try
            {
                if (!System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.CreateDirectory(path);
                    Debug.Log("[QuestSaber] Created song folder: " + path);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Could not create '" + path + "': " + ex.Message);
            }
        }

        /// <summary>
        /// Drops a short how-to into the permission-free inbox, so the player sees what the
        /// folder is for when opening it with a file manager. Never throws.
        /// </summary>
        private static void TryWriteInboxHint(string inboxPath)
        {
            try
            {
                string hint = System.IO.Path.Combine(inboxPath, GameConfig.SongFolderHintFileName);
                if (System.IO.File.Exists(hint)) return;
                System.IO.File.WriteAllText(hint,
                    "QuestSaber song inbox - no permission, no dialog, fully offline.\r\n" +
                    "\r\n" +
                    "Put songs in HERE (a .zip from a song site, or an unpacked song folder\r\n" +
                    "that contains info.dat / info.json). QuestSaber imports everything at\r\n" +
                    "every start, unpacks .zip files and then empties this folder again.\r\n" +
                    "\r\n" +
                    "This folder is also writable over USB and with a file manager\r\n" +
                    "(e.g. CX File Explorer), because it is part of the app's own storage.\r\n" +
                    "You do NOT have to pick it anywhere - the game reads it on its own.\r\n" +
                    "\r\n" +
                    "  Example:  " + inboxPath + "\\MySong.zip\r\n" +
                    "         or " + inboxPath + "\\MySong\\info.dat\r\n");
                Debug.Log("[QuestSaber] Wrote " + GameConfig.SongFolderHintFileName + " into the inbox.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Could not write the inbox hint: " + ex.Message);
            }
        }

        /// <summary>
        /// Storage startup. "All files access" (MANAGE_EXTERNAL_STORAGE) is what lets the game
        /// work on the real shared Download folder: read the songs from there, keep them
        /// visible in the top-level <see cref="SafSongFolder.VisibleFolderName"/> folder
        /// (before that inside Download) and write
        /// spiel_debug_log.txt straight into Download. Android shows this as a SINGLE system
        /// settings page. Without it nothing breaks: the permission-free media inbox still
        /// imports songs.
        ///
        /// Decides on a REAL capability probe (create + delete a file in Download), not on
        /// the "All files access" flag: on Horizon OS the flag can read false while Download is
        /// perfectly writable (and the old listFiles() check could read true while nothing
        /// could be written).
        ///
        /// (as requested): NO in-game popup in front of the system
        /// page. The game starts and the settings page is opened right away. While the access
        /// is missing the page is offered again on EVERY start; as soon as it is granted it
        /// never appears again.
        /// </summary>
        private IEnumerator StorageAccessStartupRoutine()
        {
            if (!SafSongFolder.IsAndroid) yield break;

            // Let the XR session come up first: starting an activity in the very first
            // frames can be swallowed by the runtime.
            yield return new WaitForSecondsRealtime(4f);
            // Wait for the boot song (bounded: a failed audio load must not block this).
            float bootWait = 0f;
            while (Phase == GamePhase.Boot && bootWait < 15f)
            {
                bootWait += Time.unscaledDeltaTime;
                yield return null;
            }

            // What the device really allows (raw permission flag vs. real capability).
            DebugLogFile.Mark("storage diag: " + SafSongFolder.StorageStatus);

            bool readable = SafSongFolder.CanReadDownload;
            bool writable = SafSongFolder.CanWriteDownload;

            // ---- 2) Ask when the ALL-FILES ACCESS grant is MISSING. Force the
            //         system settings page on EVERY start while not granted.
            bool hasGrant = SafSongFolder.HasAllFilesAccess;
            if (hasGrant)
            {
                _songFolderUsable = readable;
                PrepareDownloadFolder();
                DebugLogFile.Mark("storage access: grant present (read=" + readable + " write=" +
                                  writable + ") songs=" + SafSongFolder.VisibleSongsPath);
                RequestSongScan("start", force: true);
                yield break;
            }

            string note = SafSongFolder.RequestAllFilesAccess();
            Debug.Log("[QuestSaber] Asking for \"All files access\": " +
                      (string.IsNullOrEmpty(note) ? "settings page opened" : note));
            DebugLogFile.Mark("storage access: asking (settings page) " +
                              (string.IsNullOrEmpty(note) ? "opened" : note) +
                              " [read=" + readable + " write=" + writable +
                              " flag=" + hasGrant + "]");
            LogPickerDiagnostics("storage access");   // resolved settings handler (SafHelper)

            if (string.IsNullOrEmpty(note))
            {
                // Wait for the REAL GRANT (the MANAGE_EXTERNAL_STORAGE flag), never
                // for the file probe. The probe passes WITHOUT the grant on this device
                // (Scoped Storage allows listing/creating while single file reads are
                // blocked) - waiting for it pulled the game back to the front right after the
                // settings page opened, so the player never saw a permission request at all.
                // Throttled: the check is JNI, checking every frame would waste frames.
                float waited = 0f;
                float nextCheck = 0.25f;
                bool grantedNow = false;
                while (waited < 120f)
                {
                    waited += Time.unscaledDeltaTime;
                    if (waited >= nextCheck)
                    {
                        nextCheck = waited + 0.25f;
                        if (SafSongFolder.HasAllFilesAccess)
                        {
                            grantedNow = true;
                            Debug.Log("[QuestSaber] All files access granted.");
                            DebugLogFile.Mark("storage access: granted (verified after " + waited.ToString("0") + "s)");
                            // give the player a moment on the page, then take the game back
                            yield return new WaitForSecondsRealtime(0.5f);
                            SafSongFolder.BringToFront();
                            break;
                        }
                    }
                    yield return null;
                }

                // Nobody granted within 120 s (player left the page open or cancelled):
                // take the game back to the front so it is not stuck behind the settings UI.
                // The page is offered again at the next start.
                if (!grantedNow)
                {
                    DebugLogFile.Mark("storage access: no grant after " + waited.ToString("0") +
                                      "s - returning to the game (asked again at next start)");
                    SafSongFolder.BringToFront();
                }

                hasGrant = grantedNow;
            }

            // ---- 3) Decide on the REAL capability (read for songs, write for log/import) ----
            readable = SafSongFolder.CanReadDownload;
            writable = SafSongFolder.CanWriteDownload;
            _songFolderUsable = readable;

            if (hasGrant)
            {
                if (readable)
                {
                    PrepareDownloadFolder();
                    DebugLogFile.Mark("storage access: grant present - readable (write=" + writable + ")");
                }
                else
                {
                    DebugLogFile.Mark("storage access: grant present but Download not readable");
                }
            }
            else
            {
                if (readable)
                {
                    DebugLogFile.Mark("storage access: no grant - readable? " + readable + " write=" + writable);
                }
                else
                {
                    DebugLogFile.Mark("storage access: no grant - not readable, using inbox " + SafSongFolder.InboxPath);
                }
            }
            RequestSongScan("start", force: true);
        }

        /// <summary>
        /// Creates the visible song folder <c>QuestSaberSongs</c> at the top level of the shared
        /// storage (before that it sat inside Download) so the player always finds the
        /// songs in one clean place - and, when the shared storage is really writable, moves the
        /// log into Download. Gated on the REAL capability (read for the folder, write for
        /// the log), never on the "All files access" flag, which can disagree with what is really
        /// possible. Never throws.
        /// </summary>
        private void PrepareDownloadFolder()
        {
            try
            {
                // Songs only need READ access - do not gate this on writing, otherwise a
                // readable-but-not-writable Download would hide every song.
                // And gate on the REAL grant - the read probe passes without it
                // (Scoped Storage), which made the game claim a folder it cannot read.
                if (!SafSongFolder.HasAllFilesAccess || !SafSongFolder.CanReadDownload) return;
                string visible = SafSongFolder.EnsureVisibleSongFolder();
                if (!string.IsNullOrEmpty(visible))
                {
                    // The folder itself moved OUT of Download to the storage root,
                    // because the Files app's aggregated "Downloads" view listed its files flat.
                    // The old ".nomedia" marker is not written any more (the player wants
                    // to see the songs in the media library as well), so nothing to report here.
                    Debug.Log("[QuestSaber] Visible song folder ready: " + visible +
                              " (topLevel, " + SafSongFolder.VisibleFolderNote() + ")");
                }
                else
                    Debug.LogWarning("[QuestSaber] Could not create the visible song folder (" +
                                     SafSongFolder.VisibleFolderName + " at the storage root).");
                // The log moves into Download only when Download is really WRITABLE;
                // otherwise it stays in the accessible app media folder.
                if (SafSongFolder.CanWriteDownload) DebugLogFile.SetPrimaryToDownload();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Download folder preparation failed: " + ex.Message);
            }
        }
        /// <summary>
        /// Asks for a song scan. Three sources are checked in order: the shared Download
        /// folder (all-files access), the permission-free media inbox and - for older
        /// installs - the previously picked SAF folder. Called at start, when the app comes
        /// back to the front (the Quest browser is a separate app, so downloads land while
        /// QuestSaber is in the background) and from the menu.
        /// Throttled, because on Quest the focus flips often (system menu).
        /// </summary>
        private void RequestSongScan(string reason, bool force = false)
        {
            if (_songImportBusy) return;
            // Never during boot: the boot sequence is busy building the song list itself.
            if (Phase == GamePhase.Boot) return;

            float now = Time.realtimeSinceStartup;
            if (!force && now - _lastSongScanAt < 20f) return;
            _lastSongScanAt = now;

            string treeUri = SafSongFolder.HasFolder ? SafSongFolder.TreeUri : "";
            StartCoroutine(ImportSongsRoutine(treeUri, reason));
        }

        /// <summary>
        /// Looks for new songs and moves them into the visible <c>QuestSaberSongs</c> folder
        /// (at the top level of the shared storage; before that inside Download) or,
        /// without "All files access", into the app-own imported folder - the copy
        /// itself runs on a java worker thread, so the VR session does not freeze - then
        /// re-reads the song list. Three sources, in this order:
        ///   1. the shared Download folder - the primary source; needs "All files
        ///      access" (offered at most once per start while missing) and is skipped without it,
        ///   2. the permission-free inbox (Android/media/&lt;pkg&gt;/Songs) - always, it needs
        ///      no permission and works even when the device grants nothing,
        ///   3. the legacy picked SAF folder - only when <paramref name="treeUri"/> is set.
        /// Song folders and .zip archives are both handled; only a .zip that really contains
        /// a song is touched, and the source is removed afterwards (= moved).
        /// </summary>
        private IEnumerator ImportSongsRoutine(string treeUri, string reason)
        {
            _songImportBusy = true;
            try
            {
                _songImportStatus = "checking...";
                bool hasFolder = !string.IsNullOrEmpty(treeUri);
                // The Download scan needs the REAL "All files access" grant, not the
                // file probe. On this device the probe passes WITHOUT the grant (Scoped Storage
                // lets listing succeed while reading existing files like info.dat is DENIED) -
                // trusting it produced "Access to the path ...info.dat is denied" and an empty
                // song list. Without the grant we use the permission-free inbox instead.
                bool hasDownload = SafSongFolder.HasAllFilesAccess && SafSongFolder.CanReadDownload;
                string inbox = SafSongFolder.EnsureInbox();
                string inboxText = string.IsNullOrEmpty(inbox) ? "-" : inbox;
                // Songs from Download are moved into the VISIBLE folder
                // (QuestSaberSongs at the storage root) and played from there;
                // without the grant the app-own SongsImported folder is the fallback
                // (media inbox path).
                string dest = hasDownload ? GameConfig.SongImportDestination
                                          : GameConfig.ImportedSongsPath;
                Debug.Log("[QuestSaber] Song scan (" + reason + "): download=" +
                          (hasDownload ? SafSongFolder.DownloadPath : "-") +
                          " dest=" + dest +
                          " inbox=" + inboxText +
                          (hasFolder ? " folder=" + treeUri : " folder=-"));
                DebugLogFile.Mark("song scan (" + reason + "): download=" +
                                  (hasDownload ? SafSongFolder.DownloadPath : "-") +
                                  " dest=" + dest +
                                  " inbox=" + inboxText +
                                  (hasFolder ? " folder=" + treeUri : " folder=-"));

                string result = "";
                bool anyImported = false;

                // 1) The shared Download folder (primary path).
                if (hasDownload)
                {
                    SafSongFolder.ImportFromDownloadAsync(dest);
                    yield return WaitForImportRoutine();
                    result = SafSongFolder.ImportResult;
                    Debug.Log("[QuestSaber] Download scan finished: " + result);
                    DebugLogFile.Mark("download scan finished: " +
                                      (_lastImportTimedOut ? "TIMEOUT " : "") + result);
                    if (!_lastImportTimedOut && result.Contains("\"ok\":true")) anyImported = true;
                }

                // 2) Permission-free inbox - always.
                SafSongFolder.ImportFromInboxAsync(GameConfig.ImportedSongsPath);
                yield return WaitForImportRoutine();
                string inboxResult = SafSongFolder.ImportResult;
                Debug.Log("[QuestSaber] Inbox scan finished: " + inboxResult);
                DebugLogFile.Mark("inbox scan finished: " +
                                  (_lastImportTimedOut ? "TIMEOUT " : "") + inboxResult);
                if (!_lastImportTimedOut && inboxResult.Contains("\"ok\":true")) anyImported = true;
                if (string.IsNullOrEmpty(result)) result = inboxResult;

                // 3) The legacy picked folder, when one is still granted.
                if (hasFolder)
                {
                    SafSongFolder.ImportAsync(GameConfig.ImportedSongsPath);
                    yield return WaitForImportRoutine();
                    result = SafSongFolder.ImportResult;
                    Debug.Log("[QuestSaber] Song scan finished: " + result);
                    DebugLogFile.Mark("song scan finished: " +
                                      (_lastImportTimedOut ? "TIMEOUT " : "") + result);
                    if (!_lastImportTimedOut && result.Contains("\"ok\":true")) anyImported = true;
                }

                if (result.Contains("\"write\":false"))
                    _songImportStatus = "read-only folder";
                else
                    _songImportStatus = anyImported ? "imported" : "nothing new";
                ReloadSongList();
            }
            // No `catch` here ON PURPOSE: a `yield return` is only allowed in a try block
            // without a catch clause (CS1626). An exception therefore reaches Unity, which
            // logs it into THIS log file as "EXCEPTION: ..." (DebugLogFile.OnLogMessage,
            // LogType.Exception) with its stack trace - while the finally below still
            // releases the lock and writes the result line in every case.
            finally
            {
                // ALWAYS release the lock and ALWAYS write the result line - a stuck
                // flag used to silence every later scan (the game then never saw new songs).
                if (_songImportStatus == "checking...")
                    _songImportStatus = "failed";   // threw before the status was set
                _songImportBusy = false;
                // No menu row for this any more, so the status goes into the debug log -
                // that is what makes "did the songs arrive?" answerable after a session.
                DebugLogFile.Mark("song import: " + _songImportStatus +
                                  " (" + _songs.Count + " songs)");
                // Not while the name input hides the menu (the import can finish mid-typing).
                if (_menuSide != 0 && !_awaitingName) BuildMenuContent();
            }
        }

        /// <summary>
        /// Waits for the running java import worker, but never forever: a broken or huge
        /// import must not block the game (the timeout is only a safety net). Sets
        /// <see cref="_lastImportTimedOut"/> so the caller can say so in the log instead of
        /// interpreting an empty result as "nothing new".
        /// </summary>
        private IEnumerator WaitForImportRoutine()
        {
            _lastImportTimedOut = false;
            float waited = 0f;
            while (SafSongFolder.ImportRunning && waited < 180f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (SafSongFolder.ImportRunning)
            {
                _lastImportTimedOut = true;
                Debug.LogWarning("[QuestSaber] Song import did not finish within 180 s - continuing.");
                DebugLogFile.Mark("song import: TIMEOUT after 180s (worker still running)");
            }
        }

        /// <summary>
        /// Writes the tail of the Java diagnostic log (`getFilesDir()/pick_log.txt`) into the
        /// normal debug log: which folder-chooser/legacy-picker decision was taken AND - since
        /// Which activity the "All files access" settings page resolved to
        /// (see SafHelper.startSettingsPage). That is what names the app that opens an
        /// unwanted extra "Download" window on some Horizon OS builds. Needs no storage
        /// permission. Never throws.
        /// </summary>
        private static void LogPickerDiagnostics(string when)
        {
            try
            {
                string log = SafSongFolder.PickLog();
                if (string.IsNullOrEmpty(log))
                {
                    Debug.Log("[QuestSaber] Folder picker log (" + when + "): empty");
                    DebugLogFile.Mark("picker log (" + when + "): empty");
                    return;
                }
                string[] lines = log.Replace("\r", "").Split('\n');
                int first = Mathf.Max(0, lines.Length - 12);
                Debug.Log("[QuestSaber] Folder picker log (" + when + "):");
                DebugLogFile.Mark("--- picker log (" + when + ") ---");
                for (int i = first; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    Debug.Log("[QuestSaber]   " + line);
                    DebugLogFile.Mark("  " + line);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Could not read the picker log: " + ex.Message);
            }
        }

        /// <summary>
        /// Re-reads all song folders (after an import) and keeps the current song if it is
        /// still there. A background scan never switches the loaded map: if the current song is
        /// gone, the loaded one is kept (see the note in the method) and only a completely empty
        /// selection is filled with the first song of the new list. The song list never drops to
        /// zero (the demo song keeps it alive).
        /// </summary>
        private void ReloadSongList()
        {
            string keepFolder = _current != null ? _current.FolderName : "";
            bool keepDemo = _current != null && _current.IsDemo;

            var list = BeatmapParser.LoadSongList();
            if (list.Count == 0) return;

            _songs = list;
            RestoreDifficultyChoices();

            int index = 0;
            bool same = false;
            if (!string.IsNullOrEmpty(keepFolder))
            {
                for (int i = 0; i < _songs.Count; i++)
                {
                    if (_songs[i].IsDemo == keepDemo && _songs[i].FolderName == keepFolder)
                    {
                        index = i;
                        same = true;
                        break;
                    }
                }
            }

            _songIndex = index;
            if (same)
            {
                // Same song is still there: just re-point and keep playing.
                _current = _songs[index];
                Debug.Log($"[QuestSaber] Song list reloaded: {_songs.Count} songs (current kept).");
            }
            else
            {
                // The current song is not in the new list any more. Do NOT load a different
                // song from a background scan: that is what replaced the running map without
                // the player picking anything (run of 2026-10-17: the boot-time scan grew the
                // list from 1 to 20 songs right after the storage grant and the game switched to
                // song 0 by itself - "falsche Map"). Only fill the gap when nothing is loaded.
                if (_current == null)
                {
                    LoadSong(index);
                    Debug.Log($"[QuestSaber] Song list reloaded: {_songs.Count} songs (first loaded).");
                }
                else
                {
                    Debug.Log($"[QuestSaber] Song list reloaded: {_songs.Count} songs " +
                              "(current song is gone from the list - keeping the loaded one).");
                }
            }
        }

        private void Update()
        {
            _input.Update();

            // Liveness marker for the on-device debug log (throttled to ~10 s).
            // Gives the last healthy state before a hard crash.
            DebugLogFile.Heartbeat("phase=" + Phase + " songs=" + _songs.Count);

            // Right stick left/right = rotate room; left stick up/down = floor offset.
            // Only outside the song list (there the stick pages through the songs).
            UpdateRoomAdjustment();
            // ---- Quit: the ONE way - hold Y or B >= 2 s (without confirmation).
            //      Exactly like the reference (MrSaber); no menu button, no Y+B combo. ----
            if (_input.LeftYHeldLong || _input.RightBHeldLong)
            {
                Quit();
                return;
            }

            // ---- Button A (right, short): move room under headset + rotate to view direction ----
            // Only X/Z and yaw; height/Y remains; no tracking/floor intervention.
            if (Phase != GamePhase.Menu && _input.RightAWasPressedThisFrame)
            {
                RecenterRoomOnHead();
            }

            // ---- Easter egg: grip double-click retracts/extends the respective lightsaber ----
            // Possible at any time (also in the menu/pause); while retracted, no note hits.
            if (_input.LeftGripDoubleClicked) ToggleSaberRetract(SaberSide.Left);
            if (_input.RightGripDoubleClicked) ToggleSaberRetract(SaberSide.Right);

            // ---- Next song: X (left) short - during play ----
            if (Phase == GamePhase.Playing && _input.LeftXWasPressedThisFrame)
            {
                LoadSong(_songIndex + 1);
            }

            // ---- Safety: without an open menu, nothing from the menu may remain.
            //      Above all the laser (LineRenderer lies outside the canvas) must then
            //      NOT stay - it only exists as long as the menu is open.
            if (_menuSide == 0 && _menu != null && _menu.Visible) _menu.Hide();

            // ---- Open/close menu ----
            if (_menuSide == 0)
            {
                if (_input.LeftYReleasedShort) OpenMenu(-1);
                else if (_input.RightBReleasedShort) OpenMenu(1);
            }
            else
            {
                // Delete confirmation: the menu button cancels ONLY the confirmation (back to
                // player management, delete discarded). A REPEATED press then closes
                // the menu via the normal logic below.
                if (_confirmingDelete || _confirmingSongDelete)
                {
                    if (_input.LeftYReleasedShort || _input.RightBReleasedShort)
                    {
                        if (_confirmingSongDelete) CancelSongDelete();
                        else CancelDeletePopup();
                        _audio.PlayUiSfx();
                    }
                }
                // Menu open: a short release of the same button closes,
                // the OTHER menu button jumps to the other hand's menu.
                else if (_menuSide == -1)
                {
                    if (_input.LeftYReleasedShort) CloseMenu();
                    else if (_input.RightBReleasedShort) SwitchMenuSide(1);
                }
                else
                {
                    if (_input.RightBReleasedShort) CloseMenu();
                    else if (_input.LeftYReleasedShort) SwitchMenuSide(-1);
                }
            }

            // ---- Interaction / play ----
            if (_menuSide != 0)
            {
                HandleMenuInput();
            }
            else if (Phase == GamePhase.Playing)
            {
                GameplayUpdate();
            }
        }

        private void LateUpdate()
        {
            // Apply "room follows player" (depth-follow) BEFORE the room is anchored.
            UpdateRoomFollow();
            // Anchor the play space - explicitly NOT the XR rig: the camera and
            // its parent transform belong to tracking and must not
            // be overwritten (see file header).
            ApplyRoomAnchor();

            // Lightshow clock + (only "animated" mode) ring/laser animation.
            if (_environment != null) _environment.Tick(ScoreTime, 0f);

            if (Phase == GamePhase.Playing && _menuSide == 0)
                GeometricHitUpdate();

            // Arc interaction (original `SliderInteractionManager`): the arc follows the
            // saber blade (haptics + particles + intensity). Independent of the hit test.
            if (Phase == GamePhase.Playing && _menuSide == 0 && _activeArcs.Count > 0)
                UpdateArcInteraction();

            try { UpdateLabels(); }
            catch (Exception ex) { Debug.LogWarning("[QuestSaber] Grip label update: " + ex.Message); }
        }

    }
}

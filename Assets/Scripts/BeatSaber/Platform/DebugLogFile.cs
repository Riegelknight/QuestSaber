// ==============================================================================
//  DebugLogFile - autonomous debug log for manual/no-ADB headset testing.
// ==============================================================================
//  Purpose: The game is installed by hand (APK via USB + CX File Explorer) and
//  there is NO live debugging (no ADB, no Unity debugger, no development mode).
//  This component therefore writes EVERYTHING diagnostic into a single text
//  file so it can be copied out of the headset by hand and pasted back for
//  analysis:
//
//      Primary <shared storage>/Download/spiel_debug_log.txt
//      - the ONE place the player has to look. The game writes it there directly as
//        soon as "All files access" is usable, which is verified by really writing
//        Download (SafSongFolder.CanWriteDownload); the settings page is opened at most
//        Once per START while that is missing. Download is the only place for
//        the log.
//      Fallback (only while that grant is missing): the app's media folder
//      (Android/media/<pkg>/), which needs NO permission and is reachable over USB /
//      with a file manager; Application.persistentDataPath is only the last resort.
//      It is mirrored into Download as soon as the grant arrives.
//      PC side: Tools/FetchLogFromQuest.ps1 pulls the Download copy (MTP, no ADB) into
//      <project>/Game-Logs/spiel_debug_log.txt - that file always holds the NEWEST
//      session and is replaced on every fetch. Game-Logs holds game logs only, build
//      logs belong to <project>/Logs.
//
//  Captured:
//    * all Unity log messages (Log / Warning / Error / Assert / Exception)
//      including the full stack trace for errors and exceptions,
//    * unhandled managed exceptions (AppDomain / unobserved tasks),
//    * app lifecycle events (quit, low memory, focus/pause),
//    * a periodic heartbeat (frame, fps, game phase) so the LAST healthy state
//      before a hard crash is known,
//    * a "previous session ended uncleanly" detection on the next start, which
//      is how a native crash (which managed code cannot catch) is discovered.
//
//  Threading: Application.logMessageReceivedThreaded, AppDomain and TaskScheduler
//  callbacks can fire on WORKER threads. Writing the file is guarded by a lock, so
//  it is safe to call from any thread.
//
//  This class must never throw: every I/O operation is guarded.
// ==============================================================================
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace QuestSaber
{
    public static class DebugLogFile
    {
        /// <summary>Log file name (Download, the app media folder, or persistentDataPath).</summary>
        public const string FileName = "spiel_debug_log.txt";

        /// <summary>Previous, over-size session is rotated here.</summary>
        public const string PreviousFileName = "spiel_debug_log_prev.txt";

        /// <summary>Marker written on a clean shutdown (used to detect crashes).</summary>
        const string CleanEndMarker = "=== SESSION ENDED CLEANLY ===";

        /// <summary>Rotate the log once it grows beyond this size (per session).</summary>
        const long RotateAboveBytes = 4L * 1024 * 1024; // 4 MB

        /// <summary>Heartbeat interval (seconds).</summary>
        const float HeartbeatInterval = 10f;

        /// <summary>
        /// How often the log is copied into the picked song folder (Download) so it can be
        /// grabbed with the file manager. Only runs while such a folder is granted.
        /// </summary>
        const float MirrorInterval = 12f;

        static readonly object Gate = new object();
        static string _path;
        static StreamWriter _writer;
        static bool _installed;
        static bool _primaryIsDownload;
        static float _lastHeartbeat = -999f;
        static int _lastHeartbeatFrame;
        static int _lastHitchCount;
        static float _lastMirror = -999f;
        static string _lastMirrorNote = "<none>";
        // Guards against two concurrent mirror/export runs (see MirrorToSongFolder).
        static bool _mirroring;

        /// <summary>
        /// Where the log is written: normally <c>Download/spiel_debug_log.txt</c>, before
        /// "All files access" was granted (see SetPrimaryToDownload) the app media folder
        /// (<c>Android/media/&lt;pkg&gt;/</c>). Never throws.
        /// </summary>
        public static bool PrimaryIsDownload { get { lock (Gate) { return _primaryIsDownload; } } }

        /// <summary>Full path of the primary log file (or a hint if not ready yet).</summary>
        public static string Path { get { lock (Gate) { return _path ?? "<not initialized>"; } } }

        // ------------------------------------------------------------------
        //  Installation
        // ------------------------------------------------------------------

        // Installed as early as possible so even boot errors are captured.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void AutoInstall()
        {
            Install();
        }

        /// <summary>Idempotent. Safe to call from anywhere.</summary>
        public static void Install()
        {
            lock (Gate)
            {
                if (_installed) return;
                _installed = true;

                try
                {
                    // Download is THE place for the log only while it is
                    // really writable AND "All files access" is granted. Otherwise the log
                    // goes into the app's own media folder (Android/media/<pkg>/), which needs
                    // NO permission and - unlike Android/data - IS reachable with a file
                    // manager / over USB, so the log can ALWAYS be fetched even when no grant
                    // was given, and Download stays untouched.
                    string downloadLog = ProbeDownloadLogPath();
                    string logPath = downloadLog;
                    if (logPath == null)
                    {
                        string media = SafSongFolder.MediaDir;
                        if (!string.IsNullOrEmpty(media))
                            logPath = System.IO.Path.Combine(media, FileName);
                    }
                    if (logPath == null)
                        logPath = System.IO.Path.Combine(Application.persistentDataPath, FileName);

                    _path = logPath;
                    _primaryIsDownload = downloadLog != null;

                    bool previousDirty = RotateAndInspect(_path);

                    var stream = new FileStream(_path, FileMode.Append, FileAccess.Write,
                                                FileShare.ReadWrite);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                    WriteHeader(previousDirty);

                    // The heartbeat measures the frame rate as an AVERAGE over the interval
                    // (a single frame delta is far too noisy to judge 120 fps by).
                    _lastHeartbeat = Time.realtimeSinceStartup;
                    _lastHeartbeatFrame = Time.frameCount;
                }
                catch (Exception ex)
                {
                    _writer = null;
                    _path = null;
                    Debug.LogError("[QuestSaber] DebugLogFile could not be created: " + ex);
                }

                try
                {
                    Application.logMessageReceivedThreaded += OnLogMessage;
                    Application.quitting += OnQuitting;
                    Application.lowMemory += OnLowMemory;
                    Application.focusChanged += OnFocusChanged;
                    AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                    TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
                }
                catch { /* hooks are best-effort */ }
            }
        }

        /// <summary>
        /// Returns <c>&lt;Download&gt;/spiel_debug_log.txt</c> when the log is allowed to live
        /// there, otherwise null (the log then starts in the app-own media folder and
        /// moves to Download as soon as the grant arrives - see SetPrimaryToDownload).
        /// Decides on the REAL "All files access" grant only. The write probe alone
        /// (CanWriteDownload) lies on Horizon OS: scoped storage lets the app CREATE a file in
        /// Download without the grant, so the probe passed while permission was denied - and a
        /// QuestSaber file showed up in Download even though the user had refused the access
        /// ("scenario A"). Without the grant NOTHING of ours may appear in Download.
        /// Never throws.
        /// </summary>
        private static string ProbeDownloadLogPath()
        {
            try
            {
                if (!SafSongFolder.IsAndroid) return null;
                // Grant first (authoritative), write probe second (defensive): the log lives in
                // Download only while "All files access" is really granted AND writing works.
                if (!SafSongFolder.HasAllFilesAccess) return null;
                if (!SafSongFolder.CanWriteDownload) return null;
                string dl = SafSongFolder.DownloadPath;
                if (string.IsNullOrEmpty(dl)) return null;

                string path = System.IO.Path.Combine(dl, FileName);
                // Touch it: only trust the path when a write really works.
                using (var probe = new FileStream(path, FileMode.Append, FileAccess.Write,
                                                  FileShare.ReadWrite)) { probe.Flush(); }
                return path;
            }
            catch { return null; }
        }

        /// <summary>
        /// Switches the primary log file to <c>Download/spiel_debug_log.txt</c> once the
        /// shared Download folder is really usable (verified by a real write probe).
        /// Everything written so far is copied over, then logging continues in Download.
        /// A no-op when Download is already the primary location or the access is still
        /// missing. Never throws.
        /// </summary>
        public static void SetPrimaryToDownload()
        {
            lock (Gate)
            {
                try
                {
                    if (_primaryIsDownload || _writer == null) return;
                    // Only on the REAL grant (see ProbeDownloadLogPath) - the write
                    // probe alone must never move our files into Download.
                    if (!SafSongFolder.HasAllFilesAccess) return;
                    if (!SafSongFolder.CanWriteDownload) return;
                    string dl = SafSongFolder.DownloadPath;
                    if (string.IsNullOrEmpty(dl)) return;

                    string target = System.IO.Path.Combine(dl, FileName);

                    // Bring the older private content along, then continue in Download.
                    _writer.Flush();
                    string old = _path;

                    using (var src = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var dst = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        src.CopyTo(dst);

                    _writer.Dispose();
                    var stream = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    _path = target;
                    _primaryIsDownload = true;

                    // Nothing may stay in the app-private folder that looks like a second
                    // (stale) log: the content was copied above, so the private file can go.
                    try
                    {
                        if (!string.Equals(old, target, StringComparison.Ordinal) && File.Exists(old))
                            File.Delete(old);
                    }
                    catch { /* best effort */ }

                    WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") +
                              "] LOG: Download is writable - the log now lives in " +
                              "Download/" + FileName + " only.");
                }
                catch (Exception ex)
                {
                    try { WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") +
                                    "] LOG: could not switch to Download: " + ex.Message); } catch { }
                }
            }
        }

        /// <summary>Rotate the file if it is too big; return true if the previous session
        /// ended WITHOUT the clean marker (= likely a crash).</summary>
        private static bool RotateAndInspect(string path)
        {
            bool previousDirty = false;
            try
            {
                if (File.Exists(path))
                {
                    previousDirty = !TailContainsCleanMarker(path, 4096);

                    var fi = new FileInfo(path);
                    if (fi.Length > RotateAboveBytes)
                    {
                        string prev = System.IO.Path.Combine(
                            System.IO.Path.GetDirectoryName(path) ?? "", PreviousFileName);
                        try { if (File.Exists(prev)) File.Delete(prev); } catch { }
                        try { File.Move(path, prev); } catch { }
                    }
                }
            }
            catch { }
            return previousDirty;
        }

        private static bool TailContainsCleanMarker(string path, int tailBytes)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, fs.Length - tailBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[fs.Length - start];
                    int read = fs.Read(buf, 0, buf.Length);
                    string tail = Encoding.UTF8.GetString(buf, 0, read);
                    return tail.Contains(CleanEndMarker);
                }
            }
            catch { return true; } // cannot tell -> do not report a crash
        }

        /// <summary>Name of the active quality level ("?" when unavailable). Never throws.</summary>
        private static string QualityLevelName()
        {
            try
            {
                int level = QualitySettings.GetQualityLevel();
                string[] names = QualitySettings.names;
                return names != null && level >= 0 && level < names.Length ? names[level] : "?";
            }
            catch { return "?"; }
        }

        /// <summary>
        /// One-line snapshot of the settings that decide HOW THE GAME LOOKS and how
        /// expensive a frame is (HDR, MSAA, shadows, glow). Written once per session so a
        /// device log PROVES what the installed APK really used: the environment once
        /// looked washed out because the render pipeline asset had been reset to Unity
        /// template defaults (HDR off, MSAA 4, shadows on) instead of the ArcViewer values.
        /// Never throws.
        /// </summary>
        private static string RenderSummary()
        {
            var sb = new StringBuilder();
            try
            {
                sb.Append("colorSpace=").Append(QualitySettings.activeColorSpace)
                  .Append(" lightsLinear=").Append(GraphicsSettings.lightsUseLinearIntensity ? 1 : 0)
                  .Append(" lightsKelvin=").Append(GraphicsSettings.lightsUseColorTemperature ? 1 : 0)
                  .Append(" | quality=").Append(QualityLevelName())
                  .Append(" pixelLights=").Append(QualitySettings.pixelLightCount)
                  .Append(" realtimeReflProbes=").Append(QualitySettings.realtimeReflectionProbes ? 1 : 0)
                  .Append(" antiAliasing=").Append(QualitySettings.antiAliasing);
            }
            catch { sb.Append("quality=<n/a>"); }

            try
            {
                var urp = UniversalRenderPipeline.asset;
                if (urp == null)
                {
                    sb.Append(" | URP=<missing>");
                }
                else
                {
                    sb.Append(" | URP hdr=").Append(urp.supportsHDR ? 1 : 0)
                      .Append(" msaa=").Append(urp.msaaSampleCount)
                      .Append(" renderScale=").Append(urp.renderScale.ToString("0.00"))
                      .Append(" mainShadows=").Append(urp.supportsMainLightShadows ? 1 : 0)
                      .Append(" addShadows=").Append(urp.supportsAdditionalLightShadows ? 1 : 0)
                      .Append(" shadowDist=").Append(urp.shadowDistance.ToString("0.0"))
                      .Append(" colorGrading=").Append(urp.colorGradingMode)
                      .Append("/").Append(urp.colorGradingLutSize);
                }
            }
            catch { sb.Append(" | URP=<n/a>"); }

            try
            {
                // The ArcViewer look depends on the custom Bloomfog renderer feature
                // (neon glow / fog). Log whether the renderer this build shipped with
                // actually contains that feature and whether it is switched on.
                int found = 0, active = 0;
                foreach (var feature in Resources.FindObjectsOfTypeAll<ScriptableRendererFeature>())
                {
                    if (feature == null || feature.GetType().Name != "Bloomfog") continue;
                    found++;
                    if (feature.isActive) active++;
                }
                sb.Append(" | Bloomfog feature=").Append(found).Append(" active=").Append(active);
            }
            catch { /* diagnostics must never be the reason a session fails */ }

            return sb.ToString();
        }

        private static void WriteHeader(bool previousDirty)
        {
            var sb = new StringBuilder();
            sb.Append('\n');
            sb.Append("================================================================\n");
            sb.Append(" QuestSaber debug log - session start ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
            sb.Append("================================================================\n");
            sb.Append(" Log file    : ").Append(_path).Append('\n');
            sb.Append(" App version : ").Append(Application.version)
              .Append("  (build ").Append(Application.buildGUID).Append(')').Append('\n');
            // The build stamp is the FIRST thing a human sees - it tells which source
            // tree this APK came from (the missing stamp once made a log/diagnosis worthless).
            sb.Append(" Build       : ").Append(ProjectInfo.BuildStamp).Append('\n');
            sb.Append(" Unity       : ").Append(Application.unityVersion).Append('\n');
            sb.Append(" Platform    : ").Append(Application.platform)
              .Append("  device=").Append(SystemInfo.deviceModel)
              .Append("  os=").Append(SystemInfo.operatingSystem).Append('\n');
            sb.Append(" CPU/GPU     : ").Append(SystemInfo.processorType)
              .Append(" | ").Append(SystemInfo.graphicsDeviceName)
              .Append(" (").Append(SystemInfo.graphicsDeviceVersion).Append(')').Append('\n');
            sb.Append(" Memory      : ").Append(SystemInfo.systemMemorySize).Append(" MB\n");
            sb.Append(" Quality     : ").Append(QualityLevelName())
              .Append(" (level ").Append(QualitySettings.GetQualityLevel())
              .Append(", vSyncCount=").Append(QualitySettings.vSyncCount)
              .Append(")\n");
            sb.Append(" Render      : ").Append(RenderSummary()).Append('\n');
            sb.Append(" Persistent  : ").Append(Application.persistentDataPath).Append('\n');
            sb.Append(" Log place   : ")
              .Append(_primaryIsDownload
                  ? "shared Download folder (Download/" + FileName + ")"
                  : "app media folder (reachable over USB / with a file manager) - it moves to " +
                    "Download/" + FileName + " as soon as Download is really writable")
              .Append('\n');
            // Informational only: the game itself never uses the network. In flight
            // mode this reads "NoNetwork" and proves the app still starts and plays.
            sb.Append(" Network     : ").Append(Application.internetReachability)
              .Append(" (the game performs no internet requests)").Append('\n');

            if (previousDirty)
            {
                sb.Append(" !!! PREVIOUS SESSION DID NOT END CLEANLY (crash / force-kill) !!!\n");
                sb.Append("     -> the last heartbeat lines of the previous run show where it stopped.\n");
            }
            else
            {
                sb.Append(" Previous session ended cleanly.\n");
            }
            sb.Append("----------------------------------------------------------------\n");
            WriteLine(sb.ToString());
        }

        // ------------------------------------------------------------------
        //  Log capture
        // ------------------------------------------------------------------

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            string level;
            bool isProblem;
            switch (type)
            {
                case LogType.Error:     level = "ERROR";     isProblem = true;  break;
                case LogType.Exception: level = "EXCEPTION"; isProblem = true;  break;
                case LogType.Assert:    level = "ASSERT";    isProblem = true;  break;
                case LogType.Warning:   level = "WARN";      isProblem = false; break;
                default:                level = "LOG";       isProblem = false; break;
            }

            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] ")
              .Append(level).Append(": ").Append(condition);

            if (isProblem && !string.IsNullOrEmpty(stackTrace))
                sb.Append('\n').Append(stackTrace);

            WriteLine(sb.ToString());
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") +
                      "] FATAL: unhandled exception: " + e.ExceptionObject);
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") +
                      "] TASK: unobserved exception: " + e.Exception);
        }

        private static void OnLowMemory()
        {
            WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] SYSTEM: low memory warning");
        }

        private static void OnFocusChanged(bool hasFocus)
        {
            WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] APP: focus = " + hasFocus);
        }

        private static void OnQuitting()
        {
            WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] APP: quitting");
            WriteLine(CleanEndMarker);
            try { lock (Gate) { _writer?.Flush(); } } catch { }
            // Last chance to hand the log to the player (the app may be force-killed next).
            MirrorToSongFolder(true);
        }

        // ------------------------------------------------------------------
        //  Heartbeat / manual markers
        // ------------------------------------------------------------------

        /// <summary>Cheap, throttled liveness marker. Call every frame (e.g. from GameManager.Update).</summary>
        public static void Heartbeat(string state)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastHeartbeat < HeartbeatInterval) return;

            float elapsed = now - _lastHeartbeat;
            int frames = Time.frameCount - _lastHeartbeatFrame;
            _lastHeartbeat = now;
            _lastHeartbeatFrame = Time.frameCount;

            float fps = elapsed > 0.001f
                ? frames / elapsed
                : (Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f);

            // Frames that missed the budget since the last heartbeat: the honest smoothness
            // signal, because a good average fps can still hide periodic spikes.
            int hitches = MetaPerformance.TotalHitches - _lastHitchCount;
            _lastHitchCount = MetaPerformance.TotalHitches;

            WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] HB: frame=" + Time.frameCount +
                      " t=" + now.ToString("0.0") + "s fps=" + fps.ToString("0") +
                      " target=" + MetaPerformance.UnityTargetFrameRate + "Hz" +
                      " real=" + Application.targetFrameRate + " vSync=" + QualitySettings.vSyncCount +
                      (MetaPerformance.FrameRateRepairs > 0
                          ? " repairs=" + MetaPerformance.FrameRateRepairs : string.Empty) +
                      " display=" + MetaPerformance.SafeDisplayRefreshHz().ToString("0") + "Hz" +
                      " hitches=+" + hitches +
                      " worst=" + MetaPerformance.PeakFrameMs.ToString("0.0") + "ms" +
                      " gc=" + (GC.GetTotalMemory(false) / (1024 * 1024)) + "MB " +
                      state);
            MirrorToSongFolder(false);
        }

        /// <summary>
        /// Writes the log into the shared Download folder (best effort, throttled). Only
        /// needed while the primary location is still the private app folder - e.g. in the
        /// very first session before "All files access" was granted. Never throws.
        /// </summary>
        public static void MirrorToSongFolder(bool force)
        {
            // Only ONE mirror/export may run at a time. Two concurrent exports (one from
            // the heartbeat on the main thread, one from a log callback on a Unity worker
            // thread) both wrote Download\spiel_debug_log.txt with independent file positions -
            // that is exactly what produced the interleaved garbage lines in the on-device log
            // ("Songs.ingImported", "scan begin (threads:").
            lock (Gate)
            {
                if (_mirroring) return;
                if (!SafSongFolder.IsAndroid) return;
                if (_primaryIsDownload) return;          // already the only place
                // Touching Download at all requires the "All files access" grant.
                // The write probe alone passes without it (scoped storage), which would drop
                // a QuestSaber file into Download although the user refused the permission.
                // The fallback log already lives in the accessible media folder, so nothing
                // has to be mirrored just to be reachable.
                if (!SafSongFolder.HasAllFilesAccess) return;
                if (!SafSongFolder.CanWriteDownload) return;

                float now = Time.realtimeSinceStartup;
                if (!force && now - _lastMirror < MirrorInterval) return;
                _lastMirror = now;
                _mirroring = true;
            }

            try
            {
                string path;
                lock (Gate) { path = _path; }
                if (string.IsNullOrEmpty(path)) return;

                // Once Download is writable this moves the whole log over and ends the
                // private copy for good - Download stays the only place for the log.
                SetPrimaryToDownload();
                if (_primaryIsDownload) return;

                string note = SafSongFolder.ExportFileToDownload(path, FileName);
                if (string.IsNullOrEmpty(note)) note = "ok";
                if (note != _lastMirrorNote)
                {
                    _lastMirrorNote = note;
                    if (note == "ok")
                        Debug.Log("[QuestSaber] Debug log written into Download/" + FileName + ".");
                    else
                        Debug.LogWarning("[QuestSaber] Could not write Download/" + FileName + ": " + note);
                }
            }
            catch (Exception ex)
            {
                if (_lastMirrorNote != "exception")
                {
                    _lastMirrorNote = "exception";
                    Debug.LogWarning("[QuestSaber] Debug log copy failed: " + ex.Message);
                }
            }
            finally
            {
                lock (Gate) { _mirroring = false; }
            }
        }

        /// <summary>Writes a one-off marker line (always).</summary>
        public static void Mark(string message)
        {
            WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] MARK: " + message);
        }

        // ------------------------------------------------------------------
        //  Low-level write
        // ------------------------------------------------------------------

        private static void WriteLine(string line)
        {
            lock (Gate)
            {
                if (_writer == null) return;
                try { _writer.WriteLine(line); }
                catch { }
            }
        }
    }
}

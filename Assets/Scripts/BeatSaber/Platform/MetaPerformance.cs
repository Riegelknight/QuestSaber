// ==============================================================================
//  MetaPerformance - Quest 3 performance layer.
// ==============================================================================
//  Bundles the Meta-specific levers that the Mixed Reality template does NOT
//  set by itself:
//
//   1. FRAME RATE (target 120 Hz = Quest 3 maximum):
//      TWO levers are needed, and forgetting the first one is a classic Quest trap:
//        a) Application.targetFrameRate (+ QualitySettings.vSyncCount = 0):
//           on Android Unity renders at a FIXED 30 fps while vSyncCount == 0 (the
//           Android quality level used here) and targetFrameRate is left at -1.
//           Requesting a display frequency alone does NOT lift that cap.
//        b) OVRPlugin.systemDisplayFrequency (OpenXR extension
//           XR_FB_display_refresh_rate; active via the "Meta XR Feature"/Android).
//      The render rate is always DERIVED FROM the rate the panel really runs at
//      (the request may be refused - e.g. 120 asked, panel stays at 90). Rendering
//      120 fps into a 90 Hz panel is the classic Quest stutter, so the two are kept
//      in sync instead of chasing the target number.
//      The panel rate is written ONLY when it really differs from the request, only
//      while the app has focus and at most every few seconds: every write makes the
//      compositor reconfigure its output (visible as a short stutter).
//      A thermal watchdog switches ONLY DOWNWARD to the
//      fallback rate (GameConfig.FallbackDisplayRefreshRate) when the measured
//      frame rate falls below the rate-relative limit (MetaPerformance.
//      MinAcceptableFps(), 85 % of the render rate) over a whole window.
//      No automatic switching up -> no oscillation (important for a
//      rhythm game, where steady frames matter more than peak "FPS").
//
//   2. GUARDIAN / BOUNDARY:
//      Suppresses the VISIBILITY of the safety boundary via
//      OVRPlugin.RequestBoundaryVisibility(Suppressed). This means the
//      Quest no longer draws a guardian grid in passthrough. Prerequisites (both
//      met): `boundaryVisibilitySupport` in OculusProjectConfig (sets the
//      manifest permission com.oculus.permission.BOUNDARY_VISIBILITY) and
//      passthrough enabled. NOTE: The OS internally keeps enforcing the secured
//      room-scale frame (safety feature, cannot be
//      disabled by the app) - but for the game it is neither visible nor relevant.
//      Floor tracking (tracking origin = floor) remains untouched.
//      IMPORTANT: while passthrough is OFF the OS REFUSES to hide the boundary on
//      purpose (a completely virtual scene must still show the grid), so the request
//      is only made while the passthrough layer really exists
//      (MetaSeeThrough.LayerActive) - otherwise every watchdog tick logged a warning.
//      Requests are skipped while the app has no focus and, after a refusal, repeated
//      only after a short backoff (the OS refuses every request in the system menu).
//
//   3. FOVEATION:
//      Sets the fixed foveated rendering level (GameConfig.FoveationLevel) and
//      restores it after focus/resume changes.
//
//  Everything is self-initializing ([RuntimeInitializeOnLoadMethod], its own
//  DontDestroyOnLoad GameObject). In the editor (no OVRPlugin) all calls are
//  no-ops - the editor test of the demo song stays unchanged.
//
//  Context: architecture/invariants see PROJECT_NOTES.md (project root).
// ==============================================================================
using System.Collections;
using UnityEngine;

namespace QuestSaber
{
    /// <summary>
    /// Meta/Quest performance layer: frame rate (120 Hz + downward watchdog),
    /// guardian suppression and foveation. Purely static; the associated
    /// <see cref="MetaPerformanceBehaviour"/> initializes itself.
    /// </summary>
    public static class MetaPerformance
    {
        /// <summary>Maximum wait time until OVRPlugin is initialized.</summary>
        internal const float InitTimeoutSeconds = 10f;
        internal const float InitRetrySeconds = 0.5f;
        /// <summary>Interval of the FIRST retries of the panel rate. The device brings
        /// the panel up at 72/90 Hz and only accepts the requested 120 Hz a moment later;
        /// without this the first seconds of a session ran in slow motion (the old code
        /// waited a full watchdog interval before the first retry).</summary>
        internal const float EagerRetrySeconds = 1f;
        /// <summary>Upper bound for display-rate requests (keeps the log short).</summary>
        internal const int MaxRefreshRetries = 15;
        /// <summary>Minimum distance between two writes of the panel rate. Every write makes
        /// the compositor reconfigure its output (a visible short stutter), so the value is
        /// only written when the panel really runs at another rate.</summary>
        internal const float RefreshWriteCooldownSeconds = 3f;
        /// <summary>After a refused boundary request: wait this long before asking again.
        /// Re-asking every watchdog tick produced 12 warnings + 15 Failure_OperationFailed
        /// in one log; while the request is refused the grid is visible anyway, so the
        /// backoff costs nothing.</summary>
        internal const float GuardianBackoffSeconds = 10f;
        /// <summary>Interval at which boundary/foveation are enforced again.</summary>
        internal const float WatchdogIntervalSeconds = 5f;
        /// <summary>Watchdog warm-up time (wait out shader compilation/first launch).</summary>
        internal const float GovernorWarmupSeconds = 20f;
        /// <summary>Measurement window of the frame rate before the watchdog decides.</summary>
        internal const float GovernorWindowSeconds = 3f;
        internal const float GovernorSampleSeconds = 0.1f;
        /// <summary>How many BAD windows in a row are needed before switching down. One slow
        /// window (loading, shader compilation, a hiccup) must not drop the session.</summary>
        internal const int GovernorBadWindowsBeforeDowngrade = 3;
        /// <summary>
        /// How often <see cref="EnsureUnityFrameRate"/> verifies the target frame rate.
        /// Other components (XR/AR packages, the Meta runtime) may write
        /// <c>Application.targetFrameRate</c>; a forgotten write means back to Unity's
        /// Android default of 30 fps, so the value is treated as "keep it, don't set it once".
        /// </summary>
        internal const float FrameRateCheckSeconds = 0.25f;

        /// <summary>Last requested (desired) frame rate in Hz.</summary>
        public static float DesiredRefreshHz { get; private set; }

        /// <summary>Actual frame rate in Hz last reported by the system (0 = unknown).</summary>
        public static float AppliedRefreshHz { get; private set; }

        /// <summary>
        /// Applies all performance levers: target frame rate, boundary suppression and
        /// foveation. Called by the init run as soon as OVRPlugin is ready.
        /// </summary>
        public static void ApplyAll()
        {
            ApplyRefreshRate(GameConfig.TargetDisplayRefreshRate);
            RequestGuardianSuppression();
            ApplyFoveation();
        }

        /// <summary>
        /// Requests the desired display frame rate. The setter of
        /// <c>OVRPlugin.systemDisplayFrequency</c> is void - the actually effective value is
        /// read back and the RENDER rate is then derived from it.
        /// </summary>
        public static void ApplyRefreshRate(float hz)
        {
            DesiredRefreshHz = hz;
            if (Application.isEditor || !OVRPlugin.initialized) return;

            try
            {
                float wanted = NearestSupportedRate(hz);
                float current = SafeSystemDisplayFrequency();
                bool differs = current <= 1f ||
                               Mathf.Abs(NearestSupportedRate(current) - wanted) > 1f;

                // Write ONLY when the panel really runs at another rate, only while the app
                // has focus (the runtime answers with Failure_OperationFailed while the
                // system menu is open) and at most every few seconds. The previous version
                // wrote on every focus/resume/watchdog tick (94 writes in one log) - and
                // every write reconfigures the compositor output.
                bool wrote = false;
                if (differs && Application.isFocused &&
                    Time.realtimeSinceStartup >= _nextRefreshWriteTime)
                {
                    OVRPlugin.systemDisplayFrequency = hz;
                    _nextRefreshWriteTime = Time.realtimeSinceStartup + RefreshWriteCooldownSeconds;
                    _refreshWrites++;
                    wrote = true;
                }

                float panel = SafeSystemDisplayFrequency();   // the runtime's answer

                // Render EXACTLY as fast as the panel really runs. Asking for 120 while
                // the panel stays at 90 (system refusal, thermal, battery) is the classic Quest
                // stutter: 120 rendered frames do not fit a 90 Hz panel, so the compositor has
                // to drop/duplicate frames at an uneven rhythm. Deriving the render rate from
                // the panel rate makes the two match whatever the system decided.
                float effective = panel > 1f ? NearestSupportedRate(panel)
                                             : wanted;
                AppliedRefreshHz = effective;

                ApplyUnityFrameRate(effective);

                if (wrote || !_rateLogged || Mathf.Abs(effective - _lastLoggedRate) > 1f)
                {
                    _rateLogged = true;
                    _lastLoggedRate = effective;
                    Debug.Log($"[Meta] Display rate: requested {hz:0} Hz, panel {panel:0} Hz, " +
                              $"rendering {effective:0} fps (Unity Screen reports " +
                              SafeDisplayRefreshHz().ToString("0") + " Hz, writes=" +
                              _refreshWrites + ").");
                    DebugLogFile.Mark("display rate: requested=" + hz.ToString("0") +
                                      " panel=" + panel.ToString("0") +
                                      " render=" + effective.ToString("0") +
                                      " screen=" + SafeDisplayRefreshHz().ToString("0") +
                                      " writes=" + _refreshWrites);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[Meta] Frame rate could not be set: " + ex.Message);
            }
        }

        private static float _nextRefreshWriteTime;
        private static int _refreshWrites;
        private static bool _rateLogged;
        private static float _lastLoggedRate;

        /// <summary>Panel rate as the runtime reports it (0 when unreadable).</summary>
        private static float SafeSystemDisplayFrequency()
        {
            try { return OVRPlugin.initialized ? OVRPlugin.systemDisplayFrequency : 0f; }
            catch { return 0f; }
        }

        /// <summary>Quest 3 supports exactly these panel rates (144 Hz does not exist).</summary>
        private static readonly float[] SupportedRates = { 72f, 80f, 90f, 120f };

        /// <summary>Snaps a measured/requested rate to the nearest rate the panel really has.</summary>
        public static float NearestSupportedRate(float hz)
        {
            float best = SupportedRates[0];
            float bestDist = Mathf.Abs(hz - best);
            for (int i = 1; i < SupportedRates.Length; i++)
            {
                float d = Mathf.Abs(hz - SupportedRates[i]);
                if (d < bestDist) { bestDist = d; best = SupportedRates[i]; }
            }
            return best;
        }

        /// <summary>
        /// The rate the panel REALLY runs at (0 when unknown). Uses the runtime's own value
        /// first and Unity's screen resolution as a fallback.
        /// </summary>
        public static float EffectiveDisplayHz()
        {
            try
            {
                if (OVRPlugin.initialized)
                {
                    float ovr = OVRPlugin.systemDisplayFrequency;
                    if (ovr > 1f) return NearestSupportedRate(ovr);
                }
            }
            catch { /* fall through to the Unity value */ }
            return SafeDisplayRefreshHz();
        }

        private static int _refreshRetries;

        /// <summary>
        /// Re-requests the desired display rate while the panel still runs below it. The
        /// display-frequency request can be missed when it is made too early in the session
        /// (or refused for a moment), so a BOUNDED number of retries is made; afterwards the
        /// panel rate is taken as final and the render rate stays in sync with it. No
        /// oscillation, and the "many fps" goal is still pursued.
        /// </summary>
        public static void RetryDesiredRefreshRate()
        {
            if (Application.isEditor || !OVRPlugin.initialized) return;
            if (DesiredRefreshHz <= 1f) return;
            if (AppliedRefreshHz >= DesiredRefreshHz - 1f) return;
            if (_refreshRetries >= MaxRefreshRetries) return;
            // Focus loss is exactly when the runtime refuses the request
            // (Failure_OperationFailed): wait for the focus to come back.
            if (!Application.isFocused) return;

            _refreshRetries++;
            Debug.Log($"[Meta] Panel runs at {AppliedRefreshHz:0} Hz but {DesiredRefreshHz:0} Hz " +
                      $"was requested - retry {_refreshRetries}/{MaxRefreshRetries}.");
            ApplyRefreshRate(DesiredRefreshHz);
        }

        /// <summary>
        /// THE important frame rate lever on Android/Quest: Unity renders at a FIXED
        /// 30 fps when <c>QualitySettings.vSyncCount == 0</c> (which is the case for the
        /// Android quality level used here) and <c>Application.targetFrameRate</c> is left
        /// at its default -1 ("render at native refresh rate" is DESKTOP/Web behaviour,
        /// on Android -1 means "fixed 30 fps to conserve battery").
        /// Requesting the display frequency through OVRPlugin does NOT lift that cap, so
        /// the rate has to be named explicitly here as well. The value passed in is
        /// the rate the PANEL really runs at (see ApplyRefreshRate), so the render rate can
        /// never end up above the display rate - which is what happened before
        /// (panel 90 Hz, rendered 120 fps = uneven frame pacing).</summary>
        public static void ApplyUnityFrameRate(float hz)
        {
            if (Application.isEditor) return;
            try
            {
                QualitySettings.vSyncCount = 0;              // required so the value counts
                int target = Mathf.Max(30, Mathf.RoundToInt(hz));
                Application.targetFrameRate = target;
                UnityFrameRateApplied = true;
                UnityTargetFrameRate = target;
                Debug.Log($"[Meta] Unity target frame rate = {target} fps " +
                          $"(vSyncCount={QualitySettings.vSyncCount}, display refresh≈" +
                          SafeDisplayRefreshHz().ToString("0") + " Hz).");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[Meta] Unity frame rate could not be set: " + ex.Message);
            }
        }

        /// <summary>Last value written to <c>Application.targetFrameRate</c> (0 = not set).</summary>
        public static int UnityTargetFrameRate { get; private set; }

        /// <summary>true once <see cref="ApplyUnityFrameRate"/> ran successfully.</summary>
        public static bool UnityFrameRateApplied { get; private set; }

        /// <summary>
        /// Number of frames since app start that clearly missed the frame budget
        /// (&gt; 1.5x the target frame time). The AVERAGE fps hides these: "110 fps" with a
        /// 30 ms spike every second still LOOKS stuttery to the player, so the hitch count
        /// is what tells a smooth session from a stuttering one. Logged in the heartbeat.
        /// </summary>
        public static int TotalHitches { get; private set; }

        /// <summary>Longest single frame (ms) since app start - the worst hitch seen.</summary>
        public static float PeakFrameMs { get; private set; }

        /// <summary>Feeds one frame from the main loop into the hitch/peak statistics. Never throws.</summary>
        public static void ReportFrame(float dt)
        {
            if (dt <= 0f || float.IsNaN(dt)) return;

            float ms = dt * 1000f;
            if (ms > PeakFrameMs) PeakFrameMs = ms;
            if (UnityTargetFrameRate > 0)
            {
                float budgetMs = 1000f / UnityTargetFrameRate;
                if (ms > budgetMs * 1.5f)
                    TotalHitches++;
            }
        }

        /// <summary>Number of times an EXTERNAL write to the frame rate had to be repaired.</summary>
        public static int FrameRateRepairs { get; private set; }

        /// <summary>
        /// Re-applies <see cref="UnityTargetFrameRate"/> when something else changed it.
        ///
        /// This is deliberately a REPAIR loop and not a one-shot setter: any component that
        /// writes <c>Application.targetFrameRate</c> - for example a session/XR component that
        /// caches the value in OnEnable and restores it in OnDisable - would otherwise silently
        /// undo the setting and drop the game back to Unity's Android default of 30 fps.
        /// Cheap enough to run a few times per second (two property writes, no allocation).
        /// </summary>
        /// <returns>true when a foreign value was found and repaired.</returns>
        public static bool EnsureUnityFrameRate()
        {
            if (Application.isEditor || !UnityFrameRateApplied) return false;

            int want = DesiredUnityFrameRate();
            if (want <= 0) return false;
            if (Application.targetFrameRate == want && QualitySettings.vSyncCount == 0) return false;

            int was = Application.targetFrameRate;
            int wasVSync = QualitySettings.vSyncCount;

            try
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = want;
                FrameRateRepairs++;
                // First few occurrences are the interesting ones (they name the culprit value),
                // afterwards only occasionally so the log cannot flood.
                if (FrameRateRepairs <= 5 || FrameRateRepairs % 100 == 0)
                {
                    Debug.LogWarning("[Meta] target frame rate was changed externally " +
                                     "(targetFrameRate " + was + " -> " + want +
                                     ", vSyncCount " + wasVSync + " -> 0); re-applied (" +
                                     FrameRateRepairs + "x).");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[Meta] target frame rate could not be repaired: " + ex.Message);
                return false;
            }
            return true;
        }

        /// <summary>
        /// The frame rate the app should render at: the rate the panel really runs at when it
        /// is known (so render and display never drift apart), otherwise the desired
        /// rate. Keeps the repair loop honest when the system changes the panel rate later.
        /// </summary>
        public static int DesiredUnityFrameRate()
        {
            float effective = EffectiveDisplayHz();
            if (effective > 1f) return Mathf.RoundToInt(effective);
            if (UnityTargetFrameRate > 0) return UnityTargetFrameRate;
            return Mathf.RoundToInt(GameConfig.TargetDisplayRefreshRate);
        }

        /// <summary>
        /// Frame rate below which a measurement window counts as "target missed". Relative to
        /// the rate the app really renders at, so a 90 Hz session (panel refused 120 Hz) is not
        /// judged against the 120 Hz target and the watchdog cannot fire without a reason.
        /// </summary>
        public static float MinAcceptableFps()
        {
            int target = UnityTargetFrameRate > 0
                ? UnityTargetFrameRate
                : Mathf.RoundToInt(GameConfig.TargetDisplayRefreshRate);
            return Mathf.Max(60f, target * 0.85f);
        }

        /// <summary>Panel refresh rate reported by Unity (0 when unknown). Informational.</summary>
        public static float SafeDisplayRefreshHz()
        {
            try
            {
                double hz = UnityEngine.Screen.currentResolution.refreshRateRatio.value;
                return hz > 1.0 && !double.IsNaN(hz) ? (float)hz : 0f;
            }
            catch { return 0f; }
        }

        private static bool _suppressionPausedLogged;
        private static bool _suppressionRefusedLogged;
        private static float _guardianNextAttemptTime;

        /// <summary>
        /// Suppresses guardian/boundary visibility. Only requests when the
        /// actual state differs (like OVRManager.UpdateBoundary), so that there is no
        /// constant hammering of the system API.
        ///
        /// AND only while passthrough really runs: the OS refuses to hide the boundary
        /// outside passthrough ON PURPOSE (in a fully virtual scene the player must still
        /// see the grid). Asking anyway produced 14 warnings per session in the log of
        /// 2026-10-17 against 3 successes - all warnings in a passthrough-OFF phase, all
        /// successes right after "AR camera (passthrough layer) ON".
        ///
        /// While the app has no focus (system menu, space setup, permission dialog) the
        /// runtime answers with Failure_OperationFailed - so the request is skipped there
        /// and, after a refusal, repeated only after a short backoff instead of every tick.
        /// </summary>
        public static void RequestGuardianSuppression()
        {
            if (!GameConfig.SuppressGuardianBoundary) return;
            if (Application.isEditor || !OVRPlugin.initialized) return;
            if (!Application.isFocused) return;

            if (!MetaSeeThrough.LayerActive)
            {
                if (!_suppressionPausedLogged)
                {
                    _suppressionPausedLogged = true;
                    Debug.Log("[Meta] Guardian suppression paused: no passthrough layer " +
                              "(the OS keeps the boundary visible on purpose; it is " +
                              "suppressed again as soon as passthrough is ON).");
                }
                return;
            }
            _suppressionPausedLogged = false;

            if (Time.realtimeSinceStartup < _guardianNextAttemptTime) return;

            try
            {
                var getResult = OVRPlugin.GetBoundaryVisibility(out var visibility);
                if (getResult == OVRPlugin.Result.Success &&
                    visibility == OVRPlugin.BoundaryVisibility.Suppressed)
                {
                    _suppressionRefusedLogged = false;
                    _guardianNextAttemptTime = 0f;
                    return;
                }

                var result = OVRPlugin.RequestBoundaryVisibility(
                    OVRPlugin.BoundaryVisibility.Suppressed);

                if (result == OVRPlugin.Result.Success)
                {
                    _suppressionRefusedLogged = false;
                    _guardianNextAttemptTime = 0f;
                    Debug.Log("[Meta] Guardian boundary suppressed (no boundary in passthrough).");
                }
                else if (result == OVRPlugin.Result.Warning_BoundaryVisibilitySuppressionNotAllowed)
                {
                    _guardianNextAttemptTime = Time.realtimeSinceStartup + GuardianBackoffSeconds;
                    // Passthrough layer exists but the system still refuses (e.g. the system
                    // menu is open): log once, not on every watchdog tick.
                    if (!_suppressionRefusedLogged)
                    {
                        _suppressionRefusedLogged = true;
                        Debug.LogWarning("[Meta] Guardian boundary currently not suppressible " +
                                         "(system refuses despite passthrough) - " +
                                         "check boundaryVisibilitySupport in OculusProjectConfig.");
                    }
                }
                else if (result != OVRPlugin.Result.Failure_NotYetImplemented &&
                         result != OVRPlugin.Result.Failure_Unsupported)
                {
                    _guardianNextAttemptTime = Time.realtimeSinceStartup + GuardianBackoffSeconds;
                    Debug.LogWarning("[Meta] Boundary request unsuccessful: " + result);
                }
            }
            catch (System.Exception ex)
            {
                _guardianNextAttemptTime = Time.realtimeSinceStartup + GuardianBackoffSeconds;
                Debug.LogWarning("[Meta] Guardian suppression failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Sets the fixed foveated rendering level from GameConfig. Quest 3 has
        /// NO eye tracking, so dynamic foveation stays off.
        /// </summary>
        public static void ApplyFoveation()
        {
            if (Application.isEditor) return;

            try
            {
                OVRManager.foveatedRenderingLevel = GameConfig.FoveationLevel;
                OVRManager.useDynamicFoveatedRendering = false;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[Meta] Foveation could not be set: " + ex.Message);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInit()
        {
            var go = new GameObject("[Meta] Performance");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<MetaPerformanceBehaviour>();
        }
    }

    /// <summary>
    /// Runtime carrier of the <see cref="MetaPerformance"/> levers: delayed
    /// initialization (OVRPlugin needs a few frames), watchdog for
    /// boundary/foveation and the frame rate governor.
    /// </summary>
    internal sealed class MetaPerformanceBehaviour : MonoBehaviour
    {
        private float _frameRateCheckTimer;

        private void Start()
        {
            // NOT waiting for OVRPlugin on purpose: this is the lever that lifts Unity's
            // Android default of a FIXED 30 fps (see MetaPerformance.ApplyUnityFrameRate).
            MetaPerformance.ApplyUnityFrameRate(GameConfig.TargetDisplayRefreshRate);
            StartCoroutine(InitRoutine());
            StartCoroutine(WatchdogRoutine());
            StartCoroutine(GovernorRoutine());
        }

        /// <summary>
        /// Keeps the target frame rate honest. Cheap repair loop, see
        /// <see cref="MetaPerformance.EnsureUnityFrameRate"/> for why this is not a one-shot set.
        /// </summary>
        private void Update()
        {
            if (Application.isEditor) return;

            // Feed frame timing into the hitch/peak statistics reported by the heartbeat.
            MetaPerformance.ReportFrame(Time.unscaledDeltaTime);

            _frameRateCheckTimer += Time.unscaledDeltaTime;
            if (_frameRateCheckTimer < MetaPerformance.FrameRateCheckSeconds) return;
            _frameRateCheckTimer = 0f;
            MetaPerformance.EnsureUnityFrameRate();
        }

        /// <summary>Waits for OVRPlugin and then applies all levers once.</summary>
        private IEnumerator InitRoutine()
        {
            if (Application.isEditor) yield break;

            float elapsed = 0f;
            while (elapsed < MetaPerformance.InitTimeoutSeconds)
            {
                if (OVRPlugin.initialized)
                {
                    MetaPerformance.ApplyAll();
                    yield break;
                }
                elapsed += MetaPerformance.InitRetrySeconds;
                yield return new WaitForSecondsRealtime(MetaPerformance.InitRetrySeconds);
            }

            Debug.LogWarning("[Meta] OVRPlugin not initialized - performance levers skipped.");
        }

        /// <summary>
        /// Keeps boundary suppression and foveation in place - the runtime sometimes
        /// resets both on system menu/recenter transitions.
        /// </summary>
        private IEnumerator WatchdogRoutine()
        {
            if (Application.isEditor) yield break;

            // The panel only reaches the requested rate a moment after the session
            // starts (it comes up at 72/90 Hz) while the watchdog tick is 5 s - so the first
            // seconds of a session were stuck in slow motion. Poll quickly until the panel
            // has the requested rate, then fall back to the normal watchdog rhythm.
            int eager = 0;
            while (eager < 15
                   && MetaPerformance.DesiredRefreshHz > 1f
                   && MetaPerformance.AppliedRefreshHz < MetaPerformance.DesiredRefreshHz - 1f)
            {
                yield return new WaitForSecondsRealtime(MetaPerformance.EagerRetrySeconds);
                MetaPerformance.RetryDesiredRefreshRate();
                eager++;
            }

            var wait = new WaitForSecondsRealtime(MetaPerformance.WatchdogIntervalSeconds);
            while (true)
            {
                yield return wait;
                MetaPerformance.RequestGuardianSuppression();
                MetaPerformance.ApplyFoveation();
                // pursue the requested panel rate for a while (the request can be
                // missed early in the session), then settle on what the panel really gives.
                MetaPerformance.RetryDesiredRefreshRate();
            }
        }

        /// <summary>
        /// One-way frame rate watchdog: If the MEASURED frame rate stays below
        /// <see cref="MetaPerformance.MinAcceptableFps"/> (85 % of the rate the app
        /// really renders at) across a whole window
        /// (the target is not held, e.g. due to thermal throttling), it switches ONCE
        /// down to the fallback rate. No automatic switching up.
        /// </summary>
        private IEnumerator GovernorRoutine()
        {
            if (Application.isEditor) yield break;

            yield return new WaitForSecondsRealtime(MetaPerformance.GovernorWarmupSeconds);

            var sampleWait = new WaitForSecondsRealtime(MetaPerformance.GovernorSampleSeconds);
            float window = 0f;
            float sumDelta = 0f;
            int frames = 0;
            int badWindows = 0;
            bool downgraded = false;

            while (true)
            {
                yield return sampleWait;

                // Outliers occur during focus loss (system menu) - do not measure.
                if (!Application.isFocused) continue;

                float dt = Time.unscaledDeltaTime;
                sumDelta += dt;
                frames++;
                window += dt;

                if (window >= MetaPerformance.GovernorWindowSeconds)
                {
                    float avg = frames > 0 ? sumDelta / frames : 0f;
                    float fps = avg > 0.0001f ? 1f / avg : 0f;
                    float minOk = MetaPerformance.MinAcceptableFps();

                    if (fps > 0f && fps < minOk)
                    {
                        badWindows++;
                        if (!downgraded &&
                            badWindows >= MetaPerformance.GovernorBadWindowsBeforeDowngrade)
                        {
                            downgraded = true;
                            Debug.LogWarning($"[Meta] Frame rate {fps:0} < {minOk:0} " +
                                             $"for {badWindows} windows in a row " +
                                             $"- watchdog switching down to " +
                                             $"{GameConfig.FallbackDisplayRefreshRate:0} Hz.");
                            MetaPerformance.ApplyRefreshRate(GameConfig.FallbackDisplayRefreshRate);
                        }
                    }
                    else
                    {
                        badWindows = 0; // a good window clears the counter (no spurious drop)
                    }

                    window = 0f;
                    sumDelta = 0f;
                    frames = 0;
                }
            }
        }

        private void OnApplicationPause(bool pause)
        {
            if (!pause) Reapply();
        }

        private void OnApplicationFocus(bool focus)
        {
            if (focus) Reapply();
        }

        /// <summary>Re-enforce the desired frame rate + levers after resume/focus.</summary>
        private void Reapply()
        {
            if (Application.isEditor) return;

            // The frame rate must come back even when OVRPlugin (still) is not initialized -
            // focus/pause transitions are a classic moment to lose it.
            MetaPerformance.EnsureUnityFrameRate();

            if (!OVRPlugin.initialized) return;

            MetaPerformance.ApplyRefreshRate(
                MetaPerformance.DesiredRefreshHz > 0f
                    ? MetaPerformance.DesiredRefreshHz
                    : GameConfig.TargetDisplayRefreshRate);
            MetaPerformance.RequestGuardianSuppression();
            MetaPerformance.ApplyFoveation();
        }
    }
}

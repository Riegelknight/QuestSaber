// ==============================================================================
//  SafSongFolder - the song "inbox": songs from the headset's own Download folder.
// ==============================================================================
//  Why: since Android 11 the folders outside the app sandbox (`/storage/emulated/0/
//  Download`, `ModData/...`) can NOT be read with a plain file path any more - that
//  needs "All files access" (MANAGE_EXTERNAL_STORAGE).
//
//  Approach: "All files access" is asked on a system settings page (the resolved
//  settings handler is written into the picker log, see SafHelper.startSettingsPage).
//  At most once per START, and only while the access is not usable - "usable" is
//  verified by REALLY reading Download (CanReadDownload), not by trusting the page result.
//  From then on the shared Download folder is read and written with plain file paths -
//  no document chooser, no repeating dialog, no network.
//  Without the grant the app-owned media inbox below still works.
//
//  Download becomes the inbox: every song found inside - a song folder (contains
//  info.dat / info.json) or a .zip archive downloaded from a song site - is moved into
//  the VISIBLE song folder, which is created automatically and is also the folder the game
//  PLAYS from: the player finds the songs where he put them, and the game reads exactly those
//  files (no hidden app-private copy any more). That folder lives at the TOP LEVEL of
//  the shared storage (/storage/emulated/0/QuestSaberSongs) and NOT inside Download any more -
//  the Files app's aggregated "Downloads" view listed the folder's files FLAT and never showed
//  the folder, so Download looked cluttered (the .nomedia marker did not help
//  there). Without "All files access" the app-own folder GameConfig.ImportedSongsPath is used
//  instead. Songs of an older install (Download/QuestSaberSongs) are moved over automatically.
//  GameManager scans at every app start and when the app comes back to the front (the
//  Quest browser is a separate app), so no manual "refresh" menu row exists any more
//  (see GameManager.StorageAccessStartupRoutine).
//  The legacy document chooser (SafPickActivity) can no longer be opened (its only
//  launcher, SafSongFolder.Pick, was removed); only reading an already-granted legacy
//  folder survives (HasFolder/TreeUri).
//
//  Implementation: the java part (Assets/Plugins/Android/SafHelper.java) does the
//  scanning/moving/unpacking. From GameConfig.ImportedSongsPath on, the existing file based
//  scanner, parser and audio loader work completely unchanged.
//
//  Every method here is guarded and never throws: a missing/odd chooser must not
//  break the game (the app-own "Songs" folder and the demo song keep working).
//
//  FALLBACK WITHOUT ANY PERMISSION OR DIALOG (this is what makes songs work even on
//  builds that ship no document picker): the app's own external media folder
//  /sdcard/Android/media/<pkg>/Songs. It belongs to the app, so it can be read,
//  unpacked and emptied WITHOUT any grant, while a normal file manager (CX, or USB/MTP
//  from a PC) is still allowed to write into it - see SafHelper.importFromMediaInbox.
//  GameManager checks it on every scan, so dropping a .zip or a song folder in there is
//  enough. If the SAF chooser does not exist on the device, this is the working path.
// ==============================================================================
using System;
using UnityEngine;

namespace QuestSaber
{
    /// <summary>Thin C# wrapper around the java SAF helper (no-ops outside Android).</summary>
    public static class SafSongFolder
    {
        private const string HelperClass = "com.questsaber.SafHelper";
        private const string PrefsName = "questsaber_saf";
        private const string KeyUri = "tree_uri";
        private const int ModePrivate = 0;

        /// <summary>Visible song folder created at the storage root (e.g. /storage/emulated/0/QuestSaberSongs).</summary>
        public const string VisibleFolderName = "QuestSaberSongs";

        // The ".nomedia" marker is gone - the folder stays a normal folder.
        // It only hid the songs from the MEDIA library (never from the Files app's aggregated
        // views, which had caused the cluttered Download) and the player deleted it on the
        // headset by hand ("muss nicht erstellt werden"). See SafHelper.prepareVisibleSongFolder.

        // Remembered result of the visible song folder. "" means "not available (yet)",
        // so the check is not repeated in a hot path once it was created successfully.
        private static string _visibleSongsPath;

        // ------------------------------------------------------------------ system UI guard
        //
        // The app itself hands the focus to Android when it opens a system page (the "All files
        // access" settings page at boot, the legacy picker). That focus loss looks EXACTLY like
        // the system menu / headset-off case, and in the run of 2026-10-17 it made the game
        // auto-pause itself right after the boot-time permission ask ("Focus lost during play
        // (system menu / headset off)" while the settings page was in front). So every system
        // page the game opens is announced here, and the auto-pause ignores the focus loss that
        // follows. Cleared explicitly when the app gets the focus back, and by a timeout in case
        // the page never actually opened (then the run stays playable).
        private const float SystemUiTimeoutSeconds = 30f;
        private static float _systemUiUntil = -1f;

        /// <summary>Announces a system page the game is about to open (see RequestAllFilesAccess,
        /// SetVisibleFolder). While this is pending, a focus loss must not pause the run.</summary>
        public static void ExpectSystemUi(string reason)
        {
            _systemUiUntil = Time.realtimeSinceStartup + SystemUiTimeoutSeconds;
            Debug.Log("[QuestSaber] System page expected (" + reason + "): a focus loss within " +
                      SystemUiTimeoutSeconds.ToString("0") + " s will not pause the run.");
        }

        /// <summary>true while a system page opened by the game may be in front.</summary>
        public static bool SystemUiExpected => Time.realtimeSinceStartup < _systemUiUntil;

        /// <summary>The app has the focus again - the expectation is used up. Never throws.</summary>
        public static void ClearSystemUiExpectation()
        {
            _systemUiUntil = -1f;
        }

        /// <summary>
        /// Absolute path of the VISIBLE song folder (<c>/storage/emulated/0/
        /// QuestSaberSongs</c>), or "" when the shared storage cannot be read (no "All files
        /// access"). The player's songs are kept HERE and played from here: the old design
        /// copied everything into an app-private folder, so the songs were neither findable
        /// with a file manager nor the ones the player had downloaded. Created on first use
        /// and cached (this is asked on every song scan). Never throws.
        /// </summary>
        public static string VisibleSongsPath
        {
            get
            {
                if (!string.IsNullOrEmpty(_visibleSongsPath)) return _visibleSongsPath;
                // Gated on the REAL grant, not on the file probe. The probe passes
                // without "All files access" (listing works, reading existing files does not),
                // which made the song reader walk into Download and die on info.dat.
                if (!IsAndroid || !HasAllFilesAccess || !CanReadDownload) return "";
                try
                {
                    string p = EnsureVisibleSongFolder();
                    if (!string.IsNullOrEmpty(p)) _visibleSongsPath = p;
                    return _visibleSongsPath;
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        /// <summary>true on a real Android/Quest build (in the editor everything is a no-op).</summary>
        public static bool IsAndroid => Application.platform == RuntimePlatform.Android;

        /// <summary>The persisted folder URI, empty when the player never picked one.</summary>
        public static string TreeUri => PrefString(KeyUri);

        /// <summary>true as soon as a folder was picked once (survives restarts).</summary>
        public static bool HasFolder => !string.IsNullOrEmpty(TreeUri);

        /// <summary>
        /// Starts copying every song folder from the picked folder into
        /// <paramref name="destDir"/> on a BACKGROUND thread (so a big folder cannot
        /// freeze the VR session). Poll <see cref="ImportRunning"/> and then read
        /// <see cref="ImportResult"/>. The java side only replaces the destination when
        /// at least one song was found, so an unusable folder never wipes the songs
        /// that are already imported.
        /// </summary>
        public static void ImportAsync(string destDir)
        {
            if (!IsAndroid) return;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var helper = new AndroidJavaClass(HelperClass))
                {
                    helper.CallStatic("copySongsAsync", activity, destDir);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[QuestSaber] Song import could not start: " + ex.Message);
                DebugLogFile.Mark("song import could not start: " + ex.Message);
            }
        }

        /// <summary>true while the background import started with ImportAsync is still running.</summary>
        public static bool ImportRunning
        {
            get
            {
                if (!IsAndroid) return false;
                try
                {
                    using (var helper = new AndroidJavaClass(HelperClass))
                        return helper.CallStatic<bool>("isImportRunning");
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>One line JSON status of the last import (empty while it is still running).</summary>
        public static string ImportResult
        {
            get
            {
                if (!IsAndroid) return "";
                try
                {
                    using (var helper = new AndroidJavaClass(HelperClass))
                        return helper.CallStatic<string>("lastImportResult") ?? "";
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        // ------------------------------------------------------------------ permission-free inbox

        /// <summary>
        /// Absolute path of the permission-free song inbox
        /// (<c>/sdcard/Android/media/&lt;pkg&gt;/Songs</c>), or "" when the device has none.
        /// This folder needs NO permission and NO system dialog: it is part of the app's own
        /// external storage, so a file manager (or USB) can drop songs in, and the app can
        /// still read, unpack and delete them. It exists because some Horizon OS builds ship
        /// no document picker at all, which makes the SAF path above unusable there.
        /// </summary>
        public static string InboxPath
        {
            get
            {
                if (!IsAndroid) return "";
                try
                {
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    using (var helper = new AndroidJavaClass(HelperClass))
                        return helper.CallStatic<string>("mediaInboxPath", activity) ?? "";
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        /// <summary>Creates the permission-free inbox so a file manager can fill it right away.</summary>
        public static string EnsureInbox()
        {
            if (!IsAndroid) return "";
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var helper = new AndroidJavaClass(HelperClass))
                    return helper.CallStatic<string>("ensureMediaInbox", activity) ?? "";
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Could not create the song inbox: " + ex.Message);
                return "";
            }
        }

        /// <summary>
        /// Moves every song folder / .zip found in the permission-free inbox into
        /// <paramref name="destDir"/> (background thread, same rules as ImportAsync: a song
        /// folder is copied and the source removed, a .zip is unpacked only when it really
        /// contains a song and then deleted, anything else is left alone).
        /// </summary>
        public static void ImportFromInboxAsync(string destDir)
        {
            if (!IsAndroid) return;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var helper = new AndroidJavaClass(HelperClass))
                {
                    helper.CallStatic("importFromMediaInboxAsync", activity, destDir);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[QuestSaber] Inbox import could not start: " + ex.Message);
                DebugLogFile.Mark("inbox import could not start: " + ex.Message);
            }
        }

        /// <summary>
        /// Tail of the picker diagnostic log (written by SafPickActivity/SafHelper). The
        /// picker used to fail invisibly - this is how the game (and the debug log) can show
        /// whether a chooser even exists on the device. Never throws, "" when empty.
        /// </summary>
        public static string PickLog()
        {
            if (!IsAndroid) return "";
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var helper = new AndroidJavaClass(HelperClass))
                    return helper.CallStatic<string>("pickLog", activity) ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        // ------------------------------------------------------------------ all files access

        /// <summary>
        /// true when the app has "All files access" (MANAGE_EXTERNAL_STORAGE) - i.e. it may
        /// read and write the shared Download folder directly. This replaced the SAF chooser:
        /// no picker, no repeating dialog, granted once on a system settings page.
        /// </summary>
        public static bool HasAllFilesAccess
        {
            get
            {
                if (!IsAndroid) return false;
                try
                {
                    using (var helper = new AndroidJavaClass(HelperClass))
                        return helper.CallStatic<bool>("hasAllFilesAccess");
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>
        /// Opens the ONE system settings page that grants "All files access" for this app.
        /// Returns "" when the page was started (or access is already granted), otherwise a
        /// short error note - the game keeps running on the media inbox in that case.
        /// </summary>
        public static string RequestAllFilesAccess()
        {
            if (!IsAndroid) return "not android";
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var helper = new AndroidJavaClass(HelperClass))
                {
                    string note = helper.CallStatic<string>("requestAllFilesAccess", activity) ?? "";
                    // "" = the page was started (or access is already granted): the app is about
                    // to lose the focus for a moment, which must not be read as a system menu.
                    if (string.IsNullOrEmpty(note)) ExpectSystemUi("all files access page");
                    return note;
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Brings the game back to the foreground. Called right after the "All files access"
        /// grant was verified, so the player does not have to close the system window by hand
        /// (on Horizon OS the all-files page can hand off to the Files/Download view,
        /// see SafHelper.bringToFront). Best effort - never throws.
        /// </summary>
        public static void BringToFront()
        {
            if (!IsAndroid) return;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var helper = new AndroidJavaClass(HelperClass))
                    helper.CallStatic("bringToFront", activity);
            }
            catch (Exception) { }
        }

        /// <summary>Absolute path of the shared Download folder ("" when unavailable).</summary>
        public static string DownloadPath
        {
            get
            {
                if (!IsAndroid) return "";
                try
                {
                    using (var helper = new AndroidJavaClass(HelperClass))
                        return helper.CallStatic<string>("downloadPath") ?? "";
                }
                catch (Exception) { return ""; }
            }
        }

        /// <summary>
        /// true when the shared Download folder can REALLY be listed/read. This is the
        /// signal for SCANNING songs - reading them needs no write access. The raw "All files
        /// access" flag is deliberately NOT trusted here (on Horizon OS it can disagree with
        /// what is really possible).
        /// </summary>
        public static bool CanReadDownload
        {
            get
            {
                if (!IsAndroid) return false;
                try
                {
                    using (var helper = new AndroidJavaClass(HelperClass))
                        return helper.CallStatic<bool>("canReadDownload");
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>
        /// true when a probe file can REALLY be created and removed in the shared Download
        /// folder. Splits this from <see cref="CanReadDownload"/>: reading songs only
        /// needs read access, while writing is needed for the log location and for MOVING
        /// downloaded songs into the visible folder. Gating the song scan on the write probe
        /// made the songs disappear on a build where Download is readable but not
        /// writable.
        /// </summary>
        public static bool CanWriteDownload
        {
            get
            {
                if (!IsAndroid) return false;
                try
                {
                    using (var helper = new AndroidJavaClass(HelperClass))
                        return helper.CallStatic<bool>("canWriteDownload");
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>
        /// The app's own external media folder (<c>/sdcard/Android/media/&lt;pkg&gt;</c>). It needs
        /// NO permission and IS reachable with a file manager or over USB (unlike
        /// Android/data). Used as the always-accessible fallback home for the debug log.
        /// "" when unavailable.
        /// </summary>
        public static string MediaDir
        {
            get
            {
                if (!IsAndroid) return "";
                try
                {
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    using (var helper = new AndroidJavaClass(HelperClass))
                        return helper.CallStatic<string>("mediaDir", activity) ?? "";
                }
                catch (Exception) { return ""; }
            }
        }

        /// <summary>
        /// One-line diagnosis of the shared Download folder (raw permission flag plus what
        /// is really possible). Written into the game log at start so a device can be
        /// judged without guessing. Never throws, "" in the editor.
        /// </summary>
        public static string StorageStatus
        {
            get
            {
                if (!IsAndroid) return "";
                try
                {
                    using (var helper = new AndroidJavaClass(HelperClass))
                        return helper.CallStatic<string>("storageStatus") ?? "";
                }
                catch (Exception) { return ""; }
            }
        }

        /// <summary>
        /// Creates/returns the VISIBLE song folder. It lives at the TOP LEVEL of the
        /// shared storage (<c>/storage/emulated/0/QuestSaberSongs</c>) instead of inside
        /// Download. Reason: the Quest Files app's "Downloads"/"Media"/"Documents"/"Recents"
        /// sections are aggregated views that list a folder's files FLAT and never show the
        /// folder itself, so Download/QuestSaberSongs appeared as a flood of song files and
        /// Download looked cluttered (the .nomedia marker did not help there). At
        /// the storage root the folder shows up as ONE folder in "This headset"/USB, just like
        /// on a PC. Needs all-files access; the songs of an older install are moved out of
        /// Download automatically.
        /// </summary>
        public static string EnsureVisibleSongFolder()
        {
            if (!IsAndroid) return "";
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var helper = new AndroidJavaClass(HelperClass))
                    return helper.CallStatic<string>("prepareVisibleSongFolder", activity) ?? "";
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Could not create " + VisibleFolderName +
                                 " (storage root): " + ex.Message);
                return "";
            }
        }

        /// <summary>
        /// Diagnostic note of the last <see cref="EnsureVisibleSongFolder"/> call (mainly the
        /// automatic move out of Download, e.g. "migratedFromDownload=13 legacyCleared"). Written
        /// into the game log because the java logcat is not reachable on the headset.
        /// Never throws, "" in the editor.
        /// </summary>
        public static string VisibleFolderNote()
        {
            if (!IsAndroid) return "";
            try
            {
                using (var helper = new AndroidJavaClass(HelperClass))
                    return helper.CallStatic<string>("visibleFolderNote") ?? "";
            }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// Moves every song folder / .zip found directly in the shared Download folder into
        /// <paramref name="destDir"/> (background thread, same rules as the inbox import;
        /// a legacy Download/QuestSaberSongs folder is never consumed).
        /// </summary>
        public static void ImportFromDownloadAsync(string destDir)
        {
            if (!IsAndroid) return;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var helper = new AndroidJavaClass(HelperClass))
                {
                    helper.CallStatic("importFromDownloadAsync", activity, destDir);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[QuestSaber] Download import could not start: " + ex.Message);
                DebugLogFile.Mark("download import could not start: " + ex.Message);
            }
        }

        /// <summary>
        /// Copies a local file (e.g. the debug log) into the shared Download folder under
        /// <paramref name="displayName"/>. Returns "ok" or a short error note; never throws.
        /// </summary>
        public static string ExportFileToDownload(string sourcePath, string displayName)
        {
            if (!IsAndroid) return "not android";
            try
            {
                using (var helper = new AndroidJavaClass(HelperClass))
                    return helper.CallStatic<string>("exportFileToDownload", sourcePath, displayName) ?? "";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // ------------------------------------------------------------------ prefs

        private static string PrefString(string key)
        {
            if (!IsAndroid) return "";
            try
            {
                string value;
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var prefs = activity.Call<AndroidJavaObject>(
                           "getSharedPreferences", PrefsName, ModePrivate))
                {
                    value = prefs.Call<string>("getString", key, "");
                }
                return value ?? "";
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Song folder settings unreadable: " + ex.Message);
                return "";
            }
        }
    }
}

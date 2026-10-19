package com.questsaber;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.UriPermission;
import android.database.Cursor;
import android.media.MediaScannerConnection;
import android.net.Uri;
import android.provider.DocumentsContract;
import android.util.Log;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

/**
 * QuestSaber - Storage Access Framework (SAF) helper.
 *
 * Turns the folder the player picked once with the system chooser (SafPickActivity)
 * into an "inbox": every song found inside - either a song folder (contains
 * info.dat / info.json) or a .zip downloaded from a song site - is copied into a
 * normal app-owned folder (GameConfig.ImportedSongsPath) and the source is deleted
 * afterwards (= moved), so the Download folder stays tidy.
 *
 * (Legacy SAF path only) In addition the imported songs are mirrored into the visible
 * sub folder "QuestSaberSongs" inside the picked folder (e.g. Download/QuestSaberSongs),
 * which is created automatically, so the player always finds the songs with a normal
 * file manager. That mirror is a pure copy: the import into the app-owned folder runs
 * first, so nothing about playback depends on the mirror. (Since v1.8
 * Download/QuestSaberSongs is also a scan root - see GameConfig.SongsRootCandidates -
 * so a song sitting there is playable as well and is listed only once, never twice.)
 *
 * After that the existing file based song scanner / parser / audio loader work
 * completely unchanged (no content URIs needed there).
 *
 * SECOND, INDEPENDENT PATH (no chooser at all): the app's own external media folder
 * /sdcard/Android/media/<pkg>/Songs. Some builds have no document picker, so the SAF path
 * above can never complete there; this folder needs no grant and no dialog, yet a file
 * manager (or USB/MTP) may still write into it. See importFromMediaInbox / walkFs.
 *
 * Only standard Android APIs, no permission, no network - fully offline.
 */
public final class SafHelper {

    public static final String PREFS = "questsaber_saf";
    public static final String KEY_URI = "tree_uri";
    public static final String KEY_NAME = "tree_name";
    /** Bumped by SafPickActivity whenever the chooser closes (picked OR cancelled). */
    public static final String KEY_SEQ = "pick_seq";
    /** Same as KEY_SEQ, but ONLY when the player really picked a folder (not on cancel). */
    public static final String KEY_PICKED = "picked_seq";
    /** "1" as soon as the app asked for the song folder by itself (asked once per install). */
    public static final String KEY_PROMPTED = "asked_once";

    /**
     * Visible song folder that the player always finds with a normal file manager. v1.11.7:
     * at the TOP LEVEL of the shared storage (<c>/storage/emulated/0/QuestSaberSongs</c>);
     * v1.8 .. v1.11.6 it sat inside Download, which the Files app's aggregated views listed
     * as a flood of files (see {@link #prepareVisibleSongFolder}).
     */
    public static final String VISIBLE_FOLDER = "QuestSaberSongs";

    // v1.13.3: the ".nomedia" marker is GONE. It only ever kept the MEDIA library away from
    // the song files (never the Files app's aggregated views - see prepareVisibleSongFolder),
    // and the player removes it by hand on the headset anyway ("must not be created, I deleted
    // it on the Quest"). So the visible song folder is a completely normal folder now.

    /** The media library purge of the legacy Download folder runs at most once per process. */
    private static boolean legacyDownloadPurged = false;

    /**
     * Diagnostic note of the last {@link #prepareVisibleSongFolder} call (mainly the migration
     * counts). Read by the game and written into the debug log, because the java logcat is not
     * reachable on the headset - the log file is the only diagnostic channel (v1.11.7).
     */
    private static volatile String visibleFolderNote = "";

    /**
     * Permission-free inbox: a folder inside the app's OWN external media dir
     * (/sdcard/Android/media/&lt;pkg&gt;/Songs). Nothing has to be granted for it - it is the
     * app's own storage, so a file manager (or USB) can drop songs in AND the app can read,
     * unpack and delete them. It exists because some builds ship no system folder chooser
     * at all (see SafPickActivity), which makes the SAF path unusable there.
     */
    public static final String MEDIA_INBOX = "Songs";

    /** Diagnostic log of the folder picker (its result was invisible before - only logcat). */
    private static final String PICK_LOG = "pick_log.txt";
    /** The pick log is truncated above this size so it can never grow without bound. */
    private static final long PICK_LOG_MAX = 16L * 1024L;

    private static final String TAG = "QuestSaber";
    /** Song files above this size are skipped (safety net, normal audio is far smaller). */
    private static final long MAX_FILE_BYTES = 100L * 1024L * 1024L;
    /** Upper bound for one unpacked .zip (aborts a runaway archive). */
    private static final long MAX_ZIP_BYTES = 2L * 1024L * 1024L * 1024L;
    private static final int MAX_DEPTH = 8;

    /** True while a background import is running (polled by the Unity side). */
    private static volatile boolean importRunning = false;
    /** JSON result of the last background import (polled by the Unity side). */
    private static volatile String importResult = "";

    private SafHelper() { }

    // ------------------------------------------------------------------ async

    /** true while {@link #copySongsAsync} is still copying. */
    public static boolean isImportRunning() {
        return importRunning;
    }

    /** JSON result of the last {@link #copySongsAsync} run ("" while it runs). */
    public static String lastImportResult() {
        return importResult;
    }

    /**
     * Copies/moves all songs from the picked folder into destDir on a worker thread,
     * so a big folder does not freeze the VR session. The Unity side polls
     * {@link #isImportRunning()} / {@link #lastImportResult()}.
     */
    public static void copySongsAsync(Context ctx, String destDir) {
        if (importRunning) return;
        importRunning = true;
        importResult = "";
        final Context app = ctx != null ? ctx.getApplicationContext() : null;
        final String dir = destDir;
        Thread worker = new Thread(new Runnable() {
            @Override
            public void run() {
                String r;
                try {
                    r = SafHelper.copySongs(app, dir);
                } catch (Throwable t) {
                    Log.e(TAG, "Background song import failed: " + t);
                    r = "{\"ok\":false,\"error\":\"" + SafHelper.escape(String.valueOf(t)) + "\"}";
                }
                importResult = r;
                importRunning = false;
            }
        }, "QuestSaberSongImport");
        worker.setDaemon(true);
        worker.start();
    }

    // ------------------------------------------------------------------ folder

    public static String getTreeUri(Context ctx) {
        try {
            return prefs(ctx).getString(KEY_URI, "");
        } catch (Throwable t) {
            return "";
        }
    }

    public static String getTreeName(Context ctx) {
        try {
            return prefs(ctx).getString(KEY_NAME, "");
        } catch (Throwable t) {
            return "";
        }
    }

    public static void clear(Context ctx) {
        try {
            // Also drop the "asked once" mark: after forgetting the folder the app may ask
            // for a new one again.
            prefs(ctx).edit().remove(KEY_URI).remove(KEY_NAME).remove(KEY_PROMPTED).apply();
        } catch (Throwable t) {
            Log.w(TAG, "clear song folder failed: " + t);
        }
    }

    /** Value that changes as soon as the system folder chooser is closed again. */
    public static String getPickSeq(Context ctx) {
        try {
            return prefs(ctx).getString(KEY_SEQ, "");
        } catch (Throwable t) {
            return "";
        }
    }

    /** true when the app already asked for the song folder by itself (once per install). */
    public static boolean wasPrompted(Context ctx) {
        try {
            return "1".equals(prefs(ctx).getString(KEY_PROMPTED, ""));
        } catch (Throwable t) {
            return false;
        }
    }

    /** Remembers that the app asked for the song folder by itself (no nagging later). */
    public static void markPrompted(Context ctx) {
        try {
            prefs(ctx).edit().putString(KEY_PROMPTED, "1").apply();
        } catch (Throwable t) {
            Log.w(TAG, "Could not remember the folder prompt: " + t);
        }
    }

    /**
     * true when a folder was picked AND its persisted grant is still valid AND the folder
     * is still reachable. The stored URI alone is not enough: the grant is lost after a
     * reinstall / "clear data" / revoked permission and the folder itself can be deleted
     * or renamed in the file manager. When this is false the app asks for the folder again
     * (it opens the system chooser by itself, see GameManager.SongFolderStartupRoutine).
     */
    public static boolean hasUsableFolder(Context ctx) {
        if (ctx == null) return false;
        String uri = getTreeUri(ctx);
        if (uri == null || uri.length() == 0) return false;

        Uri tree;
        try {
            tree = Uri.parse(uri);
        } catch (Throwable t) {
            return false;
        }

        boolean granted = false;
        try {
            for (UriPermission p : ctx.getContentResolver().getPersistedUriPermissions()) {
                if (p.isReadPermission() && tree.equals(p.getUri())) {
                    granted = true;
                    break;
                }
            }
        } catch (Throwable t) {
            Log.w(TAG, "Reading persisted folder grants failed: " + t);
        }
        if (!granted) return false;

        try {
            String rootId = DocumentsContract.getTreeDocumentId(tree);
            return listChildren(ctx, tree, rootId) != null;
        } catch (Throwable t) {
            Log.w(TAG, "Picked folder not reachable: " + t);
            return false;
        }
    }

    /** True when the persisted grant also includes WRITE (needed to move/delete). */
    public static boolean hasWriteGrant(Context ctx) {
        if (ctx == null) return false;
        try {
            Uri tree = Uri.parse(getTreeUri(ctx));
            for (UriPermission p : ctx.getContentResolver().getPersistedUriPermissions()) {
                if (p.isWritePermission() && tree.equals(p.getUri())) return true;
            }
        } catch (Throwable t) {
            Log.w(TAG, "write grant check failed: " + t);
        }
        return false;
    }

    /**
     * Creates (or finds) a sub folder directly under the picked folder. Returns its
     * document id, or "" when it cannot be created (e.g. read-only grant).
     * Used for "QuestSaberSongs" (the visible song folder inside Download).
     */
    public static String ensureSubfolder(Context ctx, String displayName) {
        if (ctx == null || displayName == null || displayName.length() == 0) return "";
        try {
            Uri tree = Uri.parse(getTreeUri(ctx));
            String rootId = DocumentsContract.getTreeDocumentId(tree);
            return findOrCreateDir(ctx, tree, rootId, displayName);
        } catch (Throwable t) {
            Log.w(TAG, "Could not create/read sub folder '" + displayName + "': " + t);
            return "";
        }
    }

    // ------------------------------------------------------------------ log export

    /**
     * Copies a normal app-private file (the debug log) into the picked folder so it can
     * be grabbed with a file manager / over USB - no additional permission needed (the
     * player's folder grant is enough). Existing copies are overwritten (SAF documents
     * cannot be appended reliably). Returns "" on success, else a short reason.
     */
    public static String exportFileToTreeRoot(Context ctx, String sourcePath, String displayName) {
        if (ctx == null) return "no context";
        try {
            Uri tree = Uri.parse(getTreeUri(ctx));
            if (tree == null) return "no folder";
            String rootId = DocumentsContract.getTreeDocumentId(tree);

            // find an existing copy (so we do not pile up "log (1).txt" files)
            String existing = null;
            List<Child> kids = listChildren(ctx, tree, rootId);
            if (kids != null) {
                for (Child c : kids) {
                    if (!c.dir && displayName.equalsIgnoreCase(c.name)) { existing = c.docId; break; }
                }
            }

            Uri docUri;
            if (existing != null) {
                docUri = DocumentsContract.buildDocumentUriUsingTree(tree, existing);
            } else {
                Uri parent = DocumentsContract.buildDocumentUriUsingTree(tree, rootId);
                docUri = DocumentsContract.createDocument(ctx.getContentResolver(), parent,
                        "text/plain", displayName);
                if (docUri == null) return "cannot create";
            }

            InputStream in = null;
            OutputStream os = null;
            try {
                in = new java.io.FileInputStream(sourcePath);
                os = ctx.getContentResolver().openOutputStream(docUri, "wt");
                if (os == null) return "no stream";
                byte[] buf = new byte[64 * 1024];
                int n;
                while ((n = in.read(buf)) > 0) os.write(buf, 0, n);
            } finally {
                try { if (os != null) os.close(); } catch (Throwable ignored) { }
                try { if (in != null) in.close(); } catch (Throwable ignored) { }
            }
            return "";
        } catch (Throwable t) {
            Log.w(TAG, "Log export failed: " + t);
            return String.valueOf(t);
        }
    }

    private static SharedPreferences prefs(Context ctx) {
        return ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    // ------------------------------------------------------------------ picker log

    /**
     * Appends a line to the picker diagnostic log. The picker used to fail INVISIBLY (its
     * only trace was logcat, which nobody can read without ADB), so "the app does nothing
     * when it asks for the folder" was impossible to explain. This log is a normal app file:
     * the game copies it into its own debug log at start (and it can be pulled over USB).
     * Never throws - diagnostics must not be able to break the picker.
     */
    public static void notePick(Context ctx, String text) {
        try {
            Log.i(TAG, "pick: " + text);
        } catch (Throwable ignored) { }
        try {
            if (ctx == null) return;
            File f = new File(ctx.getFilesDir(), PICK_LOG);
            if (f.isFile() && f.length() > PICK_LOG_MAX) f.delete();
            String line = new java.text.SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US)
                    .format(new java.util.Date()) + " " + text + "\n";
            FileOutputStream os = new FileOutputStream(f, true);
            try {
                os.write(line.getBytes("UTF-8"));
            } finally {
                os.close();
            }
        } catch (Throwable ignored) { }
    }

    /** Tail of the picker diagnostic log ("", never null, when there is nothing). */
    public static String pickLog(Context ctx) {
        try {
            if (ctx == null) return "";
            File f = new File(ctx.getFilesDir(), PICK_LOG);
            if (!f.isFile() || f.length() == 0) return "";
            long len = f.length();
            long skip = Math.max(0L, len - 8192L);
            InputStream in = new FileInputStream(f);
            try {
                long skipped = 0;
                while (skipped < skip) {
                    long s = in.skip(skip - skipped);
                    if (s <= 0) break;
                    skipped += s;
                }
                byte[] buf = new byte[(int) (len - skipped)];
                int n = in.read(buf);
                return n <= 0 ? "" : new String(buf, 0, n, "UTF-8");
            } finally {
                in.close();
            }
        } catch (Throwable t) {
            return "";
        }
    }

    /**
     * Tells the Unity side that the picker is closed WITHOUT a result (no picker on the
     * device, launch failed, ...). Without this the game waited 300 s for a result that
     * could never arrive. Does not touch {@link #KEY_PICKED}, so it counts as a cancel.
     */
    public static void markPickClosed(Context ctx) {
        try {
            prefs(ctx).edit().putString(KEY_SEQ, String.valueOf(System.currentTimeMillis())).apply();
        } catch (Throwable t) {
            Log.w(TAG, "Could not close the pick sequence: " + t);
        }
    }

    // ------------------------------------------------------------------ media inbox

    /**
     * Absolute path of the permission-free inbox ("" when the device has no external media
     * dir). The folder is NOT created here - use {@link #ensureMediaInbox} for that.
     */
    public static String mediaInboxPath(Context ctx) {
        File d = inboxDir(ctx, false);
        return d == null ? "" : d.getAbsolutePath();
    }

    /**
     * Creates the permission-free inbox (so a file manager can fill it right away) and
     * returns its absolute path ("" when unavailable).
     */
    public static String ensureMediaInbox(Context ctx) {
        File d = inboxDir(ctx, true);
        return d == null ? "" : d.getAbsolutePath();
    }

    private static File inboxDir(Context ctx, boolean create) {
        if (ctx == null) return null;
        try {
            File[] dirs = ctx.getExternalMediaDirs();
            if (dirs == null || dirs.length == 0 || dirs[0] == null) return null;
            File inbox = new File(dirs[0], MEDIA_INBOX);
            if (create && !inbox.isDirectory()) inbox.mkdirs();
            return inbox;
        } catch (Throwable t) {
            Log.w(TAG, "No external media folder: " + t);
            return null;
        }
    }

    /**
     * Same as {@link #copySongsAsync}, but for the permission-free inbox
     * (Android/media/&lt;pkg&gt;/Songs) instead of the player-picked SAF folder.
     */
    public static void importFromMediaInboxAsync(Context ctx, String destDir) {
        if (importRunning) return;
        importRunning = true;
        importResult = "";
        final Context app = ctx != null ? ctx.getApplicationContext() : null;
        final String dir = destDir;
        Thread worker = new Thread(new Runnable() {
            @Override
            public void run() {
                String r;
                try {
                    r = SafHelper.importFromMediaInbox(app, dir);
                } catch (Throwable t) {
                    Log.e(TAG, "Inbox import failed: " + t);
                    r = "{\"ok\":false,\"error\":\"" + SafHelper.escape(String.valueOf(t)) + "\"}";
                }
                importResult = r;
                importRunning = false;
            }
        }, "QuestSaberInboxImport");
        worker.setDaemon(true);
        worker.start();
    }

    /**
     * Moves everything found in the permission-free inbox into destDir, using exactly the
     * rules of {@link #copySongs}: a folder that contains an info file is a song (copied +
     * removed), a .zip is unpacked when it really contains a song (then removed), everything
     * else is only descended into. Needs no permission and no chooser.
     */
    public static String importFromMediaInbox(Context ctx, String destDir) {
        long started = System.currentTimeMillis();
        Stats st = new Stats();
        String error = "";
        File inbox = inboxDir(ctx, true);
        if (inbox == null) {
            return "{\"ok\":false,\"inbox\":\"\",\"error\":\"no external media folder\"}";
        }

        File out = new File(destDir);
        try {
            if (!out.isDirectory() && !out.mkdirs()) {
                return "{\"ok\":false,\"inbox\":\"" + escape(inbox.getAbsolutePath())
                        + "\",\"error\":\"cannot create destination\"}";
            }
            File[] kids = inbox.listFiles();
            if (kids == null) kids = new File[0];
            for (File k : kids) {
                String name = k.getName();
                if (name == null || name.length() == 0 || name.startsWith(".")) continue;
                try {
                    if (k.isDirectory()) walkFs(k, out, st, 0);
                    else if (isZip(name)) importZipFs(k, out, st);
                    else st.skipped++; // e.g. the how-to file
                } catch (Throwable t) {
                    st.errors++;
                    Log.w(TAG, "Inbox entry failed (" + name + "): " + t);
                }
            }
        } catch (Throwable t) {
            error = String.valueOf(t);
            Log.e(TAG, "Inbox import failed: " + t);
        }

        boolean ok = (st.songs > 0) && (error.length() == 0);
        return resultJson(st, ok, true, error,
                ",\"inbox\":\"" + escape(inbox.getAbsolutePath()) + "\""
                        + ",\"ms\":" + (System.currentTimeMillis() - started));
    }

    /** Same rules as {@link #walk}, but for a plain directory (the permission-free inbox). */
    private static void walkFs(File dir, File dest, Stats st, int depth) {
        if (depth > MAX_DEPTH) return;
        File[] kids;
        try {
            kids = dir.listFiles();
        } catch (Throwable t) {
            st.errors++;
            return;
        }
        if (kids == null) {
            st.errors++;
            return;
        }

        boolean isSong = false;
        for (File k : kids) {
            if (!k.isDirectory() && isInfoFile(k.getName())) {
                isSong = true;
                break;
            }
        }

        if (isSong) {
            File songDir = new File(dest, safe(dir.getName()));
            if (copyTreeFs(dir, songDir, st, depth)) {
                st.songs++;
                if (deleteTreeFs(dir)) st.moved++;
            }
            return;
        }

        for (File k : kids) {
            String name = k.getName();
            if (name == null || name.length() == 0 || name.startsWith(".")) continue;
            if (k.isDirectory()) walkFs(k, dest, st, depth + 1);
            else if (isZip(name)) importZipFs(k, dest, st);
            else st.skipped++;
        }
    }

    /** Copies a whole song folder from the filesystem (all files + sub folders). */
    private static boolean copyTreeFs(File src, File dest, Stats st, int depth) {
        if (depth > MAX_DEPTH + 2) return false;
        if (!dest.isDirectory() && !dest.mkdirs()) {
            st.errors++;
            return false;
        }
        File[] kids;
        try {
            kids = src.listFiles();
        } catch (Throwable t) {
            st.errors++;
            return false;
        }
        if (kids == null) {
            st.errors++;
            return false;
        }
        boolean ok = true;
        for (File k : kids) {
            if (k.isDirectory()) {
                if (!copyTreeFs(k, new File(dest, safe(k.getName())), st, depth + 1)) ok = false;
            } else if (!copyFileFs(k, new File(dest, safe(k.getName())), st)) {
                ok = false;
            }
        }
        return ok;
    }

    /** Copies one file from the filesystem, skipping files that are already imported. */
    private static boolean copyFileFs(File src, File dest, Stats st) {
        long size;
        try {
            size = src.length();
        } catch (Throwable t) {
            size = -1L;
        }
        if (size > MAX_FILE_BYTES) {
            st.skipped++;
            Log.w(TAG, "Skipping large file: " + src.getName());
            return true; // deliberately not a failure
        }
        if (dest.isFile() && size > 0 && dest.length() == size) {
            st.skipped++; // already imported (same size)
            return true;
        }
        InputStream in = null;
        OutputStream os = null;
        try {
            in = new BufferedInputStream(new FileInputStream(src));
            os = new FileOutputStream(dest);
            byte[] buf = new byte[64 * 1024];
            int n;
            while ((n = in.read(buf)) > 0) {
                os.write(buf, 0, n);
                st.bytes += n;
            }
            st.files++;
            return true;
        } catch (Throwable t) {
            st.errors++;
            Log.w(TAG, "Copy failed (" + src.getName() + "): " + t);
            return false;
        } finally {
            try { if (os != null) os.close(); } catch (Throwable ignored) { }
            try { if (in != null) in.close(); } catch (Throwable ignored) { }
        }
    }

    /** {@link #importZip} for a plain file source (the inbox); deletes the .zip afterwards. */
    private static void importZipFs(File zip, File dest, Stats st) {
        String base = zip.getName();
        int dot = base.toLowerCase(Locale.US).lastIndexOf(".zip");
        if (dot > 0) base = base.substring(0, dot);
        base = safe(base);

        File staging = new File(dest, ".unpack_" + base);
        File target = new File(dest, base);
        deleteRecursive(staging);

        boolean song = false;
        InputStream in = null;
        try {
            in = new FileInputStream(zip);
            song = extractZip(in, staging, st);
        } catch (Throwable t) {
            st.errors++;
            Log.w(TAG, "Unzip failed (" + zip.getName() + "): " + t);
        } finally {
            try { if (in != null) in.close(); } catch (Throwable ignored) { }
        }

        if (!song) {
            deleteRecursive(staging);
            st.skipped++;
            Log.i(TAG, "Zip without a song ignored: " + zip.getName());
            return;
        }

        deleteRecursive(target);
        if (!staging.renameTo(target)) {
            st.errors++;
            Log.w(TAG, "Could not publish unzipped song: " + base);
            deleteRecursive(staging);
            return;
        }
        st.zips++;
        st.songs++;
        if (deleteTreeFs(zip)) st.moved++;
    }

    /** Deletes a file or a whole directory tree. true when it is gone. */
    private static boolean deleteTreeFs(File f) {
        if (f == null || !f.exists()) return true;
        if (f.isDirectory()) {
            File[] kids = f.listFiles();
            if (kids != null) for (File k : kids) deleteTreeFs(k);
        }
        boolean gone = f.delete();
        if (!gone) Log.w(TAG, "Could not delete: " + f.getAbsolutePath());
        return gone;
    }

    // ------------------------------------------------------------------ all files access (v1.4)
    //
    // The SAF chooser (SafPickActivity) never appears on this device, so v1.4 switches to
    // "All files access" (MANAGE_EXTERNAL_STORAGE): the game works on the real shared
    // Download folder directly - no picker, no repeating permission dialog. The player
    // grants it ONCE on a system settings page; everything after that is automatic.

    /** True when the app may read/write the shared storage (Download) directly. */
    public static boolean hasAllFilesAccess() {
        try {
            return android.os.Environment.isExternalStorageManager();
        } catch (Throwable t) {
            Log.w(TAG, "isExternalStorageManager failed: " + t);
            return false;
        }
    }

    /**
     * Absolute path of the shared Download folder ("" when unavailable). Not created here.
     */
    public static String downloadPath() {
        try {
            File d = android.os.Environment.getExternalStoragePublicDirectory(
                    android.os.Environment.DIRECTORY_DOWNLOADS);
            return d == null ? "" : d.getAbsolutePath();
        } catch (Throwable t) {
            Log.w(TAG, "Download path failed: " + t);
            return "";
        }
    }

    /**
     * Opens the system settings page that grants "All files access" for THIS app.
     * Returns "" when a page was started, otherwise a short error note. Never a repeating
     * dialog - if no page can be shown the game keeps running on the media inbox instead.
     *
     * Unlike v1.4 this does NOT fire a blind startActivity(): the handler is resolved first
     * and written into the picker log. On some Horizon OS builds the all-files page is
     * served by the Files app, which is exactly the extra "Download" window the player then
     * has to close by hand - with the log line the handler is known instead of guessed.
     */
    public static String requestAllFilesAccess(Activity act) {
        if (act == null) return "no activity";
        // v1.11.4: ask on EVERY start while the MANAGE_EXTERNAL_STORAGE grant is MISSING.
        // No probe-based skipping here, and no flag-based one either: on Horizon OS the file
        // probes (canReadDownload/canWriteDownload) can pass WITHOUT the grant - Scoped
        // Storage lets listFiles()/createNewFile() succeed while reading an existing file
        // such as info.dat is denied. Skipping on those probes is what made the permission
        // request disappear entirely in v1.11-v1.11.3. The grant flag alone decides.
        if (hasAllFilesAccess()) {
            return "";
        }
        String first = startSettingsPage(act, "android.settings.MANAGE_APP_ALL_FILES_ACCESS_PERMISSION", true);
        if (first == null) return "";
        String second = startSettingsPage(act, "android.settings.MANAGE_ALL_FILES_ACCESS_PERMISSION", false);
        if (second == null) return "";
        Log.w(TAG, "All-files settings page not available: " + second);
        notePick(act, "all-files settings page unavailable (" + first + "; " + second + ")");
        return "settings page not available: " + second;
    }

    /**
     * Brings OUR task back to the front (v1.10).
     *
     * Called as soon as the "All files access" grant was verified. On Horizon OS the
     * all-files page can hand off to the Files app, which is exactly the extra "Download"
     * window the player would otherwise have to close by hand after granting. Moving our
     * own task to the front takes the player straight back into the game, so the storage
     * process stays invisible (= the requested v1.5 behaviour). Best effort - never throws.
     */
    public static void bringToFront(Activity act) {
        if (act == null) return;
        try {
            android.app.ActivityManager am =
                    (android.app.ActivityManager) act.getSystemService(Context.ACTIVITY_SERVICE);
            if (am == null) return;
            am.moveTaskToFront(act.getTaskId(),
                    android.app.ActivityManager.MOVE_TASK_NO_USER_ACTION);
            Log.i(TAG, "Brought QuestSaber back to the front after the storage grant.");
        } catch (Throwable t) {
            Log.w(TAG, "bringToFront failed: " + t);
        }
    }

    /**
     * Starts one settings page. Returns null when it was started, otherwise a short reason.
     * The resolved activity (package/class) always goes into the picker log.
     */
    private static String startSettingsPage(Activity act, String action, boolean withPackage) {
        try {
            Intent i = new Intent(action);
            if (withPackage) i.setData(Uri.parse("package:" + act.getPackageName()));
            android.content.pm.ResolveInfo ri = act.getPackageManager().resolveActivity(i, 0);
            String handler = (ri == null || ri.activityInfo == null)
                    ? "<no handler>"
                    : ri.activityInfo.packageName + "/" + ri.activityInfo.name;
            notePick(act, "settings " + action + " -> " + handler);
            if (ri == null || ri.activityInfo == null) return "no handler for " + action;
            act.startActivity(i);
            return null;
        } catch (Throwable t) {
            notePick(act, "settings " + action + " failed: " + t);
            return String.valueOf(t);
        }
    }

    /**
     * Absolute path of the VISIBLE song folder at the TOP LEVEL of the shared storage -
     * <c>/storage/emulated/0/QuestSaberSongs</c> (v1.11.7) - or "" when it cannot be made.
     *
     * v1.8 .. v1.11.6 kept this folder INSIDE Download (Download/QuestSaberSongs).
     *
     * Why it moved OUT of Download: the Quest Files app sidebar has the real folder tree
     * ("This headset") plus the AGGREGATED views "Recents"/"Downloads"/"Media"/"Documents",
     * which list a folder's files FLAT and never show the folder itself. So
     * Download/QuestSaberSongs appeared as its 200+ song files and Download looked cluttered.
     * The ".nomedia" marker of v1.11.6 did NOT help (verified on the device in v1.11.6): those
     * views are not driven by the media library's no-media handling. At the storage root no
     * aggregated view covers the folder - it shows up as ONE normal folder in "This headset"
     * and over USB, exactly like on a PC, and Download stays clean.
     *
     * The songs of an older install are pulled out of Download ({@link #migrateDownloadSongs}).
     * Needs "All files access" (v1.13.3: no ".nomedia" marker any more - see above).
     */
    public static String prepareVisibleSongFolder(Context ctx) {
        try {
            File root = android.os.Environment.getExternalStorageDirectory();
            if (root == null) return "";
            File f = new File(root, VISIBLE_FOLDER);
            if (!f.isDirectory() && !f.mkdirs()) return "";
            // v1.13.3: no hideFromMediaLibrary() call any more (the marker is not wanted); the
            // legacy Download folder is still cleaned and the old songs are pulled over.
            visibleFolderNote = "";
            purgeLegacyDownloadFolder(ctx);
            migrateDownloadSongs(f);
            if (visibleFolderNote.length() == 0) visibleFolderNote = "ready";
            return f.getAbsolutePath();
        } catch (Throwable t) {
            Log.w(TAG, "Cannot create " + VISIBLE_FOLDER + ": " + t);
            return "";
        }
    }

    /**
     * Diagnostic note of the last {@link #prepareVisibleSongFolder} call, e.g.
     * "migratedFromDownload=13 legacyCleared" or "ready". Written into the game's debug log
     * (the java logcat is not reachable on the headset). Never throws, "" before the first call.
     */
    public static String visibleFolderNote() {
        return visibleFolderNote;
    }

    /**
     * v1.11.7: moves the songs of an older install from Download/QuestSaberSongs to the new
     * top-level folder and removes the old folder from Download, so Download is clean again.
     * A rename on the same volume is used, so this is instant even for gigabytes. The old
     * folder is only removed when nothing was left behind - a song is NEVER deleted. Never
     * throws.
     */
    private static void migrateDownloadSongs(File newDir) {
        try {
            String dl = downloadPath();
            if (dl.length() == 0) return;
            File old = new File(dl, VISIBLE_FOLDER);
            if (!old.isDirectory()) return;

            File[] kids = old.listFiles();
            if (kids == null || kids.length == 0) {
                deleteTreeFs(old);          // empty leftovers (e.g. only the .nomedia marker)
                visibleFolderNote = "removed empty legacy Download folder";
                return;
            }
            if (!newDir.isDirectory() && !newDir.mkdirs()) return;

            int moved = 0, kept = 0;
            for (File k : kids) {
                String name = k.getName();
                if (name == null || name.length() == 0 || name.startsWith(".")) continue;
                File target = new File(newDir, name);
                if (target.exists()) { kept++; continue; }   // already at the new place
                if (k.renameTo(target)) moved++;
                else kept++;
            }
            if (kept == 0) {
                File[] rest = old.listFiles();
                if (rest != null) for (File r : rest) deleteTreeFs(r);
                File[] left = old.listFiles();
                if (left != null && left.length == 0) {
                    //noinspection ResultOfMethodCallIgnored
                    old.delete();
                }
            }
            Log.i(TAG, "Song folder migration out of Download: moved=" + moved + " kept=" + kept);
            visibleFolderNote = "migratedFromDownload=" + moved
                    + (kept > 0 ? " kept=" + kept : " legacyCleared");
        } catch (Throwable t) {
            Log.w(TAG, "Song folder migration failed: " + t);
        }
    }

    /**
     * v1.11.7: the old visible folder sat inside Download, so the media library had indexed
     * its files. The move removes the files, but a stale row can survive - handing the old
     * path to the scanner once drops whatever is left (at most once per process). Never throws.
     */
    private static void purgeLegacyDownloadFolder(Context ctx) {
        if (ctx == null || legacyDownloadPurged) return;
        legacyDownloadPurged = true;
        try {
            String dl = downloadPath();
            if (dl.length() == 0) return;
            MediaScannerConnection.scanFile(ctx.getApplicationContext(),
                    new String[]{ new File(dl, VISIBLE_FOLDER).getAbsolutePath() }, null, null);
        } catch (Throwable t) {
            Log.w(TAG, "Could not purge the legacy Download folder: " + t);
        }
    }

    /** Probe file used to prove that Download can really be written (created and removed). */
    public static final String PROBE_FILE = ".questsaber_probe";

    /**
     * True when the shared Download folder can really be listed/READ (v1.11).
     *
     * This is the signal the game uses for SCANNING songs, because reading them needs no
     * write access. Writing has its own probe ({@link #canWriteDownload}); conflating the two
     * (v1.9/v1.10) made the songs vanish on a build where Download is readable but not
     * writable. The raw "All files access" flag is deliberately NOT trusted here - on
     * Horizon OS it can disagree with what is really possible.
     */
    public static boolean canReadDownload() {
        try {
            String dl = downloadPath();
            if (dl.length() == 0) return false;
            File d = new File(dl);
            if (!d.isDirectory() || !d.canRead()) return false;
            // A directory that can really be read also lists its entries. This is the signal
            // the game needs for SCANNING songs - it must NOT require the ability to write,
            // otherwise a build where Download is readable but not writable would show no
            // songs at all (exactly the v1.10 "songs disappeared" defect).
            File[] list = d.listFiles();
            return list != null;
        } catch (Throwable t) {
            return false;
        }
    }

    /**
     * True when a probe file can really be created and deleted inside Download (v1.11).
     * Writing is only needed for the log location and for MOVING downloaded songs into the
     * visible folder - reading songs must not depend on it (see {@link #canReadDownload}).
     */
    public static boolean canWriteDownload() {
        try {
            String dl = downloadPath();
            if (dl.length() == 0) return false;
            File d = new File(dl);
            if (!d.isDirectory()) return false;
            File probe = new File(d, PROBE_FILE);
            if (probe.exists() && !probe.delete()) return false;
            if (!probe.createNewFile()) return false;
            //noinspection ResultOfMethodCallIgnored
            probe.delete();
            return true;
        } catch (Throwable t) {
            return false;
        }
    }

    /**
     * The app's own external media directory (Android/media/&lt;pkg&gt;). It needs NO
     * permission and it IS reachable with a file manager (CX) or over USB - unlike
     * Android/data. Used as the always-accessible fallback home for the debug log when
     * "All files access" is missing, so the log can never get stuck somewhere unreadable.
     */
    public static String mediaDir(Context ctx) {
        try {
            Context app = ctx != null ? ctx.getApplicationContext() : null;
            if (app == null) return "";
            File[] dirs = app.getExternalMediaDirs();
            if (dirs == null || dirs.length == 0 || dirs[0] == null) return "";
            File d = dirs[0];
            if (!d.isDirectory() && !d.mkdirs()) return "";
            return d.getAbsolutePath();
        } catch (Throwable t) {
            return "";
        }
    }

    /**
     * One-line diagnosis of the shared Download folder, written into the game log at start.
     * Shows the raw permission flag AND what is really possible, so a device can be judged
     * without guessing: e.g. "allFiles=false list=true write=false" means the permission
     * flag and the real capability disagree.
     */
    public static String storageStatus() {
        String dl = downloadPath();
        boolean allFiles = hasAllFilesAccess();
        boolean isDir = false, canRead = false, listOk = false, writeOk = false;
        int entries = -1;
        try {
            File d = new File(dl);
            isDir = d.isDirectory();
            canRead = d.canRead();
            File[] list = d.listFiles();
            listOk = list != null;
            entries = list == null ? -1 : list.length;
            File probe = new File(d, PROBE_FILE);
            if (probe.exists()) probe.delete();
            writeOk = probe.createNewFile();
            if (writeOk) probe.delete();
        } catch (Throwable t) {
            // all values keep their safe defaults
        }
        return "allFiles=" + allFiles + " dir=" + isDir + " canRead=" + canRead +
                " list=" + listOk + " entries=" + entries + " write=" + writeOk +
                " path=" + dl;
    }

    /** {@link #importFromDownload} on a worker thread (same contract as the other imports). */
    public static void importFromDownloadAsync(Context ctx, String destDir) {
        if (importRunning) return;
        importRunning = true;
        importResult = "";
        final Context app = ctx != null ? ctx.getApplicationContext() : null;
        final String dir = destDir;
        Thread worker = new Thread(new Runnable() {
            @Override
            public void run() {
                String r;
                try {
                    r = SafHelper.importFromDownload(app, dir);
                } catch (Throwable t) {
                    Log.e(TAG, "Download import failed: " + t);
                    r = "{\"ok\":false,\"error\":\"" + SafHelper.escape(String.valueOf(t)) + "\"}";
                }
                importResult = r;
                importRunning = false;
            }
        }, "QuestSaberDownloadImport");
        worker.setDaemon(true);
        worker.start();
    }

    /**
     * Imports the songs the player put straight into the shared Download folder (song
     * folders and .zip archives from song sites) into destDir - same rules as
     * {@link #importFromMediaInbox} (move: copy, then delete the source so Download stays
     * tidy). Needs all-files access.
     *
     * Never touched:
     *   * Download/QuestSaberSongs (our own visible mirror - consuming it would eat the
     *     player's songs on every scan),
     *   * the debug log and our own APKs (both are not zips and not song folders).
     */
    public static String importFromDownload(Context ctx, String destDir) {
        long started = System.currentTimeMillis();
        Stats st = new Stats();
        String error = "";

        File dl = new File(downloadPath());
        if (downloadPath().length() == 0 || !dl.isDirectory()) {
            return "{\"ok\":false,\"download\":\"\",\"error\":\"Download folder not readable\"}";
        }
        // v1.11: gate on the REAL capability, not on the raw flag. The flag can report true
        // while Download is not really readable (which silently swallowed every import), and
        // it can report false while Download is perfectly readable. If the folder can be
        // listed, the songs can be moved.
        File[] probeList = dl.listFiles();
        if (probeList == null) {
            return "{\"ok\":false,\"download\":\"\",\"error\":\"Download folder not readable\"}";
        }

        File out = new File(destDir);
        try {
            if (!out.isDirectory() && !out.mkdirs()) {
                return "{\"ok\":false,\"download\":\"" + escape(dl.getAbsolutePath())
                        + "\",\"error\":\"cannot create destination\"}";
            }
            File[] kids = dl.listFiles();
            if (kids == null) kids = new File[0];
            for (File k : kids) {
                String name = k.getName();
                if (name == null || name.length() == 0 || name.startsWith(".")) continue;
                if (name.equals(VISIBLE_FOLDER)) continue; // our own mirror - never consumed
                try {
                    if (k.isDirectory()) walkFs(k, out, st, 0);
                    else if (isZip(name)) importZipFs(k, out, st);
                    else st.skipped++; // e.g. APKs, the debug log, browser downloads
                } catch (Throwable t) {
                    st.errors++;
                    Log.w(TAG, "Download entry failed (" + name + "): " + t);
                }
            }
        } catch (Throwable t) {
            error = String.valueOf(t);
            Log.e(TAG, "Download import failed: " + t);
        }

        boolean ok = (st.songs > 0) && (error.length() == 0);
        return resultJson(st, ok, true, error,
                ",\"download\":\"" + escape(dl.getAbsolutePath()) + "\""
                        + ",\"ms\":" + (System.currentTimeMillis() - started));
    }

    /**
     * Copies a local file (e.g. the debug log) into the shared Download folder under a
     * given display name. Needs all-files access. Returns "ok" or a short error note -
     * never throws. Plain java.io, no SAF document APIs.
     */
    public static String exportFileToDownload(String sourcePath, String displayName) {
        try {
            // v1.11: no flag gate - just try the write. If Download is not really writable the
            // write fails and the caller falls back to the media folder.
            String dl = downloadPath();
            if (dl.length() == 0) return "no Download folder";
            if (displayName == null || displayName.length() == 0) return "no file name";
            File src = new File(sourcePath);
            if (!src.isFile()) return "source missing";

            File dir = new File(dl);
            if (!dir.isDirectory() && !dir.mkdirs()) return "Download not writable";

            // Write to a temp name first and rename, so a half-copied log can never be
            // mistaken for a complete one.
            File tmp = new File(dir, displayName + ".part");
            InputStream in = null;
            OutputStream os = null;
            try {
                in = new FileInputStream(src);
                os = new FileOutputStream(tmp);
                byte[] buf = new byte[64 * 1024];
                int n;
                while ((n = in.read(buf)) > 0) os.write(buf, 0, n);
            } finally {
                try { if (os != null) os.close(); } catch (Throwable ignored) { }
                try { if (in != null) in.close(); } catch (Throwable ignored) { }
            }
            File dst = new File(dir, displayName);
            if (dst.exists() && !dst.delete()) return "cannot replace old copy";
            if (!tmp.renameTo(dst)) return "cannot rename into place";
            return "ok";
        } catch (Throwable t) {
            return String.valueOf(t);
        }
    }

    // ------------------------------------------------------------------ import

    /**
     * Walks the picked folder and copies every song into destDir; afterwards the
     * source (song folder or .zip) is deleted when the copy worked (= move).
     * Then the imported songs are mirrored into the visible sub folder
     * "QuestSaberSongs" inside the picked folder, so the player finds them there.
     * Returns a one line JSON status and never throws.
     *
     * The destination is NOT wiped: files that are already there with the same size
     * are skipped, so repeated scans are cheap and nothing ever gets lost when a
     * folder becomes temporarily unreadable.
     */
    public static String copySongs(Context ctx, String destDir) {
        long started = System.currentTimeMillis();
        Stats st = new Stats();
        String error = "";
        boolean writeGrant = hasWriteGrant(ctx);

        String uri = getTreeUri(ctx);
        if (uri == null || uri.length() == 0) {
            return "{\"ok\":false,\"error\":\"no song folder chosen\"}";
        }

        File out = new File(destDir);
        try {
            if (!out.isDirectory() && !out.mkdirs()) {
                return "{\"ok\":false,\"error\":\"cannot create " + escape(destDir) + "\"}";
            }
            Uri tree = Uri.parse(uri);
            String rootId = DocumentsContract.getTreeDocumentId(tree);

            // The visible song folder inside the picked folder: Download/QuestSaberSongs.
            // Created already here so it exists from the first run on. It MUST be skipped
            // by the walk below - otherwise the app would "import" its own copy again and
            // again (and delete it from under the player).
            String visibleId = "";
            if (writeGrant) {
                visibleId = findOrCreateDir(ctx, tree, rootId, VISIBLE_FOLDER);
                if (visibleId == null || visibleId.length() == 0) st.mirrorErrors++;
            }

            walk(ctx, tree, rootId, out, st, 0, visibleId);

            // Keep the visible folder in sync with the imported songs (folder for folder,
            // file for file; files of the same size are skipped).
            if (writeGrant && visibleId != null && visibleId.length() > 0) {
                mirrorTree(ctx, tree, visibleId, out, st, 0);
            }
        } catch (Throwable t) {
            error = String.valueOf(t);
            Log.e(TAG, "Song import failed: " + t);
        }

        boolean ok = (st.songs > 0) && (error.length() == 0);
        return resultJson(st, ok, writeGrant, error,
                ",\"ms\":" + (System.currentTimeMillis() - started));
    }

    /**
     * One-line JSON status shared by both import paths (SAF folder + permission-free inbox).
     * The Unity side reads "ok" and "write" from it.
     */
    private static String resultJson(Stats st, boolean ok, boolean write, String error, String extra) {
        return "{\"ok\":" + ok
                + ",\"songs\":" + st.songs
                + ",\"files\":" + st.files
                + ",\"zips\":" + st.zips
                + ",\"skipped\":" + st.skipped
                + ",\"moved\":" + st.moved
                + ",\"errors\":" + st.errors
                + ",\"bytes\":" + st.bytes
                + ",\"mirror\":" + st.mirrorFiles
                + ",\"mirrorSkipped\":" + st.mirrorSkipped
                + ",\"mirrorErrors\":" + st.mirrorErrors
                + ",\"write\":" + write
                + ",\"visible\":\"" + VISIBLE_FOLDER + "\""
                + (extra == null ? "" : extra)
                + ",\"error\":\"" + escape(error) + "\"}";
    }

    /**
     * Recurses through the tree. A directory that contains an info file is a song and
     * is copied + removed; a directory that contains .zip files is searched for
     * archives; everything else is only descended into (collection folders).
     * <p>{@code skipDocId} is the visible "QuestSaberSongs" folder - it belongs to the app
     * and must never be treated as a download.
     */
    private static void walk(Context ctx, Uri tree, String docId, File dest, Stats st, int depth, String skipDocId) {
        if (depth > MAX_DEPTH) return;
        List<Child> kids = listChildren(ctx, tree, docId);
        if (kids == null) {
            st.errors++;
            return;
        }

        boolean isSong = false;
        for (Child c : kids) {
            if (!c.dir && isInfoFile(c.name)) {
                isSong = true;
                break;
            }
        }

        if (isSong) {
            // A song folder: copy it (one level under its own name) and then remove it.
            File songDir = new File(dest, safe(displayNameOf(ctx, tree, docId)));
            if (copyDir(ctx, tree, docId, songDir, st, depth)) {
                st.songs++;
                if (deleteDocument(ctx, tree, docId)) st.moved++;
            }
            return;
        }

        for (Child c : kids) {
            if (c.dir) {
                if (skipDocId != null && skipDocId.length() > 0 && skipDocId.equals(c.docId)) continue;
                walk(ctx, tree, c.docId, dest, st, depth + 1, skipDocId);
            } else if (isZip(c.name)) {
                importZip(ctx, tree, c, dest, st);
            }
        }
    }

    // ------------------------------------------------------------------ visible copy

    /**
     * Mirrors the app-owned song folder into the visible folder inside the picked folder
     * (Download/QuestSaberSongs). Purely a copy for the player - the game itself always
     * plays from the app-owned folder, so a failure here can never break playback.
     * Never throws.
     */
    private static void mirrorTree(Context ctx, Uri tree, String parentDocId, File src, Stats st, int depth) {
        if (depth > MAX_DEPTH + 2) return;
        File[] kids;
        try {
            kids = src.listFiles();
        } catch (Throwable t) {
            st.mirrorErrors++;
            return;
        }
        if (kids == null) return;

        for (File f : kids) {
            String name = f.getName();
            if (name == null || name.length() == 0 || name.startsWith(".")) continue; // staging leftovers
            boolean dir;
            try {
                dir = f.isDirectory();
            } catch (Throwable t) {
                continue;
            }
            if (!dir) {
                mirrorFile(ctx, tree, parentDocId, f, st);
                continue;
            }
            String dirId = findOrCreateDir(ctx, tree, parentDocId, name);
            if (dirId == null || dirId.length() == 0) {
                st.mirrorErrors++;
                continue;
            }
            mirrorTree(ctx, tree, dirId, f, st, depth + 1);
        }
    }

    /** Copies one file into the visible folder, skipping it when it is already there. */
    private static void mirrorFile(Context ctx, Uri tree, String parentDocId, File f, Stats st) {
        try {
            long size = f.length();
            if (size > MAX_FILE_BYTES) {
                st.mirrorSkipped++;
                return;
            }

            Child existing = null;
            List<Child> kids = listChildren(ctx, tree, parentDocId);
            if (kids != null) {
                for (Child c : kids) {
                    if (!c.dir && f.getName().equalsIgnoreCase(c.name)) {
                        existing = c;
                        break;
                    }
                }
            }
            // Same size -> already there. Unknown size (-1/0, provider dependent) is treated
            // as "there" as well: re-uploading every song on every start would be far worse
            // than a stale copy.
            if (existing != null && (existing.size <= 0 || existing.size == size)) {
                st.mirrorSkipped++;
                return;
            }

            Uri target;
            if (existing != null) {
                target = DocumentsContract.buildDocumentUriUsingTree(tree, existing.docId);
            } else {
                Uri parent = DocumentsContract.buildDocumentUriUsingTree(tree, parentDocId);
                target = DocumentsContract.createDocument(ctx.getContentResolver(), parent,
                        mimeFor(f.getName()), f.getName());
                if (target == null) {
                    st.mirrorErrors++;
                    return;
                }
            }

            InputStream in = null;
            OutputStream os = null;
            try {
                in = new java.io.FileInputStream(f);
                os = ctx.getContentResolver().openOutputStream(target, "wt");
                if (os == null) {
                    st.mirrorErrors++;
                    return;
                }
                byte[] buf = new byte[64 * 1024];
                int n;
                while ((n = in.read(buf)) > 0) os.write(buf, 0, n);
            } finally {
                try { if (os != null) os.close(); } catch (Throwable ignored) { }
                try { if (in != null) in.close(); } catch (Throwable ignored) { }
            }
            st.mirrorFiles++;
        } catch (Throwable t) {
            st.mirrorErrors++;
            Log.w(TAG, "Could not copy into the visible folder (" + f.getName() + "): " + t);
        }
    }

    /** Finds a sub folder by name or creates it. Returns the document id or "". */
    private static String findOrCreateDir(Context ctx, Uri tree, String parentDocId, String name) {
        try {
            List<Child> kids = listChildren(ctx, tree, parentDocId);
            if (kids != null) {
                for (Child c : kids) {
                    if (c.dir && name.equalsIgnoreCase(c.name)) return c.docId;
                }
            }
            Uri parent = DocumentsContract.buildDocumentUriUsingTree(tree, parentDocId);
            Uri created = DocumentsContract.createDocument(ctx.getContentResolver(), parent,
                    DocumentsContract.Document.MIME_TYPE_DIR, name);
            return created == null ? "" : DocumentsContract.getDocumentId(created);
        } catch (Throwable t) {
            Log.w(TAG, "Could not create folder '" + name + "': " + t);
            return "";
        }
    }

    /** Copies one song folder (all files + sub folders). Returns true when it worked. */
    private static boolean copyDir(Context ctx, Uri tree, String docId, File dest, Stats st, int depth) {
        if (depth > MAX_DEPTH + 2) return false;
        if (!dest.isDirectory() && !dest.mkdirs()) {
            st.errors++;
            return false;
        }
        List<Child> kids = listChildren(ctx, tree, docId);
        if (kids == null) {
            st.errors++;
            return false;
        }
        boolean ok = true;
        for (Child c : kids) {
            if (c.dir) {
                if (!copyDir(ctx, tree, c.docId, new File(dest, safe(c.name)), st, depth + 1)) ok = false;
            } else if (!copyFile(ctx, tree, c, new File(dest, safe(c.name)), st)) {
                ok = false;
            }
        }
        return ok;
    }

    private static boolean copyFile(Context ctx, Uri tree, Child c, File dest, Stats st) {
        if (c.size > MAX_FILE_BYTES) {
            st.skipped++;
            Log.w(TAG, "Skipping large file: " + c.name);
            return true; // deliberately not a failure
        }
        if (dest.isFile() && c.size > 0 && dest.length() == c.size) {
            st.skipped++; // already imported (same size)
            return true;
        }
        InputStream in = null;
        OutputStream os = null;
        try {
            Uri docUri = DocumentsContract.buildDocumentUriUsingTree(tree, c.docId);
            in = ctx.getContentResolver().openInputStream(docUri);
            if (in == null) throw new IllegalStateException("no stream for " + c.name);
            os = new FileOutputStream(dest);
            byte[] buf = new byte[64 * 1024];
            int n;
            while ((n = in.read(buf)) > 0) {
                os.write(buf, 0, n);
                st.bytes += n;
            }
            st.files++;
            return true;
        } catch (Throwable t) {
            st.errors++;
            Log.w(TAG, "Copy failed (" + c.name + "): " + t);
            return false;
        } finally {
            try { if (os != null) os.close(); } catch (Throwable ignored) { }
            try { if (in != null) in.close(); } catch (Throwable ignored) { }
        }
    }

    /**
     * Unpacks a downloaded .zip. Only archives that really contain a song (info.dat /
     * info.json) are published - any other archive is left untouched, so no unrelated
     * zip in the folder is ever moved or deleted.
     */
    private static void importZip(Context ctx, Uri tree, Child c, File dest, Stats st) {
        String base = c.name;
        int dot = base.toLowerCase(Locale.US).lastIndexOf(".zip");
        if (dot > 0) base = base.substring(0, dot);
        base = safe(base);

        File staging = new File(dest, ".unpack_" + base);
        File target = new File(dest, base);
        deleteRecursive(staging);

        boolean song = false;
        InputStream in = null;
        try {
            Uri docUri = DocumentsContract.buildDocumentUriUsingTree(tree, c.docId);
            in = ctx.getContentResolver().openInputStream(docUri);
            if (in == null) throw new IllegalStateException("no stream for " + c.name);
            song = extractZip(in, staging, st);
        } catch (Throwable t) {
            st.errors++;
            Log.w(TAG, "Unzip failed (" + c.name + "): " + t);
        } finally {
            try { if (in != null) in.close(); } catch (Throwable ignored) { }
        }

        if (!song) {
            deleteRecursive(staging);
            st.skipped++;
            Log.i(TAG, "Zip without a song ignored: " + c.name);
            return;
        }

        deleteRecursive(target);
        if (!staging.renameTo(target)) {
            st.errors++;
            Log.w(TAG, "Could not publish unzipped song: " + base);
            deleteRecursive(staging);
            return;
        }
        st.zips++;
        st.songs++;
        if (deleteDocument(ctx, tree, c.docId)) st.moved++;
    }

    /** Extracts a zip; returns true when it contains an info file (i.e. is a song). */
    private static boolean extractZip(InputStream raw, File dest, Stats st) {
        boolean song = false;
        long total = 0;
        ZipInputStream zis = new ZipInputStream(new BufferedInputStream(raw));
        try {
            ZipEntry e;
            while ((e = zis.getNextEntry()) != null) {
                String name = sanitizeEntry(e.getName());
                if (name == null || name.length() == 0) continue;
                File f = new File(dest, name);
                if (e.isDirectory()) {
                    f.mkdirs();
                    continue;
                }
                File parent = f.getParentFile();
                if (parent != null) parent.mkdirs();
                OutputStream os = new FileOutputStream(f);
                try {
                    byte[] buf = new byte[64 * 1024];
                    int n;
                    while ((n = zis.read(buf)) > 0) {
                        os.write(buf, 0, n);
                        total += n;
                        st.bytes += n;
                        if (total > MAX_ZIP_BYTES) throw new IllegalStateException("zip too large");
                    }
                } finally {
                    os.close();
                }
                st.files++;
                String leaf = new File(name).getName();
                if (isInfoFile(leaf)) song = true;
            }
        } catch (Throwable t) {
            Log.w(TAG, "Unzip aborted: " + t);
            return false;
        } finally {
            try { zis.close(); } catch (Throwable ignored) { }
        }
        return song;
    }

    /** Deletes a document (folder or file) from the picked tree. true = gone. */
    private static boolean deleteDocument(Context ctx, Uri tree, String docId) {
        try {
            Uri docUri = DocumentsContract.buildDocumentUriUsingTree(tree, docId);
            return DocumentsContract.deleteDocument(ctx.getContentResolver(), docUri);
        } catch (Throwable t) {
            Log.w(TAG, "Could not move (delete) source: " + t);
            return false;
        }
    }

    // ------------------------------------------------------------------ listing

    private static final class Child {
        final String docId;
        final String name;
        final boolean dir;
        final long size;

        Child(String docId, String name, boolean dir, long size) {
            this.docId = docId;
            this.name = name;
            this.dir = dir;
            this.size = size;
        }
    }

    private static List<Child> listChildren(Context ctx, Uri tree, String docId) {
        List<Child> out = new ArrayList<>();
        Uri childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(tree, docId);
        Cursor c = null;
        try {
            c = ctx.getContentResolver().query(childrenUri, new String[] {
                    DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                    DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                    DocumentsContract.Document.COLUMN_MIME_TYPE,
                    DocumentsContract.Document.COLUMN_SIZE }, null, null, null);
            if (c == null) return null;
            while (c.moveToNext()) {
                String id = c.getString(0);
                String name = c.getString(1);
                String mime = c.getString(2);
                long size = c.isNull(3) ? -1L : c.getLong(3);
                out.add(new Child(id, name == null ? "" : name,
                        DocumentsContract.Document.MIME_TYPE_DIR.equals(mime), size));
            }
        } catch (Throwable t) {
            Log.w(TAG, "Cannot list folder: " + t);
            return null;
        } finally {
            if (c != null) c.close();
        }
        return out;
    }

    private static String displayNameOf(Context ctx, Uri tree, String docId) {
        try {
            Uri uri = DocumentsContract.buildDocumentUriUsingTree(tree, docId);
            Cursor c = ctx.getContentResolver().query(uri,
                    new String[] { DocumentsContract.Document.COLUMN_DISPLAY_NAME },
                    null, null, null);
            if (c != null) {
                try {
                    if (c.moveToFirst()) {
                        String n = c.getString(0);
                        if (n != null && n.length() > 0) return n;
                    }
                } finally {
                    c.close();
                }
            }
        } catch (Throwable t) {
            Log.w(TAG, "display name failed: " + t);
        }
        // Fallback: last segment of the document id.
        String id = docId == null ? "" : docId;
        int slash = id.lastIndexOf('/');
        int colon = id.lastIndexOf(':');
        int cut = Math.max(slash, colon);
        return cut >= 0 && cut + 1 < id.length() ? id.substring(cut + 1) : id;
    }

    // ------------------------------------------------------------------ helpers

    private static final class Stats {
        int songs;
        int files;
        int zips;
        int skipped;
        int moved;
        int errors;
        long bytes;
        // Visible copy (Download/QuestSaberSongs)
        int mirrorFiles;
        int mirrorSkipped;
        int mirrorErrors;
    }

    /** MIME type from the file extension (keeps SAF from renaming files). */
    private static String mimeFor(String name) {
        try {
            int dot = name == null ? -1 : name.lastIndexOf('.');
            if (dot >= 0 && dot + 1 < name.length()) {
                String m = android.webkit.MimeTypeMap.getSingleton()
                        .getMimeTypeFromExtension(name.substring(dot + 1).toLowerCase(Locale.US));
                if (m != null && m.length() > 0) return m;
            }
        } catch (Throwable ignored) { }
        return "application/octet-stream";
    }

    private static boolean isInfoFile(String name) {
        return name != null
                && ("info.dat".equalsIgnoreCase(name) || "info.json".equalsIgnoreCase(name));
    }

    private static boolean isZip(String name) {
        return name != null && name.toLowerCase(Locale.US).endsWith(".zip");
    }

    /** Makes a zip entry name safe (no absolute paths, no "..", no backslashes). */
    private static String sanitizeEntry(String raw) {
        if (raw == null) return null;
        String s = raw.replace('\\', '/');
        while (s.startsWith("/")) s = s.substring(1);
        if (s.length() == 0) return null;
        if (s.contains("..")) return null;
        if (s.length() > 1 && s.charAt(1) == ':') return null; // drive letter
        return s;
    }

    private static String safe(String name) {
        if (name == null) return "unnamed";
        String s = name.replace('/', '_').replace('\\', '_').replace("..", "_");
        if (s.length() == 0 || ".".equals(s)) return "unnamed";
        return s;
    }

    private static void deleteRecursive(File f) {
        if (f == null || !f.exists()) return;
        if (f.isDirectory()) {
            File[] kids = f.listFiles();
            if (kids != null) for (File k : kids) deleteRecursive(k);
        }
        if (!f.delete()) Log.w(TAG, "Could not delete: " + f.getAbsolutePath());
    }

    private static String escape(String s) {
        if (s == null) return "";
        return s.replace("\\", "/").replace("\"", "'").replace("\n", " ").replace("\r", " ");
    }
}

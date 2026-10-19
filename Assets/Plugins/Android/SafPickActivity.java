package com.questsaber;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.storage.StorageManager;
import android.os.storage.StorageVolume;
import android.provider.DocumentsContract;
import android.util.Log;

/**
 * QuestSaber - song folder picker.
 *
 * Shows the Android system folder chooser (Storage Access Framework,
 * ACTION_OPEN_DOCUMENT_TREE), remembers the chosen folder with a persistent
 * read grant and then finishes.
 *
 * Why an own activity: Unity's player activity cannot receive the result of the
 * system chooser, so this class owns the "start + onActivityResult" pair. The
 * Unity side only starts it and later reads the granted folder from the shared
 * preferences below (see SafHelper / SafSongFolder.cs).
 *
 * No storage permission is involved - the user's confirmation in the system
 * dialog IS the grant. Everything stays offline.
 *
 * IMPORTANT (learned the hard way on the Quest): some Android builds / form factors
 * ship NO document picker at all, and then the launch throws
 * ActivityNotFoundException - which used to be swallowed by a bare finish(), so the
 * game simply "did nothing" (no dialog, no result, no log). Three things changed:
 *   1. the chooser is built with StorageManager.getPrimaryStorageVolume()
 *      .createOpenDocumentTreeIntent() first (the variant that is implemented more
 *      widely), falling back to the plain action,
 *   2. the intent is checked with resolveActivity BEFORE launching,
 *   3. every step is written to SafHelper's pick log, which the game copies into its
 *      debug log, and the "closed without a result" case is reported to the game so it
 *      does not wait 300 s for a pick that can never come.
 */
public class SafPickActivity extends Activity {

    private static final String TAG = "QuestSaber";
    private static final int REQUEST_TREE = 4711;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Intent intent = null;
        String how = "";
        try {
            // API 29+: the storage volume's own chooser intent. This is what the Android
            // documentation recommends, and it is the variant that actually exists on
            // several builds where the bare action has no handler.
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                StorageManager sm = (StorageManager) getSystemService(Context.STORAGE_SERVICE);
                if (sm != null) {
                    StorageVolume vol = sm.getPrimaryStorageVolume();
                    if (vol != null) {
                        intent = vol.createOpenDocumentTreeIntent();
                        how = "volume";
                    }
                }
            }
        } catch (Throwable t) {
            SafHelper.notePick(this, "volume chooser intent failed: " + t);
        }
        if (intent == null) {
            intent = new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE);
            how = "action";
        }
        intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION
                | Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
        // Let the user navigate to the real folder tree (some builds hide it).
        intent.putExtra("android.content.extra.SHOW_ADVANCED", true);

        // Never launch blind: without a handler this throws, and before that looked exactly
        // like "the app does nothing when it asks for the folder".
        try {
            if (getPackageManager().resolveActivity(intent, 0) == null) {
                SafHelper.notePick(this, "no app handles the folder chooser (" + how + ") - trying the plain action");
                Intent bare = new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE);
                bare.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION
                        | Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                        | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
                bare.putExtra("android.content.extra.SHOW_ADVANCED", true);
                if (getPackageManager().resolveActivity(bare, 0) != null) {
                    intent = bare;
                    how = "action-fallback";
                } else {
                    SafHelper.notePick(this,
                            "NO folder chooser on this device at all - songs must come in via "
                            + "Android/media/com.questsaber.questsaber/Songs (see the song hint file)");
                    SafHelper.markPickClosed(this);
                    finish();
                    return;
                }
            }
        } catch (Throwable t) {
            SafHelper.notePick(this, "resolveActivity failed: " + t);
        }

        try {
            startActivityForResult(intent, REQUEST_TREE);
            SafHelper.notePick(this, "chooser started (" + how + ", action=" + intent.getAction() + ")");
        } catch (Throwable t) {
            // No file picker on this device/build: report it (instead of a silent finish) and
            // let the Unity side stop waiting immediately.
            Log.e(TAG, "Song folder chooser could not be started: " + t);
            SafHelper.notePick(this, "chooser launch FAILED (" + how + "): " + t);
            SafHelper.markPickClosed(this);
            finish();
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        SafHelper.notePick(this, "chooser returned: request=" + requestCode + " result=" + resultCode
                + " data=" + (data == null ? "null" : String.valueOf(data.getData()))
                + " flags=" + (data == null ? -1 : data.getFlags()));
        if (requestCode == REQUEST_TREE && resultCode == RESULT_OK && data != null && data.getData() != null) {
            Uri tree = data.getData();
            // Take EXACTLY the flags the chooser granted us. Asking for READ|WRITE when the
            // provider only granted READ makes takePersistableUriPermission throw, and then
            // NOTHING is persisted - which is why the app asked for the folder on every
            // start. Prefer READ+WRITE (needed to MOVE songs out and delete the .zip), fall
            // back to READ only.
            int granted = data.getFlags()
                    & (Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
            boolean persisted = false;
            if ((granted & Intent.FLAG_GRANT_READ_URI_PERMISSION) != 0) {
                try {
                    getContentResolver().takePersistableUriPermission(tree, granted);
                    persisted = true;
                } catch (Throwable t) {
                    Log.w(TAG, "Persisting read+write grant failed (" + granted + "): " + t);
                    try {
                        getContentResolver().takePersistableUriPermission(
                                tree, Intent.FLAG_GRANT_READ_URI_PERMISSION);
                        persisted = true;
                        Log.i(TAG, "Falling back to a read-only folder grant.");
                    } catch (Throwable t2) {
                        Log.w(TAG, "Could not persist any folder grant: " + t2);
                    }
                }
            } else {
                // Some builds deliver no flags at all - try the full set as a last resort.
                try {
                    getContentResolver().takePersistableUriPermission(tree,
                            Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
                    persisted = true;
                } catch (Throwable t) {
                    Log.w(TAG, "No URI permission flags and persisting failed: " + t);
                }
            }
            Log.i(TAG, "Folder grant persisted=" + persisted + " flags=" + granted);
            SafHelper.notePick(this, "folder grant persisted=" + persisted + " flags=" + granted);
            String name = "";
            try {
                // e.g. "primary:Download" -> "Download"
                name = DocumentsContract.getTreeDocumentId(tree);
                int colon = name.indexOf(':');
                if (colon >= 0) name = name.substring(colon + 1);
            } catch (Throwable t) {
                name = tree.toString();
            }
            String now = String.valueOf(System.currentTimeMillis());
            getSharedPreferences(SafHelper.PREFS, MODE_PRIVATE).edit()
                    .putString(SafHelper.KEY_URI, tree.toString())
                    .putString(SafHelper.KEY_NAME, name)
                    .putString(SafHelper.KEY_SEQ, now)
                    // Marks a REAL pick (not a cancel) - the game re-imports even when the
                    // player picked the same folder again to force a check.
                    .putString(SafHelper.KEY_PICKED, now)
                    .apply();
            Log.i(TAG, "Song folder chosen: " + name);
            SafHelper.notePick(this, "song folder chosen: " + name + " (" + tree + ")");
        } else {
            // Tell the Unity side that the chooser is closed (so it does not wait for a
            // pick that will never come).
            getSharedPreferences(SafHelper.PREFS, MODE_PRIVATE).edit()
                    .putString(SafHelper.KEY_SEQ, String.valueOf(System.currentTimeMillis()))
                    .apply();
            Log.i(TAG, "Song folder chooser cancelled.");
            SafHelper.notePick(this, "song folder chooser cancelled (no folder granted)");
        }
        super.onActivityResult(requestCode, resultCode, data);
        finish();
    }
}

# QuestSaber – Mixed Reality Beat Saber for Meta Quest 3 (+ VR Mode)

**English** | [Deutsch](README.de.md)

<a href="https://youtu.be/XaOJp0y6dqE" target="_blank">
  <img width="480" height="270" alt="QuestSaber" src="https://github.com/user-attachments/assets/6f68cf4b-d548-4920-9054-88f4d70f5e92" />
</a>

https://youtu.be/XaOJp0y6dqE

## Installation

*Note: No developer mode, no online connection, no SideQuest needed.*

1. Download the APK in the headset's browser, or copy it into the headset's **Download** folder: <https://github.com/Riegelknight/QuestSaber/releases/latest>
2. In **Download** tap the three dots (...) next to `QuestSaber-apk1.apk` → *Open with* → **Package installer**.
3. The game will now appear in your library under **Unknown Sources**.

Demo song: "Death to Disco" by Freeek

### Adding Songs

1. Download a song in the headset's browser, or copy it into the **Download** folder: <https://beatsaver.com>.
2. Start QuestSaber and accept the permission request to access folders.

Wow, that's it?! Yes, songs are automatically moved to `storage/QuestSaberSongs` and unpacked (if necessary).

<img width="1920" height="1080" alt="arc" src="https://github.com/user-attachments/assets/84e6dc49-0374-4d8b-abbf-31d215a75a08" />
<img width="1920" height="1080" alt="combo" src="https://github.com/user-attachments/assets/89b5700c-bd06-46f1-b1ec-c8c0eb4d737e" />

<img width="1920" height="1080" alt="lied auswahl" src="https://github.com/user-attachments/assets/9cd20cd8-0f4f-4382-865e-ecdee77781a8" />

### Keeping the headset offline (tip)

To move APKs, songs and logs between PC and headset without developer mode and without a store,
install the **CX File Explorer** once:

1. Open <https://cxfileexplorer.com/> in the headset's browser and download the APK.
2. In **Downloads** (via the menu with the small dots), open the package installer and tap
   **Install**.
3. Then connect to the PC – files can be copied in both directions.

The headset may stay offline the whole time; no online requirement, account login or SideQuest is
needed for installing or transferring songs.

## Controller Mapping

| Input | Action |
|---|---|
| Y / B | Open menu (+ Pause) / Close menu |
| X | Next song |
| A | Recenter room |
| Trigger (Right/Left) | Menu confirmation when pointing at it |
| Joystick (Both) | Scroll song list up/down by one entry |
| Left Joystick Up/Down | Raise/Lower play area |
| Right Joystick Left/Right | Rotate room |
| Y / B ≥ 2 s | Exit game |

<img width="1920" height="1080" alt="menü" src="https://github.com/user-attachments/assets/b4a5d3b9-cd0b-44fb-95cd-33b837637bef" />
<img width="1920" height="1080" alt="spieler rechts" src="https://github.com/user-attachments/assets/4615e169-afcb-4f81-8c5a-650e62aa898c" />
<img width="1920" height="1080" alt="spielerlinks" src="https://github.com/user-attachments/assets/c7a4d073-02e3-4f0a-b589-3708fd0de153" />

## Build Information

1. **Unity 6000.6.5f1** (Unity 6.6) with Android Build Support – Unity's **Mixed Reality** template (`com.unity.template.mixedreality`) — <https://unity.com/releases/editor/archive>
2. `Packages/manifest.json` → Check the **Meta XR SDK** version (`com.meta.xr.sdk.all`, `207.0.0`)
3. Bundle `apk1` (code `3`), package id `com.questsaber.questsaber`, minSdk/targetSdk 34, ARM64, IL2CPP
4. Build: menu **`QuestSaber > Build Android APK`** → `Builds\QuestSaber-<version>.apk` (older APKs move to `Builds\archive\`). The build aborts before compiling if an environment scene is missing from the build settings, or if Meta XR's *Project Validation* has silently rewritten the critical player settings — **never press "Fix All" / "Apply All" there**.

### Code Notes

* **Frame rate (120 Hz):** an update routine holds Unity's target frame rate in place (repaired every
  0.25 s), so the VR subsystems cannot throttle it to `-1` (a 30 fps lock) on their own; the panel
  rate is written through `OVRPlugin.systemDisplayFrequency` only when it really differs (no
  permanent writes, which would make the compositor reconfigure on every focus change) and rounded
  to the native hardware rates (72/80/90/120 Hz). `QualitySettings.vSyncCount` is `0`.
* **Anti-telemetry:** blocks automatic uploads and LAN injection by the Meta build tools by disabling
  `DevAgentSettings.asset` and `ImmersiveDebuggerSettings.asset` in code, and removes the trackers
  during the manifest merge:
  ```xml
  <meta-data android:name="com.oculus.telemetry.project_guid" tools:node="remove" />
  <uses-permission android:name="android.permission.RECORD_AUDIO" tools:node="remove" />
  ```
* **Assembly stripping:** uses `IFilterBuildAssemblies` to drop unused, network-capable DLLs
  (`Unity.AI.MCP.Runtime`, `Unity.AI.Tracing`) from the player.
* **Storage access:** instead of the Android folder chooser (Storage Access Framework, which this
  system never shows) the app uses **"All files" (`MANAGE_EXTERNAL_STORAGE`)**, so it can read the
  songs straight from the shared Download folder and create it itself; without the grant the
  permission-free app media folder (`Android/media`) remains.
* **System keyboard (deviation from Meta's store rule):** with Unity 6's *GameActivity* entry point
  the headset's keyboard overlay cannot be shown at all (`TouchScreenKeyboard` reports `Visible`
  forever while Horizon OS draws nothing). That is why the classic *Activity* entry point is wired
  in and enforced by `BuildAndroid`.
* **Debug log:** the game writes `spiel_debug_log.txt` into the headset's **Download** folder; on the
  PC `Tools/FetchLogFromQuest.ps1` pulls it over MTP – no ADB and no developer mode.

## Origins & Credits

| Source | Adopted |
|---|---|
| [AllPoland/ArcViewer](https://github.com/AllPoland/ArcViewer) | Environment scenes + shaders, level grid, arcs/chains, score utils |
| [s4k10503/BeatSaberClone](https://github.com/s4k10503/BeatSaberClone) | Visual note slicing physics |
| [CoffeeAt0900/OpenSaberPlus](https://github.com/CoffeeAt0900/OpenSaberPlus) | Swing-angle measurement, scoring system |

**Other sources:** [SuperSaber](https://github.com/sidequestlegend/supersaber), [BeatSaber-Web-Replays](https://github.com/BeatLeader/BeatSaber-Web-Replays), [OpenSaberVR](https://github.com/dhcdht/OpenSaberVR)

This project was built using the open-source AI agent platform **OpenCode ([opencode.ai](https://opencode.ai/))**.

## License

**GPL-3.0** – see [`LICENSE`](LICENSE). Free to use, modify, and distribute; derivative works must also be licensed under GPL-3.0.

# QuestSaber – Mixed-Reality-Beat-Saber für Meta Quest 3 (+ VR-Modus)

[English](README.md) | **Deutsch**

<a href="https://youtu.be/XaOJp0y6dqE" target="_blank">
  <img width="480" height="270" alt="QuestSaber" src="https://github.com/user-attachments/assets/6f68cf4b-d548-4920-9054-88f4d70f5e92" />
</a>

https://youtu.be/XaOJp0y6dqE

## Installation

*Hinweis: Kein Entwicklermodus, keine Online-Verbindung, kein SideQuest nötig.*

1. Die APK im Browser der Brille herunterladen oder in den **Download**-Ordner der Brille kopieren: <https://github.com/Riegelknight/QuestSaber/releases/latest>
2. Im **Download**-Ordner auf die drei Punkte (...) neben `QuestSaber-apk1.apk` tippen → *Öffnen mit* → **Paketinstallation**.
3. Das Spiel erscheint nun in deiner Bibliothek unter **Unbekannte Quellen**.

Demo-Lied: „Death to Disco“ von Freeek

### Lieder hinzufügen

1. Ein Lied im Browser der Brille herunterladen oder in den **Download**-Ordner kopieren: <https://beatsaver.com>.
2. QuestSaber starten und die Berechtigungsanfrage für den Ordnerzugriff akzeptieren.

Wow, mehr nicht?! Ja – Lieder werden automatisch nach `storage/QuestSaberSongs` verschoben und (falls nötig) entpackt.

<img width="1920" height="1080" alt="arc" src="https://github.com/user-attachments/assets/84e6dc49-0374-4d8b-abbf-31d215a75a08" />
<img width="1920" height="1080" alt="combo" src="https://github.com/user-attachments/assets/89b5700c-bd06-46f1-b1ec-c8c0eb4d737e" />

<img width="1920" height="1080" alt="lied auswahl" src="https://github.com/user-attachments/assets/9cd20cd8-0f4f-4382-865e-ecdee77781a8" />

### Quest dauerhaft offline nutzen (Tipp)

Damit du APK, Songs und Logs bequem zwischen PC und Brille hin- und herschieben kannst, ohne
Entwicklermodus und ohne Store, lohnt sich einmalig der **CX File Explorer**:

1. Im Browser der Brille <https://cxfileexplorer.com/> öffnen und die APK herunterladen.
2. In **Downloads** (über das Menü mit den kleinen Punkten) die Paketinstallation öffnen und auf
   **Installieren** tippen.
3. Danach mit dem PC verbinden – Dateien lassen sich in beide Richtungen kopieren.

Die Brille darf dabei offline bleiben; Online-Zwang, Konto-Anmeldung oder SideQuest braucht es
für Installation und Song-Transfer nicht.

## Controller-Belegung

| Eingabe | Aktion |
|---|---|
| Y/B | Menü öffnen (+ Pause) / schließen |
| X | Nächster Song |
| A | Raum neu ausrichten |
| Trigger (rechts/links) | Menübestätigung beim Daraufzeigen |
| Joystick (beide) | Songliste einen Eintrag hoch-/runterblättern |
| Linker Joystick hoch/runter | Spielfeld anheben/absenken |
| Rechter Joystick links/rechts | Raum drehen |
| Y / B ≥ 2 s | Spiel beenden |

<img width="1920" height="1080" alt="menü" src="https://github.com/user-attachments/assets/b4a5d3b9-cd0b-44fb-95cd-33b837637bef" />
<img width="1920" height="1080" alt="spieler rechts" src="https://github.com/user-attachments/assets/4615e169-afcb-4f81-8c5a-650e62aa898c" />
<img width="1920" height="1080" alt="spielerlinks" src="https://github.com/user-attachments/assets/c7a4d073-02e3-4f0a-b589-3708fd0de153" />

## Build-Informationen

1. **Unity 6000.6.5f1** (Unity 6.6) mit Android Build Support – Unitys **Mixed-Reality**-Template (`com.unity.template.mixedreality`) — <https://unity.com/releases/editor/archive>
2. `Packages/manifest.json` → Version des **Meta XR SDK** prüfen (`com.meta.xr.sdk.all`, `207.0.0`)
3. Bundle `apk1` (Code `3`), Paket-ID `com.questsaber.questsaber`, Min-/Target-SDK 34, ARM64, IL2CPP
4. Bauen: Menü **`QuestSaber > Build Android APK`** → `Builds\QuestSaber-<Version>.apk` (ältere APKs wandern nach `Builds\archive\`). Der Build bricht ab, bevor er kompiliert, wenn eine Umgebungs-Szene in den Build Settings fehlt oder wenn Meta XRs *Project Validation* die kritischen Player-Settings still umgeschrieben hat – **dort niemals „Fix All“ / „Apply All“ drücken**.

### Code-Logiken

* **Framerate (120 Hz):** Eine Update-Routine hält die Unity-Zielbildrate fest (Reparatur alle 0,25 s),
  damit VR-Subsysteme sie nicht eigenmächtig auf `-1` (30-FPS-Lock) drosseln; die Panel-Rate wird
  über `OVRPlugin.systemDisplayFrequency` nur bei echter Abweichung gesetzt (kein Dauer-Schreiben –
  das ließe den Compositor bei jedem Fokuswechsel neu konfigurieren) und auf die nativen
  Hardware-Raten (72/80/90/120 Hz) gerundet. `QualitySettings.vSyncCount` ist `0`.
* **Anti-Telemetrie:** Blockiert automatische Datenübertragungen und LAN-Injektionen der
  Meta-Build-Tools durch Code-Deaktivierung von `DevAgentSettings.asset` und
  `ImmersiveDebuggerSettings.asset` und entfernt die Tracker beim Manifest-Merge:
  ```xml
  <meta-data android:name="com.oculus.telemetry.project_guid" tools:node="remove" />
  <uses-permission android:name="android.permission.RECORD_AUDIO" tools:node="remove" />
  ```
* **Assembly-Stripping:** Nutzt `IFilterBuildAssemblies`, um ungenutzte, netzwerkfähige DLLs
  (`Unity.AI.MCP.Runtime`, `Unity.AI.Tracing`) aus dem Player zu entfernen.
* **Speicherzugriff:** Statt des Android-Ordnerwählers (Storage Access Framework, den dieses System
  nie anzeigt) nutzt die App **„Alle Dateien“ (`MANAGE_EXTERNAL_STORAGE`)** und liest die Songs
  direkt aus dem geteilten Download-Ordner, den sie selbst anlegt; ohne die Freigabe bleibt der
  rechtefreie app-eigene Medienordner (`Android/media`).
* **System-Tastatur (Abweichung von der Meta-Store-Regel):** Mit Unity 6s *GameActivity*-Einstiegspunkt
  lässt sich die Tastatur-Overlay der Brille gar nicht anzeigen (`TouchScreenKeyboard` meldet dauerhaft
  `Visible`, während Horizon OS nichts zeichnet). Deshalb ist der klassische *Activity*-Einstiegspunkt
  fest verdrahtet und wird von `BuildAndroid` erzwungen.
* **Debug-Log:** Das Spiel schreibt `spiel_debug_log.txt` in den **Download**-Ordner der Brille; auf
  dem PC holt `Tools/FetchLogFromQuest.ps1` sie per MTP ab – ohne ADB und ohne Entwicklermodus.

## Herkunft & Credits

| Quelle | Übernommen |
|---|---|
| [AllPoland/ArcViewer](https://github.com/AllPoland/ArcViewer) | Umgebungs-Szenen + Shader, Level-Raster, Arcs/Chains, Score-Utils |
| [s4k10503/BeatSaberClone](https://github.com/s4k10503/BeatSaberClone) | Noten-Schnittphysik (Optik) |
| [CoffeeAt0900/OpenSaberPlus](https://github.com/CoffeeAt0900/OpenSaberPlus) | Winkel-Messung, Scoring-System |

**Weitere Quellen:** [SuperSaber](https://github.com/sidequestlegend/supersaber), [BeatSaber-Web-Replays](https://github.com/BeatLeader/BeatSaber-Web-Replays), [OpenSaberVR](https://github.com/dhcdht/OpenSaberVR)

Dieses Projekt wurde mit der Open-Source-KI-Agentenplattform **OpenCode ([opencode.ai](https://opencode.ai/))** realisiert.

## Lizenz

**GPL-3.0** – siehe [`LICENSE`](LICENSE). Frei verwendbar, änderbar, weitergebbar; abgeleitete Werke müssen ebenfalls unter der GPL-3.0 stehen.

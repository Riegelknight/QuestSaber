// GameManager: Rig, sabers, controller models, haptics.
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;

namespace QuestSaber
{
    public sealed partial class GameManager
    {
        // ======================================================================
        // Clean up template (coaching UI, spawner ball, etc.)
        // ======================================================================
        private void SleepTemplateUi()
        {
            try
            {
                // Disable demo/coaching ballast (GoalManager/Fade stays active!). The names
                // are matched including INACTIVE objects (see DisableObjects) and were verified
                // against the scene: "Hand Menu Setup MR Template Variant" and
                // "Asset Spawner Dropdown" are the real template object names.
                DisableObjects("UI", "Coaching UI", "Hand Menu Setup", "Hand Menu Setup MR Template Variant",
                    "Tutorial Player", "Object Spawner", "Asset Spawner", "Asset Spawner Dropdown");

                // Switch off disruptive template COMPONENTS - NOT their GameObjects!
                // (The "AR Session" object manages the XR session; deactivating the
                // object can end the session -> Quest then only shows the loading ring.)
                //  - HandVisualizer: draws hand joints; we play with controllers.
                //  - PermissionsCheck (requests com.oculus.permission.USE_SCENE, Meta):
                //    is not part of this project's assets.
                var handVisualizer = FindAnyObjectByType<UnityEngine.XR.Hands.Samples.VisualizerSample.HandVisualizer>(FindObjectsInactive.Include);
                if (handVisualizer != null) handVisualizer.enabled = false;
                //  - SpawnedObjectsManager (MR template persistent anchor manager)
                foreach (var som in FindObjectsByType<UnityEngine.XR.Templates.MR.SpawnedObjectsManager>(FindObjectsInactive.Include))
                    som.enabled = false;
                //  - JumpProvider (Starter Assets rig under "MR Interaction Setup"): the
                //    XRI action "Jump" is bound in "XRI Default Input Actions" to the
                //    PrimaryButton of the right hand = button A. The provider raises
                //    the XR origin by up to 1.25 m - the actual reason for the "jump
                //    upward" on A. A belongs solely to our RecenterRoomOnHead
                //    (room X/Z under the head + yaw into the look direction).
                foreach (var jump in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Locomotion.Jump.JumpProvider>(FindObjectsInactive.Include))
                    jump.enabled = false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[QuestSaber] Cleaning up template UI: " + ex.Message);
            }
        }

        /// <summary>
        /// Deactivates the first object with a matching name in the active scene. Unlike
        /// <see cref="GameObject.Find(string)"/> this also finds objects that are already
        /// INACTIVE. The listed template ballast objects are inactive in the scene, so this
        /// call is a safety net rather than an actual change of visibility.
        /// </summary>
        private static void DisableObjects(params string[] names)
        {
            foreach (var name in names)
            {
                var go = FindInActiveScene(name);
                if (go != null) go.SetActive(false);
            }
        }

        /// <summary>Finds a GameObject by exact name under any root of the active scene,
        /// including INACTIVE objects (reuses <see cref="FindDescendant"/>).</summary>
        private static GameObject FindInActiveScene(string name)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == name) return root;
                var hit = FindDescendant(root.transform, name);
                if (hit != null) return hit.gameObject;
            }
            return null;
        }

        // ======================================================================
        // Rig & Saber
        // ======================================================================
        private Transform FindRigAnchor(string name)
        {
            if (_cameraOffset != null)
            {
                var t = FindDescendant(_cameraOffset, name);
                if (t != null) return t;
            }
            var f = GameObject.Find(name);
            return f != null ? f.transform : null;
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            foreach (Transform child in root)
            {
                if (child.name == name) return child;
                var hit = FindDescendant(child, name);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>
        /// Find the anchor for a hand - regardless of the rig type:
        /// "Left Controller" (XRI starter), "LeftHand Controller" (MRTK 3, like the
        /// reference game scene) or "Left Hand" (XR Hands rig of the AR template).
        /// </summary>
        private Transform FindControllerAnchor(string side)
        {
            string[] candidates =
            {
                side + " Controller",      // XRI: "Left Controller" / "Right Controller"
                side + "Hand Controller",  // MRTK 3: "LeftHand Controller" / "RightHand Controller"
                side + " Hand"             // XR Hands template: "Left Hand" / "Right Hand"
            };
            foreach (var name in candidates)
            {
                var t = FindRigAnchor(name);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>Full hierarchy path of an anchor (diagnostic log at start).</summary>
        private static string AnchorPath(Transform t)
        {
            if (t == null) return "—";
            string path = t.name;
            for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        private Transform CreateXrPoseAnchor(string objectName, XRNode node)
        {
            var go = new GameObject(objectName);
            var anchor = go.transform;
            anchor.SetParent(_cameraOffset != null ? _cameraOffset : null, false);
            var follower = go.AddComponent<XrPoseAnchorFollower>();
            follower.Node = node;
            return anchor;
        }

        /// <summary>
        /// Diagnostic: proves WHERE the sabers are attached and whether the anchor
        /// really follows the physical controller. Written to the log a moment after start
        /// (the poses need a few frames). This settles the "virtual controllers are not where
        /// I hold them" report without guessing: a large delta between the anchor's LOCAL pose
        /// and the raw XR device pose means the WRONG anchor is used; a small delta means the
        /// anchor itself is correct and only the saber MOUNT offset needs tuning.
        /// </summary>
        private IEnumerator AnchorDiagnosticsRoutine()
        {
            yield return new WaitForSecondsRealtime(1.5f);
            LogAnchorDiagnostic("left", _leftAnchor, XRNode.LeftHand);
            LogAnchorDiagnostic("right", _rightAnchor, XRNode.RightHand);
            Debug.Log("[QuestSaber] Rig: cameraOffset=" +
                      (_cameraOffset != null
                          ? AnchorPath(_cameraOffset) + " local=" + _cameraOffset.localPosition.ToString("F3") +
                            " world=" + _cameraOffset.position.ToString("F3")
                          : "<none>") +
                      " | camera world=" +
                      (_cam != null ? _cam.transform.position.ToString("F3") : "<none>"));
        }

        private static void LogAnchorDiagnostic(string side, Transform anchor, XRNode node)
        {
            if (anchor == null) { Debug.LogWarning("[QuestSaber] Anchor " + side + ": <null>"); return; }

            bool hasDevice = false;
            Vector3 deviceLocal = Vector3.zero;
            var device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid && device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 dp))
            {
                deviceLocal = dp;
                hasDevice = true;
            }

            Vector3 local = anchor.localPosition;
            string delta = hasDevice ? (local - deviceLocal).magnitude.ToString("F4") : "n/a";
            Debug.Log("[QuestSaber] Anchor " + side +
                      ": path=" + AnchorPath(anchor) +
                      " local=" + local.ToString("F3") +
                      " world=" + anchor.position.ToString("F3") +
                      (hasDevice
                          ? " deviceLocal=" + deviceLocal.ToString("F3") + " delta=" + delta
                          : " (no valid device)"));
        }

        // ======================================================================
        // Hide controller models (Unity-side meshes; sabers/UI remain)
        // ======================================================================
        private static readonly string[] ProtectedModelNames =
            { "Saber", "Blade", "Handle", "Trail", "PointerLaser", "Label" };

        /// <summary>
        /// Hides all mesh/skinned mesh renderers under the controller anchors,
        /// except for the self-created objects (sabers, laser, labels).
        /// Note: In passthrough, Quest additionally draws controller models
        /// SYSTEM-side over the camera image - there is no app API for this.
        /// </summary>
        private void HideControllerModels()
        {
            if (_leftAnchor != null) HideRenderersUnder(_leftAnchor);
            if (_rightAnchor != null) HideRenderersUnder(_rightAnchor);
            DisableTemplatePointerRays();
        }

        private void DisableTemplatePointerRays()
        {
            // XRI 3.x: CurveVisualController controls the interactor's LineRenderer.
            var curves = FindObjectsByType<CurveVisualController>(FindObjectsInactive.Include);
            for (int i = 0; i < curves.Length; i++)
            {
                foreach (var lr in curves[i].GetComponentsInChildren<LineRenderer>(true))
                    lr.enabled = false;
                curves[i].enabled = false;
            }
            // Older XRI variant (if present in the rig): XRInteractorLineVisual.
            var lineVisuals = FindObjectsByType<XRInteractorLineVisual>(FindObjectsInactive.Include);
            for (int i = 0; i < lineVisuals.Length; i++)
            {
                foreach (var lr in lineVisuals[i].GetComponentsInChildren<LineRenderer>(true))
                    lr.enabled = false;
                lineVisuals[i].enabled = false;
            }
        }

        private static void HideRenderersUnder(Transform root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (IsOwnObject(r.transform)) continue;
                r.enabled = false;
            }
        }

        private static bool IsOwnObject(Transform t)
        {
            // NEVER hide the debug controller (tracking visual check/calibration) -
            // the hide routine runs several times in the first seconds.
            for (var p = t; p != null; p = p.parent)
                if (p.GetComponent<ControllerDebugView>() != null) return true;
            int depth = 0;
            while (t != null && depth++ < 5)
            {
                for (int i = 0; i < ProtectedModelNames.Length; i++)
                {
                    if (t.name.StartsWith(ProtectedModelNames[i], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Interactor models can be loaded in after start;
        /// therefore hide them again several times in the first seconds.
        /// </summary>
        private IEnumerator HideControllerModelsRoutine()
        {
            for (int i = 0; i < 7; i++)
            {
                yield return new WaitForSeconds(0.5f);
                HideControllerModels();
            }
        }

        /// <summary>
        /// Tracking precision. Makes every pose driver in the rig sample the tracked poses
        /// again right before rendering instead of only once per Update.
        ///
        /// The template rig's drivers default to "Update" (= sampled one whole frame before the
        /// pose is drawn), which is exactly the "the controllers trail/feel less precise than in
        /// the Horizon OS menu" report: the system menu draws its own controller models in a
        /// late-latched compositor layer, something an app-rendered model cannot do - but sampling
        /// in the before-render phase removes roughly one frame of that gap for the sabers, the
        /// hands and the virtual controllers alike. Note that the Meta late latching /
        /// symmetric projection switches (OpenXR Meta Quest feature) are already ON and are not
        /// touched here.
        ///
        /// All driver flavours of the template rigs are handled by reflection
        /// (XRI XRBaseController "updateTrackingType", Input System / SpatialTracking
        /// "updateType"), so this file keeps compiling whatever the rig uses; components without
        /// such a property are skipped silently. The count is logged, so a build that changed
        /// nothing says so instead of pretending.
        /// </summary>
        private void EnsureBeforeRenderPoseUpdates()
        {
            int changed = 0;
            int found = 0;
            foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
            {
                if (mb == null) continue;
                if (TrySetBeforeRenderSampling(mb, "updateTrackingType")) { found++; changed++; }
                else if (TrySetBeforeRenderSampling(mb, "updateType")) { found++; changed++; }
            }
            Debug.Log($"[QuestSaber] Pose sampling: {found} pose driver(s) found, {changed} moved to " +
                      "before-render (lowest latency available to an app-rendered controller).");
        }

        /// <summary>
        /// Sets the enum property <paramref name="property"/> to "UpdateAndBeforeRender"
        /// (falls back to "BeforeRender") if it exists. Returns true only when a value was
        /// actually changed; never throws - a driver that refuses the value is not worth a crash.
        /// </summary>
        private static bool TrySetBeforeRenderSampling(object target, string property)
        {
            if (target == null) return false;
            try
            {
                var type = target.GetType();
                var p = type.GetProperty(property,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p == null || !p.CanRead || !p.CanWrite || !p.PropertyType.IsEnum) return false;

                var names = Enum.GetNames(p.PropertyType);
                string want = Array.IndexOf(names, "UpdateAndBeforeRender") >= 0
                    ? "UpdateAndBeforeRender"
                    : Array.IndexOf(names, "BeforeRender") >= 0 ? "BeforeRender" : null;
                if (want == null) return false;

                object current = p.GetValue(target);
                if (current != null && current.ToString() == want) return false;
                p.SetValue(target, Enum.Parse(p.PropertyType, want));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void BuildSabers()
        {
            if (_leftAnchor != null)
            {
                // Cross-section material (BeatSaberClone RedEmission) remains; blade/handle/trail
                // come from ArcViewer's saber assets (SaberView loads them itself).
                var cross = Resources.Load<Material>("QuestSaberDemo/Materials/RedEmission");
                _saberL = SaberView.Build(SaberSide.Left, _leftAnchor, cross,
                    new Color(0.7843137f, 0.07843138f, 0.07843138f));
                _saberL.Room = _gameRoot;
            }
            if (_rightAnchor != null)
            {
                var cross = Resources.Load<Material>("QuestSaberDemo/Materials/BlueEmission");
                _saberR = SaberView.Build(SaberSide.Right, _rightAnchor, cross,
                    new Color(0.1568627f, 0.5568627f, 0.8235294f));
                _saberR.Room = _gameRoot;
            }
        }

        private void SetSaberTrails(bool on)
        {
            if (_saberL != null) _saberL.SetTrailActive(on);
            if (_saberR != null) _saberR.SetTrailActive(on);
        }

        /// <summary>
        /// Easter egg: extend/retract saber (grip double-click of the respective hand).
        /// In the retracted state the blade cast and trigger are disabled (SaberView),
        /// so no notes can be hit. SFX: extend AND retract both play the same chopped
        /// slice "switch" sound (see below).
        /// </summary>
        private void ToggleSaberRetract(SaberSide side)
        {
            var saber = side == SaberSide.Left ? _saberL : _saberR;
            if (saber == null) return;
            if (_menuSide != 0) _menuManualChanged[(int)side] = true;
            bool nowRetracted = !saber.Retracted;
            saber.SetRetracted(nowRetracted);
            // Easter egg: extend and retract get the SAME chopped slice sound
            // (trimmed at the front, fast, duller) - this makes it act directly like a
            // switch and not like a hit on a lightsaber.
            _audio.PlaySaberToggleSfx();
            Debug.Log($"[QuestSaber] Lightsaber {side} {(nowRetracted ? "retracted" : "extended")} (Easter Egg)");
        }

        private SaberView SaberFor(SaberSide side) => side == SaberSide.Left ? _saberL : _saberR;

        /// <summary>
        /// Automatically retracts BOTH lightsabers when the menu opens. As a result,
        /// the laser/cursor of the other hand remains visible as the only "beam"; the menu page
        /// no longer glows (no blade/trail glow, regardless of saber state).
        /// </summary>
        private void AutoRetractSabersForMenu()
        {
            for (int i = 0; i < 2; i++)
            {
                _menuManualChanged[i] = false;
                _menuAutoRetracted[i] = false;
                var saber = SaberFor((SaberSide)i);
                if (saber == null) continue;
                if (!saber.Retracted) // only retract if currently extended
                {
                    saber.SetRetracted(true);
                    _menuAutoRetracted[i] = true;
                }
            }
        }

        private void RestoreSabersAfterMenu()
        {
            for (int i = 0; i < 2; i++)
            {
                if (!_menuAutoRetracted[i] || _menuManualChanged[i]) continue;
                var saber = SaberFor((SaberSide)i);
                if (saber != null) saber.SetRetracted(false);
                _menuAutoRetracted[i] = false;
            }
        }

        private void HandleSaberHit(SaberView saber, NoteView note, Vector3 point, Vector3 normal, Vector3 vel)
        {
            if (Phase != GamePhase.Playing) return;
            Vector3 notePos = note.transform.position; // save before consumption (reference: hit position)
            Vector3 noteLocal = note.transform.localPosition; // room-local, for the center scoring
            bool chainHead = note.IsChainHead;
            bool consumed = note.TrySlice(saber.Side, point, normal, saber.CrossSectionMaterial, vel);
            if (!consumed) return;
            // Haptics per object: note blocks = clearly stronger than "light" (0.95 vs 0.35);
            // mines = the strongest (1.0) with a short aftershake (GameConfig). Light contact
            // feedback for saber-on-saber/wall runs continuously in UpdateContactHaptics().
            if (note.Kind == NoteKind.Bomb)
                StartCoroutine(MineHapticsRoutine(saber.Side));
            else
                Haptics(saber.Side, GameConfig.HapticNoteAmplitude, GameConfig.HapticNoteDuration);
            Fx.PlayHit(point, saber.Side, vel,
                _gameRoot != null ? _gameRoot.forward : Vector3.forward, _roomScale);

            if (!note.WasValidSlice)
            {
                // Bomb / bad cut ("Bad Cut"): show "MISS"; combo break comes via
                // OnComboBreak. This early return skips the slice sound below, so NO sound is
                // played on this path - the reference has no separate bad-cut clip.
                ShowSliceScore(notePos, 0, true);
                return;
            }
            // Note slice: the slice sound, dull-filtered and QUIET (values in
            // GameConfig: HitSfxPitch/HitSfxLowpassHz/HitSfxVolume) - the feedback
            // also comes via haptics. HitSfxVolume = 0 = muted.
            _audio.PlayHitSfx();

            // Saber attraction: if this note is the HEAD NOTE of an arc, the saber becomes
            // the new start of the arc (original `SliderController.SetSaberAttraction`).
            if (note.Note != null && note.Note._arcHeadOf != null)
                AttachArcToSaber(note.Note._arcHeadOf, saber);

            // Scoring from the CUT MOMENT (blade contact), not only in the score zone:
            // capture reference direction + pre-swing immediately, follow-through/center after the measurement window.
            float cutTime = ScoreTime;
            Vector3 reference = saber.CurrentBladeDirectionLocal;
            float preAngle = saber.MaxBladeDeviation(reference,
                cutTime - GameConfig.SwingMeasureWindow, cutTime);
            StartCoroutine(ScoreAtCutAsync(
                saber, cutTime, reference, preAngle, noteLocal, notePos, chainHead, note.CutDirection));
        }

    }
}

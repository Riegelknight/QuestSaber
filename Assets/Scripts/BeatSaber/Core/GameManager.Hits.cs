// GameManager: hit detection, arc interaction, chains.
using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace QuestSaber
{
    public sealed partial class GameManager
    {

        /// <summary>
        /// Checks all active notes per frame against the RESPONSIBLE saber
        /// (color layer separation per OpenSaberPlus: red->left, blue->right,
        /// bombs->both) with the blade segment/box slab test (see
        /// <see cref="TryGeometricHit"/>, NOT an AABB-vs-AABB test). Allocation-free.
        /// Runs in LATEUPDATE after <see cref="ApplyRoomAnchor"/>.
        /// </summary>
        private void GeometricHitUpdate()
        {
            // IMPORTANT: also check with only active chain links (without notes) - otherwise
            // chains would no longer be counted after the head note is hit.
            if (_activeNotes.Count == 0 && _activeChains.Count == 0) return;

            for (int i = _activeNotes.Count - 1; i >= 0; i--)
            {
                var note = _activeNotes[i];
                if (note == null || note.IsConsumed) continue; // thinning is done by PruneActiveNotes

                // COLOR LAYER SEPARATION (OpenSaberPlus `LightSaber.gd`: mask per
                // saber color + bombs): the LEFT saber can only hit NOTES
                // if they are RED, the RIGHT one only on BLUE; bombs = both.
                // No saber can "steal" the note from the other color, no matter how
                // close two differently colored notes lie next to each other. A bad cut
                // through the wrong hand is excluded by design; the
                // color/direction check in NoteView.TrySlice remains as a safety net.
                if (note.Kind == NoteKind.Bomb)
                {
                    if (TryGeometricHit(_saberL, note)) continue;
                    TryGeometricHit(_saberR, note);
                }
                else
                {
                    SaberView own = note.Kind == NoteKind.Red ? _saberL : _saberR;
                    TryGeometricHit(own, note);
                }
            }

            // Chain links are checked with the same blade segment test.
            UpdateChainHits();
        }

        /// <summary>
        /// Arc interaction (1:1 idea from the original `SliderInteractionManager`): For each
        /// active arc it is checked how close the color-responsible saber blade (red->left,
        /// blue->right) is to the arc curve. The result controls arc brightness and
        /// haptics. Feedback only - the head/tail notes still give the score.
        /// Allocation-free.
        /// </summary>
        private void UpdateArcInteraction()
        {
            float radius = GameConfig.ArcInteractRadius * _roomScale;
            float pLeft = 0f, pRight = 0f;

            for (int i = 0; i < _activeArcs.Count; i++)
            {
                var arc = _activeArcs[i];
                if (arc == null || !arc.gameObject.activeSelf) continue;

                // Saber attraction FIRST: if the arc hangs on the saber (head note sliced),
                // recompute the curve from the current saber pose - this way (a) the
                // attraction already runs in the same frame as the slice and (b) the interaction
                // measurement below runs on the current arc. If the saber is not usable,
                // `UpdateAttachedCurve` releases the attraction itself.
                if (arc.IsAttached) arc.UpdateAttachedCurve();

                SaberView saber = arc.ColorType == 0 ? _saberL : _saberR;
                if (saber == null || saber.Retracted || saber.Tip == null || saber.BaseTrans == null)
                {
                    arc.SetInteraction(0f);
                    continue;
                }

                float p = arc.EvaluateInteraction(saber.BaseTrans.position, saber.Tip.position, radius);
                arc.SetInteraction(p);
                if (arc.ColorType == 0) pLeft = Mathf.Max(pLeft, p);
                else pRight = Mathf.Max(pRight, p);
            }

            ArcInteractionFeedback(SaberSide.Left, pLeft);
            ArcInteractionFeedback(SaberSide.Right, pRight);
        }

        /// <summary>
        /// Saber attraction (original `SliderController.SetSaberAttraction`): the head note
        /// of an arc was validly sliced - from now on the saber is the new START
        /// of the arc. Finds the running arc instance for the model and attaches it to the saber.
        /// </summary>
        private void AttachArcToSaber(ArcModel headModel, SaberView saber)
        {
            if (headModel == null || saber == null) return;
            for (int i = 0; i < _activeArcs.Count; i++)
            {
                var arc = _activeArcs[i];
                if (arc == null || !arc.gameObject.activeSelf) continue;
                if (!ReferenceEquals(arc.Model, headModel)) continue;
                arc.AttachToSaber(saber);
                return;
            }
        }

        /// <summary>Throttled haptics while the blade is in the arc.</summary>
        private void ArcInteractionFeedback(SaberSide side, float param)
        {
            int idx = (int)side;
            var saber = SaberFor(side);
            if (param <= 0.001f || saber == null)
            {
                _arcHapticNext[idx] = 0f;
                return;
            }

            float now = Time.time;
            if (now >= _arcHapticNext[idx])
            {
                _arcHapticNext[idx] = now + GameConfig.HapticContactInterval;
                Haptics(side, GameConfig.HapticLightAmplitude * (0.6f + 0.4f * param),
                    GameConfig.HapticContactPulseDuration);
            }
        }

        /// <summary>
        /// Checks all open links of all active chains against the color-responsible
        /// saber (segment/box slab test, as with notes). A hit gives a fixed
        /// <see cref="GameConfig.ChainLinkScore"/> x multiplier (ArcViewer: ChainLink = 20,
        /// without angle/center share). Missed links do not break the combo.
        /// </summary>
        private void UpdateChainHits()
        {
            if (_activeChains.Count == 0) return;
            for (int c = 0; c < _activeChains.Count; c++)
            {
                var chain = _activeChains[c];
                if (chain == null || chain.Expired || !chain.gameObject.activeSelf) continue;
                SaberView own = chain.ColorType == 0 ? _saberL : _saberR;
                if (own == null || own.Retracted || own.Tip == null || own.BaseTrans == null
                    || !own.HasCurrentPose) continue;

                int n = chain.LinkCount;
                for (int i = 0; i < n; i++)
                {
                    if (chain.IsLinkHit(i)) continue;
                    Transform lt = chain.LinkAt(i);
                    if (lt == null || !lt.gameObject.activeSelf) continue;
                    // Hitbox = reference chain box (web replays `toBigBox` isChain /
                    // OpenSaberPlus CollisionBig): 0.80 x 0.20 x 1.00, centered on the link
                    // - plus blade thickness.
                    Vector3 half = new Vector3(
                        GameConfig.NoteHitboxWidth * 0.5f,
                        GameConfig.NoteHitboxHeightChain * 0.5f,
                        GameConfig.NoteHitboxDepth * 0.5f);
                    half += Vector3.one * GameConfig.HitBoxPadding;
                    Bounds linkBox = BoxWorldBounds(lt, new Bounds(Vector3.zero, half * 2f));
                    if (!SaberBladeHitsBox(own, linkBox)) continue;
                    // Visually slice the link (EzySlice, BeatSaberClone look) + score.
                    Vector3 bladeDir = own.GetBladeDirection();
                    Vector3 planeNormal = Vector3.Cross(bladeDir, own.TipVelocity);
                    if (planeNormal.sqrMagnitude < 0.0001f) planeNormal = Vector3.up;
                    else planeNormal.Normalize();
                    chain.SliceLink(i, linkBox.center, planeNormal, own.CrossSectionMaterial, _roomScale);
                    AwardChainLink(lt.position);
                }
            }
        }

        /// <summary>Awards the fixed chain link score (20 x multiplier) and counts it
        /// like a note toward the combo/multiplier progress.</summary>
        private void AwardChainLink(Vector3 worldPosition)
        {
            int baseScore = Mathf.RoundToInt(GameConfig.ChainLinkScore);
            int multiplier = AdvanceCombo();
            _score += (long)baseScore * multiplier;
            UpdateHud();
            TrySaveBestImmediately();
            ShowSliceScore(worldPosition, baseScore);
        }

        /// <summary>
        /// Checks the BLADE of the current frame - segment base->tip, including
        /// PREVIOUS-FRAME sweep (interpolated intermediate poses; only with fresh history, so that
        /// pause/menu do not create jumps) - against a box. The blade is NOT
        /// thickened/lengthened; the box size alone determines the hit tolerance.
        /// </summary>
        private static bool SaberBladeHitsBox(SaberView saber, Bounds box) =>
            SaberBladeHitsBox(saber, box, out _, out _);

        /// <summary>Like <see cref="SaberBladeHitsBox(SaberView, Bounds)"/>, but additionally
        /// delivers the HIT segment (for the visual intersection point).</summary>
        private static bool SaberBladeHitsBox(SaberView saber, Bounds box,
            out Vector3 segA, out Vector3 segB)
        {
            Vector3 b = saber.CurrentBasePosition;
            Vector3 t = saber.CurrentTipPosition;
            Vector3 pb = b, pt = t;
            bool sweep = saber.HasPreviousPose && saber.SampleTimeDelta > 0.0001f
                         && saber.SampleTimeDelta < 0.25f;
            if (sweep)
            {
                pb = saber.PreviousBasePosition;
                pt = saber.PreviousTipPosition;
            }

            int n = sweep ? Mathf.Max(1, GameConfig.SaberSweepSamples) : 1;
            for (int k = 1; k <= n; k++)
            {
                float f = (float)k / n;
                segA = Vector3.Lerp(pb, b, f);
                segB = Vector3.Lerp(pt, t, f);
                if (SegmentIntersectsBounds(segA, segB, box)) return true;
            }
            segA = segB = Vector3.zero;
            return false;
        }

        private void PruneActiveNotes()
        {
            for (int i = _activeNotes.Count - 1; i >= 0; i--)
            {
                var note = _activeNotes[i];
                if (note == null || note.IsConsumed) _activeNotes.RemoveAt(i);
            }
        }

        /// <summary>
        /// A note against the BLADE: the blade SEGMENT (base->tip) of the current
        /// frame is - see <see cref="SaberBladeHitsBox"/> - interpolated from the PREVIOUS-FRAME pose
        /// (sweep) and tested against the (oriented) note box
        /// (<see cref="SegmentIntersectsBounds"/>: segment/box slab test).
        ///
        /// Why NOT AABB-against-AABB (SuperSaber `checkCollisions`): the axis box of a
        /// fast swing covers the WHOLE arc surface. It therefore "hits" notes that the
        /// blade never touches - hits then register far before the zone and with the
        /// wrong view direction, and a wrongly cut note can still
        /// count cleanly later. The segment test only hits what the blade REALLY slices
        /// (precise like Beat Saber/web replays), without tunneling: the sweep covers the
        /// arc path between two frames. <see cref="GameConfig.HitGateForward"/> remains
        /// as a cheap pre-cut for far too distant notes.
        /// </summary>
        private bool TryGeometricHit(SaberView saber, NoteView note)
        {
            if (saber == null || saber.Retracted || saber.Tip == null || saber.BaseTrans == null
                || !saber.HasCurrentPose) return false;

            if (note.transform.localPosition.z > GameConfig.HitGateForward) return false;

            Bounds noteBox = NoteWorldBounds(note);
            if (!SaberBladeHitsBox(saber, noteBox, out Vector3 winA, out Vector3 winQ)) return false;

            // Intersection point for visuals/center: the point of the blade that is closest
            // to the note center (the blade lies in the slice plane -> exactly on the slice).
            Vector3 point = ClosestPointOnSegment(winA, winQ, note.transform.position);

            Vector3 bladeDir = saber.GetBladeDirection();
            // Cut speed: smoothed tip (clone `ObjectSlicer.Velocity`
            // principle, 5-frame average - here at the TIP, since with wrist swings
            // the root moves too little). Without a swing: zero -> TrySlice: touch.
            Vector3 velocity = saber.TipVelocity;
            Vector3 planeNormal = Vector3.Cross(bladeDir, velocity);
            if (planeNormal.sqrMagnitude < 0.0001f) planeNormal = Vector3.up;
            else planeNormal.Normalize();

            HandleSaberHit(saber, note, point, planeNormal, velocity);
            return true;
        }

        /// <summary>
        /// WORLD AABB of a note's hitbox = reference collision box (web replays `toBigBox`
        /// / OpenSaberPlus CollisionBig): 0.80 x 0.50 x 1.00, dot notes 0.80 high - CENTERED
        /// on the note center (no forward offset; the score zone lies exactly on the note).
        /// Deliberately LARGER than the visible 0.5 mesh: this gives the same hit tolerance as
        /// Beat Saber / the reference. The rotation is in <see cref="BoxWorldBounds"/>
        /// (45-degree notes thus get their larger AABB).
        /// </summary>
        private static Bounds NoteWorldBounds(NoteView note)
        {
            float h = note.CutDirection >= 8 ? GameConfig.NoteHitboxHeightDot : GameConfig.NoteHitboxHeight;
            Vector3 half = new Vector3(
                GameConfig.NoteHitboxWidth * 0.5f, h * 0.5f, GameConfig.NoteHitboxDepth * 0.5f);
            // Blade thickness as tolerance (the blade is tested as a center line).
            half += Vector3.one * GameConfig.HitBoxPadding;
            return BoxWorldBounds(note.transform, new Bounds(Vector3.zero, half * 2f));
        }

        /// <summary>
        /// WORLD AABB of a local box including rotation and room scaling (OBB->AABB via
        /// the matrix columns, corresponds to `Box3.setFromObject`). Allocation-free.
        /// </summary>
        private static Bounds BoxWorldBounds(Transform t, Bounds local)
        {
            Matrix4x4 m = Matrix4x4.TRS(Vector3.zero, t.rotation, t.lossyScale);
            Vector3 h = local.extents;
            // No Vector3.Abs in UnityEngine - component-wise absolute values.
            Vector3 ax = AbsComponents(m.MultiplyVector(new Vector3(h.x, 0f, 0f)));
            Vector3 ay = AbsComponents(m.MultiplyVector(new Vector3(0f, h.y, 0f)));
            Vector3 az = AbsComponents(m.MultiplyVector(new Vector3(0f, 0f, h.z)));
            Vector3 center = t.position + m.MultiplyPoint3x4(local.center);
            return new Bounds(center, (ax + ay + az) * 2f);
        }

        private static Vector3 AbsComponents(Vector3 v) =>
            new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// <summary>
        /// Does the segment <paramref name="p0"/>-><paramref name="p1"/> intersect the
        /// axis-aligned box? Classic slab test (Ericson, Real-Time Collision
        /// Detection) with t in [0,1]. If the segment starts inside the box, it counts as a
        /// hit. Allocation-free.
        /// </summary>
        private static bool SegmentIntersectsBounds(Vector3 p0, Vector3 p1, Bounds box)
        {
            Vector3 o = p0 - box.center;
            Vector3 d = p1 - p0;
            Vector3 h = box.extents;
            float tmin = 0f, tmax = 1f;
            for (int axis = 0; axis < 3; axis++)
            {
                float origin = o[axis];
                float dir = d[axis];
                float ext = h[axis];
                if (Mathf.Abs(dir) < 1e-9f)
                {
                    // Parallel to the slab plane: only a hit if the origin lies inside.
                    if (origin < -ext || origin > ext) return false;
                    continue;
                }
                float inv = 1f / dir;
                float t1 = (-ext - origin) * inv;
                float t2 = (ext - origin) * inv;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tmin) tmin = t1;
                if (t2 < tmax) tmax = t2;
                if (tmin > tmax) return false;
            }
            return true;
        }

        /// <summary>Point on the segment <paramref name="a"/>-><paramref name="b"/> that is
        /// closest to <paramref name="p"/> (blade at the slice). Allocation-free.</summary>
        private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-12f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
            return a + ab * t;
        }

        /// <summary>
        /// Single entry point for all haptics. On Quest 3 the impulse is played as a
        /// percussive amplitude envelope (<see cref="MetaHaptics"/>) when the controller
        /// offers the required sample rate; otherwise - and on any error - the proven
        /// Unity XR impulse path is used, so the feedback can never be lost.
        /// </summary>
        private void Haptics(SaberSide side, float amplitude, float duration)
        {
            float amp = Mathf.Clamp01(amplitude);
            if (amp <= 0f || duration <= 0f) return;

            if (MetaHaptics.TryPlayEnvelope(side, amp, duration)) return;

            UnityHaptics(side, amp, duration);
        }

        /// <summary>Unity XR impulse via the cached controller device (works on
        /// Meta Quest as in the editor). `SendHapticImpulse` reports success itself,
        /// hence without a `supportsImpulse` pre-check.</summary>
        private bool UnityHaptics(SaberSide side, float amplitude, float duration)
        {
            try
            {
                if (!_devices.TryGetValue(side, out var device) || !device.isValid)
                {
                    _scratchDevices.Clear(); // reused list (no allocation)
                    InputDevices.GetDevicesAtXRNode(
                        side == SaberSide.Left ? XRNode.LeftHand : XRNode.RightHand, _scratchDevices);
                    if (_scratchDevices.Count > 0)
                    {
                        device = _scratchDevices[0];
                        _devices[side] = device;
                    }
                }
                if (!device.isValid) return false;
                return device.SendHapticImpulse(0u, amplitude, duration);
            }
            catch { return false; }
        }

        /// <summary>
        /// Mines (bombs): the STRONGEST main impulse (<see cref="GameConfig.HapticMineAmplitude"/>),
        /// then a short, weaker after-vibration (unscaled, so it fires reliably even with
        /// time stopped).
        /// </summary>
        private IEnumerator MineHapticsRoutine(SaberSide side)
        {
            Haptics(side, GameConfig.HapticMineAmplitude, GameConfig.HapticMineDuration);
            yield return new WaitForSecondsRealtime(GameConfig.HapticMineAfterDelay);
            Haptics(side, GameConfig.HapticMineAfterAmplitude, GameConfig.HapticMineAfterDuration);
        }

        /// <summary>
        /// Contact haptics: as long as the blade touches another object (saber<->saber
        /// or saber<->wall), a LIGHT vibration is sent. Throttled per hand
        /// (HapticContactInterval), so the hot path stays allocation-free and the
        /// impulses are not sent every frame. When contact ends, the
        /// timer is reset so a new contact is immediately noticeable.
        /// </summary>
        private void UpdateContactHaptics()
        {
            bool leftTouch = false, rightTouch = false;
            bool saberClash = false;
            Vector3 clashPos = Vector3.zero;

            if (IsBladeReady(_saberL) && IsBladeReady(_saberR))
            {
                // Safety: as long as the controllers are not yet tracked, both
                // sabers would sit at the origin and falsely report permanent contact. Real
                // hands are always clearly farther apart than this minimum distance.
                float handDist = Vector3.Distance(_saberL.transform.position, _saberR.transform.position);
                if (handDist > 0.05f)
                {
                    Vector3 aL = _saberL.transform.TransformPoint(_saberL.BladeClashBaseLocal);
                    Vector3 qL = _saberL.transform.TransformPoint(_saberL.BladeClashTipLocal);
                    Vector3 aR = _saberR.transform.TransformPoint(_saberR.BladeClashBaseLocal);
                    Vector3 qR = _saberR.transform.TransformPoint(_saberR.BladeClashTipLocal);
                    Vector3 laL = _saberL.transform.InverseTransformPoint(aL);
                    Vector3 lqL = _saberL.transform.InverseTransformPoint(qL);
                    Vector3 laR = _saberL.transform.InverseTransformPoint(aR);
                    Vector3 lqR = _saberL.transform.InverseTransformPoint(qR);
                    float touch = _saberL.BladeClashRadiusBase + _saberR.BladeClashRadiusBase;
                    float dist = SegmentDistance(laL, lqL, laR, lqR, out var localClash);
                    if (dist <= touch)
                    {
                        leftTouch = rightTouch = true;
                        saberClash = true;
                        clashPos = _saberL.transform.TransformPoint(localClash);
                    }
                }
            }

            // Saber <-> wall: blade segment against the wall box. Deliberately manual -
            // the wall carries no collider (see WallView/invariant 9).
            bool leftWall = false, rightWall = false;
            Vector3 sizzlePos = Vector3.zero;
            Vector3 leftWallPos = Vector3.zero, rightWallPos = Vector3.zero;
            if (_activeWalls.Count > 0)
            {
                if (IsBladeReady(_saberL) && BladeTouchesWall(_saberL, out leftWallPos))
                { leftTouch = true; leftWall = true; sizzlePos = leftWallPos; }
                if (IsBladeReady(_saberR) && BladeTouchesWall(_saberR, out rightWallPos))
                { rightTouch = true; rightWall = true; if (!leftWall) sizzlePos = rightWallPos; }
            }

            Fx.SetWallContact(SaberSide.Left, leftWall, leftWallPos, _roomScale);
            Fx.SetWallContact(SaberSide.Right, rightWall, rightWallPos, _roomScale);

            ApplyContactHaptic(SaberSide.Left, leftTouch);
            ApplyContactHaptic(SaberSide.Right, rightTouch);

            if (saberClash && !_saberClashing)
            {
                Haptics(SaberSide.Left, GameConfig.HapticLightAmplitude, GameConfig.HapticContactPulseDuration);
                Haptics(SaberSide.Right, GameConfig.HapticLightAmplitude, GameConfig.HapticContactPulseDuration);
            }
            _saberClashing = saberClash;
            Fx.SetClashContact(saberClash, clashPos, _roomScale);

            // (4) Wall sizzle: persistent at the intersection point as long as a saber is stuck
            // in the wall - spatial, electric noise.
            if (_audio != null) _audio.SetWallSizzle(leftWall || rightWall, sizzlePos);
        }

        private static bool IsBladeReady(SaberView saber) =>
            saber != null && !saber.Retracted && saber.Tip != null && saber.BaseTrans != null;

        private bool BladeTouchesWall(SaberView saber, out Vector3 contactPoint)
        {
            Vector3 a = saber.transform.TransformPoint(saber.BladeClashBaseLocal);
            Vector3 b = saber.transform.TransformPoint(saber.BladeClashTipLocal);
            contactPoint = b;
            for (int i = 0; i < _activeWalls.Count; i++)
            {
                var w = _activeWalls[i];
                if (w != null && w.TryIntersectSegment(a, b, out var p))
                {
                    contactPoint = p; // real entry point of the blade (wall sparks)
                    return true;
                }
            }
            return false;
        }

        private void ApplyContactHaptic(SaberSide side, bool touching)
        {
            int i = (int)side;
            if (!touching)
            {
                _contactHapticNext[i] = 0f;
                return;
            }
            float now = Time.unscaledTime;
            if (now < _contactHapticNext[i]) return;
            // (4) Jittery friction haptics: random impulse rate 40..80 Hz + random amplitude
            // (simulates the "sizzling"/rubbing on the wall).
            float hz = UnityEngine.Random.Range(GameConfig.WallSizzleHapticMinHz, GameConfig.WallSizzleHapticMaxHz);
            _contactHapticNext[i] = now + 1f / Mathf.Max(1f, hz);
            float amp = GameConfig.WallSizzleHapticAmplitude * UnityEngine.Random.Range(0.6f, 1f);
            Haptics(side, amp, GameConfig.HapticContactPulseDuration);
        }

        /// <summary>
        /// Shortest distance between two segments (blade center lines). Standard closest distance
        /// (Ericson, Real-Time Collision Detection) - allocation-free, no physics overhead.
        /// </summary>
        /// <summary>Shortest distance between two segments - delivers as <paramref name="contactPoint"/>
        /// the midpoint of the two closest points (saber clash contact point).</summary>
        private static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
            out Vector3 contactPoint)
        {
            Vector3 d1 = q1 - p1;
            Vector3 d2 = q2 - p2;
            Vector3 r = p1 - p2;
            float a = Vector3.Dot(d1, d1);
            float e = Vector3.Dot(d2, d2);
            float f = Vector3.Dot(d2, r);
            const float eps = 1e-8f;

            float s, t;
            if (a <= eps && e <= eps)
            {
                contactPoint = (p1 + p2) * 0.5f;
                return r.magnitude;
            }

            if (a <= eps)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= eps)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom > eps ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((b - c) / a);
                    }
                }
            }

            Vector3 c1 = p1 + d1 * s;
            Vector3 c2 = p2 + d2 * t;
            contactPoint = (c1 + c2) * 0.5f;
            return (c1 - c2).magnitude;
        }

    }
}

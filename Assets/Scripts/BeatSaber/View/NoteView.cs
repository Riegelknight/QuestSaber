using System;
using UnityEngine;
using EzySlice;

namespace QuestSaber
{
    public enum NoteKind { Red, Blue, Bomb }

    /// <summary>
    /// A note/bomb: uniform flight along the score clock (NJS of the map), immediate
    /// chart-faithful orientation, disposal behind the player and visual slicing
    /// via EzySlice (body and arrow, with a cross-section surface).
    /// </summary>
    [DefaultExecutionOrder(-100)] // position update BEFORE GameManager (hit test in LateUpdate)
    public sealed class NoteView : MonoBehaviour
    {
        public NoteModel Note { get; private set; }
        public NoteKind Kind { get; private set; }
        public bool IsConsumed { get; private set; }

        /// <summary>v3/v4 chain head: scored with max. 85 points (instead of 115).</summary>
        public bool IsChainHead => Note != null && Note._isChainHead;

        /// <summary>Arrow direction of the note (0..7 directional, 8/9 = dot/valid from any direction).</summary>
        public int CutDirection => _cutDirection;

        public Action<NoteView> OnPassedMiss;
        public Action<NoteView> OnComboBreak;

        /// <summary>True if the last TrySlice call was a valid cut (score routing).</summary>
        public bool WasValidSlice { get; private set; }

        /// <summary>Beat time of the note in song seconds (for timing evaluation).</summary>
        public float HitTime { get; private set; }

        private Rigidbody _rb;
        private MeshRenderer _bodyRenderer;
        private MeshFilter _bodyFilter;
        private Transform _dot;
        private bool _moving;
        private float _njs;              // map tempo (base units/s, from the song data)
        private float _reactionTime;     // seconds until half the jump distance
        private float _halfJumpDistance; // meters (base units)
        private Quaternion _targetRotation;    // final orientation (arrow direction)
        private float _targetRotationZ;         // target Z angle (degrees), e.g. for Noodle flip
        // pattern effects (only if the pattern specifies them, otherwise neutral):
        private Vector3 _targetBase;     // target position (x/y) without the Z (flight) component
        private float _startY;           // start height of the stack animation (== target height for normal notes)
        private float _flipStartX;       // start X for crossed doubles
        private float _flipYHeight;      // 0 = no flip; otherwise ±0.45/-0.15 (ArcViewer)
        private int _cutDirection; // 0..7 directional, 8/9 = any (cuttable from any direction)

        // ---------- Static material/mesh loading helpers ----------
        // The arrow is baked into the body mesh (sub-mesh 1) - no separate
        // arrow object anymore. The material array is always [0]=body, [1]=arrow.
        private static Material _redBody, _blueBody, _bombBody, _arrowRed, _arrowBlue;
        private static Mesh _bodyMesh, _bombMesh, _arrowMesh, _chainHeadMesh;
        private static Mesh _bodyMeshArrowed, _chainHeadMeshArrowed;
        private static Material[] _matRedArrow, _matBlueArrow, _matRed, _matBlue, _matBomb;
        private static Material _dotMaterial;
        private static Mesh _dotMesh;
        private static bool _assetsLoaded;

        /// <summary>
        /// Arrow contact point (local): upper half of the note, flush with the front face
        /// (BeatSaberClone reference: box 0.45 -> arrow at y = 0.125, z = -0.225; for
        /// note size 0.5 that is y = 0.125, z = -0.25). When baking, the arrow is offset by
        /// this point, so the mesh lies flat on the note.
        /// </summary>
        private static readonly Vector3 ArrowLocalPos = new Vector3(0f, 0.125f, -GameConfig.NoteSize * 0.5f);

        public static Mesh CachedBodyMesh { get { EnsureAssetsLoaded(); return _bodyMesh; } }
        public static Mesh CachedDotMesh { get { EnsureAssetsLoaded(); return _dotMesh; } }
        public static Material CachedDotMaterial { get { EnsureAssetsLoaded(); return _dotMaterial; } }
        /// <summary>Note body materials (BeatSaberClone style) - also used by ChainView
        /// so that chain links have exactly the same look.</summary>
        public static Material BodyMaterialRed { get { EnsureAssetsLoaded(); return _redBody; } }
        public static Material BodyMaterialBlue { get { EnsureAssetsLoaded(); return _blueBody; } }

        /// <summary>White circle marker of the dot notes (direction 8/9), child of the note.</summary>
        public void AttachDot(Transform dot) => _dot = dot;

        public static void EnsureAssetsLoaded()
        {
            if (_assetsLoaded) return;
            _assetsLoaded = true;
            // Reference 1:1 BeatSaberClone: note body = flat unlit graphs (red/blue, no
            // glow), arrows = emission graphs. new Material(shader) provides the
            // graph defaults = exactly the reference's embedded material.
            _redBody = BuildGraphMaterial("QuestSaberDemo/Shaders/RedUnlit");
            _blueBody = BuildGraphMaterial("QuestSaberDemo/Shaders/BlueUnlit");
            // Mine (bomb): model + material from the reference viewer ArcViewer
            // (Models/Bomb.obj + Materials/Bomb.mat, NoteShader/Cubemap). Fallback BlackMat.
            _bombBody = Resources.Load<Material>("QuestSaberDemo/Materials/Bomb");
            if (_bombBody == null) _bombBody = Resources.Load<Material>("QuestSaberDemo/Materials/BlackMat");
            _arrowRed = BuildGraphMaterial("QuestSaberDemo/Shaders/RedEmission");
            _arrowBlue = BuildGraphMaterial("QuestSaberDemo/Shaders/BlueEmission");
            if (_arrowRed == null) _arrowRed = Resources.Load<Material>("QuestSaberDemo/Materials/RedMat");
            if (_arrowBlue == null) _arrowBlue = Resources.Load<Material>("QuestSaberDemo/Materials/BlueMat");

            // Note body = BeatSaberClone cube Box.fbx (imported at 0.50 m and
            // read/write enabled -> EzySlice can slice it). IMPORTANT: a mesh with
            // `isReadable: 0` (e.g. ArcViewer's Cube.fbx) makes the cut silently
            // fail - the cut visual then disappears completely.
            var bodyPrefab = Resources.Load<GameObject>("QuestSaberDemo/Models/Box");
            if (bodyPrefab != null)
            {
                var mf = bodyPrefab.GetComponentInChildren<MeshFilter>();
                if (mf != null) _bodyMesh = mf.sharedMesh;
            }
            var arrowPrefab = Resources.Load<GameObject>("QuestSaberDemo/Models/Arrow");
            if (arrowPrefab != null)
            {
                var mf = arrowPrefab.GetComponentInChildren<MeshFilter>();
                if (mf != null) _arrowMesh = mf.sharedMesh;
            }
            // Chain head mesh (ArcViewer ChainHead.fbx).
            var chainHeadPrefab = Resources.Load<GameObject>("QuestSaberDemo/Models/ChainHead");
            if (chainHeadPrefab != null)
            {
                var mf = chainHeadPrefab.GetComponentInChildren<MeshFilter>();
                if (mf != null) _chainHeadMesh = mf.sharedMesh;
            }
            // mine mesh (ArcViewer model; nullptr = old cube look remains).
            var bombPrefab = Resources.Load<GameObject>("QuestSaberDemo/Models/Bomb");
            if (bombPrefab != null)
            {
                var mf = bombPrefab.GetComponentInChildren<MeshFilter>();
                if (mf != null) _bombMesh = mf.sharedMesh;
            }
            if (_bodyMesh == null) _bodyMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");

            // Bake the arrow firmly into the body (ONE mesh, 2 sub-meshes: 0=body, 1=arrow).
            // This way the arrow is part of the note (no separate arrow object anymore) and is
            // sliced along with it as one piece. Both source meshes are read/write enabled.
            if (_bodyMesh != null && _arrowMesh != null)
                _bodyMeshArrowed = BakeArrowMesh(_bodyMesh, _arrowMesh, ArrowLocalPos);
            if (_chainHeadMesh != null && _arrowMesh != null)
                _chainHeadMeshArrowed = BakeArrowMesh(_chainHeadMesh, _arrowMesh, ArrowLocalPos);

            // material arrays (0=body, 1=arrow) one-time - no GC on spawn.
            _matRedArrow = new[] { _redBody, _arrowRed };
            _matBlueArrow = new[] { _blueBody, _arrowBlue };
            _matRed = new[] { _redBody };
            _matBlue = new[] { _blueBody };
            _matBomb = new[] { _bombBody };

            // Dot marker: white, filled circle (diameter = 0.35 of the box, see NotePool).
            // Own mesh (triangle fan) + flat white unlit material; independent of
            // the note graphs so that the circle stays neutral white.
            _dotMesh = BuildCircleMesh(48, 0.5f);
            _dotMaterial = BuildWhiteUnlitMaterial();
        }

        /// <summary>
        /// Circle mesh in the XY plane (normal -Z), radius in local units. Built as a
        /// triangle fan; the diameter is thus 2 × <paramref name="radius"/>.
        /// </summary>
        private static Mesh BuildCircleMesh(int segments, float radius)
        {
            segments = Mathf.Max(3, segments);
            var mesh = new Mesh { name = "NoteDotCircle" };
            var verts = new Vector3[segments + 1];
            verts[0] = Vector3.zero;
            for (int i = 0; i < segments; i++)
            {
                float a = 2f * Mathf.PI * i / segments;
                verts[i + 1] = new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f);
            }
            var tris = new int[segments * 3];
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                tris[i * 3] = 0;
                tris[i * 3 + 1] = next + 1;
                tris[i * 3 + 2] = i + 1;
            }
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Flat white unlit material (URP/Unlit, fallbacks as in the rest of the project).</summary>
        private static Material BuildWhiteUnlitMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var mat = new Material(shader) { name = "NoteDotWhite" };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            mat.enableInstancing = true;
            return mat;
        }

        /// <summary>Material from a reference shader graph (graph defaults = embedded material).</summary>
        private static Material BuildGraphMaterial(string resourcePath)
        {
            var shader = Resources.Load<Shader>(resourcePath);
            if (shader == null)
            {
                Debug.LogWarning("[QuestSaber] Reference shader graph not loaded: " + resourcePath);
                return null;
            }
            var mat = new Material(shader);
            mat.enableInstancing = true;
            return mat;
        }

        /// <summary>
        /// Bakes the flat arrow onto the note body: ONE mesh with 2 sub-meshes
        /// (0 = body, 1 = arrow offset by <paramref name="offset"/>). The arrow is thus
        /// part of the note and is automatically sliced along by EzySlice.
        /// Normals and UVs are carried over as well (the reference graphs use both),
        /// tangents deliberately not (the graphs do not need them; EzySlice copes without).
        /// </summary>
        private static Mesh BakeArrowMesh(Mesh body, Mesh arrow, Vector3 offset)
        {
            Vector3[] bv = body.vertices;
            Vector3[] av = arrow.vertices;
            int bodyCount = bv.Length;
            int total = bodyCount + av.Length;

            var verts = new Vector3[total];
            Array.Copy(bv, 0, verts, 0, bodyCount);
            for (int i = 0; i < av.Length; i++) verts[bodyCount + i] = av[i] + offset;

            var normals = new Vector3[total];
            Vector3[] bn = body.normals;
            Vector3[] an = arrow.normals;
            for (int i = 0; i < bodyCount; i++) normals[i] = i < bn.Length ? bn[i] : Vector3.up;
            for (int i = 0; i < av.Length; i++) normals[bodyCount + i] = i < an.Length ? an[i] : Vector3.up;

            var uvs = new Vector2[total];
            Vector2[] bu = body.uv;
            Vector2[] au = arrow.uv;
            for (int i = 0; i < bodyCount; i++) uvs[i] = i < bu.Length ? bu[i] : Vector2.zero;
            for (int i = 0; i < av.Length; i++) uvs[bodyCount + i] = i < au.Length ? au[i] : Vector2.zero;

            int[] bt = body.GetTriangles(0);
            int[] at = arrow.GetTriangles(0);
            var arrowTris = new int[at.Length];
            for (int i = 0; i < at.Length; i++) arrowTris[i] = at[i] + bodyCount;

            var mesh = new Mesh { name = body.name + "+Arrow" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(bt, 0, false);
            mesh.SetTriangles(arrowTris, 1, false);
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---------- Lifecycle ----------

        public void Spawn(NoteModel note, Vector3 position,
            float hitTime, float njs, float reactionTime, float halfJumpDistance,
            float startY, float flipStartX, float flipYHeight)
        {
            if (_rb == null) _rb = GetComponent<Rigidbody>();
            Note = note;
            Kind = note._type == 3 ? NoteKind.Bomb : note._type == 0 ? NoteKind.Red : NoteKind.Blue;
            IsConsumed = false;
            WasValidSlice = false;
            HitTime = hitTime;
            _njs = njs;
            _reactionTime = reactionTime;
            _halfJumpDistance = halfJumpDistance;
            _moving = true;

            // Appearance: the arrow is already baked into the body mesh (sub-mesh 1),
            // so only the mesh + material array need to be set. Arrow only for directional notes
            // (0..7); for 8/9 ("dot": cuttable from any direction) the block remains
            // arrowless - as in the original.
            if (_bodyRenderer == null) _bodyRenderer = GetComponent<MeshRenderer>();
            if (_bodyFilter == null) _bodyFilter = GetComponent<MeshFilter>();
            bool showArrow = Kind != NoteKind.Bomb && note._cutDirection <= 7;

            Mesh bodyMesh;
            Material[] bodyMats;
            if (Kind == NoteKind.Bomb)
            {
                bodyMesh = _bombMesh != null ? _bombMesh : _bodyMesh;
                bodyMats = _matBomb;
            }
            else
            {
                bool chain = note._isChainHead && _chainHeadMesh != null;
                bool arrowed = showArrow && (chain ? _chainHeadMeshArrowed : _bodyMeshArrowed) != null;
                bodyMesh = chain
                    ? (arrowed ? _chainHeadMeshArrowed : _chainHeadMesh)
                    : (arrowed ? _bodyMeshArrowed : _bodyMesh);
                bodyMats = Kind == NoteKind.Red
                    ? (arrowed ? _matRedArrow : _matRed)
                    : (arrowed ? _matBlueArrow : _matBlue);
            }
            if (_bodyFilter != null && bodyMesh != null) _bodyFilter.sharedMesh = bodyMesh;
            if (_bodyRenderer != null) _bodyRenderer.sharedMaterials = bodyMats;

            // dot notes (direction 8/9, arrowless): white circle marker (separate child) instead of an arrow.
            bool showDot = Kind != NoteKind.Bomb && note._cutDirection >= 8;
            if (_dot != null) _dot.gameObject.SetActive(showDot);

            // direction = arrow direction + v3/v4 angle offset "a" (ArcViewer CalculateObjectAngle(d,a)).
            _targetRotationZ = TargetRotationZ(note._cutDirection) + note._angleOffset;
            // Noodle Extensions: free rotation (worldRotation/localRotation) overrides the
            // arrow direction; `flip` mirrors it. With free rotation the Z rotation is omitted.
            var noodle = note._customData;
            if (noodle != null && noodle.Flip) _targetRotationZ = -_targetRotationZ;

            if (noodle != null && noodle.HasWorldRotation)
            {
                var q = noodle.WorldRotation;
                _targetRotation = new Quaternion(q[0], q[1], q[2], q[3]);
            }
            else if (noodle != null && noodle.HasLocalRotation)
            {
                var q = noodle.LocalRotation;
                _targetRotation = Quaternion.Euler(0f, 0f, _targetRotationZ)
                    * new Quaternion(q[0], q[1], q[2], q[3]);
            }
            else
            {
                _targetRotation = Quaternion.Euler(0f, 0f, _targetRotationZ);
            }
            _targetRotationZ = _targetRotation.eulerAngles.z;
            _cutDirection = note._cutDirection;

            // Noodle scale (v3 `scale`) - the hitbox scales with it (lossyScale).
            transform.localScale = noodle != null && noodle.HasScale
                ? new Vector3(noodle.Scale[0], noodle.Scale[1], noodle.Scale[2])
                : Vector3.one;

            // Pattern effects (computed by the GameManager from the chart): stack start height
            // and flip data. For normal notes startY == target Y and flipYHeight == 0 -
            // then the note flies straight (no effect).
            _targetBase = position;
            _startY = startY;
            _flipStartX = flipStartX;
            _flipYHeight = flipYHeight;

            // Position is a BASE position in the room root (_gameRoot). The root scales
            // the entire room (PlayerProfile.Scale) - therefore set it locally, not in world space.
            transform.localPosition = new Vector3(
                Mathf.Approximately(_flipYHeight, 0f) ? position.x : flipStartX,
                _startY + GameConfig.FieldHeightOffsetMeters, position.z);
            // chart-faithful orientation: the arrow direction is fixed from the first frame.
            transform.localRotation = _targetRotation;
            gameObject.SetActive(true);
        }

        private void Update()
        {
            if (!_moving) return;

            // Position from the visual clock (score clock + optional note offset - which now
            // shifts EVERYTHING together, including the hitbox).
            float visualTime = GameManager.VisualTime;
            transform.localPosition = LocalPositionAt(visualTime);

            // NO global rotation animation: ArcViewer's `doRotationAnimation` is OFF by default
            // (only via setting/force-accuracy). Notes immediately stand at their target rotation; only
            // Noodle notes carry a free rotation (`_targetRotation`).
            transform.localRotation = _targetRotation;

            float z = transform.localPosition.z;
            if (!IsConsumed && z <= GameConfig.NoteMissZ)
            {
                IsConsumed = true;
                if (GameConfig.LogHitDiagnostics)
                    Debug.Log($"[Miss] kind={Kind} dir={_cutDirection} hitTime={HitTime:0.000}");
                OnPassedMiss?.Invoke(this);
            }

            if (IsConsumed && z <= GameConfig.NoteMissZ)
            {
                _moving = false;
                NotePool.Return(this);
            }
        }

        /// <summary>
        /// Position (local in the parent) at an arbitrary song time - identical to the
        /// Update movement including stack parabola and side flip.
        /// </summary>
        public Vector3 LocalPositionAt(float songTime)
        {
            float z = GameConfig.JumpZ(HitTime, songTime, _njs);

            // Stack parabola: before entering the last ReactionTime the note stands at
            // its start height; afterwards it glides into the target row (for startY == target Y
            // this is a constant = straight flight path).
            float x = _targetBase.x;
            float y = songTime < HitTime - _reactionTime
                ? _startY
                : GameConfig.SpawnParabola(_targetBase.y, _startY, _halfJumpDistance, z);

            // Side flip (only if the pattern specifies it): progress 0 = entry into
            // the reaction, 1 = beat. In the first half-progress the note switches from the
            // start side to its own and has the vertical over/under arc in the process.
            if (!Mathf.Approximately(_flipYHeight, 0f))
            {
                float progress = (songTime + _reactionTime - HitTime) / _reactionTime;
                if (progress <= 0f)
                {
                    x = _flipStartX;
                }
                else if (progress < 0.5f)
                {
                    float t = progress / 0.5f;
                    x = Mathf.Lerp(_flipStartX, _targetBase.x, QuadInOut(t));
                    y += _flipYHeight * (0.5f - Mathf.Cos(progress * Mathf.PI * 4f) / 2f);
                }
            }

            // Add the "field height" offset (notes/mines) every frame: it applies live, and since
            // the note transform itself is the hitbox, it moves along automatically.
            return new Vector3(x, y + GameConfig.FieldHeightOffsetMeters, z);
        }

        /// <summary>EaseInOut quad like ArcViewer (`Easings.Quad.InOut`) for the side flip.</summary>
        private static float QuadInOut(float x)
        {
            return x < 0.5f ? 2f * x * x : 1f - Mathf.Pow(-2f * x + 2f, 2f) / 2f;
        }

        // ---------- Cut ----------

        public bool TrySlice(
            SaberSide side,
            Vector3 slicePoint,
            Vector3 planeNormal,
            Material crossSectionMaterial,
            Vector3 cutVelocity)
        {
            if (IsConsumed || !_moving) return false;

            if (Kind == NoteKind.Bomb)
            {
                Fx.PlayMineExplosion(
                    transform.position,
                    Mathf.Max(0.01f, transform.lossyScale.x));
                ConsumeFail();
                return true;
            }

            // color/hand: red = left saber, blue = right (NoteKind comes from _type).
            bool wrongHand = (Kind == NoteKind.Red) != (side == SaberSide.Left);

            bool wrongDirection;
            float moveAngle = -1f; // only for diagnostics (LogHitDiagnostics)
            // Note local system: includes the display rotation (Z), the room yaw and the
            // room scaling - the arrow thus always points along local -Y.
            Vector3 local = transform.InverseTransformDirection(cutVelocity);
            var v2 = new Vector2(local.x, local.y); // ignore depth
            float speed2 = v2.magnitude;

            // TWO separate cases (instead of a shared "wrong"):
            //  - tooSlow: the blade only TOUCHES the note, it is not a cut attempt at all
            //    (the note stays alive, may pass through as a MISS).
            //  - wrong direction: a REAL swing past the arrow => immediate bad cut.
            bool tooSlow = speed2 < GameConfig.CutMinSpeed;
            if (tooSlow)
            {
                wrongDirection = false;
            }
            else if (_cutDirection >= 8)
            {
                // Dot (8/9): direction arbitrary - only real movement required
                // (OpenSaberPlus: "ignore angle if is a dot").
                wrongDirection = false;
                moveAngle = 0f;
            }
            else
            {
                moveAngle = Vector2.Angle(v2, Vector2.down); // arrow direction = local -Y
                wrongDirection = moveAngle > GameConfig.CutAllowedAngle;
            }

            if (GameConfig.LogHitDiagnostics)
            {
                Debug.Log($"[Hit] side={side} kind={Kind} dir={_cutDirection} " +
                          $"valid={!(wrongHand || wrongDirection || tooSlow)} wrongHand={wrongHand} " +
                          $"wrongDirection={wrongDirection} angle={moveAngle:0.0} " +
                          $"limit={GameConfig.CutAllowedAngle:0} " +
                          $"z={transform.localPosition.z:0.00} speed={speed2:0.00}");
            }

            // Pure touch (no swing): NO consumption - same behavior as the
            // reference, which only counts a cut from a minimum blade speed.
            if (tooSlow)
            {
                if (GameConfig.LogHitDiagnostics)
                    Debug.Log($"[Ignore] side={side} kind={Kind} dir={_cutDirection} " +
                              $"z={transform.localPosition.z:0.00} angle={moveAngle:0.0} " +
                              $"speed={speed2:0.00} (no swing)");
                return false;
            }

            // ---- Error case ("Bad Cut"): wrong color/hand or wrong direction ----
            // There is NO "windup protection" anymore (previously a wrong contact far before
            // the hit plane was ignored). That allowed one to cut into a note from the WRONG
            // SIDE and still hit it cleanly afterwards. Now, as in
            // the reference (SuperSaber `wrongHit` / Beat Saber): a swing in the wrong
            // direction consumes the note immediately as a bad cut.
            // The note is still SLICED VISUALLY here (DoSlice below): slicing from any side is
            // intended - only the POINTS require the direction (GameConfig.CutAllowedAngle).
            if (wrongHand || wrongDirection)
            {
                IsConsumed = true;
                _moving = false;
                WasValidSlice = false;
                DoSlice(slicePoint, planeNormal, crossSectionMaterial);
                OnComboBreak?.Invoke(this);
                NotePool.Return(this);
                return true;
            }

            IsConsumed = true;
            _moving = false;
            WasValidSlice = true;
            DoSlice(slicePoint, planeNormal, crossSectionMaterial);

            NotePool.Return(this);
            return true;
        }

        /// <summary>
        /// Splits the note visually into two halves (EzySlice) and lets them visibly
        /// fly apart. <paramref name="planeNormal"/> is the world normal of the
        /// cut plane - each half flies in its direction (±normal).
        /// </summary>
        private void DoSlice(Vector3 slicePoint, Vector3 planeNormal, Material crossSection)
        {
            // The arrow sits as a sub-mesh in the note mesh and is therefore automatically
            // sliced along - no separate arrow object anymore.
            SliceVisual(gameObject, transform, slicePoint, planeNormal, crossSection);
        }

        /// <summary>
        /// Splits a visible mesh (EzySlice) and lets both halves fly apart.
        /// <paramref name="placement"/> is the transform onto which the halves are placed.
        /// </summary>
        private static void SliceVisual(GameObject target, Transform placement,
            Vector3 slicePoint, Vector3 planeNormal, Material crossSection)
        {
            var filter = target.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null || !filter.sharedMesh.isReadable) return;

            var hull = target.Slice(slicePoint, planeNormal);
            if (hull == null) return;

            var upper = hull.CreateUpperHull(target, crossSection);
            var lower = hull.CreateLowerHull(target, crossSection);

            PlacePieceAt(upper, placement);
            PlacePieceAt(lower, placement);

            AddPiecePhysics(upper, planeNormal);
            AddPiecePhysics(lower, -planeNormal);
        }

        /// <summary>
        /// Places a cut half exactly on the WORLD transform of the target (position, rotation,
        /// world scale). As a root object its localScale = world scale.
        /// </summary>
        private static void PlacePieceAt(GameObject piece, Transform source)
        {
            if (piece == null) return;
            piece.transform.SetPositionAndRotation(source.position, source.rotation);
            piece.transform.localScale = source.lossyScale;
        }

        private static void AddPiecePhysics(GameObject piece, Vector3 flyDirection)
        {
            if (piece == null) return;
            var rb = piece.AddComponent<Rigidbody>();
            // Instead of a convex MeshCollider (the convex-hull cook runs synchronously on the
            // main thread = source of stutter during fast cuts), a cheap BoxCollider.
            // Identical effect for the purely VISUAL halves; the cut visual itself
            // (the EzySlice half-meshes) remains completely untouched. Size = note cube
            // (local; the world scale is already in the half's localScale).
            var col = piece.AddComponent<BoxCollider>();
            col.size = Vector3.one * GameConfig.NoteSize;

            // Visible flying apart instead of just "falling down": the halves are
            // their own WORLD objects (the room scale is already in their size), therefore
            // the room scale here comes directly from the half's lossyScale.
            float roomScale = Mathf.Max(0.01f, piece.transform.lossyScale.x);
            Vector3 dir = flyDirection.sqrMagnitude > 1e-6f ? flyDirection.normalized : Vector3.up;
            rb.linearVelocity = dir * (GameConfig.SlicePieceSpeed * roomScale);
            // angularVelocity is in rad/s (Unity) - SlicePieceSpin is already rad/s.
            rb.angularVelocity = new Vector3(
                UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f))
                * GameConfig.SlicePieceSpin;

            Destroy(piece, GameConfig.DestroyDelayTime);
        }

        // Note: the CENTER evaluation now lies in SaberView.MinBladeCenterDeviation
        // (closest XY pass over the measurement window) instead of a single frame -
        // see GameManager.ScoreAtCutAsync.

        private void ConsumeFail()
        {
            IsConsumed = true;
            _moving = false;
            // The explosion particle burst lies in the bomb branch of TrySlice (before this
            // call); here only the combo break + return to the pool happens.
            OnComboBreak?.Invoke(this);
            NotePool.Return(this);
        }

        // ---------- Arrow/direction ----------

        /// <summary>
        /// Arrow rotation around Z. At 0° the arrow mesh points DOWN (-Y). In Unity
        /// (left-handed) a positive Z rotation from the player's view turns COUNTER-
        /// clockwise (Unity docs: +X->+Y is CCW when looking along +Z), i.e. mathematically
        /// 0°=down, 90°=right, 180°=up, 270°=left. The diagonals follow from that.
        /// 0=up, 1=down, 2=left, 3=right, 4..7 diagonals (45°), 8/9=dot (arrowless).
        /// </summary>
        private float TargetRotationZ(int cutDirection)
        {
            return cutDirection switch
            {
                0 => 180f,  // up
                1 => 0f,    // down
                2 => 270f,  // left
                3 => 90f,   // right
                4 => 225f,  // up-left
                5 => 135f,  // up-right
                6 => 315f,  // down-left
                7 => 45f,   // down-right
                _ => 0f,    // 8/9 = dot (arrow is hidden)
            };
        }

    }
}

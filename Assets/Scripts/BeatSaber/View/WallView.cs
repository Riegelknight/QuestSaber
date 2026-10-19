using UnityEngine;

namespace QuestSaber
{
    /// <summary>
    /// A wall (_obstacle): moves toward the player like a note, blocks via
    /// headset collision (combo reset). Type 0 = full wall, type 1 = crouch wall.
    /// Look as in the reference viewer ArcViewer: semi-transparent block with glowing
    /// edges (body and edge shaders from there, no textures).
    /// </summary>
    [DefaultExecutionOrder(-100)] // position update BEFORE GameManager.Update (head test)
    public sealed class WallView : MonoBehaviour
    {
        public ObstacleModel Obs { get; private set; }
        public bool Consumed { get; private set; }
        public bool HeadAlreadyHit { get; set; }

        private bool _moving;
        private float _length;           // length in movement direction (base units)
        private float _njs;              // map tempo (base units/s)
        private float _hitTime;          // start beat of the wall in song seconds
        private float _rawBottom;        // bottom edge of the wall (raw map value; 0 = on the floor)
        private float _rawHeight;        // height of the wall (raw map value)
        private bool _isFloorWall;       // true = bottom edge 0 (floor-standing, does NOT follow the field height)
        private Transform _edges;        // edge shell (inverse scaling of the height)
        private bool _geomApplied;       // geometry already set? (saves per-frame writes)
        private float _geomFieldHeight;  // last applied "field height"

        /// <summary>
        /// Builds the wall visually + physically. <paramref name="width"/> and
        /// <paramref name="height"/> are base units (grid), <paramref name="length"/>
        /// the stretched wall length in movement direction (duration × NJS). <paramref name="rawBottom"/>
        /// is the bottom edge from the map (0 = on the play floor). The "field height" shifts everything
        /// upward; floor-standing walls are extended down to the floor.
        /// </summary>
        public static WallView Create(
            ObstacleModel obs, Vector3 position, Transform parent,
            float width, float height, float length,
            float hitTime, float njs, float rawBottom)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Wall";
            go.layer = GameConfig.WallLayer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position; // Y is corrected right after by ApplyVerticalGeometry

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var cubeMesh = go.GetComponent<MeshFilter>().sharedMesh;

            // Wall size in base units: width/height from the grid, length = duration × NJS
            // (ArcViewer WallManager: worldScale = (Width, Height, wallLength)).
            var fullScale = new Vector3(
                Mathf.Max(GameConfig.MinWallSize, width),
                Mathf.Max(GameConfig.MinWallSize, height),
                Mathf.Max(GameConfig.MinWallSize, length));
            // The BODY is minimally smaller than the edge shell (ArcViewer WallHandler):
            // without this offset there would be Z-fighting when the edges of two
            // adjacent walls fall exactly on top of each other.
            const float centerScaleOffset = 0.001f;
            var bodyScale = new Vector3(
                fullScale.x - centerScaleOffset,
                fullScale.y - centerScaleOffset,
                fullScale.z - centerScaleOffset);
            go.transform.localScale = bodyScale;

            var edges = ApplyWallLook(go, renderer, cubeMesh, bodyScale, fullScale);

            Destroy(go.GetComponent<Collider>());

            var body = go.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            var view = go.AddComponent<WallView>();
            view.Obs = obs;
            view._moving = true;
            view._length = fullScale.z;
            view._hitTime = hitTime;
            view._njs = njs;
            view._rawBottom = rawBottom;
            view._rawHeight = height;
            view._isFloorWall = rawBottom <= 0.001f;
            view._edges = edges;
            view.ApplyVerticalGeometry(); // set height/position incl. "field height" offset
            return view;
        }

        // ---------- Look: wall from ArcViewer (body + edges) ----------
        // Two renderers per wall (body = unlit transparent, edges = lit + alpha clip),
        // both materials SHARED and without textures; the shaders only compute with
        // position/normal (see PROJECT_NOTES §10). The edge shader needs the
        // wall size as "_WallScale" so that the edge width stays the same everywhere even
        // for very different dimensions (4 columns wide vs. 0.5 high).
        private static Material _wallBodyMat, _wallEdgeMat;
        private static MaterialPropertyBlock _wallProps; // reused (no allocation per wall)
        private static bool _wallLookLoaded;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int WallScaleId = Shader.PropertyToID("_WallScale");
        private static readonly int LightnessId = Shader.PropertyToID("_Lightness");

        private static void EnsureWallLook()
        {
            if (_wallLookLoaded) return;
            _wallLookLoaded = true;
            _wallProps = new MaterialPropertyBlock();
            _wallBodyMat = Resources.Load<Material>("QuestSaberDemo/Materials/Wall");
            _wallEdgeMat = Resources.Load<Material>("QuestSaberDemo/Materials/WallEdges");
            if (_wallBodyMat == null) _wallBodyMat = CreateFallbackGlassMaterial();
        }

        private static Transform ApplyWallLook(
            GameObject body, MeshRenderer bodyRenderer, Mesh cubeMesh, Vector3 bodyScale, Vector3 fullScale)
        {
            EnsureWallLook();

            // Body: semi-transparent wall block (ArcViewer wall shader). Color, opacity and
            // brightness per wall via the reused block - the material stays shared.
            var color = GameConfig.WallColor;
            bodyRenderer.sharedMaterial = _wallBodyMat;
            _wallProps.SetColor(BaseColorId, new Color(color.r, color.g, color.b, GameConfig.WallOpacity));
            _wallProps.SetFloat(LightnessId, GameConfig.WallLightness);
            bodyRenderer.SetPropertyBlock(_wallProps);

            if (_wallEdgeMat == null) return null; // fallback (assets missing): without edges

            var edges = new GameObject("Edges");
            edges.layer = GameConfig.WallLayer;
            edges.transform.SetParent(body.transform, false);
            // Inverse scaling of the minimally smaller body => the edge shell lies exactly
            // on fullScale (ArcViewer WallHandler.SetScale).
            edges.transform.localScale = new Vector3(
                fullScale.x / bodyScale.x, fullScale.y / bodyScale.y, fullScale.z / bodyScale.z);

            var edgeRenderer = edges.AddComponent<MeshRenderer>();
            var edgeFilter = edges.AddComponent<MeshFilter>();
            edgeFilter.sharedMesh = cubeMesh;
            edgeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            edgeRenderer.sharedMaterial = _wallEdgeMat;
            _wallProps.SetColor(BaseColorId, color);      // edge color (fully opaque)
            _wallProps.SetVector(WallScaleId, fullScale); // edge width independent of the wall size
            edgeRenderer.SetPropertyBlock(_wallProps);
            return edges.transform;
        }

        private static Material CreateFallbackGlassMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var mat = new Material(shader) { name = "WallGlassFallback" };

            mat.SetFloat("_Surface", 1f);                          // transparent
            mat.SetFloat("_Blend", 0f);                            // alpha blending
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            mat.SetColor("_BaseColor", new Color(0.80f, 0.90f, 1.00f, 0.30f));
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 0.92f);
            mat.SetColor("_EmissionColor", Color.black);

            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_ON");
            return mat;
        }

        /// <summary>
        /// Moves the wall time-based (GameConfig.JumpZ, continuous NJS flight) and
        /// collects it once its BACK edge has passed the destroy line.
        /// DISPLAY = score clock + note offset (GameManager.VisualTime); the collisions
        /// (IsHeadInside/IntersectsSegment), in contrast, always compute on the fixed score plane.
        /// The front edge is offset by <c>NoteSize/2</c> so that it
        /// aligns with the front face of the notes (ArcViewer WallManager.UpdateVisual).
        /// </summary>
        private void Update()
        {
            if (!_moving || Consumed) return;

            float visualTime = GameManager.VisualTime;
            float front = GameConfig.JumpZ(_hitTime, visualTime, _njs)
                          - GameConfig.NoteSize * 0.5f;

            // Adapt vertical geometry live to the "field height" offset (floor walls
            // stay on the floor and grow upward; floating walls are shifted along too).
            ApplyVerticalGeometry();

            var pos = transform.localPosition;
            pos.z = front + _length * 0.5f;
            transform.localPosition = pos;

            if (front + _length <= GameConfig.DestroyZ)
            {
                _moving = false;
                Consumed = true;
            }
        }

        /// <summary>
        /// World position of the fixed SCORE plane (collisions) at the current score time.
        /// The display may be shifted by the note offset - the collision may not.
        /// </summary>
        private Vector3 ScoreWorldPosition()
        {
            float front = GameConfig.JumpZ(_hitTime, GameManager.ScoreTime, _njs)
                          - GameConfig.NoteSize * 0.5f;
            var local = transform.localPosition;
            local.y = CurrentCenterY();
            local.z = front + _length * 0.5f;
            Transform parent = transform.parent;
            return parent != null ? parent.TransformPoint(local) : local;
        }

        /// <summary>
        /// Sets the wall geometry (height/Y). The "field height" shifts EVERYTHING except the floor:
        /// floating walls completely; floor-standing walls are extended DOWN to the floor
        /// so that they keep touching it. Runs every frame -> live.
        /// </summary>
        private void ApplyVerticalGeometry()
        {
            // Only re-apply if the "field height" has changed: raw geometry and
            // wall type are fixed, everything else constant - saves transform dirty writes per
            // frame and wall. On a real field-height change it takes effect again immediately.
            float fieldH = GameConfig.FieldHeightOffsetMeters;
            if (_geomApplied && Mathf.Approximately(fieldH, _geomFieldHeight)) return;
            _geomApplied = true;
            _geomFieldHeight = fieldH;

            float fullY = CurrentFullHeight();
            float bottom = CurrentBottom();
            float bodyY = Mathf.Max(GameConfig.MinWallSize, fullY - 0.001f);

            var scale = transform.localScale;
            scale.y = bodyY;
            transform.localScale = scale;

            if (_edges != null)
            {
                var es = _edges.localScale;
                es.y = fullY / bodyY;
                _edges.localScale = es;
            }

            var pos = transform.localPosition;
            pos.y = bottom + fullY * 0.5f + GameConfig.WallContainerOffsetY;
            transform.localPosition = pos;
        }

        /// <summary>Bottom edge (local): everything except the floor follows the "field height" offset.
        /// Floor-standing walls are extended down to the floor (0) so that they keep touching it.</summary>
        private float CurrentBottom()
            => _isFloorWall ? 0f : _rawBottom + GameConfig.FieldHeightOffsetMeters;

        /// <summary>Total height: floor-standing walls are extended DOWN by the "field height" offset
        /// to the floor (height = raw height + offset); floating walls keep their height
        /// and are only shifted.</summary>
        private float CurrentFullHeight()
            => Mathf.Max(GameConfig.MinWallSize,
                _isFloorWall ? _rawHeight + GameConfig.FieldHeightOffsetMeters : _rawHeight);

        /// <summary>Y center (local) of the current wall (for the collision) - incl. the
        /// wall container (ArcViewer "Walls" y = 0.1).</summary>
        private float CurrentCenterY()
            => CurrentBottom() + CurrentFullHeight() * 0.5f + GameConfig.WallContainerOffsetY;

        /// <summary>World->local matrix of the SCORE position (rotation/scaling of the wall).</summary>
        private Matrix4x4 ScoreToLocal()
        {
            return Matrix4x4.TRS(ScoreWorldPosition(), transform.rotation, transform.lossyScale).inverse;
        }

        /// <summary>
        /// True if the head (HMD) stands inside the wall box. Computed in the LOCAL space of the
        /// SCORE position (the matrix accounts for the complete scaling of the room root);
        /// the unit cube spans -0.5..0.5 in mesh coordinates. Deliberately WITHOUT
        /// tolerance: the wall is stretched in the movement direction by duration × NJS - a
        /// fixed local tolerance would blow up to meters in the length.
        /// </summary>
        public bool IsHeadInside(Vector3 headPos)
        {
            if (Consumed) return false;
            Vector3 local = ScoreToLocal().MultiplyPoint3x4(headPos);
            return Mathf.Abs(local.x) <= 0.5f
                && Mathf.Abs(local.y) <= 0.5f
                && Mathf.Abs(local.z) <= 0.5f;
        }

        /// <summary>
        /// True if the segment (e.g. the saber blade) intersects the wall box;
        /// additionally returns the WORLD entry point of the segment into the box
        /// (for the wall spark position). Manual segment-vs-box test in the LOCAL
        /// space of the SCORE position (slab/layer method); the matrix factors out the complete
        /// scaling of the room root. Deliberately WITHOUT a collider - for performance/invariant
        /// reasons the wall carries none (see IsHeadInside/file header).
        /// </summary>
        public bool TryIntersectSegment(Vector3 worldA, Vector3 worldB, out Vector3 worldPoint)
        {
            worldPoint = worldB;
            if (Consumed) return false;

            Matrix4x4 toWorld = Matrix4x4.TRS(ScoreWorldPosition(), transform.rotation, transform.lossyScale);
            Matrix4x4 toLocal = toWorld.inverse;
            Vector3 a = toLocal.MultiplyPoint3x4(worldA);
            Vector3 b = toLocal.MultiplyPoint3x4(worldB);
            // The unit cube spans -0.5..0.5 in mesh coordinates.
            const float half = 0.5f;
            float tMin = 0f, tMax = 1f;
            for (int axis = 0; axis < 3; axis++)
            {
                float d = b[axis] - a[axis];
                if (Mathf.Abs(d) < 1e-6f)
                {
                    // segment parallel to the slab: if it lies outside, there is no intersection.
                    if (a[axis] < -half || a[axis] > half) return false;
                    continue;
                }
                float t1 = (-half - a[axis]) / d;
                float t2 = (half - a[axis]) / d;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tMin) tMin = t1;
                if (t2 < tMax) tMax = t2;
                if (tMin > tMax) return false;
            }
            Vector3 localHit = a + (b - a) * tMin;
            worldPoint = toWorld.MultiplyPoint3x4(localHit);
            return true;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using EzySlice;

namespace QuestSaber
{
    /// <summary>A chain: head note (separate) plus visible, individually hittable links.</summary>
    [DefaultExecutionOrder(-100)]
    public sealed class ChainView : MonoBehaviour
    {
        private readonly List<Transform> _links = new();     // all created link transforms
        private readonly List<Vector2> _basePos = new();     // XY on the Bézier (constant)
        private readonly List<float> _hitTimes = new();      // beat time per link (seconds)
        private readonly List<float> _angles = new();        // Z rotation per link
        private readonly List<bool> _hit = new();

        private int _activeLinks;
        private int _colorType;
        private float _njs;
        private bool _moving;

        public bool Expired { get; private set; }
        public int ColorType => _colorType;
        public int LinkCount => _activeLinks;

        private static readonly Stack<ChainView> _free = new();

        public static ChainView Get(ChainModel chain, float headTime, float tailTime, float njs, Transform root)
        {
            ChainView view = _free.Count > 0 ? _free.Pop() : CreateNew(root);
            view.Build(chain, headTime, tailTime, njs);
            return view;
        }

        public static void Return(ChainView view)
        {
            if (view == null) return;
            view._moving = false;
            for (int i = 0; i < view._links.Count; i++)
                if (view._links[i] != null) view._links[i].gameObject.SetActive(false);
            view.gameObject.SetActive(false);
            _free.Push(view);
        }

        /// <summary>Collects all active chains (song change).</summary>
        public static void ReturnAll(Transform root)
        {
            if (root == null) return;
            var active = root.GetComponentsInChildren<ChainView>(); // active only
            for (int i = 0; i < active.Length; i++) Return(active[i]);
        }

        private static ChainView CreateNew(Transform root)
        {
            var go = new GameObject("Chain");
            var view = go.AddComponent<ChainView>();
            if (root != null) go.transform.SetParent(root, false);
            return view;
        }

        private void Build(ChainModel chain, float headTime, float tailTime, float njs)
        {
            _moving = true;
            Expired = false;
            _colorType = chain._colorType;
            _njs = njs;
            gameObject.SetActive(true);

            // Head/tail point and midpoint control (ArcViewer ChainManager.CreateChainLinks).
            Vector2 head = GameConfig.GridPosition(chain._lineIndex, chain._lineLayer);
            Vector2 tail = GameConfig.GridPosition(chain._tailLineIndex, chain._tailLineLayer);
            int cutDir = Mathf.Clamp((int)chain._cutDirection, 0, 8);
            Vector2 midDir = GameConfig.DirectionVector(GameConfig.NoteAngleDegrees(cutDir));
            float directDistance = Vector2.Distance(head, tail);
            Vector2 mid = head + midDir * (directDistance * 0.5f);

            float duration = Mathf.Max(0f, tailTime - headTime);
            float squish = chain._squishAmount < 0.001f ? 1f : chain._squishAmount;
            int segments = Mathf.Clamp(Mathf.RoundToInt(chain._sliceCount), 1, 64);
            _activeLinks = Mathf.Max(0, segments - 1);

            EnsureLinkObjects(_activeLinks);
            // Same look as the notes (BeatSaberClone: flat red/blue).
            Material mat = _colorType == 0 ? NoteView.BodyMaterialRed : NoteView.BodyMaterialBlue;

            float denom = Mathf.Max(1, segments - 1);
            for (int i = 0; i < _activeLinks; i++)
            {
                // ArcViewer: timeProgress = i/(SegmentCount-1) for i = 1..SegmentCount-1
                // (i=0 is the head; the last link lies exactly at the tail point).
                float progress = (float)(i + 1) / denom;
                float t = Mathf.Clamp01(progress * squish);
                Vector2 p = QuadBezier(head, mid, tail, t);

                Transform tr = _links[i];
                tr.gameObject.SetActive(true);
                var mr = tr.GetComponent<MeshRenderer>();
                if (mr != null) mr.sharedMaterial = mat;

                if (_basePos.Count <= i) { _basePos.Add(p); _hitTimes.Add(0f); _angles.Add(0f); _hit.Add(false); }
                _basePos[i] = p;
                _hitTimes[i] = headTime + duration * progress;
                _angles[i] = AngleOnQuadBezier(head, mid, tail, t);
                _hit[i] = false;
            }
            // Hide unneeded links.
            for (int i = _activeLinks; i < _links.Count; i++)
                if (_links[i] != null) _links[i].gameObject.SetActive(false);

            UpdateLinkTransforms();
        }

        private void Update()
        {
            if (!_moving) return;
            UpdateLinkTransforms();

            if (_activeLinks == 0) { Expired = true; Return(this); return; }

            bool anyPending = false;
            for (int i = 0; i < _activeLinks; i++)
            {
                if (_hit[i]) continue;
                if (GameConfig.JumpZ(_hitTimes[i], GameManager.VisualTime, _njs) > GameConfig.NoteMissZ)
                {
                    anyPending = true;
                    break;
                }
            }
            if (!anyPending)
            {
                // all links hit or flown past -> back to the pool
                Expired = true;
                Return(this);
            }
        }

        private void UpdateLinkTransforms()
        {
            float yOffset = GameConfig.FieldHeightOffsetMeters;
            for (int i = 0; i < _activeLinks; i++)
            {
                if (_hit[i]) { if (_links[i].gameObject.activeSelf) _links[i].gameObject.SetActive(false); continue; }
                var p = _basePos[i];
                float z = GameConfig.JumpZ(_hitTimes[i], GameManager.VisualTime, _njs);
                _links[i].localPosition = new Vector3(p.x, p.y + yOffset, z);
                _links[i].localRotation = Quaternion.Euler(0f, 0f, _angles[i]);
            }
        }

        // ---------- Hit interface (GameManager) ----------

        public Transform LinkAt(int index) => index >= 0 && index < _activeLinks ? _links[index] : null;
        public bool IsLinkHit(int index) => index < 0 || index >= _activeLinks || _hit[index];

        /// <summary>
        /// Slices a link visually (EzySlice) and lets the halves fly away -
        /// 1:1 like `NoteView.DoSlice` (BeatSaberClone look). The original stays in the chain
        /// (only hidden); the halves are created from a copy and are world objects.
        /// </summary>
        public void SliceLink(int index, Vector3 point, Vector3 normal, Material crossSection, float roomScale)
        {
            if (index < 0 || index >= _activeLinks || _hit[index]) return;
            _hit[index] = true;
            var tr = _links[index];
            if (tr == null) return;
            var go = tr.gameObject;
            if (!go.activeSelf) return;

            var srcMf = go.GetComponent<MeshFilter>();
            var srcMr = go.GetComponent<MeshRenderer>();
            Mesh mesh = srcMf != null ? srcMf.sharedMesh : null;
            Material mat = srcMr != null ? srcMr.sharedMaterial : null;
            Vector3 wPos = go.transform.position;
            Quaternion wRot = go.transform.rotation;
            Vector3 wScale = go.transform.lossyScale;

            // Hide the original (the chain stays intact); halves from a FRESH copy
            // (no Instantiate of an inactive object - that made the cut fail).
            go.SetActive(false);
            if (mesh == null || !mesh.isReadable) return; // no cut possible

            var copy = new GameObject("ChainLinkSlice");
            copy.transform.SetPositionAndRotation(wPos, wRot);
            copy.transform.localScale = wScale;
            copy.AddComponent<MeshFilter>().sharedMesh = mesh;
            var cmr = copy.AddComponent<MeshRenderer>();
            cmr.sharedMaterial = mat;
            cmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var hull = copy.Slice(point, normal);
            if (hull != null)
            {
                var upper = hull.CreateUpperHull(copy, crossSection);
                var lower = hull.CreateLowerHull(copy, crossSection);
                AddPiece(upper, wPos, wRot, wScale, normal, roomScale);
                AddPiece(lower, wPos, wRot, wScale, -normal, roomScale);
            }
            Object.Destroy(copy);
        }

        private static void AddPiece(GameObject piece, Vector3 pos, Quaternion rot, Vector3 scale,
            Vector3 flyDir, float roomScale)
        {
            if (piece == null) return;
            piece.transform.SetPositionAndRotation(pos, rot);
            piece.transform.localScale = scale;
            var rb = piece.AddComponent<Rigidbody>();
            // Cheap BoxCollider instead of a convex MeshCollider (see
            // NoteView.AddPiecePhysics): same visual effect, no expensive convex cook
            // per sliced link. Size = chain link (local).
            var col = piece.AddComponent<BoxCollider>();
            col.size = Vector3.one * GameConfig.ChainLinkSize;
            Vector3 dir = flyDir.sqrMagnitude > 1e-6f ? flyDir.normalized : Vector3.up;
            float s = Mathf.Max(0.01f, roomScale);
            rb.linearVelocity = dir * (GameConfig.SlicePieceSpeed * s);
            rb.angularVelocity = new Vector3(
                UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f))
                * GameConfig.SlicePieceSpin;
            Object.Destroy(piece, GameConfig.DestroyDelayTime);
        }

        // ---------- Helper functions ----------

        private static Mesh _linkMesh;
        private static bool _linkMeshLoaded;
        private static Mesh LinkMesh
        {
            get
            {
                if (_linkMeshLoaded) return _linkMesh;
                _linkMeshLoaded = true;
                var prefab = Resources.Load<GameObject>("QuestSaberDemo/Models/ChainLink");
                if (prefab != null)
                {
                    var mf = prefab.GetComponentInChildren<MeshFilter>();
                    if (mf != null) _linkMesh = mf.sharedMesh;
                }
                return _linkMesh;
            }
        }

        private void EnsureLinkObjects(int needed)
        {
            var mesh = LinkMesh;
            while (_links.Count < needed)
            {
                // ArcViewer's `ChainLink.fbx` (look as in the original). The meta is set to
                // `isReadable: 1` so that EzySlice can slice the link.
                GameObject go;
                if (mesh != null)
                {
                    go = new GameObject("ChainLink");
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>();
                }
                else
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Object.Destroy(go.GetComponent<Collider>());
                    go.transform.localScale = Vector3.one * GameConfig.ChainLinkSize;
                }
                go.name = "ChainLink";
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null) mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                // White dot on the link. IMPORTANT: ArcViewer's `Dot` sits at z = -0.235
                // (in the hole of the ring-shaped link, 0.5 × 0.087 × 0.5). In ArcViewer's
                // FREE camera it is visible; in our view from the FRONT (VR) it is
                // hidden by the front ring edge. That is why it sits here SOMEWHAT FURTHER FORWARD
                // (z = -0.27) at the previously visible size.
                var dotGo = new GameObject("Dot");
                dotGo.transform.SetParent(go.transform, false);
                dotGo.transform.localPosition = new Vector3(0f, 0f, -0.27f);
                dotGo.transform.localRotation = Quaternion.identity;
                dotGo.transform.localScale = Vector3.one * 0.12f;
                var dotMr = dotGo.AddComponent<MeshRenderer>();
                var dotMf = dotGo.AddComponent<MeshFilter>();
                dotMf.sharedMesh = NoteView.CachedDotMesh;
                dotMr.sharedMaterial = NoteView.CachedDotMaterial;
                dotMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                go.transform.SetParent(transform, false);
                go.SetActive(false);
                _links.Add(go.transform);
            }
        }

        private static Vector2 QuadBezier(Vector2 p0, Vector2 p1, Vector2 p2, float t)
        {
            float u = 1f - t;
            return u * u * p0 + 2f * u * t * p1 + t * t * p2;
        }

        private static float AngleOnQuadBezier(Vector2 p0, Vector2 p1, Vector2 p2, float t)
        {
            float u = 1f - t;
            Vector2 d = 2f * u * (p1 - p0) + 2f * t * (p2 - p1);
            if (d.sqrMagnitude < 1e-8f) return 0f;
            return Mathf.Atan2(d.x, -d.y) * Mathf.Rad2Deg;
        }

    }
}

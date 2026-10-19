using System.Collections.Generic;
using UnityEngine;

namespace QuestSaber
{
    /// <summary>
    /// Simple object pool for NoteViews (replaces the reference's Zenject memory pool).
    /// </summary>
    public static class NotePool
    {
        private static Transform _root;
        private static readonly Stack<NoteView> _free = new();

        public static void SetRoot(Transform root) => _root = root;

        public static NoteView Get(NoteModel note, Vector3 position,
            float hitTime, float njs, float reactionTime, float halfJumpDistance,
            float startY, float flipStartX, float flipYHeight)
        {
            NoteView view = _free.Count > 0 ? _free.Pop() : CreateNew();
            view.Spawn(note, position, hitTime, njs, reactionTime, halfJumpDistance,
                startY, flipStartX, flipYHeight);
            return view;
        }

        public static void Return(NoteView view)
        {
            if (view == null) return;
            view.gameObject.SetActive(false);
            _free.Push(view);
        }

        /// <summary>Return all active notes (song change): old notes would otherwise fly into the new song.</summary>
        public static void ReturnAll()
        {
            if (_root == null) return;
            var active = _root.GetComponentsInChildren<NoteView>(); // active only (pooled ones are inactive)
            for (int i = 0; i < active.Length; i++) Return(active[i]);
        }

        private static NoteView CreateNew()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Note";
            Object.Destroy(go.GetComponent<Collider>());
            go.layer = GameConfig.NoteLayer;

            var mf = go.GetComponent<MeshFilter>();
            mf.sharedMesh = NoteView.CachedBodyMesh;

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            // The arrow is baked into the note mesh (see NoteView.BakeArrowMesh) -
            // no separate arrow object anymore. For directional notes, Spawn sets the
            // "Arrowed" mesh together with the material array (0=body, 1=arrow).
            var noteView = go.AddComponent<NoteView>();

            // Dot marker (only for direction 8/9): white circle, centered on the front face,
            // diameter = 0.35 of the box. A small depth offset prevents Z-fighting with
            // the cube face. The mesh has radius 0.5 (diameter 1) -> localScale = diameter.
            var dotGo = new GameObject("Dot");
            var dotMr = dotGo.AddComponent<MeshRenderer>();
            var dotMf = dotGo.AddComponent<MeshFilter>();
            dotMf.sharedMesh = NoteView.CachedDotMesh;
            dotMr.sharedMaterial = NoteView.CachedDotMaterial;
            dotMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            dotGo.transform.SetParent(go.transform, false);
            dotGo.transform.localPosition = new Vector3(0f, 0f, -GameConfig.NoteSize * 0.5f - 0.002f);
            dotGo.transform.localScale = Vector3.one * (GameConfig.NoteSize * 0.35f);
            noteView.AttachDot(dotGo.transform);

            // false = keep local scale (1): the room root (_gameRoot) then scales
            // both the size AND the spacing of the notes uniformly. With 'true' Unity would
            // compensate the scaling (note stays the same size, spacing still scales)
            // - exactly this wrong ratio was the bug.
            if (_root != null) go.transform.SetParent(_root, false);
            return noteView;
        }
    }
}

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace QuestSaber.EditorTools
{
    /// <summary>
    /// Measures the ACTUAL outer radius of the saber grip around the saber root's Y axis
    /// (the axis the engraved grip label wraps around) so the label offset can be set to
    /// the real surface instead of a guessed constant.
    ///
    /// Hierarchy is rebuilt exactly like SaberView.Build does for the grip part:
    ///   root  : localScale (0.75, 0.5, 0.75)
    ///   grip  : localRotation Euler(270,0,0), localScale (1.25, 1.25, 2.5), Handle mesh
    /// The label surface wraps around the root Y axis on the -Z side; its radius and its
    /// anchor offset are read live from GameConfig, so the tool always reports the CURRENT
    /// gap between the engraved text and the real grip surface.
    ///
    /// Run:
    ///   Unity -batchmode -nographics -quit -executeMethod QuestSaber.EditorTools.SaberGripMeasure.Run
    /// </summary>
    public static class SaberGripMeasure
    {
        // Constants mirrored from SaberView.Build (keep in sync!).
        static readonly Vector3 RootScale = new Vector3(0.75f, 0.5f, 0.75f);
        static readonly Vector3 GripScale = new Vector3(1.25f, 1.25f, 2.5f);
        static readonly Vector3 GripRot = new Vector3(270f, 0f, 0f);
        // Current values from the game (single source of truth - GameConfig).
        static readonly Vector3 LabelAnchorLocal = new Vector3(0f, 0f, GameConfig.GripLabelAnchorZ);

        public static void Run()
        {
            var res = Resources.Load<GameObject>("QuestSaberDemo/Models/Saber");
            if (res == null)
            {
                Debug.LogError("[GripMeasure] Resources/QuestSaberDemo/Models/Saber not found.");
                return;
            }

            Mesh handle = null;
            foreach (var mf in res.GetComponentsInChildren<MeshFilter>(true))
                if (mf.name == "Handle") handle = mf.sharedMesh;
            if (handle == null)
            {
                Debug.LogError("[GripMeasure] Handle mesh not found in Saber model.");
                return;
            }

            // --- rebuild the grip transform chain (root -> grip) ---
            var root = new GameObject("__measure_root");
            try
            {
                root.transform.localScale = RootScale;
                var grip = new GameObject("Grip");
                grip.transform.SetParent(root.transform, false);
                grip.transform.localRotation = Quaternion.Euler(GripRot);
                grip.transform.localScale = GripScale;

                // Mesh vertices live in the grip's local space, so grip.localToWorldMatrix maps
                // them to world; root.worldToLocalMatrix brings them back to root-local space.
                Matrix4x4 v2root = root.transform.worldToLocalMatrix * grip.transform.localToWorldMatrix;

                Vector3[] v = handle.vertices;
                Debug.Log("[GripMeasure] Handle verts=" + v.Length + " meshBounds=" + handle.bounds);

                // Raw (unscaled) mesh bounds.
                Debug.Log("[GripMeasure] mesh bounds size=" + handle.bounds.size +
                          " center=" + handle.bounds.center);

                // Collect root-local vertices.
                var pts = new List<Vector3>(v.Length);
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                foreach (var p in v)
                {
                    Vector3 r = v2root.MultiplyPoint3x4(p);
                    pts.Add(r);
                    min = Vector3.Min(min, r);
                    max = Vector3.Max(max, r);
                }
                Debug.Log("[GripMeasure] ROOT-LOCAL bounds min=" + F(min) + " max=" + F(max) +
                          " size=" + F(max - min));

                float halfLen = Mathf.Min(Mathf.Abs(min.y), Mathf.Abs(max.y));
                Debug.Log("[GripMeasure] grip half-length along root Y (root-local) = " +
                          (halfLen * 1000f).ToString("F1") + " mm  (world " +
                          (halfLen * RootScale.y * 1000f).ToString("F1") + " mm)");

                // The grip is a straight prism along root Y, so the cross-section is the same at
                // every height - no need to restrict to the label's height. The label wraps
                // around the -Z side of the root (label anchor localPosition z = -0.025).
                Vector2 labelDir = new Vector2(0f, -1f); // root XZ, -Z side
                float cosLimit = Mathf.Cos(70f * Mathf.Deg2Rad);

                float rMax = 0f, rMin = float.MaxValue;
                float projMax = float.MinValue;
                float ringMax = 0f;   // radius within +-8 deg of the exact -Z direction
                int used = 0;
                var distinctY = new List<float>();
                foreach (var r in pts)
                {
                    if (!distinctY.Exists(y => Mathf.Abs(y - r.y) < 1e-5f)) distinctY.Add(r.y);
                    Vector2 d = new Vector2(r.x, r.z);
                    float len = d.magnitude;
                    if (len < 1e-6f) continue;
                    used++;
                    rMax = Mathf.Max(rMax, len);
                    rMin = Mathf.Min(rMin, len);
                    Vector2 n = d / len;
                    float dot = Vector2.Dot(n, labelDir);
                    if (dot >= cosLimit) projMax = Mathf.Max(projMax, len * dot);
                    if (dot >= Mathf.Cos(8f * Mathf.Deg2Rad)) ringMax = Mathf.Max(ringMax, len);
                }
                Debug.Log("[GripMeasure] distinct rootY values = " + distinctY.Count +
                          " ; radial verts = " + used);

                if (used == 0)
                {
                    Debug.LogError("[GripMeasure] no grip vertices - axis assumption wrong.");
                }
                else
                {
                    float labelRadiusRootLocal = Mathf.Abs(LabelAnchorLocal.z);
                    Debug.Log("[GripMeasure] ---- RESULT ----");
                    Debug.Log("[GripMeasure] grip cross-section radius (root-local): min=" +
                              (rMin * 1000f).ToString("F2") + " mm  max=" + (rMax * 1000f).ToString("F2") +
                              " mm  ; max along -Z (within +-70 deg)=" +
                              (projMax * 1000f).ToString("F2") + " mm ; max at -Z (+-8 deg)=" +
                              (ringMax * 1000f).ToString("F2") + " mm");
                    Debug.Log("[GripMeasure] label anchor radius (root-local) = " +
                              (labelRadiusRootLocal * 1000f).ToString("F2") + " mm");
                    Debug.Log("[GripMeasure] WORLD radius: grip=" + (rMax * RootScale.z * 1000f).ToString("F2") +
                              " mm, gripAt-Z=" + (ringMax * RootScale.z * 1000f).ToString("F2") +
                              " mm, label=" + (labelRadiusRootLocal * RootScale.z * 1000f).ToString("F2") + " mm");
                    Debug.Log("[GripMeasure] world gap along -Z = " +
                              ((labelRadiusRootLocal - ringMax) * RootScale.z * 1000f).ToString("F2") +
                              " mm   [positive = text floats above the grip]");
                    Debug.Log("[GripMeasure] to lie ON the surface: GripSurfaceRadiusWorld = " +
                              (ringMax * RootScale.z).ToString("F6") +
                              " m (= GameConfig: set GripLabelLiftWorld so that\n" +
                              "                                   GripSurfaceRadiusWorld + lift = curve radius\n" +
                              "                                   and GripLabelAnchorZ = -(that)/SaberRootScaleZ)");
                    Debug.Log("[GripMeasure] currently configured (GameConfig): anchor z=" +
                              GameConfig.GripLabelAnchorZ.ToString("F5") + " root-local (world " +
                              (-GameConfig.GripLabelAnchorZ * RootScale.z).ToString("F5") + " m), " +
                              "curveRadius=" + GameConfig.GripLabelRadiusWorld.ToString("F5") +
                              " m  => gap above the grip = " +
                              ((Mathf.Abs(GameConfig.GripLabelAnchorZ) - ringMax) * RootScale.z * 1000f)
                                  .ToString("F2") + " mm");
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static string F(Vector3 v) =>
            "(" + (v.x * 1000f).ToString("F1") + ", " + (v.y * 1000f).ToString("F1") + ", " +
            (v.z * 1000f).ToString("F1") + ") mm";
    }
}

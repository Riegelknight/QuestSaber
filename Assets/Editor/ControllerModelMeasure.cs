using UnityEngine;
using UnityEditor;

namespace QuestSaber.EditorTools
{
    /// <summary>
    /// Measures the Meta Quest Touch Plus controller model in the space of its ROOT (the
    /// transform that the game parents to the controller anchor). Answers the question that
    /// decides the whole controller alignment:
    ///
    ///   Where does the model sit relative to its own origin?
    ///
    ///  - centroid/AABB centre at ~0 -> the origin is INSIDE the model (grip-centred) -> the
    ///    model is authored for the OpenXR GRIP pose.
    ///  - centroid/AABB centre clearly BEHIND the origin (negative Z, i.e. -forward) -> the
    ///    origin is at the FRONT/tip (the "aim" point) -> the model is authored for the AIM pose.
    ///
    /// Also prints the long axis direction, so "the model is turned" can be checked instead
    /// of guessed. Run:
    ///   Unity -batchmode -nographics -quit -executeMethod QuestSaber.EditorTools.ControllerModelMeasure.Run
    /// </summary>
    public static class ControllerModelMeasure
    {
        public static void Run()
        {
            string[] paths =
            {
                "Assets/Resources/QuestControllers/LeftControllerModel.prefab",
                "Assets/Resources/QuestControllers/RightControllerModel.prefab",
            };

            foreach (string p in paths)
            {
                var src = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (src == null)
                {
                    Debug.Log("[CtrlMeasure] MISSING " + p);
                    continue;
                }

                var inst = Object.Instantiate(src);
                try
                {
                    var root = inst.transform;
                    Matrix4x4 toRoot = root.worldToLocalMatrix;

                    var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    Vector3 sum = Vector3.zero;
                    int count = 0;

                    foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh m = null;
                        if (r is SkinnedMeshRenderer smr) m = smr.sharedMesh;
                        else
                        {
                            var mf = r.GetComponent<MeshFilter>();
                            if (mf != null) m = mf.sharedMesh;
                        }
                        if (m == null) continue;

                        Matrix4x4 v2root = toRoot * r.transform.localToWorldMatrix;
                        var verts = m.vertices;
                        for (int i = 0; i < verts.Length; i++)
                        {
                            Vector3 v = v2root.MultiplyPoint3x4(verts[i]);
                            min = Vector3.Min(min, v);
                            max = Vector3.Max(max, v);
                            sum += v;
                            count++;
                        }
                    }

                    if (count == 0)
                    {
                        Debug.Log("[CtrlMeasure] " + System.IO.Path.GetFileName(p) + ": no vertices");
                        continue;
                    }

                    Vector3 centroid = sum / count;
                    Vector3 size = max - min;
                    Vector3 center = (min + max) * 0.5f;
                    Vector3 longAxis = LongAxis(inst, toRoot, centroid);

                    Debug.Log("[CtrlMeasure] " + System.IO.Path.GetFileName(p));
                    Debug.Log("    verts=" + count +
                              "  AABB size(mm)=" + Mm(size) +
                              "  AABB min(mm)=" + Mm(min) + "  max(mm)=" + Mm(max));
                    Debug.Log("    AABB center(mm)=" + Mm(center) +
                              "  vertex centroid(mm)=" + Mm(centroid) +
                              "  origin->centroid(mm)=" + Mm(centroid));
                    Debug.Log("    long axis (root-local dir)=" + longAxis.ToString("F3") +
                              "  origin->centroid . longAxis = " +
                              (Vector3.Dot(centroid, longAxis) * 1000f).ToString("F1") + " mm");
                    Debug.Log("    => origin is " +
                              (Vector3.Dot(centroid, longAxis) < -0.015f
                                  ? "IN FRONT of the body (aim/tip origin)"
                                  : Vector3.Dot(centroid, longAxis) > 0.015f
                                      ? "BEHIND the body (grip/handle origin)"
                                      : "INSIDE the body"));
                }
                finally
                {
                    Object.DestroyImmediate(inst);
                }
            }

            Debug.Log("[CtrlMeasure] reference: real Quest 3 Touch Plus = 126 x 67 x 43 mm");
        }

        private static Vector3 LongAxis(GameObject inst, Matrix4x4 toRoot, Vector3 centroid)
        {
            // covariance of all vertices (root space) -> dominant eigenvector via power iteration
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                Mesh m = null;
                if (r is SkinnedMeshRenderer smr) m = smr.sharedMesh;
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) m = mf.sharedMesh;
                }
                if (m == null) continue;

                Matrix4x4 v2root = toRoot * r.transform.localToWorldMatrix;
                var verts = m.vertices;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 d = v2root.MultiplyPoint3x4(verts[i]) - centroid;
                    xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z;
                    yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
                }
            }

            Vector3 v = Vector3.forward;
            for (int it = 0; it < 128; it++)
            {
                Vector3 n = new Vector3(xx * v.x + xy * v.y + xz * v.z,
                                        xy * v.x + yy * v.y + yz * v.z,
                                        xz * v.x + yz * v.y + zz * v.z);
                if (n.sqrMagnitude < 1e-20f) break;
                v = n.normalized;
            }
            return v;
        }

        private static string Mm(Vector3 v) =>
            "(" + (v.x * 1000f).ToString("F1") + ", " + (v.y * 1000f).ToString("F1") + ", " +
            (v.z * 1000f).ToString("F1") + ")";
    }
}

using UnityEngine;
using UnityEditor;

namespace QuestSaber.EditorTools
{
    /// <summary>
    /// Measures the true (oriented) size of the Meta Quest Touch Plus controller meshes via
    /// principal axes, so it can be compared with the real controller (126 x 67 x 43 mm).
    /// The axis-aligned bounds of the mesh are misleading when the model is tilted.
    /// Run:
    ///   Unity -batchmode -nographics -quit -executeMethod QuestSaber.EditorTools.MeshBoundsDump.Run
    /// </summary>
    public static class MeshBoundsDump
    {
        public static void Run()
        {
            string[] paths =
            {
                "Assets/Resources/QuestControllers/RightControllerModel.prefab",
                "Assets/Resources/QuestControllers/LeftControllerModel.prefab",
            };

            foreach (string p in paths)
            {
                var src = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (src == null)
                {
                    Debug.Log("[BoundsDump] MISSING " + p);
                    continue;
                }
                var inst = Object.Instantiate(src);
                try
                {
                    float s = inst.transform.lossyScale.x;
                    foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        Report(p, smr.name, smr.sharedMesh, s);
                    foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
                        Report(p, mf.name, mf.sharedMesh, s);
                }
                finally
                {
                    Object.DestroyImmediate(inst);
                }
            }
            Debug.Log("[BoundsDump] REAL Quest 3 Touch Plus = 126 x 67 x 43 mm " +
                      "(length x width x thickness)");
        }

        private static void Report(string owner, string node, Mesh m, float s)
        {
            if (m == null) return;
            Vector3[] v = m.vertices;
            if (v == null || v.Length < 8) return;

            // center
            Vector3 c = Vector3.zero;
            for (int i = 0; i < v.Length; i++) c += v[i];
            c /= v.Length;

            // covariance
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            for (int i = 0; i < v.Length; i++)
            {
                float dx = v[i].x - c.x, dy = v[i].y - c.y, dz = v[i].z - c.z;
                xx += dx * dx; xy += dx * dy; xz += dx * dz;
                yy += dy * dy; yz += dy * dz; zz += dz * dz;
            }

            Vector3[] axes = new Vector3[3];
            Vector3 a0 = Power(xx, xy, xz, yy, yz, zz, new Vector3(1, 0, 0));
            axes[0] = a0.normalized;
            Deflate(ref xx, ref xy, ref xz, ref yy, ref yz, ref zz, axes[0]);
            Vector3 a1 = Power(xx, xy, xz, yy, yz, zz, new Vector3(0, 1, 0));
            // orthogonalize against axis 0
            a1 = (a1 - axes[0] * Vector3.Dot(a1, axes[0]));
            if (a1.sqrMagnitude < 1e-12f) a1 = Vector3.Cross(axes[0], Vector3.up);
            axes[1] = a1.normalized;
            axes[2] = Vector3.Cross(axes[0], axes[1]).normalized;

            float[] min = { float.MaxValue, float.MaxValue, float.MaxValue };
            float[] max = { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 d = v[i] - c;
                float[] pr = { Vector3.Dot(d, axes[0]), Vector3.Dot(d, axes[1]), Vector3.Dot(d, axes[2]) };
                for (int k = 0; k < 3; k++)
                {
                    if (pr[k] < min[k]) min[k] = pr[k];
                    if (pr[k] > max[k]) max[k] = pr[k];
                }
            }

            float[] ext =
            {
                (max[0] - min[0]) * s * 1000f,
                (max[1] - min[1]) * s * 1000f,
                (max[2] - min[2]) * s * 1000f,
            };
            System.Array.Sort(ext);
            System.Array.Reverse(ext); // descending: length, width, thickness

            Debug.Log("[BoundsDump] " + System.IO.Path.GetFileName(owner) + "/" + node +
                      " oriented(mm)=" + ext[0].ToString("F0") + " x " + ext[1].ToString("F0") +
                      " x " + ext[2].ToString("F0") + " | verts=" + v.Length +
                      " | rootScale=" + s.ToString("F3"));
        }

        // power iteration for the dominant eigenvector of the 3x3 symmetric covariance
        private static Vector3 Power(float xx, float xy, float xz, float yy, float yz, float zz, Vector3 start)
        {
            Vector3 v = start;
            for (int it = 0; it < 64; it++)
            {
                Vector3 n = new Vector3(
                    xx * v.x + xy * v.y + xz * v.z,
                    xy * v.x + yy * v.y + yz * v.z,
                    xz * v.x + yz * v.y + zz * v.z);
                if (n.sqrMagnitude < 1e-20f) break;
                v = n.normalized;
            }
            return v;
        }

        // subtract the component along a principal direction (simple deflation)
        private static void Deflate(ref float xx, ref float xy, ref float xz,
            ref float yy, ref float yz, ref float zz, Vector3 a)
        {
            // C' = C - lambda * a a^T , lambda = a^T C a
            float l = xx * a.x * a.x + yy * a.y * a.y + zz * a.z * a.z
                      + 2f * (xy * a.x * a.y + xz * a.x * a.z + yz * a.y * a.z);
            xx -= l * a.x * a.x; xy -= l * a.x * a.y; xz -= l * a.x * a.z;
            yy -= l * a.y * a.y; yz -= l * a.y * a.z; zz -= l * a.z * a.z;
        }
    }
}

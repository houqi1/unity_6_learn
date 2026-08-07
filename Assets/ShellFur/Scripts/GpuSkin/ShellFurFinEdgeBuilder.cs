using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds GPU fin edge table from a bind-pose fur mesh (same vertex indexing as skinned buffer).
/// Used by CS B2 silhouette fin generation — no mesh geometry, only edge connectivity.
/// </summary>
public static class ShellFurFinEdgeBuilder
{
    struct EdgeInfo
    {
        public int v0, v1;
        public int faceA, faceB;
        public bool hasB;
    }

    /// <param name="bindMesh">Compact fur bind mesh (submeshes already selected).</param>
    /// <param name="skipCoplanarDot">Drop nearly coplanar manifold edges (same as static fins).</param>
    /// <param name="minEdgeLength">Skip degenerate edges in bind pose.</param>
    public static ShellFurGpuSkinTypes.FinEdge[] Build(
        Mesh bindMesh,
        float skipCoplanarDot = 0.9998f,
        float minEdgeLength = 1e-5f)
    {
        if (bindMesh == null)
            return null;

        Vector3[] positions = bindMesh.vertices;
        int[] tris = bindMesh.triangles;
        if (positions == null || positions.Length == 0 || tris == null || tris.Length < 3)
            return null;

        int triCount = tris.Length / 3;
        var faceNormals = new Vector3[triCount];
        for (int t = 0; t < triCount; t++)
        {
            int i0 = tris[t * 3];
            int i1 = tris[t * 3 + 1];
            int i2 = tris[t * 3 + 2];
            Vector3 e1 = positions[i1] - positions[i0];
            Vector3 e2 = positions[i2] - positions[i0];
            Vector3 n = Vector3.Cross(e1, e2);
            faceNormals[t] = n.sqrMagnitude > 1e-20f ? n.normalized : Vector3.up;
        }

        var edges = new Dictionary<long, EdgeInfo>(triCount * 2);

        void AddEdge(int a, int b, int face)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            long key = ((long)lo << 32) | (uint)hi;

            if (edges.TryGetValue(key, out EdgeInfo e))
            {
                if (!e.hasB)
                {
                    e.faceB = face;
                    e.hasB = true;
                    edges[key] = e;
                }
            }
            else
            {
                edges[key] = new EdgeInfo
                {
                    v0 = lo,
                    v1 = hi,
                    faceA = face,
                    faceB = -1,
                    hasB = false
                };
            }
        }

        for (int t = 0; t < triCount; t++)
        {
            int i0 = tris[t * 3];
            int i1 = tris[t * 3 + 1];
            int i2 = tris[t * 3 + 2];
            AddEdge(i0, i1, t);
            AddEdge(i1, i2, t);
            AddEdge(i2, i0, t);
        }

        float minLenSq = minEdgeLength * minEdgeLength;
        var list = new List<ShellFurGpuSkinTypes.FinEdge>(edges.Count);

        foreach (var kv in edges)
        {
            EdgeInfo e = kv.Value;
            Vector3 nA = faceNormals[e.faceA];
            Vector3 nB = e.hasB ? faceNormals[e.faceB] : nA;

            if (e.hasB && Vector3.Dot(nA, nB) > skipCoplanarDot)
                continue;

            Vector3 p0 = positions[e.v0];
            Vector3 p1 = positions[e.v1];
            if ((p1 - p0).sqrMagnitude < minLenSq)
                continue;

            int a0 = tris[e.faceA * 3];
            int a1 = tris[e.faceA * 3 + 1];
            int a2 = tris[e.faceA * 3 + 2];

            uint b0, b1, b2;
            uint flags = 0;
            if (e.hasB)
            {
                b0 = (uint)tris[e.faceB * 3];
                b1 = (uint)tris[e.faceB * 3 + 1];
                b2 = (uint)tris[e.faceB * 3 + 2];
                flags = ShellFurGpuSkinTypes.FinEdge.FlagHasB;
            }
            else
            {
                // Boundary: reuse face A so GPU path can treat as single-face graze.
                b0 = (uint)a0;
                b1 = (uint)a1;
                b2 = (uint)a2;
            }

            list.Add(new ShellFurGpuSkinTypes.FinEdge
            {
                v0 = (uint)e.v0,
                v1 = (uint)e.v1,
                a0 = (uint)a0,
                a1 = (uint)a1,
                a2 = (uint)a2,
                b0 = b0,
                b1 = b1,
                b2 = b2,
                flags = flags,
                pad = 0
            });
        }

        return list.Count > 0 ? list.ToArray() : null;
    }
}

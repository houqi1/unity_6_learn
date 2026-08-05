using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds multi-segment fin strips from source mesh edges (scheme 3).
/// Each manifold edge becomes a height stack of quads (segments along the strand).
/// Runtime VS extrudes each row by UV3.x height and applies gravity ∝ h² so the strip droops as a curve.
/// UV1 = face normal A, UV2 = face normal B, UV3.x = height 0..1.
/// </summary>
public static class ShellFurFinBuilder
{
    public const int MinSegments = 1;
    public const int MaxSegments = 16;

    struct EdgeInfo
    {
        public int v0;
        public int v1;
        public int faceA;
        public int faceB;
        public bool hasB;
    }

    /// <param name="segments">
    /// Number of quads stacked from root to tip (1 = single flat fin, 4+ recommended for gravity curves).
    /// </param>
    /// <param name="submeshIndex">
    /// &lt; 0 uses all submeshes; ≥ 0 builds fins only from that material slot / submesh.
    /// </param>
    public static Mesh Build(
        Mesh source,
        float minEdgeLength = 1e-5f,
        float skipCoplanarDot = 0.9998f,
        int segments = 4,
        int submeshIndex = -1)
    {
        int[] slots = submeshIndex < 0 ? null : new[] { submeshIndex };
        return Build(source, minEdgeLength, skipCoplanarDot, segments, slots);
    }

    /// <param name="submeshIndices">
    /// null/empty = entire mesh; otherwise only those material slots / submeshes (merged).
    /// </param>
    public static Mesh Build(
        Mesh source,
        float minEdgeLength,
        float skipCoplanarDot,
        int segments,
        int[] submeshIndices)
    {
        if (source == null)
            throw new System.ArgumentNullException(nameof(source));

        if (!source.isReadable)
        {
            Debug.LogError($"[ShellFurFinBuilder] Mesh '{source.name}' is not readable. Enable Read/Write in import settings.");
            return null;
        }

        segments = Mathf.Clamp(segments, MinSegments, MaxSegments);
        int rows = segments + 1;

        Vector3[] positions = source.vertices;
        Vector3[] normals = source.normals;
        Vector2[] uvs = source.uv;
        int[] tris = GetTriangles(source, submeshIndices);

        if (positions == null || positions.Length == 0 || tris == null || tris.Length < 3)
            return null;

        if (normals == null || normals.Length != positions.Length)
        {
            normals = new Vector3[positions.Length];
            for (int i = 0; i < positions.Length; i++)
                normals[i] = Vector3.up;
        }

        if (uvs == null || uvs.Length != positions.Length)
            uvs = new Vector2[positions.Length];

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

        int edgeCap = edges.Count;
        var finVerts = new List<Vector3>(edgeCap * rows * 2);
        var finNormals = new List<Vector3>(edgeCap * rows * 2);
        var finUV0 = new List<Vector2>(edgeCap * rows * 2);
        var finUV1 = new List<Vector3>(edgeCap * rows * 2);
        var finUV2 = new List<Vector3>(edgeCap * rows * 2);
        var finUV3 = new List<Vector2>(edgeCap * rows * 2);
        var finIndices = new List<int>(edgeCap * segments * 6);

        float minLenSq = minEdgeLength * minEdgeLength;

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

            Vector3 up0 = normals[e.v0];
            Vector3 up1 = normals[e.v1];
            if (up0.sqrMagnitude < 1e-12f) up0 = nA;
            if (up1.sqrMagnitude < 1e-12f) up1 = nA;
            up0.Normalize();
            up1.Normalize();

            Vector2 uv0 = uvs[e.v0];
            Vector2 uv1 = uvs[e.v1];

            int baseIndex = finVerts.Count;

            // rows of (left, right) vertices; height = r / segments.
            // Positions stay on the surface; VS extrudes + applies gravity per height.
            for (int r = 0; r < rows; r++)
            {
                float h = segments > 0 ? (float)r / segments : 0f;

                finVerts.Add(p0);
                finVerts.Add(p1);

                finNormals.Add(up0);
                finNormals.Add(up1);

                finUV0.Add(uv0);
                finUV0.Add(uv1);

                finUV1.Add(nA);
                finUV1.Add(nA);
                finUV2.Add(nB);
                finUV2.Add(nB);

                finUV3.Add(new Vector2(h, 0f));
                finUV3.Add(new Vector2(h, 0f));
            }

            // Stacked quads between consecutive height rows.
            for (int s = 0; s < segments; s++)
            {
                int i0 = baseIndex + s * 2;
                int i1 = i0 + 1;
                int i2 = baseIndex + (s + 1) * 2 + 1;
                int i3 = baseIndex + (s + 1) * 2;

                finIndices.Add(i0);
                finIndices.Add(i1);
                finIndices.Add(i2);
                finIndices.Add(i0);
                finIndices.Add(i2);
                finIndices.Add(i3);
            }
        }

        if (finIndices.Count == 0)
        {
            Debug.LogWarning($"[ShellFurFinBuilder] No fin edges generated for '{source.name}'.");
            return null;
        }

        var mesh = new Mesh
        {
            name = $"{source.name}_ShellFurFins_s{segments}",
            indexFormat = finVerts.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
        };

        mesh.SetVertices(finVerts);
        mesh.SetNormals(finNormals);
        mesh.SetUVs(0, finUV0);
        mesh.SetUVs(1, finUV1);
        mesh.SetUVs(2, finUV2);
        mesh.SetUVs(3, finUV3);
        mesh.SetTriangles(finIndices, 0, true);
        mesh.RecalculateBounds();

        // Room for extrusion + gravity droop at runtime.
        Bounds b = mesh.bounds;
        b.Expand(b.size.magnitude * 0.35f + 0.08f);
        mesh.bounds = b;
        mesh.UploadMeshData(false);

        return mesh;
    }

    /// <summary>
    /// Returns triangle indices for the whole mesh, or a single submesh when index ≥ 0.
    /// </summary>
    public static int[] GetTriangles(Mesh source, int submeshIndex)
    {
        if (source == null)
            return null;

        if (submeshIndex < 0)
            return source.triangles;

        return GetTriangles(source, new[] { submeshIndex });
    }

    /// <summary>
    /// Whole mesh if indices null/empty; otherwise concatenates selected submeshes.
    /// </summary>
    public static int[] GetTriangles(Mesh source, int[] submeshIndices)
    {
        if (source == null)
            return null;

        if (submeshIndices == null || submeshIndices.Length == 0)
            return source.triangles;

        var list = new List<int>(256);
        int subCount = source.subMeshCount;
        for (int i = 0; i < submeshIndices.Length; i++)
        {
            int sm = submeshIndices[i];
            if (sm < 0 || sm >= subCount)
            {
                Debug.LogError($"[ShellFurFinBuilder] Submesh {sm} out of range (count={subCount}) on '{source.name}'.");
                continue;
            }

            int[] part = source.GetTriangles(sm);
            if (part != null && part.Length > 0)
                list.AddRange(part);
        }

        return list.Count > 0 ? list.ToArray() : null;
    }
}

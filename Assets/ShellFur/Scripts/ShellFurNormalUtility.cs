using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// True smooth-normal bake for shell fur extrusion.
///
/// Critical: Unity/FBX meshes split vertices at UV seams and hard edges. Averaging only by
/// vertex *index* does almost nothing on split meshes. Real smooth shading groups vertices
/// that share the same *position* (weld-by-position), then averages adjacent face normals.
///
/// Encoding: RGB = normalOS * 0.5 + 0.5 (object space).
/// References:
/// - Average face normals per shared position (smooth shading)
/// - Angle-weighted corner contribution (ByteHazard / common mesh tools)
/// - Optional max face-angle threshold (Three.js / DCC "auto smooth")
/// </summary>
public static class ShellFurNormalUtility
{
    const float DefaultPositionEpsilon = 1e-5f;

    public static Color EncodeNormalOS(Vector3 n)
    {
        n = n.normalized;
        return new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
    }

    public static Vector3 DecodeNormalOS(Color c)
    {
        return new Vector3(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f).normalized;
    }

    /// <param name="positions">Mesh vertices (object space).</param>
    /// <param name="triangles">Triangle indices (any submesh selection already merged).</param>
    /// <param name="maxSmoothingAngleDeg">
    /// Faces whose normals differ by more than this angle do not contribute to each other
    /// at a shared position (like Blender Auto Smooth). Use 180 for fully smooth.
    /// </param>
    /// <param name="positionEpsilon">Weld tolerance for treating positions as identical.</param>
    public static Vector3[] ComputeSmoothNormals(
        Vector3[] positions,
        int[] triangles,
        float maxSmoothingAngleDeg = 180f,
        float positionEpsilon = DefaultPositionEpsilon)
    {
        if (positions == null || positions.Length == 0)
            return System.Array.Empty<Vector3>();

        var result = new Vector3[positions.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = Vector3.up;

        if (triangles == null || triangles.Length < 3)
            return result;

        // --- Pass 1: face normals + area ---
        int triCount = triangles.Length / 3;
        var faceNormals = new Vector3[triCount];
        var faceValid = new bool[triCount];

        for (int t = 0; t < triCount; t++)
        {
            int i0 = triangles[t * 3];
            int i1 = triangles[t * 3 + 1];
            int i2 = triangles[t * 3 + 2];
            if ((uint)i0 >= (uint)positions.Length ||
                (uint)i1 >= (uint)positions.Length ||
                (uint)i2 >= (uint)positions.Length)
                continue;

            Vector3 p0 = positions[i0];
            Vector3 p1 = positions[i1];
            Vector3 p2 = positions[i2];
            Vector3 fn = Vector3.Cross(p1 - p0, p2 - p0);
            float mag = fn.magnitude;
            if (mag < 1e-20f)
                continue;

            faceNormals[t] = fn / mag;
            faceValid[t] = true;
        }

        // --- Pass 2: weld vertices by position ---
        // Map each vertex index -> weld group id; accumulate face contribs per group.
        float invEps = 1f / Mathf.Max(positionEpsilon, 1e-8f);
        var cellToGroup = new Dictionary<long, int>(positions.Length);
        var vertexGroup = new int[positions.Length];
        var groupPositions = new List<Vector3>(positions.Length / 2 + 8);

        float eps2 = positionEpsilon * positionEpsilon;
        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 p = positions[i];
            long key = QuantizeKey(p, invEps);
            int g = -1;
            if (cellToGroup.TryGetValue(key, out int hit) &&
                (groupPositions[hit] - p).sqrMagnitude <= eps2)
            {
                g = hit;
            }
            else
            {
                // Probe nearby cells for epsilon neighbors (handles grid boundary + hash collisions).
                g = FindNearbyGroup(cellToGroup, groupPositions, p, positionEpsilon, invEps);
                if (g < 0)
                {
                    g = groupPositions.Count;
                    groupPositions.Add(p);
                    cellToGroup[key] = g;
                }
            }
            vertexGroup[i] = g;
        }

        int groupCount = groupPositions.Count;
        var groupAccum = new Vector3[groupCount];

        // --- Pass 3: accumulate angle-weighted face normals into position groups ---
        // For hard-edge control: if maxSmoothingAngle < 180, only mix faces whose
        // normals are within that angle (stored per-vertex via second pass).
        bool useAngleLimit = maxSmoothingAngleDeg < 179.5f;
        float cosThreshold = Mathf.Cos(maxSmoothingAngleDeg * Mathf.Deg2Rad);

        if (!useAngleLimit)
        {
            // Fully smooth: one averaged normal per world position.
            for (int t = 0; t < triCount; t++)
            {
                if (!faceValid[t])
                    continue;

                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                Vector3 p0 = positions[i0];
                Vector3 p1 = positions[i1];
                Vector3 p2 = positions[i2];
                Vector3 fn = faceNormals[t];

                float w0 = AngleWeight(p0, p1, p2);
                float w1 = AngleWeight(p1, p2, p0);
                float w2 = AngleWeight(p2, p0, p1);

                groupAccum[vertexGroup[i0]] += fn * w0;
                groupAccum[vertexGroup[i1]] += fn * w1;
                groupAccum[vertexGroup[i2]] += fn * w2;
            }

            for (int g = 0; g < groupCount; g++)
            {
                if (groupAccum[g].sqrMagnitude < 1e-20f)
                    groupAccum[g] = Vector3.up;
                else
                    groupAccum[g].Normalize();
            }

            for (int i = 0; i < positions.Length; i++)
                result[i] = groupAccum[vertexGroup[i]];
        }
        else
        {
            // Angle-limited: for each vertex corner, average only faces at the same
            // position whose face normal is within threshold of this face normal.
            // Build per-group list of (faceNormal, angleWeight, vertexIndex) contributions.
            var groupFaces = new List<(Vector3 n, float w, int vi)>[groupCount];
            for (int g = 0; g < groupCount; g++)
                groupFaces[g] = new List<(Vector3, float, int)>(8);

            for (int t = 0; t < triCount; t++)
            {
                if (!faceValid[t])
                    continue;

                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                Vector3 p0 = positions[i0];
                Vector3 p1 = positions[i1];
                Vector3 p2 = positions[i2];
                Vector3 fn = faceNormals[t];

                groupFaces[vertexGroup[i0]].Add((fn, AngleWeight(p0, p1, p2), i0));
                groupFaces[vertexGroup[i1]].Add((fn, AngleWeight(p1, p2, p0), i1));
                groupFaces[vertexGroup[i2]].Add((fn, AngleWeight(p2, p0, p1), i2));
            }

            // For each vertex, use its own incident face normals filtered by angle
            // against each contribution's face (per-corner smooth normal).
            // Simpler robust approach matching DCC auto-smooth:
            // For each vertex index, take all faces at same position; include those
            // within threshold of the *area-weighted average of faces that touch this vertex index*.
            for (int i = 0; i < positions.Length; i++)
            {
                var list = groupFaces[vertexGroup[i]];
                if (list.Count == 0)
                {
                    result[i] = Vector3.up;
                    continue;
                }

                // Seed: average of faces that explicitly reference this vertex index.
                Vector3 seed = Vector3.zero;
                int seedCount = 0;
                for (int k = 0; k < list.Count; k++)
                {
                    if (list[k].vi == i)
                    {
                        seed += list[k].n * list[k].w;
                        seedCount++;
                    }
                }
                if (seedCount == 0)
                {
                    for (int k = 0; k < list.Count; k++)
                        seed += list[k].n * list[k].w;
                }
                if (seed.sqrMagnitude < 1e-20f)
                {
                    result[i] = Vector3.up;
                    continue;
                }
                seed.Normalize();

                Vector3 accum = Vector3.zero;
                for (int k = 0; k < list.Count; k++)
                {
                    if (Vector3.Dot(seed, list[k].n) >= cosThreshold)
                        accum += list[k].n * list[k].w;
                }

                if (accum.sqrMagnitude < 1e-20f)
                    result[i] = seed;
                else
                    result[i] = accum.normalized;
            }
        }

        return result;
    }

    static float AngleWeight(Vector3 apex, Vector3 b, Vector3 c)
    {
        Vector3 u = b - apex;
        Vector3 v = c - apex;
        float lu = u.magnitude;
        float lv = v.magnitude;
        if (lu < 1e-12f || lv < 1e-12f)
            return 0f;
        float d = Mathf.Clamp(Vector3.Dot(u / lu, v / lv), -1f, 1f);
        return Mathf.Acos(d);
    }

    static long QuantizeKey(Vector3 p, float invEps)
    {
        // 21 bits per axis packed into 63 bits
        int ix = Mathf.FloorToInt(p.x * invEps);
        int iy = Mathf.FloorToInt(p.y * invEps);
        int iz = Mathf.FloorToInt(p.z * invEps);
        // Use unchecked hash combine (not perfect spatial hash but fast)
        unchecked
        {
            return ((long)(ix * 73856093)) ^ ((long)(iy * 19349663) << 21) ^ ((long)(iz * 83492791) << 42);
        }
    }

    static int FindNearbyGroup(
        Dictionary<long, int> cellToGroup,
        List<Vector3> groupPositions,
        Vector3 p,
        float eps,
        float invEps)
    {
        int ix = Mathf.FloorToInt(p.x * invEps);
        int iy = Mathf.FloorToInt(p.y * invEps);
        int iz = Mathf.FloorToInt(p.z * invEps);
        float eps2 = eps * eps;

        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            long key;
            unchecked
            {
                key = ((long)((ix + dx) * 73856093)) ^
                      ((long)((iy + dy) * 19349663) << 21) ^
                      ((long)((iz + dz) * 83492791) << 42);
            }
            if (!cellToGroup.TryGetValue(key, out int g))
                continue;
            if ((groupPositions[g] - p).sqrMagnitude <= eps2)
                return g;
        }
        return -1;
    }

    /// <summary>
    /// Write smooth normals into mesh vertex colors (object space, encoded).
    /// </summary>
    /// <param name="maxSmoothingAngleDeg">180 = fully smooth across position welds.</param>
    public static void BakeSmoothNormalsToVertexColors(
        Mesh mesh,
        int[] submeshIndices = null,
        float maxSmoothingAngleDeg = 180f,
        float positionEpsilon = DefaultPositionEpsilon)
    {
        if (mesh == null)
            throw new System.ArgumentNullException(nameof(mesh));

        Vector3[] positions = mesh.vertices;
        int[] tris = ShellFurFinBuilder.GetTriangles(mesh, submeshIndices);
        Vector3[] smooth = ComputeSmoothNormals(positions, tris, maxSmoothingAngleDeg, positionEpsilon);

        var colors = new Color[positions.Length];
        Color[] existing = mesh.colors;
        bool hasExisting = existing != null && existing.Length == positions.Length;

        var touched = new bool[positions.Length];
        if (tris != null)
        {
            for (int i = 0; i < tris.Length; i++)
            {
                int vi = tris[i];
                if ((uint)vi < (uint)touched.Length)
                    touched[vi] = true;
            }
        }

        bool allSubmeshes = submeshIndices == null || submeshIndices.Length == 0;
        for (int i = 0; i < positions.Length; i++)
        {
            if (touched[i] || allSubmeshes)
                colors[i] = EncodeNormalOS(smooth[i]);
            else if (hasExisting)
                colors[i] = existing[i];
            else
                colors[i] = EncodeNormalOS(Vector3.up);
        }

        mesh.colors = colors;
    }

    public static void BakeSmoothNormalsToVertexColorsRuntime(
        Mesh mesh,
        int[] submeshIndices = null,
        float maxSmoothingAngleDeg = 180f)
    {
        if (mesh == null || mesh.vertexCount == 0)
            return;
        BakeSmoothNormalsToVertexColors(mesh, submeshIndices, maxSmoothingAngleDeg);
    }
}

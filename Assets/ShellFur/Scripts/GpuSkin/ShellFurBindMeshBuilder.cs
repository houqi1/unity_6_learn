using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds a bind-pose fur mesh + BindVertex buffer payload for GPU skinning.
/// Extracts selected submeshes, packs bone weights, bakes position-welded smooth normals.
/// </summary>
public static class ShellFurBindMeshBuilder
{
    public struct BuildResult
    {
        public Mesh mesh;
        public ShellFurGpuSkinTypes.BindVertex[] bindVertices;
        public int boneCount;
    }

    /// <param name="submeshIndices">null/empty = all submeshes</param>
    public static BuildResult Build(Mesh source, int[] submeshIndices, float smoothAngleDeg = 180f)
    {
        var result = new BuildResult();
        if (source == null)
            return result;

        if (!source.isReadable)
        {
            Debug.LogError($"[ShellFurBindMeshBuilder] Mesh '{source.name}' is not readable.");
            return result;
        }

        Vector3[] srcPos = source.vertices;
        Vector3[] srcNrm = source.normals;
        Vector2[] srcUv = source.uv;
        BoneWeight[] srcBw = source.boneWeights;
        Matrix4x4[] bindPoses = source.bindposes;

        if (srcPos == null || srcPos.Length == 0)
            return result;

        if (srcNrm == null || srcNrm.Length != srcPos.Length)
        {
            srcNrm = new Vector3[srcPos.Length];
            for (int i = 0; i < srcNrm.Length; i++)
                srcNrm[i] = Vector3.up;
        }

        if (srcUv == null || srcUv.Length != srcPos.Length)
            srcUv = new Vector2[srcPos.Length];

        if (srcBw == null || srcBw.Length != srcPos.Length)
        {
            Debug.LogError($"[ShellFurBindMeshBuilder] Mesh '{source.name}' has no bone weights.");
            return result;
        }

        int[] srcTris = ShellFurFinBuilder.GetTriangles(source, submeshIndices);
        if (srcTris == null || srcTris.Length < 3)
        {
            Debug.LogError("[ShellFurBindMeshBuilder] No triangles for selected submeshes.");
            return result;
        }

        // Compact used vertices
        var remap = new Dictionary<int, int>(srcPos.Length / 4);
        var newPos = new List<Vector3>();
        var newNrm = new List<Vector3>();
        var newUv = new List<Vector2>();
        var newBw = new List<BoneWeight>();
        var newTris = new List<int>(srcTris.Length);

        int Remap(int oldIndex)
        {
            if (remap.TryGetValue(oldIndex, out int ni))
                return ni;
            ni = newPos.Count;
            remap[oldIndex] = ni;
            newPos.Add(srcPos[oldIndex]);
            newNrm.Add(srcNrm[oldIndex]);
            newUv.Add(srcUv[oldIndex]);
            newBw.Add(srcBw[oldIndex]);
            return ni;
        }

        for (int i = 0; i < srcTris.Length; i += 3)
        {
            newTris.Add(Remap(srcTris[i]));
            newTris.Add(Remap(srcTris[i + 1]));
            newTris.Add(Remap(srcTris[i + 2]));
        }

        Vector3[] posArr = newPos.ToArray();
        int[] triArr = newTris.ToArray();
        Vector3[] smooth = ShellFurNormalUtility.ComputeSmoothNormals(posArr, triArr, smoothAngleDeg);

        var mesh = new Mesh
        {
            name = source.name + "_FurGpuSkin",
            indexFormat = newPos.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
        };
        mesh.SetVertices(newPos);
        mesh.SetNormals(newNrm);
        mesh.SetUVs(0, newUv);
        mesh.SetTriangles(triArr, 0, true);

        // Also store smooth in UV3 for optional CPU debug; GPU path uses bind buffer.
        var smoothUv = new List<Vector3>(smooth.Length);
        for (int i = 0; i < smooth.Length; i++)
            smoothUv.Add(smooth[i]);
        mesh.SetUVs(3, smoothUv);

        // Pack weights into UV1/UV2 for shader fallback paths
        var wUv = new List<Vector4>(newBw.Count);
        var iUv = new List<Vector4>(newBw.Count);
        for (int i = 0; i < newBw.Count; i++)
        {
            BoneWeight bw = newBw[i];
            wUv.Add(new Vector4(bw.weight0, bw.weight1, bw.weight2, bw.weight3));
            iUv.Add(new Vector4(bw.boneIndex0, bw.boneIndex1, bw.boneIndex2, bw.boneIndex3));
        }
        mesh.SetUVs(1, wUv);
        mesh.SetUVs(2, iUv);

        Bounds b = mesh.bounds;
        b.Expand(b.size.magnitude * 0.5f + 0.2f);
        mesh.bounds = b;
        mesh.UploadMeshData(false);

        var bindVerts = new ShellFurGpuSkinTypes.BindVertex[newPos.Count];
        for (int i = 0; i < newPos.Count; i++)
        {
            BoneWeight bw = newBw[i];
            bindVerts[i] = ShellFurGpuSkinTypes.BindVertex.From(
                newPos[i],
                newNrm[i].normalized,
                smooth[i].normalized,
                newUv[i],
                new Vector4(bw.weight0, bw.weight1, bw.weight2, bw.weight3),
                new Vector4(bw.boneIndex0, bw.boneIndex1, bw.boneIndex2, bw.boneIndex3));
        }

        result.mesh = mesh;
        result.bindVertices = bindVerts;
        result.boneCount = bindPoses != null ? bindPoses.Length : 0;
        return result;
    }
}

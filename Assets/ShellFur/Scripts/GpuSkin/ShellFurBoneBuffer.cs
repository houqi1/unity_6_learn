using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Uploads skin matrices: M_i = bone.localToWorld * bindPose_i (world-space skinning).
/// </summary>
public class ShellFurBoneBuffer : System.IDisposable
{
    public GraphicsBuffer Buffer { get; private set; }
    public int BoneCount { get; private set; }

    Matrix4x4[] _cache;
    static readonly int BoneMatricesId = Shader.PropertyToID("_BoneMatrices");
    static readonly int BoneCountId = Shader.PropertyToID("_BoneCount");

    public void Ensure(int boneCount)
    {
        boneCount = Mathf.Max(1, boneCount);
        if (Buffer != null && BoneCount == boneCount)
            return;

        Dispose();
        BoneCount = boneCount;
        _cache = new Matrix4x4[boneCount];
        Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, boneCount, sizeof(float) * 16);
    }

    /// <summary>
    /// Fill from SkinnedMeshRenderer bones + mesh bindposes.
    /// </summary>
    public bool UpdateFrom(SkinnedMeshRenderer smr, Mesh mesh)
    {
        if (smr == null || mesh == null)
            return false;

        Matrix4x4[] bindPoses = mesh.bindposes;
        Transform[] bones = smr.bones;
        if (bindPoses == null || bones == null || bindPoses.Length == 0)
            return false;

        int count = Mathf.Min(bindPoses.Length, bones.Length);
        Ensure(count);

        for (int i = 0; i < count; i++)
        {
            Transform b = bones[i];
            if (b == null)
                _cache[i] = Matrix4x4.identity;
            else
                _cache[i] = b.localToWorldMatrix * bindPoses[i];
        }

        // Extra bindposes without bones: identity
        for (int i = count; i < BoneCount; i++)
            _cache[i] = Matrix4x4.identity;

        Buffer.SetData(_cache);
        return true;
    }

    public void Bind(MaterialPropertyBlock mpb)
    {
        if (mpb == null || Buffer == null)
            return;
        mpb.SetBuffer(BoneMatricesId, Buffer);
        mpb.SetInt(BoneCountId, BoneCount);
    }

    public void Bind(Material material)
    {
        if (material == null || Buffer == null)
            return;
        material.SetBuffer(BoneMatricesId, Buffer);
        material.SetInt(BoneCountId, BoneCount);
    }

    public void Dispose()
    {
        if (Buffer != null)
        {
            Buffer.Release();
            Buffer = null;
        }
        _cache = null;
        BoneCount = 0;
    }
}

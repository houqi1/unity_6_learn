using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Sparse local Grass guide strands for GPU-skin shell fur.
/// Each guide is a ShellFurDynamics chain (Grass, or PBD if that mode is selected)
/// anchored to a skinned surface point.
/// Per-vertex δ is a blend of the 3 nearest guides (bind-pose distances).
/// </summary>
public sealed class ShellFurLocalGrassGuides : IDisposable
{
    public const int MaxGuides = 512;
    public const int MaxNodes = ShellFurDynamics.MaxNodes; // 17

    static readonly int GuideChainsId = Shader.PropertyToID("_GuideChains");
    static readonly int VertexGuideWeightsId = Shader.PropertyToID("_VertexGuideWeights");
    static readonly int GuideCountId = Shader.PropertyToID("_GuideCount");
    static readonly int GuideNodeCountId = Shader.PropertyToID("_GuideNodeCount");
    static readonly int GuideStrideId = Shader.PropertyToID("_GuideStride");
    static readonly int UseLocalGuidesId = Shader.PropertyToID("_UseLocalGuides");
    static readonly int UseFurChainId = Shader.PropertyToID("_UseFurChain");

    // Bound when local guides are off so StructuredBuffer declarations stay valid.
    static GraphicsBuffer s_dummyChain;
    static GraphicsBuffer s_dummyWeight;

    int[] _guideVertIndices;
    ShellFurDynamics[] _chains;
    ShellFurGpuSkinTypes.GuideWeight[] _weights;
    ShellFurGpuSkinTypes.BindVertex[] _bindVerts;

    GraphicsBuffer _chainBuffer;
    GraphicsBuffer _weightBuffer;
    Vector4[] _chainUpload; // guideCount * MaxNodes

    int _guideCount;
    int _vertexCount;
    int _requestedGuideCount;
    bool _ready;

    public bool IsReady => _ready && _guideCount > 0 && _chainBuffer != null && _weightBuffer != null;
    public int GuideCount => _guideCount;
    public int VertexCount => _vertexCount;
    public int RequestedGuideCount => _requestedGuideCount;

    /// <summary>
    /// Build guide indices (even coverage) + 3-nearest weights in bind pose.
    /// </summary>
    public bool Build(
        ShellFurGpuSkinTypes.BindVertex[] bindVerts,
        int requestedGuideCount,
        ShellFurDynamics settingsTemplate)
    {
        ReleaseGpu();
        _ready = false;
        _guideCount = 0;
        _vertexCount = 0;
        _requestedGuideCount = Mathf.Clamp(requestedGuideCount, 1, MaxGuides);
        _bindVerts = bindVerts;
        _guideVertIndices = null;
        _chains = null;
        _weights = null;

        if (bindVerts == null || bindVerts.Length == 0)
            return false;

        int vcount = bindVerts.Length;
        int k = Mathf.Min(_requestedGuideCount, vcount);

        _guideVertIndices = new int[k];
        if (k == 1)
        {
            _guideVertIndices[0] = 0;
        }
        else
        {
            for (int i = 0; i < k; i++)
                _guideVertIndices[i] = (int)Mathf.Round(i * (vcount - 1) / (float)(k - 1));
            // De-duplicate if mesh tiny
            int write = 0;
            for (int i = 0; i < k; i++)
            {
                int idx = _guideVertIndices[i];
                bool dup = false;
                for (int j = 0; j < write; j++)
                {
                    if (_guideVertIndices[j] == idx) { dup = true; break; }
                }
                if (!dup)
                    _guideVertIndices[write++] = idx;
            }
            if (write < k)
            {
                var trimmed = new int[write];
                Array.Copy(_guideVertIndices, trimmed, write);
                _guideVertIndices = trimmed;
                k = write;
            }
        }

        _guideCount = k;
        _vertexCount = vcount;
        _chains = new ShellFurDynamics[k];
        for (int i = 0; i < k; i++)
        {
            var chain = new ShellFurDynamics();
            SyncSettings(settingsTemplate, chain);
            chain.enabled = true;
            chain.mode = ResolveLocalMode(settingsTemplate);
            chain.ResetState();
            _chains[i] = chain;
        }

        _weights = new ShellFurGpuSkinTypes.GuideWeight[vcount];
        BuildWeights(bindVerts, _guideVertIndices, _weights);

        _chainUpload = new Vector4[k * MaxNodes];
        _chainBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            k * MaxNodes,
            sizeof(float) * 4);
        _weightBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            vcount,
            ShellFurGpuSkinTypes.GuideWeight.Stride);
        _weightBuffer.SetData(_weights);

        _ready = true;
        return true;
    }

    public void ResetAll()
    {
        if (_chains == null) return;
        for (int i = 0; i < _chains.Length; i++)
            _chains[i]?.ResetState();
    }

    /// <summary>
    /// Skin guide anchors with the same LBS matrices as CSSkin, step Grass/PBD, upload chains.
    /// </summary>
    public void StepAndUpload(
        Matrix4x4[] boneMatrices,
        int boneCount,
        ShellFurDynamics settingsTemplate,
        Vector3 gravityDirection,
        float furLength,
        float deltaTime,
        float shellGravityStrength,
        float shellGravityPower)
    {
        if (!IsReady || _bindVerts == null || boneMatrices == null)
            return;

        Vector3 gDir = gravityDirection.sqrMagnitude > 1e-8f
            ? gravityDirection.normalized
            : Vector3.down;

        float dt = deltaTime;
        if (dt <= 1e-6f)
            dt = 1f / 60f;

        int nodes = 2;
        for (int g = 0; g < _guideCount; g++)
        {
            var chain = _chains[g];
            SyncSettings(settingsTemplate, chain);
            chain.enabled = true;
            chain.mode = ResolveLocalMode(settingsTemplate);

            int vi = _guideVertIndices[g];
            Vector3 anchor = SkinBindVertex(_bindVerts[vi], boneMatrices, boneCount);
            Vector3 erect = default;
            if (chain.mode == ShellFurDynamics.Mode.Pbd)
                erect = SkinBindDirection(_bindVerts[vi], boneMatrices, boneCount);
            chain.Evaluate(anchor, gDir, furLength, dt, shellGravityStrength, shellGravityPower, erect);

            nodes = Mathf.Max(nodes, chain.SampleCount);
            PackGuideSamples(g, chain);
        }

        _chainBuffer.SetData(_chainUpload);
    }

    public void BindMpb(MaterialPropertyBlock mpb)
    {
        if (mpb == null || !IsReady)
            return;

        mpb.SetBuffer(GuideChainsId, _chainBuffer);
        mpb.SetBuffer(VertexGuideWeightsId, _weightBuffer);
        mpb.SetFloat(GuideCountId, _guideCount);
        mpb.SetFloat(GuideNodeCountId, Mathf.Clamp(
            _chains != null && _chains.Length > 0 ? _chains[0].SampleCount : 2,
            ShellFurDynamics.MinNodes,
            MaxNodes));
        mpb.SetFloat(GuideStrideId, MaxNodes);
        mpb.SetFloat(UseLocalGuidesId, 1f);
        // Local guides replace global FurChain sampling path.
        mpb.SetFloat(UseFurChainId, 0f);
    }

    /// <summary>Bind 1-slot dummy buffers + _UseLocalGuides=0 (shader always declares StructuredBuffers).</summary>
    public static void BindDisabledMpb(MaterialPropertyBlock mpb)
    {
        if (mpb == null)
            return;
        EnsureDummies();
        mpb.SetBuffer(GuideChainsId, s_dummyChain);
        mpb.SetBuffer(VertexGuideWeightsId, s_dummyWeight);
        mpb.SetFloat(GuideCountId, 1f);
        mpb.SetFloat(GuideNodeCountId, 2f);
        mpb.SetFloat(GuideStrideId, MaxNodes);
        mpb.SetFloat(UseLocalGuidesId, 0f);
    }

    public static void BindDisabledCompute(ComputeShader cs, int kernel)
    {
        if (cs == null || kernel < 0)
            return;
        EnsureDummies();
        cs.SetBuffer(kernel, GuideChainsId, s_dummyChain);
        cs.SetBuffer(kernel, VertexGuideWeightsId, s_dummyWeight);
        cs.SetFloat(GuideCountId, 1f);
        cs.SetFloat(GuideNodeCountId, 2f);
        cs.SetFloat(GuideStrideId, MaxNodes);
        cs.SetFloat(UseLocalGuidesId, 0f);
    }

    static void EnsureDummies()
    {
        if (s_dummyChain == null)
        {
            s_dummyChain = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxNodes, sizeof(float) * 4);
            s_dummyChain.SetData(new Vector4[MaxNodes]);
        }
        if (s_dummyWeight == null)
        {
            s_dummyWeight = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, ShellFurGpuSkinTypes.GuideWeight.Stride);
            s_dummyWeight.SetData(new[]
            {
                new ShellFurGpuSkinTypes.GuideWeight { w0 = 1f }
            });
        }
    }

    public void BindCompute(ComputeShader cs)
    {
        if (cs == null || !IsReady)
            return;

        int nodes = _chains != null && _chains.Length > 0 ? _chains[0].SampleCount : 2;
        nodes = Mathf.Clamp(nodes, ShellFurDynamics.MinNodes, MaxNodes);

        cs.SetBuffer(0, GuideChainsId, _chainBuffer); // may need per-kernel; set via renderer on each kernel
        // Renderer will call SetBuffer on specific kernels; still set uniforms:
        cs.SetFloat(GuideCountId, _guideCount);
        cs.SetFloat(GuideNodeCountId, nodes);
        cs.SetFloat(GuideStrideId, MaxNodes);
        cs.SetFloat(UseLocalGuidesId, 1f);
        cs.SetFloat(UseFurChainId, 0f);
    }

    public void SetComputeBuffers(ComputeShader cs, int kernel)
    {
        if (cs == null || !IsReady || kernel < 0)
            return;
        cs.SetBuffer(kernel, GuideChainsId, _chainBuffer);
        cs.SetBuffer(kernel, VertexGuideWeightsId, _weightBuffer);
        cs.SetFloat(GuideCountId, _guideCount);
        int nodes = _chains != null && _chains.Length > 0 ? _chains[0].SampleCount : 2;
        cs.SetFloat(GuideNodeCountId, Mathf.Clamp(nodes, ShellFurDynamics.MinNodes, MaxNodes));
        cs.SetFloat(GuideStrideId, MaxNodes);
        cs.SetFloat(UseLocalGuidesId, 1f);
        cs.SetFloat(UseFurChainId, 0f);
    }

    public void DrawDebugGizmos()
    {
        if (!IsReady || _chains == null)
            return;
        for (int i = 0; i < _chains.Length; i++)
            _chains[i]?.DrawGuideChainGizmos();
    }

    public void DrawDebugLines(float duration = 0f)
    {
        if (!IsReady || _chains == null)
            return;
        for (int i = 0; i < _chains.Length; i++)
            _chains[i]?.DrawGuideChainDebugLines(duration);
    }

    void PackGuideSamples(int guideIndex, ShellFurDynamics chain)
    {
        int baseIdx = guideIndex * MaxNodes;
        int n = chain != null && chain.HasSamples ? chain.SampleCount : 0;
        Vector4[] samples = chain != null ? chain.BendSamples : null;
        for (int i = 0; i < MaxNodes; i++)
        {
            if (samples != null && i < n)
                _chainUpload[baseIdx + i] = samples[i];
            else
                _chainUpload[baseIdx + i] = Vector4.zero;
        }
    }

    static void SyncSettings(ShellFurDynamics src, ShellFurDynamics dst)
    {
        if (src == null || dst == null)
            return;

        dst.nodeCount = src.nodeCount;
        dst.guideChainLength = src.guideChainLength;
        dst.lengthScale = src.lengthScale;
        dst.guideOffsetScale = src.guideOffsetScale;
        dst.particleGravity = src.particleGravity;
        dst.gravityAsRestPose = src.gravityAsRestPose;
        dst.teleportDistance = src.teleportDistance;
        dst.grassStiffness = src.grassStiffness;
        dst.grassTipSoftness = src.grassTipSoftness;
        dst.grassWindStrength = src.grassWindStrength;
        dst.grassWindSpeed = src.grassWindSpeed;
        dst.followTension = src.followTension;
        dst.followTensionMin = src.followTensionMin;
        dst.velocityDamping = src.velocityDamping;
        dst.velocityDampingMin = src.velocityDampingMin;
        dst.nodeMass = src.nodeMass;
        dst.maxStretchLength = src.maxStretchLength;
        dst.bendStiffness = src.bendStiffness;
        dst.verletDamping = src.verletDamping;
        dst.verletIterations = src.verletIterations;
        dst.pbdStiffness = src.pbdStiffness;
        dst.pbdDamping = src.pbdDamping;
        dst.pbdGravity = src.pbdGravity;
        dst.pbdGravityAxial = src.pbdGravityAxial;
        dst.pbdIterations = src.pbdIterations;
        dst.pbdSubsteps = src.pbdSubsteps;
        dst.pbdWindStrength = src.pbdWindStrength;
        dst.pbdWindTurbulence = src.pbdWindTurbulence;
        dst.pbdWindDirection = src.pbdWindDirection;
        dst.showGuideChain = src.showGuideChain;
        dst.guideChainColor = src.guideChainColor;
        dst.guideNodeRadius = src.guideNodeRadius;
        dst.ValidateNodeCount();
    }

    static ShellFurDynamics.Mode ResolveLocalMode(ShellFurDynamics settings)
    {
        if (settings != null && settings.mode == ShellFurDynamics.Mode.Pbd)
            return ShellFurDynamics.Mode.Pbd;
        return ShellFurDynamics.Mode.Grass;
    }

    static void BuildWeights(
        ShellFurGpuSkinTypes.BindVertex[] verts,
        int[] guideIndices,
        ShellFurGpuSkinTypes.GuideWeight[] outWeights)
    {
        int vcount = verts.Length;
        int k = guideIndices.Length;
        var gpos = new Vector3[k];
        for (int g = 0; g < k; g++)
        {
            var bv = verts[guideIndices[g]];
            gpos[g] = new Vector3(bv.px, bv.py, bv.pz);
        }

        for (int i = 0; i < vcount; i++)
        {
            Vector3 p = new Vector3(verts[i].px, verts[i].py, verts[i].pz);
            int i0 = 0, i1 = 0, i2 = 0;
            float d0 = float.MaxValue, d1 = float.MaxValue, d2 = float.MaxValue;

            for (int g = 0; g < k; g++)
            {
                float d = (p - gpos[g]).sqrMagnitude;
                if (d < d0)
                {
                    d2 = d1; i2 = i1;
                    d1 = d0; i1 = i0;
                    d0 = d; i0 = g;
                }
                else if (d < d1)
                {
                    d2 = d1; i2 = i1;
                    d1 = d; i1 = g;
                }
                else if (d < d2)
                {
                    d2 = d; i2 = g;
                }
            }

            // Inverse-distance weights (bind space).
            float r0 = 1f / Mathf.Max(0.0001f, Mathf.Sqrt(d0));
            float r1 = k > 1 ? 1f / Mathf.Max(0.0001f, Mathf.Sqrt(d1)) : 0f;
            float r2 = k > 2 ? 1f / Mathf.Max(0.0001f, Mathf.Sqrt(d2)) : 0f;
            if (k == 1) { r1 = 0f; r2 = 0f; i1 = i0; i2 = i0; }
            else if (k == 2) { r2 = 0f; i2 = i0; }

            float sum = r0 + r1 + r2;
            if (sum < 1e-8f) { r0 = 1f; r1 = r2 = 0f; sum = 1f; }

            outWeights[i] = new ShellFurGpuSkinTypes.GuideWeight
            {
                i0 = i0, i1 = i1, i2 = i2, pad0 = 0,
                w0 = r0 / sum, w1 = r1 / sum, w2 = r2 / sum, pad1 = 0
            };
        }
    }

    /// <summary>Same LBS as ShellFurGpuSkin.compute CSSkin (world space).</summary>
    public static Vector3 SkinBindVertex(
        ShellFurGpuSkinTypes.BindVertex v,
        Matrix4x4[] bones,
        int boneCount)
    {
        Matrix4x4 M = BlendSkinMatrix(v, bones, boneCount);
        return M.MultiplyPoint3x4(new Vector3(v.px, v.py, v.pz));
    }

    /// <summary>Skinned smooth (fallback mesh) normal in world space.</summary>
    public static Vector3 SkinBindDirection(
        ShellFurGpuSkinTypes.BindVertex v,
        Matrix4x4[] bones,
        int boneCount)
    {
        Vector3 n = new Vector3(v.sx, v.sy, v.sz);
        if (n.sqrMagnitude < 1e-8f)
            n = new Vector3(v.nx, v.ny, v.nz);
        Matrix4x4 M = BlendSkinMatrix(v, bones, boneCount);
        Vector3 wn = M.MultiplyVector(n);
        return wn.sqrMagnitude > 1e-8f ? wn.normalized : Vector3.up;
    }

    static Matrix4x4 BlendSkinMatrix(
        ShellFurGpuSkinTypes.BindVertex v,
        Matrix4x4[] bones,
        int boneCount)
    {
        if (bones == null || boneCount <= 0)
            return Matrix4x4.identity;

        float w0 = v.w0, w1 = v.w1, w2 = v.w2, w3 = v.w3;
        float wSum = w0 + w1 + w2 + w3;
        Matrix4x4 M = default;
        bool any = false;

        any |= AccBone(ref M, bones, boneCount, w0, v.i0);
        any |= AccBone(ref M, bones, boneCount, w1, v.i1);
        any |= AccBone(ref M, bones, boneCount, w2, v.i2);
        any |= AccBone(ref M, bones, boneCount, w3, v.i3);

        if (!any || wSum < 1e-6f)
            return bones[0];
        return M;
    }

    static bool AccBone(ref Matrix4x4 M, Matrix4x4[] bones, int boneCount, float w, float indexF)
    {
        if (w <= 0f) return false;
        int bi = (int)indexF;
        if (bi < 0 || bi >= boneCount) return false;
        // M += bones[bi] * w
        Matrix4x4 b = bones[bi];
        for (int r = 0; r < 4; r++)
        for (int c = 0; c < 4; c++)
            M[r, c] += b[r, c] * w;
        return true;
    }

    public void Dispose()
    {
        ReleaseGpu();
        _chains = null;
        _guideVertIndices = null;
        _weights = null;
        _bindVerts = null;
        _chainUpload = null;
        _ready = false;
        _guideCount = 0;
        _vertexCount = 0;
    }

    void ReleaseGpu()
    {
        if (_chainBuffer != null)
        {
            _chainBuffer.Release();
            _chainBuffer = null;
        }
        if (_weightBuffer != null)
        {
            _weightBuffer.Release();
            _weightBuffer = null;
        }
    }
}

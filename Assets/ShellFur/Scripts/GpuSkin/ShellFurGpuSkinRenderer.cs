using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// GPU-skinning shell fur (Scheme B) + CS silhouette fins (B2 / true GS migration):
/// 1) Upload bone matrices from SkinnedMeshRenderer
/// 2) Compute skins bind-pose fur verts once (world space)
/// 3) DrawMeshInstanced N shell layers from skinned buffer
/// 4) Per camera: CS tests silhouette edges → compact fin triangle list → DrawProceduralIndirect
///
/// SMR is not used to draw fur — only bones + source mesh data.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class ShellFurGpuSkinRenderer : MonoBehaviour
{
    public const string ShaderName = "Custom/ShellFurGpuSkinned";
    public const string FinShaderName = "Custom/ShellFurGpuFin";
    const int MaxShells = 128;
    const int MinFinSegments = 1;
    const int MaxFinSegments = 16;

    static readonly int ShellCountId = Shader.PropertyToID("_ShellCount");
    static readonly int ShellLayerOffsetId = Shader.PropertyToID("_ShellLayerOffset");
    static readonly int FurLengthId = Shader.PropertyToID("_FurLength");
    static readonly int GravityId = Shader.PropertyToID("_Gravity");
    static readonly int GravityDirId = Shader.PropertyToID("_GravityDir");
    static readonly int GravityPowerId = Shader.PropertyToID("_GravityPower");
    static readonly int FurChainId = Shader.PropertyToID("_FurChain");
    static readonly int FurChainCountId = Shader.PropertyToID("_FurChainCount");
    static readonly int UseFurChainId = Shader.PropertyToID("_UseFurChain");
    static readonly int FurChainErectId = Shader.PropertyToID("_FurChainErect");
    static readonly int UseLocalGuidesId = Shader.PropertyToID("_UseLocalGuides");
    static readonly int GuideOffsetScaleId = Shader.PropertyToID("_GuideOffsetScale");
    static readonly int SkinnedVerticesId = Shader.PropertyToID("_SkinnedVertices");
    static readonly int BindVerticesId = Shader.PropertyToID("_BindVertices");
    static readonly int BoneMatricesId = Shader.PropertyToID("_BoneMatrices");
    static readonly int VertexCountId = Shader.PropertyToID("_VertexCount");
    static readonly int BoneCountId = Shader.PropertyToID("_BoneCount");

    static readonly int FinEdgesId = Shader.PropertyToID("_FinEdges");
    static readonly int FinVerticesId = Shader.PropertyToID("_FinVertices");
    static readonly int FinCounterId = Shader.PropertyToID("_FinCounter");
    static readonly int FinDrawArgsId = Shader.PropertyToID("_FinDrawArgs");
    static readonly int FinEdgeCountId = Shader.PropertyToID("_FinEdgeCount");
    static readonly int FinSegmentsId = Shader.PropertyToID("_FinSegments");
    static readonly int FinMaxVerticesId = Shader.PropertyToID("_FinMaxVertices");
    static readonly int FinCameraPosId = Shader.PropertyToID("_FinCameraPos");
    static readonly int FinLengthScaleId = Shader.PropertyToID("_FinLengthScale");
    static readonly int FinExtrudeWeightId = Shader.PropertyToID("_FinExtrudeWeight");
    static readonly int FinSharpId = Shader.PropertyToID("_FinSilhouetteSharpness");
    static readonly int FinBiasId = Shader.PropertyToID("_FinSilhouetteBias");
    static readonly int FinPowerId = Shader.PropertyToID("_FinSilhouettePower");
    static readonly int FinBandId = Shader.PropertyToID("_FinBandStrength");
    static readonly int FinRootOffsetId = Shader.PropertyToID("_FinRootOffset");
    static readonly int FinMinSilhouetteId = Shader.PropertyToID("_FinMinSilhouette");
    static readonly int FinRootOpacityId = Shader.PropertyToID("_FinRootOpacity");
    static readonly int FinTipOpacityId = Shader.PropertyToID("_FinTipOpacity");
    static readonly int FinOpacityFadeStartId = Shader.PropertyToID("_FinOpacityFadeStart");
    static readonly int FinOpacityFadeEndId = Shader.PropertyToID("_FinOpacityFadeEnd");
    static readonly int FinOpacityPowerId = Shader.PropertyToID("_FinOpacityPower");
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int TipColorId = Shader.PropertyToID("_TipColor");

    [Header("Source (bones + mesh only)")]
    [SerializeField] SkinnedMeshRenderer sourceSkinned;
    [Tooltip("Optional prebuilt bind fur mesh from Tools/Shell Fur/Build GPU Skin Fur Mesh. If empty, built at runtime.")]
    [SerializeField] Mesh bindFurMeshOverride;

    [Header("Material")]
    [SerializeField] Material furMaterial;

    [Header("Material Slots")]
    [SerializeField] bool useMaterialSlotOnly = true;
    [SerializeField] int[] furMaterialSlots = { 1, 2 };
    [SerializeField] bool hideSourceFurSlots = true;
    [Tooltip("Skip shell layer 0 (solid base) under fur.")]
    [SerializeField] bool hideBaseMesh = false;

    [Header("Shells")]
    [Range(2, MaxShells)]
    [SerializeField] int shellCount = 24;
    [Min(0.001f)]
    [SerializeField] float furLength = 0.08f;
    [Range(1f, 180f)]
    [SerializeField] float smoothNormalMaxAngle = 180f;

    [Header("Fins (CS B2 — silhouette emit)")]
    [SerializeField] bool enableFins = true;
    [Tooltip("When on: still draw shell layer 0 (or hide-base offset) but skip multi-layer shells; fins still emit.")]
    [SerializeField] bool finsOnly = false;
    [Range(MinFinSegments, MaxFinSegments)]
    [SerializeField] int finSegments = 4;
    [Range(0f, 2f)]
    [SerializeField] float finExtrudeWeight = 1f;
    [Range(0.5f, 32f)]
    [SerializeField] float finSilhouetteSharpness = 8f;
    [Range(0f, 1f)]
    [SerializeField] float finSilhouetteBias = 0f;
    [Range(0.25f, 4f)]
    [SerializeField] float finSilhouettePower = 1f;
    [Range(0f, 2f)]
    [SerializeField] float finBandStrength = 0.4f;
    [Range(0f, 0.02f)]
    [SerializeField] float finRootOffset = 0.0015f;
    [Range(0.25f, 2f)]
    [SerializeField] float finLengthScale = 1f;
    [Tooltip("CS only emits a fin when silhouette weight exceeds this (true GS-style cull).")]
    [Range(0.0001f, 0.2f)]
    [SerializeField] float finMinSilhouette = 0.001f;
    [Range(0.99f, 1f)]
    [SerializeField] float finSkipCoplanarDot = 0.9998f;
    [Header("Fin Opacity (root → tip)")]
    [Range(0f, 1f)]
    [SerializeField] float finRootOpacity = 1f;
    [Range(0f, 1f)]
    [SerializeField] float finTipOpacity = 0f;
    [Range(0f, 1f)]
    [SerializeField] float finOpacityFadeStart = 0f;
    [Range(0f, 1f)]
    [SerializeField] float finOpacityFadeEnd = 1f;
    [Range(0.25f, 4f)]
    [SerializeField] float finOpacityPower = 1f;
    [SerializeField] bool finCastShadows = true;
    [SerializeField] Material finMaterialOverride;

    [Header("Physics")]
    [SerializeField] float gravityStrength = 0.35f;
    [SerializeField] Vector3 gravityDirection = Vector3.down;
    [Tooltip("Nonlinear droop: bend ∝ pow(layer, power). 2 = classic tip-heavy arc.")]
    [Range(0.5f, 4f)]
    [SerializeField] float gravityPower = 2f;

    [Header("Dynamics (guide strand)")]
    [Tooltip("Spring / Verlet / Grass / Bone / PBD. Shell = pure extrude + chain δ (no GravityBend while chain on). LocalGuides: Grass hang or PBD stand-along-normal.")]
    [SerializeField] ShellFurDynamics dynamics = new ShellFurDynamics();

    public enum DynamicsResolution
    {
        /// <summary>Single chain anchored at SMR / object origin (legacy).</summary>
        GlobalChain = 0,
        /// <summary>Sparse local guides on skinned surface; per-vertex blend of 3 nearest. Grass or PBD.</summary>
        LocalGuides = 1
    }

    [Tooltip("GlobalChain = one chain at transform. LocalGuides = Grass per surface guide (follows animation).")]
    [SerializeField] DynamicsResolution dynamicsResolution = DynamicsResolution.LocalGuides;
    [Tooltip("Number of local Grass guide strands (LocalGuides). Built from bind fur verts.")]
    [Range(1, ShellFurLocalGrassGuides.MaxGuides)]
    [SerializeField] int localGuideCount = 16;

    [Header("Rendering")]
    [SerializeField] ShadowCastingMode shadowCasting = ShadowCastingMode.On;
    [SerializeField] bool receiveShadows = true;
    [SerializeField] bool drawInEditMode = true;
    [SerializeField] ComputeShader skinCompute;
    [SerializeField] ComputeShader finCompute;

    [Header("Debug")]
    [SerializeField] bool logOnce;

    bool _dynamicsSteppedThisFrame;
    int _dynamicsStepFrame = -1;

    SkinnedMeshRenderer _smr;
    Mesh _bindMesh;
    ShellFurGpuSkinTypes.BindVertex[] _bindVerts;
    ShellFurGpuSkinTypes.FinEdge[] _finEdges;
    GraphicsBuffer _bindBuffer;
    GraphicsBuffer _skinnedBuffer;
    GraphicsBuffer _finEdgeBuffer;
    GraphicsBuffer _finVertexBuffer;
    GraphicsBuffer _finCounterBuffer;
    GraphicsBuffer _finArgsBuffer;
    ShellFurBoneBuffer _boneBuffer;
    ShellFurLocalGrassGuides _localGuides;
    MaterialPropertyBlock _mpb;
    MaterialPropertyBlock _mpbFin;
    Matrix4x4[] _instanceMatrices;
    Material _ownedMaterial;
    Material _ownedFinMaterial;
    Material _runtimeFurMat;
    Material _runtimeFinMat;
    static Material _skipMat;
    static readonly List<ShellFurGpuSkinRenderer> s_Active = new List<ShellFurGpuSkinRenderer>();
    static readonly Matrix4x4[] s_HullMatrix = { Matrix4x4.identity };
    Material[] _originalShared;
    bool _hijacked;
    int _lastPrepareFrame = -1;
    int _lastFinCameraFrame = -1;
    int _lastFinCameraId = int.MinValue;
    bool _logged;
    bool _loggedFin;
    bool _ready;
    bool _finsReady;
    int _kernelSkin = -1;
    int _kernelReset = -1;
    int _kernelGenFins = -1;
    int _kernelFinalize = -1;
    int _finMaxVertices;
    int _finEdgeCount;
    int _finSegmentsBuilt = -1;

    public bool IsReady => _ready;
    public bool FinsReady => _finsReady;
    public int FinEdgeCount => _finEdgeCount;
    public static bool HasActiveDepthHull
    {
        get
        {
            for (int i = 0; i < s_Active.Count; i++)
            {
                var r = s_Active[i];
                if (r != null && r.isActiveAndEnabled && r._ready)
                    return true;
            }
            return false;
        }
    }

    public static void RefreshActive()
    {
        s_Active.RemoveAll(static r => r == null);
        var found = Object.FindObjectsByType<ShellFurGpuSkinRenderer>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            var r = found[i];
            if (r != null && r.isActiveAndEnabled && !s_Active.Contains(r))
                s_Active.Add(r);
        }
    }

    public static void DrawAllDepthHulls(IRasterCommandBuffer cmd)
    {
        RefreshActive();
        for (int i = 0; i < s_Active.Count; i++)
        {
            var r = s_Active[i];
            if (r != null && r.isActiveAndEnabled)
                r.DrawDepthHull(cmd);
        }
    }

    void OnEnable()
    {
        if (!s_Active.Contains(this))
            s_Active.Add(this);
        CacheRefs();
        EnsureMaterials();
        RebuildBindData();
        ApplySourceRendererState();
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
    }

    void OnDisable()
    {
        s_Active.Remove(this);
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RestoreSourceRendererState();
        ReleaseGpu();
        dynamics?.ResetState();
        _localGuides?.ResetAll();
        _dynamicsSteppedThisFrame = false;
        _dynamicsStepFrame = -1;
    }

    void LateUpdate()
    {
        dynamics?.ValidateNodeCount();
        // Local guides need fresh bone matrices — stepped in PrepareSkinFrame after bone upload.
        if (!UseLocalGuidesMode)
            StepDynamicsIfNeeded();
        if (dynamics != null && dynamics.showGuideChain && Application.isPlaying)
        {
            if (UseLocalGuidesMode)
                _localGuides?.DrawDebugLines();
            else
                dynamics.DrawGuideChainDebugLines();
        }
    }

    bool UseLocalGuidesMode =>
        dynamics != null && dynamics.enabled &&
        dynamicsResolution == DynamicsResolution.LocalGuides;

    void StepDynamicsIfNeeded()
    {
        if (dynamics == null || !dynamics.enabled)
            return;

        int frame = Time.frameCount;
        if (_dynamicsStepFrame == frame && _dynamicsSteppedThisFrame)
            return;

        float dt = Application.isPlaying ? Time.deltaTime : Time.unscaledDeltaTime;
        if (dt <= 1e-6f)
            dt = 1f / 60f;

        Vector3 gDir = gravityDirection.sqrMagnitude > 1e-6f ? gravityDirection.normalized : Vector3.down;

        if (UseLocalGuidesMode)
        {
            EnsureLocalGuidesBuilt();
            if (_localGuides != null && _localGuides.IsReady &&
                _boneBuffer != null && _boneBuffer.Matrices != null)
            {
                _localGuides.StepAndUpload(
                    _boneBuffer.Matrices,
                    _boneBuffer.BoneCount,
                    dynamics,
                    gDir,
                    furLength,
                    dt,
                    gravityStrength,
                    gravityPower);
            }
        }
        else
        {
            Transform t = _smr != null ? _smr.transform : transform;
            Vector3 erect = dynamics.mode == ShellFurDynamics.Mode.Pbd ? t.up : default;
            dynamics.Evaluate(t.position, gDir, furLength, dt, gravityStrength, gravityPower, erect);
        }

        _dynamicsStepFrame = frame;
        _dynamicsSteppedThisFrame = true;
    }

    void OnDrawGizmos()
    {
        if (dynamics == null || !dynamics.showGuideChain)
            return;
        if (!Application.isPlaying)
            StepDynamicsIfNeeded();
        if (UseLocalGuidesMode)
            _localGuides?.DrawDebugGizmos();
        else
            dynamics.DrawGuideChainGizmos();
    }

    void EnsureLocalGuidesBuilt()
    {
        if (!UseLocalGuidesMode)
            return;
        int want = Mathf.Clamp(localGuideCount, 1, ShellFurLocalGrassGuides.MaxGuides);
        if (_localGuides != null && _localGuides.IsReady &&
            _localGuides.VertexCount == (_bindVerts != null ? _bindVerts.Length : 0) &&
            _localGuides.RequestedGuideCount == want)
            return;
        RebuildLocalGuides();
    }

    void RebuildLocalGuides()
    {
        _localGuides?.Dispose();
        _localGuides = null;
        if (_bindVerts == null || _bindVerts.Length == 0 || dynamics == null)
            return;

        _localGuides = new ShellFurLocalGrassGuides();
        int k = Mathf.Clamp(localGuideCount, 1, ShellFurLocalGrassGuides.MaxGuides);
        if (!_localGuides.Build(_bindVerts, k, dynamics))
        {
            _localGuides.Dispose();
            _localGuides = null;
        }
    }

    void ApplyPhysicsToMpb(MaterialPropertyBlock mpb)
    {
        if (mpb == null)
            return;

        Vector3 gDir = gravityDirection.sqrMagnitude > 1e-6f ? gravityDirection.normalized : Vector3.down;
        mpb.SetFloat(GravityId, gravityStrength);
        mpb.SetVector(GravityDirId, gDir);
        mpb.SetFloat(GravityPowerId, Mathf.Max(0.01f, gravityPower));

        // Hang-rest amplitude for strain emission (matches PackSamples normalization).
        float guideOff = dynamics != null ? Mathf.Max(0f, dynamics.guideOffsetScale) : 0f;
        mpb.SetFloat(GuideOffsetScaleId, guideOff);

        if (dynamics != null && dynamics.enabled)
        {
            if (!_dynamicsSteppedThisFrame || _dynamicsStepFrame != Time.frameCount)
                StepDynamicsIfNeeded();
        }

        if (UseLocalGuidesMode && _localGuides != null && _localGuides.IsReady)
        {
            _localGuides.BindMpb(mpb);
            mpb.SetFloat(GuideOffsetScaleId, guideOff);
            return;
        }

        ShellFurLocalGrassGuides.BindDisabledMpb(mpb);
        bool useChain = dynamics != null && dynamics.enabled && dynamics.HasSamples;
        if (useChain)
        {
            mpb.SetFloat(UseFurChainId, 1f);
            mpb.SetFloat(FurChainCountId, dynamics.SampleCount);
            mpb.SetVectorArray(FurChainId, dynamics.BendSamples);
            Vector3 erect = dynamics.ErectDirection;
            mpb.SetVector(FurChainErectId, new Vector4(erect.x, erect.y, erect.z, 0f));
        }
        else
        {
            mpb.SetFloat(UseFurChainId, 0f);
            mpb.SetFloat(FurChainCountId, 0f);
        }
    }

    void ApplyPhysicsToCompute()
    {
        if (finCompute == null)
            return;

        Vector3 gDir = gravityDirection.sqrMagnitude > 1e-6f ? gravityDirection.normalized : Vector3.down;
        finCompute.SetFloat(GravityId, gravityStrength);
        finCompute.SetVector(GravityDirId, gDir);
        finCompute.SetFloat(GravityPowerId, Mathf.Max(0.01f, gravityPower));

        if (dynamics != null && dynamics.enabled)
        {
            if (!_dynamicsSteppedThisFrame || _dynamicsStepFrame != Time.frameCount)
                StepDynamicsIfNeeded();
        }

        if (UseLocalGuidesMode && _localGuides != null && _localGuides.IsReady)
        {
            if (_kernelGenFins >= 0)
                _localGuides.SetComputeBuffers(finCompute, _kernelGenFins);
            finCompute.SetFloat(UseLocalGuidesId, 1f);
            finCompute.SetFloat(UseFurChainId, 0f);
            return;
        }

        if (_kernelGenFins >= 0)
            ShellFurLocalGrassGuides.BindDisabledCompute(finCompute, _kernelGenFins);
        bool useChain = dynamics != null && dynamics.enabled && dynamics.HasSamples;
        finCompute.SetFloat(UseFurChainId, useChain ? 1f : 0f);
        finCompute.SetFloat(FurChainCountId, useChain ? dynamics.SampleCount : 0f);
        if (useChain)
        {
            finCompute.SetVectorArray(FurChainId, dynamics.BendSamples);
            Vector3 erect = dynamics.ErectDirection;
            finCompute.SetVector(FurChainErectId, new Vector4(erect.x, erect.y, erect.z, 0f));
        }
    }

    void OnDestroy()
    {
        RestoreSourceRendererState();
        ReleaseGpu();
        DestroyOwned(_ownedMaterial);
        DestroyOwned(_ownedFinMaterial);
        _ownedMaterial = null;
        _ownedFinMaterial = null;
        if (_bindMesh != null && bindFurMeshOverride == null)
            DestroyOwned(_bindMesh);
    }

    static void DestroyOwned(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    void OnValidate()
    {
        shellCount = Mathf.Clamp(shellCount, 2, MaxShells);
        furLength = Mathf.Max(0.001f, furLength);
        finSegments = Mathf.Clamp(finSegments, MinFinSegments, MaxFinSegments);
        localGuideCount = Mathf.Clamp(localGuideCount, 1, ShellFurLocalGrassGuides.MaxGuides);
        if (furMaterialSlots == null || furMaterialSlots.Length == 0)
            furMaterialSlots = new[] { 0 };
        CacheRefs();
        ApplySourceRendererState();
    }

    [ContextMenu("Rebuild GPU Skin Bind Mesh + Fin Edges")]
    public void RebuildBindData()
    {
        ReleaseGpu();
        _ready = false;
        _finsReady = false;
        CacheRefs();

        if (_smr == null || _smr.sharedMesh == null)
        {
            Debug.LogWarning($"[{nameof(ShellFurGpuSkinRenderer)}] Need SkinnedMeshRenderer with mesh on '{name}'.", this);
            return;
        }

        Mesh source = _smr.sharedMesh;
        if (!source.isReadable && bindFurMeshOverride == null)
        {
            Debug.LogError(
                $"[{nameof(ShellFurGpuSkinRenderer)}] Source mesh not readable. Enable Read/Write on FBX, or assign Bind Fur Mesh Override.",
                this);
            return;
        }

        if (bindFurMeshOverride != null)
        {
            _bindMesh = bindFurMeshOverride;
            _bindVerts = ExtractBindVerticesFromMesh(_bindMesh);
        }
        else
        {
            int[] slots = useMaterialSlotOnly ? furMaterialSlots : null;
            var built = ShellFurBindMeshBuilder.Build(source, slots, smoothNormalMaxAngle);
            _bindMesh = built.mesh;
            _bindVerts = built.bindVertices;
            if (_bindMesh != null)
                _bindMesh.hideFlags = HideFlags.HideAndDontSave;
        }

        if (_bindMesh == null || _bindVerts == null || _bindVerts.Length == 0)
        {
            Debug.LogError($"[{nameof(ShellFurGpuSkinRenderer)}] Failed to build bind fur data.", this);
            return;
        }

        int vcount = _bindVerts.Length;
        _bindBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vcount, ShellFurGpuSkinTypes.BindVertex.Stride);
        _bindBuffer.SetData(_bindVerts);
        _skinnedBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vcount, ShellFurGpuSkinTypes.SkinnedVertex.Stride);

        _boneBuffer = new ShellFurBoneBuffer();
        _boneBuffer.Ensure(source.bindposes != null ? source.bindposes.Length : 1);

        EnsureSkinKernel();
        BuildFinGpuResources();
        RebuildLocalGuides();

        _mpb = new MaterialPropertyBlock();
        _mpbFin = new MaterialPropertyBlock();
        _instanceMatrices = new Matrix4x4[MaxShells];
        for (int i = 0; i < MaxShells; i++)
            _instanceMatrices[i] = Matrix4x4.identity;

        _ready = true;
        if (logOnce && !_logged)
        {
            Debug.Log(
                $"[{nameof(ShellFurGpuSkinRenderer)}] Ready verts={vcount} edges={_finEdgeCount} maxFinVerts={_finMaxVertices} bones={source.bindposes?.Length} localGuides={_localGuides?.GuideCount ?? 0}",
                this);
            _logged = true;
        }
    }

    void BuildFinGpuResources()
    {
        ReleaseBuffer(ref _finEdgeBuffer);
        ReleaseBuffer(ref _finVertexBuffer);
        ReleaseBuffer(ref _finCounterBuffer);
        ReleaseBuffer(ref _finArgsBuffer);

        _finsReady = false;
        _finEdges = null;
        _finEdgeCount = 0;
        _finMaxVertices = 0;
        _finSegmentsBuilt = -1;

        if (!enableFins || _bindMesh == null)
            return;

        int segs = Mathf.Clamp(finSegments, MinFinSegments, MaxFinSegments);
        _finEdges = ShellFurFinEdgeBuilder.Build(_bindMesh, finSkipCoplanarDot);
        if (_finEdges == null || _finEdges.Length == 0)
        {
            if (logOnce && !_loggedFin)
            {
                Debug.LogWarning($"[{nameof(ShellFurGpuSkinRenderer)}] No fin edges built for '{_bindMesh.name}'.", this);
                _loggedFin = true;
            }
            return;
        }

        _finEdgeCount = _finEdges.Length;
        _finMaxVertices = _finEdgeCount * segs * 6;
        _finSegmentsBuilt = segs;

        _finEdgeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _finEdgeCount, ShellFurGpuSkinTypes.FinEdge.Stride);
        _finEdgeBuffer.SetData(_finEdges);

        _finVertexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, _finMaxVertices), ShellFurGpuSkinTypes.FinVertex.Stride);
        _finCounterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
        _finArgsBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
            4,
            sizeof(uint));
        _finArgsBuffer.SetData(new uint[] { 0, 1, 0, 0 });

        EnsureFinKernels();
        EnsureFinMaterial();
        _finsReady = _kernelGenFins >= 0 && finCompute != null && _runtimeFinMat != null;

        if (logOnce && !_loggedFin)
        {
            Debug.Log(
                $"[{nameof(ShellFurGpuSkinRenderer)}] Fin B2 ready edges={_finEdgeCount} segs={segs} maxVerts={_finMaxVertices} kernelsOK={_finsReady}",
                this);
            _loggedFin = true;
        }
    }

    void EnsureSkinKernel()
    {
        _kernelSkin = -1;
        if (skinCompute == null)
        {
#if UNITY_EDITOR
            skinCompute = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/ShellFur/Shaders/ShellFurGpuSkin.compute");
#endif
        }
        if (skinCompute != null)
            _kernelSkin = skinCompute.FindKernel("CSSkin");
    }

    void EnsureFinKernels()
    {
        _kernelReset = _kernelGenFins = _kernelFinalize = -1;
        if (finCompute == null)
        {
#if UNITY_EDITOR
            finCompute = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/ShellFur/Shaders/ShellFurGpuFin.compute");
#endif
        }
        if (finCompute == null)
            return;

        _kernelReset = finCompute.FindKernel("CSResetFinCounter");
        _kernelGenFins = finCompute.FindKernel("CSGenerateFins");
        _kernelFinalize = finCompute.FindKernel("CSFinalizeFinArgs");
    }

    static ShellFurGpuSkinTypes.BindVertex[] ExtractBindVerticesFromMesh(Mesh mesh)
    {
        Vector3[] pos = mesh.vertices;
        Vector3[] nrm = mesh.normals;
        var uv0 = new System.Collections.Generic.List<Vector2>();
        var uv1 = new System.Collections.Generic.List<Vector4>();
        var uv2 = new System.Collections.Generic.List<Vector4>();
        var uv3 = new System.Collections.Generic.List<Vector3>();
        mesh.GetUVs(0, uv0);
        mesh.GetUVs(1, uv1);
        mesh.GetUVs(2, uv2);
        mesh.GetUVs(3, uv3);

        var arr = new ShellFurGpuSkinTypes.BindVertex[pos.Length];
        for (int i = 0; i < pos.Length; i++)
        {
            Vector4 w = i < uv1.Count ? uv1[i] : new Vector4(1, 0, 0, 0);
            Vector4 bi = i < uv2.Count ? uv2[i] : Vector4.zero;
            Vector3 sm = i < uv3.Count ? uv3[i] : (nrm != null && i < nrm.Length ? nrm[i] : Vector3.up);
            Vector3 n = nrm != null && i < nrm.Length ? nrm[i] : Vector3.up;
            Vector2 uv = i < uv0.Count ? uv0[i] : Vector2.zero;
            arr[i] = ShellFurGpuSkinTypes.BindVertex.From(pos[i], n.normalized, sm.normalized, uv, w, bi);
        }
        return arr;
    }

    void CacheRefs()
    {
        if (sourceSkinned != null)
            _smr = sourceSkinned;
        else
        {
            _smr = GetComponent<SkinnedMeshRenderer>();
            if (_smr == null)
                _smr = GetComponentInChildren<SkinnedMeshRenderer>();
        }
    }

    void EnsureMaterials()
    {
        EnsureFurMaterial();
        EnsureFinMaterial();
    }

    void EnsureFurMaterial()
    {
        if (furMaterial != null)
        {
            if (!furMaterial.enableInstancing)
                furMaterial.enableInstancing = true;
            _runtimeFurMat = furMaterial;
            return;
        }

        if (_ownedMaterial != null)
        {
            _runtimeFurMat = _ownedMaterial;
            furMaterial = _ownedMaterial;
            return;
        }

        Shader sh = Shader.Find(ShaderName);
        if (sh == null)
            return;
        _ownedMaterial = new Material(sh)
        {
            name = "ShellFurGpuSkinned (Runtime)",
            enableInstancing = true,
            hideFlags = HideFlags.HideAndDontSave
        };
        _ownedMaterial.SetFloat("_Cull", 0f);
        furMaterial = _ownedMaterial;
        _runtimeFurMat = _ownedMaterial;
    }

    void EnsureFinMaterial()
    {
        if (finMaterialOverride != null)
        {
            _runtimeFinMat = finMaterialOverride;
            return;
        }

        if (_ownedFinMaterial != null)
        {
            _runtimeFinMat = _ownedFinMaterial;
            return;
        }

        Shader sh = Shader.Find(FinShaderName);
        if (sh == null)
            return;

        _ownedFinMaterial = new Material(sh)
        {
            name = "ShellFurGpuFin (Runtime)",
            hideFlags = HideFlags.HideAndDontSave
        };
        _ownedFinMaterial.SetFloat("_Cull", 0f);
        _runtimeFinMat = _ownedFinMaterial;
        CopyShellLookToFin(_runtimeFinMat);
    }

    void CopyShellLookToFin(Material fin)
    {
        if (fin == null)
            return;
        Material src = _runtimeFurMat != null ? _runtimeFurMat : furMaterial;
        if (src == null)
            return;

        if (src.HasProperty("_BaseColor")) fin.SetColor("_BaseColor", src.GetColor("_BaseColor"));
        if (src.HasProperty("_TipColor")) fin.SetColor("_TipColor", src.GetColor("_TipColor"));
        if (src.HasProperty("_BaseMap")) fin.SetTexture("_BaseMap", src.GetTexture("_BaseMap"));
        if (src.HasProperty("_FurMap")) fin.SetTexture("_FurMap", src.GetTexture("_FurMap"));
        if (src.HasProperty("_Density")) fin.SetFloat("_Density", src.GetFloat("_Density"));
        if (src.HasProperty("_Thickness")) fin.SetFloat("_Thickness", src.GetFloat("_Thickness"));
        if (src.HasProperty("_Occlusion")) fin.SetFloat("_Occlusion", src.GetFloat("_Occlusion"));
        if (src.HasProperty("_AlphaCutoff")) fin.SetFloat("_AlphaCutoff", src.GetFloat("_AlphaCutoff"));
        if (src.HasProperty("_FurLengthRandom")) fin.SetFloat("_FurLengthRandom", src.GetFloat("_FurLengthRandom"));
        if (src.HasProperty("_Smoothness")) fin.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
        if (src.HasProperty("_RimPower")) fin.SetFloat("_RimPower", src.GetFloat("_RimPower"));
        if (src.HasProperty("_RimStrength")) fin.SetFloat("_RimStrength", src.GetFloat("_RimStrength"));
        if (src.HasProperty("_ShadowStrength")) fin.SetFloat("_ShadowStrength", src.GetFloat("_ShadowStrength"));
        if (src.IsKeywordEnabled("_USE_PROCEDURAL")) fin.EnableKeyword("_USE_PROCEDURAL");
        else fin.DisableKeyword("_USE_PROCEDURAL");
        if (src.IsKeywordEnabled("_USE_KAJIYA_KAY")) fin.EnableKeyword("_USE_KAJIYA_KAY");
        else fin.DisableKeyword("_USE_KAJIYA_KAY");
    }

    void ApplySourceRendererState()
    {
        if (_smr == null)
            return;

        if (useMaterialSlotOnly && hideSourceFurSlots)
        {
            _smr.enabled = true;
            HijackSlots();
        }
        else if (!useMaterialSlotOnly)
        {
            RestoreSlots();
            _smr.enabled = false;
        }
        else
        {
            RestoreSlots();
            _smr.enabled = true;
        }
    }

    void RestoreSourceRendererState()
    {
        RestoreSlots();
        if (_smr != null)
            _smr.enabled = true;
    }

    static Material GetSkip()
    {
        if (_skipMat != null)
            return _skipMat;
        Shader s = Shader.Find("Hidden/ShellFur/SkipSubmesh");
        if (s == null)
            s = Shader.Find("Universal Render Pipeline/Unlit");
        _skipMat = new Material(s) { name = "ShellFur_Skip", hideFlags = HideFlags.HideAndDontSave };
        return _skipMat;
    }

    void HijackSlots()
    {
        if (_smr == null)
            return;
        Material[] cur = _smr.sharedMaterials;
        if (cur == null || cur.Length == 0)
            return;
        if (!_hijacked)
        {
            _originalShared = (Material[])cur.Clone();
            _hijacked = true;
        }
        Material[] next = (Material[])_originalShared.Clone();
        Material skip = GetSkip();
        if (furMaterialSlots != null)
        {
            for (int i = 0; i < furMaterialSlots.Length; i++)
            {
                int s = furMaterialSlots[i];
                if (s >= 0 && s < next.Length)
                    next[s] = skip;
            }
        }
        _smr.sharedMaterials = next;
    }

    void RestoreSlots()
    {
        if (!_hijacked || _smr == null || _originalShared == null)
            return;
        _smr.sharedMaterials = _originalShared;
        _originalShared = null;
        _hijacked = false;
    }

    void ReleaseGpu()
    {
        _boneBuffer?.Dispose();
        _boneBuffer = null;
        _localGuides?.Dispose();
        _localGuides = null;
        ReleaseBuffer(ref _bindBuffer);
        ReleaseBuffer(ref _skinnedBuffer);
        ReleaseBuffer(ref _finEdgeBuffer);
        ReleaseBuffer(ref _finVertexBuffer);
        ReleaseBuffer(ref _finCounterBuffer);
        ReleaseBuffer(ref _finArgsBuffer);
        _ready = false;
        _finsReady = false;
        _lastPrepareFrame = -1;
        _lastFinCameraFrame = -1;
    }

    static void ReleaseBuffer(ref GraphicsBuffer buf)
    {
        if (buf == null) return;
        buf.Release();
        buf = null;
    }

    void PrepareSkinFrame()
    {
        int frame = Time.frameCount;
        if (_lastPrepareFrame == frame)
            return;
        _lastPrepareFrame = frame;

        if (!_ready)
            RebuildBindData();
        if (!_ready || _smr == null || _bindMesh == null)
            return;

        EnsureSkinKernel();
        if (skinCompute == null || _kernelSkin < 0)
            return;

        Mesh skinMesh = _smr.sharedMesh;
        if (!_boneBuffer.UpdateFrom(_smr, skinMesh))
            return;

        int vcount = _bindVerts.Length;
        skinCompute.SetInt(VertexCountId, vcount);
        skinCompute.SetInt(BoneCountId, _boneBuffer.BoneCount);
        skinCompute.SetBuffer(_kernelSkin, BindVerticesId, _bindBuffer);
        skinCompute.SetBuffer(_kernelSkin, BoneMatricesId, _boneBuffer.Buffer);
        skinCompute.SetBuffer(_kernelSkin, SkinnedVerticesId, _skinnedBuffer);

        int groups = Mathf.CeilToInt(vcount / 64f);
        skinCompute.Dispatch(_kernelSkin, Mathf.Max(1, groups), 1, 1);

        // Local Grass anchors use the same bone matrices just uploaded.
        if (UseLocalGuidesMode)
            StepDynamicsIfNeeded();
    }

    void EnsureFinCapacityMatchesSegments()
    {
        if (!enableFins || !_ready)
            return;
        int segs = Mathf.Clamp(finSegments, MinFinSegments, MaxFinSegments);
        if (_finSegmentsBuilt == segs && _finsReady)
            return;

        // Rebuild only fin buffers when segment count changes.
        ReleaseBuffer(ref _finEdgeBuffer);
        ReleaseBuffer(ref _finVertexBuffer);
        ReleaseBuffer(ref _finCounterBuffer);
        ReleaseBuffer(ref _finArgsBuffer);
        BuildFinGpuResources();
    }

    void GenerateFinsForCamera(Camera camera)
    {
        if (!enableFins || !_ready || camera == null)
            return;

        // Lazy build when fins were off at first Rebuild, or compute asset was late-assigned.
        if (!_finsReady || _finEdgeBuffer == null)
            BuildFinGpuResources();
        if (!_finsReady || finCompute == null || _kernelGenFins < 0 || _finVertexBuffer == null)
            return;

        // One fin rebuild per camera per frame (game + scene view each get correct silhouette).
        int camId = camera.GetInstanceID();
        int frame = Time.frameCount;
        if (_lastFinCameraFrame == frame && _lastFinCameraId == camId)
            return;
        _lastFinCameraFrame = frame;
        _lastFinCameraId = camId;

        EnsureFinCapacityMatchesSegments();
        if (!_finsReady)
            return;

        int segs = Mathf.Clamp(finSegments, MinFinSegments, MaxFinSegments);

        finCompute.SetInt(FinEdgeCountId, _finEdgeCount);
        finCompute.SetInt(FinSegmentsId, segs);
        finCompute.SetInt(FinMaxVerticesId, _finMaxVertices);
        finCompute.SetVector(FinCameraPosId, camera.transform.position);
        finCompute.SetFloat(FurLengthId, furLength);
        finCompute.SetFloat(FinLengthScaleId, finLengthScale);
        finCompute.SetFloat(FinExtrudeWeightId, finExtrudeWeight);
        finCompute.SetFloat(FinSharpId, finSilhouetteSharpness);
        finCompute.SetFloat(FinBiasId, finSilhouetteBias);
        finCompute.SetFloat(FinPowerId, finSilhouettePower);
        finCompute.SetFloat(FinBandId, finBandStrength);
        finCompute.SetFloat(FinRootOffsetId, finRootOffset);
        finCompute.SetFloat(FinMinSilhouetteId, finMinSilhouette);
        ApplyPhysicsToCompute();

        // Reset
        finCompute.SetBuffer(_kernelReset, FinCounterId, _finCounterBuffer);
        finCompute.Dispatch(_kernelReset, 1, 1, 1);

        // Generate
        finCompute.SetBuffer(_kernelGenFins, SkinnedVerticesId, _skinnedBuffer);
        finCompute.SetBuffer(_kernelGenFins, FinEdgesId, _finEdgeBuffer);
        finCompute.SetBuffer(_kernelGenFins, FinVerticesId, _finVertexBuffer);
        finCompute.SetBuffer(_kernelGenFins, FinCounterId, _finCounterBuffer);
        int groups = Mathf.CeilToInt(_finEdgeCount / 64f);
        finCompute.Dispatch(_kernelGenFins, Mathf.Max(1, groups), 1, 1);

        // Args
        finCompute.SetBuffer(_kernelFinalize, FinCounterId, _finCounterBuffer);
        finCompute.SetBuffer(_kernelFinalize, FinDrawArgsId, _finArgsBuffer);
        finCompute.SetInt(FinMaxVerticesId, _finMaxVertices);
        finCompute.Dispatch(_kernelFinalize, 1, 1, 1);
    }

    void OnBeginCamera(ScriptableRenderContext ctx, Camera camera)
    {
        if (!isActiveAndEnabled)
            return;
        if (!Application.isPlaying && !drawInEditMode)
            return;
        if (camera == null || camera.cameraType == CameraType.Preview)
            return;

        if (!s_Active.Contains(this))
            s_Active.Add(this);
        PrepareSkinFrame();
        GenerateFinsForCamera(camera);
        DrawShells(camera);
        DrawFins(camera);
    }

    void DrawShells(Camera camera)
    {
        if (!_ready || _bindMesh == null || _skinnedBuffer == null)
            return;

        EnsureFurMaterial();
        Material mat = _runtimeFurMat != null ? _runtimeFurMat : furMaterial;
        if (mat == null)
            return;
        if (!mat.enableInstancing)
            mat.enableInstancing = true;
        if (mat.shader == null || !mat.shader.isSupported)
            return;

        GetShellDrawParams(out int drawCount, out float layerOffset);

        if (_mpb == null)
            _mpb = new MaterialPropertyBlock();
        _mpb.Clear();
        _mpb.SetBuffer(SkinnedVerticesId, _skinnedBuffer);
        _mpb.SetFloat(ShellCountId, Mathf.Max(shellCount, 2));
        _mpb.SetFloat(ShellLayerOffsetId, layerOffset);
        _mpb.SetFloat(FurLengthId, furLength);
        ApplyPhysicsToMpb(_mpb);

        if (_instanceMatrices == null || _instanceMatrices.Length < drawCount)
        {
            _instanceMatrices = new Matrix4x4[MaxShells];
            for (int i = 0; i < MaxShells; i++)
                _instanceMatrices[i] = Matrix4x4.identity;
        }

        if (_smr != null)
        {
            Bounds lb = _bindMesh.bounds;
            float pad = furLength * 2f + _smr.bounds.extents.magnitude * 0.1f;
            lb.Expand(pad);
            _bindMesh.bounds = lb;
        }

        Graphics.DrawMeshInstanced(
            _bindMesh,
            0,
            mat,
            _instanceMatrices,
            drawCount,
            _mpb,
            shadowCasting,
            receiveShadows,
            gameObject.layer,
            camera,
            LightProbeUsage.Off,
            null);
    }

    void GetShellDrawParams(out int drawCount, out float layerOffset)
    {
        drawCount = shellCount;
        layerOffset = 0f;
        if (finsOnly)
        {
            drawCount = 1;
            layerOffset = hideBaseMesh ? 1f : 0f;
        }
        else if (hideBaseMesh)
        {
            drawCount = Mathf.Max(1, shellCount - 1);
            layerOffset = 1f;
        }
    }

    void DrawDepthHull(IRasterCommandBuffer cmd)
    {
        if (cmd == null || !_ready || _bindMesh == null || _skinnedBuffer == null)
            return;

        EnsureFurMaterial();
        Material mat = _runtimeFurMat != null ? _runtimeFurMat : furMaterial;
        if (mat == null || mat.shader == null || !mat.shader.isSupported)
            return;
        if (!mat.enableInstancing)
            mat.enableInstancing = true;

        int pass = mat.FindPass("VolumetricDepth");
        if (pass < 0)
        {
            Debug.LogWarning("[ShellFurGpuSkin] VolumetricDepth pass missing; volumetric fur depth skipped.", this);
            return;
        }

        GetShellDrawParams(out int drawCount, out float layerOffset);
        if (_mpb == null)
            _mpb = new MaterialPropertyBlock();
        _mpb.Clear();
        _mpb.SetBuffer(SkinnedVerticesId, _skinnedBuffer);
        _mpb.SetFloat(ShellCountId, Mathf.Max(shellCount, 2));
        _mpb.SetFloat(FurLengthId, furLength);
        ApplyPhysicsToMpb(_mpb);

        for (int i = 0; i < drawCount; i++)
        {
            _mpb.SetFloat(ShellLayerOffsetId, layerOffset + i);
            cmd.DrawMesh(_bindMesh, Matrix4x4.identity, mat, 0, pass, _mpb);
        }
    }

    void DrawFins(Camera camera)
    {
        if (!enableFins || !_ready || !_finsReady)
            return;
        if (_finVertexBuffer == null || _finArgsBuffer == null)
            return;

        EnsureFinMaterial();
        Material mat = _runtimeFinMat;
        if (mat == null || mat.shader == null || !mat.shader.isSupported)
            return;

        CopyShellLookToFin(mat);

        if (_mpbFin == null)
            _mpbFin = new MaterialPropertyBlock();
        _mpbFin.Clear();
        _mpbFin.SetBuffer(FinVerticesId, _finVertexBuffer);
        _mpbFin.SetFloat(FurLengthId, furLength);
        ApplyPhysicsToMpb(_mpbFin);
        _mpbFin.SetFloat(FinRootOpacityId, finRootOpacity);
        _mpbFin.SetFloat(FinTipOpacityId, finTipOpacity);
        _mpbFin.SetFloat(FinOpacityFadeStartId, finOpacityFadeStart);
        _mpbFin.SetFloat(FinOpacityFadeEndId, finOpacityFadeEnd);
        _mpbFin.SetFloat(FinOpacityPowerId, finOpacityPower);
        _mpbFin.SetFloat(ShellCountId, Mathf.Max(shellCount, 2));

        // Keep root/tip colors in lockstep with the shell fur material.
        Material shellSrc = _runtimeFurMat != null ? _runtimeFurMat : furMaterial;
        if (shellSrc != null)
        {
            if (shellSrc.HasProperty(BaseColorId))
                _mpbFin.SetColor(BaseColorId, shellSrc.GetColor(BaseColorId));
            if (shellSrc.HasProperty(TipColorId))
                _mpbFin.SetColor(TipColorId, shellSrc.GetColor(TipColorId));
        }

        Bounds bounds;
        if (_smr != null)
        {
            bounds = _smr.bounds;
            bounds.Expand(furLength * 2f * finLengthScale + 0.05f);
        }
        else
        {
            bounds = new Bounds(transform.position, Vector3.one * (furLength * 4f + 1f));
        }

        var cast = finCastShadows ? shadowCasting : ShadowCastingMode.Off;

        Graphics.DrawProceduralIndirect(
            mat,
            bounds,
            MeshTopology.Triangles,
            _finArgsBuffer,
            0,
            camera,
            _mpbFin,
            cast,
            receiveShadows,
            gameObject.layer);
    }
}

using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// GPU-skinning shell fur (Scheme B, tier 2):
/// 1) Upload bone matrices from SkinnedMeshRenderer
/// 2) Compute shader skins bind-pose fur verts once (world space)
/// 3) DrawMeshInstanced N shell layers reading the skinned buffer (extrude only)
///
/// SMR is not used to draw fur — only bones + source mesh data.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class ShellFurGpuSkinRenderer : MonoBehaviour
{
    public const string ShaderName = "Custom/ShellFurGpuSkinned";
    const int MaxShells = 128;

    static readonly int ShellCountId = Shader.PropertyToID("_ShellCount");
    static readonly int ShellLayerOffsetId = Shader.PropertyToID("_ShellLayerOffset");
    static readonly int FurLengthId = Shader.PropertyToID("_FurLength");
    static readonly int GravityId = Shader.PropertyToID("_Gravity");
    static readonly int GravityDirId = Shader.PropertyToID("_GravityDir");
    static readonly int SkinnedVerticesId = Shader.PropertyToID("_SkinnedVertices");
    static readonly int BindVerticesId = Shader.PropertyToID("_BindVertices");
    static readonly int BoneMatricesId = Shader.PropertyToID("_BoneMatrices");
    static readonly int VertexCountId = Shader.PropertyToID("_VertexCount");
    static readonly int BoneCountId = Shader.PropertyToID("_BoneCount");

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

    [Header("Physics")]
    [SerializeField] float gravityStrength = 0.35f;
    [SerializeField] Vector3 gravityDirection = Vector3.down;

    [Header("Rendering")]
    [SerializeField] ShadowCastingMode shadowCasting = ShadowCastingMode.On;
    [SerializeField] bool receiveShadows = true;
    [SerializeField] bool drawInEditMode = true;
    [SerializeField] ComputeShader skinCompute;

    [Header("Debug")]
    [SerializeField] bool logOnce;

    SkinnedMeshRenderer _smr;
    Mesh _bindMesh;
    ShellFurGpuSkinTypes.BindVertex[] _bindVerts;
    GraphicsBuffer _bindBuffer;
    GraphicsBuffer _skinnedBuffer;
    ShellFurBoneBuffer _boneBuffer;
    MaterialPropertyBlock _mpb;
    Matrix4x4[] _instanceMatrices;
    Material _ownedMaterial;
    Material _runtimeFurMat;
    static Material _skipMat;
    Material[] _originalShared;
    bool _hijacked;
    int _lastPrepareFrame = -1;
    bool _logged;
    bool _ready;
    int _kernel = -1;

    public bool IsReady => _ready;

    void OnEnable()
    {
        CacheRefs();
        EnsureMaterial();
        RebuildBindData();
        ApplySourceRendererState();
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RestoreSourceRendererState();
        ReleaseGpu();
    }

    void OnDestroy()
    {
        RestoreSourceRendererState();
        ReleaseGpu();
        if (_ownedMaterial != null)
        {
            if (Application.isPlaying) Destroy(_ownedMaterial);
            else DestroyImmediate(_ownedMaterial);
        }
        if (_bindMesh != null && bindFurMeshOverride == null)
        {
            if (Application.isPlaying) Destroy(_bindMesh);
            else DestroyImmediate(_bindMesh);
        }
    }

    void OnValidate()
    {
        shellCount = Mathf.Clamp(shellCount, 2, MaxShells);
        furLength = Mathf.Max(0.001f, furLength);
        if (furMaterialSlots == null || furMaterialSlots.Length == 0)
            furMaterialSlots = new[] { 0 };
        CacheRefs();
        ApplySourceRendererState();
    }

    [ContextMenu("Rebuild GPU Skin Bind Mesh")]
    public void RebuildBindData()
    {
        ReleaseGpu();
        _ready = false;
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
            // Rebuild bind vertex array from override mesh channels
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

        if (skinCompute != null)
            _kernel = skinCompute.FindKernel("CSSkin");

        _mpb = new MaterialPropertyBlock();
        _instanceMatrices = new Matrix4x4[MaxShells];
        for (int i = 0; i < MaxShells; i++)
            _instanceMatrices[i] = Matrix4x4.identity;

        _ready = true;
        if (logOnce && !_logged)
        {
            Debug.Log($"[{nameof(ShellFurGpuSkinRenderer)}] Ready verts={vcount} bones={source.bindposes?.Length} mesh={_bindMesh.name}", this);
            _logged = true;
        }
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

        if (skinCompute == null)
            skinCompute = Resources.Load<ComputeShader>("ShellFurGpuSkin");
        // Also try direct asset path load in editor via Shader - compute assigned in inspector preferably
    }

    void EnsureMaterial()
    {
        if (furMaterial != null)
        {
            if (!furMaterial.enableInstancing)
                furMaterial.enableInstancing = true;
            _runtimeFurMat = furMaterial;
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
            // Full fur replacement: hide SMR mesh draw
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
        _bindBuffer?.Release();
        _bindBuffer = null;
        _skinnedBuffer?.Release();
        _skinnedBuffer = null;
        _ready = false;
    }

    void PrepareFrame()
    {
        int frame = Time.frameCount;
        if (_lastPrepareFrame == frame)
            return;
        _lastPrepareFrame = frame;

        if (!_ready)
            RebuildBindData();
        if (!_ready || _smr == null || _bindMesh == null)
            return;

        if (skinCompute == null || _kernel < 0)
        {
            // Try load compute by name from project
            #if UNITY_EDITOR
            if (skinCompute == null)
            {
                skinCompute = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/ShellFur/Shaders/ShellFurGpuSkin.compute");
                if (skinCompute != null)
                    _kernel = skinCompute.FindKernel("CSSkin");
            }
            #endif
            if (skinCompute == null || _kernel < 0)
                return;
        }

        Mesh skinMesh = _smr.sharedMesh;
        if (!_boneBuffer.UpdateFrom(_smr, skinMesh))
            return;

        int vcount = _bindVerts.Length;
        skinCompute.SetInt(VertexCountId, vcount);
        skinCompute.SetInt(BoneCountId, _boneBuffer.BoneCount);
        skinCompute.SetBuffer(_kernel, BindVerticesId, _bindBuffer);
        skinCompute.SetBuffer(_kernel, BoneMatricesId, _boneBuffer.Buffer);
        skinCompute.SetBuffer(_kernel, SkinnedVerticesId, _skinnedBuffer);

        int groups = Mathf.CeilToInt(vcount / 64f);
        skinCompute.Dispatch(_kernel, Mathf.Max(1, groups), 1, 1);
    }

    void OnBeginCamera(ScriptableRenderContext ctx, Camera camera)
    {
        if (!isActiveAndEnabled)
            return;
        if (!Application.isPlaying && !drawInEditMode)
            return;
        if (camera == null || camera.cameraType == CameraType.Preview)
            return;

        PrepareFrame();
        DrawShells(camera);
    }

    void DrawShells(Camera camera)
    {
        if (!_ready || _bindMesh == null || _skinnedBuffer == null || _runtimeFurMat == null && furMaterial == null)
            return;

        EnsureMaterial();
        Material mat = _runtimeFurMat != null ? _runtimeFurMat : furMaterial;
        if (mat == null || !mat.enableInstancing)
        {
            if (mat != null)
                mat.enableInstancing = true;
        }
        if (mat == null || mat.shader == null || !mat.shader.isSupported)
            return;

        int drawCount = shellCount;
        float layerOffset = 0f;
        if (hideBaseMesh)
        {
            drawCount = Mathf.Max(1, shellCount - 1);
            layerOffset = 1f;
        }

        if (_mpb == null)
            _mpb = new MaterialPropertyBlock();
        _mpb.Clear();
        _mpb.SetBuffer(SkinnedVerticesId, _skinnedBuffer);
        _mpb.SetFloat(ShellCountId, Mathf.Max(shellCount, 2));
        _mpb.SetFloat(ShellLayerOffsetId, layerOffset);
        _mpb.SetFloat(FurLengthId, furLength);
        _mpb.SetFloat(GravityId, gravityStrength);
        _mpb.SetVector(GravityDirId,
            gravityDirection.sqrMagnitude > 1e-6f ? gravityDirection.normalized : Vector3.down);

        // World-space skin: object matrices are identity
        if (_instanceMatrices == null || _instanceMatrices.Length < drawCount)
        {
            _instanceMatrices = new Matrix4x4[MaxShells];
            for (int i = 0; i < MaxShells; i++)
                _instanceMatrices[i] = Matrix4x4.identity;
        }

        // Expand mesh bounds using SMR bounds so culling works
        if (_smr != null)
        {
            Bounds wb = _smr.bounds;
            wb.Expand(furLength * 2f);
            // mesh bounds stay local; DrawMeshInstanced with I uses mesh.bounds — expand bind mesh
            Bounds lb = _bindMesh.bounds;
            float pad = furLength * 2f + wb.extents.magnitude * 0.1f;
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
}

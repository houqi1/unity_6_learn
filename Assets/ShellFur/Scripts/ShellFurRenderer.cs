using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Shell fur via GPU instancing. Supports static MeshFilter and animated SkinnedMeshRenderer
/// (per-frame BakeMesh). Optional edge fins for static meshes; skinned path skips fins for now.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class ShellFurRenderer : MonoBehaviour
{
    public const string ShaderName = "Custom/ShellFur";
    public const string FinShaderName = "Custom/ShellFurFin";
    const int MaxShells = 128;

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
    static readonly int FinExtrudeWeightId = Shader.PropertyToID("_FinExtrudeWeight");
    static readonly int FinSharpId = Shader.PropertyToID("_FinSilhouetteSharpness");
    static readonly int FinBiasId = Shader.PropertyToID("_FinSilhouetteBias");
    static readonly int FinPowerId = Shader.PropertyToID("_FinSilhouettePower");
    static readonly int FinBandId = Shader.PropertyToID("_FinBandStrength");
    static readonly int FinRootId = Shader.PropertyToID("_FinRootOffset");
    static readonly int FinLenScaleId = Shader.PropertyToID("_FinLengthScale");
    static readonly int FinRootOpacityId = Shader.PropertyToID("_FinRootOpacity");
    static readonly int FinTipOpacityId = Shader.PropertyToID("_FinTipOpacity");
    static readonly int FinOpacityFadeStartId = Shader.PropertyToID("_FinOpacityFadeStart");
    static readonly int FinOpacityFadeEndId = Shader.PropertyToID("_FinOpacityFadeEnd");
    static readonly int FinOpacityPowerId = Shader.PropertyToID("_FinOpacityPower");

    [Header("Source")]
    [Tooltip("Optional mesh override (static path). Skinned path always bakes from SkinnedMeshRenderer.")]
    [SerializeField] Mesh meshOverride;

    [Tooltip("Shell fur material (Custom/ShellFur).")]
    [SerializeField] Material furMaterial;

    [Header("Skinned Mesh")]
    [Tooltip("Bake skinned pose every frame so shells follow animation (required for SkinnedMeshRenderer).")]
    [SerializeField] bool bakeSkinnedEveryFrame = true;
    [Tooltip("Also bake in edit mode when the scene view updates (can be costly).")]
    [SerializeField] bool bakeSkinnedInEditMode = true;

    [Header("Smooth Normals")]
    [Tooltip("Extrude shells along smooth normals stored in vertex colors (RGB = n*0.5+0.5). Bake with Tools/Shell Fur/...")]
    [SerializeField] bool useSmoothNormalsFromVertexColor = false;
    [Tooltip("Skinned only: after BakeMesh, recompute smooth normals into vertex colors (VC is not bone-skinned).")]
    [SerializeField] bool recomputeSmoothNormalsAfterSkinBake = true;
    [Tooltip("180 = fully smooth (position-weld). Lower keeps hard edges above this face angle.")]
    [Range(1f, 180f)]
    [SerializeField] float smoothNormalMaxAngle = 180f;

    [Header("Material Slots (Scheme A)")]
    [Tooltip("When on, only the listed material slots / submeshes get shell fur; other slots stay on the source renderer.")]
    [SerializeField] bool useMaterialSlotOnly = false;
    [Tooltip("One or more material slot / submesh indices for fur (e.g. 1, 3).")]
    [SerializeField] int[] furMaterialSlots = { 0 };
    [Tooltip("Replace those slots with an invisible material so the default renderer does not double-draw fur.")]
    [SerializeField] bool hideSourceFurSlot = true;
    [Tooltip("Slot mode only: skip shell layer 0 (solid base skin under fur); only draw fur shell layers.")]
    [SerializeField] bool hideBaseMesh = false;

    [Header("Shells")]
    [Range(2, MaxShells)]
    [SerializeField] int shellCount = 32;

    [Min(0.001f)]
    [SerializeField] float furLength = 0.08f;

    [Header("Fins (static meshes only for now)")]
    [SerializeField] bool enableFins = true;
    [Tooltip("When enabled, skips multi-layer shells but still draws the base mesh (layer 0 skin) plus fins.")]
    [SerializeField] bool finsOnly = false;
    [Tooltip("Optional override. If empty, fins are built from the shell source mesh (static only).")]
    [SerializeField] Mesh finMeshOverride;
    [SerializeField] bool rebuildFinsOnEnable = true;
    [Tooltip("Quads stacked along each fin (1=flat card, 4+ recommended for gravity curves).")]
    [Range(ShellFurFinBuilder.MinSegments, ShellFurFinBuilder.MaxSegments)]
    [SerializeField] int finSegments = 4;
    [Tooltip("Overall fin erect strength (0 = flat, 1 = default).")]
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
    [Range(0.99f, 1f)]
    [SerializeField] float finSkipCoplanarDot = 0.9998f;

    [Header("Physics (pushed each frame via MaterialPropertyBlock)")]
    [SerializeField] float gravityStrength = 0.35f;
    [SerializeField] Vector3 gravityDirection = Vector3.down;
    [Tooltip("Nonlinear droop: bend ∝ pow(layer, power). 2 = classic tip-heavy arc; higher = more tip-only sag.")]
    [Range(0.5f, 4f)]
    [SerializeField] float gravityPower = 2f;

    [Header("Dynamics (guide strand: root pinned to object)")]
    [Tooltip("Root pinned to object. Modes: Spring / Verlet / Grass / Bone (MaxScript tip spring). Shell = pure extrude + chain δ (no GravityBend while chain on).")]
    [SerializeField] ShellFurDynamics dynamics = new ShellFurDynamics();

    [Header("Rendering")]
    [SerializeField] ShadowCastingMode shadowCasting = ShadowCastingMode.On;
    [SerializeField] bool receiveShadows = true;
    [SerializeField] bool drawInEditMode = true;
    [Tooltip("When Material Slot mode is off: disable entire source renderer (full-mesh fur). Ignored when Material Slot mode is on.")]
    [SerializeField] bool disableSourceRenderer = true;

    [Header("Debug")]
    [SerializeField] bool showBoundsGizmo = true;
    [SerializeField] bool logDrawOnce = false;

    MeshFilter _meshFilter;
    MeshRenderer _meshRenderer;
    SkinnedMeshRenderer _skinned;
    MaterialPropertyBlock _mpb;
    Matrix4x4[] _matrices;
    int _cachedShellCount = -1;
    Material _ownedMaterial;
    Material _finMaterial;
    static Material _skipSubmeshMaterial;
    Mesh _runtimeFinMesh;
    Mesh _finSourceBuiltFrom;
    int _finSegmentsBuilt = -1;
    int _finSlotsKeyBuilt = int.MinValue;
    Mesh _bakedMesh;
    int _lastSkinPrepareFrame = -1;
    Material[] _originalSharedMaterials;
    bool _furSlotHijacked;
    bool _loggedMissingShader;
    bool _loggedMissingFinShader;
    bool _loggedDraw;
    bool _loggedMissingMesh;
    bool _loggedFinBuildFail;
    bool _loggedBadSlot;
    bool _loggedSkinnedNoFins;
    bool _dynamicsSteppedThisFrame;
    int _dynamicsStepFrame = -1;

    public int ShellCount
    {
        get => shellCount;
        set => shellCount = Mathf.Clamp(value, 2, MaxShells);
    }

    public float FurLength
    {
        get => furLength;
        set => furLength = Mathf.Max(0.001f, value);
    }

    public Material FurMaterial
    {
        get => furMaterial;
        set => furMaterial = value;
    }

    public bool EnableFins
    {
        get => enableFins;
        set => enableFins = value;
    }

    public bool FinsOnly
    {
        get => finsOnly;
        set => finsOnly = value;
    }

    public bool UseMaterialSlotOnly
    {
        get => useMaterialSlotOnly;
        set
        {
            useMaterialSlotOnly = value;
            ApplySourceRendererState();
        }
    }

    public int[] FurMaterialSlots
    {
        get => furMaterialSlots;
        set
        {
            furMaterialSlots = value;
            ApplySourceRendererState();
            if (enableFins && finMeshOverride == null && !IsSkinned)
                RebuildFins();
        }
    }

    public bool IsSkinned => _skinned != null;

    public ShellFurDynamics Dynamics => dynamics;

    /// <summary>null = whole mesh; otherwise valid submesh indices for fur.</summary>
    int[] GetActiveFurSubmeshes(Mesh mesh)
    {
        if (!useMaterialSlotOnly)
            return null;

        return ResolveValidSlots(mesh != null ? mesh.subMeshCount : 0, logErrors: true);
    }

    int[] ResolveValidSlots(int subMeshCount, bool logErrors)
    {
        if (furMaterialSlots == null || furMaterialSlots.Length == 0)
        {
            if (logErrors && !_loggedBadSlot)
            {
                Debug.LogWarning($"[{nameof(ShellFurRenderer)}] Use Material Slot Only is on but Fur Material Slots is empty on '{name}'.", this);
                _loggedBadSlot = true;
            }
            return System.Array.Empty<int>();
        }

        var list = new System.Collections.Generic.List<int>(furMaterialSlots.Length);
        for (int i = 0; i < furMaterialSlots.Length; i++)
        {
            int s = furMaterialSlots[i];
            if (s < 0)
                continue;
            if (subMeshCount > 0 && s >= subMeshCount)
            {
                if (logErrors && !_loggedBadSlot)
                {
                    Debug.LogWarning(
                        $"[{nameof(ShellFurRenderer)}] Fur slot {s} ≥ subMeshCount {subMeshCount} on '{name}'. Skipped.",
                        this);
                    _loggedBadSlot = true;
                }
                continue;
            }
            if (!list.Contains(s))
                list.Add(s);
        }

        list.Sort();
        return list.ToArray();
    }

    static int ComputeSlotsKey(int[] slots)
    {
        if (slots == null)
            return -1;
        unchecked
        {
            int h = 17;
            for (int i = 0; i < slots.Length; i++)
                h = h * 31 + slots[i];
            return h;
        }
    }

    /// <summary>Mesh used for drawing shells (baked pose when skinned).</summary>
    Mesh ActiveDrawMesh
    {
        get
        {
            if (meshOverride != null && _skinned == null)
                return meshOverride;
            if (_skinned != null)
                return _bakedMesh != null ? _bakedMesh : _skinned.sharedMesh;
            return _meshFilter != null ? _meshFilter.sharedMesh : null;
        }
    }

    Mesh ActiveFinMesh
    {
        get
        {
            if (finMeshOverride != null)
                return finMeshOverride;
            return _runtimeFinMesh;
        }
    }

    Renderer SourceRenderer => _skinned != null ? (Renderer)_skinned : _meshRenderer;

    Matrix4x4 DrawLocalToWorld
    {
        get
        {
            if (_skinned != null)
                return _skinned.localToWorldMatrix;
            return transform.localToWorldMatrix;
        }
    }

    void OnEnable()
    {
        CacheComponents();
        EnsureBuffers(shellCount);
        EnsureMaterial();
        EnsureBakedMesh();
        ApplySourceRendererState();

        if (enableFins && rebuildFinsOnEnable && !IsSkinned)
            RebuildFins();

        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RestoreSourceRendererState();
        dynamics?.ResetState();
        _dynamicsSteppedThisFrame = false;
        _dynamicsStepFrame = -1;
    }

    void LateUpdate()
    {
        dynamics?.ValidateNodeCount();
        StepDynamicsIfNeeded();
        if (dynamics != null && dynamics.showGuideChain && Application.isPlaying)
            dynamics.DrawGuideChainDebugLines();
    }

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
        Vector3 anchor = _skinned != null ? _skinned.transform.position : transform.position;

        dynamics.Evaluate(anchor, gDir, furLength, dt, gravityStrength, gravityPower);
        _dynamicsStepFrame = frame;
        _dynamicsSteppedThisFrame = true;
    }

    void OnDrawGizmos()
    {
        if (dynamics == null || !dynamics.showGuideChain)
            return;
        // Keep chain up to date in edit mode when gizmos refresh.
        if (!Application.isPlaying)
            StepDynamicsIfNeeded();
        dynamics.DrawGuideChainGizmos();
    }

    void OnDestroy()
    {
        RestoreSourceRendererState();
        DestroyOwned(_ownedMaterial);
        _ownedMaterial = null;
        DestroyOwned(_finMaterial);
        _finMaterial = null;
        DestroyOwned(_runtimeFinMesh);
        _runtimeFinMesh = null;
        DestroyOwned(_bakedMesh);
        _bakedMesh = null;
    }

    void OnValidate()
    {
        shellCount = Mathf.Clamp(shellCount, 2, MaxShells);
        furLength = Mathf.Max(0.001f, furLength);
        if (furMaterialSlots == null || furMaterialSlots.Length == 0)
            furMaterialSlots = new[] { 0 };
        finSegments = Mathf.Clamp(finSegments, ShellFurFinBuilder.MinSegments, ShellFurFinBuilder.MaxSegments);
        CacheComponents();
        EnsureBuffers(shellCount);
        EnsureMaterial();
        ApplySmoothNormalKeyword();
        ApplySourceRendererState();

        int slotsKey = ComputeSlotsKey(GetActiveFurSubmeshes(ActiveDrawMesh));
        if (enableFins && !IsSkinned && finMeshOverride == null && _runtimeFinMesh != null &&
            (_finSegmentsBuilt != finSegments || _finSlotsKeyBuilt != slotsKey))
            RebuildFins();
    }

    [ContextMenu("Rebuild Fin Mesh")]
    public void RebuildFins()
    {
        if (IsSkinned)
        {
            // Skinned fin support deferred — avoid building from bind-pose mesh.
            return;
        }

        _loggedFinBuildFail = false;
        Mesh source = ActiveDrawMesh;
        if (source == null)
            return;

        if (finMeshOverride != null)
            return;

        finSegments = Mathf.Clamp(finSegments, ShellFurFinBuilder.MinSegments, ShellFurFinBuilder.MaxSegments);
        int[] slots = GetActiveFurSubmeshes(source);

        DestroyOwned(_runtimeFinMesh);
        _runtimeFinMesh = ShellFurFinBuilder.Build(source, 1e-5f, finSkipCoplanarDot, finSegments, slots);
        _finSourceBuiltFrom = source;
        _finSegmentsBuilt = finSegments;
        _finSlotsKeyBuilt = ComputeSlotsKey(slots);

        if (_runtimeFinMesh != null)
            _runtimeFinMesh.hideFlags = HideFlags.HideAndDontSave;
        else if (!_loggedFinBuildFail)
        {
            Debug.LogWarning(
                $"[{nameof(ShellFurRenderer)}] Fin mesh build failed for '{name}'. Is the mesh Read/Write enabled?",
                this);
            _loggedFinBuildFail = true;
        }
    }

    void CacheComponents()
    {
        _skinned = GetComponent<SkinnedMeshRenderer>();
        if (_skinned == null)
            _skinned = GetComponentInChildren<SkinnedMeshRenderer>();

        if (_meshFilter == null)
            _meshFilter = GetComponent<MeshFilter>();
        if (_meshRenderer == null)
            _meshRenderer = GetComponent<MeshRenderer>();
        if (_mpb == null)
            _mpb = new MaterialPropertyBlock();
    }

    void EnsureBakedMesh()
    {
        if (_skinned == null)
            return;

        if (_bakedMesh == null)
        {
            _bakedMesh = new Mesh
            {
                name = "ShellFur_SkinnedBake",
                hideFlags = HideFlags.HideAndDontSave
            };
        }
    }

    /// <summary>
    /// Bake current skinned pose once per frame (shared by all cameras).
    /// </summary>
    void PrepareSkinnedPoseIfNeeded()
    {
        if (_skinned == null || !bakeSkinnedEveryFrame)
            return;

        if (!Application.isPlaying && !bakeSkinnedInEditMode)
            return;

        int frame = Time.frameCount;
        if (_lastSkinPrepareFrame == frame)
            return;
        _lastSkinPrepareFrame = frame;

        EnsureBakedMesh();
        // Bake into local space of the SkinnedMeshRenderer; draw with its localToWorldMatrix.
        _skinned.BakeMesh(_bakedMesh, true);

        // Vertex colors are not skinned; rebuild smooth normals from the deformed pose for extrusion.
        if (useSmoothNormalsFromVertexColor && recomputeSmoothNormalsAfterSkinBake)
        {
            int[] slots = GetActiveFurSubmeshes(_bakedMesh);
            ShellFurNormalUtility.BakeSmoothNormalsToVertexColorsRuntime(
                _bakedMesh, slots, smoothNormalMaxAngle);
        }
    }

    void EnsureBuffers(int count)
    {
        count = Mathf.Clamp(count, 2, MaxShells);
        if (_matrices != null && _cachedShellCount == count)
            return;

        _matrices = new Matrix4x4[count];
        _cachedShellCount = count;
    }

    void ApplySmoothNormalKeyword()
    {
        if (furMaterial == null)
            return;

        if (useSmoothNormalsFromVertexColor)
            furMaterial.EnableKeyword("_USE_SMOOTH_NORMALS_VC");
        else
            furMaterial.DisableKeyword("_USE_SMOOTH_NORMALS_VC");

        if (furMaterial.HasProperty("_UseSmoothNormalsVC"))
            furMaterial.SetFloat("_UseSmoothNormalsVC", useSmoothNormalsFromVertexColor ? 1f : 0f);
    }

    void EnsureMaterial()
    {
        if (furMaterial != null)
        {
            if (!furMaterial.enableInstancing)
                furMaterial.enableInstancing = true;
            ApplySmoothNormalKeyword();
            return;
        }

        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            if (!_loggedMissingShader)
            {
                Debug.LogWarning($"[{nameof(ShellFurRenderer)}] Shader '{ShaderName}' not found on '{name}'.", this);
                _loggedMissingShader = true;
            }
            return;
        }

        _ownedMaterial = new Material(shader)
        {
            name = "ShellFur (Runtime)",
            enableInstancing = true,
            hideFlags = HideFlags.HideAndDontSave
        };
        _ownedMaterial.EnableKeyword("_USE_PROCEDURAL");
        _ownedMaterial.SetFloat("_UseProcedural", 1f);
        _ownedMaterial.SetColor("_BaseColor", new Color(0.32f, 0.18f, 0.10f, 1f));
        _ownedMaterial.SetColor("_TipColor", new Color(0.90f, 0.72f, 0.45f, 1f));
        _ownedMaterial.SetFloat("_Density", 140f);
        _ownedMaterial.SetFloat("_Thickness", 0.55f);
        furMaterial = _ownedMaterial;
        ApplySmoothNormalKeyword();
    }

    void EnsureFinMaterial()
    {
        if (_finMaterial != null)
            return;

        Shader finShader = Shader.Find(FinShaderName);
        if (finShader == null)
        {
            if (!_loggedMissingFinShader)
            {
                Debug.LogWarning($"[{nameof(ShellFurRenderer)}] Shader '{FinShaderName}' not found.", this);
                _loggedMissingFinShader = true;
            }
            return;
        }

        _finMaterial = new Material(finShader)
        {
            name = "ShellFurFin (Runtime)",
            hideFlags = HideFlags.HideAndDontSave
        };
        _finMaterial.SetFloat("_Cull", 0f);
    }

    void SyncFinMaterialFromShell()
    {
        EnsureFinMaterial();
        if (_finMaterial == null || furMaterial == null)
            return;

        _finMaterial.CopyPropertiesFromMaterial(furMaterial);
        _finMaterial.SetFloat("_Cull", 0f);
        _finMaterial.enableInstancing = false;
    }

    void ApplySourceRendererState()
    {
        Renderer r = SourceRenderer;
        if (r == null)
            return;

        if (useMaterialSlotOnly)
        {
            r.enabled = true;
            if (hideSourceFurSlot)
                HijackFurSlotMaterial();
            else
                RestoreFurSlotMaterial();
        }
        else
        {
            RestoreFurSlotMaterial();
            r.enabled = !disableSourceRenderer;
        }
    }

    void RestoreSourceRendererState()
    {
        RestoreFurSlotMaterial();
        Renderer r = SourceRenderer;
        if (r != null && disableSourceRenderer && !useMaterialSlotOnly)
            r.enabled = true;
    }

    static Material GetSkipSubmeshMaterial()
    {
        if (_skipSubmeshMaterial != null)
            return _skipSubmeshMaterial;

        Shader shader = Shader.Find("Hidden/ShellFur/SkipSubmesh");
        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Unlit");

        _skipSubmeshMaterial = new Material(shader)
        {
            name = "ShellFur_SkipSubmesh",
            hideFlags = HideFlags.HideAndDontSave
        };
        if (_skipSubmeshMaterial.HasProperty("_BaseColor"))
            _skipSubmeshMaterial.SetColor("_BaseColor", new Color(0, 0, 0, 0));
        if (_skipSubmeshMaterial.HasProperty("_Surface"))
            _skipSubmeshMaterial.SetFloat("_Surface", 1f);
        return _skipSubmeshMaterial;
    }

    void HijackFurSlotMaterial()
    {
        Renderer r = SourceRenderer;
        if (r == null)
            return;

        Material[] current = r.sharedMaterials;
        if (current == null || current.Length == 0)
            return;

        if (!_furSlotHijacked)
        {
            _originalSharedMaterials = (Material[])current.Clone();
            _furSlotHijacked = true;
        }

        Material[] next = (Material[])_originalSharedMaterials.Clone();
        Material skip = GetSkipSubmeshMaterial();
        int[] slots = ResolveValidSlots(next.Length, logErrors: false);
        for (int i = 0; i < slots.Length; i++)
        {
            int slot = slots[i];
            if (slot >= 0 && slot < next.Length)
                next[slot] = skip;
        }

        r.sharedMaterials = next;
    }

    void RestoreFurSlotMaterial()
    {
        Renderer r = SourceRenderer;
        if (!_furSlotHijacked || r == null || _originalSharedMaterials == null)
            return;

        r.sharedMaterials = _originalSharedMaterials;
        _originalSharedMaterials = null;
        _furSlotHijacked = false;
    }

    void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!isActiveAndEnabled)
            return;

        if (!Application.isPlaying && !drawInEditMode)
            return;

        if (camera == null || camera.cameraType == CameraType.Preview)
            return;

        PrepareSkinnedPoseIfNeeded();

        bool drawFins = (enableFins || finsOnly) && !IsSkinned;
        if (IsSkinned && (enableFins || finsOnly) && !_loggedSkinnedNoFins)
        {
            Debug.Log(
                $"[{nameof(ShellFurRenderer)}] Skinned mesh: shells follow animation; fins are skipped until skinned fin support is added.",
                this);
            _loggedSkinnedNoFins = true;
        }

        if (finsOnly && !IsSkinned)
            DrawShells(camera, shellLayersOverride: 1);
        else if (finsOnly && IsSkinned)
            DrawShells(camera, shellLayersOverride: 1); // base skin layer only + no fins
        else
            DrawShells(camera, shellLayersOverride: -1);

        if (drawFins)
            DrawFins(camera);
    }

    void ApplyPhysicsToMpb()
    {
        Vector3 gDir = gravityDirection.sqrMagnitude > 1e-6f ? gravityDirection.normalized : Vector3.down;
        _mpb.SetFloat(GravityId, gravityStrength);
        _mpb.SetVector(GravityDirId, gDir);
        _mpb.SetFloat(GravityPowerId, Mathf.Max(0.01f, gravityPower));

        // Ensure chain is stepped before draw (edit mode / first frame).
        if (dynamics != null && dynamics.enabled)
        {
            if (!_dynamicsSteppedThisFrame || _dynamicsStepFrame != Time.frameCount)
                StepDynamicsIfNeeded();
        }

        bool useChain = dynamics != null && dynamics.enabled && dynamics.HasSamples;
        if (useChain)
        {
            _mpb.SetFloat(UseFurChainId, 1f);
            _mpb.SetFloat(FurChainCountId, dynamics.SampleCount);
            _mpb.SetVectorArray(FurChainId, dynamics.BendSamples);
            Vector3 erect = dynamics.ErectDirection;
            _mpb.SetVector(FurChainErectId, new Vector4(erect.x, erect.y, erect.z, 0f));
        }
        else
        {
            _mpb.SetFloat(UseFurChainId, 0f);
            _mpb.SetFloat(FurChainCountId, 0f);
        }
    }

    void FillPropertyBlock()
    {
        if (_mpb == null)
            _mpb = new MaterialPropertyBlock();

        _mpb.Clear();
        _mpb.SetFloat(ShellCountId, shellCount);
        _mpb.SetFloat(ShellLayerOffsetId, 0f);
        _mpb.SetFloat(FurLengthId, furLength);
        ApplyPhysicsToMpb();
        _mpb.SetFloat(FinExtrudeWeightId, finExtrudeWeight);
        _mpb.SetFloat(FinSharpId, finSilhouetteSharpness);
        _mpb.SetFloat(FinBiasId, finSilhouetteBias);
        _mpb.SetFloat(FinPowerId, finSilhouettePower);
        _mpb.SetFloat(FinBandId, finBandStrength);
        _mpb.SetFloat(FinRootId, finRootOffset);
        _mpb.SetFloat(FinLenScaleId, finLengthScale);
        _mpb.SetFloat(FinRootOpacityId, finRootOpacity);
        _mpb.SetFloat(FinTipOpacityId, finTipOpacity);
        _mpb.SetFloat(FinOpacityFadeStartId, finOpacityFadeStart);
        _mpb.SetFloat(FinOpacityFadeEndId, Mathf.Max(finOpacityFadeEnd, finOpacityFadeStart + 0.001f));
        _mpb.SetFloat(FinOpacityPowerId, finOpacityPower);
    }

    void DrawShells(Camera camera, int shellLayersOverride = -1)
    {
        Mesh mesh = ActiveDrawMesh;
        EnsureMaterial();

        if (mesh == null)
        {
            if (!_loggedMissingMesh)
            {
                Debug.LogWarning(
                    $"[{nameof(ShellFurRenderer)}] No mesh on '{name}'. Need MeshFilter or SkinnedMeshRenderer.",
                    this);
                _loggedMissingMesh = true;
            }
            return;
        }

        if (furMaterial == null)
            return;

        if (!furMaterial.enableInstancing)
            furMaterial.enableInstancing = true;

        ApplySmoothNormalKeyword();

        if (furMaterial.shader == null || !furMaterial.shader.isSupported)
        {
            if (!_loggedMissingShader)
            {
                Debug.LogError($"[{nameof(ShellFurRenderer)}] Shader unsupported/missing on material '{furMaterial.name}'.", this);
                _loggedMissingShader = true;
            }
            return;
        }

        int drawCount = shellLayersOverride > 0
            ? Mathf.Clamp(shellLayersOverride, 1, MaxShells)
            : shellCount;

        // Slot mode + hide base: skip shell layer 0 (solid skin), keep full shellCount for height scale.
        float layerOffset = 0f;
        if (useMaterialSlotOnly && hideBaseMesh && shellLayersOverride < 0)
        {
            drawCount = Mathf.Max(1, shellCount - 1);
            layerOffset = 1f;
        }

        int[] slots = GetActiveFurSubmeshes(mesh);
        // Whole-mesh mode: draw every submesh with fur material.
        // DrawMeshInstanced takes one submesh index per call — loop slots.
        if (slots == null)
        {
            int n = Mathf.Max(1, mesh.subMeshCount);
            slots = new int[n];
            for (int i = 0; i < n; i++)
                slots[i] = i;
        }

        if (slots.Length == 0)
            return;

        EnsureBuffers(Mathf.Max(drawCount, shellCount));

        Matrix4x4 localToWorld = DrawLocalToWorld;
        for (int i = 0; i < drawCount; i++)
            _matrices[i] = localToWorld;

        FillPropertyBlock();
        _mpb.SetFloat(ShellCountId, Mathf.Max(shellCount, 2));
        _mpb.SetFloat(ShellLayerOffsetId, layerOffset);

        int subCount = Mathf.Max(1, mesh.subMeshCount);
        for (int s = 0; s < slots.Length; s++)
        {
            int submesh = Mathf.Clamp(slots[s], 0, subCount - 1);
            Graphics.DrawMeshInstanced(
                mesh,
                submesh,
                furMaterial,
                _matrices,
                drawCount,
                _mpb,
                shadowCasting,
                receiveShadows,
                gameObject.layer,
                camera,
                LightProbeUsage.Off,
                null);
        }

        if (logDrawOnce && !_loggedDraw)
        {
            Debug.Log(
                $"[{nameof(ShellFurRenderer)}] Shells '{name}' skinned={IsSkinned} drawCount={drawCount} slots=[{string.Join(",", slots)}]",
                this);
            _loggedDraw = true;
        }
    }

    void DrawFins(Camera camera)
    {
        if (IsSkinned)
            return;

        Mesh source = ActiveDrawMesh;
        if (source == null || furMaterial == null)
            return;

        int slotsKey = ComputeSlotsKey(GetActiveFurSubmeshes(source));
        if (finMeshOverride == null &&
            (_runtimeFinMesh == null || _finSourceBuiltFrom != source ||
             _finSegmentsBuilt != finSegments || _finSlotsKeyBuilt != slotsKey))
            RebuildFins();

        Mesh fins = ActiveFinMesh;
        if (fins == null)
            return;

        SyncFinMaterialFromShell();
        if (_finMaterial == null || !_finMaterial.shader.isSupported)
            return;

        FillPropertyBlock();

        ShadowCastingMode finShadows = finCastShadows ? shadowCasting : ShadowCastingMode.Off;

        Graphics.DrawMesh(
            fins,
            DrawLocalToWorld,
            _finMaterial,
            gameObject.layer,
            camera,
            0,
            _mpb,
            finShadows,
            receiveShadows,
            null,
            LightProbeUsage.Off,
            null);
    }

    static void DestroyOwned(Object obj)
    {
        if (obj == null)
            return;
        if (Application.isPlaying)
            Destroy(obj);
        else
            DestroyImmediate(obj);
    }

    static Bounds TransformBounds(Bounds localBounds, Matrix4x4 matrix)
    {
        Vector3 center = matrix.MultiplyPoint3x4(localBounds.center);
        Vector3 extents = localBounds.extents;
        Vector3 axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
        Vector3 axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
        Vector3 axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));

        extents.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
        extents.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
        extents.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

        return new Bounds(center, extents * 2f);
    }

    void OnDrawGizmosSelected()
    {
        if (!showBoundsGizmo)
            return;

        if (IsSkinned && _skinned != null)
        {
            Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.35f);
            Bounds b = _skinned.bounds;
            float pad = furLength * 1.25f * Mathf.Max(finLengthScale, 1f);
            b.Expand(pad * 2f);
            Gizmos.DrawWireCube(b.center, b.size);
            return;
        }

        Mesh mesh = ActiveDrawMesh;
        if (mesh == null)
            return;

        Bounds worldBounds = TransformBounds(mesh.bounds, DrawLocalToWorld);
        float pad2 = furLength * transform.lossyScale.magnitude * 1.25f * Mathf.Max(finLengthScale, 1f);
        worldBounds.Expand(pad2 * 2f);

        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.35f);
        Gizmos.DrawWireCube(worldBounds.center, worldBounds.size);
    }
}

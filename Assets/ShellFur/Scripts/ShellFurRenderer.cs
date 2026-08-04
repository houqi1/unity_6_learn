using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Renders shell-based fur by drawing the same mesh multiple times via GPU instancing.
/// Each instance corresponds to one shell layer; the shader uses unity_InstanceID as the layer index.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter))]
public class ShellFurRenderer : MonoBehaviour
{
    public const string ShaderName = "Custom/ShellFur";
    const int MaxShells = 128;

    static readonly int ShellCountId = Shader.PropertyToID("_ShellCount");
    static readonly int FurLengthId = Shader.PropertyToID("_FurLength");
    static readonly int GravityId = Shader.PropertyToID("_Gravity");
    static readonly int GravityDirId = Shader.PropertyToID("_GravityDir");

    [Header("Source")]
    [Tooltip("Optional override. Defaults to MeshFilter.sharedMesh.")]
    [SerializeField] Mesh meshOverride;

    [Tooltip("Shell fur material (Custom/ShellFur).")]
    [SerializeField] Material furMaterial;

    [Header("Shells")]
    [Range(2, MaxShells)]
    [SerializeField] int shellCount = 32;

    [Min(0.001f)]
    [SerializeField] float furLength = 0.08f;

    [Header("Physics (pushed each frame via MaterialPropertyBlock)")]
    [SerializeField] float gravityStrength = 0.35f;
    [SerializeField] Vector3 gravityDirection = Vector3.down;

    [Header("Rendering")]
    [SerializeField] ShadowCastingMode shadowCasting = ShadowCastingMode.On;
    [SerializeField] bool receiveShadows = true;
    [SerializeField] bool drawInEditMode = true;
    [SerializeField] bool disableSourceRenderer = true;

    [Header("Debug")]
    [SerializeField] bool showBoundsGizmo = true;
    [SerializeField] bool logDrawOnce = false;

    MeshFilter _meshFilter;
    MeshRenderer _meshRenderer;
    MaterialPropertyBlock _mpb;
    Matrix4x4[] _matrices;
    int _cachedShellCount = -1;
    Material _ownedMaterial;
    bool _loggedMissingShader;
    bool _loggedDraw;
    bool _loggedMissingMesh;

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

    Mesh ActiveMesh => meshOverride != null ? meshOverride : (_meshFilter != null ? _meshFilter.sharedMesh : null);

    void OnEnable()
    {
        CacheComponents();
        EnsureBuffers(shellCount);
        EnsureMaterial();
        ApplySourceRendererState();

        // Reliable for Game view + Scene view (play & edit).
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RestoreSourceRendererState();
    }

    void OnDestroy()
    {
        if (_ownedMaterial != null)
        {
            if (Application.isPlaying)
                Destroy(_ownedMaterial);
            else
                DestroyImmediate(_ownedMaterial);
            _ownedMaterial = null;
        }
    }

    void OnValidate()
    {
        shellCount = Mathf.Clamp(shellCount, 2, MaxShells);
        furLength = Mathf.Max(0.001f, furLength);
        CacheComponents();
        EnsureBuffers(shellCount);
        ApplySourceRendererState();
    }

    void CacheComponents()
    {
        if (_meshFilter == null)
            _meshFilter = GetComponent<MeshFilter>();
        if (_meshRenderer == null)
            _meshRenderer = GetComponent<MeshRenderer>();
        if (_mpb == null)
            _mpb = new MaterialPropertyBlock();
    }

    void EnsureBuffers(int count)
    {
        count = Mathf.Clamp(count, 2, MaxShells);
        if (_matrices != null && _cachedShellCount == count)
            return;

        _matrices = new Matrix4x4[count];
        _cachedShellCount = count;
    }

    void EnsureMaterial()
    {
        if (furMaterial != null)
        {
            if (!furMaterial.enableInstancing)
                furMaterial.enableInstancing = true;
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
    }

    void ApplySourceRendererState()
    {
        if (_meshRenderer == null)
            return;

        // Shells replace the original draw.
        if (disableSourceRenderer)
            _meshRenderer.enabled = false;
    }

    void RestoreSourceRendererState()
    {
        if (_meshRenderer != null && disableSourceRenderer)
            _meshRenderer.enabled = true;
    }

    void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!isActiveAndEnabled)
            return;

        if (!Application.isPlaying && !drawInEditMode)
            return;

        if (camera == null)
            return;

        // Skip tiny inspector previews; still draw Game / Scene / VR cameras.
        if (camera.cameraType == CameraType.Preview)
            return;

        // Per-camera submit so Game view and Scene view both receive the draw.
        DrawShells(camera);
    }

    void DrawShells(Camera camera)
    {
        Mesh mesh = ActiveMesh;
        EnsureMaterial();

        if (mesh == null)
        {
            if (!_loggedMissingMesh)
            {
                Debug.LogWarning($"[{nameof(ShellFurRenderer)}] No mesh on '{name}'. Add MeshFilter mesh.", this);
                _loggedMissingMesh = true;
            }
            return;
        }

        if (furMaterial == null)
            return;

        if (!furMaterial.enableInstancing)
            furMaterial.enableInstancing = true;

        // If shader failed to compile, drawing would be invisible while MeshRenderer is off.
        if (furMaterial.shader == null || !furMaterial.shader.isSupported)
        {
            if (!_loggedMissingShader)
            {
                Debug.LogError($"[{nameof(ShellFurRenderer)}] Shader unsupported/missing on material '{furMaterial.name}'. Check Console for shader errors.", this);
                _loggedMissingShader = true;
            }
            return;
        }

        EnsureBuffers(shellCount);

        Matrix4x4 localToWorld = transform.localToWorldMatrix;
        for (int i = 0; i < shellCount; i++)
            _matrices[i] = localToWorld;

        if (_mpb == null)
            _mpb = new MaterialPropertyBlock();

        _mpb.Clear();
        _mpb.SetFloat(ShellCountId, shellCount);
        _mpb.SetFloat(FurLengthId, furLength);
        _mpb.SetFloat(GravityId, gravityStrength);
        _mpb.SetVector(GravityDirId, gravityDirection.sqrMagnitude > 1e-6f ? gravityDirection.normalized : Vector3.down);

        // One draw call for all shell layers; unity_InstanceID selects the layer in the shader.
        Graphics.DrawMeshInstanced(
            mesh,
            0,
            furMaterial,
            _matrices,
            shellCount,
            _mpb,
            shadowCasting,
            receiveShadows,
            gameObject.layer,
            camera,
            LightProbeUsage.Off,
            null);

        if (logDrawOnce && !_loggedDraw)
        {
            Debug.Log($"[{nameof(ShellFurRenderer)}] DrawMeshInstanced '{name}' shells={shellCount} mesh={mesh.name} mat={furMaterial.name} cam={camera.name}", this);
            _loggedDraw = true;
        }
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

        Mesh mesh = ActiveMesh;
        if (mesh == null)
            return;

        Bounds worldBounds = TransformBounds(mesh.bounds, transform.localToWorldMatrix);
        float pad = furLength * transform.lossyScale.magnitude * 1.25f;
        worldBounds.Expand(pad * 2f);

        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.35f);
        Gizmos.DrawWireCube(worldBounds.center, worldBounds.size);
    }
}

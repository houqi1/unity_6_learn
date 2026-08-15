using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 落叶粒子：落到固定水面高度后贴着漂，并给水面 shader 喂撞击涟漪。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ParticleSystem))]
public class ParticleWaterFloat : MonoBehaviour
{
    public const int MaxRipples = 32;

    static readonly int RipplesId = Shader.PropertyToID("_ParticleRipples");
    static readonly int RippleCountId = Shader.PropertyToID("_ParticleRippleCount");
    static readonly int RippleDurationId = Shader.PropertyToID("_ParticleRippleDuration");
    static readonly int RippleMaxRadiusId = Shader.PropertyToID("_ParticleRippleMaxRadius");
    static readonly int RippleStrengthId = Shader.PropertyToID("_ParticleRippleStrength");

    [Header("Water")]
    [Tooltip("固定水面高度（世界 Y）。勾选下方自动同步时会被水面物体覆盖。")]
    public float waterY = 3.14f;

    [Tooltip("从水面 Renderer 的世界 Y 自动同步高度。")]
    public bool syncWaterYFromRenderer = true;

    [Tooltip("叶子略抬高，避免与透明水面 z-fight。")]
    public float surfaceOffset = 0.03f;

    [Tooltip("水面 Renderer。空则运行时按 Custom/Water 材质查找。")]
    public Renderer waterRenderer;

    [Header("Ripple")]
    [Tooltip("单次撞击涟漪时长（秒）。")]
    public float rippleDuration = 2.5f;

    [Tooltip("涟漪最大半径。")]
    public float rippleMaxRadius = 4f;

    [Tooltip("涟漪整体强度。")]
    public float rippleStrength = 1f;

    [Tooltip("下落速度达到该值时强度为 1。")]
    public float impactSpeedRef = 6f;

    [Tooltip("低于此下落速度不产生涟漪。")]
    public float minImpactSpeed = 0.35f;

    [Tooltip("同一帧最多接受多少个新涟漪。")]
    [Range(1, 16)]
    public int maxNewRipplesPerFrame = 4;

    ParticleSystem _ps;
    ParticleSystem.Particle[] _particles;
    readonly List<Vector4> _custom1 = new List<Vector4>(256);
    readonly Vector4[] _rippleSlots = new Vector4[MaxRipples];
    readonly Vector4[] _rippleGpu = new Vector4[MaxRipples];
    MaterialPropertyBlock _mpb;
    int _rippleWrite;
    bool _loggedMissingWater;

    void Awake()
    {
        _ps = GetComponent<ParticleSystem>();
        _mpb = new MaterialPropertyBlock();
        EnsureCustomData();
        ApplyFlatRenderMode();
        ResolveWaterRenderer();
    }

    void OnEnable()
    {
        if (_ps == null)
            _ps = GetComponent<ParticleSystem>();
        EnsureCustomData();
        ApplyFlatRenderMode();
        ResolveWaterRenderer();
    }

    void ApplyFlatRenderMode()
    {
        var renderer = GetComponent<ParticleSystemRenderer>();
        if (renderer == null)
            return;
        // Billboard 永远朝向相机，无法躺平；Horizontal 贴在 XZ 上。
        renderer.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
    }

    void LateUpdate()
    {
        if (_ps == null || !_ps.IsAlive(true))
            return;

        SyncWaterHeight();

        int count = _ps.particleCount;
        if (count <= 0)
        {
            PushRipplesToWater();
            return;
        }

        if (_particles == null || _particles.Length < count)
            _particles = new ParticleSystem.Particle[Mathf.NextPowerOfTwo(count)];

        int read = _ps.GetParticles(_particles, count);
        _ps.GetCustomParticleData(_custom1, ParticleSystemCustomData.Custom1);
        while (_custom1.Count < read)
            _custom1.Add(Vector4.zero);

        bool worldSpace = _ps.main.simulationSpace == ParticleSystemSimulationSpace.World;
        float surfaceY = waterY + surfaceOffset;
        int newRipples = 0;

        for (int i = 0; i < read; ++i)
        {
            ParticleSystem.Particle p = _particles[i];
            Vector3 worldPos = worldSpace ? p.position : transform.TransformPoint(p.position);
            Vector3 worldVel = worldSpace ? p.velocity : transform.TransformVector(p.velocity);

            bool landed = _custom1[i].x > 0.5f;
            if (!landed && worldPos.y <= surfaceY)
            {
                landed = true;
                // x=落地标记，y=锁住的 2D 旋转，z=锁住的 yaw
                _custom1[i] = new Vector4(1f, p.rotation, p.rotation3D.y, 0f);

                float impact = Mathf.Max(-worldVel.y, 0f);
                if (newRipples < maxNewRipplesPerFrame && impact >= minImpactSpeed)
                {
                    float strength = Mathf.Clamp01(impact / Mathf.Max(impactSpeedRef, 0.01f));
                    strength = Mathf.Max(strength, 0.35f);
                    PushRipple(worldPos.x, worldPos.z, strength);
                    newRipples++;
                }
            }

            if (landed)
            {
                worldPos.y = surfaceY;
                worldVel.y = 0f;

                Vector4 frozen = _custom1[i];
                p.angularVelocity = 0f;
                p.angularVelocity3D = Vector3.zero;
                p.rotation = frozen.y;
                Vector3 rot = p.rotation3D;
                rot.y = frozen.z;
                p.rotation3D = rot;
            }

            p.position = worldSpace ? worldPos : transform.InverseTransformPoint(worldPos);
            p.velocity = worldSpace ? worldVel : transform.InverseTransformVector(worldVel);
            _particles[i] = p;
        }

        _ps.SetParticles(_particles, read);
        _ps.SetCustomParticleData(_custom1, ParticleSystemCustomData.Custom1);
        PushRipplesToWater();
    }

    void OnDisable()
    {
        Shader.SetGlobalFloat(RippleCountId, 0f);
        if (waterRenderer == null)
            return;
        if (_mpb == null)
            _mpb = new MaterialPropertyBlock();
        _mpb.Clear();
        waterRenderer.SetPropertyBlock(_mpb);
    }

    void EnsureCustomData()
    {
        if (_ps == null)
            return;
        var custom = _ps.customData;
        custom.enabled = true;
        custom.SetMode(ParticleSystemCustomData.Custom1, ParticleSystemCustomDataMode.Vector);
        custom.SetVectorComponentCount(ParticleSystemCustomData.Custom1, 3);
    }

    void ResolveWaterRenderer()
    {
        if (waterRenderer != null)
        {
            SyncWaterHeight();
            return;
        }

        Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < renderers.Length; ++i)
        {
            Material mat = renderers[i].sharedMaterial;
            if (mat == null || mat.shader == null)
                continue;
            if (mat.shader.name == "Custom/Water" || mat.name.IndexOf("Water", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                waterRenderer = renderers[i];
                SyncWaterHeight();
                return;
            }
        }

        if (!_loggedMissingWater)
        {
            Debug.LogWarning($"[{nameof(ParticleWaterFloat)}] 没找到 Custom/Water 水面，涟漪不会显示。请把 ground 拖到 waterRenderer。", this);
            _loggedMissingWater = true;
        }
    }

    void SyncWaterHeight()
    {
        if (!syncWaterYFromRenderer || waterRenderer == null)
            return;
        waterY = waterRenderer.transform.position.y;
    }

    void PushRipple(float x, float z, float strength)
    {
        _rippleSlots[_rippleWrite] = new Vector4(x, z, Time.timeSinceLevelLoad, strength);
        _rippleWrite = (_rippleWrite + 1) % MaxRipples;
    }

    void PushRipplesToWater()
    {
        if (_mpb == null)
            _mpb = new MaterialPropertyBlock();

        if (waterRenderer == null)
        {
            ResolveWaterRenderer();
            if (waterRenderer == null)
                return;
        }

        float now = Time.timeSinceLevelLoad;
        float duration = Mathf.Max(rippleDuration, 0.05f);
        int live = 0;
        for (int i = 0; i < MaxRipples; ++i)
        {
            Vector4 slot = _rippleSlots[i];
            if (slot.w <= 1e-4f)
                continue;
            if (now - slot.z > duration)
            {
                _rippleSlots[i] = Vector4.zero;
                continue;
            }
            _rippleGpu[live++] = slot;
        }

        for (int i = live; i < MaxRipples; ++i)
            _rippleGpu[i] = Vector4.zero;

        // URP SRP Batcher / MPB 经常吃不到未在 Properties 里声明的数组，用全局参数最稳。
        Shader.SetGlobalVectorArray(RipplesId, _rippleGpu);
        Shader.SetGlobalFloat(RippleCountId, live);
        Shader.SetGlobalFloat(RippleDurationId, duration);
        Shader.SetGlobalFloat(RippleMaxRadiusId, Mathf.Max(rippleMaxRadius, 0.1f));
        Shader.SetGlobalFloat(RippleStrengthId, rippleStrength);
        Shader.SetGlobalFloat("_ParticleRippleTime", Time.timeSinceLevelLoad);
    }
}

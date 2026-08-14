using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 体积光美术参数。方向只来自 VolumetricLightSource 指定的灯。
/// </summary>
[Serializable]
[VolumeComponentMenu("Custom/Volumetric Light")]
[VolumeRequiresRendererFeatures(typeof(VolumetricLightFeature))]
[SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
public class VolumetricLightVolume : VolumeComponent, IPostProcessComponent
{
    [Header("Light")]
    [Tooltip("乘在 Source 颜色上的染色，默认白=不改色。")]
    public ColorParameter color = new ColorParameter(Color.white, true, false, true);
    [Tooltip("总强度倍率，乘在 Source 强度上。0 关闭体积光。")]
    public ClampedFloatParameter intensity = new ClampedFloatParameter(1f, 0f, 8f);

    [Header("Media")]
    public ClampedFloatParameter density = new ClampedFloatParameter(0.08f, 0f, 1f);
    public ClampedFloatParameter extinction = new ClampedFloatParameter(0.04f, 0f, 2f);
    public ClampedFloatParameter anisotropy = new ClampedFloatParameter(0.6f, -0.9f, 0.9f);
    public ClampedFloatParameter maxDistance = new ClampedFloatParameter(160f, 10f, 300f);
    public FloatParameter heightStart = new FloatParameter(3.14f);
    public ClampedFloatParameter heightFalloff = new ClampedFloatParameter(0.08f, 0f, 2f);
    public ClampedFloatParameter noiseAmp = new ClampedFloatParameter(0f, 0f, 1f);

    [Header("Shadow Shafts")]
    public ClampedFloatParameter shadowStrength = new ClampedFloatParameter(1f, 0f, 1f);

    [Header("Composite")]
    public BoolParameter applyExtinction = new BoolParameter(false);
    public ClampedFloatParameter compositeScale = new ClampedFloatParameter(1f, 0f, 4f);

    public bool IsActive() => intensity.value > 0.001f;
}

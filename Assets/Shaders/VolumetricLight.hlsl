#ifndef VOLUMETRIC_LIGHT_INCLUDED
#define VOLUMETRIC_LIGHT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"

float4x4 _InverseVP;
float3   _LightTravelDir;
float3   _LightColor;
float    _Intensity;
float    _Density;
float    _Extinction;
float    _Anisotropy;
float    _MaxDistance;
float    _HeightStart;
float    _HeightFalloff;
float    _ShadowStrength;
float    _NoiseAmp;
float    _Jitter;
float    _ApplyExtinction;
float    _CompositeScale;
float    _StepCount;
float    _Frame;
float    _AnimTime;
float    _UseMainLightCascade;
float    _DebugMode;
float    _DepthReject;
float4   _VolumeTexelSize;
float2   _BlurDirection;
float    _SpecifiedShadowBias;
float4x4 _SpecifiedLightVP;
float    _UseCylinder;
float3   _CylinderOrigin;
float3   _CylinderDir;
float    _CylinderRadius;
float    _CylinderHeight;
float    _CylinderEdgeFade;

TEXTURE2D_FLOAT(_SpecifiedShadowMap);
SAMPLER(sampler_SpecifiedShadowMap);
TEXTURE2D(_VolumeTex);
SAMPLER(sampler_VolumeTex);

float HenyeyGreenstein(float mu, float g)
{
    float gg = g * g;
    float denom = 1.0 + gg - 2.0 * g * mu;
    return (1.0 - gg) / max(pow(abs(denom), 1.5), 1e-4);
}

float HeightAtten(float y)
{
    return exp(-_HeightFalloff * max(0.0, y - _HeightStart));
}

float CylinderShape(float3 p)
{
    if (_UseCylinder < 0.5)
        return 1.0;

    float3 axis = _CylinderDir;
    float axisLen = length(axis);
    if (axisLen < 1e-5)
        return 0.0;
    axis /= axisLen;

    float3 rel = p - _CylinderOrigin;
    float along = dot(rel, axis);
    float radial = length(rel - axis * along) - _CylinderRadius;
    float caps = max(-along, along - _CylinderHeight);
    float sdf = radial > 0.0 && caps > 0.0
        ? length(float2(radial, caps))
        : max(radial, caps);

    float fade = max(_CylinderEdgeFade, 1e-4);
    return 1.0 - smoothstep(0.0, fade, sdf);
}

float CheapNoise(float3 p, float t)
{
    p += t * 0.15;
    return frac(sin(dot(p, float3(12.9898, 78.233, 37.719))) * 43758.5453) * 2.0 - 1.0;
}

float SampleSpecifiedLightShadow(float3 worldPos)
{
    float4 clip = mul(_SpecifiedLightVP, float4(worldPos, 1.0));
    float3 ndc = clip.xyz / max(clip.w, 1e-6);
    float2 uv = ndc.xy * 0.5 + 0.5;

    if (any(uv < 0.0) || any(uv > 1.0))
        return 1.0;

#if UNITY_REVERSED_Z
    float pointZ = ndc.z;
#else
    float pointZ = ndc.z * 0.5 + 0.5;
#endif

    float mapZ = SAMPLE_TEXTURE2D(_SpecifiedShadowMap, sampler_SpecifiedShadowMap, uv).r;
#if UNITY_REVERSED_Z
    return mapZ <= pointZ + _SpecifiedShadowBias ? 1.0 : 0.0;
#else
    return mapZ >= pointZ - _SpecifiedShadowBias ? 1.0 : 0.0;
#endif
}

#if defined(VOLUMETRIC_LIGHT_SHADOWS)
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

float SampleVisibility(float3 worldPos)
{
    float vis;
    if (_UseMainLightCascade > 0.5)
        vis = MainLightRealtimeShadow(TransformWorldToShadowCoord(worldPos));
    else
        vis = SampleSpecifiedLightShadow(worldPos);
    return lerp(1.0, vis, _ShadowStrength);
}
#else
float SampleVisibility(float3 worldPos)
{
    if (_UseMainLightCascade > 0.5)
        return 1.0;
    return lerp(1.0, SampleSpecifiedLightShadow(worldPos), _ShadowStrength);
}
#endif

float4 MarchVolumetric(float2 uv, float2 pixelPos)
{
    float rawDepth = SampleSceneDepth(uv);
    float3 worldEnd = ComputeWorldSpacePosition(uv, rawDepth, _InverseVP);
    float3 camPos = _WorldSpaceCameraPos;
    float3 viewDir = worldEnd - camPos;
    float sceneZ = length(viewDir);
    viewDir = sceneZ > 1e-5 ? viewDir / sceneZ : float3(0, 0, 1);
    sceneZ = min(sceneZ, _MaxDistance);

    int stepCount = max(1, (int)_StepCount);
    float stepSize = sceneZ / (float)stepCount;
    float t = 0.5 * stepSize;
    if (_Jitter > 0.5)
        t = InterleavedGradientNoise(pixelPos, (int)_Frame) * stepSize;

    float T = 1.0;
    float3 inscatter = 0;
    float visAcc = 0;
    float visCount = 0;
    float marched = 0;

    [loop]
    for (int i = 0; i < stepCount; i++)
    {
        if (t >= sceneZ || T < 0.01)
            break;

        float3 p = camPos + viewDir * t;
        float density = _Density * HeightAtten(p.y) * CylinderShape(p);
        if (density < 1e-5)
        {
            t += stepSize;
            continue;
        }
        if (_NoiseAmp > 0.001)
            density *= 1.0 + _NoiseAmp * CheapNoise(p, _AnimTime);

        float vis = SampleVisibility(p);
        visAcc += vis;
        visCount += 1.0;

        float mu = dot(viewDir, -_LightTravelDir);
        float phase = HenyeyGreenstein(mu, _Anisotropy);
        float3 lightCol = _LightColor * _Intensity;

        float optical = _Extinction * density * stepSize;
        float stepT = exp(-optical);
        float scatterIntegral = (abs(_Extinction) < 1e-5)
            ? (density * stepSize)
            : ((1.0 - stepT) / _Extinction);

        inscatter += T * lightCol * vis * phase * scatterIntegral;
        T *= stepT;
        marched = t;
        t += stepSize;
    }

    if (_DebugMode > 2.5 && _DebugMode < 3.5)
    {
        float avgVis = visCount > 0.0 ? visAcc / visCount : 1.0;
        return float4(avgVis.xxx, T);
    }

    if (_DebugMode > 4.5)
    {
        float md = saturate(marched / max(sceneZ, 1e-3));
        return float4(md.xxx, T);
    }

    return float4(inscatter, T);
}

float4 BilateralBlur(float2 uv)
{
    float2 texel = _VolumeTexelSize.xy;
    float centerDepth = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
    float4 acc = 0;
    float wsum = 0;

    [unroll]
    for (int i = -2; i <= 2; i++)
    {
        float2 suv = uv + _BlurDirection * texel * i;
        float4 s = SAMPLE_TEXTURE2D(_VolumeTex, sampler_VolumeTex, suv);
        float sd = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
        float spatial = exp(-0.5 * (i * i));
        float depthW = abs(sd - centerDepth) > _DepthReject ? 0.0 : 1.0;
        float w = spatial * depthW;
        acc += s * w;
        wsum += w;
    }

    return wsum > 1e-5 ? acc / wsum : SAMPLE_TEXTURE2D(_VolumeTex, sampler_VolumeTex, uv);
}

float4 CompositeVolumetric(float2 uv, float3 scene)
{
    if (_DebugMode > 3.5 && _DebugMode < 4.5)
        return float4(_LightTravelDir * 0.5 + 0.5, 1);

    float4 vol = SAMPLE_TEXTURE2D(_VolumeTex, sampler_VolumeTex, uv);

    if (_DebugMode > 0.5 && _DebugMode < 1.5)
        return float4(vol.rgb, 1);
    if (_DebugMode > 1.5 && _DebugMode < 2.5)
        return float4(vol.aaa, 1);
    if (_DebugMode > 2.5)
        return float4(vol.rgb, 1);

    float3 outCol = scene + vol.rgb * _CompositeScale;
    if (_ApplyExtinction > 0.5)
        outCol = scene * vol.a + vol.rgb * _CompositeScale;
    return float4(outCol, 1);
}

#endif

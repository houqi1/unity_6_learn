#ifndef SHELL_FUR_GPU_SKINNING_INCLUDED
#define SHELL_FUR_GPU_SKINNING_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// Match ShellFurGpuSkinTypes.SkinnedVertex
struct SkinnedVertex
{
    float px, py, pz, pad0;
    float nx, ny, nz, pad1;
    float sx, sy, sz, pad2;
    float u, v, pad3a, pad3b;
};

StructuredBuffer<SkinnedVertex> _SkinnedVertices;

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
TEXTURE2D(_FurMap);
SAMPLER(sampler_FurMap);

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _FurMap_ST;
    half4  _BaseColor;
    half4  _TipColor;
    float  _Density;
    float  _Thickness;
    float  _Occlusion;
    float  _AlphaCutoff;
    float  _ShellCount;
    float  _ShellLayerOffset;
    float  _FurLength;
    float  _FurLengthRandom;
    float  _Gravity;
    float4 _GravityDir;
    float  _Smoothness;
    float  _RimPower;
    float  _RimStrength;
    float  _ShadowStrength;
CBUFFER_END

float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float GetShellLayer(uint instanceId)
{
    float count = max(_ShellCount, 1.0);
    float idx = (float)instanceId + max(_ShellLayerOffset, 0.0);
    return saturate(idx / max(count - 1.0, 1.0));
}

bool EvaluateFurMaskGpu(float2 furUV, float layer, out float alphaOut, out float strandHeight)
{
    alphaOut = 0.0;
    strandHeight = 1.0;
    if (layer < 0.001)
    {
        alphaOut = 1.0;
        strandHeight = 1.0;
        return true;
    }
#if defined(_USE_PROCEDURAL)
    float2 gridUV = furUV * _Density;
    float2 cell = floor(gridUV);
    float2 local = frac(gridUV) - 0.5;
    float h0 = Hash21(cell);
    float h1 = Hash21(cell + 17.13);
    strandHeight = lerp(1.0 - _FurLengthRandom, 1.0, h0);
    if (layer > strandHeight) return false;
    float heightAlongStrand = layer / max(strandHeight, 1e-4);
    float radius = _Thickness * 0.5 * (1.0 - heightAlongStrand * 0.85) * lerp(0.7, 1.15, h1);
    float dist = length(local);
    if (dist > radius) return false;
    alphaOut = pow(abs(saturate(1.0 - dist / max(radius, 1e-4))), 0.75);
#else
    float density = SAMPLE_TEXTURE2D(_FurMap, sampler_FurMap, furUV).r;
    strandHeight = saturate(density * (1.0 + _FurLengthRandom) - _FurLengthRandom * 0.5);
    if (layer > strandHeight) return false;
    float threshold = lerp(_AlphaCutoff, 1.0, layer);
    if (density < threshold) return false;
    alphaOut = saturate((density - threshold) / max(1.0 - threshold, 1e-4));
#endif
    return alphaOut > 0.01;
}

half3 ShadeShellFurGpu(float3 positionWS, float3 normalWS, float2 uv, float layer, float strandHeight)
{
    half4 albedoSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    half3 rootColor = _BaseColor.rgb * albedoSample.rgb;
    half3 tipColor  = _TipColor.rgb  * albedoSample.rgb;
    float tipFactor = saturate(layer / max(strandHeight, 1e-4));
    half3 albedo = lerp(rootColor, tipColor, tipFactor);
    float layer01 = saturate(layer);
    float ao = lerp(1.0 - _Occlusion, 1.0, pow(abs(layer01), 0.55));
    albedo *= ao;
    albedo *= lerp(1.0 - _ShadowStrength, 1.0, layer01);

    float3 n = NormalizeNormalPerPixel(normalWS);
    float3 vDir = GetWorldSpaceNormalizeViewDir(positionWS);
    float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
    Light mainLight = GetMainLight(shadowCoord);
    half NdotL = saturate(dot(n, mainLight.direction));
    half3 lighting = mainLight.color * (mainLight.shadowAttenuation * mainLight.distanceAttenuation * NdotL);
    half3 ambient = SampleSH(n) * ao;
    half3 color = albedo * (ambient + lighting);

#if defined(_ADDITIONAL_LIGHTS)
    uint lightsCount = GetAdditionalLightsCount();
    for (uint li = 0u; li < lightsCount; ++li)
    {
        Light light = GetAdditionalLight(li, positionWS);
        half addNdotL = saturate(dot(n, light.direction));
        color += albedo * light.color * (light.distanceAttenuation * light.shadowAttenuation * addNdotL);
    }
#endif

    float ndotv = saturate(dot(n, vDir));
    color += tipColor * (pow(abs(1.0 - ndotv), max(_RimPower, 0.0001)) * _RimStrength * layer01);

    float3 L = mainLight.direction;
    float specExp = lerp(8.0, 64.0, saturate(_Smoothness));
    float spec = 0.0;
#if defined(_USE_KAJIYA_KAY)
    float TdotL = clamp(dot(n, L), -1.0, 1.0);
    float TdotV = clamp(dot(n, vDir), -1.0, 1.0);
    float sinTL = sqrt(max(0.0, 1.0 - TdotL * TdotL));
    float sinTV = sqrt(max(0.0, 1.0 - TdotV * TdotV));
    spec = pow(abs(saturate(TdotL * TdotV + sinTL * sinTV)), max(specExp, 1e-3)) * _Smoothness;
    spec *= lerp(0.35, 1.0, layer01);
#else
    float3 h = normalize(L + vDir);
    spec = pow(abs(saturate(dot(n, h))), max(specExp, 1e-3)) * _Smoothness;
#endif
    color += spec * mainLight.color * mainLight.shadowAttenuation * tipColor;
    return color;
}

struct Attributes
{
    float4 positionOS : POSITION; // topology only; position comes from buffer
    uint vertexID : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    float2 uv         : TEXCOORD2;
    float2 furUV      : TEXCOORD3;
    float  layer      : TEXCOORD4;
    float  fogFactor  : TEXCOORD5;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings ShellFurGpuVert(Attributes input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    uint inst = 0;
#ifdef UNITY_INSTANCING_ENABLED
    inst = unity_InstanceID;
#endif

    SkinnedVertex sv = _SkinnedVertices[input.vertexID];
    float layer = GetShellLayer(inst);

    float3 posWS = float3(sv.px, sv.py, sv.pz);
    // Smooth normal: extrusion + lighting (softer shading on shells).
    float3 nSmoothWS = float3(sv.sx, sv.sy, sv.sz);

    float layer2 = layer * layer;
    posWS += nSmoothWS * (layer * _FurLength);
    posWS += normalize(_GravityDir.xyz + 1e-5) * (_Gravity * layer2 * _FurLength);

    output.positionCS = TransformWorldToHClip(posWS);
    output.positionWS = posWS;
    output.normalWS = nSmoothWS;
    float2 uv = float2(sv.u, sv.v);
    output.uv = TRANSFORM_TEX(uv, _BaseMap);
    output.furUV = TRANSFORM_TEX(uv, _FurMap);
    output.layer = layer;
    output.fogFactor = ComputeFogFactor(output.positionCS.z);
    return output;
}

half4 ShellFurGpuFrag(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    float alpha;
    float strandHeight;
    if (!EvaluateFurMaskGpu(input.furUV, input.layer, alpha, strandHeight))
        discard;
    clip(alpha - 0.01);
    half3 color = ShadeShellFurGpu(input.positionWS, input.normalWS, input.uv, input.layer, strandHeight);
    color = MixFog(color, input.fogFactor);
    return half4(color, alpha);
}

float3 _LightDirection;
float3 _LightPosition;

struct ShadowAttributes
{
    float4 positionOS : POSITION;
    uint vertexID : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct ShadowVaryings
{
    float4 positionCS : SV_POSITION;
    float2 furUV : TEXCOORD0;
    float layer : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

ShadowVaryings ShellFurGpuShadowVert(ShadowAttributes input)
{
    ShadowVaryings output = (ShadowVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    uint inst = 0;
#ifdef UNITY_INSTANCING_ENABLED
    inst = unity_InstanceID;
#endif

    SkinnedVertex sv = _SkinnedVertices[input.vertexID];
    float layer = GetShellLayer(inst);
    float3 posWS = float3(sv.px, sv.py, sv.pz);
    float3 nSmoothWS = float3(sv.sx, sv.sy, sv.sz);
    posWS += nSmoothWS * (layer * _FurLength);
    posWS += normalize(_GravityDir.xyz + 1e-5) * (_Gravity * layer * layer * _FurLength);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - posWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif
    output.positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, nSmoothWS, lightDirectionWS));
#if UNITY_REVERSED_Z
    output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
    output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif
    output.furUV = TRANSFORM_TEX(float2(sv.u, sv.v), _FurMap);
    output.layer = layer;
    return output;
}

half4 ShellFurGpuShadowFrag(ShadowVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    float alpha;
    float strandHeight;
    if (!EvaluateFurMaskGpu(input.furUV, input.layer, alpha, strandHeight))
        discard;
    clip(alpha - 0.01);
    return 0;
}

#endif

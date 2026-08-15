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
    float  _TipAlphaCutoff;
    float4 _UVOffset;
    float  _ShellCount;
    float  _ShellLayerOffset;
    float  _FurLength;
    float  _FurLengthRandom;
    float  _Gravity;
    float4 _GravityDir;
    float  _GravityPower;
    float  _Smoothness;
    float  _RimPower;
    float  _RimStrength;
    float  _ShadowStrength;
    float  _DiffuseBoostMin;
    float  _DiffuseBoostMax;
    float  _StrainEmissionEnable;
    half4  _StrainEmissionColor;
    float  _StrainEmissionIntensity;
    float  _StrainEmissionTipPower;
    float  _StrainEmissionDistMin;
    float  _StrainEmissionDistMax;
    float  _StrainEmissionDistPower;
    float  _GuideOffsetScale;
CBUFFER_END

// Guide-strand bend samples (world-space). Set via MaterialPropertyBlock.
// Size must match ShellFurDynamics.MaxNodes
float4 _FurChain[17];
float  _FurChainCount;
float  _UseFurChain;
float4 _FurChainErect;

// Local Grass guides (sparse). Match ShellFurGpuSkinTypes.GuideWeight + packed chains.
struct GuideWeight
{
    float i0, i1, i2, pad0;
    float w0, w1, w2, pad1;
};

StructuredBuffer<float4> _GuideChains;       // GuideCount * stride (stride=17)
StructuredBuffer<GuideWeight> _VertexGuideWeights;
float _GuideCount;
float _GuideNodeCount;
float _GuideStride;
float _UseLocalGuides;

float3 SampleFurChainOffsetWS(float layer)
{
    int count = (int)clamp(_FurChainCount, 1.0, 17.0);
    float t = saturate(layer) * max((float)count - 1.0, 0.0);
    int i0 = (int)floor(t);
    int i1 = min(i0 + 1, count - 1);
    float f = t - (float)i0;
    return lerp(_FurChain[i0].xyz, _FurChain[i1].xyz, f);
}

float3 SampleOneGuideOffsetWS(uint guideId, float layer)
{
    uint stride = (uint)max(_GuideStride, 1.0);
    uint gcount = (uint)max(_GuideCount, 1.0);
    uint gid = min(guideId, gcount - 1u);
    int count = (int)clamp(_GuideNodeCount, 1.0, 17.0);
    float t = saturate(layer) * max((float)count - 1.0, 0.0);
    int i0 = (int)floor(t);
    int i1 = min(i0 + 1, count - 1);
    float f = t - (float)i0;
    uint base = gid * stride;
    return lerp(_GuideChains[base + (uint)i0].xyz, _GuideChains[base + (uint)i1].xyz, f);
}

float3 SampleLocalGuidesOffsetWS(uint vertexId, float layer)
{
    GuideWeight gw = _VertexGuideWeights[vertexId];
    float3 d = 0;
    d += SampleOneGuideOffsetWS((uint)gw.i0, layer) * gw.w0;
    d += SampleOneGuideOffsetWS((uint)gw.i1, layer) * gw.w1;
    d += SampleOneGuideOffsetWS((uint)gw.i2, layer) * gw.w2;
    return d;
}

float3 GravityBendWS(float layer, float lengthScale)
{
    float h = saturate(layer);
    float p = max(_GravityPower, 0.01);
    float w = pow(h, p);
    return normalize(_GravityDir.xyz + 1e-5) * (_Gravity * w * lengthScale);
}

float3 SampleShellDynamicsOffsetWS(uint vertexId, float layer)
{
    if (_UseLocalGuides > 0.5)
        return SampleLocalGuidesOffsetWS(vertexId, layer);
    if (_UseFurChain > 0.5)
        return SampleFurChainOffsetWS(layer);
    return GravityBendWS(layer, _FurLength);
}

// Understanding A: hang / rest ideal after chain-length normalization pack.
// δ_rest(h) = normalize(gDir) * h * guideOffsetScale  (matches PackSamples at full hang)
float3 SampleHangRestOffsetWS(float layer)
{
    float3 g = normalize(_GravityDir.xyz + 1e-5);
    return g * (saturate(layer) * max(_GuideOffsetScale, 0.0));
}

// |current δ − rest δ|; 0 when dynamics chain is off.
float ComputeChainStrainDistance(uint vertexId, float layer)
{
    if (_UseLocalGuides < 0.5 && _UseFurChain < 0.5)
        return 0.0;
    float3 dCur = SampleShellDynamicsOffsetWS(vertexId, layer);
    float3 dRest = SampleHangRestOffsetWS(layer);
    return length(dCur - dRest);
}

half3 EvaluateStrainEmission(float layer, float strandHeight, float strainDist)
{
    if (_StrainEmissionEnable < 0.5 || _StrainEmissionIntensity <= 1e-6)
        return 0;

    // Tip-only mask (layer along strand, clamped by strand height)
    float tipH = saturate(layer / max(strandHeight, 1e-4));
    float tipMask = pow(abs(tipH), max(_StrainEmissionTipPower, 0.01));

    float d0 = max(_StrainEmissionDistMin, 0.0);
    float d1 = max(_StrainEmissionDistMax, d0 + 1e-5);
    float t = saturate((strainDist - d0) / (d1 - d0));
    t = pow(abs(t), max(_StrainEmissionDistPower, 0.01));

    return _StrainEmissionColor.rgb * (_StrainEmissionIntensity * tipMask * t);
}

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

float2 ApplyFurUvBend(float2 furUV, float layer)
{
#if defined(_USE_UV_BEND)
    float2 uvOffset = _UVOffset.xy * pow(saturate(layer), max(_UVOffset.z, 1e-4)) * 0.1;
    furUV += uvOffset;
#endif
    return furUV;
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
#if defined(_USE_TIP_ALPHA_CUTOFF)
    float threshold = lerp(_AlphaCutoff, _TipAlphaCutoff, layer);
#else
    float threshold = lerp(_AlphaCutoff, 1.0, layer);
#endif
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
#if defined(_OCCLUSION_TO_BASECOLOR)
    albedo = lerp(rootColor, albedo, ao);
#else
    albedo *= ao;
#endif
    albedo *= lerp(1.0 - _ShadowStrength, 1.0, layer01);

    float3 n = NormalizeNormalPerPixel(normalWS);
    float3 vDir = GetWorldSpaceNormalizeViewDir(positionWS);
    // DrawMeshInstanced does not fill unity_LightData.z; force main distance atten = 1.
    float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
    Light mainLight = GetMainLight(shadowCoord);
    mainLight.distanceAttenuation = 1.0;
    half NdotL = saturate(dot(n, mainLight.direction));
    half3 lighting = mainLight.color * (mainLight.shadowAttenuation * mainLight.distanceAttenuation * NdotL);
    half diffuseBoost = lerp(_DiffuseBoostMin, _DiffuseBoostMax, layer01);
    lighting *= diffuseBoost;
    half3 ambient = SampleSH(n) * ao;
    half3 color = albedo * (ambient + lighting);

#if defined(_ADDITIONAL_LIGHTS)
    uint lightsCount = GetAdditionalLightsCount();
    for (uint li = 0u; li < lightsCount; ++li)
    {
        Light light = GetAdditionalLight(li, positionWS);
        half addNdotL = saturate(dot(n, light.direction));
        color += albedo * light.color * (light.distanceAttenuation * light.shadowAttenuation * addNdotL) * diffuseBoost;
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
    float  strainDist : TEXCOORD6;
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

    posWS += nSmoothWS * (layer * _FurLength);
    posWS += SampleShellDynamicsOffsetWS(input.vertexID, layer);

    output.positionCS = TransformWorldToHClip(posWS);
    output.positionWS = posWS;
    output.normalWS = nSmoothWS;
    float2 uv = float2(sv.u, sv.v);
    output.uv = TRANSFORM_TEX(uv, _BaseMap);
    output.furUV = ApplyFurUvBend(TRANSFORM_TEX(uv, _FurMap), layer);
    output.layer = layer;
    output.fogFactor = ComputeFogFactor(output.positionCS.z);
    // |δ_current − δ_hangRest| for tip strain emission (understanding A)
    output.strainDist = ComputeChainStrainDistance(input.vertexID, layer);
    return output;
}

half4 ShellFurGpuFrag(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    float alpha;
    float strandHeight;
    if (!EvaluateFurMaskGpu(input.furUV, input.layer, alpha, strandHeight))
        discard;
#if !defined(_SKIP_SOFT_ALPHA_CLIP)
    clip(alpha - 0.01);
#endif
    half3 color = ShadeShellFurGpu(input.positionWS, input.normalWS, input.uv, input.layer, strandHeight);
    color += EvaluateStrainEmission(input.layer, strandHeight, input.strainDist);
    color = MixFog(color, input.fogFactor);
#if defined(_OPAQUE_OUTPUT_ALPHA)
    return half4(color, 1);
#else
    return half4(color, alpha);
#endif
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
    posWS += SampleShellDynamicsOffsetWS(input.vertexID, layer);

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
    output.furUV = ApplyFurUvBend(TRANSFORM_TEX(float2(sv.u, sv.v), _FurMap), layer);
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
#if !defined(_SKIP_SOFT_ALPHA_CLIP)
    clip(alpha - 0.01);
#endif
    return 0;
}

#endif

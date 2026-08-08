#ifndef SHELL_FUR_GPU_FIN_INCLUDED
#define SHELL_FUR_GPU_FIN_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// Match ShellFurGpuSkinTypes.FinVertex — already world-space from CS B2.
struct FinVertexGPU
{
    float px, py, pz, pad0;
    float nx, ny, nz, pad1;
    float u, v, height01, silhouette;
};

StructuredBuffer<FinVertexGPU> _FinVertices;

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
    float  _GravityPower;
    float  _Smoothness;
    float  _RimPower;
    float  _RimStrength;
    float  _ShadowStrength;
    float  _FinRootOpacity;
    float  _FinTipOpacity;
    float  _FinOpacityFadeStart;
    float  _FinOpacityFadeEnd;
    float  _FinOpacityPower;
CBUFFER_END

float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

bool EvaluateFurMaskGpuFin(float2 furUV, float layer, out float alphaOut, out float strandHeight)
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

float EvaluateFinHeightOpacity(float height01)
{
    float h = saturate(height01);
    float fadeStart = saturate(_FinOpacityFadeStart);
    float fadeEnd = max(saturate(_FinOpacityFadeEnd), fadeStart + 1e-4);
    float t = saturate((h - fadeStart) / (fadeEnd - fadeStart));
    t = pow(abs(t), max(_FinOpacityPower, 1e-3));
    return lerp(saturate(_FinRootOpacity), saturate(_FinTipOpacity), t);
}

// ---------------------------------------------------------------------------
// Forward only — Lighting.hlsl is heavy; do not pull it into ShadowCaster.
// ---------------------------------------------------------------------------
#if defined(SHELL_FUR_GPU_FIN_FORWARD)

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// Lightweight shade: main light + SH only (no additional-light loop).
// Soft-shadow keyword stripped in .shader to avoid d3d11 FXC OOM.
half3 ShadeShellFurGpuFin(float3 positionWS, float3 normalWS, float2 uv, float colorHeight01, float strandHeight)
{
    half4 albedoSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    half3 rootColor = _BaseColor.rgb * albedoSample.rgb;
    half3 tipColor  = _TipColor.rgb  * albedoSample.rgb;

    float h = saturate(colorHeight01);
    float tipFactor = saturate(h / max(strandHeight, 1e-4));
    half3 albedo = lerp(rootColor, tipColor, tipFactor);

    float layer01 = h;
    float ao = lerp(1.0 - _Occlusion, 1.0, pow(abs(layer01), 0.55));
    albedo *= ao;
    albedo *= lerp(1.0 - _ShadowStrength, 1.0, layer01);

    float3 n = NormalizeNormalPerPixel(normalWS);
    float3 vDir = GetWorldSpaceNormalizeViewDir(positionWS);

#if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
    float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
    Light mainLight = GetMainLight(shadowCoord);
#else
    Light mainLight = GetMainLight();
#endif
    // DrawMeshInstanced / procedural draws leave unity_LightData.z = 0 (main light "culled").
    mainLight.distanceAttenuation = 1.0;

    half NdotL = saturate(dot(n, mainLight.direction));
    half atten = mainLight.shadowAttenuation * mainLight.distanceAttenuation;
    half3 lighting = mainLight.color * (atten * NdotL);
    half3 ambient = SampleSH(n) * ao;
    half3 color = albedo * (ambient + lighting);

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
    float3 hh = normalize(L + vDir);
    spec = pow(abs(saturate(dot(n, hh))), max(specExp, 1e-3)) * _Smoothness;
#endif
    color += spec * mainLight.color * mainLight.shadowAttenuation * tipColor;
    return color;
}

struct Attributes
{
    uint vertexID : SV_VertexID;
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    float2 uv         : TEXCOORD2;
    float2 furUV      : TEXCOORD3;
    float  maskLayer  : TEXCOORD4;
    float  fogFactor  : TEXCOORD5;
    float  silhouette : TEXCOORD6;
    float  height01   : TEXCOORD7;
};

Varyings ShellFurGpuFinVert(Attributes input)
{
    Varyings output = (Varyings)0;
    FinVertexGPU v = _FinVertices[input.vertexID];

    float3 posWS = float3(v.px, v.py, v.pz);
    float3 nWS = float3(v.nx, v.ny, v.nz);
    float n2 = dot(nWS, nWS);
    nWS = n2 > 1e-12 ? nWS * rsqrt(n2) : float3(0, 1, 0);

    float h = saturate(v.height01);
    float sil = saturate(v.silhouette);

    output.positionCS = TransformWorldToHClip(posWS);
    output.positionWS = posWS;
    output.normalWS = nWS;
    float2 uv = float2(v.u, v.v);
    output.uv = TRANSFORM_TEX(uv, _BaseMap);
    output.furUV = TRANSFORM_TEX(uv, _FurMap);
    output.maskLayer = max(h * sil, 0.02);
    output.fogFactor = ComputeFogFactor(output.positionCS.z);
    output.silhouette = sil;
    output.height01 = h;
    return output;
}

half4 ShellFurGpuFinFrag(Varyings input) : SV_Target
{
    if (input.silhouette < 0.001)
        discard;

    float alpha;
    float strandHeight;
    if (!EvaluateFurMaskGpuFin(input.furUV, input.maskLayer, alpha, strandHeight))
        discard;

    alpha *= saturate(input.silhouette);
    alpha *= EvaluateFinHeightOpacity(input.height01);

    if (alpha < 1.0 / 255.0)
        discard;

    half3 color = ShadeShellFurGpuFin(
        input.positionWS, input.normalWS, input.uv, input.height01, strandHeight);
    color = MixFog(color, input.fogFactor);
    return half4(color, alpha);
}

#endif // SHELL_FUR_GPU_FIN_FORWARD

// ---------------------------------------------------------------------------
// ShadowCaster — Shadows.hlsl only (no full lighting library).
// ---------------------------------------------------------------------------
#if defined(SHELL_FUR_GPU_FIN_SHADOW)

// Shadows.hlsl uses LerpWhiteTo from CommonMaterial — required even for ApplyShadowBias path.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

float3 _LightDirection;
float3 _LightPosition;

struct ShadowAttributes
{
    uint vertexID : SV_VertexID;
};

struct ShadowVaryings
{
    float4 positionCS : SV_POSITION;
    float2 furUV      : TEXCOORD0;
    float  maskLayer  : TEXCOORD1;
    float  silhouette : TEXCOORD2;
    float  height01   : TEXCOORD3;
};

ShadowVaryings ShellFurGpuFinShadowVert(ShadowAttributes input)
{
    ShadowVaryings output = (ShadowVaryings)0;
    FinVertexGPU v = _FinVertices[input.vertexID];

    float3 posWS = float3(v.px, v.py, v.pz);
    float3 nWS = float3(v.nx, v.ny, v.nz);
    float n2 = dot(nWS, nWS);
    nWS = n2 > 1e-12 ? nWS * rsqrt(n2) : float3(0, 1, 0);

    float h = saturate(v.height01);
    float sil = saturate(v.silhouette);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - posWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    output.positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, nWS, lightDirectionWS));
#if UNITY_REVERSED_Z
    output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
    output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif
    output.furUV = TRANSFORM_TEX(float2(v.u, v.v), _FurMap);
    output.maskLayer = max(h * sil, 0.02);
    output.silhouette = sil;
    output.height01 = h;
    return output;
}

half4 ShellFurGpuFinShadowFrag(ShadowVaryings input) : SV_Target
{
    if (input.silhouette < 0.02)
        discard;

    float alpha;
    float strandHeight;
    if (!EvaluateFurMaskGpuFin(input.furUV, input.maskLayer, alpha, strandHeight))
        discard;
    alpha *= saturate(input.silhouette);
    alpha *= EvaluateFinHeightOpacity(input.height01);
    clip(alpha - 0.01);
    return 0;
}

#endif // SHELL_FUR_GPU_FIN_SHADOW

#endif // SHELL_FUR_GPU_FIN_INCLUDED

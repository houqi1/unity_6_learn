#ifndef SHELL_FUR_INCLUDED
#define SHELL_FUR_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------
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
    float  _FurLength;
    float  _FurLengthRandom;
    float  _Gravity;
    float4 _GravityDir;
    float  _Smoothness;
    float  _RimPower;
    float  _RimStrength;
    float  _ShadowStrength;
CBUFFER_END

// ---------------------------------------------------------------------------
// Hash / noise helpers (procedural strands)
// ---------------------------------------------------------------------------
float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

// ---------------------------------------------------------------------------
// Shared shell evaluation
// ---------------------------------------------------------------------------
struct ShellData
{
    float  layer;       // 0..1 normalized shell height
    float  layerIndex;  // raw instance id
    float3 positionOS;
    float3 normalOS;
    float2 uv;
    float2 furUV;
};

float GetShellLayer(uint instanceId)
{
    float count = max(_ShellCount, 1.0);
    // Layer 0 sits on the surface; last layer is near the tip.
    return saturate((float)instanceId / max(count - 1.0, 1.0));
}

float3 ApplyShellDisplacement(float3 positionOS, float3 normalOS, float layer)
{
    // Quadratic bend so tips respond more than roots.
    float layer2 = layer * layer;

    // Gravity is authored in world space, then converted to object space
    // so rotated meshes still droop "down" in the world.
    float3 gravityWS = normalize(_GravityDir.xyz + 1e-5) * (_Gravity * layer2 * _FurLength);
    float3 bendOS = TransformWorldToObjectDir(gravityWS, false);
    float3 offset = normalOS * (layer * _FurLength) + bendOS;
    return positionOS + offset;
}

// Returns true if the strand is visible at this shell layer.
// alphaOut is used for soft cutoff / shadow consistency.
bool EvaluateFurMask(float2 furUV, float layer, out float alphaOut, out float strandHeight)
{
    alphaOut = 0.0;
    strandHeight = 1.0;

    // Layer 0 is the solid skin / root — never cut holes into the body.
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

    // Strands that are shorter than the current shell disappear.
    if (layer > strandHeight)
        return false;

    // Taper: thicker near root, thinner near tip of this strand.
    float heightAlongStrand = layer / max(strandHeight, 1e-4);
    float radius = _Thickness * 0.5 * (1.0 - heightAlongStrand * 0.85);
    // Mild per-strand thickness variation.
    radius *= lerp(0.7, 1.15, h1);

    float dist = length(local);
    if (dist > radius)
        return false;

    // Soft edge inside the strand for AA-ish look with AlphaToMask.
    alphaOut = saturate(1.0 - dist / max(radius, 1e-4));
    alphaOut = pow(abs(alphaOut), 0.75);
#else
    float density = SAMPLE_TEXTURE2D(_FurMap, sampler_FurMap, furUV).r;
    // Remap density into strand height with randomness-like threshold curve.
    strandHeight = saturate(density * (1.0 + _FurLengthRandom) - _FurLengthRandom * 0.5);

    if (layer > strandHeight)
        return false;

    // Higher layers require higher density (classic shell fur cutoff).
    float threshold = lerp(_AlphaCutoff, 1.0, layer);
    if (density < threshold)
        return false;

    alphaOut = saturate((density - threshold) / max(1.0 - threshold, 1e-4));
#endif

    return alphaOut > 0.01;
}

half3 ShadeShellFur(float3 positionWS, float3 normalWS, float2 uv, float layer, float strandHeight)
{
    half4 albedoSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    half3 rootColor = _BaseColor.rgb * albedoSample.rgb;
    half3 tipColor  = _TipColor.rgb  * albedoSample.rgb;

    // Color along the strand, slightly influenced by per-strand height.
    float tipFactor = saturate(layer / max(strandHeight, 1e-4));
    half3 albedo = lerp(rootColor, tipColor, tipFactor);

    // Fake AO denser near the roots.
    // Use abs() on pow bases — DX11 treats "pow may get negative" as a compile error.
    float layer01 = saturate(layer);
    float ao = lerp(1.0 - _Occlusion, 1.0, pow(abs(layer01), 0.55));
    albedo *= ao;

    // Extra self-shadowing between shells.
    albedo *= lerp(1.0 - _ShadowStrength, 1.0, layer01);

    float3 n = NormalizeNormalPerPixel(normalWS);
    float3 v = GetWorldSpaceNormalizeViewDir(positionWS);

    // Main light (with shadows when available).
    float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
    Light mainLight = GetMainLight(shadowCoord);
    half NdotL = saturate(dot(n, mainLight.direction));
    half3 lighting = mainLight.color * (mainLight.shadowAttenuation * mainLight.distanceAttenuation * NdotL);

    // Ambient from SH.
    half3 ambient = SampleSH(n) * ao;
    half3 color = albedo * (ambient + lighting);

#if defined(_ADDITIONAL_LIGHTS)
    uint lightsCount = GetAdditionalLightsCount();
    for (uint lightIndex = 0u; lightIndex < lightsCount; ++lightIndex)
    {
        Light light = GetAdditionalLight(lightIndex, positionWS);
        half addNdotL = saturate(dot(n, light.direction));
        color += albedo * light.color * (light.distanceAttenuation * light.shadowAttenuation * addNdotL);
    }
#endif

    // Soft rim to lift silhouettes.
    float ndotv = saturate(dot(n, v));
    float rim = pow(abs(1.0 - ndotv), max(_RimPower, 0.0001)) * _RimStrength * layer01;
    color += tipColor * rim;

    // Mild specular lobe along the light for a bit of sheen.
    float3 h = normalize(mainLight.direction + v);
    float spec = pow(abs(saturate(dot(n, h))), lerp(8.0, 64.0, saturate(_Smoothness))) * _Smoothness;
    color += spec * mainLight.color * mainLight.shadowAttenuation * tipColor;

    return color;
}

// ---------------------------------------------------------------------------
// Forward
// ---------------------------------------------------------------------------
struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv         : TEXCOORD0;
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
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings ShellFurVert(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    uint id = 0;
#ifdef UNITY_INSTANCING_ENABLED
    id = unity_InstanceID;
#endif

    float layer = GetShellLayer(id);
    float3 posOS = ApplyShellDisplacement(input.positionOS.xyz, input.normalOS, layer);

    VertexPositionInputs posInputs = GetVertexPositionInputs(posOS);
    VertexNormalInputs   nrmInputs = GetVertexNormalInputs(input.normalOS);

    output.positionCS = posInputs.positionCS;
    output.positionWS = posInputs.positionWS;
    output.normalWS   = nrmInputs.normalWS;
    output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
    output.furUV      = TRANSFORM_TEX(input.uv, _FurMap);
    output.layer      = layer;
    output.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);
    return output;
}

half4 ShellFurFrag(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    float alpha;
    float strandHeight;
    if (!EvaluateFurMask(input.furUV, input.layer, alpha, strandHeight))
        discard;

    // Soft threshold for AlphaToMask / MSAA coverage.
    clip(alpha - 0.01);

    half3 color = ShadeShellFur(input.positionWS, input.normalWS, input.uv, input.layer, strandHeight);
    color = MixFog(color, input.fogFactor);
    return half4(color, alpha);
}

// ---------------------------------------------------------------------------
// ShadowCaster
// ---------------------------------------------------------------------------
struct ShadowAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct ShadowVaryings
{
    float4 positionCS : SV_POSITION;
    float2 furUV      : TEXCOORD0;
    float  layer      : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

float3 _LightDirection;
float3 _LightPosition;

ShadowVaryings ShellFurShadowVert(ShadowAttributes input)
{
    ShadowVaryings output = (ShadowVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    uint id = 0;
#ifdef UNITY_INSTANCING_ENABLED
    id = unity_InstanceID;
#endif

    float layer = GetShellLayer(id);
    float3 posOS = ApplyShellDisplacement(input.positionOS.xyz, input.normalOS, layer);

    float3 positionWS = TransformObjectToWorld(posOS);
    float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
#if UNITY_REVERSED_Z
    output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
    output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif

    output.furUV = TRANSFORM_TEX(input.uv, _FurMap);
    output.layer = layer;
    return output;
}

half4 ShellFurShadowFrag(ShadowVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);

    float alpha;
    float strandHeight;
    if (!EvaluateFurMask(input.furUV, input.layer, alpha, strandHeight))
        discard;
    clip(alpha - 0.01);
    return 0;
}

// ---------------------------------------------------------------------------
// DepthOnly
// ---------------------------------------------------------------------------
struct DepthAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct DepthVaryings
{
    float4 positionCS : SV_POSITION;
    float2 furUV      : TEXCOORD0;
    float  layer      : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

DepthVaryings ShellFurDepthVert(DepthAttributes input)
{
    DepthVaryings output = (DepthVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    uint id = 0;
#ifdef UNITY_INSTANCING_ENABLED
    id = unity_InstanceID;
#endif

    float layer = GetShellLayer(id);
    float3 posOS = ApplyShellDisplacement(input.positionOS.xyz, input.normalOS, layer);

    output.positionCS = TransformObjectToHClip(posOS);
    output.furUV = TRANSFORM_TEX(input.uv, _FurMap);
    output.layer = layer;
    return output;
}

half4 ShellFurDepthFrag(DepthVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);

    float alpha;
    float strandHeight;
    if (!EvaluateFurMask(input.furUV, input.layer, alpha, strandHeight))
        discard;
    clip(alpha - 0.01);
    return 0;
}

#endif // SHELL_FUR_INCLUDED

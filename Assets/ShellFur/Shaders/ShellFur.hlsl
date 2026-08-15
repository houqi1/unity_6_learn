#ifndef SHELL_FUR_INCLUDED
#define SHELL_FUR_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// Shell fur is drawn with Graphics.DrawMeshInstanced + MaterialPropertyBlock.
// That path does not populate per-object unity_LightData the way MeshRenderer does.
// URP GetMainLight() (non-Forward+) sets:
//   distanceAttenuation = unity_LightData.z  // 1 = not culled, else 0
// which is often 0 for instanced draws, killing main-light Lambert even though
// globals _MainLightPosition / _MainLightColor are valid. Main light is a frame
// global — force distance atten to 1 and keep realtime shadows.
Light GetShellFurMainLight(float3 positionWS)
{
    float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
    Light light = GetMainLight(shadowCoord);
    light.distanceAttenuation = 1.0;
    return light;
}

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
    float  _TipAlphaCutoff;
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
    float4 _CustomLightDir;
    float  _FinSilhouetteSharpness;
    float  _FinExtrudeWeight;
    float  _FinSilhouetteBias;
    float  _FinSilhouettePower;
    float  _FinBandStrength;
    float  _FinRootOffset;
    float  _FinLengthScale;
    float  _FinRootOpacity;
    float  _FinTipOpacity;
    float  _FinOpacityFadeStart;
    float  _FinOpacityFadeEnd;
    float  _FinOpacityPower;
CBUFFER_END

// Guide additive offsets δ. Shell = pure extrude (n*h*L) + δ when _UseFurChain; GravityBend off.
// CPU: δ(h) = ((chainPos(h)-root)/chainLen) * guideOffsetScale; shader samples by layer h.
// Outside UnityPerMaterial — set via MaterialPropertyBlock.
// Size must match ShellFurDynamics.MaxNodes
float4 _FurChain[17];
float  _FurChainCount;
float  _UseFurChain;
float4 _FurChainErect; // xyz = erect axis used by simulation (usually -gravity)

float3 SampleFurChainOffsetWS(float layer)
{
    int count = (int)clamp(_FurChainCount, 1.0, 17.0);
    float t = saturate(layer) * max((float)count - 1.0, 0.0);
    int i0 = (int)floor(t);
    int i1 = min(i0 + 1, count - 1);
    float f = t - (float)i0;
    return lerp(_FurChain[i0].xyz, _FurChain[i1].xyz, f);
}

// Static-only nonlinear gravity (used when dynamics is off).
float3 GravityBendWS(float layer, float lengthScale)
{
    float h = saturate(layer);
    float p = max(_GravityPower, 0.01);
    float w = pow(h, p);
    return normalize(_GravityDir.xyz + 1e-5) * (_Gravity * w * lengthScale);
}

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
    // Optional offset skips base skin (layer 0) when hide-base is on in slot mode.
    float idx = (float)instanceId + max(_ShellLayerOffset, 0.0);
    // Layer 0 sits on the surface; last layer is near the tip.
    return saturate(idx / max(count - 1.0, 1.0));
}

// Decode object-space unit normal stored in vertex color (RGB = n * 0.5 + 0.5).
float3 DecodeSmoothNormalOS(float4 vertexColor)
{
    float3 n = vertexColor.rgb * 2.0 - 1.0;
    float len2 = dot(n, n);
    return len2 > 1e-8 ? n * rsqrt(len2) : float3(0, 1, 0);
}

float3 ResolveExtrudeNormalOS(float3 meshNormalOS, float4 vertexColor)
{
#if defined(_USE_SMOOTH_NORMALS_VC)
    return DecodeSmoothNormalOS(vertexColor);
#else
    return meshNormalOS;
#endif
}

float3 ApplyShellDisplacement(float3 positionOS, float3 extrudeNormalOS, float layer)
{
    // Classic shell base (always).
    float3 offset = normalize(extrudeNormalOS + 1e-5) * (layer * _FurLength);

    if (_UseFurChain > 0.5)
    {
        // Additive guide δ only — base stays pure extrude (no GravityBend).
        offset += TransformWorldToObjectDir(SampleFurChainOffsetWS(layer), false);
    }
    else
    {
        // Dynamics off: static nonlinear gravity.
        offset += TransformWorldToObjectDir(GravityBendWS(layer, _FurLength), false);
    }
    return positionOS + offset;
}

// Returns true if the strand is visible at this shell layer.
// alphaOut is used for soft cutoff / shadow consistency.
bool EvaluateFurMask(float2 furUV, float layer, out float alphaOut, out float strandHeight)
{
    alphaOut = 0.0;
    strandHeight = 1.0;

    // Shell layer 0 is solid skin. Fin cards always use density (no solid root strip).
#if !defined(SHELL_FUR_FIN)
    if (layer < 0.001)
    {
        alphaOut = 1.0;
        strandHeight = 1.0;
        return true;
    }
#endif

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
    float threshold = lerp(_AlphaCutoff, _TipAlphaCutoff, layer);
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
    // Occlude toward base/root color, not black.
    albedo = lerp(rootColor, albedo, ao);

    // Extra self-shadowing between shells.
    albedo *= lerp(1.0 - _ShadowStrength, 1.0, layer01);

    float3 n = NormalizeNormalPerPixel(normalWS);

    // Debug: world-space normals remapped to 0..1 (RGB = n*0.5+0.5). Highest priority.
#if defined(_DEBUG_NORMALS)
    return n * 0.5 + 0.5;
#endif

    // Main light: direction/color from URP globals; see GetShellFurMainLight().
    Light mainLight = GetShellFurMainLight(positionWS);

    // Light direction: URP main light, or material custom world-space direction.
#if defined(_USE_CUSTOM_LIGHT_DIR)
    float3 lightDirWS = normalize(_CustomLightDir.xyz + 1e-5);
    half mainAtten = 1.0; // custom dir is unbounded; do not inherit main-light shadows
#else
    float3 lightDirWS = mainLight.direction;
    half mainAtten = mainLight.shadowAttenuation * mainLight.distanceAttenuation;
#endif

    half NdotL = saturate(dot(n, lightDirWS));
    half3 lambert = mainLight.color * (mainAtten * NdotL);

#if defined(_ADDITIONAL_LIGHTS) && !defined(_USE_CUSTOM_LIGHT_DIR)
    uint lightsCount = GetAdditionalLightsCount();
    for (uint lightIndex = 0u; lightIndex < lightsCount; ++lightIndex)
    {
        Light light = GetAdditionalLight(lightIndex, positionWS);
        half addNdotL = saturate(dot(n, light.direction));
        lambert += light.color * (light.distanceAttenuation * light.shadowAttenuation * addNdotL);
    }
#endif

    // Debug: pure Lambert only (Σ lightColor * atten * saturate(N·L)). No albedo/AO/ambient/rim/spec.
#if defined(_DEBUG_DIFFUSE)
    return lambert;
#endif

    // Ambient from SH.
    half3 ambient = SampleSH(n) * ao;
    half3 color = albedo * (ambient + lambert);

    float3 v = GetWorldSpaceNormalizeViewDir(positionWS);

    // Soft rim to lift silhouettes.
    float ndotv = saturate(dot(n, v));
    float rim = pow(abs(1.0 - ndotv), max(_RimPower, 0.0001)) * _RimStrength * layer01;
    color += tipColor * rim;

    // Specular: Blinn-Phong (default) or Kajiya-Kay (hair fiber along shell normal).
    float3 L = lightDirWS;
    float specExp = lerp(8.0, 64.0, saturate(_Smoothness));
    float spec = 0.0;

#if defined(_USE_KAJIYA_KAY)
    // Strand tangent ≈ extrusion direction (surface normal for shell fur).
    float3 T = n;
    float TdotL = clamp(dot(T, L), -1.0, 1.0);
    float TdotV = clamp(dot(T, v), -1.0, 1.0);
    float sinTL = sqrt(max(0.0, 1.0 - TdotL * TdotL));
    float sinTV = sqrt(max(0.0, 1.0 - TdotV * TdotV));
    // Classic Kajiya-Kay longitudinal specular: cos(θi − θr)
    float kk = saturate(TdotL * TdotV + sinTL * sinTV);
    spec = pow(abs(kk), max(specExp, 1e-3)) * _Smoothness;
    // Slightly stronger toward tips so sheen reads on outer shells.
    spec *= lerp(0.35, 1.0, layer01);
#else
    float3 h = normalize(L + v);
    spec = pow(abs(saturate(dot(n, h))), max(specExp, 1e-3)) * _Smoothness;
#endif

    color += spec * mainLight.color * mainAtten * tipColor;

    return color;
}

// ---------------------------------------------------------------------------
// Forward
// ---------------------------------------------------------------------------
struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 color      : COLOR;
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
    float3 extrudeN = ResolveExtrudeNormalOS(input.normalOS, input.color);
    float3 posOS = ApplyShellDisplacement(input.positionOS.xyz, extrudeN, layer);

    VertexPositionInputs posInputs = GetVertexPositionInputs(posOS);
    // Lighting uses the same normal as extrusion when smooth-from-VC is on.
#if defined(_USE_SMOOTH_NORMALS_VC)
    float3 normalWS = TransformObjectToWorldNormal(extrudeN);
#else
    float3 normalWS = GetVertexNormalInputs(input.normalOS).normalWS;
#endif

    output.positionCS = posInputs.positionCS;
    output.positionWS = posInputs.positionWS;
    output.normalWS   = normalWS;
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

    half3 color = ShadeShellFur(input.positionWS, input.normalWS, input.uv, input.layer, strandHeight);
    color = MixFog(color, input.fogFactor);
    return half4(color, 1);
}

// ---------------------------------------------------------------------------
// ShadowCaster
// ---------------------------------------------------------------------------
struct ShadowAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 color      : COLOR;
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
    float3 extrudeN = ResolveExtrudeNormalOS(input.normalOS, input.color);
    float3 posOS = ApplyShellDisplacement(input.positionOS.xyz, extrudeN, layer);

    float3 positionWS = TransformObjectToWorld(posOS);
    float3 normalWS   = TransformObjectToWorldNormal(extrudeN);

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
    return 0;
}

// ---------------------------------------------------------------------------
// DepthOnly
// ---------------------------------------------------------------------------
struct DepthAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 color      : COLOR;
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
    float3 extrudeN = ResolveExtrudeNormalOS(input.normalOS, input.color);
    float3 posOS = ApplyShellDisplacement(input.positionOS.xyz, extrudeN, layer);

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
    return 0;
}

// ---------------------------------------------------------------------------
// Fins (pre-generated edge quads; silhouette extrusion in VS)
// Mesh layout:
//   POSITION  = base position OS (on surface edge)
//   NORMAL    = extrusion up OS
//   TEXCOORD0 = surface UV
//   TEXCOORD1 = adjacent face normal A (OS)
//   TEXCOORD2 = adjacent face normal B (OS)
//   TEXCOORD3 = (height01, unused)  0 = root, 1 = tip
// ---------------------------------------------------------------------------
#if defined(SHELL_FUR_FIN)

// Returns 0..1 weight for how much a fin tip should erect (view-dependent).
float ComputeFinSilhouette(float3 positionWS, float3 faceNormalA_OS, float3 faceNormalB_OS)
{
    float3 nA = TransformObjectToWorldNormal(faceNormalA_OS);
    float3 nB = TransformObjectToWorldNormal(faceNormalB_OS);
    float3 V = GetWorldSpaceNormalizeViewDir(positionWS);

    float dA = dot(nA, V);
    float dB = dot(nB, V);
    float sharp = max(_FinSilhouetteSharpness, 0.01);
    float raw = 0.0;

    // Boundary edges store nB == nA: use grazing of the single face.
    if (dot(nA, nB) > 0.995)
    {
        float graze = saturate(1.0 - abs(dA));
        raw = pow(abs(graze), lerp(2.5, 0.75, saturate(sharp / 16.0)));
    }
    else
    {
        // Manifold silhouette: adjacent faces on opposite sides of the view plane.
        float opposite = saturate(-dA * dB * sharp);
        // Soft band around the contour (reduces popping; scale with Band Strength).
        float mixAB = abs(dA) + abs(dB);
        float band = saturate(1.0 - mixAB * 0.5);
        band = pow(abs(band), 2.0) * saturate(_FinBandStrength);
        raw = saturate(opposite + band * opposite);
    }

    // Bias: raise to require a stronger silhouette before fins lift.
    raw = saturate(raw - saturate(_FinSilhouetteBias));

    // Power: >1 keeps only strong contours fully erect; <1 lifts a wider band.
    float p = max(_FinSilhouettePower, 1e-3);
    raw = pow(abs(raw), p);

    // Overall extrude weight (0 = flat, 1 = normal, >1 = stronger tips).
    return saturate(raw * max(_FinExtrudeWeight, 0.0));
}

float3 ApplyFinDisplacement(float3 baseOS, float3 upOS, float height01, float silhouette)
{
    float h = saturate(height01) * saturate(silhouette);
    float len = _FurLength * max(_FinLengthScale, 0.0);
    float rootLift = _FinRootOffset * (1.0 - saturate(height01));
    float3 up = normalize(upOS + 1e-5);
    float hBend = saturate(height01);

    // Base fin extrusion + optional additive guide offset.
    float3 pos = baseOS + up * (len * h + rootLift);
    if (_UseFurChain > 0.5)
        pos += TransformWorldToObjectDir(SampleFurChainOffsetWS(hBend), false);
    else
        pos += TransformWorldToObjectDir(GravityBendWS(hBend, len), false);
    return pos;
}

struct FinAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;     // up
    float2 uv         : TEXCOORD0;
    float3 faceA      : TEXCOORD1;
    float3 faceB      : TEXCOORD2;
    float2 height     : TEXCOORD3;  // x = 0 root / 1 tip
};

// Root (height=0) → tip (height=1) opacity falloff along the fin card.
float EvaluateFinHeightOpacity(float height01)
{
    float h = saturate(height01);
    float fadeStart = saturate(_FinOpacityFadeStart);
    float fadeEnd = max(saturate(_FinOpacityFadeEnd), fadeStart + 1e-4);
    // 0 before start, 1 after end.
    float t = saturate((h - fadeStart) / (fadeEnd - fadeStart));
    t = pow(abs(t), max(_FinOpacityPower, 1e-3));
    return lerp(saturate(_FinRootOpacity), saturate(_FinTipOpacity), t);
}

struct FinVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    float2 uv         : TEXCOORD2;
    float2 furUV      : TEXCOORD3;
    float  layer      : TEXCOORD4;
    float  fogFactor  : TEXCOORD5;
    float  silhouette : TEXCOORD6;
    float  height01   : TEXCOORD7;
};

FinVaryings ShellFurFinVert(FinAttributes input)
{
    FinVaryings output = (FinVaryings)0;

    float3 baseWS = TransformObjectToWorld(input.positionOS.xyz);
    float sil = ComputeFinSilhouette(baseWS, input.faceA, input.faceB);

    float height01 = saturate(input.height.x);
    float3 posOS = ApplyFinDisplacement(input.positionOS.xyz, input.normalOS, height01, sil);

    // Degenerate non-silhouette fins (tips collapse to base).
    VertexPositionInputs posInputs = GetVertexPositionInputs(posOS);
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

    output.positionCS = posInputs.positionCS;
    output.positionWS = posInputs.positionWS;
    output.normalWS   = normalWS;
    output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
    output.furUV      = TRANSFORM_TEX(input.uv, _FurMap);
    // Mask/density: view-weighted height. Color uses pure height01 (shell-like root→tip).
    output.layer      = height01 * sil;
    output.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);
    output.silhouette = sil;
    output.height01   = height01;
    return output;
}

half4 ShellFurFinFrag(FinVaryings input) : SV_Target
{
    // Cull almost-invisible fins early.
    if (input.silhouette < 0.001)
        discard;

    float alpha;
    float strandHeight;
    // Use max(layer, small) so root of fin still samples density taper.
    float maskLayer = max(input.layer, 0.02);
    // Density / procedural pattern still hard-masks holes (no partial strand coverage).
    if (!EvaluateFurMask(input.furUV, maskLayer, alpha, strandHeight))
        discard;

    alpha *= saturate(input.silhouette);
    // Root → tip opacity: smooth alpha blend (not clip/cutoff).
    alpha *= EvaluateFinHeightOpacity(input.height01);

#if defined(SHELL_FUR_FIN_ALPHA_BLEND)
    // Only skip fully transparent pixels; mid-alpha is blended by the pipeline.
    if (alpha < 1.0 / 255.0)
        discard;
#endif

    // Root→tip color uses pure height01 (same role as shell layer), not silhouette-scaled layer.
    half3 color = ShadeShellFur(input.positionWS, input.normalWS, input.uv, input.height01, strandHeight);
    color = MixFog(color, input.fogFactor);
    return half4(color, 1);
}

struct FinShadowAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv         : TEXCOORD0;
    float3 faceA      : TEXCOORD1;
    float3 faceB      : TEXCOORD2;
    float2 height     : TEXCOORD3;
};

struct FinShadowVaryings
{
    float4 positionCS : SV_POSITION;
    float2 furUV      : TEXCOORD0;
    float  layer      : TEXCOORD1;
    float  silhouette : TEXCOORD2;
    float  height01   : TEXCOORD3;
};

FinShadowVaryings ShellFurFinShadowVert(FinShadowAttributes input)
{
    FinShadowVaryings output = (FinShadowVaryings)0;

    float3 baseWS = TransformObjectToWorld(input.positionOS.xyz);
    float sil = ComputeFinSilhouette(baseWS, input.faceA, input.faceB);
    float height01 = input.height.x;
    float3 posOS = ApplyFinDisplacement(input.positionOS.xyz, input.normalOS, height01, sil);

    float3 positionWS = TransformObjectToWorld(posOS);
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

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
    output.layer = height01 * sil;
    output.silhouette = sil;
    output.height01 = height01;
    return output;
}

half4 ShellFurFinShadowFrag(FinShadowVaryings input) : SV_Target
{
    if (input.silhouette < 0.02)
        discard;

    float alpha;
    float strandHeight;
    float layer = max(input.layer, 0.02);
    if (!EvaluateFurMask(input.furUV, layer, alpha, strandHeight))
        discard;
    alpha *= saturate(input.silhouette);
    alpha *= EvaluateFinHeightOpacity(input.height01);
    return 0;
}

#endif // SHELL_FUR_FIN

#endif // SHELL_FUR_INCLUDED

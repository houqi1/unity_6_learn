#ifndef TREE_TRUNK_HLSL
#define TREE_TRUNK_HLSL

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

// -----------------------------------------------------------------------------
// Properties
// -----------------------------------------------------------------------------
TEXTURE2D(_BaseMap);            SAMPLER(sampler_BaseMap);
TEXTURE2D(_BumpMap);            SAMPLER(sampler_BumpMap);
TEXTURE2D(_MaskMap);            SAMPLER(sampler_MaskMap);
TEXTURE2D(_DetailAlbedoMap);    SAMPLER(sampler_DetailAlbedoMap);
TEXTURE2D(_DetailNormalMap);    SAMPLER(sampler_DetailNormalMap);

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _BumpMap_ST;
    float4 _MaskMap_ST;
    float4 _DetailAlbedoMap_ST;
    float4 _DetailNormalMap_ST;
    half4  _BaseColor;
    half   _BumpScale;
    half   _OcclusionStrength;
    half   _Smoothness;
    half   _Metallic;
    half   _DiffuseWrap;
    half4  _DiffuseColor;
    half4  _EmissionColor;
    half   _DetailNormalScale;
    half   _DetailAlbedoScale;
CBUFFER_END

// -----------------------------------------------------------------------------
// Dual-channel normal decode（与 Leaf 一致）
// -----------------------------------------------------------------------------
half3 DecodeDualChannelNormal(half4 packedNormal, half scale)
{
#if defined(_NORMALPACK_AG)
    return UnpackNormalAG(packedNormal, scale);
#elif defined(_NORMALPACK_RGB)
    return UnpackNormalRGB(packedNormal, scale);
#else
    half3 normal;
    normal.xy = packedNormal.rg * 2.0h - 1.0h;
    normal.z = max(1.0e-16h, sqrt(1.0h - saturate(dot(normal.xy, normal.xy))));
    normal.xy *= scale;
    return normal;
#endif
}

half3 SampleTrunkNormalTS(float2 uv)
{
    half4 packed = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv);
    half3 n = DecodeDualChannelNormal(packed, _BumpScale);

#if defined(_DETAIL)
    float2 detailUV = TRANSFORM_TEX(uv, _DetailNormalMap);
    half4 detailPacked = SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, detailUV);
    // Detail 默认按 RG 双通道解（与主法线模式无关，避免 detail 图被当成 RGB）
    half3 detailN;
    detailN.xy = detailPacked.rg * 2.0h - 1.0h;
    detailN.z = max(1.0e-16h, sqrt(1.0h - saturate(dot(detailN.xy, detailN.xy))));
    detailN.xy *= _DetailNormalScale;
    // Whiteout blend
    n = normalize(half3(n.xy + detailN.xy, n.z * detailN.z));
#endif
    return n;
}

// Wrap Lambert
half DiffuseWrapNdotL(half3 normalWS, half3 lightDirWS, half wrap)
{
    half NdotL = dot(normalWS, lightDirWS);
    half w = saturate(wrap);
    return saturate((NdotL + w) / (1.0h + w));
}

// -----------------------------------------------------------------------------
// Attributes / Varyings
// -----------------------------------------------------------------------------
struct TrunkAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;
    float2 uv         : TEXCOORD0;
    float2 staticLightmapUV : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct TrunkVaryings
{
    float4 positionCS  : SV_POSITION;
    float2 uv          : TEXCOORD0;
    float3 positionWS  : TEXCOORD1;
    float3 normalWS    : TEXCOORD2;
    float4 tangentWS   : TEXCOORD3;
    float  fogFactor   : TEXCOORD4;
    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord : TEXCOORD5;
    #endif
    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 6);
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// =============================================================================
// Forward Lit
// =============================================================================
#if defined(TRUNK_FORWARD_PASS)

TrunkVaryings TrunkVert(TrunkAttributes input)
{
    TrunkVaryings o = (TrunkVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs   nrmInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

    o.positionCS = posInputs.positionCS;
    o.positionWS = posInputs.positionWS;
    o.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
    o.normalWS   = nrmInputs.normalWS;
    real sign    = input.tangentOS.w * GetOddNegativeScale();
    o.tangentWS  = float4(nrmInputs.tangentWS.xyz, sign);
    o.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);

    OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, o.staticLightmapUV);
    OUTPUT_SH(o.normalWS, o.vertexSH);

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    o.shadowCoord = GetShadowCoord(posInputs);
    #endif
    return o;
}

half4 TrunkFrag(TrunkVaryings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

    // --- Albedo ---
    half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
    half3 albedo = baseSample.rgb * _BaseColor.rgb;

#if defined(_DETAIL)
    float2 detailUV = TRANSFORM_TEX(i.uv, _DetailAlbedoMap);
    half3 detailAlbedo = SAMPLE_TEXTURE2D(_DetailAlbedoMap, sampler_DetailAlbedoMap, detailUV).rgb;
    // Unity-style detail: lerp around 0.5 grey
    albedo = albedo * lerp(half3(1, 1, 1), detailAlbedo * 2.0h, saturate(_DetailAlbedoScale));
#endif

    // --- Mask ---
    half occlusion = 1.0h;
    half smoothness = _Smoothness;
    half metallic = _Metallic;
#if defined(_MASKMAP)
    half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, i.uv);
    occlusion  = LerpWhiteTo(mask.r, _OcclusionStrength);
    smoothness = (1.0h - mask.g) * _Smoothness;
    metallic   = mask.b * _Metallic;
#endif

    // --- Normal ---
    half3 normalTS = SampleTrunkNormalTS(i.uv);
    half3 nWS = normalize(i.normalWS);
    half3 tWS = normalize(i.tangentWS.xyz);
    half3 bWS = cross(nWS, tWS) * i.tangentWS.w;
    half3 normalWS = normalize(TransformTangentToWorld(normalTS, half3x3(tWS, bWS, nWS)));

    float3 positionWS = i.positionWS;
    half3  viewDirWS  = GetWorldSpaceNormalizeViewDir(positionWS);

    // --- Shadow / Main light ---
    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord = i.shadowCoord;
    #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
    #else
    float4 shadowCoord = float4(0, 0, 0, 0);
    #endif

    Light mainLight = GetMainLight(shadowCoord);
    half3 lightDir  = mainLight.direction;
    half  lightAtten = mainLight.distanceAttenuation * mainLight.shadowAttenuation;
    half3 lightCol  = mainLight.color * lightAtten;

    // Diffuse：自定义色，不乘灯光颜色
    half NdotL = DiffuseWrapNdotL(normalWS, lightDir, _DiffuseWrap);
    half3 diffuse = albedo * (1.0h - metallic) * _DiffuseColor.rgb * NdotL * lightAtten;

    // Specular：仍用真实灯光色
    half3 halfDir = normalize(lightDir + viewDirWS);
    half  NdotH   = saturate(dot(normalWS, halfDir));
    half  specPow = exp2(10.0h * smoothness + 1.0h);
    half3 specular = lightCol * pow(NdotH, specPow) * smoothness * (0.04h + metallic * 0.96h);

    // Ambient
    half3 bakedGI = SAMPLE_GI(i.staticLightmapUV, i.vertexSH, normalWS);
    half3 ambient = bakedGI * albedo * occlusion;

    // Emission = albedo * HDR
    half3 emission = albedo * _EmissionColor.rgb;

    half3 color = ambient + diffuse + specular + emission;

    // Additional lights
    #ifdef _ADDITIONAL_LIGHTS
    {
        uint lightCount = GetAdditionalLightsCount();
        #if USE_FORWARD_PLUS
        InputData inputData = (InputData)0;
        inputData.positionWS = positionWS;
        inputData.normalWS = normalWS;
        inputData.viewDirectionWS = viewDirWS;
        inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
        #endif
        LIGHT_LOOP_BEGIN(lightCount)
            Light light = GetAdditionalLight(lightIndex, positionWS);
            half  addAtten = light.distanceAttenuation * light.shadowAttenuation;
            half3 addCol = light.color * addAtten;
            half  addNdotL = DiffuseWrapNdotL(normalWS, light.direction, _DiffuseWrap);
            color += albedo * (1.0h - metallic) * _DiffuseColor.rgb * addNdotL * addAtten;

            half3 addHalf = normalize(light.direction + viewDirWS);
            half  addNdotH = saturate(dot(normalWS, addHalf));
            color += addCol * pow(addNdotH, specPow) * smoothness * (0.04h + metallic * 0.96h);
        LIGHT_LOOP_END
    }
    #endif

    color = MixFog(color, i.fogFactor);
    return half4(color, 1.0h);
}

#endif // TRUNK_FORWARD_PASS

// =============================================================================
// Shadow Caster
// =============================================================================
#if defined(TRUNK_SHADOW_PASS)

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

float3 _LightDirection;
float3 _LightPosition;

struct TrunkShadowAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct TrunkShadowVaryings
{
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

float4 GetTrunkShadowPositionHClip(float3 positionOS, float3 normalOS)
{
    float3 positionWS = TransformObjectToWorld(positionOS);
    float3 normalWS   = TransformObjectToWorldNormal(normalOS);

    #if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
    #else
    float3 lightDirectionWS = _LightDirection;
    #endif

    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
    #if UNITY_REVERSED_Z
    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
    #else
    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
    #endif
    return positionCS;
}

TrunkShadowVaryings TrunkShadowVert(TrunkShadowAttributes input)
{
    TrunkShadowVaryings o;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, o);
    o.positionCS = GetTrunkShadowPositionHClip(input.positionOS.xyz, input.normalOS);
    return o;
}

half4 TrunkShadowFrag(TrunkShadowVaryings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    return 0;
}

#endif // TRUNK_SHADOW_PASS

// =============================================================================
// Depth Only
// =============================================================================
#if defined(TRUNK_DEPTH_PASS)

struct TrunkDepthAttributes
{
    float4 positionOS : POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct TrunkDepthVaryings
{
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

TrunkDepthVaryings TrunkDepthVert(TrunkDepthAttributes input)
{
    TrunkDepthVaryings o;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, o);
    o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    return o;
}

half4 TrunkDepthFrag(TrunkDepthVaryings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    return 0;
}

#endif // TRUNK_DEPTH_PASS

// =============================================================================
// Depth Normals
// =============================================================================
#if defined(TRUNK_DEPTHNORMALS_PASS)

struct TrunkDNAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct TrunkDNVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    float4 tangentWS  : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

TrunkDNVaryings TrunkDepthNormalsVert(TrunkDNAttributes input)
{
    TrunkDNVaryings o;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, o);

    VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs   nrmInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

    o.positionCS = posInputs.positionCS;
    o.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
    o.normalWS   = nrmInputs.normalWS;
    real sign    = input.tangentOS.w * GetOddNegativeScale();
    o.tangentWS  = float4(nrmInputs.tangentWS.xyz, sign);
    return o;
}

half4 TrunkDepthNormalsFrag(TrunkDNVaryings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);

    half3 normalTS = SampleTrunkNormalTS(i.uv);
    half3 nWS = normalize(i.normalWS);
    half3 tWS = normalize(i.tangentWS.xyz);
    half3 bWS = cross(nWS, tWS) * i.tangentWS.w;
    half3 normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, half3x3(tWS, bWS, nWS)));

    return half4(normalWS, 0.0h);
}

#endif // TRUNK_DEPTHNORMALS_PASS

#endif // TREE_TRUNK_HLSL

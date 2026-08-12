#ifndef LEAF_HLSL
#define LEAF_HLSL

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

// -----------------------------------------------------------------------------
// Properties
// -----------------------------------------------------------------------------
TEXTURE2D(_BaseMap);        SAMPLER(sampler_BaseMap);
TEXTURE2D(_BumpMap);        SAMPLER(sampler_BumpMap);
TEXTURE2D(_MaskMap);        SAMPLER(sampler_MaskMap);

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _BumpMap_ST;
    float4 _MaskMap_ST;
    half4  _BaseColor;
    half   _Cutoff;
    half   _AlphaScale;
    half   _BumpScale;
    half   _OcclusionStrength;
    half   _Smoothness;
    half   _Metallic;
    half   _DiffuseWrap;
    half4  _DiffuseColor;
    half4  _EmissionColor;
    half4  _TranslucencyColor;
    half   _TranslucencyPower;
    half   _TranslucencyStrength;
    half   _TranslucencyDistortion;
    half   _WindStrength;
    half   _WindSpeed;
    half   _WindFrequency;
CBUFFER_END

// -----------------------------------------------------------------------------
// Dual-channel normal decode
// 双通道法线只存 X/Y（RG 或 AG），Z 由单位向量约束重建：
//   n.xy = packed * 2 - 1
//   n.z  = sqrt(1 - saturate(dot(n.xy, n.xy)))
// -----------------------------------------------------------------------------
half3 DecodeDualChannelNormal(half4 packedNormal, half scale)
{
#if defined(_NORMALPACK_AG)
    // DXT5nm / Unity 默认：X in Alpha, Y in Green
    // UnpackNormalAG 内部已做 Z 重建
    return UnpackNormalAG(packedNormal, scale);
#elif defined(_NORMALPACK_RGB)
    // 完整 RGB 法线（蓝紫调传统法线贴图）
    return UnpackNormalRGB(packedNormal, scale);
#else
    // 默认 RG：BC5 / 手工双通道（R=X, G=Y）
    half3 normal;
    normal.xy = packedNormal.rg * 2.0h - 1.0h;
    // 先重建 Z，再缩放 XY（与 URP UnpackNormalAG 一致）
    normal.z = max(1.0e-16h, sqrt(1.0h - saturate(dot(normal.xy, normal.xy))));
    normal.xy *= scale;
    return normal;
#endif
}

// 采样并解码切线空间法线
half3 SampleLeafNormalTS(float2 uv)
{
    half4 packed = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv);
    return DecodeDualChannelNormal(packed, _BumpScale);
}

// -----------------------------------------------------------------------------
// Wind（可选简易顶点摆动）
// -----------------------------------------------------------------------------
float3 ApplyLeafWind(float3 positionOS, float3 normalOS, float2 uv)
{
#if defined(_WIND)
    // 用 UV.y 近似叶片尖端权重：根部不动、尖端摆动更强
    float tip = saturate(uv.y);
    float t = _Time.y * _WindSpeed;
    float phase = dot(positionOS.xz, float2(_WindFrequency, _WindFrequency * 0.73));
    float2 wind = float2(
        sin(t + phase),
        cos(t * 1.17 + phase * 0.9)
    ) * (_WindStrength * tip);
    positionOS.xz += wind;
    // 轻微沿法线抬升，避免平面内挤扁
    positionOS += normalOS * (abs(wind.x) + abs(wind.y)) * 0.15;
#endif
    return positionOS;
}

// -----------------------------------------------------------------------------
// Shared attributes / varyings
// -----------------------------------------------------------------------------
struct LeafAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;
    float2 uv         : TEXCOORD0;
    float2 staticLightmapUV : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct LeafVaryings
{
    float4 positionCS  : SV_POSITION;
    float2 uv          : TEXCOORD0;
    float3 positionWS  : TEXCOORD1;
    float3 normalWS    : TEXCOORD2;
    float4 tangentWS   : TEXCOORD3; // xyz = tangent, w = bitangent sign
    float  fogFactor   : TEXCOORD4;
    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord : TEXCOORD5;
    #endif
    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 6);
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------
half SampleLeafAlpha(float2 uv)
{
    half a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).a * _BaseColor.a * _AlphaScale;
    return saturate(a);
}

void LeafClipAlpha(float2 uv)
{
    half a = SampleLeafAlpha(uv);
    clip(a - _Cutoff);
}

// 双面：背面翻转世界法线 / 切线（配合 Cull Off）
void ApplyFaceFlip(inout half3 normalWS, inout half3 tangentWS, half faceSign)
{
    // VFACE: 正面 +1，背面 -1
    normalWS  *= faceSign;
    tangentWS *= faceSign;
}

half3 Translucency(half3 lightDirWS, half3 viewDirWS, half3 normalWS, half3 lightColor, half3 albedo)
{
    // 次表面透射：光从背面穿透叶片
    half3 backLitDir = lightDirWS + normalWS * _TranslucencyDistortion;
    half  vDotL = pow(saturate(dot(viewDirWS, -backLitDir)), _TranslucencyPower);
    return lightColor * albedo * _TranslucencyColor.rgb * (vDotL * _TranslucencyStrength);
}

// Wrap / Warp diffuse：将 NdotL 从 [-wrap, 1] 映射到 [0, 1]
// wrap = 0 → 标准 Lambert；wrap → 1 → 半兰伯特风格，暗部更亮更软
half DiffuseWrapNdotL(half3 normalWS, half3 lightDirWS, half wrap)
{
    half NdotL = dot(normalWS, lightDirWS);
    half w = saturate(wrap);
    return saturate((NdotL + w) / (1.0h + w));
}

// =============================================================================
// Forward Lit
// =============================================================================
#if defined(LEAF_FORWARD_PASS)

LeafVaryings LeafVert(LeafAttributes input)
{
    LeafVaryings o = (LeafVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    float3 posOS = ApplyLeafWind(input.positionOS.xyz, input.normalOS, input.uv);
    VertexPositionInputs posInputs = GetVertexPositionInputs(posOS);
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

half4 LeafFrag(LeafVaryings i, half face : VFACE) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

    // --- Albedo + Alpha ---
    half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
    half3 albedo = baseSample.rgb * _BaseColor.rgb;
    half  alpha  = saturate(baseSample.a * _BaseColor.a * _AlphaScale);

    // --- Mask: AO / Roughness / Metallic ---
    half occlusion = 1.0h;
    half smoothness = _Smoothness;
    half metallic = _Metallic;
#if defined(_MASKMAP)
    half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, i.uv);
    occlusion  = LerpWhiteTo(mask.r, _OcclusionStrength);
    // Mask.G 视为 Roughness，转 Smoothness 再乘全局强度
    smoothness = (1.0h - mask.g) * _Smoothness;
    metallic   = mask.b * _Metallic;
#endif

    // --- Dual-channel normal → world ---
    half3 normalTS = SampleLeafNormalTS(i.uv);
    half3 nWS = normalize(i.normalWS);
    half3 tWS = normalize(i.tangentWS.xyz);
    ApplyFaceFlip(nWS, tWS, face);
    half3 bWS = cross(nWS, tWS) * i.tangentWS.w;
    half3 normalWS = normalize(TransformTangentToWorld(normalTS, half3x3(tWS, bWS, nWS)));

    float3 positionWS = i.positionWS;
    half3  viewDirWS  = GetWorldSpaceNormalizeViewDir(positionWS);

    // --- Shadows / Main light ---
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
    // 高光 / 透光仍用真实灯光色；diffuse 改用自定义色
    half3 lightCol  = mainLight.color * lightAtten;

    // --- Wrap Lambert diffuse：不乘灯光颜色，改乘 _DiffuseColor ---
    half NdotL = DiffuseWrapNdotL(normalWS, lightDir, _DiffuseWrap);
    half3 diffuse = albedo * (1.0h - metallic) * _DiffuseColor.rgb * NdotL * lightAtten;

    half3 halfDir = normalize(lightDir + viewDirWS);
    half  NdotH   = saturate(dot(normalWS, halfDir));
    half  specPow = exp2(10.0h * smoothness + 1.0h);
    half3 specular = lightCol * pow(NdotH, specPow) * smoothness * (0.04h + metallic * 0.96h);

    // --- Ambient ---
    half3 bakedGI = SAMPLE_GI(i.staticLightmapUV, i.vertexSH, normalWS);
    half3 ambient = bakedGI * albedo * occlusion;

    // --- Emission：BaseColor(albedo) * HDR 颜色 ---
    half3 emission = albedo * _EmissionColor.rgb;

    // --- Translucency（叶片透光）---
    half3 translucency = Translucency(lightDir, viewDirWS, normalWS, lightCol, albedo) * occlusion;

    half3 color = ambient + diffuse + specular + translucency + emission;

    // --- Additional lights ---
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
            color += Translucency(light.direction, viewDirWS, normalWS, addCol, albedo) * occlusion;
        LIGHT_LOOP_END
    }
    #endif

    color = MixFog(color, i.fogFactor);
    return half4(color, alpha);
}

#endif // LEAF_FORWARD_PASS

// =============================================================================
// Shadow Caster
// =============================================================================
#if defined(LEAF_SHADOW_PASS)

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

float3 _LightDirection;
float3 _LightPosition;

struct LeafShadowAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct LeafShadowVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

float4 GetLeafShadowPositionHClip(float3 positionOS, float3 normalOS)
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

LeafShadowVaryings LeafShadowVert(LeafShadowAttributes input)
{
    LeafShadowVaryings o;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, o);

    float3 posOS = ApplyLeafWind(input.positionOS.xyz, input.normalOS, input.uv);
    o.positionCS = GetLeafShadowPositionHClip(posOS, input.normalOS);
    o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    return o;
}

half4 LeafShadowFrag(LeafShadowVaryings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    LeafClipAlpha(i.uv);
    return 0;
}

#endif // LEAF_SHADOW_PASS

// =============================================================================
// Depth Only
// =============================================================================
#if defined(LEAF_DEPTH_PASS)

struct LeafDepthAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct LeafDepthVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

LeafDepthVaryings LeafDepthVert(LeafDepthAttributes input)
{
    LeafDepthVaryings o;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, o);

    float3 posOS = ApplyLeafWind(input.positionOS.xyz, input.normalOS, input.uv);
    o.positionCS = TransformObjectToHClip(posOS);
    o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    return o;
}

half4 LeafDepthFrag(LeafDepthVaryings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    LeafClipAlpha(i.uv);
    return 0;
}

#endif // LEAF_DEPTH_PASS

// =============================================================================
// Depth Normals
// =============================================================================
#if defined(LEAF_DEPTHNORMALS_PASS)

struct LeafDNAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct LeafDNVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    float4 tangentWS  : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

LeafDNVaryings LeafDepthNormalsVert(LeafDNAttributes input)
{
    LeafDNVaryings o;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, o);

    float3 posOS = ApplyLeafWind(input.positionOS.xyz, input.normalOS, input.uv);
    VertexPositionInputs posInputs = GetVertexPositionInputs(posOS);
    VertexNormalInputs   nrmInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

    o.positionCS = posInputs.positionCS;
    o.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
    o.normalWS   = nrmInputs.normalWS;
    real sign    = input.tangentOS.w * GetOddNegativeScale();
    o.tangentWS  = float4(nrmInputs.tangentWS.xyz, sign);
    return o;
}

half4 LeafDepthNormalsFrag(LeafDNVaryings i, half face : VFACE) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    LeafClipAlpha(i.uv);

    half3 normalTS = SampleLeafNormalTS(i.uv);
    half3 nWS = normalize(i.normalWS);
    half3 tWS = normalize(i.tangentWS.xyz);
    ApplyFaceFlip(nWS, tWS, face);
    half3 bWS = cross(nWS, tWS) * i.tangentWS.w;
    half3 normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, half3x3(tWS, bWS, nWS)));

    return half4(normalWS, 0.0h);
}

#endif // LEAF_DEPTHNORMALS_PASS

#endif // LEAF_HLSL
